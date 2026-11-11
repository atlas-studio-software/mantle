using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.TopBar;

// The feature half of lib/features/topbar.js: WHEN the top bar shows, the strip it reserves at the top of the primary
// display (a registered TOP app bar, so maximised windows start below it), the drag guard, hidden by hand (Win+Alt+T,
// persisted), the step-aside rules (lock screen, full-screen window, the sidecar's game verdict), the peek at the top
// edge while hidden, display changes (one burst = two runs, Mantle's own work-area moves ignored) and the focus and
// stats it hands the strip. The WINDOW is the surface's (Mantle.App/Surfaces/TopBar): it says where it is
// (topbar.placed, with its hwnd) and what the display did (topbar.display); this side tells it to show or hide
// (topbar.visible) and to re-place (topbar.place), and hands it the focus (topbar.focus) and stats (topbar.stats).
//
// WINDOWS STATE: the app bar (topbar.reserve / topbar.release) is asked ONLY in a --live run. A --test run never
// reserves: it logs the dry run with the size it would have asked for. The drag guard is asked in both, as Electron
// does: a --test platform is passive and only measures drags. Hidden by hand is written to the feature's own store
// (features\topbar.json under the run's --user-data).
//
// Not ported in this part (docs/lanes/topbar.md "Left"): known games RUNNING (the shared game watch follower),
// full-screen VIDEO (media sessions), the drift put-back, the glass and colour under (dark), menus and dropdowns,
// app power, ping test, the tools' own features, top-row hints.

public sealed record TopBarVisible(bool Show, string Why, string? Kind, bool Peek, int Seq);
public sealed record TopBarFocus(string? Json, string? MenuJson, bool Fixture);
public sealed record TopBarStats(string? Json, string? Paused);
public sealed record TopBarPlace(int Height, string Why);
public sealed record TopBarClose(string Why);

[Feature("topbar", "Top menu bar", true, Chords = new[] { TopBarRules.HideChord }, Rest = true)]
public sealed class TopBarFeature : IFeature
{
    public static readonly IReadOnlyList<FeatureOption> Options = new[]
    {
        new FeatureOption("hidden", "Hide the top bar", FeatureOptionKind.Bool, false) { Help = "Win+Alt+T or the top edge of the screen brings it back" },
        new FeatureOption("overGames", "Keep the top bar over full-screen games", FeatureOptionKind.Bool, false) { Help = "Otherwise it steps aside while a full-screen game has the screen" },
    };

    IFeatureContext _ctx = null!;
    HostClock? _clock;
    ITopBarScreen _screen;
    bool _test, _live, _stopped, _ending, _applied;
    // the window (from the surface)
    IntPtr _hwnd; double _scale = 1; bool _placed; bool _shown;
    int _seq;
    // the rules' inputs
    bool _hidden, _overGames;
    JsonObject? _focus; JsonObject? _stats; (string App, string Stats)? _fixture;
    bool _stepAside; string? _kind, _signal; long _stepSince;
    bool? _dpiAware;
    long _ownAt;
    long? _reRegAt;
    ReserveState _reserve;
    (bool Ok, string? Reason) _guard = (false, "not started");
    (bool? Ok, string? Reason) _watch = (null, "starting");
    (bool On, string Reason) _statsOn = (false, "not started");
    bool _menuOpen; int _menuH = TopBarRules.BarDip;
    IDisposable? _graceT;
    TopBarBurst? _burst;
    readonly Dictionary<string, object> _displayCounters = new() { ["events"] = 0, ["runs"] = 0, ["skipped"] = 0 };
    // peek
    PeekState _peekSt = TopBarPeek.Initial();
    bool _peekWatching, _peekShown; bool? _peekOk; string? _peekReason; long _peekSince; IDisposable? _peekT;
    string? _peekBlocked; bool _peekLooked;
    (double X, double Y)? _fakeCursor; bool _fakeSource;

    public TopBarFeature() { _screen = new Win32TopBarScreen(); _reserve = new(false, "not reserved yet"); }
    /// For the tests: a clock on manual time and a fake screen.
    public TopBarFeature(HostClock clock, ITopBarScreen screen) { _clock = clock; _screen = screen; _reserve = new(false, "not reserved yet"); }

    // read by the tests
    public bool Hidden => _hidden;
    public bool StepAside => _stepAside;
    public string? Kind => _kind;
    public ReserveState Reserve => _reserve;
    public bool PeekShown => _peekShown;
    public bool PeekWatching => _peekWatching;
    public bool Shown => _shown;

