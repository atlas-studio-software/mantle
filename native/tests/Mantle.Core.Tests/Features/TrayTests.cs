using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features;
using Mantle.Core.Features.Tray;
using Mantle.Core.Keys;
using Xunit;

namespace Mantle.Core.Tests.Features;

// lib/main-rel.test.js (tray and report regions), lib/tray-batch.test.js and lib/tray-icon.js, ported.
public class TrayModelTests
{
    static TrayInputs In(IReadOnlyList<TrayProblem>? problems = null, bool paused = false, bool test = false, string status = "ok", bool gaveUp = false,
        bool launcher = true, bool kill = true, bool settings = true, IReadOnlyList<TrayItem>? feats = null, string? recovery = null, string? update = null) =>
        new(test, paused, problems ?? Array.Empty<TrayProblem>(), recovery, status, gaveUp, launcher, kill, settings, feats ?? Array.Empty<TrayItem>(), update);

    [Fact]
    public void Health_RunningNormally_Paused_AndATestRunSaysSoInTheTip()
    {
        var ok = TrayModel.Health(null, false, false, "ok", false);
        Assert.Equal("Mantle is running normally", ok.Head);
        Assert.Equal("Mantle\nRunning normally", ok.Tip);
        var paused = TrayModel.Health(null, true, true, "ok", false);
        Assert.Equal("Paused: Windows is back to normal", paused.Head);
        Assert.Equal(new[] { "Restart Mantle to use it again" }, paused.Lines);
        Assert.Equal("Mantle (test run)\nPaused: Windows is back to normal", paused.Tip);
    }

    [Fact]
    public void Health_UpToFourLines_InWords_ThenAndNMore()
    {
        var ps = new[] { "taskbar", "edge clip", "bar backdrop", "window glass", "tray", "shortcuts" }.Select(p => new TrayProblem(p, "the thing failed badly; more words here")).ToArray();
        var h = TrayModel.Health(ps, false, false, "ok", false);
        Assert.Equal("6 things need a look", h.Head);
        Assert.Equal("Taskbar auto-hide: the thing failed badly", h.Lines[0]);
        Assert.Equal("Bottom-edge guard: the thing failed badly", h.Lines[1]);
        Assert.Equal("Bar background: the thing failed badly", h.Lines[2]);
        Assert.Equal("…and 2 more, listed in Mantle Settings", h.Lines[4]);
        Assert.True(h.Tip.Length <= 120);
        Assert.Equal("1 thing needs a look", TrayModel.Health(ps.Take(1).ToArray(), false, false, "ok", false).Head);
    }

    [Fact]
    public void Health_ADownSidecarIsOneLine_WithWhatWaitsOnIt()
    {
        var ps = new[] { new TrayProblem("sidecar", "it crashed"), new TrayProblem("taskbar", "waiting: it crashed"), new TrayProblem("tray", "no icons") };
        var h = TrayModel.Health(ps, false, false, "down", false);
        Assert.Equal("2 things need a look", h.Head);
        Assert.Equal("Mantle's link to Windows stopped; trying again (1 thing waits on it)", h.Lines[0]);
        Assert.Equal("Notification-area icons: no icons", h.Lines[1]);
        Assert.StartsWith("Mantle's link to Windows is starting", TrayModel.Health(ps, false, false, "starting", false).Lines[0]);
        Assert.StartsWith("Mantle's link to Windows stopped (", TrayModel.Health(ps, false, false, "down", true).Lines[0]);
    }

    [Fact]
    public void Words_ReplaceTheJargon()
    {
        Assert.Equal("Mantle's link to Windows did not install the Win key watcher", TrayModel.Words("the sidecar did not install the Win key hook"));
        Assert.Equal("the keyboard watcher failed", TrayModel.Words("the low-level keyboard hook failed (HRESULT 0x80070005)"));
        Assert.Equal("an error on window 5 in the page", TrayModel.Words("0x80004005 on hwnd 5 in the renderer"));
        Assert.Equal("Mantle's reply", TrayModel.Words("the sidecar's reply"));
    }

