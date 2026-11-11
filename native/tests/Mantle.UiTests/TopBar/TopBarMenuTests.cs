using FlaUI.Core.AutomationElements;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// The dropdowns (TOP-029..082) and their keys (TOP-083..087), on the made-up Notepad++ (MANTLE_TEST_TOPBAR): the Mantle
/// menu's rows, the window growing to the menu and back to the 28 DIP bar, a pick and its dry-run words on the strip,
/// Restart's second click, the app's File menu with its Recent Files submenu and a Win32 item's dry run, a lazy menu
/// that stays empty, and the keyboard after a real click (Down, Enter, Right, Escape). Every action is a --test dry run.
/// </summary>
public partial class TopBarSurfaceTests
{
    static int Height(IntPtr bar) { FrameProbe.GetWindowRect(bar, out var r); return r.Bottom - r.Top; }
    static void Invoke(AutomationElement win, string id) => Find(win, id).AsButton().Invoke();
    static string NameOf(AutomationElement win, string id) => Find(win, id).Name;

    [TestMethod]
    public void Menus_open_grow_the_window_pick_with_dry_runs_confirm_open_submenus_and_follow_the_keys()
    {
        using var app = Launch("topbar-menus");
        IntPtr bar = Up(app);
        int pct = FrameProbe.PrimaryScale();
        int barPx = (int)Math.Round(28 * pct / 100.0, MidpointRounding.AwayFromZero);
        Host.LogContains(() => app.Log, "focus: Notepad++, ", "the made-up app's focus never reached the strip", 6000);
        var win = Host.Uia.FromHandle(bar);

        // the Mantle menu: its rows in order, and the window grows to hold it (TOP-050, TOP-074/075)
        Invoke(win, "topbar.title.mantle");
        Host.LogContains(() => app.Log, "topbar: menu opened: mantle (13 rows)", "the Mantle menu did not open", 4000);
        Assert.AreEqual("About Mantle", NameOf(win, "topbar.menu.0.0"));
        Assert.AreEqual("Hide top bar", NameOf(win, "topbar.menu.0.4"));
        Assert.AreEqual("Layouts", NameOf(win, "topbar.menu.0.6"));
        Assert.AreEqual("Shut down…", NameOf(win, "topbar.menu.0.12"));
        Assert.IsTrue(Host.Until(() => Height(bar) > barPx * 5, 4000), $"the window did not grow to the menu: {Height(bar)} px");
        Host.Screenshot("topbar-menu-mantle");
        // a pick: the menu closes first, the window shrinks back, and the strip says what About said (TOP-031, TOP-077, TOP-081)
        Invoke(win, "topbar.menu.0.0");
        Host.LogContains(() => app.Log, "topbar: menu closed (a pick; refocus yes)", "a pick did not close the menu", 4000);
        Host.LogContains(() => app.Log, "topbar: menu pick: About Mantle -> topbar.about: ok", "About Mantle was not called", 4000);
        Assert.IsTrue(Host.Until(() => Height(bar) == barPx, 4000), $"the window did not shrink back to the bar: {Height(bar)} px, want {barPx}");
        Assert.IsTrue(Find(win, "topbar.note").Name.StartsWith("Mantle ", StringComparison.Ordinal), "About's line is not on the strip");

        // Restart… arms first, then the second click is the dry run (TOP-048, TOP-051)
        Invoke(win, "topbar.title.mantle");
        Host.LogContains(() => app.Log, "topbar: menu opened: mantle", "the Mantle menu did not open again", 4000);
        Invoke(win, "topbar.menu.0.11");
        Host.LogContains(() => app.Log, "topbar: armed: Restart… (\"Click again to restart\")", "Restart… did not arm", 4000);
        Assert.IsTrue(Host.Until(() => NameOf(win, "topbar.menu.0.11") == "Click again to restart", 3000), "the armed row does not read 'Click again to restart'");
        Thread.Sleep(400);
        Invoke(win, "topbar.menu.0.11");
        Host.LogContains(() => app.Log, "topbar: menu pick: Restart… -> topbar.power: dry run (--test): would have run ", "the confirmed Restart was not a dry run", 4000);
        StringAssert.Contains(app.Log, "shutdown.exe /r /t 0");

        // the app's File menu: a submenu opens beside its row; a Win32 leaf is a dry run (TOP-061/062/064, TOP-072)
        Invoke(win, "topbar.title.m0");
        Host.LogContains(() => app.Log, "topbar: menu opened: m0 (11 rows)", "File did not open", 4000);
        Assert.AreEqual("Recent Files", NameOf(win, "topbar.menu.0.2"));
        Assert.IsFalse(Find(win, "topbar.menu.0.6").IsEnabled, "'Save a Copy As…' is disabled in the app but not dimmed here");
        Invoke(win, "topbar.menu.0.2");
        Host.LogContains(() => app.Log, "topbar: submenu opened: Recent Files (level 1, 3 rows)", "Recent Files did not open", 4000);
        var sub = Find(win, "topbar.menu.1").BoundingRectangle; var row = Find(win, "topbar.menu.0.2").BoundingRectangle;
        Assert.IsTrue(sub.Left >= row.Right - 2, $"the submenu ({sub}) is not to the right of its row ({row})");
        Invoke(win, "topbar.menu.1.0");
        Host.LogContains(() => app.Log, "topbar: menu pick: notes.txt -> topbar.invoke: dry run (--test): would have sent menu item ", "notes.txt was not a dry run", 4000);
        Assert.IsTrue(Host.Until(() => Height(bar) == barPx, 4000), "the window did not shrink back after the submenu's pick");

        // the keyboard after a real click on the glyph: Down x3, Enter runs "Windows Settings…" (TOP-083, TOP-087)
        var glyph = Find(win, "topbar.title.mantle").BoundingRectangle;
        ZOrder.Click(glyph.Left + glyph.Width / 2, glyph.Top + glyph.Height / 2);
        Host.LogContains(() => app.Log, "topbar: menu opened: mantle", "a real click on the glyph did not open its menu", 4000);
        if (Win32.GetForegroundWindow() != bar) Console.WriteLine("the click did not give the bar the foreground; asking for it");
        if (Win32.GetForegroundWindow() != bar) Assert.IsTrue(ZOrder.Foreground(bar), "the bar never took the foreground, so it cannot get keys");
        FrameProbe.Tap(0x28); FrameProbe.Tap(0x28); FrameProbe.Tap(0x28);
        Host.LogContains(() => app.Log, "topbar: key: ArrowDown", "the bar got no key", 4000);
        FrameProbe.Tap(0x0D);
        Host.LogContains(() => app.Log, "topbar: menu pick: Windows Settings… -> topbar.winSettings: dry run (--test): would have opened Windows Settings (ms-settings:)", "Enter did not run the second row", 4000);
        // Right walks to the next title; Escape closes and hands the keyboard back (TOP-084, TOP-086)
        var appT = Find(win, "topbar.title.app").BoundingRectangle;
        ZOrder.Click(appT.Left + appT.Width / 2, appT.Top + appT.Height / 2);
        Host.LogContains(() => app.Log, "topbar: title pressed: app (open;", "a real click on the app's name did not open its menu", 4000);
        FrameProbe.Tap(0x27);
        Host.LogContains(() => app.Log, "topbar: key: ArrowRight", "ArrowRight never arrived", 4000);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0"))?.Name == "m0", 3000), "ArrowRight did not walk to File");
        Assert.AreEqual("New", NameOf(win, "topbar.menu.0.0"));
        FrameProbe.Tap(0x1B);
        Host.LogContains(() => app.Log, "topbar: menu closed (Escape; refocus yes)", "Escape did not close the menu", 4000);
        Assert.IsTrue(Host.Until(() => Height(bar) == barPx, 4000), "the window did not shrink back after Escape");
        Assert.IsFalse(app.Proc.HasExited, "the app exited while its menus were used");
    }
}
