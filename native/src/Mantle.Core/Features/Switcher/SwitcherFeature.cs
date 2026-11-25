using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Features.Switcher;

/// <summary>
/// [Feature("switcher")] (lib/switcher.js start/stop/rest/onEvent/COMMANDS): the Alt+Tab switcher's service. The keys,
/// the selection, the order and the switch itself stay the linked sidecar's (sidecar/AltTab.cs); this asks the sidecar
/// for exactly what <see cref="SwitcherHook.Want"/> allows, turns its <c>switcher</c> events into drawing through
/// <see cref="SwitcherSessions"/> and an <see cref="ISwitcherOverlay"/>, keeps the two switches (altTab, winKeys) in its
/// settings file, and publishes the state words as <c>switcher.state</c>.
/// SAFETY: with no overlay (no surface yet), a page that is not ready, the feature off or unhealthy, or Mantle resting,
/// Alt+Tab is asked back for Windows (altTab false); nothing here sits in front of any key, the kill switch included.
/// </summary>
[Feature("switcher", "Alt+Tab window switcher", true, Rest = true)]
public sealed class SwitcherFeature : IFeature
{
    public const string StateEvent = "switcher.state";
    public const string NoOverlay = "the switcher window is not drawn in native Mantle yet";
    public const string NoReady = "the switcher page has not loaded yet";
    public const int TickMs = 40, GridTimeoutMs = 1000, PickTimeoutMs = 1500;
    public const string EmptyPatch = "say altTab or winKeys, true or false";
    public const string DidNotMove = "the switch did not move";

    /// <summary>What the feature is given (tests: a fake overlay, a manual clock, a key reader).</summary>
    /// <param name="Overlay">The surface; null = none (Alt+Tab stays Windows' own).</param>
    /// <param name="WindowCount">The bar's window list length, for the pre-size; null = unknown (no pre-size).</param>
    /// <param name="AltDown">GetAsyncKeyState(VK_MENU) high bit, for the stuck-overlay guard; null = cannot read.</param>
    /// <param name="Now">Milliseconds on the sidecar's keyAt clock (QueryPerformanceCounter); default Stopwatch.</param>
    /// <param name="AllowForeground">AllowSetForegroundWindow(pid) before a click's switch; null = not available.</param>
    /// <param name="HandBack">The UNSCOPED platform's <c>hotkey</c> call for the stop/rest hand-back: the feature's own platform
    /// refuses every call once its stop has begun (FeatureHost StopOne), so without this a rest leaves Alt+Tab with the
    /// sidecar's machine (it still switches on Alt-up, with nothing drawn). The composition root passes it.</param>
    public sealed record Host(ISwitcherOverlay? Overlay = null, Func<int?>? WindowCount = null, Func<bool?>? AltDown = null,
        Func<double>? Now = null, HostClock? Clock = null, Func<int, bool>? AllowForeground = null,
        Func<JsonObject, ValueTask<PlatformReply>>? HandBack = null, Action<string>? OnWinKey = null);
    // OnWinKey: the bar opening its notification centre ("n") or quick settings ("a") for the sidecar's winkey event
    // (Electron's ctx.onWinKey). Null = nothing would open them, so Win+N / Win+A are never asked for (they stay Windows').

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;

    /// <summary>The composition root's <see cref="Host.HandBack"/> (lane r11-app-wiring): the stop/rest <c>hotkey</c> ask on
    /// the UNSCOPED platform, with the hook's own 4 s timeout. Never throws: a throw comes back as a failed reply, which
    /// StopAsync logs in one line.</summary>
    public static Func<JsonObject, ValueTask<PlatformReply>> HandBackVia(IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return async req =>
        {
            try { return await platform.CallAsync("hotkey", req, TimeSpan.FromMilliseconds(SwitcherHook.AskTimeoutMs)).ConfigureAwait(true); }
            catch (Exception e) { return PlatformReply.Fail(PlatformOutcome.Failed, $"hotkey threw: {e.Message}"); }
        };
    }

