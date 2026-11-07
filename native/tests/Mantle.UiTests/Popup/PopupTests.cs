using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The pop-up framework (Shell/Popup) from outside, through the test stand-ins (MANTLE_TEST_POPUPS=1: More opens
/// "more", instant and parked; the clock opens "cal", on demand and focusable), with the outside watch armed
/// (MANTLE_TEST_POPWATCH=away). Every click and key is real input through the queue.
[TestClass]
public sealed class PopupTests
{
    [TestCleanup]
    public void Cleanup() { FrameProbe.CursorAway(); Host.KillAll(); }

    static void ClickEl(BarApp app, string id) { var r = app.Find(id).BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(450); }
    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 25))); }

    /// WIN-014 + BAR-375/376 (one at a time, toggle, replace), parking only the instant kind (WIN-006/010),
    /// BAR-392 (a press on the bar outside the pop-up), BAR-394/WIN-017 (outside click, Esc from another app's
    /// keyboard), BAR-396 (another app to the front closes it), WIN-011 (raised above the app in front).
    [TestMethod]
    public void Popups_open_one_at_a_time_toggle_park_and_close_on_outside_click_esc_and_app_switch()
    {
        string dir = FrameProbe.NewDir("popups");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_POPUPS"] = "1", ["MANTLE_TEST_POPWATCH"] = "away" };
        using var app = BarApp.Launch("popups", env: env, extra: new[] { "--bench-out=" + dir });
        using var fx = new Host.Fixture("Mantle pop-up fixture", 300, 150);
        Assert.IsTrue(ZOrder.Foreground(fx.Hwnd), "the fixture could not take the foreground");
        int pid = app.Proc.Id;
        IntPtr More() => FrameProbe.Find(pid, "Mantle pop-up: More");
        IntPtr Cal() => FrameProbe.Find(pid, "Mantle pop-up: Calendar");

        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(More()), 2000), "More did not open. Log:\n" + Tail(dir));
        IntPtr more1 = More();
        Assert.IsTrue(FrameProbe.LogHas(dir, "pop-up More: shown at", 2000), "no shown line");
        Assert.IsTrue(ZOrder.IsTopmost(more1) && ZOrder.IsAbove(more1, fx.Hwnd), "the pop-up is not above the app in front (WIN-011)");
        Assert.AreEqual(fx.Hwnd, Win32.GetForegroundWindow(), "a no-focus pop-up took the foreground");
        Host.Screenshot("popup-more-open");

        // its own button again: closed and parked (cloaked), not destroyed
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(more1), 1500), "More did not close on its own button");
        Assert.AreEqual(more1, More(), "the instant pop-up was destroyed instead of parked");
        Assert.IsTrue(FrameProbe.Cloaked(more1), "the parked pop-up is not cloaked");

        // reopened: the SAME window; then the clock replaces it (one at a time)
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(More()), 1500), "More did not reopen");
        Assert.AreEqual(more1, More(), "reopening made a new window instead of using the parked one");
        StringAssert.Contains(FrameProbe.Log(dir), "pop-up More: opening (parked, uncloaked");
        ClickEl(app, "bar.clock");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(Cal()), 2000), "the calendar did not open. Log:\n" + Tail(dir));
        Assert.IsFalse(FrameProbe.Showing(more1), "two pop-ups open at once");
        Assert.IsTrue(FrameProbe.LogHas(dir, "pop-up Calendar: has the keyboard", 1500) || FrameProbe.LogHas(dir, "pop-up Calendar: could not take the keyboard", 500), "the focusable pop-up said nothing about the keyboard");

        // an on-demand kind is closed for good
        IntPtr cal1 = Cal();
        ClickEl(app, "bar.clock");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindow(cal1) || !FrameProbe.Showing(cal1), 1500), "the calendar did not close on its own button");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindow(cal1), 1500), "the on-demand pop-up was kept instead of closed");

        // outside click (the fixture), and the same click does not reopen anything
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(More()), 1500), "More did not open (3)");
        ZOrder.Click(300 + 200, 150 + 150);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(more1), 1500), "an outside click did not close the pop-up. Log:\n" + Tail(dir));
        StringAssert.Contains(FrameProbe.Log(dir), "pop-up More: closed (a left click outside)");

        // Esc while ANOTHER app has the keyboard
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(More()), 1500), "More did not open (4)");
        ZOrder.Foreground(fx.Hwnd);
        FrameProbe.Tap(0x1B);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(more1), 1500), "Esc did not close the pop-up. Log:\n" + Tail(dir));

        // a switch to another app closes it (BAR-396): open, then the app in front changes
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(More()), 1500), "More did not open (5)");
        using var fx2 = new Host.Fixture("Mantle pop-up fixture 2", 700, 200);
        ZOrder.Foreground(fx2.Hwnd);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(more1), 2000), "a switch to another app did not close the pop-up. Log:\n" + Tail(dir));
        StringAssert.Contains(FrameProbe.Log(dir), "pop-up watch: hook");
    }
}
