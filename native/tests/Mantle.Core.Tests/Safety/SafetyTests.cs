using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Lifecycle;
using Mantle.Core.Safety;
using Xunit;

namespace Mantle.Core.Tests.Safety;

// main.js crashPlan / recoveryLine / makeRejectionLog / shutdown / dirtyStartGuard as the spec, plus the
// new never-two-shells row.
public class SafetyTests
{
    const long Min = 60_000;

    [Fact]
    public void SAFE018_three_restarts_in_10_minutes_then_the_4th_stays_down_and_the_record_is_written()
    {
        long now = 100 * Min;
        Assert.Equal("stopped with an error, so it is starting again", Crash.Decide(null, now).Why);
        var third = Crash.Decide(new long[] { now - 2 * Min, now - Min, now - 30 * Min }, now);
        Assert.True(third.Restart);
        Assert.Equal(3, third.Recent);
        var fourth = Crash.Decide(new long[] { now - 3 * Min, now - 2 * Min, now - Min }, now);
        Assert.False(fourth.Restart);
        Assert.Equal("stopped with an error 4 times in 10 minutes, so it was not started again", fourth.Why);
        Assert.Equal("first line", Crash.What("first   line\n   at Stack.Frame()"));
        Assert.Equal(160, Crash.What(new string('x', 400)).Length);
        using var d = new TestDir("crash");
        var plan = Crash.Record(d.Path, true, "boom", now);
        Assert.True(plan.Restart);
        var back = Crash.Read(Path.Combine(d.Path, Crash.FileName));
        Assert.Equal(new List<long> { now }, back.History);
        Assert.Equal((long?)now, back.At);
        Assert.True(back.Restarted);
        Assert.False(Crash.Record(d.Path, false, "boom", now + 1).Restart);
    }

    [Fact]
    public void SAFE019_the_next_start_says_it_for_an_hour()
    {
        long now = 1000 * Min;
        Assert.Equal("Restarted by itself after an error at 09:41", Crash.RecoveryLine(now - 5 * Min, true, now, _ => "09:41"));
        Assert.Equal("Stopped after repeated errors at 09:41", Crash.RecoveryLine(now - 59 * Min, false, now, _ => "09:41"));
        Assert.Null(Crash.RecoveryLine(now - 61 * Min, true, now, _ => "x"));
        Assert.Null(Crash.RecoveryLine(null, true, now, _ => "x"));
    }

    [Fact]
    public void CORE038_a_rejection_is_logged_once_then_at_the_10th_100th_repeat()
    {
        var lines = new List<string>();
        var log = new RejectionLog(lines.Add);
        for (int i = 0; i < 100; i++) log.Note("socket closed", "Error: socket closed\n at x");
        Assert.Equal(3, lines.Count);
        Assert.StartsWith("mantle: unhandled promise rejection: Error: socket closed", lines[0]);
        Assert.Equal("mantle: the same unhandled promise rejection has now happened 10 times: socket closed", lines[1]);
        Assert.EndsWith("100 times: socket closed", lines[2]);
    }

    [Fact]
    public async Task CORE025_shutdown_runs_once_in_order_and_a_failing_step_does_not_stop_the_road()
    {
        var lines = new List<string>();
        var order = new List<string>();
        var sd = new Shutdown(lines.Add, "powershell -File restore-windows.ps1");
        var steps = new List<Shutdown.Step> {
            new("stand the supervisor down", TimeSpan.FromSeconds(2), _ => { order.Add("supervisor"); return Task.CompletedTask; }),
            new("stop the features", TimeSpan.FromSeconds(2), _ => throw new InvalidOperationException("clips would not stop")),
            new("stop the platform", TimeSpan.FromSeconds(2), _ => { order.Add("platform"); return Task.CompletedTask; }),
        };
        var first = sd.RunAsync("the tray's Quit Mantle", ExitCodes.Ok, steps);
        var second = sd.RunAsync("--quit from a second start", ExitCodes.Failed, steps);
        Assert.Same(first, second);
        Assert.Equal(0, await first);
        Assert.Equal(new[] { "supervisor", "platform" }, order);
        Assert.Equal("failed: InvalidOperationException: clips would not stop", sd.Ran[1].Outcome);
        Assert.Contains(lines, l => l.Contains("already shutting down (the tray's Quit Mantle)"));
    }

