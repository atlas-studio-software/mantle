using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// Is the old standalone Spotify auto-pause helper in charge? (lib/features/_autopause-coexist.js, ported.) Two controllers
/// must never fight over Spotify, so while the helper runs Mantle's auto-pause stands by. READ ONLY: nothing here starts,
/// stops, enables or edits the helper or its "SpotifyAutoPause" scheduled task (AUTO-092); this type has no member that
/// could, and its only file access is a read.
///
/// The helper is IN CHARGE when its task exists and is enabled (even while its bridge is down: the task brings it back within
/// a minute), or when an OLD bridge answers /ping on 127.0.0.1:8787-8790 ({"app":"spotify-autopause"} with NO "server" field).
/// A host built into Mantle or Quartz says "server" and never counts: those cooperate through the link instead.
/// A check that throws counts as in charge (stay off to be safe), with words.
/// </summary>
public static class AutoPauseCoexist
{
    public const string TaskName = "SpotifyAutoPause";
    public const int PingTimeoutMs = 700, SchtasksTimeoutMs = 4000;
    /// The ONLY schtasks.exe call (a read-only query, hidden window, 4 s): never /change, /run, /end, /create or /delete.
    public static readonly IReadOnlyList<string> SchtasksArgs = new[] { "/query", "/tn", TaskName, "/fo", "csv", "/nh" };
    /// The coexistence ping's own header (the Electron check sends it too).
    public const string PingHeader = "X-Mantle: coexist";
    public const string InChargeWords = "The separate Spotify auto-pause helper on this PC is in charge, so Mantle\u2019s stays off. Turn the helper off to use this one";
    public const string ThrewWords = "Mantle could not tell whether the separate auto-pause helper runs, so it stays off to be safe";

    /// <summary>The verdict: <c>Task</c> enabled | disabled | absent | unknown; <c>Bridge</c> the old bridge's port, if one answered.</summary>
    public sealed record Verdict(bool InCharge, string TaskState, int? BridgePort, string? BridgeVersion, string Words);

    /// %SystemRoot%\System32\Tasks\SpotifyAutoPause.
    public static string TaskFile(string? systemRoot) => Path.Combine(string.IsNullOrEmpty(systemRoot) ? "C:\\Windows" : systemRoot!, "System32", "Tasks", TaskName);