    readonly Host? _given;
    public SwitcherFeature() { }
    public SwitcherFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    SwitcherHookClient _client = null!;
    SwitcherSessions _sess = null!;
    SwitcherSettings _settings = SwitcherSettings.Default;
    SwitcherPage _page = new(false, NoReady);
    string? _settingsNote, _resting, _quitting;
    bool _stopped;
    IDisposable? _tick;
    double _nextPresize;
    SwitcherPiece? _last;

    double Now() => _h.Now?.Invoke() ?? Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// The two switches as Electron's loadSettings reads them: a boolean is taken, anything else leaves the default (on).
    public static (SwitcherSettings Settings, string? Note) ReadSettings(Data.IStore store)
    {
        bool altTab = true, winKeys = true;
        var bad = new List<string>();
        if (store.TryGet("altTab", out var a)) { if (a.ValueKind is JsonValueKind.True or JsonValueKind.False) altTab = a.GetBoolean(); else bad.Add("altTab"); }
        if (store.TryGet("winKeys", out var w)) { if (w.ValueKind is JsonValueKind.True or JsonValueKind.False) winKeys = w.GetBoolean(); else bad.Add("winKeys"); }
        string? note = bad.Count == 0 ? null : $"the saved {string.Join(" and ", bad)} {(bad.Count == 1 ? "was" : "were")} not true or false, so {(bad.Count == 1 ? "it is" : "both are")} at the default (on)";
        return (new SwitcherSettings(altTab, winKeys), note);
    }

