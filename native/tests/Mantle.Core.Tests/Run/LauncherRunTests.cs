using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Run;
using Mantle.Core.Tests.Search;
using Xunit;

namespace Mantle.Core.Tests.Run;

/// <summary>Launch by id (LAU-186 and the rows part 1 opens). Every case is a dry run except one, which runs a scratch script it made.</summary>
public sealed class LauncherRunTests
{
    static LauncherItem Item(string kind, string name, string path) => new() { Id = Js.StableId(kind, path), Kind = kind, Name = name, Path = path };

    [Fact]
    public void DryRunsSayWhatTheyWouldRun()
    {
        var store = new LauncherItem { Id = "app:x", Kind = "app", Name = "Calculator", Path = @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", Aumid = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" };
        Assert.Equal(@"dry run: would run explorer.exe shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", LauncherLaunch.Launch(store, true).Reason);
        var evil = store with { Aumid = "x y\" & calc" };
        Assert.Equal("that Store app has an id Mantle does not trust, so it was not opened", LauncherLaunch.Launch(evil, true).Reason);
        Assert.Equal(@"dry run: would open C:\nope\Gone.lnk (but the file is missing)", LauncherLaunch.Launch(Item("app", "Gone", @"C:\nope\Gone.lnk"), true).Reason);
        Assert.StartsWith("dry run: would run ShellExecute(\"ms-settings:privacy-webcam\")", LauncherLaunch.Launch(Item("setting", "Camera", "ms-settings:privacy-webcam"), true).Reason);
        // a page Mantle Settings covers opens Windows' own natively for now, and says why
        Assert.Equal("dry run: would run ShellExecute(\"ms-settings:display\") — opened Windows Settings: Mantle Settings is not running",
            LauncherLaunch.Launch(Item("setting", "Display", "ms-settings:display"), true).Reason);
        Assert.Equal("dry run: would open Mantle's wallpapers window", LauncherLaunch.Launch(Item("setting", "Wallpapers", "mantle:wallpapers"), true).Reason);
        Assert.Equal("Restart runs through launcher.power (which asks first), not apps.launch", LauncherLaunch.Launch(Item("power", "Restart", "restart"), true).Reason);
        Assert.Equal("that row has no item attached (a bug in the launcher)", LauncherLaunch.Launch(null, true).Reason);
    }

    [Fact]
    public async Task ServiceFindsByIdAndTestRunsAreAlwaysDry()
    {
        using var fx = new LauncherGoldenTests.Fixture();
        var svc = new LauncherService(fx.Inputs, test: true);
        var snap = await svc.IndexAsync();
        Assert.Same(snap, await svc.IndexAsync());                 // kept: no second build within 10 minutes
        var hits = svc.Search("calc");
        Assert.Equal("Calculator", hits[0].Item.Name);
        var r = svc.Launch(hits[0].Item.Id);
        Assert.True(r.Ok && r.DryRun, r.Reason);
        // a typed URL exists only in the last search's hits
        var url = svc.Search("example.com")[0].Item;
        Assert.Equal("url", url.Kind);
        Assert.True(svc.Launch(url.Id).DryRun);
        Assert.Equal("nothing in the index has the id \"app:nope\"", svc.Launch("app:nope").Reason);
    }

    [Fact]
    public async Task CommandsAnswerWithElectronsNames()
    {
        using var fx = new LauncherGoldenTests.Fixture();
        var cmds = new LauncherCommands(new LauncherService(fx.Inputs, test: true));
        JsonElement Args(JsonObject o) => JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
        var idx = await cmds.Index(new CommandCall("apps.index", Args(new JsonObject()), "launcher", CancellationToken.None));
        Assert.True(idx.Ok, idx.Error);
        Assert.Equal(4, idx.Result.GetProperty("sources").GetArrayLength());
        var s = await cmds.SearchCmd(new CommandCall("apps.search", Args(new JsonObject { ["q"] = "settings" }), "launcher", CancellationToken.None));
        var names = s.Result.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Equal("Mantle Settings", names[0]);
        var l = await cmds.LaunchCmd(new CommandCall("apps.launch", Args(new JsonObject()), "launcher", CancellationToken.None));
        Assert.False(l.Ok);
    }

    /// The one live launch: a script this test wrote, opened the way a shortcut is (ShellExecute). It writes a
    /// marker file and exits. Runs on the CI runner only (CI / GITHUB_ACTIONS set): never on a desktop in use.
    [Fact]
    public async Task ALiveLaunchOpensTheFileTheRowPointsAt()
    {
        if (Environment.GetEnvironmentVariable("CI") == null && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == null) return;
        using var d = new TestDir("run");
        string marker = d.File("ran.txt");
        string script = d.Write("launch-fixture.cmd", "@echo off\r\necho ran> \"%~dp0ran.txt\"\r\nexit /b 0\r\n");
        var r = LauncherLaunch.Launch(Item("app", "Launch fixture", script), dryRun: false);
        Assert.True(r.Ok, r.Reason);
        for (int i = 0; i < 100 && !File.Exists(marker); i++) await Task.Delay(100);
        Assert.True(File.Exists(marker), "the launched fixture did not write its marker within 10 s");
        if (r.Pid is int pid)
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); if (!p.HasExited) p.Kill(); }
            catch (ArgumentException) { /* already gone */ }
    }
}
