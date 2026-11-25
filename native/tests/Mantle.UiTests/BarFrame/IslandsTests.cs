using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// Lane r13-bar-islands: the bar as three floating islands (apps-r3/mantle-bar look.css "the bar: three islands"; the pure
/// numbers are Mantle.Core BarIslands, tested in Mantle.Core.Tests): left (the M), dock, right, in that order, each 44 DIP
/// tall, 12 DIP from the screen's left, right and bottom edges, the left island 44 DIP square.
[TestClass]
public sealed class IslandsTests
{
    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    [TestMethod]
    public void Three_islands_left_to_right_44_tall_12_from_the_edges()
    {
        using var app = BarApp.Launch("uitest-islands");
        var mon = Win32.MonitorRect(app.Hwnd);
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        Rectangle left = app.Find("bar.island.left").BoundingRectangle;
        Rectangle dock = app.Find("bar.island.dock").BoundingRectangle;
        Rectangle right = app.Find("bar.island.right").BoundingRectangle;
        string got = $"left {left}, dock {dock}, right {right} on monitor {mon.Left},{mon.Top}-{mon.Right},{mon.Bottom} at {s * 100:0}%";

        // order
        Assert.IsTrue(left.Right <= dock.Left, "the dock does not come after the left island: " + got);
        Assert.IsTrue(dock.Right <= right.Left, "the right island does not come after the dock: " + got);

        // 44 DIP tall, all on one line 12 DIP above the bottom edge
        double h = 44 * s, gap = 12 * s;
        foreach (var (name, r) in new[] { ("left", left), ("dock", dock), ("right", right) }) {
            Assert.AreEqual(h, r.Height, 2.0, $"the {name} island is {r.Height} px tall, want 44 DIP ({h:0.#} px): {got}");
            Assert.AreEqual(gap, mon.Bottom - r.Bottom, 2.0, $"the {name} island ends {mon.Bottom - r.Bottom} px above the bottom edge, want 12 DIP ({gap:0.#} px): {got}");
        }

        // 12 DIP from the left and right edges; the left island 44 DIP wide
        Assert.AreEqual(gap, left.Left - mon.Left, 2.0, "the left island's air to the screen's left edge: " + got);
        Assert.AreEqual(gap, mon.Right - right.Right, 2.0, "the right island's air to the screen's right edge: " + got);
        Assert.AreEqual(h, left.Width, 2.0, "the left island is not 44 DIP wide: " + got);

        // the dock: centred on the screen, or 12 DIP after the M when centred it would run into the right island
        double centre = (mon.Left + mon.Right) / 2.0, dockCentre = (dock.Left + dock.Right) / 2.0;
        bool centred = System.Math.Abs(dockCentre - centre) <= 2;
        bool afterM = System.Math.Abs(dock.Left - left.Right - gap) <= 2;
        Assert.IsTrue(centred || afterM, "the dock is neither centred on the screen nor 12 DIP after the M: " + got);
        if (afterM && !centred)
            Assert.IsTrue(centre + dock.Width / 2.0 + gap > right.Left, "the dock left the centre with room to spare: " + got);
    }

    /// The offline network button (Electron ui/bar/net.js, the prototype's ?net=off): MANTLE_TEST_NET=off draws it whatever
    /// the runner's network is; its name is "Not connected, " and one of net.js's three details.
    [TestMethod]
    public void Offline_network_button_says_not_connected_and_why()
    {
        var env = new System.Collections.Generic.Dictionary<string, string> { ["MANTLE_TEST_NET"] = "off" };
        using var app = BarApp.Launch("uitest-netoff", env: env);
        var net = app.Find("bar.network");
        string[] want = {
            "Not connected, Wi-Fi is on but not connected to a network",
            "Not connected, Wi-Fi is turned off",
            "Not connected, No connections are available",
        };
        Assert.IsTrue(Host.Until(() => System.Array.IndexOf(want, net.Name ?? "") >= 0, 8000), $"the network button is named \"{net.Name}\"");
    }
}
