using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// The stat dropdowns (TOP-100..108, TOP-138, TOP-090) on the made-up sample (MANTLE_TEST_TOPBAR stats=ok, Electron's
/// mock): a press on CPU opens its dropdown hung from the cell's right edge and asks for its details; Open Task Manager
/// closes it and the strip says what happened (a dry run in --test); PING's dropdown names its servers and asks for
/// nothing; a second press closes it; after a real click on CPU, Right walks to the app's first menu.
/// </summary>
public partial class TopBarSurfaceTests
{
    [TestMethod]
    public void Stat_dropdowns_hang_from_their_cell_ask_for_details_open_task_manager_and_walk_to_the_titles()
    {
        using var app = Launch("topbar-stats");
        IntPtr bar = Up(app);
        int pct = FrameProbe.PrimaryScale();
        int barPx = (int)Math.Round(28 * pct / 100.0, MidpointRounding.AwayFromZero);
        Host.LogContains(() => app.Log, "focus: Notepad++, ", "the made-up app's focus never reached the strip", 6000);
        var win = Host.Uia.FromHandle(bar);

        // CPU: the header, the 8 core bars, the top processes and Open Task Manager; the details are asked for (TOP-101, TOP-103)
        Invoke(win, "topbar.stat.cpu");
        Host.LogContains(() => app.Log, "topbar: stat pressed: cpu (open; 10 rows)", "the CPU dropdown did not open with its 10 rows", 4000);
        Host.LogContains(() => app.Log, "topbar: stats detail asked: cpu (ok)", "the CPU dropdown did not ask for its details", 4000);
        Assert.AreEqual("stat:cpu", NameOf(win, "topbar.menu.0"));
        Assert.AreEqual("CPU · 23%", NameOf(win, "topbar.menu.0.0"));
        Assert.AreEqual("8 cores", NameOf(win, "topbar.menu.0.1"));
        Assert.AreEqual("Top processes", NameOf(win, "topbar.menu.0.2"));
        Assert.AreEqual("Code 11%", NameOf(win, "topbar.menu.0.3"));
        Assert.AreEqual("Open Task Manager", NameOf(win, "topbar.menu.0.9"));
        Assert.IsTrue(Host.Until(() => Height(bar) > barPx * 5, 4000), $"the window did not grow to the dropdown: {Height(bar)} px");
        // hung from the cell's right edge (TOP-100): the dropdown's box is 6 DIP (padding + border) outside its list
        var cell = Find(win, "topbar.stat.cpu").BoundingRectangle; var list = Find(win, "topbar.menu.0").BoundingRectangle;
        double inset = 6 * pct / 100.0;
        Assert.IsTrue(Math.Abs(list.Right + inset - cell.Right) <= 3, $"the dropdown ({list}) does not hang from the CPU cell's right edge ({cell})");
        Assert.IsTrue(list.Top >= cell.Bottom, $"the dropdown ({list}) is not under the strip ({cell})");
        Host.Screenshot("topbar-stat-cpu");

        // Open Task Manager: the dropdown closes, Mantle's own is asked for first, then Windows' as a dry run (TOP-108/109)
        Invoke(win, "topbar.menu.0.9");
        Host.LogContains(() => app.Log, "topbar: menu closed (Open Task Manager; refocus no)", "Open Task Manager did not close the dropdown", 4000);
        Host.LogContains(() => app.Log, "topbar: task manager: Task Manager: Mantle's did not open (", "Open Task Manager said nothing", 4000);
        StringAssert.Contains(app.Log, "dry run (--test): would have run ");
        Assert.IsTrue(Host.Until(() => Height(bar) == barPx, 4000), $"the window did not shrink back: {Height(bar)} px");
        Assert.IsTrue(Find(win, "topbar.note").Name.StartsWith("Task Manager: Mantle's did not open", StringComparison.Ordinal), "the strip does not say what Open Task Manager did");

        // PING: its server and the last pick, no Task Manager row, no details asked; a second press closes it (TOP-138)
        Invoke(win, "topbar.stat.ping");
        Host.LogContains(() => app.Log, "topbar: stat pressed: ping (open; 7 rows)", "the PING dropdown did not open with its 7 rows", 4000);
        Assert.AreEqual("Ping · 23 ms · Valorant US West (N. California)", NameOf(win, "topbar.menu.0.0"));
        Assert.AreEqual("Measured to dynamodb.us-west-1.amazonaws.com (AWS us-west-1)", NameOf(win, "topbar.menu.0.1"));
        Assert.AreEqual("US West (Oregon) 38 ms", NameOf(win, "topbar.menu.0.5"));
        Assert.IsNull(win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0.7")), "the PING dropdown has an eighth row");
        Host.Screenshot("topbar-stat-ping");
        Invoke(win, "topbar.stat.ping");
        Host.LogContains(() => app.Log, "topbar: stat pressed: ping (closed; refocus)", "a second press on PING did not close it", 4000);
        Assert.IsFalse(app.Log.Contains("stats detail asked: ping"), "the PING dropdown asked for details");

        // the keyboard after a real click on CPU: Right walks to the app's first title (TOP-090), Escape closes
        ZOrder.Click(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2);
        Host.LogContains(() => app.Log, "topbar: stat pressed: cpu (open;", "a real click on CPU did not open its dropdown", 4000);
        if (Win32.GetForegroundWindow() != bar) Assert.IsTrue(ZOrder.Foreground(bar), "the bar never took the foreground, so it cannot get keys");
        FrameProbe.Tap(0x27);
        Host.LogContains(() => app.Log, "topbar: key: ArrowRight", "ArrowRight never arrived", 4000);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0"))?.Name == "app", 3000), "ArrowRight from CPU did not walk to the app's menu");
        FrameProbe.Tap(0x1B);
        Host.LogContains(() => app.Log, "topbar: menu closed (Escape; refocus yes)", "Escape did not close the menu", 4000);
        Assert.IsFalse(app.Proc.HasExited, "the app exited while its stat dropdowns were used");
    }
}
