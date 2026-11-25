using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Welcome;

/// <summary>The first-run flag, <c>&lt;profile&gt;\first-run.json</c> (lib/welcome.js): <c>{state:'pending', at}</c> or
/// <c>{state:'done', how, at[, evidence][, note]}</c> with how = finished | skipped | existing-profile.</summary>
public sealed record WelcomeFlag(string State, string? How = null, string? At = null, IReadOnlyList<string>? Evidence = null, string? Note = null)
{
    /// The object in the key order lib/welcome.js builds it (JSON.stringify keeps insertion order).
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["state"] = State };
        if (How != null) o["how"] = How;
        o["at"] = At;
        if (Evidence != null)
        {
            var a = new JsonArray();
            foreach (var e in Evidence) a.Add((JsonNode?)JsonValue.Create(e));   // the non-generic Add: Add<T> is not AOT-safe (IL2026/IL3050)
            o["evidence"] = a;
        }
        if (Note != null) o["note"] = Note;
        return o;
    }
}

/// <summary>The startup verdict (lib/welcome.js judge): show it or not, why, and the flag to record later (or none).</summary>
public sealed record WelcomeVerdict(bool Show, string Reason, WelcomeFlag? Write, WelcomeFlag? Flag, IReadOnlyList<string> Evidence, bool Test, bool OptIn);

/// <summary>A flag write's answer: Ok, or the reason in plain words (never a throw).</summary>
public readonly record struct WelcomeWrite(bool Ok, string? Reason, bool Wrote = false);

/// <summary>welcome.done's answer: <c>{ok, recorded, flag | reason}</c>.</summary>
public readonly record struct WelcomeDone(bool Ok, bool Recorded, string? Reason, WelcomeFlag? Flag = null);

/// <summary>
/// THE FIRST-RUN DECISION (WEL-001..013), a port of lib/welcome.js's judge, readFlag, evidenceIn and writeFlag.
/// <see cref="Judge"/> only READS (it runs before anything of this run writes); every write goes through
/// <see cref="SafeFile"/>, so the write gate decides whether the user's own profile may be touched at all.
/// </summary>
public static class FirstRun
{
    public const string FlagFile = "first-run.json";
    public const string Title = "Welcome to Mantle";
    /// The window: the 560 x 500 card plus room for its shadow.
    public const int WinW = 624, WinH = 564;
    /// After the bar's page is up: the shell settles first, then the welcome comes in over it.
    public const int ShowDelayMs = 1200;

    /// Mantle's own files and folders: each exists only once Mantle has run in this profile. mantle.log and
    /// Chromium's own files are not evidence (main's log writer makes the log before the verdict).
    public static readonly IReadOnlyList<string> EvidenceNames = new[]
        { "shell-state.json", "theme.json", "features.json", "features", "wallpapers", "topbar-appbar.txt", "look-sync.json", "crash.json", "Local Storage" };

    public static readonly IReadOnlyList<string> Hows = new[] { "finished", "skipped", "existing-profile" };

