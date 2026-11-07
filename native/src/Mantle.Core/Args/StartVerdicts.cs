using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mantle.Core;

/// <summary>
/// One --open request (lib/args.js openRequest / playFiles / openNotRunningLine): what a second start
/// asks the running Mantle to open. Only allow-listed windows are opened from outside.
/// </summary>
public sealed record OpenRequest(bool Ok, string Value, string? Target, string? Label, IReadOnlyList<string> Files, string Line)
{
    public static readonly string[] Targets = { "wallpapers", "player" };
    static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal) { ["wallpapers"] = "Wallpapers", ["player"] = "Mantle Player" };
    public const int PlayMax = 64;
    static readonly Regex Absolute = new(@"^([a-zA-Z]:[\\/]|\\\\)", RegexOptions.CultureInvariant);

    /// The last --open wins, as in lib/args.js; null when there is none.
    public static OpenRequest? From(IEnumerable<string> argv) => From(argv, null);

    /// <summary>The same, plus <paramref name="testTargets"/> (the build's test stand-in surfaces, "test-..."),
    /// which are allowed only when the arguments carry --test: a live or scratch start keeps Electron's allow-list.</summary>
    public static OpenRequest? From(IEnumerable<string> argv, IReadOnlyCollection<string>? testTargets)
    {
        var list = argv.ToList();
        string? value = null;
        foreach (var a in list) {
            if (a == "--open") value = "";
            else if (a.StartsWith("--open=", StringComparison.Ordinal)) value = a["--open=".Length..];
        }
        if (value == null) return null;
        string target = value.Trim().ToLowerInvariant();
        if (testTargets != null && list.Any(a => a == "--test" || (a != null && a.StartsWith("--test=", StringComparison.Ordinal))) && target.StartsWith("test-", StringComparison.Ordinal) && testTargets.Contains(target))
            return new OpenRequest(true, value, target, target, Array.Empty<string>(), $"mantle: opened {target} for a second start (--open={target}, a test stand-in)");
        if (Labels.TryGetValue(target, out var label)) {
            var files = target == "player" ? PlayFiles(list) : Array.Empty<string>();
            return new OpenRequest(true, value, target, label, files,
                $"mantle: opened {label} for a second start (--open={target}{(files.Count > 0 ? $", {files.Count} file(s)" : "")})");
        }
        string shown = Quote(value.Length > 60 ? value[..60] + "..." : value);
        return new OpenRequest(false, value, null, null, Array.Empty<string>(),
            $"mantle: a second start asked to open {shown}, which is not something Mantle opens from outside (allowed: {string.Join(", ", Targets)})");
    }

    /// --play=&lt;absolute path&gt; values: at most 64, each up to 1024 chars, no control characters.
    public static IReadOnlyList<string> PlayFiles(IEnumerable<string> argv)
    {
        var o = new List<string>();
        foreach (var a in argv) {
            if (a == null || !a.StartsWith("--play=", StringComparison.Ordinal)) continue;
            string p = a["--play=".Length..];
            if (p.StartsWith('"')) p = p[1..];
            if (p.EndsWith('"')) p = p[..^1];
            if (p.Length > 0 && p.Length <= 1024 && Absolute.IsMatch(p) && p.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0 && o.Count < PlayMax) o.Add(p);
        }
        return o;
    }

    /// JSON.stringify of a string, for the sentences (quotes and backslashes escaped, nothing else).
    static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// The line a start prints when nothing is running to open it (exit code 3).
    public static string NotRunningLine(OpenRequest? req)
    {
        if (req is { Ok: true }) return $"Mantle is not running, so its {req.Label} could not be opened";
        string why = req != null
            ? $" (and {Quote(req.Value.Length > 60 ? req.Value[..60] : req.Value)} is not something Mantle opens from outside; allowed: {string.Join(", ", Targets)})"
            : "";
        return "Mantle is not running, so nothing could be opened" + why;
    }
}


/// <summary>lib/start-wake.js: the 3 s "Ctrl+C now" pause and the first-ping ladder.</summary>
public static class StartWake
{
    public const int CountdownMs = 3000;
    public static readonly int[] PingRetryMs = { 2000, 5000, 15000 };

    /// countdownVerdict: only a live start with a console pauses (CORE-015).
    public static (int PauseMs, string? Line) Countdown(bool live, bool console)
    {
        if (!live) return (0, null);
        if (console) return (CountdownMs, "starting in 3 seconds. Ctrl+C now if that was not what you meant.");
        return (0, "starting now: there is no console to press Ctrl+C in (the logon task or another program started Mantle), so there is no 3 s pause; the lines above are in the log");
    }

    public enum PingNext { Go, Old, Retry, GiveUp }

    /// <summary>pingVerdict(age, n): what to do after the n-th (0-based) protocol-age ping (CORE-018).</summary>
    /// <param name="current">the exe speaks the current protocol</param>
    /// <param name="answered">it answered, just with an older protocol (null = unknown: judged from the reason)</param>
    public static (PingNext Next, int WaitMs, string Reason) Ping(bool current, bool? answered, string? reason, int n)
    {
        if (current) return (PingNext.Go, 0, reason ?? "the sidecar speaks the current protocol");
        bool old = answered == true || (answered != false && (reason ?? "").Contains("older than the protocol", StringComparison.Ordinal));
        if (old) return (PingNext.Old, 0, reason ?? "the sidecar exe is older than the protocol");
        int tries = PingRetryMs.Length + 1;
        string why = reason ?? "the sidecar did not answer ping";
        if (n < PingRetryMs.Length) {
            int ms = PingRetryMs[n];
            return (PingNext.Retry, ms, $"the sidecar did not answer its first ping in time, so Windows is untouched for now; asking again in {ms / 1000} s (ask {n + 2} of {tries}): {why}");
        }
        return (PingNext.GiveUp, 0, $"{why} (asked {tries} times over {PingRetryMs.Sum() / 1000} s)");
    }
}
