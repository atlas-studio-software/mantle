using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Features.Corners;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Corners;

/// <summary>
/// The live hot-corner feature (lib/features/corners.js) over the host test kit: settings, corners.state / corners.set,
/// the tray switch, rest, health, the fire-time scan, and the pointer watch that costs nothing at rest (CORN-008:
/// better than Electron's 100 ms poll). A --test rig: every action is a dry run and only the test pointer is read.
/// </summary>
public class CornersFeatureTests
{
    static readonly DipRect Screen = new(0, 0, 1536, 864);
    static readonly DipPoint Mid = new(700, 400), Tl = new(1, 1), Br = new(1534, 861);

    sealed class Kit
    {
        public Rig R = null!;
        public ManualTime T = null!;
        public CornersFeature F = null!;
        public readonly TestCornerPointer P = new() { Pt = Mid };
        public readonly CornerClaims Claims = new();
        public int Scans => R.Platform.Calls.Count(c => c.Verb == "gamemode.scan");
        public MemoryStore Store => R.Stores.Get("features\\corners.json");
        public void Ms(double ms) { for (double t = 0; t < ms; t += 50) T.AdvanceMs(Math.Min(50, ms - t)); }
    }

    static void T(Func<Kit, Task> body, Action<Kit>? before = null, bool scanOk = true) => HostLoop.Run(async (_, time) => {
        var k = new Kit { T = time, R = new Rig(time) };
        k.R.Platform.Start();
        k.R.Platform.Answer("gamemode.scan", scanOk ? "{\"fg\":{\"exe\":\"C:\\\\Windows\\\\notepad.exe\",\"title\":\"x\"},\"procs\":[]}" : "{\"ok\":false,\"reason\":\"the scan timed out\"}");
        k.R.Platform.Answer("hotcorners.desktop", "{\"ok\":true,\"dryRun\":true,\"wouldCall\":\"IShellDispatch4.ToggleDesktop\"}");
        before?.Invoke(k);
        var a = typeof(CornersFeature).GetCustomAttribute<FeatureAttribute>()!;
        k.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => k.F = new CornersFeature(k.Claims, k.P, new HostClock(time))) });
        await k.R.Host.Installed;
        try { await body(k); }
        finally { await k.R.Host.StopAllAsync("the test ended"); }
    });

    static async Task<JsonElement> Call(Kit k, string name, string args = "{}")
    {
        var r = await k.R.Bus.CallAsync(name, JsonNode.Parse(args) as JsonObject);
        Assert.True(r.Ok, name + ": " + r.Error);
        return r.Result;
    }

    static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool B(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    [Fact]
    public void The_module_is_Electrons_hot_corners_feature_with_its_two_commands_and_a_replayed_state() => T(async k =>
    {
        var a = typeof(CornersFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("corners", "Hot corners", true, true), (a.Name, a.Label, a.DefaultOn, a.Rest));
        Assert.Empty(a.Chords);
        Assert.True(k.R.Bus.Has("corners.state") && k.R.Bus.Has("corners.set"));
        Assert.True(k.R.Events.HasReplay("corners.state"));
        var st = await Call(k, "corners.state");
        foreach (var key in new[] { "enabled", "test", "actions", "corners", "names", "choices", "watching", "watch", "last", "fails", "zone", "dwell" })
            Assert.True(st.TryGetProperty(key, out _), "corners.state has no " + key);
        Assert.Equal("taskview", S(st.GetProperty("actions"), "tl"));   // the defaults: Task view top left, Show desktop bottom right
        Assert.Equal("desktop", S(st.GetProperty("actions"), "br"));
        Assert.Equal(6, st.GetProperty("choices").GetArrayLength());
        Assert.Equal("top-left", S(st.GetProperty("names"), "tl"));
        Assert.True(B(st, "test") && B(st, "watching"));
    });

    [Fact]
    public void Saved_choices_are_read_and_an_unreadable_file_runs_on_the_defaults_in_words()
    {
        T(async k => {
            var st = await Call(k, "corners.state");
            Assert.Equal("lock", S(st.GetProperty("actions"), "tr"));
            Assert.Equal("off", S(st.GetProperty("actions"), "tl"));
        }, before: k => k.Store.Put("actions", new JsonObject { ["tl"] = "off", ["tr"] = "lock", ["bl"] = "nonsense", ["br"] = "qs" }));
        T(async k => {
            Assert.True(k.R.Logged("the saved corners could not be read (the file is cut off); using the defaults"));
            Assert.Equal("taskview", S((await Call(k, "corners.state")).GetProperty("actions"), "tl"));
        }, before: k => { k.Store.State = ReadState.Corrupt; k.Store.Reason = "the file is cut off"; });
    }

    [Fact]
    public void Set_checks_its_words_saves_at_once_applies_live_and_a_failed_save_keeps_the_old_choice() => T(async k =>
    {
        var bad = await Call(k, "corners.set", "{\"corner\":\"middle\",\"action\":\"lock\"}");
        Assert.False(B(bad, "ok"));
        Assert.Equal("\"middle\" is not a corner (tl, tr, bl, br)", S(bad, "reason"));
        bad = await Call(k, "corners.set", "{\"corner\":\"tr\",\"action\":\"explode-the-pc-and-keep-going-for-a-while\"}");
        Assert.Equal("\"explode-the-pc-and-keep-going-for-a-whil\" is not a corner action (off, taskview, desktop, launcher, qs, lock)", S(bad, "reason"));

        int writes = k.Store.Writes;
        var same = await Call(k, "corners.set", "{\"corner\":\"tl\",\"action\":\"taskview\"}");
        Assert.True(B(same, "ok"));
        Assert.Equal(writes, k.Store.Writes);   // the same value: nothing written

        var ok = await Call(k, "corners.set", "{\"corner\":\"tr\",\"action\":\"lock\",\"via\":\"quick settings\"}");
        Assert.True(B(ok, "ok"));
        Assert.Equal("lock", S(ok.GetProperty("state").GetProperty("actions"), "tr"));
        Assert.True(k.Store.TryGet("actions", out var saved) && saved.GetProperty("tr").GetString() == "lock");
        Assert.True(k.R.Logged("the top-right corner is now Lock screen (quick settings): top-left Task view, top-right Lock screen, bottom-right Show desktop"));
        Assert.True(k.R.Logged("watching the corners (top-left Task view, top-right Lock screen, bottom-right Show desktop)"));   // applied live

        k.Store.FailWrites = "the disk is full";
        var fail = await Call(k, "corners.set", "{\"corner\":\"tr\",\"action\":\"qs\"}");
        Assert.False(B(fail, "ok"));
        Assert.Equal("it could not be saved: the disk is full", S(fail, "reason"));
        Assert.Equal("lock", k.F.Table.Tr);
    });

    [Fact]
    public void A_resting_pointer_costs_nothing_no_timer_no_look_and_moves_away_from_the_corners_never_wake_the_host() => T(async k =>
    {
        Assert.True(k.F.Watching);
        Assert.False(k.F.Polling);
        Assert.True(k.R.Logged("looked at only when the pointer moves near an active corner (no poll at rest)"));
        int looks = k.F.Looks, reads = k.P.ButtonReads;
        k.Ms(60_000);                                       // a minute at rest
        Assert.Equal(looks, k.F.Looks);
        Assert.False(k.F.Ticking);
        for (int x = 100; x < 1400; x += 7) k.P.Pt = new DipPoint(x, 300 + x % 200);   // a long sweep across the middle
        k.P.Pt = new DipPoint(1, 400);                      // along the left edge, away from its corners
        k.P.Pt = new DipPoint(1534, 1);                     // the top-right corner is Off by default
        Assert.Equal(looks, k.F.Looks);
        Assert.Equal(0, k.P.Wakes);
        Assert.Equal(reads, k.P.ButtonReads);               // the buttons are read only in an active corner
        Assert.Equal(0, k.Scans);
        await Task.CompletedTask;
    });

    [Fact]
    public void A_push_into_an_active_corner_looks_through_the_dwell_fires_once_after_one_scan_then_rests_again() => T(async k =>
    {
        k.P.Pt = Tl;
        Assert.Equal(1, k.P.Wakes);
        Assert.Equal("dwell", k.F.WatchState.Phase);
        k.Ms(400);
        await Task.Yield();
        Assert.Equal(1, k.Scans);                           // one gamemode.scan {procs:true} at the moment of firing
        Assert.Contains("\"procs\":true", k.R.Platform.Calls.Single(c => c.Verb == "gamemode.scan").ArgsJson);
        Assert.Equal("fired", k.F.WatchState.Phase);
        Assert.True(k.R.Logged("top-left corner -> Task view:"));
        int looks = k.F.Looks;
        k.Ms(10_000);                                       // still in the corner: once per arrival, and no timer
        Assert.Equal(looks, k.F.Looks);
        Assert.False(k.F.Ticking);
        k.P.Pt = Mid;                                       // leaving re-arms
        Assert.Equal("idle", k.F.WatchState.Phase);
        k.P.Pt = Tl; k.Ms(400); await Task.Yield();
        Assert.Equal(2, k.Scans);
    });

    [Fact]
    public void A_pointer_that_cannot_be_watched_falls_back_to_the_100_ms_poll_and_says_why() => T(async k =>
    {
        Assert.True(k.F.Polling);
        Assert.True(k.R.Logged("polled every 100 ms (the pointer watch could not start: the test pointer was told it cannot be watched)"));
        k.Ms(150); k.P.Pt = Br; k.Ms(400); await Task.Yield();
        Assert.True(k.R.Logged("bottom-right corner -> Show desktop:"));
    }, before: k => k.P.CanWatch = false);

    [Fact]
    public void A_refused_push_is_recorded_as_blocked_and_said_but_is_not_a_failure() => T(async k =>
    {
        k.P.Pt = Tl; k.Ms(400); await Task.Yield();
        Assert.True(k.R.Logged("top-left corner -> Task view: not run: the window in front could not be checked for a game or full screen (the scan timed out), so nothing fired"));
        var st = await Call(k, "corners.state");
        Assert.True(B(st.GetProperty("last"), "blocked"));
        Assert.StartsWith("the window in front could not be checked", S(st.GetProperty("last"), "said"));
        Assert.Empty(st.GetProperty("fails").EnumerateObject());
    }, scanOk: false);

    [Fact]
    public void A_failed_action_is_kept_per_corner_with_its_action_until_that_corner_changes() => T(async k =>
    {
        k.P.Pt = Tl; k.Ms(400); await Task.Yield();          // the launcher: this Mantle (a Core rig) has none
        var st = await Call(k, "corners.state");
        var f = st.GetProperty("fails").GetProperty("tl");
        Assert.Equal("launcher", S(f, "action"));
        Assert.False(string.IsNullOrEmpty(S(f, "said")));
        Assert.True(k.R.Logged("top-left corner -> Launcher: FAILED:"));
        await Call(k, "corners.set", "{\"corner\":\"tl\",\"action\":\"lock\"}");
        Assert.Empty((await Call(k, "corners.state")).GetProperty("fails").EnumerateObject());
    }, before: k => k.Store.Put("actions", new JsonObject { ["tl"] = "launcher", ["tr"] = "off", ["bl"] = "off", ["br"] = "off" }));

    [Fact]
    public void The_tray_switch_and_all_corners_off_stop_the_watch_and_release_the_claims_choices_kept() => T(async k =>
    {
        Assert.True(k.Claims.Active);
        k.R.Host.SetEnabled("corners", false, "the tray");
        Assert.False(k.F.Watching);
        Assert.False(k.P.WatchRunning);
        Assert.False(k.Claims.Active);
        Assert.True(k.R.Logged("stopped watching the corners: switched off in the tray"));
        Assert.Equal("taskview", k.F.Table.Tl);
        k.R.Host.SetEnabled("corners", true, "the tray");
        Assert.True(k.F.Watching && k.P.WatchRunning && k.Claims.Active);
        Assert.True(k.R.Logged("(switched on in the tray)"));
        await Call(k, "corners.set", "{\"corner\":\"tl\",\"action\":\"off\"}");
        await Call(k, "corners.set", "{\"corner\":\"br\",\"action\":\"off\"}");
        Assert.False(k.F.Watching || k.P.WatchRunning || k.Claims.Active);
        Assert.True(k.R.Logged("stopped watching the corners: every corner is off"));
        Assert.False(k.F.Ticking);
    });

    [Fact]
    public void An_unreadable_cursor_makes_the_feature_unhealthy_in_words_and_a_read_brings_it_back() => T(async k =>
    {
        k.P.Why = "Windows would not say where it is (GetCursorPos failed: the sign-in or lock screen has the input)";
        k.P.Pt = null;
        var piece = k.R.Hub.Piece("corners");
        Assert.False(piece.Ok);
        Assert.StartsWith("the corners cannot fire: the cursor could not be read (Windows would not say where it is", piece.Reason);
        Assert.False(B((await Call(k, "corners.state")).GetProperty("watch"), "ok"));
        Assert.Equal(0, k.Scans);                           // nothing fires behind the PIN box
        k.P.Pt = Mid;
        Assert.True(k.R.Hub.Piece("corners").Ok);
        Assert.True(k.R.Logged("the corner watch reads the cursor and the mouse buttons again"));
    });

    [Fact]
    public void Resting_for_a_game_stops_the_watch_and_waking_starts_it_disarmed() => T(async k =>
    {
        await k.R.Host.RestAllAsync("VALORANT runs");
        Assert.False(k.P.WatchRunning);
        Assert.False(k.Claims.Active);
        Assert.False(k.F.Ticking);
        k.P.Pt = Tl; k.Ms(1000); await Task.Yield();
        Assert.Equal(0, k.Scans);
        await k.R.Host.WakeAllAsync("the game closed");
        Assert.True(k.P.WatchRunning);
        k.Ms(1000); await Task.Yield();
        Assert.Equal(0, k.Scans);                           // woke with the cursor in the corner: it has to leave first
        k.P.Pt = Mid; k.P.Pt = Tl; k.Ms(400); await Task.Yield();
        Assert.Equal(1, k.Scans);
    });
}