    /// new Date().toISOString().
    public static string IsoNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The flag as it is on disk (READ only): (flag, null) when usable, (null, null) when missing,
    /// (null, why) when it cannot be read or is not a state Mantle writes.</summary>
    public static (WelcomeFlag? Flag, string? Why) ReadFlag(string dir)
    {
        string text;
        try { text = JsJson.DecodeUtf8(File.ReadAllBytes(Path.Combine(dir, FlagFile))); }
        catch (Exception e)
        {
            string code = SafeFile.Code(e);
            return code == "ENOENT" ? (null, null) : (null, "the flag could not be read (" + code + ")");
        }
        JsonNode? v;
        try { v = JsJson.Parse(text); }
        catch (Exception) { return (null, "the flag is not valid JSON"); }
        if (v is not JsonObject and not JsonArray) return (null, "the flag is not an object");
        var o = v as JsonObject;
        string state = JsString(o, "state");
        string? how = o != null && o.TryGetPropertyValue("how", out var h) && h is JsonValue hv && hv.TryGetValue<string>(out var hs) ? hs : null;
        if (state == "pending" && IsString(o, "state")) return (new WelcomeFlag("pending", At: Truthy(o, "at") ? JsString(o, "at") : null), null);
        if (state == "done" && IsString(o, "state") && how != null && Hows.Contains(how))
        {
            string? at = o!.TryGetPropertyValue("at", out var a) && a != null ? JsString(o, "at") : null;
            List<string>? ev = o.TryGetPropertyValue("evidence", out var e) && e is JsonArray ea
                ? ea.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var xs) ? xs : null).Where(x => x != null).Select(x => x!).ToList() : null;
            string? note = o.TryGetPropertyValue("note", out var n) && n is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns : null;
            return (new WelcomeFlag("done", how, at, ev, note), null);
        }
        string says = "the flag says \"" + Cut(state, 20) + "\"" + (Truthy(o, "how") ? " / \"" + Cut(JsString(o, "how"), 20) + "\"" : "");
        return (null, says + ", which is not a state Mantle writes");
    }

    /// <summary>The <see cref="EvidenceNames"/> present in <paramref name="dir"/> (READ only; a file or a folder).</summary>
    public static List<string> EvidenceIn(string dir)
    {
        var o = new List<string>();
        foreach (var name in EvidenceNames)
        {
            try { string p = Path.Combine(dir, name); if (File.Exists(p) || Directory.Exists(p)) o.Add(name); }
            catch (Exception) { /* not there */ }
        }
        return o;
    }

    /// <summary>The startup verdict. READS only; never throws. <paramref name="optIn"/> = <c>--first-run</c>.</summary>
    public static WelcomeVerdict Judge(string? dir, bool test, bool optIn, Func<string>? now = null)
    {
        var none = Array.Empty<string>();
        try
        {
            string at = now != null ? now() : IsoNow();
            if (test && !optIn)
                return new(false, "a test run without --first-run: the welcome is not shown and its flag is not written", null, null, none, test, optIn);
            if (string.IsNullOrEmpty(dir))
                return new(false, "no profile folder was given, so nothing can be judged; the welcome is not shown", null, null, none, test, optIn);
            var (flag, why) = ReadFlag(dir);
            if (flag is { State: "done" })
                return new(false, "this profile is done with the welcome (" + flag.How + (string.IsNullOrEmpty(flag.At) ? "" : ", " + flag.At) + ")", null, flag, none, test, optIn);
            if (flag is { State: "pending" })
                return new(true, "the welcome was shown before and not finished or skipped yet", null, flag, none, test, optIn);
            var evidence = EvidenceIn(dir);
            string note = why != null ? "; " + why : "";
            if (evidence.Count > 0)
                return new(false, "this profile has been used (" + string.Join(", ", evidence) + "), so it is marked and the welcome never shows" + note,
                    new WelcomeFlag("done", "existing-profile", at, evidence), null, evidence, test, optIn);
            return new(true, "a brand-new profile: nothing of Mantle's is in it yet" + note, new WelcomeFlag("pending", At: at), null, none, test, optIn);
        }
        catch (Exception e)
        {
            return new(false, "the welcome could not be judged (" + e.GetType().Name + "); it is not shown", null, null, none, test, optIn);
        }
    }

    /// <summary>Write a flag atomically (tmp <c>first-run.json.&lt;pid&gt;.tmp</c> + rename, Electron's bytes: 2-space JSON, no
    /// newline). A flag Mantle cannot read is first set aside as <c>first-run.json.unreadable</c>, never overwritten.</summary>
    public static WelcomeWrite WriteFlag(SafeFile files, string dir, WelcomeFlag flag)
    {
        string file = Path.Combine(dir, FlagFile);
        if (File.Exists(file) && ReadFlag(dir).Why != null)
        {
            string? aside = files.MoveAside(file, file + ".unreadable");
            if (aside != null) return new(false, "the first-run flag could not be saved (the unreadable flag could not be set aside: " + aside + ")");
        }
        byte[] bytes = JsJson.ToFileBytes(flag.ToJson(), 2, false);
        string? err = files.WriteAtomic(file, bytes, file + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp");
        return err == null ? new(true, null, true) : new(false, "the first-run flag could not be saved (" + err + ")");
    }

    static bool IsString(JsonObject? o, string key) => o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out _);

    static bool Truthy(JsonObject? o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out var v) || v == null) return false;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s.Length > 0;
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<double>(out var d)) return d != 0 && !double.IsNaN(d);
        }
        return true;   // objects and arrays
    }

    /// JavaScript's String(v.key) for the shapes a flag can hold.
    static string JsString(JsonObject? o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out var v)) return "undefined";
        return JsString(v);
    }

    static string JsString(JsonNode? v)
    {
        switch (v)
        {
            case null: return "null";
            case JsonObject: return "[object Object]";
            case JsonArray a: return string.Join(",", a.Select(x => x == null ? "" : JsString(x)));
            case JsonValue jv:
                if (jv.TryGetValue<string>(out var s)) return s;
                if (jv.TryGetValue<bool>(out var b)) return b ? "true" : "false";
                if (jv.TryGetValue<double>(out var d)) return JsJson.Number(d);
                return jv.ToJsonString();
        }
        return "";
    }

    static string Cut(string s, int n) => s.Length > n ? s.Substring(0, n) : s;
}