    /// <summary>settings.switcher's patch (SWI-127): only booleans for altTab / winKeys; none is refused in words.</summary>
    public static (bool? AltTab, bool? WinKeys, string? Error) Patch(JsonElement a)
    {
        bool? Get(string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
        var at = Get("altTab");
        var wk = Get("winKeys");
        return at is null && wk is null ? (null, null, EmptyPatch) : (at, wk, null);
    }

    public async ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        (_settings, _settingsNote) = ReadSettings(ctx.Settings);
        if (_settingsNote is not null) ctx.Log(_settingsNote);
        _client = new SwitcherHookClient(ctx.Platform, ctx.Log);
        _sess = new SwitcherSessions(_h.Overlay ?? new Absent(), ctx.Flags.Test, Grid, ctx.Log);
        if (_h.Overlay is null) _page = new SwitcherPage(false, NoOverlay);

        ctx.Replay(StateEvent, () => _last ?? Piece());
        ctx.Platform.On("switcher", e => OnEvent(e.Body));
        ctx.Platform.OnStatus(st =>
        {
            if (st.State == PlatformState.Ok) { _ = Apply("the sidecar is running again"); return; }
            // The hook died with the sidecar's process, so Alt+Tab is already Windows' own again.
            _sess.EndAll("the sidecar stopped", Now());
            Publish();
        });
        ctx.OnEnabled(on =>
        {
            if (!on) _sess.EndAll("the switcher was turned off", Now());
            _ = Apply(on ? "the switcher was turned on" : "the switcher was turned off");
        });

        ctx.Command("ready", _ => Guard(() =>
        {
            if (!_page.Ready && _h.Overlay is not null)
            {
                _page = new SwitcherPage(true, null);
                _sess.PageDrew = false;               // a new page has drawn nothing yet: the next open is cold
                ctx.Log($"the switcher page is ready{(ctx.Flags.Test ? "; test mode: it is never shown and Alt+Tab stays Windows' own" : "")}");
                _ = Apply("the switcher page is ready");
            }
            return Ok(new JsonObject { ["ok"] = true });
        }));
        ctx.Command("down", c => Guard(() =>
        {
            // The page crashed, failed to load or stopped responding: never switch blind (SWI-138, the hand-back half).
            string why = c.Args.ValueKind == JsonValueKind.Object && c.Args.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? "the switcher page went away" : "the switcher page went away";
            _page = new SwitcherPage(false, why, true);
            _sess.EndAll(why, Now());
            _ = Apply(why);
            return Ok(new JsonObject { ["ok"] = true });
        }));
        ctx.Command("painted", c => Guard(() =>
        {
            var a = c.Args;
            long seq = (long)Num(a, "seq");
            var boxes = new List<PreviewBox?>();
            if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("boxes", out var bs) && bs.ValueKind == JsonValueKind.Array)
                foreach (var b in bs.EnumerateArray())
                    boxes.Add(b.ValueKind == JsonValueKind.Object && b.TryGetProperty("hwnd", out var h)
                        ? new PreviewBox(h.ValueKind == JsonValueKind.String ? h.GetString() : h.GetRawText(), Num(b, "x"), Num(b, "y"), Num(b, "w"), Num(b, "h"))
                        : null);
            string? held = _sess.Painted(seq, NumOrNaN(a, "vw"), NumOrNaN(a, "vh"), boxes, NumOrNaN(a, "dpr"), Now());
            Arm();
            var o = new JsonObject { ["ok"] = true };
            if (held == "stale") o["stale"] = true;
            else if (held is not null) o["waiting"] = held.StartsWith("waiting: ", StringComparison.Ordinal) ? held["waiting: ".Length..] : held;
            else o["thumbs"] = _sess.Current?.Thumbs?.Count ?? 0;
            return Ok(o);
        }));
        ctx.Command("pick", c => Guard(async () =>
        {
            var a = c.Args;
            if (!(NumOrNaN(a, "seq") is double sq && sq == Math.Floor(sq)) || !(NumOrNaN(a, "index") is double ix && ix == Math.Floor(ix) && ix >= 0))
                return Ok(new JsonObject { ["ok"] = false, ["reason"] = "switcher.pick needs the seq and index of a tile" });
            string? hwnd = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("hwnd", out var hv) && hv.ValueKind is JsonValueKind.String or JsonValueKind.Number
                ? (hv.ValueKind == JsonValueKind.String ? hv.GetString() : hv.GetRawText()) : null;
            var (index, err) = _sess.PickIndex((long)sq, (int)ix, hwnd);
            if (err is not null) return Ok(new JsonObject { ["ok"] = false, ["reason"] = err });
            // THIS process received the click, so it may hand its right to set the foreground window to the sidecar.
            bool granted = false;
            int? pid = ctx.Platform.Status.Info?.Pid;
            if (_h.AllowForeground is { } allow && pid is int p && p > 0) { try { granted = allow(p); } catch { granted = false; } }
            var r = await ctx.Platform.CallAsync("hotkey", new JsonObject { ["op"] = "pick", ["seq"] = (long)sq, ["index"] = index },
                TimeSpan.FromMilliseconds(PickTimeoutMs)).ConfigureAwait(true);
            return Ok(r.Ok ? new JsonObject { ["ok"] = true, ["granted"] = granted }
                : new JsonObject { ["ok"] = false, ["reason"] = $"the click could not reach the sidecar: {r.Error}" });
        }));
        ctx.Command("status", _ => Guard(() => Ok(Status())));
        ctx.Command("settings", _ => Guard(() => Ok(View())));
        ctx.Command("set", c => Guard(async () =>
        {
            var (at, wk, err) = Patch(c.Args);
            if (err is not null) { var v = View(); v["ok"] = false; v["reason"] = err; return Ok(v); }
            var changed = new List<string>();
            var s = _settings;
            if (at is bool x && x != s.AltTab) { s = s with { AltTab = x }; changed.Add($"Mantle's Alt+Tab {(x ? "on" : "off")}"); }
            if (wk is bool y && y != s.WinKeys) { s = s with { WinKeys = y }; changed.Add($"Win+N / Win+A {(y ? "on" : "off")}"); }
            if (changed.Count > 0)
            {
                _settings = s;
                var w1 = await ctx.Settings.SetAsync("altTab", JsonValue.Create(s.AltTab)).ConfigureAwait(true);
                var w2 = await ctx.Settings.SetAsync("winKeys", JsonValue.Create(s.WinKeys)).ConfigureAwait(true);
                _settingsNote = w1.Ok && w2.Ok ? null
                    : $"the switch was changed but its settings file could not be written ({(w1.Ok ? w2.Reason : w1.Reason)}), so it will be back to the old setting after a restart";
                if (_settingsNote is not null) ctx.Log(_settingsNote);
                string who = c.Caller;
                ctx.Log($"{string.Join(", ", changed)} ({who})");
                await Apply($"{who} turned {string.Join(", ", changed)}").ConfigureAwait(true);
            }
            bool moved = (at is null || _settings.AltTab == at) && (wk is null || _settings.WinKeys == wk);
            var view = View();
            view["ok"] = moved;
            view["reason"] = moved ? null : DidNotMove;
            return Ok(view);
        }));

        _nextPresize = Now() + SwitcherSessions.PresizeMs;
        Arm();
        await Apply("the switcher started").ConfigureAwait(true);
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        if (_stopped || _sess is null) { _stopped = true; return; }
        _stopped = true;
        _tick?.Dispose();
        _tick = null;
        string why = stop.Rest ? $"resting while {SwitcherHook.RestWords(stop.Reason)}" : (string.IsNullOrEmpty(stop.Reason) ? "the switcher stopped" : stop.Reason);
        if (stop.Rest) _resting = SwitcherHook.RestWords(stop.Reason); else _quitting = stop.Reason;
        // SWI-140: nothing stays drawn, nothing keeps ticking.
        _sess.EndAll(why, Now());
        _h.Overlay?.ClearThumbs(why);
        _sess.Forget();
        // The hand-back: Alt+Tab goes to Windows while stopped or resting. Only Host.HandBack can still reach the
        // sidecar here (the feature's own platform refuses calls once the stop began); without it this is logged.
        try
        {
            // Both go back: a stopped or resting feature hears no winkey event, so a swallowed Win+N would open nothing.
            var req = SwitcherHookClient.Request(false, false);
            var r = _h.HandBack is { } hb ? await hb(req).ConfigureAwait(true)
                : await _ctx.Platform.CallAsync("hotkey", req, TimeSpan.FromMilliseconds(SwitcherHook.AskTimeoutMs)).ConfigureAwait(true);
            if (!r.Ok) _ctx.Log($"Alt+Tab could not be handed back to Windows on the way out ({r.Error}); the sidecar gives it back when it stops");
        }
        catch (Exception e) { _ctx.Log($"Alt+Tab could not be handed back to Windows on the way out ({e.Message})"); }
    }

