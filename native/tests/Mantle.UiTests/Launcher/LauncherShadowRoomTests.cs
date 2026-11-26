using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Launcher;

/// <summary>
/// Lane r16-trial-fixes: the launcher's window is larger than its island (the room for the float-hi shadow, ShadowRoom
/// 44/12/44/88 DIP in Theme/LvLauncher.xaml). A click in that room is outside the island, so it closes the launcher as
/// an outside click does; a click on the island does not. Real (injected) clicks, read from outside.
/// </summary>
public partial class LauncherTests
{
    [TestMethod]
    public void A_click_in_the_shadow_room_below_the_island_closes_the_launcher_and_a_click_on_the_island_does_not()
    {
        using var app = Launch("uitest-launcher-room", keys: false);
        var h = LauncherHwnd(app);
        Open(app, h);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: first frame with rows "), 5000), "no first frame with rows after the open:\n" + Tail(app.Log));
        var win = Host.Uia.FromHandle(h);
        var panel = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("panel")), 5000);
        Assert.IsNotNull(panel, "no panel group in the launcher");
        double s = Win32.GetDpiForWindow(h) / 96.0;
        var isl = panel!.BoundingRectangle;
        var wr = win.BoundingRectangle;
        int x = (int)Math.Round(isl.Left + isl.Width / 2.0), y = (int)Math.Round(isl.Bottom + 40 * s);
        if (y >= wr.Bottom)
            Assert.Inconclusive($"the launcher's window ends {(wr.Bottom - isl.Bottom) / s:0} DIP below its pane (the glass look's inset, not the islands look's shadow room), so 40 DIP below the pane is not in its window");

        // a click on the island (the key hints in the foot: no row, so nothing is launched) leaves it open
        var hint = win.FindFirstDescendant(cf => cf.ByAutomationId("hint"));
        Assert.IsNotNull(hint, "no key hint (hint) in the footer");
        var p = Mid(hint!);
        ZOrder.Click(p.X, p.Y);
        var q = win.FindFirstDescendant(cf => cf.ByAutomationId("q"));
        Assert.IsNotNull(q, "no search line (q)");
        p = Mid(q!);
        ZOrder.Click(p.X, p.Y);
        Thread.Sleep(800);
        Assert.IsTrue(Win32.IsWindowVisible(h), "a click on the island closed the launcher:\n" + Tail(app.Log));
        Assert.IsFalse(app.Log.Contains("launcher: hidden ("), "a click on the island hid the launcher:\n" + Tail(app.Log));

        // a click in the shadow room, 40 DIP below the island's bottom edge and centred across it, closes it
        ZOrder.Click(x, y);
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (a click outside the island)"), 5000),
            $"a click in the shadow room at ({x}, {y}), 40 DIP below the island, did not close the launcher:\n" + Tail(app.Log));

        // and it opens again as before
        Open(app, h);
        Assert.AreEqual(1, AllWindows(app.Proc.Id, Title).Count, "closing from the shadow room made or lost a launcher window");
    }
}
