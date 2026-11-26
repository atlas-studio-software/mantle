using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The two parity known gaps of the right cluster (tools/parity run-parity.ps1 $gapList).
[TestClass]
public sealed class ClusterTests
{
    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    /// Lane r13-bar-islands: the status island's glyph buttons are 32 x 36 DIP, 2 apart (look.css .glyph, .b-right gap 2),
    /// in the prototype's order volume, network, battery, clock (shell.js). The battery is a glyph button like the others;
    /// lane r14-bar-review: with no battery it is not there at all and takes no room, so network to clock is the one
    /// 2 DIP gap (the empty 32 x 36 box left a gap there). The battery's own box is checked only on a machine with one.
    [TestMethod]
    public void Status_glyphs_are_32_by_36_and_2_apart()
    {
        using var app = BarApp.Launch("uitest-cluster");
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        var net = app.Find("bar.network").BoundingRectangle;
        var vol = app.Find("bar.volume").BoundingRectangle;
        var clock = app.Find("bar.clock").BoundingRectangle;
        Assert.AreEqual(32 * s, net.Width, 1.0, $"the network button is {net.Width} px wide, want 32 DIP");
        Assert.AreEqual(36 * s, net.Height, 1.0, $"the network button is {net.Height} px tall, want 36 DIP");
        Assert.AreEqual(2 * s, net.Left - vol.Right, 1.5, $"volume right {vol.Right}, network left {net.Left}: want the 2 DIP gap");
        var (has, why) = Win32.Battery();
        if (!has) {
            Assert.IsNull(app.TryFind("bar.battery", 500), $"this machine has no battery ({why}), yet the bar shows a battery button");
            Assert.AreEqual(2 * s, clock.Left - net.Right, 1.5, $"network right {net.Right}, clock left {clock.Left}: with no battery, want the one 2 DIP gap (an absent battery takes no room)");
            Assert.Inconclusive($"the battery button's 32 x 36 box and its gaps were not checked: {why}, so the bar draws no battery (checked: it is absent and network to clock is 2 DIP)");
        }
        var bat = app.Find("bar.battery").BoundingRectangle;
        Assert.AreEqual(32 * s, bat.Width, 1.0, $"the battery button is {bat.Width} px wide, want 32 DIP");
        Assert.AreEqual(36 * s, bat.Height, 1.0, $"the battery button is {bat.Height} px tall, want 36 DIP");
        Assert.AreEqual(2 * s, bat.Left - net.Right, 1.5, $"network right {net.Right}, battery left {bat.Left}: want the 2 DIP gap");
        Assert.AreEqual(2 * s, clock.Left - bat.Right, 1.5, $"battery right {bat.Right}, clock left {clock.Left}: want the 2 DIP gap");
    }

    /// bar.js renderChips: one pip on "more" stands in for every problem, warn-coloured when one is a warning.
    /// The runner does not let apps read notifications, so Electron shows the warn pip there ("notifications
    /// off", notifications.js); the native bar must show the same pip, and say why in words. Lane r14-bar-review: in the
    /// islands look the pip is the prototype's (barhide.js #more .bh-pip): a 6 px dot at top 8 / right 8, no ring.
    [TestMethod]
    public void More_pip_shows_and_says_why_when_notifications_cannot_be_read()
    {
        using var app = BarApp.Launch("uitest-pip");
        var pip = app.TryFind("bar.more.pip", 8000);
        if (pip == null) Assert.Fail("no pip on \"more\"; ids present: " + string.Join(", ", app.SeenIds()));   // the tree walk only on a failure
        string help = pip!.Properties.HelpText.ValueOrDefault ?? "";
        Assert.IsTrue(help.StartsWith("warn: notifications off", StringComparison.Ordinal), "pip help text: " + help);
        var more = app.Find("bar.more").BoundingRectangle;
        var r = pip.BoundingRectangle;
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        Assert.AreEqual(6 * s, r.Width, 1.0, $"the pip is {r.Width} px wide, want 6 DIP");
        Assert.AreEqual(6 * s, r.Height, 1.0, $"the pip is {r.Height} px tall, want 6 DIP");
        Assert.AreEqual(more.Right - 8 * s, r.Right, 1.5, "the pip's right edge is 8 DIP in (right 8)");
        Assert.AreEqual(more.Top + 8 * s, r.Top, 1.5, "the pip's top is 8 DIP down (top 8)");
    }

    /// Lane r14-bar-review, the prototype's clock (look.css .clock: padding 0 8, align-items flex-end; shell.js tick: hour
    /// and minute): the time reads h:mm (12- or 24-hour by the locale, never seconds), and the time and the date sit on
    /// the right, 8 DIP in from the button's right edge, the button as wide as its text plus 8 each side (no min-width;
    /// BAR-416's 80 px box is the electron face's, which ticked seconds).
    [TestMethod]
    public void Clock_reads_h_mm_and_sits_on_the_right_of_its_button()
    {
        using var app = BarApp.Launch("uitest-clockbox");
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        var t = app.Find("bar.clock.time");
        var d = app.Find("bar.clock.date");
        var clock = app.Find("bar.clock").BoundingRectangle;
        string text = t.Name ?? "";
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d{1,2}:\d{2}( \S+)?$"), $"time text '{text}' is not h:mm (no seconds)");
        var tr = t.BoundingRectangle; var dr = d.BoundingRectangle;
        Assert.AreEqual(clock.Right - 8 * s, tr.Right, 1.5, $"the time's right edge {tr.Right}, the clock's {clock.Right}: want it 8 DIP in (right-aligned)");
        Assert.AreEqual(clock.Right - 8 * s, dr.Right, 1.5, $"the date's right edge {dr.Right}, the clock's {clock.Right}: want it 8 DIP in (right-aligned)");
        Assert.AreEqual(clock.Left + 8 * s, Math.Min(tr.Left, dr.Left), 1.5, $"the clock's left {clock.Left}, its text's left {Math.Min(tr.Left, dr.Left)}: want 8 DIP (as wide as its text, no min-width)");
    }

    /// BAR-416: the time box is sized for the longest string ("10:00:00 PM", min-width 80 px, tabular
    /// figures), so a tick never moves the cluster: the clock's left edge stays put across ticks. The electron face's
    /// rule (MANTLE_TEST_GLASS=electron), whose clock ticks seconds; the islands clock is the test above.
    [TestMethod]
    public void Clock_time_box_is_sized_for_the_longest_time_and_never_moves()
    {
        using var app = BarApp.Launch("uitest-clockbox-electron", env: new Dictionary<string, string> { ["MANTLE_TEST_GLASS"] = "electron" });
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        var t = app.Find("bar.clock.time");
        Assert.IsTrue(t.BoundingRectangle.Width >= 80 * s - 0.5, $"time box {t.BoundingRectangle.Width} px, want at least {80 * s}");
        var clock = app.Find("bar.clock");
        double left0 = clock.BoundingRectangle.Left;
        string t0 = t.Name ?? "";
        Assert.IsTrue(Host.Until(() => (t.Name ?? "") != t0, 3000), "the time did not tick in 3 s");
        Assert.AreEqual(left0, clock.BoundingRectangle.Left, 0.5, "the clock moved when the time ticked");
    }
}
