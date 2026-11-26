using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Launcher;

/// <summary>
/// Lane r15-quick-launcher: the launcher in the islands look (Mantle.App Surfaces/Launcher/LauncherSurface.Islands.cs), to
/// the approved prototype apps-r3/mantle-bar panels/launcher.css (L3 the island, L7 to L10 the field, L30 to L38 the rows
/// and their tick). Sizes are checked in DIP through the window's dpi; the numbers themselves are pinned in unit
/// (Mantle.Core.Tests LauncherSpecTests). No pixel compare.
/// </summary>
public partial class LauncherTests
{
    static string Status(AutomationElement? e) => e == null ? "(missing)" : e.Properties.ItemStatus.ValueOrDefault ?? "";

    /// Each row's tick state, top to bottom ("on" on the selected row, "" elsewhere).
    static List<string> Ticks(AutomationElement win) => Rows(win).Select(r => Status(r.FindFirstDescendant(cf => cf.ByAutomationId("row-tick")))).ToList();

    [TestMethod]
    public void The_islands_launcher_is_one_centred_600_by_520_island_with_the_field_line_and_the_selected_rows_tick()
    {
        using var app = Launch("uitest-launcher-look", keys: true);
        var h = LauncherHwnd(app);
        Open(app, h);
        if (!Host.Until(() => app.Log.Contains("launcher: islands look: one island "), 5000))
            Assert.Inconclusive("the launcher wears the old glass look in this run (MANTLE_TEST_GLASS=electron turns the islands look off), so the islands checks did not run");
        var win = Host.Uia.FromHandle(h);
        var panel = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("panel")), 5000);
        Assert.IsNotNull(panel, "no panel group in the launcher");

        // L3: min(600px, 100vw - 24px) x min(520px, 100vh - 80px), centred on the primary display (LAU-002)
        double s = Win32.GetDpiForWindow(h) / 96.0;
        var mon = Win32.MonitorRect(h);
        double pw = Math.Min(600, (mon.Right - mon.Left) / s - 24), ph = Math.Min(520, (mon.Bottom - mon.Top) / s - 80);
        var r = panel!.BoundingRectangle;
        Assert.AreEqual(pw * s, r.Width, 1.5, $"the island is {r.Width} px wide, want {pw} DIP");
        Assert.AreEqual(ph * s, r.Height, 1.5, $"the island is {r.Height} px tall, want {ph} DIP");
        Assert.AreEqual((mon.Left + mon.Right) / 2.0, r.Left + r.Width / 2.0, 2.0, "the island is not centred across the primary display");
        Assert.AreEqual((mon.Top + mon.Bottom) / 2.0, r.Top + r.Height / 2.0, 2.0, "the island is not centred down the primary display");
        Host.LogContains(() => app.Log, "rows 36 (Quick links 32), the float-hi shadow round it");

        // L9, L10: the accent line 16 in from each side, on while the query has the keyboard (FIXES-2: no ring, a hair line)
        var line = win.FindFirstDescendant(cf => cf.ByAutomationId("field-line"));
        Assert.IsNotNull(line, "no field-line (the accent line under the query)");
        var q = win.FindFirstDescendant(cf => cf.ByAutomationId("q"));
        Assert.IsNotNull(q, "no search line (q)");
        Mouse.Click(Mid(q!));
        Thread.Sleep(300);
        Assert.IsTrue(Host.Until(() => Status(line) == "focused", 3000), $"the field's accent line did not come on with the keyboard in the query (ItemStatus \"{Status(line)}\")");
        var lr = line!.BoundingRectangle;
        Assert.AreEqual((pw - 32) * s, lr.Width, 1.5, $"the accent line is {lr.Width} px wide, want the island less 16 DIP each side");
        Assert.AreEqual(1 * s, lr.Height, 1.0, $"the accent line is {lr.Height} px tall, want 1 DIP");

        // L30, L34, L36: rows 36 tall; the selected row, and only it, wears its 3 x 14 accent tick
        Keyboard.Type("zephyr");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: typed \"zephyr\": 4 row(s) on screen ") && Rows(win).Count == 4, 5000), "\"zephyr\" did not list its 4 rows:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Ticks(win).SequenceEqual(new[] { "on", "", "", "" }), 3000), "after typing, the tick is not on row 1 alone: " + string.Join(" | ", Ticks(win)));
        var row = Rows(win)[0].BoundingRectangle;
        Assert.AreEqual(36 * s, row.Height, 1.0, $"a row is {row.Height} px tall, want 36 DIP");
        var tick = Rows(win)[0].FindFirstDescendant(cf => cf.ByAutomationId("row-tick"))!.BoundingRectangle;
        Assert.AreEqual(3 * s, tick.Width, 1.0, $"the tick is {tick.Width} px wide, want 3 DIP");
        Assert.AreEqual(14 * s, tick.Height, 1.0, $"the tick is {tick.Height} px tall, want 36 less 11 top and bottom");
        Assert.AreEqual(row.Left, tick.Left, 1.0, "the tick is not at the row's left edge");
        Keyboard.Type(VirtualKeyShort.DOWN);
        Assert.IsTrue(Host.Until(() => Selected(win) == Rows(win)[1].Name && Ticks(win).SequenceEqual(new[] { "", "on", "", "" }), 3000),
            "Down did not move the tick to row 2 alone: " + string.Join(" | ", Ticks(win)));

        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (Esc)"), 5000), "Esc did not close the launcher (the pop-over out never landed?):\n" + Tail(app.Log));
    }
}