    /* ---- the hook ----------------------------------------------------------------------------- */

    bool FeatureUp => _ctx.Enabled && !_stopped;

    async Task Apply(string why)
    {
        if (_stopped) return;
        var f = _ctx.Flags;
        // The feature off: neither key is Mantle's (no Windows changes while off). Win+N / Win+A only with something to open.
        var want = FeatureUp ? _settings with { WinKeys = _settings.WinKeys && _h.OnWinKey is not null } : new SwitcherSettings(false, false);
        bool open = _sess.Current is { Rehearsal: null, Ended: null };
        var step = await _client.ApplyAsync(why, want, _page, _resting, FeatureUp, true, f.Test, f.Live, open).ConfigureAwait(true);
        var piece = Publish();
        if (step == HookStep.Ask || step == HookStep.Blocked) _client.LogState(why, piece);
    }

    SwitcherPiece Piece() => SwitcherHook.StateWords(_client.Facts(_settings, _page, _ctx.Flags.Test, _ctx.Flags.Live, _quitting, null, _settingsNote, _resting));

    SwitcherPiece Publish()
    {
        var p = Piece();
        _last = p;
        if (!_stopped) _ctx.Broadcast(StateEvent, p);
        return p;
    }

    JsonObject View()
    {
        var st = _last ?? Piece();
        return new JsonObject
        {
            ["ok"] = true, ["reason"] = null, ["altTab"] = _settings.AltTab, ["winKeys"] = _settings.WinKeys,
            ["altTabLive"] = st.AltTab, ["winKeysLive"] = st.WinKeys, ["altTabReason"] = st.Reason ?? "", ["winKeysReason"] = st.WinKeysReason ?? "",
        };
    }

