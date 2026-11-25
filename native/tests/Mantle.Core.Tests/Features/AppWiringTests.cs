using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features.Clipboard;
using Mantle.Core.Features.Switcher;
using Mantle.Core.Features.Timers;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features;

/// <summary>
/// lane r11-app-wiring: the pieces the app's composition root (App.xaml.cs) hands the features, each tested here because
/// App.xaml.cs itself has no unit tests: WakeCalls (the resume hook's commands), TimerFeature.RestingFrom and the timer's
/// OnRest path, SwitcherFeature.HandBackVia, and ClipHost.PinsOrRunOnly.
/// </summary>
public sealed class AppWiringTests
{
    // ------------------------------------------------------------------ WakeCalls

    /// A command bus that records every call and answers as told (default: ok).
    sealed class Bus : ICommandBus
    {
        public readonly List<(string Name, string Caller)> Calls = new();
        public readonly Dictionary<string, Func<ValueTask<CommandResult>>> Do = new(StringComparer.Ordinal);
        public ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null, string caller = "host", CancellationToken cancel = default)
        {
            Calls.Add((name, caller));
            return Do.TryGetValue(name, out var f) ? f() : new ValueTask<CommandResult>(CommandResult.Success());
        }
        public IDisposable Register(CommandRegistration command) => throw new NotSupportedException();
        public bool Has(string name) => true;
        public IReadOnlyCollection<string> Names => Array.Empty<string>();
    }

    [Fact]
    public void Wake_resume_names_timer_wake_then_gcal_resume_and_unlock_names_nothing()
    {
        Assert.Equal(new[] { "timer.wake", "gcal.resume" }, WakeCalls.For(WakeCalls.Resume));
        Assert.Equal(TimerFeature.WakeCommand, WakeCalls.For(WakeCalls.Resume)[0]);
        Assert.Empty(WakeCalls.For(WakeCalls.Unlock));
        Assert.Empty(WakeCalls.For(null));
        Assert.Empty(WakeCalls.For("something else"));
        Assert.Equal("the PC woke from sleep", WakeCalls.Resume);
        Assert.Equal("the screen was unlocked", WakeCalls.Unlock);
    }

    [Fact]
    public async Task Wake_resume_calls_both_as_the_host_and_logs_nothing_when_both_answer_ok()
    {
        var bus = new Bus();
        var lines = new List<string>();
        Assert.Equal(2, await WakeCalls.RunAsync(bus, WakeCalls.Resume, lines.Add));
        Assert.Equal(new[] { ("timer.wake", "host"), ("gcal.resume", "host") }, bus.Calls);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Wake_unlock_calls_nothing_and_logs_nothing()
    {
        var bus = new Bus();
        var lines = new List<string>();
        Assert.Equal(0, await WakeCalls.RunAsync(bus, WakeCalls.Unlock, lines.Add));
        Assert.Empty(bus.Calls);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Wake_without_a_bus_is_one_log_line_naming_both_commands_never_a_throw()
    {
        var lines = new List<string>();
        Assert.Equal(0, await WakeCalls.RunAsync(null, WakeCalls.Resume, lines.Add));
        Assert.Equal(new[] { "mantle: the PC woke from sleep, but the command bus is not up yet, so timer.wake and gcal.resume could not be called" }, lines);
        lines.Clear();
        Assert.Equal(0, await WakeCalls.RunAsync(null, WakeCalls.Unlock, lines.Add));   // nothing to call, nothing to say
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Wake_a_failing_call_is_one_log_line_and_the_other_still_runs()
    {
        var bus = new Bus();
        bus.Do["gcal.resume"] = () => new ValueTask<CommandResult>(CommandResult.Fail("unknown command: gcal.resume"));
        var lines = new List<string>();
        Assert.Equal(1, await WakeCalls.RunAsync(bus, WakeCalls.Resume, lines.Add));
        Assert.Equal(2, bus.Calls.Count);
        Assert.Equal(new[] { "mantle: the PC woke from sleep, but gcal.resume did not run: unknown command: gcal.resume" }, lines);
    }

    [Fact]
    public async Task Wake_a_throwing_call_is_one_log_line_each_never_a_throw()
    {
        var bus = new Bus();
        bus.Do["timer.wake"] = () => throw new InvalidOperationException("the bus broke");                // throws at once
        bus.Do["gcal.resume"] = async () => { await Task.Yield(); throw new InvalidOperationException(" "); };   // throws later, no words
        var lines = new List<string>();
        Assert.Equal(0, await WakeCalls.RunAsync(bus, WakeCalls.Resume, lines.Add));
        Assert.Equal(new[] {
            "mantle: the PC woke from sleep, but timer.wake did not run: the bus broke",
            "mantle: the PC woke from sleep, but gcal.resume did not run: InvalidOperationException",
        }, lines);
    }

    [Fact]
    public async Task Wake_starts_every_call_at_once_so_a_slow_one_holds_up_none()
    {
        var bus = new Bus();
        var slow = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.Do["timer.wake"] = () => new ValueTask<CommandResult>(slow.Task);
        var lines = new List<string>();
        var run = WakeCalls.RunAsync(bus, WakeCalls.Resume, lines.Add);
        Assert.Equal(new[] { "timer.wake", "gcal.resume" }, bus.Calls.Select(c => c.Name));   // gcal.resume did not wait
        Assert.False(run.IsCompleted);
        slow.SetResult(CommandResult.Success());
        Assert.Equal(2, await run);
        Assert.Empty(lines);
    }

    // ------------------------------------------------------------------ TimerFeature.RestingFrom

    sealed class FakeRest : IRestHost
    {
        public string? RestingWhy { get; set; }
        public Task<RestAnswer> RestAsync(bool on, string why) => throw new NotSupportedException();
    }

    [Fact]
    public void RestingFrom_is_unknown_until_the_rest_host_exists_then_reads_it_at_each_ask()
    {
        FakeRest? rest = null;
        var resting = TimerFeature.RestingFrom(() => rest);
        Assert.Equal((false, (string?)null), resting());                  // not made yet: unknown (TIMER-059)
        rest = new FakeRest { RestingWhy = "Elden Ring runs" };            // RestCoordinator's words (GameModePlan: "<names> runs")
        Assert.Equal((true, (string?)"Elden Ring"), resting());           // known, resting for a game, in the game's own words
        rest.RestingWhy = null;
        Assert.Equal((true, (string?)null), resting());                   // known, not resting
        Assert.Equal((false, (string?)null), TimerFeature.RestingFrom(() => throw new InvalidOperationException("x"))());   // a throw = unknown
    }

    [Theory]
    [InlineData("VALORANT runs", "VALORANT")]
    [InlineData("VALORANT.exe, Steam runs", "VALORANT.exe, Steam")]
    [InlineData("resting while VALORANT.exe runs", "VALORANT.exe")]
    [InlineData("Elden Ring is running", "Elden Ring")]
    [InlineData("game mode is on (by hand)", "game mode")]
    [InlineData("game mode is on", "game mode")]
    [InlineData("Elden Ring", "Elden Ring")]
    [InlineData("   ", "a game")]
    [InlineData(null, null)]
    public void GameWords_turns_the_rests_reason_into_the_words_the_notice_says(string? why, string? game)
        => Assert.Equal(game, TimerFeature.GameWords(why));

    // ------------------------------------------------------------------ the timer's OnRest path (TIMER-057)

    [Fact]
    public void A_done_notice_held_for_a_game_shows_once_the_rest_ends() => HostLoop.Run(async (_, time) =>
    {
        var r = new Rig(time);
        var rest = new FakeRest { RestingWhy = "Elden Ring runs" };
        var shown = new List<TimerDoneNotice>();
        var h = new TimerFeature.Host(Clock: HostClock.Inline(r.Time), Beep: _ => { },
            ShowDone: n => { shown.Add(n); return new TimerNoticeResult(true, null); },
            Resting: TimerFeature.RestingFrom(() => rest));
        TimerFeature? f = null;
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Host.Load(new[] { FeatureDescriptor.Of(typeof(TimerFeature).GetCustomAttribute<FeatureAttribute>()!, () => f = new TimerFeature(h)) });
        await r.Host.Installed;
        await r.Host.RestAllAsync("Elden Ring runs");

        var res = await r.Bus.CallAsync("timer.start", new JsonObject { ["text"] = "1" });
        Assert.True(res.Ok, res.Error);
        r.Time.AdvanceMs(60_000);
        for (int i = 0; i < 200 && !f!.Engine.Held; i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
        Assert.True(f!.Engine.Held);
        Assert.Empty(shown);                                              // held while the game runs

        rest.RestingWhy = null;
        await r.Host.WakeAllAsync("the game ended");                       // the host tells the timer (OnRest), it reads Resting
        for (int i = 0; i < 200 && shown.Count == 0; i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
        Assert.Single(shown);
        Assert.False(f.Engine.Held);
        Assert.EndsWith(", while Elden Ring ran", shown[0].Sub, StringComparison.Ordinal);   // not "Elden Ring runs ran"
    });

    // ------------------------------------------------------------------ SwitcherFeature.HandBackVia

    [Fact]
    public async Task HandBackVia_sends_the_hotkey_ask_on_the_platform_it_was_given_with_the_4_s_timeout()
    {
        var p = new FakePlatform();
        p.Start();
        p.Answer("hotkey", "{\"ok\":true}");
        var reply = await SwitcherFeature.HandBackVia(p)(new JsonObject { ["altTab"] = false, ["winKeys"] = false });
        Assert.True(reply.Ok, reply.Error);
        var call = Assert.Single(p.Calls);
        Assert.Equal("hotkey", call.Verb);
        Assert.Equal("{\"altTab\":false,\"winKeys\":false}", call.ArgsJson);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), call.Timeout);
        Assert.Equal(4000, SwitcherHook.AskTimeoutMs);
    }

    [Fact]
    public async Task HandBackVia_never_throws_a_throw_comes_back_as_a_failed_reply_in_words()
    {
        var p = new FakePlatform();
        p.Start();
        p.Handle("hotkey", _ => throw new InvalidOperationException("the pipe broke"));
        var reply = await SwitcherFeature.HandBackVia(p)(new JsonObject { ["altTab"] = false });
        Assert.False(reply.Ok);
        Assert.Equal(PlatformOutcome.Failed, reply.Outcome);
        Assert.Equal("hotkey threw: the pipe broke", reply.Error);

        var down = new FakePlatform();                                     // not started: refused in words, no throw
        var no = await SwitcherFeature.HandBackVia(down)(new JsonObject { ["altTab"] = false });
        Assert.False(no.Ok);
        Assert.Equal(PlatformOutcome.Refused, no.Outcome);
        Assert.Throws<ArgumentNullException>(() => SwitcherFeature.HandBackVia(null!));   // a wiring mistake shows at start
    }

    // ------------------------------------------------------------------ ClipHost.PinsOrRunOnly

    [Fact]
    public void Pins_when_the_gate_check_throws_are_kept_for_this_run_only_with_the_reason_in_words()
    {
        using var d = new TestDir("app-wiring-pins");
        var args = LaunchArgs.Parse(new[] { "--live" });
        var paths = DataPaths.Resolve(args, k => k == "APPDATA" ? d.Path : null, Path.GetTempPath());
        Assert.True(paths.IsUserProfile);
        // the "is Electron running?" probe throws, so the gate's check on the user's own pins file throws too
        var gate = WriteGate.For(paths, args, () => throw new InvalidOperationException("the process list could not be read"));
        Assert.Throws<InvalidOperationException>(() => ClipHost.PinsFor(paths.DataRoot, gate));
        var pins = ClipHost.PinsOrRunOnly(paths.DataRoot, gate);
        Assert.Null(pins.PinsFile);
        Assert.Equal("pins are kept for this run only: the pins file's place could not be checked (the process list could not be read)", pins.Why);
        Assert.False(ClipHost.StartRequest(pins, test: false).ContainsKey("pinsFile"));   // the sidecar gets no file

        // and the same helper with a gate that answers: the file in a live run that is the only shell
        var ok = ClipHost.PinsOrRunOnly(paths.DataRoot, WriteGate.For(paths, args, () => null));
        Assert.Equal(Path.Combine(paths.DataRoot, ClipHost.PinsRelativePath), ok.PinsFile);
    }
}