/// <summary>
/// lib/welcome.js host's record() and done(): the verdict's flag once this process holds the single-instance lock, then
/// the user's answer. A plain <c>--test</c> run records nothing; every write goes through the gate.
/// </summary>
public sealed class WelcomeRecorder
{
    readonly SafeFile _files;
    readonly string? _dir;
    readonly Action<string> _log;
    readonly Func<string> _now;

    public WelcomeVerdict Verdict { get; }
    public bool Test { get; }
    /// The flag is written only where it may be: never in a plain --test run.
    public bool MayWrite => !Test || Verdict.OptIn;

    public WelcomeRecorder(WelcomeVerdict verdict, string? dir, bool test, SafeFile files, Action<string>? log = null, Func<string>? now = null)
    {
        Verdict = verdict ?? throw new ArgumentNullException(nameof(verdict));
        _dir = dir; Test = test;
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _log = log ?? (_ => { });
        if (now != null) _now = now;
        else _now = FirstRun.IsoNow;
    }

    /// <summary>record(): write the verdict's flag (pending, or existing-profile), if it has one.</summary>
    public WelcomeWrite Record()
    {
        if (Verdict.Write == null || string.IsNullOrEmpty(_dir)) return new(true, null, false);
        var r = FirstRun.WriteFlag(_files, _dir, Verdict.Write);
        _log(r.Ok ? "flag written: " + Verdict.Write.State + (Verdict.Write.How != null ? " (" + Verdict.Write.How + ")" : "") : r.Reason!);
        return r;
    }

    /// <summary>welcome.done {how}: finished or skipped, recorded as <c>{state:'done', how, at[, note]}</c>.</summary>
    public WelcomeDone Done(string? how, string? why = null)
    {
        if (how != "finished" && how != "skipped") return new(false, false, "welcome.done takes {how: \"finished\" | \"skipped\"}");
        if (!MayWrite) return new(true, false, "a test run without --first-run: nothing is recorded");
        if (string.IsNullOrEmpty(_dir)) return new(false, false, "no profile folder was given, so nothing can be recorded");
        var flag = new WelcomeFlag("done", how, _now(), null, why);
        var r = FirstRun.WriteFlag(_files, _dir, flag);
        if (!r.Ok) { _log(r.Reason!); return new(false, false, r.Reason); }
        return new(true, true, null, flag);
    }

    /// <summary>The window closed without an answer: by hand = Skip ("the window was closed"); because Mantle is
    /// quitting = the flag stays as it was (pending comes back next start).</summary>
    public WelcomeDone Closed(bool quitting)
    {
        if (quitting) { _log("closed because Mantle is quitting; the welcome stays as it was"); return new(true, false, "Mantle is quitting"); }
        var r = Done("skipped", "the window was closed");
        _log(r.Recorded ? "closed without an answer: recorded as skipped" : "closed without an answer (" + r.Reason + ")");
        return r;
    }
}