    /// switcher.status (SWI-144): settings, page, hook, the session, the thumbnails' count; no title, no handle.
    /// Built as a JsonObject (the same keys an anonymous object serialized to): Native AOT has no reflection JSON.
    JsonObject Status() => new()
    {
        ["ok"] = true,
        ["settings"] = new JsonObject { ["altTab"] = _settings.AltTab, ["winKeys"] = _settings.WinKeys },
        ["page"] = new JsonObject { ["ready"] = _page.Ready, ["reason"] = _page.Reason, ["failed"] = _page.Failed },
        ["hook"] = _client.Hook is { } hk
            ? new JsonObject { ["Installed"] = hk.Installed, ["AltTab"] = hk.AltTab, ["WinKeys"] = hk.WinKeys, ["Tracking"] = hk.Tracking, ["Reason"] = hk.Reason }
            : null,
        ["hookErr"] = _client.HookErr, ["pendingApply"] = _client.PendingApply,
        ["session"] = _sess.Current is { } s
            ? new JsonObject { ["seq"] = s.Seq, ["windows"] = s.List.Count, ["shown"] = s.Shown is not null, ["painted"] = s.Painted is not null, ["undrawn"] = s.Undrawn }
            : null,
        ["thumbs"] = _sess.Current?.Thumbs?.Count ?? 0,
        ["overlay"] = _h.Overlay is null ? NoOverlay : null,
        ["resting"] = _resting,
    };

    /* ---- events ------------------------------------------------------------------------------- */

