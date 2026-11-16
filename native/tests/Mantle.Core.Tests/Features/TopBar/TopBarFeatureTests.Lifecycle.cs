using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.TopBar;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// Part 9: known games RUNNING (TOP-267..272), the strip races (TOP-251/252), Explorer restarted (TOP-250), session end
/// (TOP-258), the health order (TOP-380), a new sidecar (TOP-381), the stop order (TOP-382) and the window fight's
/// rules (TOP-227..231). The game check reads the fake sidecar's gamemode.scan (nothing drives the watch here, so the
/// follower reads every second on its own, as Electron's does with no game mode).
/// </summary>
public sealed partial class TopBarFeatureTests
{
    static PlatformReply Json(string s) => PlatformReply.Success(JsonDocument.Parse(s).RootElement.Clone());
    const string Valorant = "{\"procs\":[{\"pid\":4242,\"file\":\"VALORANT-Win64-Shipping.exe\"}]}";
    const string TwoGames = "{\"procs\":[{\"pid\":4242,\"file\":\"VALORANT-Win64-Shipping.exe\"},{\"pid\":77,\"file\":\"cs2.exe\"}]}";

    [Fact]
    public void A_known_game_running_steps_aside_with_the_strip_let_go_and_comes_back_when_it_exits_TOP_267_268_269_270_271() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        int reserves = Calls(r, "topbar.reserve"), releases = Calls(r, "topbar.release");
        string scan = Valorant;
        r.Platform.Handle("gamemode.scan", _ => Json(scan));
        await Tick(r, 1100);   // the follower's own read (every RunMs while nothing drives the watch)
        Assert.NotNull(f.Run);
        Assert.True(f.StepAside);
        Assert.Equal("run", f.Kind);
        Assert.True(r.Logged("stepped aside (game started): a known game running: the bar stays aside with its strip let go until it exits"));
        Assert.True(r.Logged("a known game running; the top bar let go of the top 28 DIP and stepped aside until it exits"));
        Assert.False(r.Lines.Any(l => l.Contains("VALORANT", StringComparison.Ordinal)), "the log names the game");
        Assert.Equal(releases + 1, Calls(r, "topbar.release"));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.guard" && c.ArgsJson.Contains("\"on\":false"));
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.stats" && c.ArgsJson.Contains("\"on\":false"));
        Assert.False(Vis(r)[^1].Show);
        Assert.Contains(r.Events.Published, p => p.Name == "topbar.close");
        var st = await r.Bus.CallAsync("topbar.state");
        Assert.StartsWith("VALORANT-Win64-Shipping.exe is running (a known game): the bar stays aside with its strip let go until it exits", st.Result.GetProperty("signal").GetString());
        // TOP-268: overGames does not apply, a second game and a display change change nothing, nothing re-reserves
        await r.Bus.CallAsync("topbar.overGames", new JsonObject { ["on"] = true });
        scan = TwoGames;
        await Tick(r, 1100);
        await r.Bus.CallAsync("topbar.display", new JsonObject { ["kind"] = "metrics", ["changed"] = new JsonArray("bounds") });
        await Tick(r, 1100);
        Assert.True(f.StepAside);
        Assert.Equal(reserves, Calls(r, "topbar.reserve"));
        Assert.Equal(releases + 1, Calls(r, "topbar.release"));
        Assert.Equal(2, f.Run!.All.Count);
        // TOP-271: a failing check says so in health and never takes the strip back mid-game
        scan = "{\"procs\":[],\"procsReason\":\"the process list is still being read\"}";
        await Tick(r, 1100);
        Assert.True(r.Logged("cannot check for games starting: the process list is still being read; stays stepped aside for 2 known games until the check works again"));
        Assert.True(f.StepAside);
        Assert.False(r.Hub.Piece("topbar").Ok);
        Assert.Equal("a game starting is not noticed, so the top bar can sit over it until it goes full screen: the process list is still being read", r.Hub.Piece("topbar").Reason);
        // TOP-269: the last one exits: the strip is taken back once and the bar comes back
        scan = "{\"procs\":[]}";
        await Tick(r, 1100);
        Assert.True(r.Logged("checking for games starting again"));
        Assert.Null(f.Run);
        Assert.False(f.StepAside);
        Assert.Contains(r.Lines, l => l.Contains("2 known games exited after ", StringComparison.Ordinal) && l.Contains(" s; the top bar takes back the top 28 DIP and comes back", StringComparison.Ordinal));
        Assert.True(r.Logged("back (game exited): the game exited"));
        Assert.Equal(reserves + 1, Calls(r, "topbar.reserve"));
        Assert.True(Vis(r)[^1].Show);
        await Tick(r, 400);
        Assert.True(r.Hub.Piece("topbar").Ok);
        // TOP-270: at rest the follower's own read stays on its RunMs pace only while nothing drives; game mode drives in the app
        Assert.Equal(1000, TopBarFeature.RunMs);
    }, test: false);

    [Fact]
    public void A_game_driven_hub_feeds_the_bar_and_a_hidden_bar_keeps_its_peek_off_while_a_game_runs_TOP_267_270() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Host.FireChord("Win+Alt+T");
        await Tick(r, 100);
        Assert.True(f.PeekWatching);
        int own = Calls(r, "gamemode.scan");
        using var drive = r.Host.GameWatch.Drive(null);   // game mode reading: the bar no longer reads on its own
        r.Host.GameWatch.Publish((JsonObject)JsonNode.Parse("{\"ok\":true,\"procs\":[{\"pid\":9,\"file\":\"cs2.exe\"}]}")!);
        await Tick(r, 200);
        Assert.NotNull(f.Run);
        Assert.False(f.PeekWatching);
        Assert.True(r.Logged("peek: stopped watching the top edge: a known game started"));
        Assert.True(r.Logged("a known game running; the top bar stepped aside until it exits (a --test instance holds no strip, so there was none to let go)"));
        Assert.Equal(1000, r.Host.GameWatch.FastMs());   // a game runs: the reader goes to every second
        await Tick(r, 3000);
        Assert.Equal(own, Calls(r, "gamemode.scan"));
        r.Host.GameWatch.Publish((JsonObject)JsonNode.Parse("{\"ok\":true,\"procs\":[]}")!);
        await Tick(r, 200);
        Assert.Null(f.Run);
        Assert.True(f.PeekWatching);   // still hidden by hand: the edge is watched again
        Assert.True(f.Hidden);
        Assert.True(r.Logged("app bar game exited: a --test instance never reserves space on the screen, so there was nothing to take back"));
        Assert.Null(r.Host.GameWatch.FastMs());
    });

    [Fact]
    public void Mantle_restarted_mid_game_checks_for_games_before_the_reserve_so_the_strip_is_never_taken_TOP_272_251() => T(async (r, s) =>
    {
        var f = await Up(r, s, before: x => x.Platform.Handle("gamemode.scan", _ => Json(Valorant)));
        await Tick(r, 7000);   // past the 1.5 s and 6 s re-checks
        Assert.NotNull(f.Run);
        Assert.Equal(0, Calls(r, "topbar.reserve"));
        Assert.True(r.Logged("app bar sidecar ready: not taken back: a known game is running; the strip comes back when it exits"));
        Assert.True(r.Logged("app bar 6 s re-check: not taken back: a known game is running; the strip comes back when it exits"));
        Assert.False(Vis(r).Any(v => v.Show));
    }, test: false);

    [Fact]
    public void A_game_starting_while_a_reserve_is_on_its_way_lets_the_strip_go_again_at_once_TOP_252_250() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        bool fire = true;
        r.Platform.Handle("topbar.reserve", _ =>
        {
            if (fire) { fire = false; _ = f.SeenForTest((JsonObject)JsonNode.Parse("{\"ok\":true,\"procs\":[{\"pid\":9,\"file\":\"cs2.exe\"}]}")!); }
            return Json("{\"ok\":true}");
        });
        int releases = Calls(r, "topbar.release");
        var a = await r.Bus.CallAsync("topbar.shell", new JsonObject { ["why"] = "Explorer restarted" });   // TOP-250: Explorer came back
        await Tick(r, 400);
        Assert.True(a.Ok);
        Assert.True(r.Logged("app bar Explorer restarted: a known game started meanwhile, so the strip was let go again"));
        Assert.Equal(releases + 2, Calls(r, "topbar.release"));   // the game's own, then the reserve's
        Assert.NotNull(f.Run);
    }, test: false);

    [Fact]
    public void Explorer_restarted_asks_for_the_strip_again_and_a_test_run_only_logs_the_dry_run_TOP_250() => T(async (r, s) =>
    {
        await Up(r, s);
        var a = await r.Bus.CallAsync("topbar.shell", new JsonObject { ["why"] = "Explorer restarted" });
        await Tick(r, 100);
        Assert.True(a.Ok);
        Assert.True(r.Logged("app bar Explorer restarted: dry run (--test): would have reserved 42 px"));
        Assert.Equal(0, Calls(r, "topbar.reserve"));
    });

    [Fact]
    public void Live_Explorer_restarted_reserves_again_and_checks_the_work_area_TOP_250() => T(async (r, s) =>
    {
        await Up(r, s);
        await Tick(r, 7000);
        int reserves = Calls(r, "topbar.reserve");
        await r.Bus.CallAsync("topbar.shell", new JsonObject { ["why"] = "Explorer restarted" });
        await Tick(r, 400);
        Assert.Equal(reserves + 1, Calls(r, "topbar.reserve"));
        Assert.True(r.Logged("app bar Explorer restarted: reserved 42px"));
    }, test: false);

    [Fact]
    public void Session_end_lets_the_strip_go_at_once_and_nothing_takes_it_again_TOP_258() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        int reserves = Calls(r, "topbar.reserve"), releases = Calls(r, "topbar.release");
        await f.SessionEnd();
        Assert.True(f.Ending);
        Assert.True(r.Logged("Windows is ending the session: the top bar let go of its strip now, so the next start finds none left over"));
        Assert.Equal(releases + 1, Calls(r, "topbar.release"));
        await r.Bus.CallAsync("topbar.display", new JsonObject { ["kind"] = "metrics", ["changed"] = new JsonArray("bounds") });
        await r.Bus.CallAsync("topbar.shell", new JsonObject { ["why"] = "Explorer restarted" });
        await Tick(r, 7000);   // the pending 1.5 s / 6 s re-checks
        Assert.Equal(reserves, Calls(r, "topbar.reserve"));
        int scans = Calls(r, "gamemode.scan");
        await Tick(r, 3000);
        Assert.Equal(scans, Calls(r, "gamemode.scan"));   // the game loop stopped
        await f.SessionEnd();   // once
        Assert.Equal(releases + 1, Calls(r, "topbar.release"));
    }, test: false);

    [Fact]
    public void Health_names_the_window_fight_first_then_the_game_watch_and_drops_the_fight_while_hidden_TOP_380_230() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        r.Platform.Handle("gamemode.scan", _ => PlatformReply.Fail(PlatformOutcome.Failed, "the sidecar is busy"));
        await Tick(r, 1100);
        Assert.Equal("a game starting is not noticed, so the top bar can sit over it until it goes full screen: the sidecar is busy", r.Hub.Piece("topbar").Reason);
        await r.Bus.CallAsync("topbar.drift", new JsonObject { ["stuck"] = true, ["reason"] = "something keeps moving or resizing the top bar's window" });
        Assert.Equal("something keeps moving or resizing the top bar's window", r.Hub.Piece("topbar").Reason);
        await r.Bus.CallAsync("topbar.shown", new JsonObject { ["on"] = false });   // hidden: a fight pauses
        Assert.Equal("a game starting is not noticed, so the top bar can sit over it until it goes full screen: the sidecar is busy", r.Hub.Piece("topbar").Reason);
        await r.Bus.CallAsync("topbar.shown", new JsonObject { ["on"] = true });
        Assert.Equal("something keeps moving or resizing the top bar's window", r.Hub.Piece("topbar").Reason);
        await r.Bus.CallAsync("topbar.drift", new JsonObject { ["stuck"] = false });
        Assert.StartsWith("a game starting is not noticed", r.Hub.Piece("topbar").Reason);
        Assert.Null(f.DriftWhy);
    });

    [Fact]
    public void A_new_sidecar_reruns_the_whole_start_TOP_381() => T(async (r, s) =>
    {
        await Up(r, s);
        int watches = r.Platform.Calls.Count(c => c.Verb == "topbar.watch" && c.ArgsJson.Contains("\"on\":true"));
        int pings = Calls(r, "ping"), scans = Calls(r, "gamemode.scan"), guards = r.Platform.Calls.Count(c => c.Verb == "topbar.guard" && c.ArgsJson.Contains("\"on\":true"));
        int dry = r.Lines.Count(l => l.Contains("app bar sidecar ready: dry run (--test)", StringComparison.Ordinal));
        r.Platform.SetStatus(PlatformState.Starting, "restarting");
        r.Platform.Retry();   // a new sidecar is ready
        await Tick(r, 400);
        Assert.Equal(pings + 1, Calls(r, "ping"));
        Assert.Equal(watches + 1, r.Platform.Calls.Count(c => c.Verb == "topbar.watch" && c.ArgsJson.Contains("\"on\":true")));
        Assert.True(Calls(r, "gamemode.scan") > scans);   // the game check before the reserve, again
        Assert.Equal(guards + 1, r.Platform.Calls.Count(c => c.Verb == "topbar.guard" && c.ArgsJson.Contains("\"on\":true")));
        Assert.Equal(dry + 1, r.Lines.Count(l => l.Contains("app bar sidecar ready: dry run (--test)", StringComparison.Ordinal)));
        Assert.True(Vis(r)[^1].Show);
    });

    [Fact]
    public void Stop_ends_the_loops_then_turns_the_watch_guard_and_stats_off_and_lets_the_strip_go_TOP_382() => T(async (r, s) =>
    {
        var f = await Up(r, s);
        int before = r.Platform.Calls.Count;
        await f.StopAsync(new FeatureStop("Mantle is putting Windows back"));
        var after = r.Platform.Calls.Skip(before).Select(c => c.Verb + (c.ArgsJson.Contains("\"on\":false") ? " off" : "")).ToArray();
        Assert.Equal(new[] { "topbar.watch off", "topbar.guard off", "topbar.stats off", "topbar.release" }, after);
        Assert.False(Vis(r)[^1].Show);
        Assert.Equal("Mantle is putting Windows back", Vis(r)[^1].Why);
        int scans = Calls(r, "gamemode.scan");
        await Tick(r, 3000);
        Assert.Equal(scans, Calls(r, "gamemode.scan"));   // the game loop ended
        Assert.False(f.PeekWatching);
    }, test: false);

    // ------------------------------------------------------------------ the window fight's rules (pure)
    [Fact]
    public void Drift_three_free_put_backs_then_a_backoff_that_never_gives_up_and_one_line_when_it_stays_TOP_227_228_229_230_231()
    {
        Assert.Null(TopBarDrift.Off(0, 0, 2304, 57, 0, 0, 2304, 1.5));   // the height is not compared
        var off = TopBarDrift.Off(0, 42, 2304, 42, 0, 0, 2304, 1.5);
        Assert.Equal("0,42 2304x42 px on a 2304 px wide display at 150 %", off);
        DriftState? d = null;
        for (int i = 1; i <= 3; i++)
        {
            var st = TopBarDrift.Step(d, off, "0,0 2304 wide", 1000 + i, false);
            Assert.True(st.Fix); Assert.Equal(0, st.RetryMs);
            Assert.Equal($"the window drifted to {off}; put back to 0,0 2304 wide", st.Log);
            d = st.D;
        }
        var s4 = TopBarDrift.Step(d, off, "0,0 2304 wide", 1004, false);
        Assert.True(s4.Fix); Assert.Equal(1000, s4.RetryMs); Assert.True(s4.D!.Stuck);
        Assert.Equal($"the window keeps leaving its place ({off}) after 3 put-backs in 10 s; put back to 0,0 2304 wide again, then checked after 1, 2, 4, 8, 16, 30 s until it stays", s4.Log);
        d = s4.D;
        // a move in between waits for the queued re-check
        var mid = TopBarDrift.Step(d, "0,9 2304x42 px", "0,0 2304 wide", 1100, false);
        Assert.False(mid.Fix); Assert.Equal(0, mid.RetryMs); Assert.Null(mid.Log); Assert.Equal("0,9 2304x42 px", mid.D!.Last);
        int[] want = { 2000, 4000, 8000, 16000, 30000, 30000, 30000 };
        long now = 2004;
        foreach (var ms in want)
        {
            var st = TopBarDrift.Step(d, off, "0,0 2304 wide", now, true);
            Assert.True(st.Fix); Assert.Equal(ms, st.RetryMs);
            if (ms != d!.RetryMs) Assert.Equal($"the window was out of place again ({off}); put back to 0,0 2304 wide (put-back {d.Fixes + 1}), next check in {ms / 1000} s", st.Log);
            else Assert.Null(st.Log);   // the 30 s cap: health carries it, no line every 30 s
            d = st.D; now += ms;
        }
        Assert.Equal(11, d!.Fixes);
        var why = TopBarDrift.Reason(d, true, _ => "10:20:30");
        Assert.Equal($"something keeps moving or resizing the top bar's window (last seen at {off}); Mantle put it back 11 times since 10:20:30 and tries again every 30 s until it stays", why);
        Assert.Null(TopBarDrift.Reason(d, false, _ => "x"));   // hidden: no health state
        var done = TopBarDrift.Step(d, null, "0,0 2304 wide", now, true);
        Assert.True(done.Settled); Assert.Null(done.D); Assert.False(done.Fix);
        Assert.Equal($"the window is back in place and stayed there, after 11 put-backs in {Math.Round((now - 1001) / 1000.0, MidpointRounding.AwayFromZero)} s", done.Log);
        // the count resets after 10 s
        var a = TopBarDrift.Step(null, off, "t", 0, false).D;
        a = TopBarDrift.Step(a, off, "t", 1, false).D;
        a = TopBarDrift.Step(a, off, "t", 2, false).D;
        var late = TopBarDrift.Step(a, off, "t", 10_003, false);
        Assert.Equal(0, late.RetryMs); Assert.Equal(1, late.D!.N);
    }
}