    [Fact]
    public void Template_TheOrder_TheHints_AndNeverAHideTheTaskbarItem()
    {
        var feats = TrayModel.FeatureItems(new[] { new FeatureTrayRow("clip", "Clipboard history", true, true, "its hook failed"), new FeatureTrayRow("osd", "Volume pop-up", false, true, null) });
        var t = TrayModel.Template(In(feats: feats));
        var ids = t.Where(i => i.Id != null).Select(i => i.Id).ToArray();
        Assert.Equal(new[] { "launcher", "settings", "wallpapers", "report", "restart", "restore", "quit" }, ids);
        Assert.Equal("Alt+Space", t.Single(i => i.Id == "launcher").Accelerator);
        Assert.Equal("Super+I", t.Single(i => i.Id == "settings").Accelerator);
        Assert.Equal("Ctrl+Alt+Shift+Backspace", t.Single(i => i.Id == "quit").Accelerator);
        var sub = t.Single(i => i.Kind == TrayItemKind.Submenu).Submenu!;
        Assert.Equal("Clipboard history", sub[0].Label);
        Assert.True(sub[0].Checked);
        Assert.Equal("      its keyboard watcher failed", sub[1].Label);
        Assert.DoesNotContain(t, i => i.Label.Contains("hide", StringComparison.OrdinalIgnoreCase));
        var noKeys = TrayModel.Template(In(launcher: false, kill: false, settings: false));
        Assert.Null(noKeys.Single(i => i.Id == "launcher").Accelerator);
        Assert.Null(noKeys.Single(i => i.Id == "quit").Accelerator);
        Assert.DoesNotContain(noKeys, i => i.Id == "settings");
    }

    [Fact]
    public void Template_ReconnectSitsUnderTheFault_PausedHidesPutWindowsBack_RecoveryAndUpdateRows()
    {
        var gave = TrayModel.Template(In(new[] { new TrayProblem("sidecar", "gave up") }, status: "down", gaveUp: true, recovery: "Restarted by itself after an error at 09:41", update: "Mantle 1.2 is ready"));
        Assert.Equal("retry", gave[2].Id);
        Assert.Equal("Reconnect Mantle to Windows", gave[2].Label);
        Assert.Equal("    Restarted by itself after an error at 09:41", gave[3].Label);
        Assert.Equal("update", gave[4].Id);
        var paused = TrayModel.Template(In(paused: true, gaveUp: true));
        Assert.DoesNotContain(paused, i => i.Id == "restore" || i.Id == "retry");
        Assert.Equal("Restart Mantle to resume", paused.Single(i => i.Id == "restart").Label);
    }

    [Fact]
    public void Describe_IsTheTestTrayLog()
    {
        var lines = TrayModel.Describe(TrayModel.Template(In(feats: TrayModel.FeatureItems(new[] { new FeatureTrayRow("a", "A", true, true, null) }))));
        Assert.Equal("Mantle is running normally (text)", lines[0]);
        Assert.Contains("Open launcher [Alt+Space]", lines);
        Assert.Contains("Features (1 inside)", lines);
        Assert.Contains("  A (on)", lines);
        Assert.Contains("Quit Mantle [Ctrl+Alt+Shift+Backspace]", lines);
    }

    [Fact]
    public void Report_HasEverySectionInWords_AndGoesThroughTheScrubber()
    {
        var snap = new ReportSnapshot("2026-11-07 09:00", "1.2.3", "abc1234", ".NET 10.0", "10.0.26100", false, 3900,
            new[] { new ReportDisplay(1920, 1080, 1.5, true) }, (300, 3, 120), TrayModel.Health(new[] { new TrayProblem("tray", "no icons") }, false, false, "ok", false),
            "Restarted by itself after an error at 08:00", "ok", 0, false, 5, 2, new[] { "Clipboard history" }, new[] { "08:59:01 something went wrong" });
        var text = TrayReport.Build(snap, s => s.Replace("abc1234", "COMMIT"));
        Assert.StartsWith("Mantle diagnostic report\n", text);
        Assert.Contains("Mantle 1.2.3 (COMMIT), running live, up 1 h 5 min", text);
        Assert.Contains("Displays: 1920x1080 at 150% (main)", text);
        Assert.Contains("Memory: 300 MB in 3 processes (Mantle's main process 120 MB)", text);
        Assert.Contains("Health: 1 thing needs a look\n  - Notification-area icons: no icons\n  - Restarted by itself", text);
        Assert.Contains("Link to Windows: ok", text);
        Assert.Contains("Features: 5 on, 2 off; not working: Clipboard history", text);
        Assert.Contains("Last 1 warnings and errors this run, oldest first:\n  08:59:01 something went wrong", text);
        var recent = new TrayReport.RecentLines(2, () => new DateTime(2026, 11, 7, 9, 5, 7));
        recent.Add("one\ntwo\n\nthree " + new string('x', 300));
        Assert.Equal(2, recent.List().Count);
        Assert.StartsWith("09:05:07 two", recent.List()[0]);
        Assert.Equal(9 + 240, recent.List()[1].Length);
    }