    void OnEvent(JsonElement m)
    {
        if (_stopped || m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("op", out var opv) || opv.ValueKind != JsonValueKind.String) return;
        string op = opv.GetString()!;
        try
        {
            double now = Now();
            switch (op)
            {
                case "open":
                    if (m.TryGetProperty("rehearsal", out var rh) && rh.ValueKind == JsonValueKind.True)
                    { if (_ctx.Flags.Test) _sess.Open(m, now, _page.Ready, _page.Reason, "live-list rehearsal"); break; }
                    _sess.Open(m, now, _page.Ready && FeatureUp, FeatureUp ? _page.Reason : "the switcher is off");
                    break;
                case "windows": _sess.Reflow(m); break;
                case "move": _sess.Move((long)Num(m, "seq"), (int)Num(m, "sel"), m.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null); break;
                case "commit":
                    _sess.Commit((long)Num(m, "seq"), m.TryGetProperty("via", out var via) && via.ValueKind == JsonValueKind.String ? via.GetString() : null, now);
                    AfterEnd();
                    break;
                case "cancel":
                    _sess.Cancel((long)Num(m, "seq"), m.TryGetProperty("why", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null, now);
                    AfterEnd();
                    break;
                case "activated":
                    _sess.Activated(new ActivatedEvent((long)Num(m, "seq"), m.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                        Str(m, "via"), Str(m, "how"), m.TryGetProperty("ms", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : null,
                        Str(m, "cause"), Str(m, "reason")));
                    break;
                case "winkey":
                    {
                        string key = Str(m, "key") ?? "";
                        _ctx.Log($"Win+{key.ToUpperInvariant()}: asking the bar to open its {(key == "n" ? "notification centre" : "quick settings")}");
                        _h.OnWinKey?.Invoke(key);
                        break;
                    }
                case "refused": _ctx.Log($"#{(long)Num(m, "seq")}: a click was not used: {Str(m, "reason") ?? "undefined"}"); break;
                case "hook":
                    // The sidecar's own word on its hook (a re-install, a drop): the state follows it.
                    Publish();
                    break;
            }
        }
        catch (Exception e) { _ctx.Log($"the \"{op}\" event threw: {e.Message}"); }
        Arm();
    }

    void AfterEnd()
    {
        if (_client.PendingApply is { } why) _ = Apply(why);
    }

    void Grid(long seq, int cols)
    {
        _ = GridAsync(seq, cols);
    }

    async Task GridAsync(long seq, int cols)
    {
        var r = await _ctx.Platform.CallAsync("hotkey", new JsonObject { ["op"] = "grid", ["seq"] = seq, ["cols"] = cols },
            TimeSpan.FromMilliseconds(GridTimeoutMs)).ConfigureAwait(true);
        if (!r.Ok) _ctx.Log($"#{seq}: the arrow keys stay left/right only (the grid could not be sent: {r.Error})");
    }

    /* ---- the clock ------------------------------------------------------------------------------ */

    /// One timer: every 40 ms while an Alt+Tab is up (show delay, late paint, the stuck-overlay guard), else at the
    /// next 500 ms pre-size.
    void Arm()
    {
        if (_stopped) return;
        _tick?.Dispose();
        double now = Now();
        double due = _sess.Current is not null ? TickMs : Math.Max(TickMs, _nextPresize - now);
        _tick = _clock.Later(TimeSpan.FromMilliseconds(due), Tick);
    }

    void Tick()
    {
        _tick = null;
        if (_stopped) return;
        double now = Now();
        try
        {
            var before = _sess.Current;
            _sess.Tick(now, _h.AltDown ?? (() => null));
            if (before is not null && _sess.Current is null) AfterEnd();
            if (now >= _nextPresize)
            {
                _nextPresize = now + SwitcherSessions.PresizeMs;
                // SWI-052: a test run never shows the window, and with Alt+Tab off there is no next one.
                if (SwitcherSessions.PresizeWanted(_stopped, _ctx.Flags.Test, _settings.AltTab && FeatureUp, _resting is not null)
                    && _h.WindowCount?.Invoke() is int n)
                    _sess.Presize(n, _page.Ready);
            }
        }
        catch (Exception e) { _ctx.Log($"the switcher's clock tick threw: {e.Message}"); }
        Arm();
    }

    /* ---- helpers ------------------------------------------------------------------------------ */

    static double Num(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;

    static double NumOrNaN(JsonElement o, string k) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : double.NaN;

    static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // JsonDocument, not JsonSerializer: Core is IsAotCompatible and the app ships Native AOT (no reflection JSON)
    static CommandResult Ok(JsonObject o) => CommandResult.Success(JsonDocument.Parse(o.ToJsonString()).RootElement.Clone());

    ValueTask<CommandResult> Guard(Func<CommandResult> body)
    {
        try { return ValueTask.FromResult(body()); }
        catch (Exception e) { return ValueTask.FromResult(CommandResult.Fail($"the switcher could not do that ({e.Message})")); }
    }

    async ValueTask<CommandResult> Guard(Func<Task<CommandResult>> body)
    {
        try { return await body().ConfigureAwait(true); }
        catch (Exception e) { return CommandResult.Fail($"the switcher could not do that ({e.Message})"); }
    }

    /// No surface: nothing is drawn, so the page is never ready and Alt+Tab stays Windows' own.
    sealed class Absent : ISwitcherOverlay
    {
        public bool Alive => false;
        public string? Place(int x, int y, int w, int h, bool show) => "there is no switcher window";
        public void Send(string name, object payload) { }
        public ThumbResult SyncThumbs(IReadOnlyList<PreviewBox> boxes, double dpr, int originX, int originY) => new(false, "there is no switcher window", 0);
        public void ClearThumbs(string why) { }
        public double ScaleFor(PhysRect mon) => 1;
        public IReadOnlyList<PhysRect> Displays => Array.Empty<PhysRect>();
        public PhysRect PrimaryMon => new(0, 0, 1920, 1080);
        public PhysRect PrimaryWork => new(0, 0, 1920, 1040);
    }
}
