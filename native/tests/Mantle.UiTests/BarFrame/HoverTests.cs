using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The bar frame from outside (Surfaces/Bar/Frame): the hover mode a live run uses (MANTLE_TEST_HOVER=1 asks a
/// test run for it), the fullscreen block, the dock mode, and what the bar + its glass cost at rest.
[TestClass]
public sealed class HoverTests
{
    [TestCleanup]
    public void Cleanup() { FrameProbe.CursorAway(); Host.KillAll(); }

    static (BarApp App, IntPtr Hwnd, string Dir) Hover(string tag, string theme = "light", IDictionary<string, string>? env = null, params string[] extra)
    {
        FrameProbe.CursorAway();
        string dir = FrameProbe.NewDir(tag);
        var e = new Dictionary<string, string> { ["MANTLE_TEST_HOVER"] = "1" };
        if (env != null) foreach (var kv in env) e[kv.Key] = kv.Value;
        var app = BarApp.Launch("frame-" + tag, theme, wait: false, env: e, extra: new[] { "--bench-out=" + dir }.Concat(extra));
        Assert.IsTrue(FrameProbe.LogHas(dir, "bar hidden: at rest", 20000), "the hover bar never said it was hidden at rest. Log:\n" + Tail(dir));
        IntPtr h = FrameProbe.Find(app.Proc.Id, BarApp.WindowTitle);
        Assert.AreNotEqual(IntPtr.Zero, h, "no \"Mantle bar\" window, hidden or not");
        return (app, h, dir);
    }

    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 25))); }

    /// BAR-009, BAR-010, BAR-026, BAR-095, BAR-097, BAR-017/020 (the window side of the motion), BAR-025/417 (clock gear).
    [TestMethod]
    public void Hover_bar_hides_at_rest_reveals_at_the_edge_stays_on_the_pill_and_hides_when_left()
    {
        var (app, h, dir) = Hover("reveal");
        using var _ = app;
        Assert.IsFalse(Win32.IsWindowVisible(h), "hidden at rest, but the window is visible");
        // above the hidden pill's place, not the band: never a reveal (BAR-010)
        FrameProbe.SetCursorPos(FrameProbe.ScreenW / 2, FrameProbe.ScreenH - 40);
        Thread.Sleep(700);
        Assert.IsFalse(Win32.IsWindowVisible(h), "the cursor over the hidden pill's place revealed the bar; only the edge band may");
        FrameProbe.CursorToEdge();
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(h), 1500), "the cursor on the bottom edge did not reveal the bar within 1.5 s. Log:\n" + Tail(dir));
        Assert.IsTrue(FrameProbe.LogHas(dir, "bar revealed (the cursor reached the bottom edge)", 2000), "no reveal line");
        // on the pill: it stays up (the rest band)
        FrameNativeRect(h, out var r);
        FrameProbe.SetCursorPos((r.Left + r.Right) / 2, r.Bottom - (int)Math.Round(14 * Win32.GetDpiForWindow(h) / 96.0) - 10);
        Thread.Sleep(1200);
        Assert.IsTrue(Win32.IsWindowVisible(h), "the bar hid with the cursor resting on the pill");
        Host.Screenshot("frame-hover-revealed");
        // away: hidden after the grace + the exit (BAR-097, BAR-020)
        FrameProbe.CursorAway();
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h), 2000), "the bar did not hide within 2 s of the cursor leaving. Log:\n" + Tail(dir));
        Assert.IsTrue(FrameProbe.LogHas(dir, "bar hidden: window hidden, nothing renders", 1000), "no hidden line");
        string log = FrameProbe.Log(dir);
        StringAssert.Contains(log, "bar hiding: the cursor has been away from the bar for 180 ms");
        StringAssert.Contains(log, "glass: light: live, 4 pictures a second while up");
        StringAssert.Contains(log, "glass: stopped (the window is down");
        // a second reveal works too (re-reads the geometry each time)
        FrameProbe.CursorToEdge();
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(h), 1500), "the second reveal did not happen");
        Assert.IsTrue(FrameProbe.LogHas(dir, "reveal 2", 1000), "no second reveal line");
    }

    static void FrameNativeRect(IntPtr h, out Win32.RECT r) => FrameProbe.GetWindowRect(h, out r);

    /// BAR-091: a fullscreen window in front keeps the bar down; a maximised one does not.
    [TestMethod]
    public void Fullscreen_window_in_front_keeps_the_hover_bar_down()
    {
        var (app, h, dir) = Hover("fullscreen");
        using var _ = app;
        using var fx = new Host.Fixture("Mantle fullscreen fixture", 200, 200);
        FrameProbe.MakeFullscreen(fx.Hwnd);
        Assert.IsTrue(ZOrder.Foreground(fx.Hwnd), "the fullscreen fixture could not take the foreground");
        Thread.Sleep(300);
        FrameProbe.CursorToEdge();
        Thread.Sleep(1200);
        Assert.IsFalse(Win32.IsWindowVisible(h), "the bar revealed over a fullscreen window. Log:\n" + Tail(dir));
        Assert.IsTrue(FrameProbe.LogHas(dir, "is fullscreen in front, so the bar stays hidden", 1000), "no blocked line. Log:\n" + Tail(dir));
        Host.Screenshot("frame-fullscreen-blocked");
    }

    /// BAR-106 in a test run: the bar sits flush on its dock slot, always up; a test reserves nothing in Windows.
    [TestMethod]
    public void Dock_bar_sits_on_its_slot_and_a_test_run_reserves_nothing()
    {
        var before = FrameProbe.WorkArea();
        string dir = FrameProbe.NewDir("dock");
        using var app = BarApp.Launch("frame-dock", extra: new[] { "--dock", "--bench-out=" + dir });
        Assert.IsTrue(FrameProbe.LogHas(dir, "dock (start):", 10000), "no dock line. Log:\n" + Tail(dir));
        var pill = app.Find("bar.pill").BoundingRectangle;
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        Assert.AreEqual(FrameProbe.ScreenH, pill.Bottom, 1.0 + s, $"the docked pill should end at the screen's bottom edge; pill {pill}");
        Assert.AreEqual(Math.Round(48 * s), pill.Height, 1.0, "the docked pill is not 48 DIP tall");
        var after = FrameProbe.WorkArea();
        Assert.AreEqual(before.Bottom, after.Bottom, $"a test run changed the work area ({before.Bottom} -> {after.Bottom}); it must reserve nothing");
        StringAssert.Contains(FrameProbe.Log(dir), "nothing reserved", "the dock line does not say why nothing was reserved");
        Thread.Sleep(1500);
        Assert.IsTrue(Win32.IsWindowVisible(app.Hwnd), "the docked bar hid");
        Host.Screenshot("frame-dock");
    }

    /// The promise: idle CPU near zero. One run per theme: the bar + its glass hidden at rest, then shown and idle
    /// (cursor resting on the pill: light refreshes its glass 4 times a second while up, dark holds one still).
    [TestMethod]
    public void Bar_and_glass_idle_cpu_hidden_at_rest_and_shown()
    {
        var lines = new List<string>();
        var worst = new Dictionary<string, double>();
        foreach (var theme in new[] { "light", "dark" })
        {
            var (app, h, dir) = Hover("cpu-" + theme, theme);
            using var _ = app;
            Thread.Sleep(3000);
            var hidden = FrameProbe.Cpu(app.Proc, 15000);
            FrameProbe.CursorToEdge();
            Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(h), 1500), "no reveal for the shown measurement");
            FrameProbe.GetWindowRect(h, out var r);
            FrameProbe.SetCursorPos((r.Left + r.Right) / 2, r.Bottom - (int)Math.Round(14 * Win32.GetDpiForWindow(h) / 96.0) - 10);
            Thread.Sleep(2000);
            var shown = FrameProbe.Cpu(app.Proc, 15000);
            bool stillUp = Win32.IsWindowVisible(h);
            FrameProbe.CursorAway();
            Host.Until(() => !Win32.IsWindowVisible(h), 2000);
            Thread.Sleep(500);
            int pics = FrameProbe.Count(dir, "glass: stopped");
            var after = FrameProbe.Cpu(app.Proc, 5000);
            string line = $"IDLE-CPU {theme}: hidden at rest {hidden.App} % of one core (dwm {hidden.Dwm} %), shown idle {shown.App} % (dwm {shown.Dwm} %), hidden again {after.App} %; bar stayed up {stillUp}; glass stops logged {pics}";
            lines.Add(line); Console.WriteLine(line);
            worst["hidden " + theme] = hidden.App; worst["shown " + theme] = shown.App;
            Assert.IsTrue(stillUp, "the bar hid during the shown measurement: " + line);
            if (theme == "light") Assert.IsTrue(pics >= 1, "light glass never logged its stop on hide: " + line);
        }
        File.WriteAllLines(Path.Combine(Host.OutDir, "idle-cpu.txt"), lines);
        Assert.IsTrue(worst["hidden light"] < 1.0 && worst["hidden dark"] < 1.0, "hidden at rest costs 1 % of a core or more:\n" + string.Join("\n", lines));
        Assert.IsTrue(worst["shown light"] < 20 && worst["shown dark"] < 20, "shown idle costs 20 % of a core or more:\n" + string.Join("\n", lines));
    }
}
