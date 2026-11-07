using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Search;
using Mantle.Core.Tests.Search;
using Xunit;

namespace Mantle.Core.Tests.LauncherIndex;

/// <summary>The index sources one by one (LAU-115..155), on scratch folders only: never the user's Start Menu or profile.</summary>
public sealed class LauncherIndexTests
{
    static byte[] Lnk(string? rel = null, string? icon = null)
    {
        int flags = 0x80 | (rel != null ? 0x08 : 0) | (icon != null ? 0x40 : 0);
        using var ms = new MemoryStream();
        var head = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(head, 0);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(head, 4);
        BitConverter.GetBytes(flags).CopyTo(head, 0x14);
        ms.Write(head);
        foreach (var s in new[] { rel, icon }.Where(x => x != null))
        {
            ms.Write(BitConverter.GetBytes((ushort)s!.Length));
            ms.Write(System.Text.Encoding.Unicode.GetBytes(s));
        }
        return ms.ToArray();
    }

    [Fact]
    public void LnkReaderReadsIconLocationAndRelativeTarget()
    {
        using var d = new TestDir("lnk");
        string tm = d.File("Task Manager.lnk");
        File.WriteAllBytes(tm, Lnk(icon: @"%windir%\system32\Taskmgr.exe,-0"));
        var p = LnkFile.ReadFile(tm);
        Assert.Null(p.Target);
        Assert.Equal(@"%windir%\system32\Taskmgr.exe", p.IconPath);
        Assert.Equal(0, p.IconIndex);
        string st = d.File("Settings.lnk");
        File.WriteAllBytes(st, Lnk(rel: @"..\Windows\ImmersiveControlPanel\SystemSettings.exe"));
        Assert.EndsWith(@"\Windows\ImmersiveControlPanel\SystemSettings.exe", LnkFile.ReadFile(st).Target);
        Assert.Equal("taskmgr", MantleApps.StandInFor(target: null, exe: p.IconPath));
    }

    [Fact]
    public void LnkReaderRefusesInPlainWords()
    {
        var e = Assert.Throws<InvalidDataException>(() => LnkFile.Parse(new byte[10]));
        Assert.Equal("the file is too small to be a shortcut (10 bytes)", e.Message);
        var bad = Lnk(); bad[0] = 0x4D;
        Assert.StartsWith("not a shortcut: its header says 77 bytes", Assert.Throws<InvalidDataException>(() => LnkFile.Parse(bad)).Message);
    }

    [Fact]
    public void StartMenuSourceSaysWhyWhenNothingCanBeRead()
    {
        using var d = new TestDir("sm");
        var none = StartMenuApps.Build([new StartMenuRoot(d.File("nope"), "all users")]);
        Assert.Equal("error", none.Status);
        Assert.Equal("no Start Menu folder could be read: missing folder " + d.File("nope"), none.Reason);
        Directory.CreateDirectory(d.File("empty"));
        var empty = StartMenuApps.Build([new StartMenuRoot(d.File("empty"), "you")]);
        Assert.Equal("the Start Menu folders are empty or unreadable", empty.Reason);
    }

    [Fact]
    public void ProfileCommandsFollowElectronsRules()
    {
        using var d = new TestDir("ps");
        string f = d.Write(@"Documents\PowerShell\Microsoft.PowerShell_profile.ps1", "function b {}\r\n  function hidden {}\r\nfunction _x {}\r\nSet-Alias -Name a x\r\nfunction B {}\n");
        var r = ProfileCommands.Build(ProfileCommands.Candidates(d.Path, null));
        Assert.Equal(new[] { "a", "b" }, r.Items.Select(i => i.Name).ToArray());
        Assert.Equal("ok", r.Status);
        File.WriteAllText(f, "# nothing here");
        var none = ProfileCommands.Build(ProfileCommands.Candidates(d.Path, null));
        Assert.Equal("error", none.Status);
        Assert.Equal("no functions or aliases in " + f, none.Reason);
        // no profile at all is normal: zero commands, ok, no reason (LAU-136)
        var missing = ProfileCommands.Build(ProfileCommands.Candidates(d.File("elsewhere"), null));
        Assert.Equal(("ok", "", 0), (missing.Status, missing.Reason, missing.Items.Count));
    }

    [Fact]
    public void PowerRowsLeaveOutWhatPowercfgSaysIsMissing()
    {
        var states = PowerRows.ParseSleepStates("The following sleep states are available on this system:\r\n    Standby (S3)\r\n\r\nThe following sleep states are not available on this system:\r\n    Hibernate\r\n        Hibernation has not been enabled.\r\n");
        var r = PowerRows.Build(states);
        Assert.DoesNotContain(r.Items, i => i.Path == "hibernate");
        Assert.Equal("Hibernate left out: Hibernation has not been enabled.", r.Reason);
        var odd = PowerRows.ParseSleepStates("Les états de veille suivants sont disponibles");
        Assert.False(odd.Ok);
        Assert.StartsWith("powercfg /a answered in a layout this parser does not know", odd.Reason);
        Assert.Equal(new[] { "/s", "/hybrid", "/t", "0" }, PowerRows.ShutdownArgs(new SleepStates(true, FastStartup: true)));
    }

    [Fact]
    public async Task AStoreListThatNeverAnswersIsSaidInWords()
    {
        using var fx = new LauncherGoldenTests.Fixture();
        var never = new TaskCompletionSource<StoreRead>();
        var inputs = new IndexInputs
        {
            Roots = fx.Inputs.Roots, ProfileFiles = fx.Inputs.ProfileFiles, ReadSleep = fx.Inputs.ReadSleep,
            ReadStore = _ => never.Task, StoreGuard = TimeSpan.FromMilliseconds(200),
        };
        var idx = new LauncherIndexer(inputs);
        var snap = await idx.BuildAsync();     // waits at most the 4 s cap, the guard ends the read at 0.2 s
        Assert.DoesNotContain(snap.Items, i => i.Aumid != null);
        Assert.Contains("Store apps are missing: Windows did not list its Store apps in time", snap.Sources[0].Reason);
    }

    [Fact]
    public void SearchStaysUnderTenMillisecondsPerKeystroke()
    {
        var items = LauncherGoldenTests.GoldenItems();
        LauncherSearch.Search("warm", items);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int n = 0;
        foreach (var q in new[] { "s", "se", "set", "sett", "settings", "chr", "task manager", "dark mode", "example.com" }) { LauncherSearch.Search(q, items); n++; }
        double per = sw.Elapsed.TotalMilliseconds / n;
        Assert.True(per < 10, "search took " + per.ToString("0.00") + " ms per keystroke (LAU-181: under 10 ms)");
    }
}
