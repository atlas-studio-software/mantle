using System.Globalization;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The see-through look on the top bar (look.md SEE-012) and the desktop layer (SEE-013), part 2: the same fixture still
/// (LOOK_SYNC_DIR, never the user's look.json), the same check on real screen pixels (the still at that screen point
/// under the window's logged tint, within 6 per channel).
public sealed partial class GlassTests
{
    /// Points of the rectangles that must ALL be the still under the window's tint (nothing is drawn there): a few looks
    /// while the picture lands, then every point within the tolerance.
    static (int N, int Worst) Strict(string dir, string name, IntPtr h, Func<(int, int, int, int)[]> rects, bool dark, string what, bool outer)
    {
        (int N, int Worst, int Good, string Bad) c = (0, 0, 0, "");
        double a = 0;
        for (int i = 0; i < 12; i++) {
            a = TintOf(dir, name, dark);
            c = CheckIn(h, rects(), 3, dark, a, what, outer);
            if (c.N >= 12 && c.Worst <= Tol) break;
            Thread.Sleep(250);
        }
        Assert.IsTrue(c.N >= 12, $"{what}: only {c.N} sample points inside blocks");
        Assert.IsTrue(c.Worst <= Tol, $"{what}: the pixels are not the still at those screen points under the {a:0.00} tint (worst error {c.Worst} over {c.N} points):{c.Bad}\n{Tail(dir)}");
        return (c.N, c.Worst);
    }

