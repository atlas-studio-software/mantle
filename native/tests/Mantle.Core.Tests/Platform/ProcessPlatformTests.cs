using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Platform;

// lib/sidecar.test.js as the spec, against a fake process and a virtual clock.
public class ProcessPlatformTests
{
    readonly ManualScheduler _host = new();
    readonly FakeLauncher _launch = new();
    readonly List<string> _logs = new();
    readonly List<PlatformStatus> _statuses = new();

    ProcessPlatform Make(Action<SidecarOptions>? tune = null)
    {
        var o = new SidecarOptions {
            ExePath = typeof(ProcessPlatformTests).Assembly.Location, Passive = true, StateFile = @"X:\p\shell-state.json",
            Log = (lvl, m) => _logs.Add(lvl + ": " + m),
        };
        tune?.Invoke(o);
        var p = new ProcessPlatform(o, _launch, _host);
        p.OnStatus(s => _statuses.Add(s));
        return p;
    }

    ProcessPlatform Ready(Action<SidecarOptions>? tune = null)
    {
        var p = Make(tune);
        p.Start();
        _launch.Last.Ready();
        _host.Advance();
        Assert.True(p.Status.Ready, p.Status.Reason);
        return p;
    }

    [Fact]
    public void SC013_SC046_a_call_while_starting_waits_for_the_ready_line_and_goes_with_the_time_left()
    {
        var p = Make();
        p.Start();
        Assert.Equal(PlatformState.Starting, p.Status.State);
        Assert.Contains("--passive", _launch.Args[0]);
        Assert.Contains("--state-file=" + @"X:\p\shell-state.json", _launch.Args[0]);
        var t = p.CallAsync("windows", null, TimeSpan.FromMilliseconds(5000));
        _host.Advance(1000);
        Assert.Empty(_launch.Last.Written);
        _launch.Last.Ready();
        _host.Advance();
        var line = JsonNode.Parse(_launch.Last.Written.Single())!.AsObject();
        Assert.Equal("windows", (string?)line["cmd"]);
        Assert.Equal(4000, (long)line["ttl"]!);
        _launch.Last.Say($"{{\"id\":{(long)line["id"]!},\"ok\":true,\"result\":[1,2]}}");
        Assert.True(t.IsCompleted);
        Assert.True(t.Result.Ok);
        Assert.Equal("[1,2]", t.Result.Result.GetRawText());
    }

    [Fact]
    public void SC046_a_call_with_no_process_is_refused_at_once_with_the_reason()
    {
        var p = Make(o => o.ExePath = @"X:\nowhere\Mantle.Sidecar.exe");
        p.Start();
        Assert.Equal(PlatformState.Down, p.Status.State);
        Assert.Contains("no exe at", p.Status.Reason);
        var r = p.CallAsync("ping").Result;
        Assert.Equal(PlatformOutcome.Refused, r.Outcome);
        Assert.StartsWith("cannot run \"ping\": sidecar down: no exe at", r.Error);
    }

    [Fact]
    public void SC046_a_short_caller_times_out_waiting_for_ready_and_a_long_one_hears_it_may_be_hung()
    {
        var p = Make(o => o.ReadyWaitMs = 8000);
        p.Start();
        var shortCall = p.CallAsync("a", null, TimeSpan.FromMilliseconds(2000));
        var longCall = p.CallAsync("b", null, TimeSpan.FromMilliseconds(20000));
        _host.Advance(2000);
        Assert.Equal(PlatformOutcome.TimedOut, shortCall.Result.Outcome);
        Assert.Contains("had not finished starting (no ready line yet)", shortCall.Result.Error);
        _host.Advance(6000);
        Assert.Contains("may be hung starting up", longCall.Result.Error);
    }

    [Fact]
    public void SC045_SC047_a_call_times_out_at_8s_by_default_and_a_late_answer_is_counted_and_logged()
    {
        var p = Ready();
        var t = p.CallAsync("slow", null, TimeSpan.Zero);   // non-positive: the default
        long id = _launch.Last.LastId("slow");
        _host.Advance(7999);
        Assert.False(t.IsCompleted);
        _host.Advance(1);
        Assert.Equal(PlatformOutcome.TimedOut, t.Result.Outcome);
        Assert.Equal("the sidecar did not answer \"slow\" within 8s", t.Result.Error);
        _host.Advance(500);
        _launch.Last.Say($"{{\"id\":{id},\"ok\":true,\"result\":1}}");
        Assert.Equal(1, p.Shed.Late);
        Assert.Contains(_logs, l => l.Contains("answered \"slow\"") && l.Contains("after its 8000 ms timeout; the answer was dropped"));
        _launch.Last.Say("{\"id\":999,\"ok\":true}");
        Assert.Contains(_logs, l => l.Contains("answered id 999, which nobody asked for"));
    }

