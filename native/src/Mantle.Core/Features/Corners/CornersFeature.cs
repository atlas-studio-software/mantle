using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.Corners;

// HOT CORNERS, the live part: lib/features/corners.js (lane native-r7-desktop). The poll, the guard at the moment of
// firing, the actions and the words. The rules are CornerRules (pure); the claim table the edges read is CornerClaims.
//
// A --test run: EVERY action is a dry run (Task view asks only whether Win+Tab's feature could take it; Show desktop
// asks the passive sidecar with dryRun; the launcher and quick settings are only looked for; Lock is never called), and
// the REAL cursor is never read: the watch runs only on the test pointer (MANTLE_CORNERS_SELFTEST=1 drives it), so a
// test never pushes the runner's cursor into a corner where Windows has actions of its own.

[Feature("corners", "Hot corners", true, Rest = true)]
public sealed class CornersFeature : IFeature
{
    /// Opens Mantle's Start in apps mode with an empty query (dry: only looks for it). Wired by the app (App.xaml.cs);
    /// null = "this Mantle has no launcher to open".
    public static Func<bool, CornerFx>? OpenLauncher { get; set; }
    /// Toggles quick settings on the bar (dry: only looks for the bar). Wired by the app; null = "this build of the bar cannot open quick settings from a corner yet".
    public static Func<bool, Task<CornerFx>>? ToggleQs { get; set; }

    IFeatureContext _ctx = null!;
    HostClock _clock = null!;
    ICornerPointer? _pointer;
    TestCornerPointer? _testPointer;
    CornerTable _actions = CornerRules.Defaults;
    CornerState _st = CornerRules.StartState();
    IDisposable? _timer;
    bool _watching, _busy, _stopped, _unhealthy;
    string _watchWhy = "not started yet";
    IReadOnlyList<DipRect> _displays = Array.Empty<DipRect>();
    long _displaysAt = long.MinValue;
    JsonObject? _last;
    readonly Dictionary<string, string> _fails = new();
    readonly List<string> _testGames = new();
    readonly CornerClaims _claims;

    /// For the self-test and the unit tests: every fire's outcome.
    public event Action<string, string, CornerOutcome?, string?>? Fired;

    public CornersFeature() : this(CornerClaims.Live) { }
    public CornersFeature(CornerClaims claims, ICornerPointer? pointer = null, HostClock? clock = null)
    {
        _claims = claims; _pointer = pointer;
        if (clock != null) _clock = clock;
    }

