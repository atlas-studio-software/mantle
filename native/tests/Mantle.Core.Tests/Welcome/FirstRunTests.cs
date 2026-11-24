using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mantle.Core.Data;
using Mantle.Core.Features.Welcome;
using Xunit;

namespace Mantle.Core.Tests.Welcome;

/// <summary>
/// The first-run decision and its flag (WEL-001..013, 020..025), lib/welcome.test.js's cases. Every profile is a TestDir
/// under %TEMP% and every write goes through a scratch (or closed) gate; the real %APPDATA% is never touched.
/// </summary>
public sealed class FirstRunTests
{
    const string Now = "2026-11-24T12:00:00.000Z";
    static string FixedNow() => Now;

    static SafeFile Scratch(TestDir d) => new(WriteGate.Scratch(d.Path));

    /// Every file under the folder with its bytes and write time, to prove a step wrote nothing.
    static Dictionary<string, (byte[] Bytes, DateTime At)> Snapshot(TestDir d) =>
        Directory.GetFiles(d.Path, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));

    static void Unchanged(Dictionary<string, (byte[] Bytes, DateTime At)> before, TestDir d)
    {
        var after = Snapshot(d);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var (f, v) in before)
        {
            Assert.True(v.Bytes.SequenceEqual(after[f].Bytes), "changed: " + Path.GetFileName(f));
            Assert.Equal(v.At, after[f].At);
        }
    }

    static string Flag(TestDir d) => File.ReadAllText(d.File(FirstRun.FlagFile), Encoding.UTF8);

    [Fact]
    public void A_brand_new_profile_is_due_and_its_pending_flag_is_written_later_with_electrons_bytes()
    {
        using var d = new TestDir("welcome-new");
        var v = FirstRun.Judge(d.Path, test: false, optIn: false, FixedNow);
        Assert.True(v.Show);
        Assert.StartsWith("a brand-new profile", v.Reason);
        Assert.Equal(new WelcomeFlag("pending", At: Now), v.Write);
        Assert.Empty(Directory.GetFileSystemEntries(d.Path));            // WEL-001: the verdict wrote nothing

        var rec = new WelcomeRecorder(v, d.Path, false, Scratch(d), null, FixedNow);
        var w = rec.Record();
        Assert.True(w.Ok);
        Assert.True(w.Wrote);
        Assert.Equal("{\n  \"state\": \"pending\",\n  \"at\": \"2026-11-24T12:00:00.000Z\"\n}", Flag(d));
        Assert.Equal(new[] { FirstRun.FlagFile }, Directory.GetFileSystemEntries(d.Path).Select(f => Path.GetFileName(f)).ToArray());   // no tmp behind

        var again = FirstRun.Judge(d.Path, false, false, FixedNow);   // WEL-005: pending comes back, nothing re-written
        Assert.True(again.Show);
        Assert.Null(again.Write);
    }

    [Theory]
    [InlineData("shell-state.json", false)] [InlineData("theme.json", false)] [InlineData("features.json", false)] [InlineData("features", true)]
    [InlineData("wallpapers", true)] [InlineData("topbar-appbar.txt", false)] [InlineData("look-sync.json", false)] [InlineData("crash.json", false)]
    [InlineData("Local Storage", true)]
    public void Any_one_piece_of_evidence_marks_the_profile_existing_and_never_shows(string name, bool folder)
    {
        using var d = new TestDir("welcome-used");
        if (folder) Directory.CreateDirectory(d.File(name)); else d.Write(name, "{}");
        var v = FirstRun.Judge(d.Path, false, false, FixedNow);
        Assert.False(v.Show);
        Assert.Equal(new[] { name }, v.Evidence.ToArray());
        Assert.Equal("done", v.Write!.State);
        Assert.Equal("existing-profile", v.Write.How);
        Assert.Contains("this profile has been used (" + name + ")", v.Reason);
    }

    [Fact]
    public void The_log_and_chromiums_own_files_are_not_evidence()
    {
        using var d = new TestDir("welcome-log");
        d.Write("mantle.log", "x");
        d.Write("mantle-native.log", "x");
        d.Write("Local State", "{}");
        Directory.CreateDirectory(d.File("Cache"));
        var v = FirstRun.Judge(d.Path, false, false, FixedNow);
        Assert.True(v.Show);
        Assert.Empty(v.Evidence);
    }

    [Fact]
    public void The_existing_profile_flag_lists_its_evidence_like_json_stringify()
    {
        using var d = new TestDir("welcome-ev");
        d.Write("theme.json", "{}");
        d.Write("features.json", "{}");
        var v = FirstRun.Judge(d.Path, false, false, FixedNow);
        Assert.True(new WelcomeRecorder(v, d.Path, false, Scratch(d), null, FixedNow).Record().Ok);
        Assert.Equal("{\n  \"state\": \"done\",\n  \"how\": \"existing-profile\",\n  \"at\": \"2026-11-24T12:00:00.000Z\",\n  \"evidence\": [\n    \"theme.json\",\n    \"features.json\"\n  ]\n}", Flag(d));
        var next = FirstRun.Judge(d.Path, false, false, FixedNow);
        Assert.False(next.Show);
        Assert.Null(next.Write);
        Assert.Equal("this profile is done with the welcome (existing-profile, 2026-11-24T12:00:00.000Z)", next.Reason);
    }

    [Theory]
    [InlineData("not json", "the flag is not valid JSON")]
    [InlineData("42", "the flag is not an object")]
    [InlineData("null", "the flag is not an object")]
    [InlineData("{\"state\":\"weird\"}", "the flag says \"weird\", which is not a state Mantle writes")]
    [InlineData("{\"state\":\"done\",\"how\":\"maybe\"}", "the flag says \"done\" / \"maybe\", which is not a state Mantle writes")]
    [InlineData("[1]", "the flag says \"undefined\", which is not a state Mantle writes")]
    public void A_bad_flag_is_judged_by_evidence_and_the_reason_says_so(string flag, string why)
    {
        using var fresh = new TestDir("welcome-bad");
        fresh.Write(FirstRun.FlagFile, flag);
        var v = FirstRun.Judge(fresh.Path, false, false, FixedNow);
        Assert.True(v.Show);                                    // no evidence: shown
        Assert.EndsWith("; " + why, v.Reason);

        using var used = new TestDir("welcome-bad-used");
        used.Write(FirstRun.FlagFile, flag);
        used.Write("theme.json", "{}");
        var u = FirstRun.Judge(used.Path, false, false, FixedNow);
        Assert.False(u.Show);                                   // evidence wins
        Assert.EndsWith("; " + why, u.Reason);
    }

    [Fact]
    public void No_profile_folder_means_not_shown()
    {
        var v = FirstRun.Judge(null, false, false, FixedNow);
        Assert.False(v.Show);
        Assert.Equal("no profile folder was given, so nothing can be judged; the welcome is not shown", v.Reason);
        Assert.Null(v.Write);
        Assert.False(FirstRun.Judge("", false, false, FixedNow).Show);
    }

    // SAFETY (WEL-001, WEL-008): judging a used profile, even one with a damaged flag, changes nothing on disk
    [Fact]
    public void Judging_reads_only_and_changes_nothing_on_disk()
    {
        using var d = new TestDir("welcome-readonly");
        d.WriteLikeElectron("theme.json", "{\"mode\":\"dark\"}");
        d.WriteLikeElectron(@"features\clipboard.json", "{}");
        d.Write(FirstRun.FlagFile, "{\"state\":");
        var before = Snapshot(d);
        for (int i = 0; i < 3; i++) FirstRun.Judge(d.Path, false, false, FixedNow);
        FirstRun.Judge(d.Path, true, true, FixedNow);
        Unchanged(before, d);
    }

    // SAFETY (WEL-009, WEL-025): a plain --test run never shows it and never writes the flag, also through Show again / done
    [Fact]
    public void A_test_run_without_first_run_records_nothing()
    {
        using var d = new TestDir("welcome-test");
        var v = FirstRun.Judge(d.Path, test: true, optIn: false, FixedNow);
        Assert.False(v.Show);
        Assert.Null(v.Write);
        var rec = new WelcomeRecorder(v, d.Path, true, Scratch(d), null, FixedNow);
        Assert.False(rec.MayWrite);
        Assert.True(rec.Record().Ok);
        var done = rec.Done("skipped");
        Assert.True(done.Ok);
        Assert.False(done.Recorded);
        Assert.Equal("a test run without --first-run: nothing is recorded", done.Reason);
        Assert.False(rec.Closed(quitting: false).Recorded);
        Assert.Empty(Directory.GetFileSystemEntries(d.Path));

        var opt = FirstRun.Judge(d.Path, test: true, optIn: true, FixedNow);   // --first-run: judged like live
        Assert.True(opt.Show);
        Assert.True(new WelcomeRecorder(opt, d.Path, true, Scratch(d), null, FixedNow).MayWrite);
    }

    // SAFETY (never write the Electron app's files): with the gate closed the user's profile keeps its bytes
    [Fact]
    public void A_closed_gate_writes_nothing_and_says_why_in_plain_words()
    {
        using var d = new TestDir("welcome-gate");
        d.WriteLikeElectron(FirstRun.FlagFile, "{\n  \"state\": \"pending\",\n  \"at\": \"2026-01-01T00:00:00.000Z\"\n}");
        var before = Snapshot(d);
        var v = FirstRun.Judge(d.Path, false, false, FixedNow);
        var rec = new WelcomeRecorder(v, d.Path, false, new SafeFile(WriteGate.Never("this run is not --live")), null, FixedNow);
        var r = rec.Done("finished");
        Assert.False(r.Ok);
        Assert.False(r.Recorded);
        Assert.StartsWith("the first-run flag could not be saved (", r.Reason);
        Assert.Contains("this run is not --live", r.Reason);
        Unchanged(before, d);
    }

    [Fact]
    public void Done_finished_skipped_and_a_closed_window_are_recorded_and_quitting_keeps_it_pending()
    {
        using var d = new TestDir("welcome-done");
        var v = FirstRun.Judge(d.Path, false, false, FixedNow);
        var lines = new List<string>();
        var rec = new WelcomeRecorder(v, d.Path, false, Scratch(d), lines.Add, FixedNow);
        Assert.True(rec.Record().Ok);

        Assert.True(rec.Closed(quitting: true).Ok);             // WEL-023: still pending
        Assert.Contains("\"pending\"", Flag(d));

        var bad = rec.Done("later");
        Assert.False(bad.Ok);
        Assert.Equal("welcome.done takes {how: \"finished\" | \"skipped\"}", bad.Reason);

        var closed = rec.Closed(quitting: false);                // WEL-022: closed by hand = skipped, with its note
        Assert.True(closed.Recorded);
        Assert.Equal("{\n  \"state\": \"done\",\n  \"how\": \"skipped\",\n  \"at\": \"2026-11-24T12:00:00.000Z\",\n  \"note\": \"the window was closed\"\n}", Flag(d));

        var fin = rec.Done("finished");                          // WEL-020: Show again on a done profile re-records
        Assert.True(fin.Recorded);
        Assert.Equal("{\n  \"state\": \"done\",\n  \"how\": \"finished\",\n  \"at\": \"2026-11-24T12:00:00.000Z\"\n}", Flag(d));
        Assert.False(FirstRun.Judge(d.Path, false, false, FixedNow).Show);
        Assert.Contains(lines, l => l == "flag written: pending");
        Assert.Equal(new[] { FirstRun.FlagFile }, Directory.GetFileSystemEntries(d.Path).Select(f => Path.GetFileName(f)).ToArray());
    }

    // SAFETY (.unreadable): a flag Mantle cannot read is set aside before the first write, never overwritten
    [Fact]
    public void A_damaged_flag_is_set_aside_as_unreadable_before_the_flag_is_written()
    {
        using var d = new TestDir("welcome-aside");
        d.Write(FirstRun.FlagFile, "{\"state\":");
        var v = FirstRun.Judge(d.Path, false, false, FixedNow);
        Assert.True(v.Show);
        Assert.True(new WelcomeRecorder(v, d.Path, false, Scratch(d), null, FixedNow).Record().Ok);
        Assert.Equal("{\"state\":", File.ReadAllText(d.File(FirstRun.FlagFile + ".unreadable")));
        Assert.Contains("\"pending\"", Flag(d));

        // a second damaged flag never replaces the first aside copy: the write is refused instead
        d.Write(FirstRun.FlagFile, "also bad");
        var r = FirstRun.WriteFlag(Scratch(d), d.Path, new WelcomeFlag("done", "skipped", Now));
        Assert.False(r.Ok);
        Assert.Equal("{\"state\":", File.ReadAllText(d.File(FirstRun.FlagFile + ".unreadable")));
        Assert.Equal("also bad", Flag(d));
    }
}
