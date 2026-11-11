using System;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The top bar feature (lib/features/topbar.js's state machine) on the host test kit: a fake platform (the sidecar's
/// topbar.* verbs), a fake screen (a 2304 x 1296 primary at 150 %), manual time. The window is played by calling the
/// surface's own commands (topbar.placed). A --test run never asks for the app bar; a live run does, with the size in
/// the sidecar's units. Nothing touches the real screen.
/// </summary>
public sealed class TopBarFeatureTests
{
    sealed class FakeScreen : ITopBarScreen
    {
        public int WorkY = 42;
        public (int X, int Y)? Cur = (700, 600);
        public TopBarDisplayInfo? Primary(out string? why) { why = null; return new TopBarDisplayInfo(0, 0, 2304, 1296, WorkY, 1.5); }
        public (int X, int Y)? Cursor(out string? why) { why = Cur == null ? "denied" : null; return Cur; }
        public PeekButtons Buttons() => new(true);
    }

    static void T(Func<Rig, FakeScreen, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test), new FakeScreen()));

    static FeatureDescriptor D(Rig r, FakeScreen s, Action<TopBarFeature>? made = null)
    {
        var a = typeof(TopBarFeature).GetCustomAttribute<FeatureAttribute>()!;
        return FeatureDescriptor.Of(a, () => { var f = new TopBarFeature(new HostClock(r.Time, SynchronizationContext.Current), s); made?.Invoke(f); return f; }, TopBarFeature.Options);
    }

    static void Sidecar(Rig r)
    {
        r.Platform.Answer("ping", "{\"dpiAware\":true}");
        r.Platform.Answer("topbar.watch", "{\"ok\":true}");
        r.Platform.Answer("topbar.guard", "{\"guard\":true,\"passive\":true}");
        r.Platform.Answer("topbar.stats", "{\"ok\":true}");
        r.Platform.Answer("topbar.reserve", "{\"ok\":true}");
        r.Platform.Answer("topbar.release", "{\"ok\":true}");
        r.Platform.Start();
    }

    static async Task Tick(Rig r, int ms)
    {
        for (int t = 0; t < ms; t += 50) { r.Time.AdvanceMs(50); for (int i = 0; i < 8; i++) await Task.Yield(); }
        for (int i = 0; i < 20; i++) await Task.Yield();
    }

    static async Task<TopBarFeature> Up(Rig r, FakeScreen s, string hwnd = "123")
    {
        TopBarFeature? f = null;
        Sidecar(r);
        r.Host.Load(new[] { D(r, s, x => f = x) });
        await r.Host.Installed;
        await Tick(r, 50);
        await r.Bus.CallAsync("topbar.placed", new JsonObject { ["hwnd"] = hwnd, ["scale"] = 1.5 });
        await Tick(r, 400);
        return f!;
    }

    static TopBarVisible[] Vis(Rig r) => r.Events.Published.Where(p => p.Name == "topbar.visible").Select(p => (TopBarVisible)p.Payload!).ToArray();
    static int Calls(Rig r, string verb) => r.Platform.Calls.Count(c => c.Verb == verb);

    [Fact]
    public void The_module_fields_and_options_are_Electrons_TOP_201_203_204_205()
    {
        var a = typeof(TopBarFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("topbar", a.Name);
        Assert.Equal("Top menu bar", a.Label);
        Assert.True(a.DefaultOn);
        Assert.True(a.Rest);
        Assert.Equal(new[] { "Win+Alt+T" }, a.Chords);
        var h = TopBarFeature.Options.Single(o => o.Key == "hidden");
        Assert.Equal(("Hide the top bar", "Win+Alt+T or the top edge of the screen brings it back", (object?)false), (h.Label, h.Help, h.Default));
        var g = TopBarFeature.Options.Single(o => o.Key == "overGames");
        Assert.Equal(("Keep the top bar over full-screen games", "Otherwise it steps aside while a full-screen game has the screen"), (g.Label, g.Help));
    }

    [Fact]
    public void A_test_run_shows_the_bar_and_never_asks_for_the_app_bar_TOP_222_242_253() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        Assert.Equal(0, Calls(r, "topbar.reserve"));
        Assert.Equal(0, Calls(r, "topbar.release"));
        Assert.True(r.Logged("dry run (--test): would have reserved 42 px (28 DIP at 1.5x)"), string.Join("\n", r.Lines));
        Assert.Contains(Vis(r), v => v.Show);
        Assert.True(f.Shown);
        Assert.True(r.Hub.Piece("topbar").Ok, r.Hub.Piece("topbar").Reason);
        Assert.Equal(1, Calls(r, "topbar.watch"));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.guard" && c.ArgsJson.Contains("[0,0,2304,42]"));
        // hidden and shown by hand: still never an app bar call
        r.Host.FireChord("Win+Alt+T");
        await Tick(r, 100);
        r.Host.FireChord("Win+Alt+T");
        await Tick(r, 100);
        Assert.Equal(0, Calls(r, "topbar.reserve") + Calls(r, "topbar.release"));
        Assert.True(r.Logged("hidden by hand: a --test instance holds no strip, so there was none to let go"));
        Assert.True(r.Logged("app bar shown by hand: a --test instance never reserves space on the screen"));
    });

    [Fact]
    public void A_live_run_reserves_the_strip_in_physical_px_checks_the_work_area_and_re_asks_TOP_241_243_247_248() => T(async (r, s) =>
    {
        await Up(r, s);
        var res = r.Platform.Calls.Where(c => c.Verb == "topbar.reserve").ToArray();
        Assert.NotEmpty(res);
        Assert.Contains("\"hwnd\":\"123\"", res[0].ArgsJson);
        Assert.Contains("\"size\":42", res[0].ArgsJson);
        Assert.True(r.Logged("app bar the window is ready: reserved 42px (28 DIP at 1.5x); the work area starts at the bar's bottom (28)"), string.Join("\n", r.Lines));
        Assert.True(r.Hub.Piece("topbar").Ok, r.Hub.Piece("topbar").Reason);
        int before = Calls(r, "topbar.reserve");
        await Tick(r, 6500);
        Assert.Equal(before + 2, Calls(r, "topbar.reserve"));   // the 1.5 s and 6 s re-checks
    }, test: false);

    [Fact]
    public void Live_a_work_area_that_ignores_the_strip_fails_health_and_registers_afresh_once_TOP_243_249() => T(async (r, s) =>
    {
        s.WorkY = 0;
        await Up(r, s);
        await Tick(r, 500);   // the fresh registration's own check
        Assert.True(r.Logged("reserved 42px, but the work area starts at 0 DIP instead of the bar's bottom 28 DIP"));
        Assert.True(r.Logged("the work area ignored the strip, so the bar is registered with Windows afresh (Explorer may have restarted)"));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.release");
        Assert.False(r.Hub.Piece("topbar").Ok);
        Assert.Contains("maximised windows can go under the top bar", r.Hub.Piece("topbar").Reason);
    }, test: false);

    [Fact]
    public void Live_hidden_by_hand_lets_the_strip_go_and_shown_takes_it_back_TOP_282_283_287_BAR_348() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        var a = await r.Bus.CallAsync("topbar.hidden", new JsonObject { ["on"] = true, ["via"] = "quick settings" });
        await Tick(r, 100);
        Assert.True(a.Ok);
        Assert.True(a.Result.GetProperty("hidden").GetBoolean());
        Assert.True(f.Hidden);
        Assert.True(r.Logged("hidden by hand (quick settings); Win+Alt+T or \"Show top bar\" in quick settings shows it again, also after a restart"));
        Assert.True(r.Logged("hidden by hand: let go of the top 28 DIP, so maximised windows get the whole screen"));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.release" && c.ArgsJson.Contains("\"123\""));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.guard" && c.ArgsJson.Contains("\"on\":false"));
        Assert.False(Vis(r)[^1].Show);
        Assert.True(r.Stores.Get("features\\topbar.json").TryGet("hidden", out var hv) && hv.GetBoolean());
        Assert.True(f.PeekWatching);
        int before = Calls(r, "topbar.reserve");
        await r.Bus.CallAsync("topbar.hidden", new JsonObject { ["on"] = false, ["via"] = "quick settings" });
        await Tick(r, 400);
        Assert.Equal(before + 1, Calls(r, "topbar.reserve"));
        Assert.True(r.Logged("back (quick settings): shown by hand"));
        Assert.True(Vis(r)[^1].Show);
        Assert.False(f.PeekWatching);
    }, test: false);

    [Fact]
    public void Hidden_is_kept_across_a_restart_no_strip_no_show_and_the_edge_is_watched_TOP_286() => T(async (r, s) =>
    {
        _ = r.Stores.Get("features\\topbar.json").SetAsync("hidden", JsonValue.Create(true));
        var f = await Up(r, s);
        Assert.True(f.Hidden);
        Assert.Equal(0, Calls(r, "topbar.reserve"));
        Assert.DoesNotContain(Vis(r), v => v.Show);
        Assert.True(f.PeekWatching);
        Assert.True(r.Logged("app bar the window is ready: not taken: the top bar is hidden by hand (Win+Alt+T shows it)"));
    }, test: false);

    [Fact]
    public void The_lock_screen_steps_aside_and_the_bar_comes_back_when_it_closes_TOP_261_262_266() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Platform.Raise("topbar", "{\"kind\":\"focus\",\"hwnd\":\"9\",\"exe\":\"C:\\\\Windows\\\\SystemApps\\\\LockApp.exe\"}");
        await Tick(r, 100);
        Assert.True(f.StepAside);
        Assert.Equal("lock", f.Kind);
        Assert.True(r.Logged("stepped aside (focus): LockApp.exe is in front (the lock screen): the bar never draws over it"));
        Assert.False(Vis(r)[^1].Show);
        r.Platform.Raise("topbar", "{\"kind\":\"focus\",\"hwnd\":\"10\",\"exe\":\"C:\\\\x\\\\notepad.exe\",\"desc\":\"Notepad\"}");
        await Tick(r, 100);
        Assert.False(f.StepAside);
        Assert.True(r.Logged("back (focus): the lock screen closed"));
        Assert.True(Vis(r)[^1].Show);
    });

    [Fact]
    public void A_full_screen_window_steps_aside_and_the_log_names_no_app_TOP_259_260() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Platform.Raise("topbar", "{\"kind\":\"focus\",\"hwnd\":\"11\",\"exe\":\"C:\\\\x\\\\chrome.exe\",\"desc\":\"Google Chrome\",\"fullscreen\":true}");
        await Tick(r, 100);
        Assert.Equal("full", f.Kind);
        Assert.DoesNotContain(r.Lines, l => l.Contains("Google Chrome"));
        var st = (System.Text.Json.JsonElement)r.Events.ReplayValue("topbar.state")!;
        Assert.Equal("Google Chrome is full screen; the top bar stepped aside until it leaves full screen", st.GetProperty("signal").GetString());
    });

    [Fact]
    public void Display_changes_skip_Mantles_own_work_area_move_and_burst_the_rest_TOP_233_238_239() => T(async (r, s) =>
    {
        await Up(r, s);
        var own = await r.Bus.CallAsync("topbar.display", new JsonObject { ["kind"] = "metrics", ["changed"] = new JsonArray("workArea") });
        Assert.False(own.Result.GetProperty("run").GetBoolean());
        await Tick(r, 2500);   // past SELF_MS and the 1.5 s re-check
        int before = Calls(r, "topbar.reserve");
        var sc = await r.Bus.CallAsync("topbar.display", new JsonObject { ["kind"] = "metrics", ["changed"] = new JsonArray("scaleFactor") });
        Assert.Equal("now", sc.Result.GetProperty("when").GetString());
        var sc2 = await r.Bus.CallAsync("topbar.display", new JsonObject { ["kind"] = "metrics", ["changed"] = new JsonArray("bounds") });
        Assert.Equal("later", sc2.Result.GetProperty("when").GetString());
        await Tick(r, 1500);
        Assert.Equal(before + 2, Calls(r, "topbar.reserve"));
        Assert.True(r.Logged("display changed (the scale changed): re-placed"));
        Assert.Contains(r.Events.Published, p => p.Name == "topbar.place" && ((TopBarPlace)p.Payload!).Why.StartsWith("display changed", StringComparison.Ordinal));
    }, test: false);

    [Fact]
    public void Peek_on_a_fake_cursor_shows_over_the_screen_and_hides_after_the_grace_TOP_292_298_300_BAR_349() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Host.FireChord("Win+Alt+T");
        await Tick(r, 100);
        await r.Bus.CallAsync("topbar.testPeek", new JsonObject { ["x"] = 700, ["y"] = 400 });
        await Tick(r, 200);
        await r.Bus.CallAsync("topbar.testPeek", new JsonObject { ["x"] = 700, ["y"] = 0 });
        await Tick(r, 700);
        Assert.True(f.PeekShown);
        Assert.True(r.Logged("peek shown: the cursor rested on the top edge for"));
        Assert.True(Vis(r)[^1].Show && Vis(r)[^1].Peek);
        Assert.True(f.Hidden);   // a peek never changes hidden
        await r.Bus.CallAsync("topbar.testPeek", new JsonObject { ["x"] = 700, ["y"] = 400 });
        await Tick(r, 800);
        Assert.False(f.PeekShown);
        Assert.True(r.Logged("peek hidden after"));
        Assert.False(Vis(r)[^1].Show);
    });

    [Fact]
    public void Show_during_a_peek_keeps_it_up_for_good_TOP_302() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Host.FireChord("Win+Alt+T");
        await Tick(r, 100);
        await r.Bus.CallAsync("topbar.testPeek", new JsonObject { ["x"] = 700, ["y"] = 400 });
        await Tick(r, 200);
        await r.Bus.CallAsync("topbar.testPeek", new JsonObject { ["x"] = 700, ["y"] = 0 });
        await Tick(r, 700);
        Assert.True(f.PeekShown);
        r.Host.FireChord("Win+Alt+T");
        await Tick(r, 100);
        Assert.True(r.Logged("shown by hand (Win+Alt+T) while it was peeking: it stays, now for good"));
        Assert.False(f.Hidden);
        Assert.True(Vis(r)[^1].Show);
        Assert.False(f.PeekWatching);
    });

    [Fact]
    public void Over_games_keeps_the_bar_over_a_game_verdict_TOP_205_265() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Platform.Raise("topbar", "{\"kind\":\"focus\",\"hwnd\":\"12\",\"exe\":\"C:\\\\g\\\\game.exe\",\"fullscreen\":true,\"game\":{\"stepAside\":true,\"signal\":\"a full-screen game\"}}");
        await Tick(r, 100);
        Assert.Equal("game", f.Kind);
        var a = await r.Bus.CallAsync("topbar.overGames", new JsonObject { ["on"] = true });
        await Tick(r, 100);
        Assert.False(a.Result.GetProperty("stepAside").GetBoolean());
        Assert.True(r.Logged("keep the top bar over full-screen games: true"));
        Assert.True(r.Stores.Get("features\\topbar.json").TryGet("overGames", out var ov) && ov.GetBoolean());
    });
}
