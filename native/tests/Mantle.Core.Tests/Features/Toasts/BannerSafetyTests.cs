using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features.Toasts;
using Xunit;

namespace Mantle.Core.Tests.Features.Toasts;

// lib/features/toasts-safety.test.js, the _toasts-safety.js unit part (TST-086..TST-091), on a fake clock and a fake script.
public class BannerSafetyTests
{
    const string Good = "  record: x\n  back    A (ShowBanner = 1)\n  1 put back to Windows' own pop-up, 0 left as they were\nverified\n";

    sealed class Kit
    {
        public readonly FakeToastClock Clock = new(1000);
        public int Owned = 1, Runs, Syncs;
        public ScriptOutput Out = new(0, Good);
        public readonly List<string> Logs = new();
        public readonly BannerSafety S;
        public Kit(int? grace = null)
        {
            S = new BannerSafety(() => Owned, () => { Runs++; return Task.FromResult(Out); }, () => { Syncs++; return Out; }, Clock, Logs.Add, null, grace);
        }
    }

    [Fact]
    public void ReadOut_reads_the_counts_and_the_verified_line()
    {
        var r = BannerSafety.ReadOut(0, Good.Replace("\n", "\r\n"));
        Assert.True(r.Ok); Assert.Equal(1, r.Back); Assert.Equal(0, r.Left);
        Assert.True(BannerSafety.ReadOut(0, "  no record: Mantle never turned off a Windows pop-up here; nothing to put back").Ok);
        var bad = BannerSafety.ReadOut(1, "  PROBLEM  Mantle.ToastTest.A could not be put back (denied)\n  0 put back to Windows' own pop-up, 1 left as they were");
        Assert.False(bad.Ok); Assert.Equal("Mantle.ToastTest.A could not be put back (denied)", bad.Why);
        Assert.Equal("the put-back script ended with code 3", BannerSafety.ReadOut(3, "").Why);
        Assert.False(BannerSafety.ReadOut(1, "verified").Ok);   // a non-zero exit is never ok
    }

    [Fact]
    public void Within_the_grace_nothing_runs_and_after_it_the_script_puts_them_back()
    {
        var k = new Kit();
        k.S.Down("the helper exited");
        k.Clock.Advance(BannerSafety.GraceMs - 1);
        Assert.Equal(0, k.Runs);
        k.Clock.Advance(1);
        Assert.Equal(1, k.Runs);
        Assert.True(k.S.State().Last!.Ok);
        k.S.Down("again");                                       // a second down after a good put-back: not run again
        k.Clock.Advance(20000);
        Assert.Equal(1, k.Runs);
    }

    [Fact]
    public void Back_inside_the_grace_runs_nothing()
    {
        var k = new Kit();
        k.S.Down("restarting");
        k.Clock.Advance(3000);
        k.S.Up();
        k.Clock.Advance(20000);
        Assert.Equal(0, k.Runs); Assert.Null(k.S.State().Down);
    }

    [Fact]
    public async Task Gave_up_runs_at_once_and_a_failed_run_is_retried_next_time()
    {
        var k = new Kit { Out = new ScriptOutput(1, "  PROBLEM  denied") };
        k.S.Down("exited");
        var r = await k.S.GaveUp("gave up");
        Assert.Equal(1, k.Runs); Assert.False(r!.Ok); Assert.Equal("denied", r.Why);
        k.Out = new ScriptOutput(0, Good);
        var r2 = await k.S.GaveUp("gave up again");
        Assert.Equal(2, k.Runs); Assert.True(r2!.Ok);
        var r3 = await k.S.GaveUp("and again");
        Assert.Equal(2, k.Runs); Assert.True(r3!.Ok);             // a good put-back since the down: not run again
    }

    [Fact]
    public async Task Nothing_owned_means_no_script()
    {
        var k = new Kit { Owned = 0 };
        var r = await k.S.GaveUp("gave up");
        Assert.Equal(0, k.Runs); Assert.True(r!.Nothing);
        Assert.False(k.S.Leave("quit"));
        Assert.Equal(0, k.Syncs);
    }

    [Fact]
    public void Leave_runs_the_script_synchronously_unless_already_back()
    {
        var k = new Kit();
        k.S.Down("exited");
        Assert.True(k.S.Leave("quit"));
        Assert.Equal(1, k.Syncs); Assert.True(k.S.State().Last!.Sync);
        Assert.False(k.S.State().Pending);
        Assert.False(k.S.Leave("quit twice"));                    // already back since the down
        Assert.Equal(1, k.Syncs);
        var k2 = new Kit { Out = new ScriptOutput(null, "", "PowerShell would not start") };
        Assert.False(k2.S.Leave("quit"));
        Assert.Contains(k2.Logs, l => l.Contains("would not run (PowerShell would not start)"));
    }

    [Fact]
    public void The_runner_passes_the_record_the_test_key_and_TestOnly()
    {
        var a = new RestoreBannersScript(@"C:\m\tools\restore-banners.ps1", @"C:\d\notif-banners.json", test: true, settingsKey: @"Software\Mantle-test-1\Settings").Args();
        Assert.Equal(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", @"C:\m\tools\restore-banners.ps1",
            "-RecordFile", @"C:\d\notif-banners.json", "-SettingsKey", @"Software\Mantle-test-1\Settings", "-TestOnly" }, a);
        var live = new RestoreBannersScript("s.ps1", "r.json", test: false).Args();
        Assert.DoesNotContain("-TestOnly", live); Assert.DoesNotContain("-SettingsKey", live);
    }

    /// The read-only promise for this lane's folders: no file write that skips SafeFile (the one gate).
    [Fact]
    public void Nothing_in_the_osd_toasts_and_notifications_code_writes_a_file_except_through_SafeFile()
    {
        string[] forbidden = { "File.Write", "File.Create", "File.Move", "File.Delete", "File.Copy", "File.Replace", "File.Append", "File.Open(",
            "FileAccess.Write", "FileMode.Create", "Directory.Create", "Directory.Delete", "StreamWriter", "WriteAllText", "WriteAllBytes",
            "OpenWrite", "AppendText", "CreateText", ".MoveTo(", ".CopyTo(", "Registry.", "RegistryKey" };
        string? src = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && src == null; d = d.Parent)
        {
            var c = Path.Combine(d.FullName, "src", "Mantle.Core", "Features");
            if (Directory.Exists(c)) src = c;
            var c2 = Path.Combine(d.FullName, "native", "src", "Mantle.Core", "Features");
            if (src == null && Directory.Exists(c2)) src = c2;
        }
        Assert.True(src != null, "the Mantle.Core Features source was not found above the tests");
        var hits = new List<string>(); int files = 0;
        foreach (var sub in new[] { "Osd", "Toasts", "Notifications" })
            foreach (var f in Directory.GetFiles(Path.Combine(src!, sub), "*.cs", SearchOption.AllDirectories))
            {
                files++;
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    var code = lines[i]; int c = code.IndexOf("//", StringComparison.Ordinal); if (c >= 0) code = code[..c];
                    foreach (var w in forbidden) if (code.Contains(w, StringComparison.Ordinal)) hits.Add($"{sub}/{Path.GetFileName(f)}:{i + 1}: {w}");
                }
            }
        Assert.True(hits.Count == 0, "writes that skip the gate (registry writes go through the sidecar): " + string.Join("; ", hits));
        Assert.True(files >= 8, "expected this lane's sources, found " + files);
    }
}