    [Fact]
    public async Task CORE026_a_road_that_does_not_finish_in_the_hard_stop_leaves_anyway_with_the_recovery_command()
    {
        var lines = new List<string>();
        var sd = new Shutdown(lines.Add, "powershell -File restore-windows.ps1", TimeSpan.FromMilliseconds(300));
        var hang = new TaskCompletionSource();
        int code = await sd.RunAsync("Windows is ending the session", 0, new[] { new Shutdown.Step("restore the shell", TimeSpan.FromSeconds(30), _ => hang.Task) });
        Assert.Equal(0, code);
        Assert.Contains(lines, l => l.Contains("the restore did not finish in 0s, leaving anyway. If Windows looks wrong, run: powershell -File restore-windows.ps1"));
    }

    [Fact]
    public void SAFE038_native_refuses_live_while_electron_mantle_runs_live()
    {
        var none = new ShellStateNote(false, false, null, null, null);
        Assert.Null(TwoShells.Verdict(new TwoShells.Facts(false, Array.Empty<int>(), none, false, null)));
        var locked = TwoShells.Verdict(new TwoShells.Facts(true, Array.Empty<int>(), none, false, null))!;
        Assert.StartsWith("Mantle (the Electron app) is running live, so this native Mantle did not start", locked);
        Assert.Contains("its single-instance lock is held in its profile", locked);
        Assert.Contains("Mantle.exe is running (pid 42)", TwoShells.Verdict(new TwoShells.Facts(false, new[] { 42 }, none, false, null)));
        var live = new ShellStateNote(true, true, false, 77, null);
        Assert.Contains("owned by pid 77 (Mantle.Sidecar), which is still running", TwoShells.Verdict(new TwoShells.Facts(false, Array.Empty<int>(), live, true, "Mantle.Sidecar")));
        Assert.Null(TwoShells.Verdict(new TwoShells.Facts(false, Array.Empty<int>(), live, false, null)));   // dead owner: a dirty start, not a live shell
    }

    [Fact]
    public void SAFE038_the_probe_reads_the_lock_file_and_the_state_file_and_writes_nothing()
    {
        using var d = new TestDir("shells");
        d.Write("lockfile", "");
        d.Write("shell-state.json", "{\"pid\":" + Environment.ProcessId + ",\"restored\":false}");
        var before = Directory.GetFiles(d.Path).Select(f => (f, File.GetLastWriteTimeUtc(f))).ToList();
        var f = TwoShells.Probe(d.Path, null);
        Assert.True(f.LockFile);
        Assert.Equal(false, f.State.Restored);
        Assert.False(f.StatePidAlive);   // our own pid never counts as the other shell
        Assert.Equal(before, Directory.GetFiles(d.Path).Select(x => (x, File.GetLastWriteTimeUtc(x))).ToList());
    }

    [Fact]
    public void SAFE017_a_live_start_after_an_unrestored_run_restores_first_and_an_unreadable_note_is_said()
    {
        Assert.True(DirtyStart.Judge(true, new ShellStateNote(true, true, false, 1, null)).RunRestore);
        Assert.False(DirtyStart.Judge(false, new ShellStateNote(true, true, false, 1, null)).RunRestore);
        Assert.False(DirtyStart.Judge(true, new ShellStateNote(true, true, true, 1, null)).RunRestore);
        var bad = DirtyStart.Judge(true, new ShellStateNote(true, false, null, null, "JsonException: x"));
        Assert.False(bad.RunRestore);
        Assert.Contains("shell-state.json could not be read: JsonException: x", bad.Note);
        Assert.Equal("(the previous run died without restoring; Windows was put back before this one started)", DirtyStart.Outcome(true, "cmd", null));
        Assert.Contains("run this by hand: cmd", DirtyStart.Outcome(false, "cmd", "it exited with code 1"));
        using var d = new TestDir("note");
        d.Write("shell-state.json", "{\"restored\":");
        Assert.False(ShellStateNote.Read(d.File("shell-state.json")).Readable);
    }
}