    static AutomationElement El(IntPtr h, string id)
    {
        var win = Host.Uia.FromHandle(h);
        var el = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId(id)), 5000) as AutomationElement;
        Assert.IsNotNull(el, "no " + id + " element");
        return el!;
    }

    const string AutoWords = @" \d\.\d\d \(Automatic: [0-9.]+, (raised to [0-9.]+ so text stays readable|text already reads) over this window's part of the picture";

    [TestMethod]
    [DataRow("dark")]
    [DataRow("light")]
    public void The_top_bar_and_its_dropdowns_paint_the_desktop_still_sharp_lined_up_under_the_tint_of_their_own_band(string theme)
    {
        bool dark = theme == "dark";
        FrameProbe.CursorAway();
        string dir = FrameProbe.NewDir("glass-top-" + theme);
        string still = Path.Combine(dir, "still.png");
        WritePng(still);
        var env = new Dictionary<string, string> { ["LOOK_SYNC_DIR"] = Fixture("top-" + theme, still), ["MANTLE_TEST_GLASS"] = "", ["MANTLE_TEST_TOPBAR"] = "app=npp&stats=ok" };
        using var app = BarApp.Launch("glass-top-" + theme + "-" + Guid.NewGuid().ToString("N")[..4], theme: theme, env: env, extra: new[] { "--bench-out=" + dir });
        int pid = app.Proc.Id;
        Host.LogContains(() => FrameProbe.Log(dir), "topbar: shown (", "the top bar was never shown", 15000);
        IntPtr bar = Host.Until(() => FrameProbe.Find(pid, "Mantle top bar") is var h && FrameProbe.Showing(h) ? (object)h : null, 5000) is IntPtr b ? b : IntPtr.Zero;
        Assert.AreNotEqual(IntPtr.Zero, bar, "no top bar window:\n" + Tail(dir));
        Host.LogContains(() => FrameProbe.Log(dir), "glass top bar: painted the desktop still", "the top bar painted the still", 8000);
        StringAssert.DoesNotMatch(FrameProbe.Log(dir), new Regex("glass top bar: no picture"), "the top bar fell back to its plain fill");
        // Automatic: the native guard's worst strip over the bar's own band (at least 140 DIP), not Electron's own tint
        StringAssert.Matches(FrameProbe.Log(dir), new Regex(@"glass top bar: painted the desktop still [^\r\n]*tint " + theme + AutoWords));

        // the strip: the rows above and below its 22 DIP controls (the light bottom line, row 27, is left out), the full width
        double s = Math.Max(96u, GetDpiForWindow(bar)) / 96.0;
        (int, int, int, int)[] Strip()
        {
            Assert.IsTrue(FrameProbe.GetWindowRect(bar, out var r), "no top bar rect");
            return new[] { (r.Left + 2, r.Right - 2, r.Top, r.Top + (int)Math.Floor(3 * s)), (r.Left + 2, r.Right - 2, r.Top + (int)Math.Ceiling(25 * s), r.Top + (int)Math.Floor(27 * s)) };
        }
        var strip = Strict(dir, "top bar", bar, Strip, dark, theme + "-topbar-strip", outer: true);

        // a dropdown (the Mantle menu): its padding inside the hairline, above and below the rows
        El(bar, "topbar.title.mantle").AsButton().Invoke();
        Host.LogContains(() => FrameProbe.Log(dir), "topbar: menu opened: mantle", "the Mantle menu did not open", 4000);
        Host.LogContains(() => FrameProbe.Log(dir), "glass top bar menu 0: painted the desktop still", "the dropdown painted the still", 8000);
        StringAssert.Matches(FrameProbe.Log(dir), new Regex(@"glass top bar menu 0: painted the desktop still [^\r\n]*tint " + theme + AutoWords));
        Thread.Sleep(450);   // its slide has landed
        int pad = (int)Math.Floor((dark ? 4 : 5) * s);
        (int, int, int, int)[] Pads()
        {
            var r = El(bar, "topbar.menu.0").BoundingRectangle;
            return new[] { (r.Left + 8, r.Right - 8, r.Top - pad, r.Top), (r.Left + 8, r.Right - 8, r.Bottom, r.Bottom + pad) };
        }
        var menu = Strict(dir, "top bar menu 0", bar, Pads, dark, theme + "-topbar-menu", outer: true);
        Assert.AreEqual(1, Count(FrameProbe.Log(dir), "glass: the desktop still 1600x900 cover-fit"), "the dropdown decoded the still again (it is shared):\n" + Tail(dir));
        El(bar, "topbar.title.mantle").AsButton().Invoke();
        Host.LogContains(() => FrameProbe.Log(dir), "topbar: menu closed", "the Mantle menu did not close", 4000);

        // at rest the look does nothing (one picture, no timer, no redraw on the stats tick): no glass line, and the CPU
        Thread.Sleep(1500);
        int said = Count(FrameProbe.Log(dir), "glass top bar");
        var c1 = FrameProbe.Cpu(app.Proc, 6000);
        var c2 = FrameProbe.Cpu(app.Proc, 6000);
        string line = $"GLASS-IDLE top bar {theme}: painted at rest {c1.App} % and {c2.App} % of one core (dwm {c1.Dwm} %, {c2.Dwm} %); strip {strip.N} points worst {strip.Worst}, menu {menu.N} points worst {menu.Worst}";
        Console.WriteLine(line);
        File.AppendAllLines(Path.Combine(Host.OutDir, "glass-idle.txt"), new[] { line });
        Assert.AreEqual(said, Count(FrameProbe.Log(dir), "glass top bar"), "the top bar's look did work at rest:\n" + Tail(dir));
        // dark: the bottom bar holds one still, so the app at rest is the top bar's cost; light's bottom bar refreshes its
        // own glass while up (HoverTests), so light is measured, not judged
        if (dark) Assert.IsTrue(Math.Min(c1.App, c2.App) < 1.0, "the top bar painting the still costs 1 % of a core or more at rest: " + line);
    }

    /// "desktop: panes (physical px on screen): clock=x,y,w,h ..." -> that pane's screen rect (the last line).
    static Win32.RECT PaneOf(string dir, string key)
    {
        var ms = Regex.Matches(FrameProbe.Log(dir), @"desktop: panes \(physical px on screen\):[^\r\n]* " + key + @"=(-?\d+),(-?\d+),(\d+),(\d+)");
        Assert.IsTrue(ms.Count > 0, "no rect for the " + key + " pane:\n" + Tail(dir));
        var g = ms[^1].Groups;
        int x = int.Parse(g[1].Value, CultureInfo.InvariantCulture), y = int.Parse(g[2].Value, CultureInfo.InvariantCulture);
        return new Win32.RECT { Left = x, Top = y, Right = x + int.Parse(g[3].Value, CultureInfo.InvariantCulture), Bottom = y + int.Parse(g[4].Value, CultureInfo.InvariantCulture) };
    }

    [TestMethod]
    public void The_desktop_panes_and_the_desktop_menu_paint_the_desktop_still_sharp_lined_up_under_their_own_tint()
    {
        FrameProbe.CursorAway();
        string dir = FrameProbe.NewDir("glass-desk");
        string still = Path.Combine(dir, "still.png");
        WritePng(still);
        var env = new Dictionary<string, string> { ["LOOK_SYNC_DIR"] = Fixture("desk", still), ["MANTLE_TEST_GLASS"] = "", ["MANTLE_TEST_DESKTOP"] = DesktopSurfaceTests.Fixture };
        using var app = BarApp.Launch("glass-desk-" + Guid.NewGuid().ToString("N")[..4], theme: "dark", env: env, extra: new[] { "--bench-out=" + dir });
        int pid = app.Proc.Id;
        IntPtr layer = Host.Until(() => FrameProbe.Find(pid, "Mantle desktop") is var h && FrameProbe.Showing(h) ? (object)h : null, 15000) is IntPtr l ? l : IntPtr.Zero;
        Assert.AreNotEqual(IntPtr.Zero, layer, "the desktop layer did not show:\n" + Tail(dir));
        // an ordinary window in a --test run: put at the top of the plain z-order so nothing of the runner's sits over it
        SetWindowPos(layer, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        Host.LogContains(() => FrameProbe.Log(dir), "desktop: panes (physical px on screen): clock=", "the pane rects were reported", 10000);
        double s = Math.Max(96u, GetDpiForWindow(layer)) / 96.0;
        int e = (int)Math.Round(16 * s);   // clear of the 16 DIP corners
        foreach (var key in new[] { "clock", "stats", "mc" }) {
            Host.LogContains(() => FrameProbe.Log(dir), "glass desktop pane " + key + ": painted the desktop still", "the " + key + " pane painted the still", 8000);
            StringAssert.Matches(FrameProbe.Log(dir), new Regex("glass desktop pane " + key + @": painted the desktop still [^\r\n]*tint dark" + AutoWords));
            // the pane's padding inside its hairline: a band along each edge (its words sit inside)
            PaneLined(dir, "desktop pane " + key, layer, () => {
                var r = PaneOf(dir, key);
                int a = (int)Math.Ceiling(2 * s), z = (int)Math.Floor(9 * s);
                return new[] { (r.Left + e, r.Right - e, r.Top + a, r.Top + z), (r.Left + e, r.Right - e, r.Bottom - z, r.Bottom - a), (r.Left + a, r.Left + z, r.Top + e, r.Bottom - e), (r.Right - z, r.Right - a, r.Top + e, r.Bottom - e) };
            }, true, "dark-desk-" + key);
        }
        StringAssert.DoesNotMatch(FrameProbe.Log(dir), new Regex("glass desktop pane [a-z]+: no picture"), "a pane fell back to its plain fill");

        // the menu: its panel inset 20 DIP in its window; the padding along its right and bottom edges
        Second(app, env, "--test-surface=show:deskmenu");
        Host.LogContains(() => FrameProbe.Log(dir), "glass desktop menu: painted the desktop still", "the menu painted the still", 8000);
        IntPtr menu = FrameProbe.Find(pid, "Mantle desktop menu");
        var mm = Regex.Match(FrameProbe.Log(dir), @"desktop: menu opened at (-?\d+),(-?\d+) (\d+)x(\d+) px");
        Assert.IsTrue(mm.Success, "no menu rect:\n" + Tail(dir));
        int mx = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture), my = int.Parse(mm.Groups[2].Value, CultureInfo.InvariantCulture);
        int mw = int.Parse(mm.Groups[3].Value, CultureInfo.InvariantCulture), mh = int.Parse(mm.Groups[4].Value, CultureInfo.InvariantCulture);
        int inset = (int)Math.Round(20 * s), p0 = (int)Math.Ceiling(1 * s), p1 = (int)Math.Floor(5 * s), c = (int)Math.Round(12 * s);
        PaneLined(dir, "desktop menu", menu, () => {
            int L = mx + inset, T = my + inset, R = mx + mw - inset, B = my + mh - inset;
            return new[] { (R - p1, R - p0, T + c, B - c), (L + c, R - c, B - p1, B - p0), (L + c, R - c, T + p0, T + p1) };
        }, true, "dark-desk-menu");
        Assert.AreEqual(1, Count(FrameProbe.Log(dir), "glass: the desktop still 1600x900 cover-fit"), "a pane or the menu decoded the still again (it is shared):\n" + Tail(dir));
    }
}
