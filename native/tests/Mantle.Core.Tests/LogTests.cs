using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Mantle.Core.Logging;
using Xunit;

namespace Mantle.Core.Tests;

public class LogTests
{
    sealed class Clock
    {
        public DateTime Now = new(2026, 3, 4, 23, 59, 58, 123);
        public double Ms;
    }

    static (MantleLog log, Clock clock, List<string> said) Make(string file, Action<LogOptions>? tweak = null)
    {
        var c = new Clock();
        var said = new List<string>();
        var o = new LogOptions { Now = () => c.Now, ClockMs = () => c.Ms, Say = s => said.Add(s) };
        tweak?.Invoke(o);
        return (new MantleLog(file, o), c, said);
    }

    static string[] Lines(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd().Split('\n');
    }

    [Fact]
    public void Lines_carry_the_time_and_the_first_line_and_midnight_the_date()
    {
        using var d = new TestDir("log");
        string f = d.File("mantle-native.log");
        var (log, c, _) = Make(f);
        Assert.True(log.Start("===== mantle-native start x pid 1: --test"));
        log.Line("first");
        c.Now = c.Now.AddSeconds(3);   // past midnight
        log.Line("second");
        log.Line("third");
        log.Dispose();
        var l = Lines(f);
        Assert.Equal("2026-03-04 23:59:58.123 ===== mantle-native start x pid 1: --test", l[0]);
        Assert.Equal("23:59:58.123 mantle-native: first", l[1]);
        Assert.Equal("2026-03-05 00:00:01.123 mantle-native: second", l[2]);
        Assert.Equal("00:00:01.123 mantle-native: third", l[3]);
        Assert.Equal("", l[4]);   // ends with a newline
    }

    [Fact]
    public void Every_line_matches_electrons_stamp_format()
    {
        using var d = new TestDir("log");
        string f = d.File("a.log");
        var (log, _, _) = Make(f);
        log.Start("===== start");
        log.Line("multi\nline\r\ntext");
        log.Dispose();
        var stamp = new Regex(@"^(\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2}\.\d{3} ");
        foreach (var line in Lines(f)) if (line.Length > 0) Assert.Matches(stamp, line);
        Assert.Equal(4, Array.FindAll(Lines(f), x => x.Length > 0).Length);
    }

    [Fact]
    public void A_new_run_starts_after_a_blank_line_with_its_date()
    {
        using var d = new TestDir("log");
        string f = d.File("a.log");
        var (a, _, _) = Make(f); a.Start("===== run 1"); a.Line("x"); a.Dispose();
        var (b, _, _) = Make(f); b.Start("===== run 2"); b.Dispose();
        var l = Lines(f);
        Assert.Equal("", l[2]);
        Assert.StartsWith("2026-03-04 ", l[3]);
        Assert.EndsWith("===== run 2", l[3]);
    }

    [Fact]
    public void It_rotates_between_lines_and_keeps_two_old_files()
    {
        using var d = new TestDir("log");
        string f = d.File("a.log");
        var (log, _, _) = Make(f, o => o.MaxBytes = 300);
        log.Start("===== start");
        for (int i = 0; i < 40; i++) log.Line("line " + i + " " + new string('x', 30));
        log.Dispose();
        Assert.True(File.Exists(f + ".1"));
        Assert.True(File.Exists(f + ".2"));
        Assert.False(File.Exists(f + ".3"));
        Assert.Contains(Lines(f), x => x.Contains("log: rotated at 0 MB: the previous", StringComparison.Ordinal));
        // nothing split: every non-empty line in every file is whole and stamped
        foreach (var file in new[] { f, f + ".1", f + ".2" })
            foreach (var line in Lines(file))
                if (line.Length > 0) Assert.Matches(@"^(\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2}\.\d{3} (mantle-native: |=====)", line);
        Assert.True(new FileInfo(f).Length < 300 + 200);
    }