    long Now => _clock!.NowMs;
    IDisposable Later(int ms, Action fn) => _clock!.Later(TimeSpan.FromMilliseconds(ms), () => { if (!_stopped) fn(); });   // every callback checks the stop
    static JsonElement J(JsonObject o) => FeatureOptions.Parse(o.ToJsonString());
    static bool Bool(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string? Str(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static string? S(JsonObject? o, string k) => o != null && o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool B(JsonObject? o, string k) => o != null && o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    bool SettingBool(string key) => _ctx.Settings.TryGet(key, out var v) && v.ValueKind == JsonValueKind.True;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _clock ??= new HostClock(null, SynchronizationContext.Current);
        _test = ctx.Flags.Test;
        _live = ctx.Flags.Live && !_test;
        _hidden = SettingBool("hidden");
        _overGames = SettingBool("overGames");
        _reserve = _live ? new(false, "not reserved yet", new(Now, "starting", TopBarRules.ReserveStartGraceMs))
                         : new(false, "a --test instance never reserves space on the screen");
        if (_test) _fixture = TopBarFixtures.Parse(Environment.GetEnvironmentVariable("MANTLE_TEST_TOPBAR"));
        if (_fixture is { } fx) { _focus = TopBarFixtures.Focus(fx.App); _stats = TopBarFixtures.Stats(fx.Stats); }
        _burst = new TopBarBurst(DisplayRun, (ms, fn) => Later(ms, fn));

        ctx.Replay("topbar.state", View);
        ctx.Replay("topbar.visible", () => new TopBarVisible(_shown, _shown ? "shown" : "not shown yet", _kind, _peekShown, _seq));
        ctx.Replay("topbar.focus", FocusPayload);
        ctx.Replay("topbar.stats", StatsPayload);

        ctx.Command("hello", _ => Ok(new JsonObject
        {
            ["focus"] = _focus?.DeepClone(), ["stats"] = _stats?.DeepClone(), ["stepAside"] = _stepAside, ["overGames"] = _overGames,
            ["hidden"] = _hidden, ["peek"] = _peekShown, ["test"] = _test,
        }));
        ctx.Command("hidden", async c => Answer(await SetHidden(Bool(c.Args, "on"), Str(c.Args, "via") ?? "the menu")));
        ctx.Command("state", _ => Ok(ViewObj()));
        ctx.Command("status", _ => Ok(Status()));
        ctx.Command("overGames", async c =>
        {
            _overGames = Bool(c.Args, "on");
            await _ctx.Settings.SetAsync("overGames", JsonValue.Create(_overGames));
            _ctx.Log($"keep the top bar over full-screen games: {(_overGames ? "true" : "false")}");
            await Decide("setting");
            Publish();
            return CommandResult.Success(J(new JsonObject { ["ok"] = true, ["overGames"] = _overGames, ["stepAside"] = _stepAside }));
        });
        ctx.Command("placed", c => Placed(c.Args));
        ctx.Command("display", c => Display(c.Args));
        ctx.Command("menuState", c => MenuState(c.Args));
        ctx.Command("shown", c => { _shown = Bool(c.Args, "on"); return Ok(new JsonObject { ["ok"] = true }); });
        if (_test)
        {
            // --test only: the made-up focus / stats (TopBarFixtures) and a fake cursor for the peek (the user's real
            // cursor never drives a test instance's peek once a test has set one)
            ctx.Command("testFixture", c =>
            {
                _fixture = (Str(c.Args, "app") ?? "npp", Str(c.Args, "stats") ?? "ok");
                _focus = TopBarFixtures.Focus(_fixture.Value.App); _stats = TopBarFixtures.Stats(_fixture.Value.Stats);
                _ctx.Log($"test: fixture app={_fixture.Value.App} stats={_fixture.Value.Stats}");
                Broadcast("topbar.focus", FocusPayload()); Broadcast("topbar.stats", StatsPayload());
                return Ok(new JsonObject { ["ok"] = true });
            });
            ctx.Command("testPeek", c =>
            {
                _fakeSource = !Bool(c.Args, "real");
                _fakeCursor = Num(c.Args, "x") is double x && Num(c.Args, "y") is double y ? (x, y) : null;
                _ctx.Log(_fakeSource ? $"test: the peek reads a fake cursor ({(_fakeCursor is { } p ? $"{p.X:0},{p.Y:0} DIP" : "unreadable")}), buttons up" : "test: the peek reads the real cursor again");
                return Ok(new JsonObject { ["ok"] = true });
            });
        }
        ctx.OnChord(TopBarRules.HideChord, chord => _ = Run(() => SetHidden(!_hidden, TopBarRules.HideChord), TopBarRules.HideChord + " failed"));
        ctx.OnOption("hidden", (v, key) => _ = Run(() => SetHidden(v.ValueKind == JsonValueKind.True, "Mantle Settings"), "hidden"));
        ctx.OnOption("overGames", (v, key) => _ = Run(async () =>
        {
            _overGames = v.ValueKind == JsonValueKind.True;
            _ctx.Log($"keep the top bar over full-screen games: {(_overGames ? "true" : "false")} (Mantle Settings)");
            await Decide("setting"); Publish(); return ViewObj();
        }, "overGames"));
        ctx.Platform.On("topbar", e => OnTopbar(e.Body));
        ctx.Platform.On("topbarstats", e => { if (_fixture == null && !_stopped) { _stats = JsonNode.Parse(e.Body.GetRawText()) as JsonObject; Broadcast("topbar.stats", StatsPayload()); } });
        ctx.Platform.OnReady(() => _ = Run(async () => { await Apply("sidecar ready"); return null; }, "start"));
        ctx.OnEnabled(on => _ = Run(async () => { if (on) await Apply("switched on"); else await Off("the top bar is switched off in the tray"); return null; }, "switch"));
        // a window made before this install (or kept across a rest) reports itself again on this push
        Broadcast("topbar.place", new TopBarPlace(TopBarRules.BarDip, "the top bar started"));
        return ValueTask.CompletedTask;
    }

    async Task<JsonObject?> Run(Func<Task<JsonObject?>> work, string what)
    {
        try { return await work(); }
        catch (Exception e) { _ctx.Log($"{what}: {e.Message}"); return null; }
    }
    static ValueTask<CommandResult> Ok(JsonObject o) => new(CommandResult.Success(J(o)));
    static CommandResult Answer(JsonObject o) => CommandResult.Success(J(o));
    void Broadcast<T>(string ev, T payload) { if (!_stopped) _ctx.Broadcast(ev, payload); }

    TopBarFocus FocusPayload() => new(_focus?.ToJsonString(), (_focus?["menu"] as JsonObject)?.ToJsonString(), _fixture != null);
    TopBarStats StatsPayload() => new(_stats?.ToJsonString(), _stats == null ? (_statsOn.On ? null : _statsOn.Reason) : null);

    // ------------------------------------------------------------------ the state the bar's quick settings row reads (TOP-288)
    JsonObject ViewObj() => new()
    {
        ["hidden"] = _hidden, ["overGames"] = _overGames, ["chord"] = TopBarRules.HideChord, ["enabled"] = _ctx.Enabled, ["visible"] = _shown,
        ["stepAside"] = _stepAside, ["kind"] = _kind, ["signal"] = _signal, ["video"] = null, ["full"] = _kind == "full" ? new JsonObject { ["app"] = TopBarModel.AppName(_focus) } : null,
        ["test"] = _test,
        ["peek"] = new JsonObject { ["ok"] = _peekOk, ["reason"] = _peekReason, ["watching"] = _peekWatching, ["shown"] = _peekShown },
    };
    JsonElement View() => J(ViewObj());
    void Publish() => Broadcast("topbar.state", View());

    JsonObject Status() => new()
    {
        ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["placed"] = _placed, ["scale"] = _scale, ["shown"] = _shown,
        ["reserve"] = new JsonObject { ["reserved"] = _reserve.Reserved, ["reason"] = _reserve.Reason, ["size"] = _reserve.Size, ["pending"] = _reserve.Pending?.Why },
        ["guard"] = new JsonObject { ["ok"] = _guard.Ok, ["reason"] = _guard.Reason },
        ["watch"] = new JsonObject { ["ok"] = _watch.Ok, ["reason"] = _watch.Reason },
        ["stats"] = new JsonObject { ["on"] = _statsOn.On, ["reason"] = _statsOn.Reason },
        ["display"] = new JsonObject(_displayCounters.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value.ToString())))),
        ["dpiAware"] = _dpiAware, ["live"] = _live, ["state"] = ViewObj(),
    };

    // ------------------------------------------------------------------ health (topbar.js health)
    void Asked(string why, int grace = TopBarRules.ReserveGraceMs)
    {
        if (!_live || _reserve.Reserved) return;
        _reserve = _reserve with { Pending = new(Now, why, grace) };
    }
    void Health()
    {
        if (_stopped) return;
        var gap = _watch.Ok == false ? null : TopBarRules.StripGap(_live, _reserve, false, _hidden, Now);
        var p = _reserve.Pending;
        long left = p != null ? p.Since + p.Grace - Now : 0;
        if (_live && p != null && left > 0 && !_reserve.Reserved && _graceT == null)
            _graceT = Later((int)left + 50, () => { _graceT = null; Health(); });
        string? why = _watch.Ok == false ? $"the top bar cannot follow the focused app: {_watch.Reason}"
            : gap != null ? $"maximised windows can go under the top bar: {gap}"
            : (_hidden && _peekOk == false) ? $"the top bar is hidden by hand and cannot peek at the top edge: {_peekReason}"
            : null;
        _ctx.Health(why == null, why);
    }

    // ------------------------------------------------------------------ the window: show / hide (the surface draws it)
    void Show(string why)
    {
        if (!_ctx.Enabled || _stopped || _stepAside || _hidden) return;
        Visible(true, why);
    }
    void Hide(string why) => Visible(false, why);
    void Visible(bool on, string why)
    {
        _shown = on;
        Broadcast("topbar.visible", new TopBarVisible(on, why, _kind, _peekShown, ++_seq));
    }

    // ------------------------------------------------------------------ the surface's reports
    ValueTask<CommandResult> Placed(JsonElement a)
    {
        bool first = !_placed;
        if (Str(a, "hwnd") is string h && long.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hv)) _hwnd = new IntPtr(hv);
        if (Num(a, "scale") is double sc && sc > 0) _scale = sc;
        _placed = _hwnd != IntPtr.Zero;
        if (first && _placed)
        {
            // the window exists now: a start that ran before it reserves now; the strip first, then the bar
            if (_applied) _ = Run(async () => { await ReserveStrip("the window is ready"); Show("the window is ready"); PeekSync("the window is ready"); Health(); return null; }, "app bar");
        }
        return Ok(new JsonObject { ["ok"] = true, ["show"] = _ctx.Enabled && !_stopped && !_stepAside && !_hidden && _applied, ["peek"] = _peekShown });
    }

    ValueTask<CommandResult> MenuState(JsonElement a)
    {
        _menuOpen = Bool(a, "open");
        double h = Num(a, "height") ?? TopBarRules.BarDip;
        var p = _screen.Primary(out _);
        double dispH = p is { } d ? d.H / d.Scale : 10000;
        _menuH = _menuOpen ? (int)Math.Max(TopBarRules.BarDip, Math.Min(Math.Ceiling(h), dispH)) : TopBarRules.BarDip;   // TOP-224
        Broadcast("topbar.place", new TopBarPlace(_menuH, _menuOpen ? "a menu opened" : "the menu closed"));
        return Ok(new JsonObject { ["ok"] = true, ["height"] = _menuH });
    }

    // ------------------------------------------------------------------ display changes (TOP-233..239)
    ValueTask<CommandResult> Display(JsonElement a)
    {
        if (_stopped || _ending) return Ok(new JsonObject { ["ok"] = true, ["run"] = false });
        _displayCounters["events"] = (int)_displayCounters["events"] + 1;
        var kind = Str(a, "kind") ?? "metrics";
        var changed = a.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.Array ? ch.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();
        var p = _screen.Primary(out _);
        bool workOk = p is { } d && TopBarRules.WorkAreaCheck(d.TopDip, d.WorkTopDip).Ok;
        var v = TopBarDisplay.Verdict(new(kind, changed, !a.TryGetProperty("primary", out var pr) || pr.ValueKind != JsonValueKind.False, workOk, _ownAt, Now));
        if (!v.Run) { _displayCounters["skipped"] = (int)_displayCounters["skipped"] + 1; _displayCounters["lastSkip"] = v.Why; return Ok(new JsonObject { ["ok"] = true, ["run"] = false, ["why"] = v.Why }); }
        var when = _burst!.Fire(v.Why);
        return Ok(new JsonObject { ["ok"] = true, ["run"] = true, ["when"] = when, ["why"] = v.Why });
    }
    void DisplayRun(string why)
    {
        if (_stopped || _ending) return;
        _displayCounters["runs"] = (int)_displayCounters["runs"] + 1;
        _displayCounters["last"] = why;
        _ctx.Log($"display changed ({why}): re-placed{(_ctx.Enabled ? ", the strip re-asked and the drag guard re-armed" : "")}");
        Broadcast("topbar.place", new TopBarPlace(_menuOpen ? _menuH : TopBarRules.BarDip, "display changed (" + why + ")"));
        if (_ctx.Enabled)
        {
            _ = Run(async () => { await ReserveStrip($"display changed ({why})"); return null; }, "app bar display changed");
            _ = Run(async () => { await GuardOn(); return null; }, "drag guard display changed");
        }
    }

    // ------------------------------------------------------------------ focus (the sidecar's topbar events)
    void OnTopbar(JsonElement ev)
    {
        if (_stopped) return;
        var kind = Str(ev, "kind");
        if (kind == "guard")
        {
            bool dry = Bool(ev, "dryRun"), ok = Bool(ev, "ok");
            var r = Str(ev, "reason");
            _ctx.Log($"{(dry ? "would have put" : ok ? "put" : "could not put")} a window back below the bar ({Str(ev, "how")}){(r != null ? ": " + r : "")}");
            return;
        }
        if (_fixture != null) return;   // --test with a fixture: the made-up app holds, the real desktop waits
        var o = JsonNode.Parse(ev.GetRawText()) as JsonObject;
        if (o == null) return;
        o.Remove("ev");
        if (kind == "focus")
        {
            if (!B(o, "self")) { _focus = o; _ = Run(async () => { await Decide("focus"); return null; }, "focus"); }
        }
        else if (kind == "title" && _focus != null && S(_focus, "hwnd") == S(o, "hwnd"))
        {
            _focus["title"] = S(o, "title");
            if (B(o, "menuChanged")) _focus["menu"] = o["menu"]?.DeepClone();
        }
        else if (kind == "geometry" && _focus != null && S(_focus, "hwnd") == S(o, "hwnd"))
        {
            _focus["zoomed"] = B(o, "zoomed"); _focus["fullscreen"] = B(o, "fullscreen");
            _ = Run(async () => { await Decide("geometry"); return null; }, "geometry");
        }
        else return;
        Broadcast("topbar.focus", FocusPayload());
    }

    // ------------------------------------------------------------------ decide (TOP-260..266)
    async Task Decide(string from)
    {
        var f = _focus;
        bool self = B(f, "self");
        string? lockExe = f == null ? null : TopBarRules.LockScreen(S(f, "exe"), S(f, "desc"), self);
        var game = f?["game"] as JsonObject;
        bool verdict = B(game, "stepAside");
        bool full = f != null && !self && !B(f, "shell") && B(f, "fullscreen") && lockExe == null;
        var kind = TopBarRules.AsideKind(_hidden, lockExe, false, false, verdict, _overGames, full);
        bool want = kind != null;
        var signal = TopBarRules.Signal(kind, lockExe, null, _overGames, TopBarModel.AppName(f), S(game, "signal"));
        var logSig = TopBarRules.LogSignal(kind, signal);
        var was = _kind;
        if (want == _stepAside)
        {
            if (want && signal != _signal)
            {
                _signal = signal; _kind = kind;
                _ctx.Log($"still stepped aside ({from}): {logSig}");
                await Strip(kind, was);
                Publish();
            }
            return;
        }
        _stepAside = want; _signal = signal; _kind = kind; _stepSince = Now;
        if (want)
        {
            _ctx.Log($"stepped aside ({from}): {logSig}");
            Broadcast("topbar.close", new TopBarClose("stepped aside"));
            Hide("stepped aside: " + kind);
            Publish();
            await StatsOn(false, TopBarRules.StatsOffWords(kind!));
            await Strip(kind, was);
        }
        else
        {
            _ctx.Log($"back ({from}): {TopBarRules.BackWords(was)}");
            await Strip(kind, was);   // shown by hand: the strip is taken back BEFORE the bar shows
            Show("back");
            Publish();
            await StatsOn(true, null);
        }
    }

    /// Hidden by hand lets the strip and the guard go; shown by hand takes them back once (TOP-283/287).
    async Task Strip(string? kind, string? was)
    {
        if (kind == "hidden" && was != "hidden")
        {
            await _ctx.Platform.CallAsync("topbar.guard", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500));
            _guard = (false, $"off while the top bar is hidden by hand ({TopBarRules.HideChord} shows it)");
            var rel = await ReleaseStrip($"hidden by hand; {TopBarRules.HideChord} takes it back");
            _ctx.Log(rel.DryRun ? "hidden by hand: a --test instance holds no strip, so there was none to let go"
                : rel.Ok ? $"hidden by hand: let go of the top {TopBarRules.BarDip} DIP, so maximised windows get the whole screen"
                : $"hidden by hand, but could NOT let go of the top {TopBarRules.BarDip} DIP ({rel.Reason}), so maximised windows still start below it");
            Health();
        }
        else if (was == "hidden" && kind != "hidden")
        {
            await GuardOn();
            if (_live) await ReserveStrip("shown by hand");
            else _ctx.Log("app bar shown by hand: a --test instance never reserves space on the screen, so there was nothing to take back");
            Health();
        }
    }

    // ------------------------------------------------------------------ hidden by hand (TOP-204, TOP-281..287, BAR-348/350)
    async Task<JsonObject> SetHidden(bool on, string via)
    {
        if (on != _hidden)
        {
            _hidden = on;
            await _ctx.Settings.SetAsync("hidden", JsonValue.Create(on));
            _ctx.Log(on ? $"hidden by hand ({via}); {TopBarRules.HideChord} or \"Show top bar\" in quick settings shows it again, also after a restart"
                : $"shown by hand ({via})" + (_peekShown ? " while it was peeking: it stays, now for good" : ""));
            if (!on && _ctx.Enabled && !_stopped) Asked("shown by hand");
            if (!on) PeekSync(via, keepUp: true);
            if (_ctx.Enabled && !_stopped) await Decide(via);
            else _ctx.Log($"the top bar is switched off in the tray, so it is only remembered: {(on ? "hidden" : "shown")} when it is switched on");
            if (on) PeekSync(via);
        }
        Publish();
        var o = ViewObj(); o["ok"] = true;
        return o;
    }

    // ------------------------------------------------------------------ the strip: reserve / release (TOP-241..253)
    async Task ReserveStrip(string why)
    {
        if (_stopped || _ending || !_ctx.Enabled) return;
        if (_hidden) { if (_live) _ctx.Log($"app bar {why}: not taken: the top bar is hidden by hand ({TopBarRules.HideChord} shows it)"); return; }
        var d = _screen.Primary(out var pwhy);
        double scale = d?.Scale ?? _scale;
        int size = TopBarRules.ReserveSize(scale, _dpiAware != false);
        if (!_live)
        {
            _ctx.Log($"app bar {why}: dry run (--test): would have reserved {size} px ({TopBarRules.BarDip} DIP at {scale.ToString(CultureInfo.InvariantCulture)}x) at the top of the primary display; a --test instance never reserves space on the screen");
            return;
        }
        if (_hwnd == IntPtr.Zero) return;   // the window is not made yet: Placed() reserves when it is
        Asked(why);
        _ownAt = Now;
        var r = await _ctx.Platform.CallAsync("topbar.reserve", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["size"] = size });
        _ownAt = Now;
        if (_ending || _stopped) return;
        if (_hidden)
        {
            _ctx.Log($"app bar {why}: the top bar was hidden by hand meanwhile, so the strip was let go again");
            await ReleaseStrip($"hidden by hand; {TopBarRules.HideChord} takes it back");
            return;
        }
        if (!r.Ok)
        {
            _reserve = new(false, $"the strip could not be reserved ({r.Error}), so maximised windows go under the bar", null, size);
            _ctx.Log($"app bar {why}: {_reserve.Reason}");
            Health();
            return;
        }
        Broadcast("topbar.place", new TopBarPlace(_menuOpen ? _menuH : TopBarRules.BarDip, "reserved"));
        Later(TopBarRules.WorkCheckMs, () =>
        {
            var p = _screen.Primary(out var w2);
            var w = p is { } dd ? TopBarRules.WorkAreaCheck(dd.TopDip, dd.WorkTopDip) : (Ok: false, Want: double.NaN, Got: double.NaN);
            _reserve = new(w.Ok, w.Ok ? $"reserved {size}px ({TopBarRules.BarDip} DIP at {scale.ToString(CultureInfo.InvariantCulture)}x); the work area starts at the bar's bottom ({w.Got.ToString(CultureInfo.InvariantCulture)})"
                : p == null ? $"reserved {size}px, but the work area could not be read ({w2})"
                : $"reserved {size}px, but the work area starts at {w.Got.ToString(CultureInfo.InvariantCulture)} DIP instead of the bar's bottom {w.Want.ToString(CultureInfo.InvariantCulture)} DIP", null, size);
            _ctx.Log($"app bar {why}: {_reserve.Reason}");
            if (TopBarRules.ReRegister(w.Ok, why == TopBarRules.ReRegisterWhy, _reRegAt, Now) && !_hidden)
            {
                _reRegAt = Now;
                _ctx.Log($"app bar {why}: the work area ignored the strip, so the bar is registered with Windows afresh (Explorer may have restarted)");
                _ = Run(async () => { await ReleaseStrip(TopBarRules.ReRegisterWhy); await ReserveStrip(TopBarRules.ReRegisterWhy); return null; }, "app bar: registering afresh failed");
            }
            Health();
        });
    }

    async Task<(bool Ok, bool DryRun, string? Reason)> ReleaseStrip(string? why)
    {
        if (!_live || _hwnd == IntPtr.Zero) return (true, true, null);
        _ownAt = Now;
        var r = await _ctx.Platform.CallAsync("topbar.release", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture) }, TimeSpan.FromSeconds(2));
        _ownAt = Now;
        _reserve = new(false, r.Ok ? (why != null ? $"let go: {why}" : "released") : $"release failed: {r.Error}");
        return (r.Ok, false, r.Error);
    }

    /// The drag guard (TOP-254/255/257): the bar's physical rect; off while hidden by hand. A --test platform is passive.
    async Task GuardOn()
    {
        if (_hidden || _stopped) return;
        var d = _screen.Primary(out var why);
        if (d is not { } p) { _guard = (false, why); return; }
        int h = (int)TopBarModel.JsRound(TopBarRules.BarDip * p.Scale);
        var r = await _ctx.Platform.CallAsync("topbar.guard", new JsonObject { ["on"] = true, ["bar"] = new JsonArray(p.X, p.Y, p.X + p.W, p.Y + h) });
        bool passive = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("passive", out var pv) && pv.ValueKind == JsonValueKind.True;
        _guard = (r.Ok, r.Ok ? (passive ? "passive: drags into the strip are measured and logged, nothing is moved" : null) : r.Error);
        if (!r.Ok) Broadcast("topbar.note", $"A window dragged into the top bar will not be put back: {r.Error}");
    }

    async Task StatsOn(bool on, string? why)
    {
        if (on && (_stepAside && !_peekShown)) return;
        _statsOn = (on, on ? "on" : why ?? "off");
        if (_fixture != null) { Broadcast("topbar.stats", StatsPayload()); return; }
        var r = await _ctx.Platform.CallAsync("topbar.stats", new JsonObject { ["on"] = on }, TimeSpan.FromMilliseconds(1500));
        if (!r.Ok) _statsOn = (false, $"the stats sampler did not start: {r.Error}");
        if (!on) { _stats = null; Broadcast("topbar.stats", StatsPayload()); }
    }

    // ------------------------------------------------------------------ the run (TOP-247, TOP-208)
    async Task Apply(string why)
    {
        if (_stopped) return;
        if (!_ctx.Enabled) { await Off("the top bar is switched off in the tray"); return; }
        _applied = true;
        Asked(why, TopBarRules.ReserveStartGraceMs);
        var p = await _ctx.Platform.CallAsync("ping");
        _dpiAware = p.Ok ? p.Result.ValueKind == JsonValueKind.Object && p.Result.TryGetProperty("dpiAware", out var da) && da.ValueKind == JsonValueKind.True : null;
        var w = await _ctx.Platform.CallAsync("topbar.watch", new JsonObject { ["on"] = true });
        _watch = w.Ok ? (true, null) : (false, w.Error);
        if (w.Ok && _fixture == null && w.Result.ValueKind == JsonValueKind.Object && w.Result.TryGetProperty("focus", out var fo) && fo.ValueKind == JsonValueKind.Object)
        {
            var o = JsonNode.Parse(fo.GetRawText()) as JsonObject;
            if (o != null && !B(o, "self")) { _focus = o; Broadcast("topbar.focus", FocusPayload()); }
        }
        await Decide(why);
        await ReserveStrip(why);
        if (_live) foreach (var ms in new[] { 1500, 6000 }) Later(ms, () => _ = Run(async () => { await ReserveStrip($"{ms / 1000.0} s re-check".Replace(".0 s", " s")); return null; }, "app bar"));
        await GuardOn();
        if (!_stepAside) await StatsOn(true, null);
        Show(why);
        Publish();
        PeekSync(why);
        Health();
    }

    async Task Off(string why)
    {
        PeekSync(why);
        Hide(why);
        _shown = false;
        await Task.WhenAll(
            _ctx.Platform.CallAsync("topbar.watch", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).AsTask(),
            _ctx.Platform.CallAsync("topbar.guard", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).AsTask(),
            _ctx.Platform.CallAsync("topbar.stats", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).AsTask(),
            ReleaseStrip(null));
        _watch = (false, why); _statsOn = (false, why);
        Publish();
    }

    // ------------------------------------------------------------------ the peek (TOP-289..305, BAR-349)
    bool PeekWanted() => _hidden && _ctx.Enabled && !_stopped && _placed;
    string PeekStopWhy() => _stopped ? "the top bar stopped" : !_ctx.Enabled ? "the top bar was switched off in the tray" : !_hidden ? "shown by hand" : "the window is not made yet";

    void PeekSync(string why, bool keepUp = false)
    {
        bool want = PeekWanted();
        if (want && !_peekWatching)
        {
            _peekWatching = true;
            _peekSt = TopBarPeek.StartState();
            _peekBlocked = null; _peekLooked = false;
            PeekProbe();
            _ctx.Log($"peek: watching the top edge ({why})");
            PeekSchedule();
            Publish();
        }
        else if (!want && _peekWatching)
        {
            _peekWatching = false;
            _peekT?.Dispose(); _peekT = null;
            _ctx.Log($"peek: stopped watching the top edge: {PeekStopWhy()}");
            if (_peekShown) { if (keepUp) _peekShown = false; else PeekHide(PeekStopWhy()); }
            _peekOk = null; _peekReason = null;
            Publish();
        }
    }
    void PeekProbe()
    {
        string? why = null;
        if (!_fakeSource) { if (_screen.Cursor(out var cw) == null) why = $"the cursor cannot be read ({cw})"; else if (!_screen.Buttons().Ok) why = $"the mouse buttons cannot be read ({_screen.Buttons().Reason})"; }
        bool was = _peekOk == false;
        _peekOk = why == null; _peekReason = why;
        if (was && why == null) _ctx.Log("the top edge is watched again");
    }
    void PeekSchedule() { _peekT?.Dispose(); _peekT = Later(TopBarPeek.PollMs, PeekTick); }
    void PeekTick()
    {
        _peekT = null;
        if (!_peekWatching) return;
        var d = _screen.Primary(out _);
        double s = d?.Scale ?? _scale;
        var bounds = d is { } p ? new PeekRect(p.X / s, p.Y / s, p.W / s, p.H / s) : new PeekRect(0, 0, 0, 0);
        PeekPoint? pt; string? cwhy = null;
        if (_fakeSource) pt = _fakeCursor is { } fc ? new PeekPoint(fc.X, fc.Y) : null;
        else { var c = _screen.Cursor(out cwhy); pt = c is { } cc ? new PeekPoint(cc.X / s, cc.Y / s) : null; }
        var f = _focus;
        string? lockExe = f == null ? null : TopBarRules.LockScreen(S(f, "exe"), S(f, "desc"), B(f, "self"));
        var guard = TopBarPeek.Blocker(new PeekGuard(_hidden, _ctx.Enabled, _stopped, null, lockExe,
            B(f?["game"] as JsonObject, "stepAside") && !_overGames ? "the sidecar said so" : null,
            f != null && B(f, "fullscreen") && !B(f, "self") && !B(f, "shell") ? TopBarModel.AppName(f) : null));
        var r = TopBarPeek.Step(_peekSt, new PeekInput(Now, pt, cwhy, bounds, new PeekRect(bounds.X, bounds.Y, bounds.W, _menuOpen ? _menuH : TopBarRules.BarDip),
            _menuOpen, guard, false, () => _fakeSource ? new PeekButtons(true) : _screen.Buttons()));
        _peekSt = r.St;
        // what keeps the edge from peeking, said once per change (never silent); a --test run also says its first look
        if (r.Blocked != _peekBlocked) { _peekBlocked = r.Blocked; if (r.Blocked != null) _ctx.Log($"peek: not now: {r.Blocked}"); }
        if (_test && !_peekLooked)
        {
            _peekLooked = true;
            _ctx.Log($"peek: first look: the cursor {(pt is { } q ? $"at {q.X:0.#},{q.Y:0.#} DIP" : $"unreadable ({cwhy ?? "no reason"})")}, the edge band the top {TopBarPeek.BandDip} DIP of {bounds.X:0.#},{bounds.Y:0.#} {bounds.W:0.#}x{bounds.H:0.#} DIP, phase {r.St.Phase}");
        }
        if (r.Act == "show") PeekShow(r.Why!);
        else if (r.Act == "hide") PeekHide(r.Why!);
        PeekSchedule();
    }
    void PeekShow(string why)
    {
        _peekShown = true; _peekSince = Now;
        _ctx.Log($"peek shown: {why}; over the screen, the strip stays let go and it is still hidden by hand");
        Visible(true, "peek");
        Broadcast("topbar.peek", true);
        _ = StatsOn(true, null);
        Publish();
    }
    void PeekHide(string why)
    {
        if (!_peekShown) return;
        _peekShown = false;
        _ctx.Log($"peek hidden after {((Now - _peekSince) / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s: {why}");
        Broadcast("topbar.close", new TopBarClose("the peek ended"));
        Visible(false, "peek ended");
        Broadcast("topbar.peek", false);
        _ = StatsOn(false, "the peek ended");
        Publish();
    }

    // ------------------------------------------------------------------ stop (rest / quit): put Windows back
    public async ValueTask StopAsync(FeatureStop stop)
    {
        if (_stopped) return;
        _burst?.Stop();
        _peekT?.Dispose(); _peekT = null; _peekWatching = false;
        _graceT?.Dispose(); _graceT = null;
        Hide(stop.Reason);
        await Task.WhenAll(
            _ctx.Platform.CallAsync("topbar.watch", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).AsTask(),
            _ctx.Platform.CallAsync("topbar.guard", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).AsTask(),
            _ctx.Platform.CallAsync("topbar.stats", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).AsTask(),
            ReleaseStrip("Mantle is putting Windows back"));
        _stopped = true;
    }
}
