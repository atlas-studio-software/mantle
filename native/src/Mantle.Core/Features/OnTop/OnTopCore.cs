using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.OnTop;

/// One window Mantle keeps on top: its handle, the pid it had (a reused handle is never touched), the name the user sees.
public sealed record OnTopPin(string Hwnd, int Pid, string Name, string Exe = "", string Via = "");

/// What the sidecar knows of a window (ontop.info / ontop.front): <see cref="Refuse"/> = why it can never be pinned.
public sealed record OnTopFacts(string Hwnd, int Pid, int AppPid, string Name, string Exe, bool Topmost, string? Refuse)
{
    public static OnTopFacts Parse(JsonElement r)
    {
        static string S(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : "";
        static int I(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
        bool top = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("topmost", out var t) && t.ValueKind == JsonValueKind.True;
        string refuse = S(r, "refuse");
        return new OnTopFacts(S(r, "hwnd") is { Length: > 0 } h ? h : "0", I(r, "pid"), I(r, "appPid"), S(r, "name"), S(r, "exe"), top, refuse.Length > 0 ? refuse : null);
    }
}

/// Every outcome, for the pop-up and the bar menu (_ontop-core.js out): <see cref="Raw"/> is the sidecar's words, for the log only.
public sealed record OnTopResult(bool Ok, string Did, string Text, string? Reason, string? Name, string Tone)
{
    internal string? Raw { get; init; }
    public JsonObject ToJson() => new() { ["ok"] = Ok, ["did"] = Did, ["text"] = Text, ["reason"] = Reason, ["name"] = Name, ["tone"] = Tone };
}

/// <summary>
/// The rules of "Keep on top" (lib/features/_ontop-core.js), with the sidecar injected so every rule is unit-tested
/// against a made-up Windows: Mantle only takes away what Mantle gave (a pin is recorded only when Mantle turned the
/// window from not-on-top to on-top); every pin carries its pid; a pin that closed or was taken off top by something
/// else is forgotten (Sweep); ReleaseAll puts every pin back; Suspend / Resume around a game; Adopt after a crash.
/// No app names in the log: "an app window (pid N, window H)" (BAR-336..BAR-340, BAR-342, BAR-344..BAR-346).
/// </summary>
public sealed class OnTopCore
{
    public const string Passive = "refused: sidecar is passive (test mode)";
    const string HelperDown = "Mantle’s window helper didn’t answer (it may be restarting): try again in a moment";

    readonly Func<string, JsonObject, ValueTask<PlatformReply>> _send;
    readonly Action<string> _log;
    readonly Action<IReadOnlyList<OnTopPin>> _save;
    readonly bool _test;
    readonly Dictionary<string, OnTopPin> _pins = new(StringComparer.Ordinal);
    bool _sweeping;

    public event Action<IReadOnlyList<OnTopPin>>? Changed;

    public OnTopCore(Func<string, JsonObject, ValueTask<PlatformReply>> send, Action<string> log, Action<IReadOnlyList<OnTopPin>> save, bool test)
    { _send = send; _log = log; _save = save; _test = test; }

    public IReadOnlyList<OnTopPin> List => _pins.Values.ToList();
    public int Count => _pins.Count;

    /// The sidecar's reasons made fit for a pop-up or a menu (plain): never "sidecar", API names or error codes.
    public static string Plain(string? why)
    {
        string s = (why ?? "").Trim();
        if (s.Length == 0) return "no reason came back";
        if (s.StartsWith(Passive, StringComparison.Ordinal) || s.StartsWith("refused: sidecar is passive", StringComparison.Ordinal)) return "test run: Mantle only changes its own test windows";
        if (Regex.IsMatch(s, "unknown command|older than .*sidecar|build:sidecar", RegexOptions.IgnoreCase)) return "part of Mantle is out of date: restart Mantle after updating it";
        if (Regex.IsMatch(s, "sidecar|timed out|did not answer in time", RegexOptions.IgnoreCase)) return HelperDown;
        s = Regex.Replace(s, @",? and Windows does not let Mantle \(which does not\) (change|move) its windows \(UIPI\)", ", so Windows doesn’t let Mantle $1 it");
        s = s.Replace(" runs above Mantle (as administrator, or protected), and Windows refused the change (access denied, UIPI)", " runs as administrator (or is protected), so Windows refused the change", StringComparison.Ordinal)
             .Replace(" (access denied (it runs above Mantle, UIPI))", ": it runs as administrator (or is protected)", StringComparison.Ordinal)
             .Replace(" (access denied, UIPI)", ": it runs as administrator (or is protected)", StringComparison.Ordinal);
        s = Regex.Replace(s, @"\s*\((?:[A-Za-z]+ )?(?:Win32 )?error -?\d+\)", "");
        s = Regex.Replace(s, @"\s*\(UIPI\)", "");
        s = Regex.Replace(s, @"\s*\b0x[0-9a-f]{6,8}\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\b(ENOENT|EACCES|EPERM|EBUSY)\b:?\s*", "");
        s = s.Trim();
        return s.Length > 0 ? s : "no reason came back";
    }

    static string Who(string hwnd, int pid) => $"an app window (pid {pid}, window {hwnd})";

    /// The names cut out of any text before it is logged (quiet): the log never names an app.
    public static string Quiet(string? text, IEnumerable<string?> names)
    {
        string s = text ?? "";
        foreach (var n in names) if (n is { Length: > 1 } && n != "this window") s = s.Replace(n, "the window", StringComparison.Ordinal);
        s = Regex.Replace(s, @"^.+?(?= is not showing \(| is not responding, | runs as administrator| runs above Mantle)", "the window");
        s = Regex.Replace(s, @"(Windows refused to change ).+?(?= \(it may be closing)", "$1the window");
        s = Regex.Replace(s, @"(Windows accepted the change, but ).+?(?= is still (?:not )?on top after)", "$1the window");
        return s;
    }

    void Changed_()
    {
        var l = List;
        try { _save(l); } catch (Exception e) { _log($"could not save the pin list ({e.Message}); if Mantle crashes now, those windows stay on top"); }
        try { Changed?.Invoke(l); } catch (Exception e) { _log($"the pin-state push failed: {e.Message}"); }
    }

    static OnTopResult Out(string did, string text, string? reason, string? name)
    {
        bool ok = did is "pinned" or "unpinned" or "dry" or "none";
        return new OnTopResult(ok, did, text, reason != null ? Plain(reason) : null, name, ok ? (did == "dry" ? "quiet" : "") : "warn") { Raw = reason };
    }

    static string Said(PlatformReply r) => r.Error is { Length: > 0 } e ? e : "no reason came back";
    bool IsPassive(PlatformReply r) => _test && Said(r).StartsWith(Passive, StringComparison.Ordinal);

    void Note(OnTopResult res, string via, string? name2 = null, string? exe = null)
    {
        if (res.Did is "pinned" or "unpinned") return;   // Pin / Unpin log their own line with the window
        var names = new[] { res.Name, name2, exe };
        string? why = res.Raw ?? res.Reason;
        _log($"{res.Did}{(via.Length > 0 ? $" ({via})" : "")}: {Quiet(res.Text, names)}{(why != null ? ": " + Quiet(why, names) : "")}");
    }

    static bool Bool(JsonElement r, string k) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    async ValueTask<OnTopResult> Pin(OnTopFacts t, string via)
    {
        string name = t.Name.Length > 0 ? t.Name : "this window";
        if (t.Refuse != null) { var r = Out("refused", $"Can’t keep {name} on top", t.Refuse, name); Note(r, via, t.Name, t.Exe); return r; }
        if (t.Topmost) {
            var r = Out("refused", $"{name} is already on top", "it keeps itself on top (its own setting), so Mantle leaves it exactly as it is", name);
            Note(r, via, t.Name, t.Exe); return r;
        }
        var s = await _send("ontop.set", new JsonObject { ["hwnd"] = t.Hwnd, ["on"] = true, ["pid"] = t.Pid }).ConfigureAwait(true);
        if (!s.Ok) {
            var r = IsPassive(s) ? Out("dry", $"Dry run: would keep {name} on top", "test run: Mantle only changes its own test windows", name)
                : Out("failed", $"Couldn’t keep {name} on top", Said(s), name);
            Note(r, via, t.Name, t.Exe); return r;
        }
        if (!Bool(s.Result, "changed")) {
            var r = Out("refused", $"{name} is already on top", "it put itself on top just now, so Mantle leaves it as it is", name);
            Note(r, via, t.Name, t.Exe); return r;
        }
        var p = new OnTopPin(t.Hwnd, t.Pid, name, t.Exe, via);
        _pins[p.Hwnd] = p;
        int tries = s.Result.TryGetProperty("tries", out var tr) && tr.ValueKind == JsonValueKind.Number ? tr.GetInt32() : 1;
        _log($"kept on top: {Who(p.Hwnd, p.Pid)} via {via}{(tries > 1 ? $", took {tries} tries" : "")}");
        Changed_();
        return Out("pinned", $"Kept on top: {name}", null, name);
    }

    async ValueTask<OnTopResult> Unpin(OnTopPin p, string via)
    {
        var s = await _send("ontop.set", new JsonObject { ["hwnd"] = p.Hwnd, ["on"] = false, ["pid"] = p.Pid }).ConfigureAwait(true);
        if (!s.Ok && Regex.IsMatch(Said(s), "has closed|closed while")) {
            _pins.Remove(p.Hwnd);
            _log($"forgot {Who(p.Hwnd, p.Pid)} via {via}: it had closed");
            Changed_();
            return Out("unpinned", $"No longer on top: {p.Name}", "its window had closed", p.Name);
        }
        if (!s.Ok) {
            var r = IsPassive(s) ? Out("dry", $"Dry run: would stop keeping {p.Name} on top", "test run: Mantle only changes its own test windows", p.Name)
                : Out("failed", $"Couldn’t stop keeping {p.Name} on top", Said(s), p.Name);
            Note(r, via, p.Name, p.Exe);
            return r;   // still recorded: it IS still on top, and quit will try again
        }
        _pins.Remove(p.Hwnd);
        _log($"no longer on top: {Who(p.Hwnd, p.Pid)} via {via}");
        Changed_();
        return Out("unpinned", $"No longer on top: {p.Name}", null, p.Name);
    }

    /// The chord: flip the window in front.
    public async ValueTask<OnTopResult> ToggleAsync(OnTopFacts? t, string via)
    {
        if (t == null || t.Hwnd is "" or "0") {
            var r = Out("refused", "Nothing to keep on top", t?.Refuse ?? "no window is in front");
            Note(r, via); return r;
        }
        if (_pins.TryGetValue(t.Hwnd, out var p)) {
            if (p.Pid == t.Pid) return await Unpin(p, via).ConfigureAwait(true);
            _pins.Remove(p.Hwnd);
            _log($"forgot {Who(p.Hwnd, p.Pid)}: its handle now belongs to another program");
            Changed_();
        }
        return await Pin(t, via).ConfigureAwait(true);
    }

    /// The menu: an explicit on / off for one window.
    public async ValueTask<OnTopResult> SetAsync(OnTopFacts? t, bool on, string via)
    {
        if (t == null || t.Hwnd is "" or "0") { var r = Out("refused", "That window has closed", t?.Refuse); Note(r, via); return r; }
        bool mine = _pins.TryGetValue(t.Hwnd, out var p) && p.Pid == t.Pid;
        if (on) return mine ? Out("none", $"{p!.Name} is already kept on top", null, p.Name) : await ToggleAsync(t, via).ConfigureAwait(true);
        if (mine) return await Unpin(p!, via).ConfigureAwait(true);
        string name = t.Name.Length > 0 ? t.Name : "this window";
        var res = t.Topmost ? Out("refused", $"{name} keeps itself on top", "Mantle did not put it there, so Mantle does not take it away", name)
            : Out("none", $"{name} is not kept on top", null, name);
        Note(res, via, t.Name, t.Exe);
        return res;
    }

    static IEnumerable<(string Hwnd, bool Alive, bool Topmost)> Windows(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("windows", out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var w in arr.EnumerateArray())
            yield return (w.TryGetProperty("hwnd", out var h) ? h.ToString() : "", Bool(w, "alive"), Bool(w, "topmost"));
    }

    JsonArray CheckArgs(IEnumerable<OnTopPin> l) => new(l.Select(p => (JsonNode?)new JsonObject { ["hwnd"] = p.Hwnd, ["pid"] = p.Pid }).ToArray());

    /// Forget pins whose window closed or went off top by someone else's hand; one check at a time.
    public async ValueTask<(int Checked, int Dropped, string? Error)> SweepAsync()
    {
        if (_pins.Count == 0 || _sweeping) return (0, 0, null);
        _sweeping = true;
        PlatformReply s;
        try { s = await _send("ontop.check", new JsonObject { ["hwnds"] = CheckArgs(List) }).ConfigureAwait(true); } finally { _sweeping = false; }
        if (!s.Ok) return (0, 0, Said(s));
        int n = 0, dropped = 0;
        foreach (var (h, alive, top) in Windows(s.Result)) {
            n++;
            if (!_pins.TryGetValue(h, out var p) || (alive && top)) continue;
            _pins.Remove(h);
            dropped++;
            _log($"forgot {Who(p.Hwnd, p.Pid)}: {(alive ? "something else took it off top (the app itself, most likely); Mantle leaves it" : "its window closed")}");
        }
        if (dropped > 0) Changed_();
        return (n, dropped, null);
    }

    /// Every Mantle pin back to not-on-top (quit, stop, switched off). Returns what was put back.
    public async ValueTask<IReadOnlyList<OnTopPin>> ReleaseAllAsync(string why)
    {
        var all = List;
        if (all.Count == 0) return all;
        var put = new List<OnTopPin>();
        var failed = new List<string>();
        int closed = 0;
        foreach (var p in all) {
            var s = await _send("ontop.set", new JsonObject { ["hwnd"] = p.Hwnd, ["on"] = false, ["pid"] = p.Pid }).ConfigureAwait(true);
            if (s.Ok) put.Add(p);
            else if (Regex.IsMatch(Said(s), "has closed|closed while")) closed++;
            else if (IsPassive(s)) put.Add(p);   // --test: never really pinned, nothing to put back
            else failed.Add($"{Who(p.Hwnd, p.Pid)}: {Quiet(Said(s), new[] { p.Name, p.Exe })}");
            _pins.Remove(p.Hwnd);
        }
        _log($"put back {put.Count} window(s) kept on top ({why}){(closed > 0 ? $"; {closed} had closed" : "")}{(failed.Count > 0 ? "; COULD NOT put back " + string.Join("; ", failed) : "")}");
        Changed_();
        return put;
    }

    /// After a game: each saved pin goes back on top if it is still the same, open, pinnable window.
    public async ValueTask<(List<string> Back, List<string> Skipped)> ResumeAsync(IReadOnlyList<OnTopPin> saved, string why)
    {
        var back = new List<string>(); var skipped = new List<string>(); var unnamed = new List<string>();
        void Skip(OnTopPin p, string w, params string?[] more) { skipped.Add($"{p.Name}: {w}"); unnamed.Add($"{Who(p.Hwnd, p.Pid)}: {Quiet(w, new[] { p.Name, p.Exe }.Concat(more))}"); }
        foreach (var p in saved) {
            var i = await _send("ontop.info", new JsonObject { ["hwnd"] = p.Hwnd }).ConfigureAwait(true);
            if (!i.Ok) { Skip(p, "it closed"); continue; }
            var t = OnTopFacts.Parse(i.Result);
            if (t.Pid != p.Pid) { Skip(p, "it closed"); continue; }
            if (t.Topmost) { Skip(p, "already on top by itself"); continue; }
            var r = await Pin(t with { Name = p.Name }, $"back after {why}").ConfigureAwait(true);
            if (r.Did == "pinned") back.Add(p.Name); else Skip(p, r.Reason ?? r.Text, t.Name, t.Exe);
        }
        if (saved.Count > 0) _log($"after {why}: {back.Count} of {saved.Count} window(s) on top again{(unnamed.Count > 0 ? "; not: " + string.Join("; ", unnamed) : "")}");
        return (back, skipped);
    }

    /// Start-up: pins a crashed Mantle left behind that are still open (same pid) and on top are Mantle's again.
    public async ValueTask<int> AdoptAsync(IReadOnlyList<OnTopPin> left)
    {
        var items = left.Where(p => p.Hwnd.Length > 0 && p.Pid > 0).ToList();
        if (items.Count == 0) return 0;
        var s = await _send("ontop.check", new JsonObject { ["hwnds"] = CheckArgs(items) }).ConfigureAwait(true);
        if (!s.Ok) { _log($"a previous Mantle left {items.Count} window(s) kept on top; could not check them ({Said(s)})"); return 0; }
        int kept = 0;
        foreach (var (h, alive, top) in Windows(s.Result)) {
            if (!alive || !top || _pins.ContainsKey(h)) continue;
            var p = items.FirstOrDefault(x => x.Hwnd == h);
            if (p == null) continue;
            string name = p.Name.Length > 0 ? (p.Name.Length > 48 ? p.Name[..48] : p.Name) : "a window";
            _pins[h] = new OnTopPin(h, p.Pid, name, "", "kept after a restart");
            kept++;
        }
        _log($"a previous Mantle left {items.Count} window(s) kept on top: {kept} still on top, kept");
        Changed_();
        return kept;
    }
}