    [Fact]
    public void A_file_already_over_the_limit_rotates_at_start()
    {
        using var d = new TestDir("log");
        string f = d.Write("a.log", new string('o', 500) + "\n");
        var (log, _, _) = Make(f, o => o.MaxBytes = 300);
        log.Start("===== start");
        log.Dispose();
        Assert.True(File.Exists(f + ".1"));
        Assert.Contains(Lines(f), x => x.Contains("rotated at start", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rotation_that_fails_keeps_writing_says_so_and_retries_later()
    {
        using var d = new TestDir("log");
        string f = d.File("a.log");
        d.Write("a.log.1", "old one\n");
        d.Write("a.log.2", "old two\n");
        var (log, _, said) = Make(f, o => { o.MaxBytes = 300; o.RetryBytes = 200; });
        log.Start("===== start");
        using (new FileStream(f + ".2", FileMode.Open, FileAccess.Read, FileShare.Read)) {   // a reader that blocks the rename over .2
            for (int i = 0; i < 12; i++) log.Line("line " + i + " " + new string('x', 30));
        }
        var l = Lines(f);
        Assert.Contains(l, x => x.Contains("could NOT rotate", StringComparison.Ordinal) && x.Contains("next try at", StringComparison.Ordinal));
        Assert.Contains(said, s => s.Contains("could NOT rotate", StringComparison.Ordinal));
        Assert.Contains(l, x => x.EndsWith("line 11 " + new string('x', 30), StringComparison.Ordinal));   // kept writing
        // with the reader gone, the retry 200 bytes later rotates
        for (int i = 0; i < 12; i++) log.Line("after " + i + " " + new string('y', 30));
        log.Dispose();
        Assert.Contains(Lines(f), x => x.Contains("the previous", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bench_copy_uses_the_spike_format()
    {
        using var d = new TestDir("log");
        string f = d.File("a.log"), m = d.File("native-log.txt");
        var (log, _, _) = Make(f, o => o.MirrorFile = m);
        log.Start("===== start");
        log.Line("first frame 393 ms after Main");
        log.Dispose();
        var l = File.ReadAllLines(m);
        Assert.Equal("23:59:58.123 ===== start", l[0]);
        Assert.Equal("23:59:58.123 first frame 393 ms after Main", l[1]);
    }

    [Fact]
    public void A_log_that_cannot_open_says_so_and_never_throws()
    {
        using var d = new TestDir("log");
        string dirAsFile = d.Write("blocker", "x");
        var (log, _, said) = Make(Path.Combine(dirAsFile, "a.log"));
        Assert.False(log.Start("===== start"));
        log.Line("still fine");
        Assert.Contains(said, s => s.Contains("cannot be written", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scrubber_removes_home_user_and_pc()
    {
        var s = new LogScrubber(@"D:\Home\Sample Person", "sperson", "SAMPLE-PC");
        Assert.Equal(@"opened %USERPROFILE%\AppData\Roaming\mantle", s.Scrub(@"opened D:\Home\Sample Person\AppData\Roaming\mantle"));
        Assert.Equal(@"json %USERPROFILE%\\x", s.Scrub(@"json D:\\Home\\Sample Person\\x"));
        Assert.Equal("fwd %USERPROFILE%/x", s.Scrub("fwd d:/home/sample person/x"));
        Assert.Equal(@"E:\Users\<user>\x", s.Scrub(@"E:\Users\sperson\x"));
        Assert.Equal("host <pc> up", s.Scrub("host SAMPLE-PC up"));
        Assert.Equal("SAMPLE-PCX stays", s.Scrub("SAMPLE-PCX stays"));
        Assert.Equal(@"D:\Home\Sample Personnel", s.Scrub(@"D:\Home\Sample Personnel"));   // a longer name is not the home
    }

    [Fact]
    public void Log_lines_are_scrubbed_on_disk()
    {
        using var d = new TestDir("log");
        string f = d.File("a.log");
        var (log, _, _) = Make(f, o => o.Scrub = new LogScrubber(@"D:\Home\Sample Person", "sperson", "SAMPLE-PC"));
        log.Start("===== start on SAMPLE-PC");
        log.Line(@"data root D:\Home\Sample Person\AppData\Roaming\mantle");
        log.Dispose();
        string all = File.ReadAllText(f);
        Assert.DoesNotContain("Sample Person", all);
        Assert.DoesNotContain("SAMPLE-PC", all);
        Assert.Contains("%USERPROFILE%", all);
    }

    [Fact]
    public void Startup_marks_are_logged_in_order()
    {
        var sink = new ListLog();
        var marks = new StartupMarks(sink, DateTimeOffset.UtcNow.AddMilliseconds(-50));
        marks.Add("main");
        marks.Add("first-frame");
        Assert.Equal(2, marks.All.Count);
        Assert.Equal("main", marks.All[0].Name);
        Assert.True(marks.All[0].SinceStartMs >= 40);
        Assert.True(marks.All[1].SinceMainMs >= marks.All[0].SinceMainMs);
        Assert.Matches(@"^mark first-frame \+\d+ ms after process start \(\+\d+ after Main\)$", sink.Lines[1]);
    }
}
