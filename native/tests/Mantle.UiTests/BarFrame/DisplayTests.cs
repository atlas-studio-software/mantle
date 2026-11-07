using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The bar across display scale changes under a running bar (BAR-087, BAR-088's scale part): the runner's primary
/// display goes 100 -> 150 -> 125 -> 100 % and the pill must land where the spec puts it at each scale, without a
/// restart. Runner only (it changes the display); the scale is put back to 100 % in the cleanup whatever happens.
[TestClass]
public sealed class DisplayTests
{
    [TestCleanup]
    public void Cleanup()
    {
        if (FrameProbe.PrimaryScale() != 100) { FrameProbe.SetScale(100); Thread.Sleep(3000); }
        Host.KillAll();
    }

    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 20))); }

    /// The spec's pill at scale s on the primary display, physical px: min(1120, screen - 28) DIP wide, 48 tall,
    /// 14 above the bottom edge, centred (the same numbers tools/parity crops with).
    static (double L, double T, double W, double H) Spec(double s)
    {
        double w = Math.Min(1120, FrameProbe.ScreenW / s - 28) * s, h = 48 * s;
        return ((FrameProbe.ScreenW - w) / 2, FrameProbe.ScreenH - 14 * s - h, w, h);
    }

    /// BAR-087 (125 %: 62 DIP x 1.25 = 77.5 px, the window still ends on the screen's last row), BAR-088 (a scale
    /// change re-places the bar for the primary display's new scale, at once and without a restart).
    [TestMethod]
    public void Scale_change_re_places_the_bar_for_the_new_dpi_without_a_restart()
    {
        Win32.DpiAware();
        if (FrameProbe.PrimaryScale() != 100) { FrameProbe.SetScale(100); Thread.Sleep(3000); }
        Assert.AreEqual(100, FrameProbe.PrimaryScale(), "the runner's display is not at 100 % to start from");
        string dir = FrameProbe.NewDir("dpi");
        using var app = BarApp.Launch("frame-dpi", extra: new[] { "--bench-out=" + dir });
        Assert.IsTrue(FrameProbe.LogHas(dir, "bar frame: up mode", 15000), "no up-mode frame line. Log:\n" + Tail(dir));
        int pid = app.Proc.Id;
        var report = new List<string>();
        foreach (int pct in new[] { 150, 125, 100 })
        {
            int placedBefore = FrameProbe.Count(dir, "bar placed (");
            uint dpi = (uint)Math.Round(96 * pct / 100.0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string set = FrameProbe.SetScale(pct);
            Assert.IsTrue(set.StartsWith("ok", StringComparison.Ordinal), $"the runner refused {pct} %: {set}");
            Assert.IsTrue(Host.Until(() => Win32.GetDpiForWindow(app.Hwnd) == dpi, 15000),
                $"{pct} %: the bar window's dpi stayed {Win32.GetDpiForWindow(app.Hwnd)} (want {dpi}). Log:\n" + Tail(dir));
            Assert.IsTrue(Host.Until(() => FrameProbe.Log(dir).Contains($"at {pct} % (dpi {dpi})", StringComparison.Ordinal)
                && FrameProbe.Count(dir, "bar placed (") > placedBefore, 10000),
                $"{pct} %: no \"bar placed (...)\" line for dpi {dpi}. Log:\n" + Tail(dir));
            long placedMs = sw.ElapsedMilliseconds;
            Thread.Sleep(1500);   // past the 400 ms burst quiet: any trailing run has happened
            Assert.IsFalse(app.Proc.HasExited, $"{pct} %: the app exited on the scale change");
            Assert.AreEqual(app.Hwnd, FrameProbe.Find(pid, BarApp.WindowTitle), $"{pct} %: the bar window was replaced, not re-placed");
            double s = pct / 100.0;
            var want = Spec(s);
            var pill = app.Find("bar.pill").BoundingRectangle;
            string got = $"{pct} %: pill {pill.Left},{pill.Top} {pill.Width}x{pill.Height}, spec {want.L:0.#},{want.T:0.#} {want.W:0.#}x{want.H:0.#}";
            Assert.AreEqual(want.W, pill.Width, 2.0, "width: " + got);
            Assert.AreEqual(want.H, pill.Height, 2.0, "height: " + got);
            Assert.AreEqual(want.L, pill.Left, 2.0, "left (centred): " + got);
            Assert.AreEqual(want.T, pill.Top, 2.0, "top (14 DIP above the bottom edge): " + got);
            FrameProbe.GetWindowRect(app.Hwnd, out var wr);
            Assert.AreEqual(FrameProbe.ScreenH, wr.Bottom, $"{pct} %: the window ends at y {wr.Bottom}, not on the screen's bottom edge ({FrameProbe.ScreenH})");
            Assert.IsTrue(Win32.IsWindowVisible(app.Hwnd), $"{pct} %: the bar is not visible after the change");
            int runs = FrameProbe.Count(dir, "bar placed (") - placedBefore;
            report.Add($"{got}; window bottom {wr.Bottom}; placed in {placedMs} ms; {runs} placement line(s)");
            Host.Screenshot("frame-dpi-" + pct);
        }
        Console.WriteLine(string.Join("\n", report));
    }
}
