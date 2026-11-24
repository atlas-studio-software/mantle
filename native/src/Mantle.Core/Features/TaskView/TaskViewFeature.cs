using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.TaskView;

/// <summary>One Task View open as the service sees it: what the page may name in it (taskview.js learn()).</summary>
public sealed class TaskViewSession
{
    public TaskViewSession(long seq, string via) { Seq = seq; Via = via; }
    public long Seq { get; }
    /// What opened it: "Win+Tab", the hot corner, "bench".
    public string Via { get; }
    /// The list payload the sidecar gave (<c>taskview.list</c>'s result), for the surface to draw.
    public JsonElement Payload { get; internal set; }
    public HashSet<string> Hwnds { get; internal set; } = new(StringComparer.Ordinal);
    public HashSet<string> Desks { get; internal set; } = new(StringComparer.Ordinal);
    public int N => Hwnds.Count;
}

/// <summary>
/// The Task View window as the service needs it: the seam a surface (Surfaces/TaskView) implements: the parked
/// window, the keyboard ladder, the previews, the fold. Called on the host thread; never throws (failures are words).
/// </summary>
public interface ITaskViewSurface
{
    /// The window exists and its page has said ready.
    bool Ready { get; }
    /// Bring the session on screen (take the keyboard, draw, reveal). Null when it opened, else the words.
    ValueTask<string?> OpenAsync(TaskViewSession s);
    /// Take it down: previews FIRST, then the fold, then the park; <paramref name="giveBack"/>: the keyboard goes
    /// back to the window that had it (never after a switch: it belongs where the user went).
    void End(TaskViewSession s, string why, bool giveBack);
    /// A message to the page (<c>taskview.update</c>, <c>taskview.notice</c>).
    void Push(string name, object payload);
    /// Destroy the window (stop, rest).
    void Drop(string why);
}

/// <summary>
/// [Feature("taskview")] (lib/features/taskview.js): Win+Tab opens and closes Task View; the page's calls are checked
/// against THIS session (<see cref="TaskViewGuards"/>) before the sidecar is asked; every write is a dry run in a test
/// run; a close is re-checked and a window still there after 1.6 s is said on screen.
/// SAFETY: with no surface the feature reports itself unhealthy, so the host never takes Win+Tab from Windows; a page
/// that is not ready makes a Win+Tab do nothing but a log line. Nothing here touches any other key.
/// </summary>
[Feature("taskview", "Task View (Win+Tab)", true, Chords = new[] { "Win+Tab" }, Rest = true)]
public sealed class TaskViewFeature : IFeature
{
    public const string NoSurface = "the Task View window is not drawn in native Mantle yet, so Win+Tab stays Windows' own";
    public const string NotReady = "Win+Tab came before the Task View page was ready; nothing was opened";
    public const int ListTimeoutMs = 1500, ActTimeoutMs = 2500;
    static readonly int[] CloseRecheckMs = { 350, 1600 };

    /// <summary>What the feature is given (tests: a fake surface, a manual clock).</summary>
    public sealed record Host(ITaskViewSurface? Surface = null, HostClock? Clock = null);

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;
    readonly Host? _given;
    public TaskViewFeature() { }
    public TaskViewFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    bool _test, _stopped;
    long _seq;
    TaskViewSession? _sess;
    readonly List<IDisposable> _timers = new();

    /// The open session, if any.
    public TaskViewSession? Current => _sess;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        _test = ctx.Flags.Test;
        if (_h.Surface is null) ctx.Health(false, NoSurface);

        ctx.OnChord("Win+Tab", via => _ = OpenSafe(via));
        ctx.OnEnabled(on => { if (!on) End("Task View was turned off"); });

        ctx.Command("dismiss", c => Guard(() =>
        {
            var a = c.Args;
            if (_sess is { } s && TaskViewGuards.IsCurrent(SeqOf(a), s.Seq))
            {
                string? why = Str(a, "why");
                End(why == "esc" ? "Esc" : why == "click-away" ? "clicked away" : string.IsNullOrEmpty(why) ? "dismissed" : why);
            }
            return Task.FromResult(Ok(new JsonObject { ["ok"] = true }));
        }));

        ctx.Command("activate", c => Guard(async () =>
        {
            if (Stale(c.Args) is not { } s) return Ok(new JsonObject { ["ok"] = true, ["stale"] = true });
            string? hwnd = Str(c.Args, "hwnd");
            if (TaskViewGuards.CheckHwnd(hwnd, s.Hwnds) is { } bad) return Fail(bad);
            var r = await ctx.Platform.CallAsync("taskview.activate", new JsonObject { ["hwnd"] = hwnd, ["dry"] = _test },
                TimeSpan.FromMilliseconds(ActTimeoutMs)).ConfigureAwait(true);
            if (!r.Ok) return Fail(TaskViewGuards.Plain(r.Error));
            if (_test) ctx.Log($"#{s.Seq}: dry run, would switch to a window");
            End(_test ? "switched to a window (dry run)" : $"switched to a window ({HowOf(r.Result)})", giveBack: false);
            return CommandResult.Success(r.Result);
        }));

