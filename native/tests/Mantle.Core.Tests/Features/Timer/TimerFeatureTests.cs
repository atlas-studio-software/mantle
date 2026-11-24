using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Timers;
using Xunit;

namespace Mantle.Core.Tests.Features.Timers;

// lane r10-timer: the [Feature("timer")] service (lib/features/timer.js) on the feature host's test rig: a manual clock,
// in-memory stores (no file is read or written), a fake platform. TIMER-001, TIMER-041, TIMER-043, TIMER-049, TIMER-051, TIMER-076.
public class TimerFeatureTests
{
    static void T(Func<Rig, Task> body, bool test = true) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D(Rig r, TimerFeature.Host? h = null) =>
        FeatureDescriptor.Of(typeof(TimerFeature).GetCustomAttribute<FeatureAttribute>()!,
            () => new TimerFeature(h ?? new TimerFeature.Host(Clock: HostClock.Inline(r.Time))));

    static async Task Up(Rig r, TimerFeature.Host? h = null)
    {
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Host.Load(new[] { D(r, h) });
        await r.Host.Installed;
    }

    static async Task Settle(Rig r, Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    static JsonElement LastState(Rig r) => (JsonElement)r.Events.Published.Last(p => p.Name == TimerFeature.StateEvent).Payload!;

    [Fact]
    public void The_feature_is_timer_on_by_default_with_no_chords_and_the_tray_label()
    {
        var a = typeof(TimerFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal("timer", a.Name);
        Assert.Equal("Timer and stopwatch (top bar)", a.Label);
        Assert.True(a.DefaultOn);
        Assert.Empty(a.Chords);
        Assert.False(a.Rest);   // never rests: a timer still ends during a game (TIMER-055)
    }

    [Fact]
    public void A_started_timer_ends_on_the_host_clock_saves_idle_and_with_no_pop_up_wired_says_so_and_beeps() => T(async r =>
    {
        await Up(r);
        var res = await r.Bus.CallAsync("timer.start", new JsonObject { ["text"] = "1" });
        Assert.True(res.Ok, res.Error);
        Assert.Equal("timer", res.Result.GetProperty("kind").GetString());
        Assert.True(res.Result.GetProperty("ok").GetBoolean());
        Assert.Equal("timer", LastState(r).GetProperty("kind").GetString());

        r.Time.AdvanceMs(60_000);
        await Settle(r, () => LastState(r).GetProperty("kind").GetString() == "idle");
        JsonElement st = LastState(r);
        Assert.Equal("idle", st.GetProperty("kind").GetString());
        Assert.Equal("failed", st.GetProperty("done").GetProperty("popup").GetString());
        Assert.Equal(TimerFeature.NoSurface, st.GetProperty("done").GetProperty("reason").GetString());
        Assert.Contains(r.Events.Published, p => p.Name == TimerFeature.DoneEvent);
        Assert.Equal("idle", r.Stores.Get("features\\timer.json").Snapshot().GetProperty("clock").GetProperty("kind").GetString());
        Assert.True(r.Logged("would beep (muted in --test)"));
    });

    [Fact]
    public void A_refused_command_comes_back_failed_in_words() => T(async r =>
    {
        await Up(r);
        var res = await r.Bus.CallAsync("timer.pause", new JsonObject());
        Assert.False(res.Ok);
        Assert.Equal("No timer or stopwatch is running", res.Error);
        var bad = await r.Bus.CallAsync("timer.start", new JsonObject { ["text"] = "soon" });
        Assert.Equal(TimerCore.Junk, bad.Error);
    });

    [Fact]
    public void A_save_the_store_refuses_keeps_the_timer_and_marks_health_in_words() => T(async r =>
    {
        r.Stores.Get("features\\timer.json").FailWrites = "the profile folder is read-only";
        await Up(r);
        var res = await r.Bus.CallAsync("timer.stopwatch", new JsonObject());
        Assert.True(res.Ok, res.Error);
        Assert.Equal("stopwatch", res.Result.GetProperty("kind").GetString());
        Assert.False(res.Result.GetProperty("persist").GetProperty("ok").GetBoolean());
        Assert.Equal("the profile folder is read-only", res.Result.GetProperty("persist").GetProperty("reason").GetString());
        Assert.True(r.Logged("could not save the timer (the profile folder is read-only)"));
    });

    [Fact]
    public void A_saved_timer_that_ended_while_Mantle_was_off_fires_once_at_start() => T(async r =>
    {
        long now = r.Time.GetUtcNow().ToUnixTimeMilliseconds();
        r.Stores.Get("features\\timer.json").Put("clock", JsonNode.Parse("{\"kind\":\"timer\",\"endAt\":" + (now - 60_000) + ",\"totalMs\":300000,\"label\":\"5 min\",\"startedAt\":1}"));
        await Up(r);
        await Settle(r, () => r.Events.Published.Any(p => p.Name == TimerFeature.DoneEvent));
        var done = (JsonElement)r.Events.Published.Last(p => p.Name == TimerFeature.DoneEvent).Payload!;
        Assert.EndsWith(", while Mantle was off", done.GetProperty("sub").GetString(), StringComparison.Ordinal);
        Assert.Equal("idle", r.Stores.Get("features\\timer.json").Snapshot().GetProperty("clock").GetProperty("kind").GetString());
        Assert.Equal(1, r.Events.Published.Count(p => p.Name == TimerFeature.DoneEvent));
    });
}