    public bool Watching => _watching;
    public CornerTable Table => _actions;
    public CornerState WatchState => _st;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _clock ??= new HostClock(null, SynchronizationContext.Current);
        if (ctx.Flags.Test) {
            _testPointer = _pointer as TestCornerPointer ?? new TestCornerPointer { Pt = null, Why = "a test run never reads the real cursor; only the test pointer drives the corners" };
            _pointer = _testPointer;
            foreach (var g in (Environment.GetEnvironmentVariable("MANTLE_CORNERS_TEST_GAME") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _testGames.Add(g.ToLowerInvariant());
        } else _pointer ??= new Win32CornerPointer();
        _actions = Load();
        ctx.Command("state", _ => new(CommandResult.Success(El(StateJson()))));
        ctx.Command("set", c => SetAsync(c.Args));
        ctx.Replay("corners.state", () => StateJson());
        ctx.OnEnabled(on => Restart(on ? "switched on in the tray" : "switched off in the tray"));
        Restart("Mantle started");
        if (ctx.Flags.Test && Environment.GetEnvironmentVariable("MANTLE_CORNERS_SELFTEST") == "1")
            _clock.Later(TimeSpan.FromMilliseconds(800), () => _ = SelfTestAsync());
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        Halt(stop.Reason);
        return ValueTask.CompletedTask;
    }

    // ---- settings ----

    CornerTable Load()
    {
        try {
            if (_ctx.Settings.TryGet("actions", out var v) && v.ValueKind == JsonValueKind.Object) {
                var d = new Dictionary<string, string?>();
                foreach (var p in v.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                return CornerRules.Normalize(d);
            }
            if (_ctx.Settings.Reason is { Length: > 0 } why && _ctx.Settings.State != Data.ReadState.Missing)
                _ctx.Log($"the saved corners could not be read ({why}); using the defaults");
        } catch (Exception e) { _ctx.Log($"the saved corners could not be read ({e.Message}); using the defaults"); }
        return CornerRules.Defaults;
    }

    static string Summary(CornerTable t)
    {
        var on = t.Each().Where(e => e.Action != "off").Select(e => $"{CornerRules.NameOf(e.Corner)} {CornerRules.ActionName(e.Action)}").ToArray();
        return on.Length == 0 ? "every corner is off" : string.Join(", ", on);
    }

    async ValueTask<CommandResult> SetAsync(JsonElement a)
    {
        string? corner = Str(a, "corner"), action = Str(a, "action");
        string via = Clip(Str(a, "via")) ?? "a command";
        if (corner == null || !CornerRules.Corners.Contains(corner))
            return Answer(false, $"\"{Clip(corner) ?? ""}\" is not a corner (tl, tr, bl, br)");
        if (action == null || !CornerRules.Actions.Contains(action))
            return Answer(false, $"\"{Clip(action) ?? ""}\" is not a corner action ({string.Join(", ", CornerRules.Actions)})");
        if (_actions.Of(corner) == action) return Answer(true, null);
        var next = _actions.With(corner, action);
        var obj = new JsonObject();
        foreach (var (c, act) in next.Each()) obj[c] = act;
        var w = await _ctx.Settings.SetAsync("actions", obj).ConfigureAwait(true);
        if (!w.Ok) return Answer(false, $"it could not be saved: {w.Reason}");
        _actions = next;
        _fails.Remove(corner);
        _ctx.Log($"the {CornerRules.NameOf(corner)} corner is now {CornerRules.ActionName(action)} ({via}): {Summary(_actions)}");
        Restart($"the {CornerRules.NameOf(corner)} corner changed");
        return Answer(true, null);
    }

    CommandResult Answer(bool ok, string? reason)
    {
        var o = new JsonObject { ["ok"] = ok, ["reason"] = reason, ["state"] = StateJson() };
        return CommandResult.Success(El(o));
    }

    // ---- the watch ----

    void Restart(string why)
    {
        Halt(null);
        if (_stopped) return;
        if (!_ctx.Enabled) { Say(false, "switched off in the tray", why); return; }
        if (!CornerRules.AnyOn(_actions)) { Say(false, "every corner is off", why); return; }
        if (_ctx.Flags.Test && _testPointer?.Pt == null && !_selfTesting) {
            Say(false, "a test run never reads the real cursor; the corners run only on the test pointer (MANTLE_CORNERS_SELFTEST=1)", why);
            return;
        }
        _st = CornerRules.StartState();
        _watching = true;
        _displaysAt = long.MinValue;
        ReadDisplays();
        _claims.Set(_actions, _displays);
        var p = _displays.Count > 0 ? _displays[0] : new DipRect(0, 0, 0, 0);
        _watchWhy = "watching";
        _ctx.Log($"watching the corners ({Summary(_actions)}) of the {p.Width}x{p.Height} DIP screen: a {CornerRules.CornerZoneDip} DIP square each, {CornerRules.CornerDwellMs} ms at rest fires once, polled every {CornerRules.CornerPollMs} ms ({why})");
        Broadcast();
        _timer = _clock.Later(TimeSpan.FromMilliseconds(CornerRules.CornerPollMs), Tick);
    }

    void Say(bool watching, string reason, string why)
    {
        _watching = watching; _watchWhy = reason;
        _ctx.Log($"stopped watching the corners: {reason} ({why})");
        Broadcast();
    }

    void Halt(string? why)
    {
        _timer?.Dispose(); _timer = null;
        bool was = _watching;
        _watching = false;
        _claims.Set(null);
        if (was && why != null) { _watchWhy = why; _ctx.Log($"stopped watching the corners: {why}"); Broadcast(); }
    }

    void ReadDisplays()
    {
        long now = _clock.NowMs;
        if (now - _displaysAt < CornerRules.DisplaysMs && _displays.Count > 0) return;
        _displaysAt = now;
        var d = _pointer!.Displays();
        if (d.Count > 0 && !d.SequenceEqual(_displays)) { _displays = d; if (_watching) _claims.Set(_actions, _displays); }
    }

    void Tick()
    {
        _timer = null;
        if (!_watching || _stopped) return;
        try { Poll(); }
        catch (Exception e) { _ctx.Log($"a corner poll threw: {e.Message}"); }
        if (_watching && !_stopped) _timer = _clock.Later(TimeSpan.FromMilliseconds(CornerRules.CornerPollMs), Tick);
    }

    void Poll()
    {
        ReadDisplays();
        var (pt, why) = _pointer!.Read();
        var r = CornerRules.Step(_st, new CornerInput {
            Now = _clock.NowMs, Pt = pt, CursorWhy = why,
            Bounds = _displays.Count > 0 ? _displays[0] : new DipRect(0, 0, 0, 0), Displays = _displays,
            Actions = _actions, Guard = _busy ? "the last corner action is still running" : null,
            Buttons = _pointer.Buttons,
        });
        _st = r.St;
        bool bad = r.Blocked != null && (r.Blocked.StartsWith("the cursor could not be read", StringComparison.Ordinal) || r.Blocked.StartsWith("the mouse buttons cannot be read", StringComparison.Ordinal));
        if (bad && !_unhealthy) { _unhealthy = true; _ctx.Health(false, "the corners cannot fire: " + r.Blocked); _watchWhy = r.Blocked!; Broadcast(); }
        else if (!bad && _unhealthy && pt != null) { _unhealthy = false; _ctx.Health(true); _watchWhy = "watching"; _ctx.Log("the corner watch reads the cursor and the mouse buttons again"); Broadcast(); }
        if (r.Act == "fire" && r.Corner != null && r.Action != null) _ = FireAsync(r.Corner, r.Action, r.Why ?? "");
    }

    async Task FireAsync(string corner, string action, string why)
    {
        _busy = true;
        string label = $"{CornerRules.NameOf(corner)} corner -> {CornerRules.ActionName(action)}";
        try {
            var scan = await ScanAsync().ConfigureAwait(true);
            string? blocked = CornerRules.FireBlocker(scan, TestGame);
            if (_stopped || !_watching) { _ctx.Log($"{label}: not run: the corners stopped while the window in front was checked"); return; }
            if (blocked != null) {
                _last = new JsonObject { ["corner"] = corner, ["action"] = action, ["blocked"] = blocked, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                _ctx.Log($"{label}: not run: {blocked}");
                Fired?.Invoke(corner, action, null, blocked);
                Broadcast();
                return;
            }
            var o = await CornerActions.RunAsync(action, new Effects(this), _ctx.Flags.Test).ConfigureAwait(true);
            _last = new JsonObject { ["corner"] = corner, ["action"] = action, ["ok"] = o.Ok, ["dryRun"] = o.DryRun, ["said"] = o.Said, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
            if (o.Ok) _fails.Remove(corner); else _fails[corner] = o.Said;
            _ctx.Log(o.Ok ? $"{label}: {o.Said} ({why})" : $"{label}: FAILED: {o.Said} ({why})");
            Fired?.Invoke(corner, action, o, null);
            Broadcast();
        } finally { _busy = false; }
    }

    /// One gamemode.scan {procs:true} at the moment of firing (1500 ms), never per poll.
    async Task<CornerScan?> ScanAsync()
    {
        var r = await _ctx.Platform.CallAsync("gamemode.scan", new JsonObject { ["procs"] = true }, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
        if (!r.Ok) return new CornerScan(false, r.Error, null, null, false);
        var e = r.Result;
        if (e.ValueKind != JsonValueKind.Object) return new CornerScan(false, "the scan answered with nothing", null, null, false);
        if (e.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return new CornerScan(false, e.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null, null, null, false);
        string? exe = null, title = null; bool full = false;
        if (e.TryGetProperty("fg", out var fg) && fg.ValueKind == JsonValueKind.Object) {
            exe = fg.TryGetProperty("exe", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            title = fg.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            full = fg.TryGetProperty("fullscreen", out var f) && f.ValueKind == JsonValueKind.True;
        }
        int? own = e.TryGetProperty("ownSession", out var os) && os.ValueKind == JsonValueKind.Number ? os.GetInt32() : null;
        var procs = new List<(string, int)>();
        if (e.TryGetProperty("procs", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("file", out var pf) && pf.ValueKind == JsonValueKind.String)
                    procs.Add((pf.GetString()!, p.TryGetProperty("session", out var se) && se.ValueKind == JsonValueKind.Number ? se.GetInt32() : -1));
        return new CornerScan(true, null, exe, title, full, own, procs);
    }

    /// The game plan's check is the game-mode lane's; until it lands only the self-test's stand-in names count
    /// (MANTLE_CORNERS_TEST_GAME, --test only, during the self-test's game case), running in the user's own session.
    string? TestGame(CornerScan s)
    {
        if (!_gameCase || _testGames.Count == 0 || s.Procs == null) return null;
        foreach (var (file, session) in s.Procs)
            if (_testGames.Contains(file.ToLowerInvariant()) && (s.OwnSession == null || session == s.OwnSession)) return $"{file} is running (a known game)";
        return null;
    }

    sealed class Effects : ICornerEffects
    {
        readonly CornersFeature _f;
        public Effects(CornersFeature f) { _f = f; }
        public CornerFx Chord(string chord, bool dry)
        {
            if (_f._ctx is not IFeatureHostAccess h) return new CornerFx(false, "no feature takes Win+Tab", dry);
            var d = h.RunChord(chord, "a hot corner", dry);
            return new CornerFx(d.Delivered || d.DryRun, d.Reason, d.DryRun, Feature: d.Feature);
        }
        public async Task<CornerFx> DesktopAsync(bool dry)
        {
            var a = new JsonObject(); if (dry) a["dryRun"] = true;
            var r = await _f._ctx.Platform.CallAsync("hotcorners.desktop", a, TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
            if (!r.Ok) return new CornerFx(false, r.Error, dry);
            var e = r.Result;
            string? S(string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            bool ok = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            return new CornerFx(ok, ok ? null : S("reason") ?? "the sidecar answered without ok", dry, S("called"), S("wouldCall"));
        }
        public CornerFx Launcher(bool dry) => OpenLauncher?.Invoke(dry) ?? new CornerFx(false, "this Mantle has no launcher to open", dry);
        public Task<CornerFx> QsAsync(bool dry) => ToggleQs?.Invoke(dry) ?? Task.FromResult(new CornerFx(false, "this build of the bar cannot open quick settings from a corner yet", dry));
        public CornerFx Lock(bool dry) => dry ? new CornerFx(true, DryRun: true, WouldCall: "user32 LockWorkStation") : Win32CornerPointer.Lock();
    }

    // ---- state ----

    JsonObject StateJson()
    {
        var acts = new JsonObject(); foreach (var (c, a) in _actions.Each()) acts[c] = a;
        var names = new JsonObject(); foreach (var c in CornerRules.Corners) names[c] = CornerRules.NameOf(c);
        var corners = new JsonArray(); foreach (var c in CornerRules.Corners) corners.Add((JsonNode)c);
        var choices = new JsonArray();
        foreach (var a in CornerRules.Actions) choices.Add((JsonNode)new JsonObject { ["id"] = a, ["label"] = CornerRules.ActionName(a) });
        var fails = new JsonObject(); foreach (var (c, s) in _fails) fails[c] = s;
        return new JsonObject {
            ["enabled"] = _ctx?.Enabled ?? false, ["test"] = _ctx?.Flags.Test ?? false, ["actions"] = acts, ["corners"] = corners,
            ["names"] = names, ["choices"] = choices, ["watching"] = _watching,
            ["watch"] = new JsonObject { ["ok"] = !_unhealthy, ["reason"] = _watchWhy },
            ["last"] = _last?.DeepClone(), ["fails"] = fails, ["zone"] = CornerRules.CornerZoneDip, ["dwell"] = CornerRules.CornerDwellMs,
        };
    }

    void Broadcast() { try { _ctx.Broadcast("corners.state", StateJson()); } catch { /* stopped */ } }

    static string? Str(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string? Clip(string? s) => s == null ? null : (s.Replace('\n', ' ').Replace('\r', ' ') is var one && one.Length > 40 ? one[..40] : one);
    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

    // ---- the self-test (--test + MANTLE_CORNERS_SELFTEST=1): CORN-060 ----

    bool _selfTesting, _gameCase;

    async Task SelfTestAsync()
    {
        var tp = _testPointer!;
        var fails = new List<string>();
        int ran = 0, cases = 0;
        IntPtr fgBefore = Win32CornerPointer.Foreground();
        var saved = _actions;
        _selfTesting = true;
        var screen = tp.Screens[0];
        var mid = new DipPoint(screen.X + screen.Width / 2, screen.Y + screen.Height / 2);
        DipPoint At(string c) => new(c[1] == 'l' ? screen.X + 1 : screen.X + screen.Width - 2, c[0] == 't' ? screen.Y + 1 : screen.Y + screen.Height - 3);
        var got = new List<(string C, string A, CornerOutcome? O, string? B)>();
        void On(string c, string a, CornerOutcome? o, string? b) => got.Add((c, a, o, b));
        Fired += On;
        async Task Wait(int ms) => await Task.Delay(ms).ConfigureAwait(true);
        try {
            _ctx.Log("SELFTEST: driving the corners with a made-up cursor (the real one is never read or moved)");
            foreach (var c in CornerRules.Corners)
                foreach (var a in CornerRules.Actions.Where(x => x != "off")) {
                    cases++;
                    got.Clear();
                    tp.Pt = mid; tp.Btn = new ButtonRead(true);
                    _actions = new CornerTable("off", "off", "off", "off").With(c, a);
                    Restart($"self-test {c} x {a}");
                    await Wait(250);
                    tp.Pt = At(c);
                    for (int i = 0; i < 70 && got.Count == 0; i++) await Wait(50);
                    if (got.Count != 1) { fails.Add($"{c} x {a}: {got.Count} fires"); continue; }
                    var g = got[0];
                    if (g.B != null) fails.Add($"{c} x {a}: refused: {g.B}");
                    else if (g.O == null || !g.O.DryRun) fails.Add($"{c} x {a}: NOT a dry run: {g.O?.Said}");
                    else { ran++; _ctx.Log($"SELFTEST case {c} x {a}: {g.O.Said}"); }
                    tp.Pt = mid; await Wait(150);
                }
            // a held button in the corner never fires; a flick through never fires
            got.Clear();
            _actions = new CornerTable("taskview", "off", "off", "off");
            tp.Pt = mid; Restart("self-test held button"); await Wait(250);
            tp.Btn = new ButtonRead(true, Held: true, Pressed: true); tp.Pt = At("tl"); await Wait(700);
            bool heldOk = got.Count == 0 && _st.Phase == "disarmed";
            if (!heldOk) fails.Add($"a held button fired ({got.Count}) or did not disarm ({_st.Phase})");
            tp.Btn = new ButtonRead(true); tp.Pt = mid; await Wait(250);
            tp.Pt = At("tl"); await Wait(120); tp.Pt = mid; await Wait(400);
            bool flickOk = got.Count == 0;
            if (!flickOk) fails.Add("a flick through the corner fired");
            // the game stand-in refuses
            string gameSaid = "no stand-in game (MANTLE_CORNERS_TEST_GAME unset)";
            if (_testGames.Count > 0) {
                got.Clear(); tp.Pt = mid; _gameCase = true; Restart("self-test game stand-in"); await Wait(250);
                tp.Pt = At("tl");
                for (int i = 0; i < 70 && got.Count == 0; i++) await Wait(50);
                if (got.Count == 1 && got[0].B is { } b && b.Contains("(a known game)", StringComparison.Ordinal)) gameSaid = "the game stand-in refused: " + b;
                else { gameSaid = "the game stand-in did NOT refuse"; fails.Add(gameSaid + $" ({(got.Count > 0 ? got[0].B ?? got[0].O?.Said : "no fire")})"); }
                tp.Pt = mid; _gameCase = false; await Wait(150);
            }
            bool fgSame = Win32CornerPointer.Foreground() == fgBefore;
            if (!fgSame) fails.Add("the foreground window changed");
            string tail = $"{ran} of {cases} corner x action dry runs, a held button {(heldOk ? "never fired" : "FIRED")}, a flick {(flickOk ? "never fired" : "FIRED")}, {gameSaid}, the foreground window {(fgSame ? "never changed" : "CHANGED")}";
            _ctx.Log(fails.Count == 0 ? "SELFTEST ok: " + tail : "SELFTEST FAILED: " + string.Join("; ", fails) + " | " + tail);
        } catch (Exception e) {
            _ctx.Log("SELFTEST FAILED: it threw: " + e.Message);
        } finally {
            Fired -= On;
            _selfTesting = _gameCase = false;
            _actions = saved;
            tp.Pt = null;
            Restart("the self-test ended");
        }
    }
}
