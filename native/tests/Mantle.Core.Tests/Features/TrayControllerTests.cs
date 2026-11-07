using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features;
using Mantle.Core.Features.Tray;
using Mantle.Core.State;
using Xunit;

namespace Mantle.Core.Tests.Features;

// main.js createTray / updateTrayNow / trayNotice / TRAY_ACTIONS / copyDiagnosticReport, driven through the
// stand-in icon a test run uses (the Win32 icon itself: Win32TrayIconTests, CI runners only).
public class TrayControllerTests
{
    sealed class Kit
    {
        public readonly Rig Rig;
        public readonly StandInTrayIcon Icon = new();
        public readonly List<string> Calls = new();
        public string? LauncherFails, Written, Copied;
        public bool GaveUp;
        public TrayController Tray = null!;
        public Kit(ManualTime t, bool test = true, Func<TrayOptions, TrayOptions>? edit = null)
        {
            Rig = new Rig(t, test);
            var o = new TrayOptions
            {
                Icon = Icon, Clock = new HostClock(t), Commands = Rig.Bus, Test = test, State = Rig.Hub, Features = Rig.Host, Log = Rig.Lines.Add,
                SidecarGaveUp = () => GaveUp,
                ShowLauncher = via => { Calls.Add($"launcher:{via}"); return LauncherFails; },
                StandDown = why => Calls.Add($"standDown:{why}"),
                Restore = why => { Calls.Add($"restore:{why}"); return Task.CompletedTask; },
                Quit = why => Calls.Add($"quit:{why}"),
                Report = via => new ValueTask<string>($"report for {via}\nline 2\n"),
                WriteTestReport = text => { Written = text; return null; },
                CopyText = text => { Copied = text; return null; },
            };
            Tray = new TrayController(edit?.Invoke(o) ?? o);
        }
        public (string Title, string Text, bool Warning) LastBalloon => Icon.Balloons[^1];
    }

    static void Cmd(TestBus bus, string name, Func<CommandCall, CommandResult> f, List<string>? seen = null) =>
        bus.Register(new CommandRegistration(name, "test", c => { seen?.Add($"{name} {c.Args.GetRawText()} {c.Caller}"); return new ValueTask<CommandResult>(f(c)); }));