    [Fact]
    public void SC003_outcomes_follow_the_reply_reason_and_reserved_keys_are_refused()
    {
        var p = Ready();
        var busy = p.CallAsync("x");
        _launch.Last.Say($"{{\"id\":{_launch.Last.LastId("x")},\"ok\":false,\"error\":\"the ui lane is full\",\"reason\":\"busy\"}}");
        Assert.Equal(PlatformOutcome.Busy, busy.Result.Outcome);
        var plain = p.CallAsync("y");
        _launch.Last.Say($"{{\"id\":{_launch.Last.LastId("y")},\"ok\":false}}");
        Assert.Equal("sidecar refused \"y\" without saying why", plain.Result.Error);
        var reserved = p.CallAsync("trayClick", new JsonObject { ["id"] = 3 }).Result;
        Assert.Equal(PlatformOutcome.Refused, reserved.Outcome);
        Assert.Contains("reserved key \"id\"", reserved.Error);
    }

    [Fact]
    public void SC049_the_restart_ladder_is_3_6_12_s_then_gives_up_and_retry_gives_a_fresh_budget()
    {
        var p = Ready();
        foreach (var (delay, n) in new[] { (3000, 1), (6000, 2), (12000, 3) }) {
            _launch.Last.Exit(1);
            Assert.Equal(PlatformState.Down, p.Status.State);
            Assert.Contains($"exited with code 1; restarting in {delay / 1000}s (attempt {n} of 3)", p.Status.Reason);
            int before = _launch.Started.Count;
            _host.Advance(delay - 1);
            Assert.Equal(before, _launch.Started.Count);
            _host.Advance(1);
            Assert.Equal(before + 1, _launch.Started.Count);
            _launch.Last.Ready();
            _host.Advance();
        }
        _launch.Last.Exit(1);
        Assert.True(p.GaveUp);
        Assert.Contains("gave up after 3 restarts in a row; try again (retry)", p.Status.Reason);
        p.Retry();
        Assert.Equal(PlatformState.Starting, p.Status.State);
        Assert.Equal(0, p.Restarts);
    }

    [Fact]
    public void SC049_the_budget_resets_after_60_s_up()
    {
        var p = Ready(o => o.HealthMs = 0);
        _launch.Last.Exit(1);
        _host.Advance(3000);
        _launch.Last.Ready();
        _host.Advance(60000);
        _launch.Last.Exit(1);
        Assert.Contains("attempt 1 of 3", p.Status.Reason);
    }

    [Fact]
    public void SC048_three_missed_pings_end_the_process_and_the_status_says_it_was_not_answering()
    {
        var p = Ready(o => { o.HealthMs = 1000; o.HealthTimeoutMs = 300; o.HealthMisses = 3; });
        _host.Advance(1000);   // ping 1
        _host.Advance(300);    // miss 1
        _host.Advance(700);    // still in flight a whole interval later: miss 2
        _host.Advance(1000);   // miss 3
        Assert.True(_launch.Last.Killed);
        Assert.Equal(1, p.HealthKills);
        Assert.Contains("the helper is not answering", p.Status.Reason);
        _launch.Last.Exit(-1);
        Assert.Contains("was stopped because the helper is not answering", p.Status.Reason);
    }

    [Fact]
    public void SC048_an_answered_ping_keeps_it_alive()
    {
        var p = Ready(o => { o.HealthMs = 1000; o.HealthTimeoutMs = 300; });
        for (int i = 0; i < 5; i++) {
            _host.Advance(1000);
            _launch.Last.Say($"{{\"id\":{_launch.Last.LastId("ping")},\"ok\":true,\"result\":{{}}}}");
        }
        Assert.False(_launch.Last.Killed);
        Assert.True(p.Status.Ready);
    }

    [Fact]
    public void SC050_stderr_is_rate_limited_and_an_oversized_line_is_dropped_with_its_caller_told()
    {
        var p = Ready(o => o.HealthMs = 0);
        var proc = _launch.Last;
        proc.Sink.Stderr(proc, string.Join("\n", Enumerable.Range(1, 25).Select(i => "line " + i)) + "\n");
        Assert.Equal(20, _logs.Count(l => l.StartsWith("error: line ")));
        _host.Advance(10000);
        proc.Sink.Stderr(proc, "again\n");
        Assert.Contains(_logs, l => l.Contains("printed 5 more error line(s) in 10 s"));
        var big = p.CallAsync("clip.list");
        long id = proc.LastId("clip.list");
        proc.Sink.Stdout(proc, $"{{\"id\":{id},\"ok\":true,\"result\":\"" + new string('x', ProcessPlatform.MaxLineChars + 10));
        proc.Sink.Stdout(proc, "\"}\n{\"ev\":\"tick\"}\n");
        Assert.Contains("over the 4 MB line cap", big.Result.Error);
    }