    [Fact]
    public void Icon_IsTheIonRoundedSquare_PremultipliedBgra()
    {
        var px = TrayReport.IconBitmap(16);
        Assert.Equal(16 * 16 * 4, px.Length);
        var c = (8 * 16 + 8) * 4;
        Assert.Equal(new byte[] { 0xCA, 0xAD, 0x72, 0xFF }, px[c..(c + 4)]);
        Assert.Equal(0, px[3]);   // the corner is clear
        Assert.Equal(32 * 32 * 4, TrayReport.IconBitmap(32).Length);
    }
}

public class TrayBatchTests
{
    sealed class Box { public object?[] Key = { "a" }; public int Runs; public bool Throw; }

    static (TrayBatch B, ManualTime T, Box X, List<string> Errors) Make()
    {
        var t = new ManualTime();
        var x = new Box();
        var errors = new List<string>();
        var b = new TrayBatch(HostClock.Inline(t), () => x.Key, () => { if (x.Throw) throw new InvalidOperationException("menu"); x.Runs++; }, (e, w) => errors.Add($"{w}: {e.Message}"));
        return (b, t, x, errors);
    }

    [Fact]
    public void AnAskNeverRebuildsInsideTheCall_ABurstIsAFewRebuilds_AndNeverTwoWithin250ms()
    {
        var (b, t, x, _) = Make();
        b.Request();
        Assert.Equal(0, x.Runs);
        t.AdvanceMs(0);
        Assert.Equal(1, x.Runs);
        for (var i = 0; i < 60; i++) { x.Key = new object?[] { i }; b.Request(); t.AdvanceMs(10); }
        Assert.InRange(x.Runs, 2, 5);
        t.AdvanceMs(250);
        Assert.False(b.Pending);
        Assert.Equal(61, b.Requests);
    }

    [Fact]
    public void SameKeyIsSkipped_ForceRebuildsAtOnce_AndAThrowIsReportedAndTriedAgain()
    {
        var (b, t, x, errors) = Make();
        b.Request(); t.AdvanceMs(300);
        b.Request(); t.AdvanceMs(300);
        Assert.Equal(1, x.Runs);
        Assert.Equal(1, b.Skipped);
        b.Force();
        Assert.Equal(2, x.Runs);
        x.Throw = true;
        b.Force();
        Assert.Equal(new[] { "run: menu" }, errors);
        x.Throw = false;
        b.Request(); t.AdvanceMs(300);
        Assert.Equal(3, x.Runs);   // tried again on the next ask, same key or not
        Assert.True(TrayBatch.SameKey(new object?[] { "a", 1 }, new object?[] { "a", 1 }));
        Assert.False(TrayBatch.SameKey(new object?[] { new object() }, new object?[] { new object() }));
    }
}

public class ShellShortcutsTests
{
    sealed class Keys : IGlobalHotkeys
    {
        public readonly HashSet<string> Taken = new();
        public readonly List<string> Registered = new();
        public bool Register(string accelerator, Action fired) { if (Taken.Contains(accelerator)) return false; Registered.Add(accelerator); return true; }
        public void UnregisterAll() => Registered.Clear();
    }

    [Fact]
    public void TestModeRegistersNothing_AndSaysWhy()
    {
        var k = new Keys();
        var p = ShellShortcuts.Register(true, k, () => { }, () => { });
        Assert.False(p.KillSwitch || p.Launcher);
        Assert.Empty(k.Registered);
        Assert.StartsWith("test mode: no global shortcuts were registered", p.Reason);
    }

    [Fact]
    public void LiveRegistersBoth_AndATakenKeyIsSaidInWords()
    {
        var ok = ShellShortcuts.Register(false, new Keys(), () => { }, () => { });
        Assert.True(ok.KillSwitch && ok.Launcher);
        Assert.Equal("Ctrl+Alt+Shift+Backspace puts Windows back, Alt+Space opens the launcher", ok.Reason);
        var k = new Keys();
        k.Taken.Add("Alt+Space"); k.Taken.Add("Ctrl+Alt+Shift+Backspace");
        var bad = ShellShortcuts.Register(false, k, () => { }, () => { });
        Assert.Equal("Ctrl+Alt+Shift+Backspace is already taken by another app, so the kill switch is NOT armed; Alt+Space is already taken by another app, so it will not open the launcher", bad.Reason);
        Assert.EndsWith("the tray icon could not be created either (no shell)", ShellShortcuts.NoTray(bad, "no shell").Reason);
    }
}