    /// <summary>'enabled' | 'disabled' | 'unknown' from the task XML text. PURE. <c>&lt;Settings&gt;&lt;Enabled&gt;false</c> is what a
    /// disable writes; anything else that is a &lt;Task&gt; is enabled.</summary>
    public static string TaskStateFromXml(string? text)
    {
        var s = text ?? "";
        if (!Regex.IsMatch(s, "<Task[\\s>]")) return "unknown";
        var m = Regex.Match(s, "<Settings>[\\s\\S]*?</Settings>");
        var en = m.Success ? Regex.Match(m.Value, "<Enabled>\\s*(true|false)\\s*</Enabled>", RegexOptions.IgnoreCase) : null;
        return en is { Success: true } && en.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase) ? "disabled" : "enabled";
    }

    /// The task file's bytes as text: UTF-16 LE with a BOM or a zero second byte, else UTF-8.
    public static string Decode(byte[] buf)
    {
        if (buf.Length >= 2 && buf[0] == 0xff && buf[1] == 0xfe) return Encoding.Unicode.GetString(buf, 2, buf.Length - 2);
        if (buf.Length >= 2 && buf[1] == 0x00) return Encoding.Unicode.GetString(buf);
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>schtasks.exe /query /tn SpotifyAutoPause /fo csv /nh, read: an error that says the task is not there is absent, any
    /// other error unknown; a "Disabled" row is disabled, anything else enabled.</summary>
    public static string TaskStateFromSchtasks(bool failed, string? output, string? error)
    {
        if (failed) return Regex.IsMatch((error ?? "") + (output ?? ""), "cannot find|does not exist", RegexOptions.IgnoreCase) ? "absent" : "unknown";
        return Regex.IsMatch(output ?? "", "\"Disabled\"", RegexOptions.IgnoreCase) ? "disabled" : "enabled";
    }

    /// <summary>One /ping answer: the OLD bridge only (app spotify-autopause, no server field). Mantle's or Quartz's own host says
    /// server and is never the helper (AUTO-085).</summary>
    public static bool IsOldBridge(JsonObject? j, out string version)
    {
        version = "";
        if (j == null) return false;
        if (j.TryGetPropertyValue("server", out var srv) && Truthy(srv)) return false;
        var app = j.TryGetPropertyValue("app", out var a) && a is JsonValue av && av.TryGetValue(out string? s) ? s ?? "" : "";
        if (!Regex.IsMatch(app, "spotify-autopause", RegexOptions.IgnoreCase)) return false;
        version = j.TryGetPropertyValue("version", out var v) && v is JsonValue vv && vv.TryGetValue(out string? vs) ? vs ?? "" : "";
        return true;
    }

    /// JS truthiness of a JSON value (a "server" of "", 0, false or null does not count).
    static bool Truthy(JsonNode? n)
    {
        if (n == null) return false;
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out bool b)) return b;
            if (v.TryGetValue(out string? s)) return !string.IsNullOrEmpty(s);
            if (v.TryGetValue(out double d)) return d != 0 && !double.IsNaN(d);
        }
        return true;
    }

    /// <summary>The reads the check needs, injected (tests: no file, no process, no socket).</summary>
    /// <param name="ReadTaskFile">The task file's bytes; throw FileNotFoundException / DirectoryNotFoundException for missing.</param>
    /// <param name="Schtasks">(failed, stdout, error text) of the read-only schtasks query; never PowerShell.</param>
    /// <param name="Ping">One /ping of 127.0.0.1:port with a 700 ms timeout: the JSON answer, or null.</param>
    public sealed record Reads(Func<byte[]> ReadTaskFile, Func<Task<(bool Failed, string? Output, string? Error)>> Schtasks,
        Func<int, Task<JsonObject?>> Ping, IReadOnlyList<int>? Ports = null);

    /// <summary>The task's state: the file, else schtasks.</summary>
    public static async Task<string> ReadTask(Reads r)
    {
        try { return TaskStateFromXml(Decode(r.ReadTaskFile())); }
        catch (FileNotFoundException) { return "absent"; }
        catch (DirectoryNotFoundException) { return "absent"; }
        catch (Exception)
        {   // not readable: ask schtasks (a plain exe, read-only query)
            var (failed, output, error) = await r.Schtasks().ConfigureAwait(true);
            return TaskStateFromSchtasks(failed, output, error);
        }
    }

    /// <summary>The whole check. Pings the four ports in parallel only when the task is not enabled.</summary>
    public static async Task<Verdict> Check(Reads r)
    {
        var task = await ReadTask(r).ConfigureAwait(true);
        int? port = null; string? version = null;
        if (task != "enabled")
        {
            var ports = r.Ports ?? AutoPauseLink.Ports;
            var hits = await System.Threading.Tasks.Task.WhenAll(ports.Select(p => r.Ping(p))).ConfigureAwait(true);
            for (int i = 0; i < hits.Length; i++)
                if (IsOldBridge(hits[i], out var ver)) { port = ports[i]; version = ver; break; }
        }
        bool inCharge = task == "enabled" || port != null;
        return new Verdict(inCharge, task, port, version, inCharge ? InChargeWords : "");
    }

    /// <summary>The check as the feature runs it: one that THROWS counts as in charge (stay off to be safe), with words.</summary>
    public static async Task<Verdict> CheckSafe(Func<Task<Verdict>> check, Action<string> log)
    {
        try { return await check().ConfigureAwait(true); }
        catch (Exception e)
        {
            log($"the check for the separate auto-pause helper threw ({e.GetType().Name}); standing by to be safe");
            return new Verdict(true, "unknown", null, null, ThrewWords);
        }
    }
}
