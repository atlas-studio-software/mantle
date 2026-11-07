using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The two parity known gaps of the right cluster (tools/parity run-parity.ps1 $gapList).
[TestClass]
public sealed class ClusterTests
{
    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    /// Electron's hidden battery button still takes 14 px (.btn display:flex beats [hidden]) + the 4 px gap,
    /// so network -> clock is 4 + 14 + 4 DIP; without the slot the cluster sat ~18 px further right.
    [TestMethod]
    public void Battery_slot_keeps_the_cluster_where_Electron_has_it()
    {
        using var app = BarApp.Launch("uitest-cluster");
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        var net = app.Find("bar.network").BoundingRectangle;
        var bat = app.Find("bar.battery").BoundingRectangle;
        var clock = app.Find("bar.clock").BoundingRectangle;
        Assert.AreEqual(14 * s, bat.Width, 1.0, "the battery slot is 14 DIP wide");
        Assert.AreEqual(22 * s, clock.Left - net.Right, 1.5, $"network right {net.Right}, battery {bat.Left}..{bat.Right}, clock left {clock.Left}");
    }

    /// bar.js renderChips: one pip on "more" stands in for every problem, warn-coloured when one is a warning.
    /// The runner does not let apps read notifications, so Electron shows the warn pip there ("notifications
    /// off", notifications.js); the native bar must show the same pip, and say why in words.
    [TestMethod]
    public void More_pip_shows_and_says_why_when_notifications_cannot_be_read()
    {
        using var app = BarApp.Launch("uitest-pip");
        var pip = app.TryFind("bar.more.pip", 8000);
        Assert.IsNotNull(pip, "no pip on \"more\"; ids present: " + string.Join(", ", app.SeenIds()));
        string help = pip!.Properties.HelpText.ValueOrDefault ?? "";
        Assert.IsTrue(help.StartsWith("warn: notifications off", StringComparison.Ordinal), "pip help text: " + help);
        var more = app.Find("bar.more").BoundingRectangle;
        var r = pip.BoundingRectangle;
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        Assert.AreEqual(more.Right - 5 * s, r.Right, 1.5, "the pip ring's right edge is 5 DIP in (dot at right 7)");
        Assert.AreEqual(more.Top + 5 * s, r.Top, 1.5, "the pip ring's top is 5 DIP down (dot at top 7)");
    }
}
