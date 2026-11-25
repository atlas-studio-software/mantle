using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Lifecycle;
using Xunit;

namespace Mantle.Core.Tests.Lifecycle;

/// <summary>
/// lane r12-crash-restart (BOOT-014, DATA-010/011): sidecar/CrashLoop.cs, the supervisor's ONE crash rule for native
/// Mantle, linked into this project and run with a fake clock, a fake launcher, a fake restore script and a fake
/// crash.json. Nothing here starts, kills or restores anything real.
/// </summary>
public class CrashLoopTests
{
    const long T0 = 1_764_000_000_000;   // a fixed Unix-ms clock
    const long Min = 60_000;

    sealed class Fake : ICrashHost
    {
        public long Clock = T0;
        public List<long> Stored = new();
        public Dictionary<string, object>? Last;
        public int Writes, Starts, Restores, Asks;
        public string? WriteFails, OtherShellWords, StartWhy, RestoreWouldNotRun;
        public string? OtherShellAtStart;
        public int NextPid = 4242;
        public int RestoreExit;
        public string RestoreOut = "  put the taskbar back\r\nverified: Windows is back the way it was\r\n";
        public bool SayYes;
        public readonly List<string> Lines = new();

        public long Now() => Clock;
        public List<long> History() => new(Stored);
        public string? WriteCrash(Dictionary<string, object> record)
        {
            if (WriteFails != null) return WriteFails;
            Writes++;
            Stored = ((List<object>)record["history"]).Select(o => (long)o).ToList();
            Last = (Dictionary<string, object>)record["last"];
            Json = JsonSerializer.Serialize(record);
            return null;
        }
        public string Json = "";
        public string? OtherShell() => OtherShellWords ?? (Starts > 0 ? OtherShellAtStart : null);
        public string? RunRestore(out int exitCode, out string output)
        {
            Restores++;
            exitCode = RestoreExit; output = RestoreOut;
            return RestoreWouldNotRun;
        }
        public int StartAgain(out string? why)
        {
            Starts++;
            why = StartWhy ?? OtherShellAtStart;
            return why == null ? NextPid++ : 0;
        }
        public bool AskAgain(string text) { Asks++; Lines.Add("ASK: " + text); return SayYes; }
        public void Log(string line) => Lines.Add(line);
    }

    static CrashFacts Crashed(int pid = 100) => new() { Pid = pid, CodeHex = "0xC0000005", Words = "it crashed reading memory it did not own" };

    static CrashOutcome CrashAt(Fake f, long at, int pid = 100) { f.Clock = at; return CrashLoop.Handle(f, Crashed(pid)); }

    // ------------------------------------------------------------------ the rule itself

