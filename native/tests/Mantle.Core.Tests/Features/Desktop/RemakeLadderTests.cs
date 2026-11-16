using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Desktop;
using Xunit;

namespace Mantle.Core.Tests.Features.Desktop;

/// <summary>
/// The desktop layer's way back under the icons (RemakeLadder, DESK-011/012) and the right-click hook's 30 s re-arm after
/// a stand-down (DeskMenuFeature, DESK-124), both on a hand-moved clock: nothing waits for real time, nothing touches
/// the shell (no TaskbarCreated is sent, Explorer is never restarted).
/// </summary>
public class RemakeLadderTests
{
    sealed class Tries
    {
        public readonly List<(int N, bool Make, long At)> Log = new();
        public Func<int, RemakeTry?> Answer = _ => new RemakeTry(false, "Progman would not take it");
    }

    static (RemakeLadder L, Tries T, List<string> Out) Make(ManualTime time)
    {
        var t = new Tries();
        var outcomes = new List<string>();
        var clock = new HostClock(time);
        var l = new RemakeLadder(clock, (n, make, when) => { t.Log.Add((n, make, clock.NowMs)); return Task.FromResult(t.Answer(n)); });
        l.Done += (n, when) => outcomes.Add($"done {n} {when}");
        l.GaveUp += (tries, why, when) => outcomes.Add($"gave up {tries} {why} {when}");
        return (l, t, outcomes);
    }

    [Fact]
    public void Every_try_failing_walks_the_back_off_1_5_3_5_8_12_20_s_and_gives_up_after_six_tries() => HostLoop.Run(async (_, time) =>
    {
        var (l, t, outs) = Make(time);
        l.Schedule(0, true, null);
        Assert.True(l.Pending);
        time.AdvanceMs(1499);
        Assert.Empty(t.Log);                                  // nothing before its back-off
        time.AdvanceMs(60_000);
        await Task.Yield();
        Assert.Equal(new long[] { 1500, 4500, 9500, 17500, 29500, 49500 }, t.Log.Select(x => x.At));
        Assert.Equal(new[] { true, false, false, false, false, false }, t.Log.Select(x => x.Make));   // made once, then only attached
        Assert.Equal(new[] { "gave up 6 Progman would not take it " }, outs);
        Assert.False(l.Pending);
        Assert.Equal(0, time.PendingTimers);                  // given up: nothing more is tried
        Assert.Equal("the desktop layer could not go back under the icons after Explorer restarted (6 tries: Progman would not take it), so Windows' own wallpaper shows; restart Mantle to bring it back",
            DesktopLayer.GaveUp(6, "Progman would not take it", null));
    });

    [Fact]
    public void A_try_that_works_ends_the_ladder_and_Explorer_coming_back_runs_the_waiting_try_at_once() => HostLoop.Run(async (_, time) =>
    {
        var (l, t, outs) = Make(time);
        t.Answer = n => new RemakeTry(n == 2);
        l.Schedule(0, true, null);
        time.AdvanceMs(1500 + 3000);                           // tries 0 and 1 fail
        await Task.Yield();
        Assert.Equal(2, t.Log.Count);
        Assert.True(l.Pending);
        Assert.True(l.TryNow());                               // TaskbarCreated: try 2 now, not 5 s on
        await Task.Yield();
        Assert.Equal(3, t.Log.Count);
        Assert.Equal(4500, t.Log[2].At);
        Assert.Equal(new[] { "done 2 " }, outs);
        Assert.False(l.TryNow());                              // nothing waits any more
        time.AdvanceMs(60_000);
        Assert.Equal(3, t.Log.Count);
    });

    [Fact]
    public void The_first_try_at_start_has_no_wait_a_try_that_did_not_run_stops_the_ladder_and_cancel_drops_a_waiting_one() => HostLoop.Run(async (_, time) =>
    {
        var (l, t, outs) = Make(time);
        t.Answer = n => n == 0 ? new RemakeTry(false, "not yet") : null;   // try 1: the layer was destroyed meanwhile
        await l.RunAsync(0, false, "at start");
        Assert.Equal(0, t.Log[0].At);
        Assert.True(l.Pending);
        time.AdvanceMs(3000);
        await Task.Yield();
        Assert.Equal(2, t.Log.Count);
        Assert.Empty(outs);
        Assert.False(l.Pending);                               // a try that did not run schedules nothing
        l.Schedule(0, true, null);
        l.Cancel();
        time.AdvanceMs(60_000);
        Assert.Equal(2, t.Log.Count);
        Assert.Equal(0, time.PendingTimers);
    });

    [Fact]
    public void A_stood_down_right_click_hook_says_why_and_arms_again_after_30_s_on_the_given_clock() => HostLoop.Run(async (_, time) =>
    {
        var r = new Rig(time, test: false);
        r.Platform.Start();
        r.Platform.Answer("deskmenu.arm", "{\"ok\":true}");
        var a = typeof(DeskMenuFeature).GetCustomAttribute<FeatureAttribute>()!;
        r.Host.Load(new[] { FeatureDescriptor.Of(a, () => new DeskMenuFeature(time)) });
        await r.Host.Installed;
        await Task.Yield();
        int Arms() => r.Platform.Calls.Count(c => c.Verb == "deskmenu.arm" && c.ArgsJson.Contains("\"on\":true"));
        Assert.Equal(1, Arms());
        r.Platform.Raise("deskmenu", "{\"op\":\"fallback\",\"why\":\"Mantle did not answer within 1000 ms\",\"replayed\":true,\"standDown\":true,\"standWhy\":\"two misses in a row\"}");
        await Task.Yield();
        Assert.False(r.Hub.Piece("deskmenu").Ok);
        time.AdvanceMs(29_999);
        await Task.Yield();
        Assert.Equal(1, Arms());                               // not before 30 s
        time.AdvanceMs(1);
        await Task.Yield();
        Assert.Equal(2, Arms());
        Assert.True(r.Logged("the desktop right-click hook is armed (trying again after standing down)"));
        Assert.True(r.Hub.Piece("deskmenu").Ok);
        await r.Host.StopAllAsync("the test ended");
    });
}