    [Fact]
    public void Start_PutsTheMenuUp_ATestRunDrawsNoIcon_AndTheTipSaysTestRun() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        Assert.Null(k.Tray.Start());
        Assert.False(k.Icon.Draws);
        Assert.Equal("Mantle (test run)\nRunning normally", k.Icon.Tip);
        Assert.Contains(k.Icon.Menu, i => i.Id == "quit");
        Assert.True(k.Rig.Logged("a stand-in tray keeps the menu and draws no icon"));
        Assert.Equal("mantle: test-tray: Mantle is running normally (text) | --- | Open launcher | Wallpapers… | --- | Copy diagnostic report | Restart Mantle | Put Windows back (keep Mantle open) | --- | Quit Mantle",
            k.Tray.TestTrayLine());
        await Task.CompletedTask;
    });

    [Fact]
    public void Start_AnIconThatCannotBeMade_IsLogged_AndTheShortcutsSaySo() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t, edit: o => new TrayOptions { Icon = new FailingIcon(), Clock = o.Clock, Commands = o.Commands, State = o.State, Log = o.Log, Test = o.Test });
        Assert.Equal("no notification area", k.Tray.Start());
        Assert.True(k.Rig.Logged("could not create the tray icon: no notification area"));
        Assert.EndsWith("the tray icon could not be created either (no notification area)", k.Rig.Hub.Current.Shortcuts.Reason);
        await Task.CompletedTask;
    });

    sealed class FailingIcon : ITrayIcon
    {
        public Action<TrayItem>? Chosen { get; set; }
        public bool Draws => true;
        public string? Create(string tip) => "no notification area";
        public void SetTip(string tip) { }
        public void SetMenu(IReadOnlyList<TrayItem> items) { }
        public void Balloon(string title, string text, bool warning) { }
        public void PopUp() { }
        public void Dispose() { }
    }

    [Fact]
    public void ARebuildWaitsForTheWindow_AndAnUnchangedMenuIsNotSetAgain() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        k.Tray.Start();
        var sets = k.Icon.MenuSets;
        k.Tray.Request(); k.Tray.Request();
        t.AdvanceMs(300); loop.Drain();
        Assert.Equal(sets, k.Icon.MenuSets);   // nothing moved
        k.GaveUp = true;
        k.Tray.Request();
        Assert.Equal(sets, k.Icon.MenuSets);   // never inside the call that asked
        t.AdvanceMs(300); loop.Drain();
        Assert.Equal(sets + 1, k.Icon.MenuSets);
        Assert.Contains(k.Icon.Menu, i => i.Id == "retry" && i.Label == "Reconnect Mantle to Windows");
        await Task.CompletedTask;
    });

    [Fact]
    public void Launcher_AndWallpapers_RunTheirActions_AndAFailureIsABalloonInWords() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        k.Tray.Start();
        Assert.True(k.Icon.Choose("launcher"));
        Assert.Equal("launcher:the tray menu", k.Calls[^1]);
        Assert.Empty(k.Icon.Balloons);
        k.LauncherFails = "the sidecar did not answer";
        k.Icon.Choose("launcher");
        Assert.Equal(("The launcher did not open", "Mantle's link to Windows did not answer", true), k.LastBalloon);
        Assert.True(k.Rig.Logged("tray: The launcher did not open: the sidecar did not answer"));   // the log keeps the exact words
        k.Icon.Choose("wallpapers");   // not wired: says so, never silent
        Assert.Equal(("Wallpapers did not open", "the wallpaper library is not part of this build yet", true), k.LastBalloon);
        await Task.CompletedTask;
    });

    [Fact]
    public void Settings_Restart_Reconnect_Update_GoThroughTheBus_AndSayWhyWhenTheyFail() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        var seen = new List<string>();
        k.Tray.Start();
        k.Tray.Choose(new TrayItem(TrayItemKind.Normal, "Open Mantle Settings", "settings"));
        loop.Drain();
        Assert.Equal(("Mantle Settings did not open", "the Settings feature is turned off", true), k.LastBalloon);
        Cmd(k.Rig.Bus, "settings.open", _ => CommandResult.Fail("Settings is resting while VALORANT runs"), seen);
        Cmd(k.Rig.Bus, "shell.restart", _ => CommandResult.Success(JsonDocument.Parse("{\"ok\":false,\"reason\":\"a restore is running\"}").RootElement), seen);
        Cmd(k.Rig.Bus, "shell.reconnect", _ => CommandResult.Fail("the sidecar exe is missing"), seen);
        Cmd(k.Rig.Bus, "updates.show", _ => CommandResult.Success(), seen);
        k.Tray.Force();
        Assert.Contains(k.Icon.Menu, i => i.Id == "settings" && i.Accelerator == "Super+I");
        k.Icon.Choose("settings"); loop.Drain();
        Assert.Equal(("Mantle Settings did not open", "Settings is resting while VALORANT runs", true), k.LastBalloon);
        Assert.Equal("settings.open {\"via\":\"the tray menu\"} tray", seen[^1]);
        k.Icon.Choose("restart"); loop.Drain();
        Assert.Equal(("Mantle did not restart", "a restore is running", true), k.LastBalloon);
        k.Tray.Choose(new TrayItem(TrayItemKind.Normal, "Reconnect Mantle to Windows", "retry")); loop.Drain();
        Assert.Equal(("Mantle could not reconnect to Windows", "Mantle's link to Windows exe is missing", true), k.LastBalloon);
        var n = k.Icon.Balloons.Count;
        k.Tray.Choose(new TrayItem(TrayItemKind.Normal, "Update", "update")); loop.Drain();
        Assert.Equal(n, k.Icon.Balloons.Count);   // it worked: no notice
        Assert.Equal("updates.show {} tray", seen[^1]);
        await Task.CompletedTask;
    });

    [Fact]
    public void Quit_IsTheShutdownRoad_AndPutWindowsBack_StandsDownFirst_ThenShowsThePausedMenu() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        k.Tray.Start();
        k.Icon.Choose("quit");
        Assert.Equal("quit:the tray menu", k.Calls[^1]);
        k.Calls.Clear();
        k.Icon.Choose("restore"); loop.Drain();
        Assert.Equal(new[] { "standDown:Windows was put back from the tray menu (\"Restore Windows taskbar\")", "restore:the tray menu asked for it" }, k.Calls);
        Assert.True(k.Tray.Paused);
        Assert.Equal("Paused: Windows is back to normal", k.Icon.Menu[0].Label);
        Assert.Contains(k.Icon.Menu, i => i.Id == "restart" && i.Label == "Restart Mantle to resume");
        Assert.DoesNotContain(k.Icon.Menu, i => i.Id == "restore");
        Assert.DoesNotContain(k.Icon.Menu, i => i.Label.Contains("hide", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    });

    [Fact]
    public void Report_ATestRunWritesItsProfile_NeverTheClipboard_LiveCopiesAndSaysSo() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        k.Tray.Start();
        k.Icon.Choose("report"); loop.Drain();
        Assert.Equal("report for the tray menu\nline 2\n", k.Written);
        Assert.Null(k.Copied);
        Assert.True(k.Rig.Logged("diagnostic report (3 lines, asked from the tray menu) written to the test profile, not the clipboard (test run)"));
        Assert.Empty(k.Icon.Balloons);

        var live = new Kit(t, test: false);
        live.Tray.Start();
        live.Icon.Choose("report"); loop.Drain();
        Assert.Equal("report for the tray menu\nline 2\n", live.Copied);
        Assert.Null(live.Written);
        Assert.Equal((TrayReport.CopiedTitle, TrayReport.CopiedText, false), live.LastBalloon);

        var broken = new Kit(t, test: false, edit: o => new TrayOptions { Icon = o.Icon, Clock = o.Clock, Commands = o.Commands, Test = false, Log = o.Log, Report = o.Report, CopyText = _ => "another app is holding the clipboard" });
        broken.Tray.Start();
        broken.Icon.Choose("report"); loop.Drain();
        Assert.Equal(("The report was not copied", "another app is holding the clipboard", true), broken.LastBalloon);
        await Task.CompletedTask;
    });

    [Fact]
    public void AFeatureRow_SwitchesTheFeature_AndARefusalIsABalloon() => HostLoop.Run(async (loop, t) =>
    {
        var k = new Kit(t);
        k.Rig.Host.Load(new[] { k.Rig.D("osd", label: "Volume pop-up") });
        await k.Rig.Host.Installed;
        k.Tray.Start();
        var feats = k.Icon.Menu.Single(i => i.Kind == TrayItemKind.Submenu).Submenu!;
        var row = feats.Single(i => i.Id == "feature:osd");
        Assert.True(row.Checked);
        k.Icon.Choose("feature:osd"); loop.Drain();
        Assert.False(k.Rig.Host.List().Single().Enabled);
        t.AdvanceMs(300); loop.Drain();
        Assert.False(k.Icon.Menu.Single(i => i.Kind == TrayItemKind.Submenu).Submenu!.Single(i => i.Id == "feature:osd").Checked);
        k.Tray.Choose(new TrayItem(TrayItemKind.Checkbox, "Gone", "feature:nope", Checked: false));
        Assert.Equal("Gone was not switched on", k.LastBalloon.Title);
        k.Tray.Choose(new TrayItem(TrayItemKind.Normal, "Odd", "odd"));
        Assert.Equal(("That menu item did nothing", "no action is wired for \"odd\"", true), k.LastBalloon);
    });
}