    [Fact]
    public void SC051_a_dead_pipe_refuses_the_call_and_never_throws()
    {
        var p = Ready(o => o.HealthMs = 0);
        _launch.Last.BrokenPipe = true;
        var r = p.CallAsync("focus").Result;
        Assert.Equal(PlatformOutcome.Refused, r.Outcome);
        Assert.Equal("could not send \"focus\" to the sidecar: EPIPE", r.Error);
    }

    [Fact]
    public async Task SC052_stop_sends_quit_ends_stdin_and_kills_after_the_grace_time()
    {
        var p = Ready(o => o.HealthMs = 0);
        var proc = _launch.Last;
        var stopped = p.StopAsync("Mantle is quitting");
        Assert.Contains(proc.Written, l => l.Contains("\"cmd\":\"quit\""));
        Assert.True(proc.InputEnded);
        _host.Advance(1499);
        Assert.False(proc.Killed);
        _host.Advance(1);
        Assert.True(proc.Killed);
        proc.Exit(1);
        await stopped;
        Assert.Equal(PlatformState.Down, p.Status.State);
        Assert.Equal("Mantle is quitting", p.Status.Reason);
        Assert.Single(_launch.Started);   // no restart while stopping
    }

    [Fact]
    public void SC053_protocol_age_refuses_an_exe_whose_ping_has_no_dpiAware()
    {
        var p = Ready(o => o.HealthMs = 0);
        var age = p.ProtocolAgeAsync();
        _launch.Last.Say($"{{\"id\":{_launch.Last.LastId("ping")},\"ok\":true,\"result\":{{\"version\":\"0.2\"}}}}");
        Assert.False(age.Result.Current);
        Assert.True(age.Result.Answered);
        Assert.Contains("older than the protocol", age.Result.Reason);
        Assert.True(ProcessPlatform.IsOldExeError("unknown command: tbhide"));
        Assert.False(ProcessPlatform.IsOldExeError("access was denied"));
    }

    [Fact]
    public void SC054_every_status_change_is_plain_words_and_events_reach_their_handlers()
    {
        var p = Make();
        var got = new List<string>();
        p.On("windows", e => got.Add(e.Body.GetProperty("ev").GetString()!));
        int readies = 0;
        p.OnReady(() => readies++);
        p.Start();
        _launch.Last.Ready(passive: true);
        _launch.Last.Say("{\"ev\":\"windows\",\"list\":[]}");
        _launch.Last.Say("not json at all");
        Assert.Equal(new[] { "windows" }, got);
        Assert.Equal(1, readies);
        Assert.Contains(_logs, l => l.Contains("sidecar printed a line that is not JSON: not json at all"));
        Assert.Contains(_statuses, s => s.Reason == "sidecar started, waiting for its ready line");
        Assert.Contains(_statuses, s => s.Reason.EndsWith("running as pid " + _launch.Last.Pid + ", passive: it will refuse to change the shell"));
        Assert.All(_statuses, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }

    [Fact]
    public void SC023_a_launcher_that_fails_is_a_down_status_not_an_exception()
    {
        _launch.Refuse = "Access is denied";
        var p = Make();
        p.Start();
        Assert.Equal("sidecar would not start: Access is denied", p.Status.Reason);
    }

    [Fact]
    public void CORE008_the_override_exe_is_only_honoured_outside_a_live_run()
    {
        var env = new Dictionary<string, string> { ["MANTLE_SIDECAR_EXE"] = @"X:\other\Mantle.Sidecar.exe" };
        var live = SidecarLocator.Resolve(LaunchArgs.Parse(new[] { "--live" }), k => env.GetValueOrDefault(k), @"X:\app");
        Assert.False(live.Override);
        Assert.Contains("is ignored: it is honoured only with --test", live.Line);
        var test = SidecarLocator.Resolve(LaunchArgs.Parse(new[] { "--test", "--profile-name=a" }), k => env.GetValueOrDefault(k), @"X:\app");
        Assert.True(test.Override);
        Assert.Equal(@"X:\other\Mantle.Sidecar.exe", test.Path);
    }
}