    [Theory]
    [InlineData(new long[0])]
    [InlineData(new long[] { T0 - 1 * Min })]
    [InlineData(new long[] { T0 - 9 * Min, T0 - 5 * Min })]
    [InlineData(new long[] { T0 - 9 * Min, T0 - 5 * Min, T0 - 1 * Min })]
    [InlineData(new long[] { T0 - 10 * Min, T0 - 5 * Min, T0 - 1 * Min })]            // exactly 10 minutes old: out of the window
    [InlineData(new long[] { T0 - 30 * Min, T0 - 20 * Min, T0 - 11 * Min, T0 - 1 })]
    [InlineData(new long[] { T0 + 5 * Min, T0 - 1 * Min })]                           // a time in the future is not counted
    [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, T0 - 3, T0 - 2, T0 - 1 })]
    public void The_rule_is_main_js_crashPlan_one_to_one(long[] history)
    {
        bool restart = CrashLoop.Decide(history.ToList(), T0, out var keep, out int recent);
        var plan = Crash.Decide(history, T0);
        Assert.Equal(plan.Restart, restart);
        Assert.Equal(plan.Recent, recent);
        Assert.Equal(plan.History, keep);
        Assert.Equal(plan.Why, CrashLoop.Why(restart, recent));
        Assert.Equal((long)Crash.Window.TotalMilliseconds, CrashLoop.WindowMs);
        Assert.Equal(Crash.MaxRestarts, CrashLoop.MaxRestarts);
    }

    [Theory]
    [InlineData(0, "verified: Windows is back the way it was\r\n", true)]
    [InlineData(0, "  taskbar: back\r\nverified: Windows is back the way it was", true)]
    [InlineData(0, "NOT VERIFIED - 2 thing(s) did not check out:\r\n  PROBLEM  taskbar", false)]
    [InlineData(0, "Verified: Windows is back", false)]                  // case-sensitive
    [InlineData(0, "  verified: indented is not the script's line", false)]
    [InlineData(1, "verified: Windows is back the way it was", false)]    // exit 0 is needed as well
    [InlineData(0, "", false)]
    public void Windows_counts_as_back_only_on_exit_0_and_a_verified_line(int exit, string output, bool want)
        => Assert.Equal(want, CrashLoop.Verified(exit, output));

    // ------------------------------------------------------------------ deliberate stops are never crashes

    [Fact]
    public void A_stand_down_is_a_clean_quit_never_counted_never_restarted_even_with_a_crash_exit_code()
    {
        var f = new Fake { Stored = { T0 - Min } };
        bool askedOther = false, askedTree = false;
        var facts = Crashed();
        facts.StandDownWhy = "the kill switch (Ctrl+Alt+Shift+Backspace)";
        facts.OtherMain = () => { askedOther = true; return 0; };
        facts.TreeKill = () => { askedTree = true; return false; };
        var o = CrashLoop.Handle(f, facts);
        Assert.Equal("quit", o.Kind);
        Assert.False(o.Counted);
        Assert.Equal(0, o.NewPid);
        Assert.Equal((0, 0, 0), (f.Writes, f.Starts, f.Restores));
        Assert.Equal(new List<long> { T0 - Min }, f.Stored);   // crash.json untouched
        Assert.False(askedOther || askedTree);                  // a quit stops the questions there
        Assert.Contains(f.Lines, l => l.Contains("quit on purpose (the kill switch") && l.Contains("not counted as a crash"));
    }

    [Theory]
    [InlineData("--quit (switch-back.ps1, or the tray's Quit)")]
    [InlineData("Windows is ending the session")]
    [InlineData("Restore Windows taskbar")]
    public void Every_named_stand_down_is_a_quit(string why)
    {
        var f = new Fake();
        var facts = Crashed(); facts.StandDownWhy = why;
        Assert.Equal("quit", CrashLoop.Handle(f, facts).Kind);
        Assert.Equal((0, 0, 0), (f.Writes, f.Starts, f.Restores));
    }

    [Fact]
    public void Signing_out_or_shutting_down_is_not_a_crash()
    {
        var f = new Fake();
        var facts = Crashed(); facts.ShuttingDown = true;
        var o = CrashLoop.Handle(f, facts);
        Assert.Equal("signout", o.Kind);
        Assert.Equal((0, 0, 0), (f.Writes, f.Starts, f.Restores));
    }

    [Fact]
    public void A_main_replaced_by_another_live_main_is_not_a_crash()
    {
        var f = new Fake();
        var facts = Crashed(); facts.OtherMain = () => 777;
        var o = CrashLoop.Handle(f, facts);
        Assert.Equal("replaced", o.Kind);
        Assert.Equal((0, 0, 0), (f.Writes, f.Starts, f.Restores));
    }

    [Fact]
    public void A_process_tree_kill_puts_Windows_back_but_is_neither_counted_nor_restarted()
    {
        var f = new Fake();
        var facts = Crashed(); facts.TreeKill = () => true;
        var o = CrashLoop.Handle(f, facts);
        Assert.Equal("treekill", o.Kind);
        Assert.Equal((0, 0, 1), (f.Writes, f.Starts, f.Restores));
        Assert.True(o.Verified);
    }

    // ------------------------------------------------------------------ crashes: 3 restarts in 10 minutes, the 4th stays down

    [Fact]
    public void Three_crashes_in_10_minutes_are_started_again_the_4th_stays_down_and_Windows_is_put_back_verified()
    {
        var f = new Fake();
        for (int i = 0; i < 3; i++) {
            var o = CrashAt(f, T0 + i * 2 * Min);
            Assert.Equal("restarted", o.Kind);
            Assert.Equal(i + 1, o.Recent);
            Assert.True(o.NewPid > 0);
            Assert.Equal(true, f.Last!["restarted"]);
            Assert.Equal((long)o.NewPid, f.Last["newPid"]);
        }
        Assert.Equal(3, f.Starts);
        Assert.Equal(0, f.Restores);

        var down = CrashAt(f, T0 + 6 * Min);
        Assert.Equal("gaveup", down.Kind);
        Assert.Equal(4, down.Recent);
        Assert.Equal(0, down.NewPid);
        Assert.Equal(0, down.Exit);
        Assert.Equal(3, f.Starts);           // no 4th start
        Assert.Equal(1, f.Restores);
        Assert.True(down.Verified);
        Assert.Equal("done and verified", down.Restore);
        Assert.Equal(false, f.Last!["restarted"]);
        var restore = (Dictionary<string, object>)f.Last["restore"];
        Assert.Equal(true, restore["verified"]);
        Assert.Equal(4, f.Stored.Count);     // the record is kept for the tray and the next start
        Assert.Contains(f.Lines, l => l.Contains("stopped with an error 4 times in 10 minutes, so it was not started again"));
        Assert.Contains(f.Lines, l => l.Contains("NOT started again; the Windows taskbar: done and verified"));
    }

    [Fact]
    public void Crashes_spread_over_more_than_10_minutes_keep_coming_back()
    {
        var f = new Fake();
        for (int i = 0; i < 8; i++) Assert.Equal("restarted", CrashAt(f, T0 + i * 4 * Min).Kind);   // never more than 3 inside 10 minutes
        Assert.Equal(8, f.Starts);
        Assert.Equal(0, f.Restores);
    }

    [Fact]
    public void A_history_written_by_main_itself_counts_too_one_file_one_rule()
    {
        // crash.json as main.js / Crash.Record wrote it before a supervisor counted (a run without a supervisor)
        var f = new Fake { Stored = { T0 - 3 * Min, T0 - 2 * Min, T0 - 1 * Min } };
        var o = CrashAt(f, T0);
        Assert.Equal("gaveup", o.Kind);
        Assert.Equal(0, f.Starts);
    }

    [Fact]
    public void An_unverified_restore_is_said_and_kept_never_reported_as_back()
    {
        var f = new Fake { Stored = { T0 - 3 * Min, T0 - 2 * Min, T0 - 1 * Min }, RestoreExit = 0,
                           RestoreOut = "NOT VERIFIED - 1 thing(s) did not check out:\r\n  PROBLEM  taskbar: still hidden\r\n" };
        var o = CrashAt(f, T0);
        Assert.Equal("gaveup", o.Kind);
        Assert.False(o.Verified);
        Assert.Equal("NOT verified (exit 0)", o.Restore);
        Assert.Equal(false, ((Dictionary<string, object>)f.Last!["restore"])["verified"]);
        Assert.Contains(f.Lines, l => l.StartsWith("ASK: ") && l.Contains("could not be confirmed back"));
    }

    [Fact]
    public void A_restore_that_will_not_run_is_said_in_words()
    {
        var f = new Fake { Stored = { T0 - 3 * Min, T0 - 2 * Min, T0 - 1 * Min }, RestoreWouldNotRun = "no file at tools\\restore-windows.ps1" };
        var o = CrashAt(f, T0);
        Assert.False(o.Verified);
        Assert.Equal("the restore script would not run (no file at tools\\restore-windows.ps1)", o.Restore);
    }

    [Fact]
    public void Yes_at_the_crash_loop_question_starts_over_with_an_empty_history()
    {
        var f = new Fake { Stored = { T0 - 3 * Min, T0 - 2 * Min, T0 - 1 * Min }, SayYes = true };
        var o = CrashAt(f, T0);
        Assert.Equal("restarted", o.Kind);
        Assert.Equal(1, f.Asks);
        Assert.Equal(1, f.Restores);          // Windows was put back before the question
        Assert.Empty(f.Stored);
        Assert.Equal(true, f.Last!["restarted"]);
        Assert.Equal("restarted", CrashAt(f, T0 + Min).Kind);   // the count really started over
    }

    [Fact]
    public void A_crash_that_cannot_be_written_down_is_not_started_again()
    {
        var f = new Fake { WriteFails = "access denied" };
        var o = CrashAt(f, T0);
        Assert.Equal("gaveup", o.Kind);
        Assert.Equal(0, f.Starts);
        Assert.Equal(1, f.Restores);
        Assert.Contains(f.Lines, l => l.Contains("the crash could not be written down (access denied), so it was not started again"));
    }

    [Fact]
    public void A_start_that_fails_puts_Windows_back_and_exits_1()
    {
        var f = new Fake { StartWhy = "the record of how Mantle was started has no program that exists" };
        var o = CrashAt(f, T0);
        Assert.Equal("startfailed", o.Kind);
        Assert.Equal(1, o.Exit);
        Assert.Equal(1, f.Restores);
        Assert.Equal(false, f.Last!["restarted"]);
        Assert.Equal("the record of how Mantle was started has no program that exists", f.Last["failed"]);
    }

    // ------------------------------------------------------------------ never while another shell is live

    [Fact]
    public void While_Electron_is_the_live_shell_nothing_is_started_and_Windows_is_left_to_it()
    {
        var f = new Fake { OtherShellWords = "Electron Mantle's single-instance lock is held" };
        var o = CrashAt(f, T0);
        Assert.Equal("othershell", o.Kind);
        Assert.True(o.Counted);                // it was a crash: it is written down
        Assert.Equal(0, f.Starts);
        Assert.Equal(0, f.Restores);           // restore-windows.ps1 would stop Electron's processes and fight its taskbar
        Assert.Equal(false, f.Last!["restarted"]);
        Assert.Contains("another Mantle is the live shell", (string)f.Last["why"]);
    }

    [Fact]
    public void A_shell_that_goes_live_during_the_waits_stops_the_start_and_gets_no_restore_over_it()
    {
        var f = new Fake { OtherShellAtStart = "another Mantle became the live shell meanwhile (another Mantle holds Local\\Mantle.LiveShell)" };
        var o = CrashAt(f, T0);
        Assert.Equal("othershell", o.Kind);
        Assert.Equal(0, o.Exit);
        Assert.Equal(1, f.Starts);
        Assert.Equal(0, f.Restores);
        Assert.Equal(false, f.Last!["restarted"]);
    }

    // ------------------------------------------------------------------ the record the tray and the next start read

    [Fact]
    public void The_record_is_main_js_shape_so_the_tray_line_reads_it()
    {
        var f = new Fake { Stored = { T0 - 3 * Min, T0 - 2 * Min, T0 - 1 * Min } };
        CrashAt(f, T0);
        string file = Path.Combine(Path.GetTempPath(), "mantle-crashloop-" + Guid.NewGuid().ToString("N") + ".json");
        try {
            File.WriteAllText(file, f.Json);
            var rec = Crash.Read(file);
            Assert.Equal(f.Stored, rec.History);
            Assert.Equal(T0, rec.At);
            Assert.False(rec.Restarted);
            Assert.Equal("Stopped after repeated errors at 12:00", Crash.RecoveryLine(rec.At, rec.Restarted, T0 + 5 * Min, _ => "12:00"));
            using var d = JsonDocument.Parse(f.Json);
            var last = d.RootElement.GetProperty("last");
            Assert.Equal("it crashed reading memory it did not own", last.GetProperty("what").GetString());   // no crash note: the exit words
            Assert.Equal("supervisor", last.GetProperty("by").GetString());
        } finally { File.Delete(file); }
    }

    [Fact]
    public void The_crash_note_main_left_is_the_records_what()
    {
        var f = new Fake();
        var facts = Crashed(); facts.What = "Object reference not set to an instance of an object.";
        CrashLoop.Handle(f, facts);
        Assert.Equal("Object reference not set to an instance of an object.", f.Last!["what"]);
        Assert.Equal(true, f.Last["restarted"]);
        Assert.Contains(f.Lines, l => l.Contains("stopped with an error, so it is starting again"));
    }
}