        ctx.Command("close", c => Guard(async () =>
        {
            if (Stale(c.Args) is not { } s) return Ok(new JsonObject { ["ok"] = true, ["stale"] = true });
            string? hwnd = Str(c.Args, "hwnd");
            if (TaskViewGuards.CheckHwnd(hwnd, s.Hwnds) is { } bad) return Fail(bad);
            if (_test) return Fail("this is a test run, and a test run never closes your windows");
            var r = await ctx.Platform.CallAsync("close", new JsonObject { ["hwnd"] = hwnd }, TimeSpan.FromMilliseconds(ActTimeoutMs)).ConfigureAwait(true);
            if (!r.Ok) return Fail(TaskViewGuards.Plain(r.Error));
            Recheck(s, hwnd!, 0);
            return Ok(new JsonObject { ["ok"] = true });
        }));

        ctx.Command("desk", c => Guard(async () =>
        {
            var a = c.Args;
            if (Stale(a) is not { } s) return Ok(new JsonObject { ["ok"] = true, ["stale"] = true });
            object? name = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var nv)
                ? (nv.ValueKind == JsonValueKind.String ? nv.GetString() : (object)nv.ValueKind) : null;   // not text: refused in words
            string? op = Str(a, "op");
            var (req, err) = TaskViewGuards.Desk(op, Str(a, "desktop"), Str(a, "hwnd"), name, s.Hwnds, s.Desks, _test);
            if (err is not null) return Fail(err);
            var args = new JsonObject { ["dry"] = req!.Dry };
            if (req.Desktop is not null) args["desktop"] = req.Desktop;
            if (req.Hwnd is not null) args["hwnd"] = req.Hwnd;
            if (req.Name is not null) args["name"] = req.Name;
            var r = await ctx.Platform.CallAsync(req.Verb, args, TimeSpan.FromMilliseconds(TaskViewGuards.DeskTimeoutMs)).ConfigureAwait(true);
            if (!r.Ok) return Fail(TaskViewGuards.Plain(r.Error));
            if (_test) ctx.Log($"dry run, {op}");
            bool already = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("already", out var al) && al.ValueKind == JsonValueKind.True;
            if (op == "switch" && !_test && !already) { End("switched desktop", giveBack: false); return Ok(new JsonObject { ["ended"] = true }); }
            var fresh = await Relist(s).ConfigureAwait(true);
            if (fresh is null) return Fail("the window list could not be read again");
            var o = JsonNode.Parse(fresh.Value.GetRawText()) as JsonObject ?? new JsonObject();
            if (op == "create" && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
            {
                string created = id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText();
                o["created"] = created;
                if (_sess == s) s.Desks.Add(created);
            }
            return Ok(o);
        }));
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
        string why = string.IsNullOrEmpty(stop.Reason) ? "Mantle is putting Windows back" : stop.Reason;
        End(why);
        // TV-029 / TV-032: the window goes (and its previews with it) on a stop and on a rest.
        _h?.Surface?.Drop(why);
        return ValueTask.CompletedTask;
    }

    /* ---- open / end ---------------------------------------------------------------------------- */

    async Task OpenSafe(string via)
    {
        try { await Open(via).ConfigureAwait(true); }
        catch (Exception e) { _ctx.Log($"open failed: {e.Message}"); End("open failed"); }
    }

    /// <summary>open(): Win+Tab again closes (TV-002); off, stopped, no surface or a page not ready: nothing but a log
    /// line (TV-004); else the list is read and the surface brings it up.</summary>
    public async Task Open(string via)
    {
        if (!_ctx.Enabled || _stopped) return;
        if (_sess is not null) { End("Win+Tab again"); return; }
        var surface = _h.Surface;
        if (surface is null) { _ctx.Log($"{via}: {NoSurface}"); return; }
        if (!surface.Ready) { _ctx.Log($"{via}: {NotReady}"); return; }
        var s = new TaskViewSession(++_seq, via);
        _sess = s;
        var pl = await Relist(s).ConfigureAwait(true);
        if (_sess != s) return;                                    // a second Win+Tab or a stop came first
        if (pl is null) { End("the window list could not be read"); return; }
        var why = await surface.OpenAsync(s).ConfigureAwait(true);
        if (why is not null && _sess == s) { _ctx.Log($"#{s.Seq}: Task View could not open: {why}"); End("open failed"); }
    }

    /// <summary>end(): once; the surface takes it down; the keyboard goes back unless a switch sent it elsewhere (TV-015).</summary>
    public void End(string why, bool giveBack = true)
    {
        var s = _sess;
        if (s is null) return;
        _sess = null;
        _h.Surface?.End(s, why, giveBack && !_test);
        _ctx.Log($"#{s.Seq} ({s.Via}): {s.N} windows; ended: {why}");
    }

    /// The fresh list for a session (taskview.list, 1.5 s): learns what the page may name. Null when it failed (logged).
    async Task<JsonElement?> Relist(TaskViewSession s)
    {
        var r = await _ctx.Platform.CallAsync("taskview.list", new JsonObject(), TimeSpan.FromMilliseconds(ListTimeoutMs)).ConfigureAwait(true);
        if (!r.Ok) { _ctx.Log($"relist: {TaskViewGuards.Plain(r.Error)}"); return null; }
        if (_sess == s) Learn(s, r.Result);
        return r.Result;
    }

    /// <summary>learn(): the windows (list[].hwnd) and desktops (desktops.list[].id) this session may name.</summary>
    public static void Learn(TaskViewSession s, JsonElement pl)
    {
        var hw = new HashSet<string>(StringComparer.Ordinal);
        var dk = new HashSet<string>(StringComparer.Ordinal);
        if (pl.ValueKind == JsonValueKind.Object)
        {
            if (pl.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
                foreach (var it in l.EnumerateArray())
                    if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("hwnd", out var h))
                        hw.Add(h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : h.GetRawText());
            if (pl.TryGetProperty("desktops", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("list", out var dl) && dl.ValueKind == JsonValueKind.Array)
                foreach (var it in dl.EnumerateArray())
                    if (it.ValueKind == JsonValueKind.Object && it.TryGetProperty("id", out var id))
                        dk.Add(id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : id.GetRawText());
        }
        s.Payload = pl;
        s.Hwnds = hw;
        s.Desks = dk;
    }

    /// After a close the list is read again at 350 ms and 1.6 s; still there then = the app is asking something.
    void Recheck(TaskViewSession s, string hwnd, int i)
    {
        int wait = CloseRecheckMs[i] - (i > 0 ? CloseRecheckMs[i - 1] : 0);
        _timers.Add(_clock.Later(TimeSpan.FromMilliseconds(wait), () => _ = RecheckNow(s, hwnd, i)));
    }

    async Task RecheckNow(TaskViewSession s, string hwnd, int i)
    {
        if (_sess != s || _stopped) return;
        var fresh = await Relist(s).ConfigureAwait(true);
        if (_sess != s || fresh is null) return;
        _h.Surface?.Push("taskview.update", new { seq = s.Seq, payload = fresh.Value });
        if (!s.Hwnds.Contains(hwnd)) return;
        if (i + 1 < CloseRecheckMs.Length) { Recheck(s, hwnd, i + 1); return; }
        string title = TitleOf(fresh.Value, hwnd);
        _h.Surface?.Push("taskview.notice", new
        {
            seq = s.Seq, kind = "info",
            text = $"\"{(string.IsNullOrEmpty(title) ? "That window" : title)}\" is still open. It may be asking you something, like whether to save. Switch to it to answer.",
        });
    }

    /* ---- helpers ------------------------------------------------------------------------------ */

    TaskViewSession? Stale(JsonElement a) => _sess is { } s && TaskViewGuards.IsCurrent(SeqOf(a), s.Seq) ? s : null;

    static long? SeqOf(JsonElement a) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty("seq", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    /// A string, or a number's digits (an hwnd may come as a number); anything else is null.
    static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null
            : null;

    static string HowOf(JsonElement r) =>
        r.ValueKind == JsonValueKind.Object && r.TryGetProperty("how", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? "undefined" : "undefined";

    static string TitleOf(JsonElement pl, string hwnd)
    {
        if (pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty("list", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var it in l.EnumerateArray())
                if (Str(it, "hwnd") == hwnd) return Str(it, "title") ?? "";
        return "";
    }

    // JsonDocument, not JsonSerializer: Core is IsAotCompatible and the app ships Native AOT (no reflection JSON)
    static CommandResult Ok(JsonObject o) => CommandResult.Success(JsonDocument.Parse(o.ToJsonString()).RootElement.Clone());
    static CommandResult Fail(string words) => CommandResult.Fail(words);

    static async ValueTask<CommandResult> Guard(Func<Task<CommandResult>> body)
    {
        try { return await body().ConfigureAwait(true); }
        catch (Exception e) { return CommandResult.Fail(TaskViewGuards.Plain(e.Message)); }
    }
}
