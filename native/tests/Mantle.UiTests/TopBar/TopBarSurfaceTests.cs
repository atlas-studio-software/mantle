using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// The top menu bar ([Surface("topbar", AtStart = true)] in Mantle.App/Surfaces/TopBar, with [Feature("topbar")]):
/// made at start and parked, then shown at the primary display's top edge; never topmost in --test; 28 DIP tall at
/// 100 % and 150 %; every control found by AutomationId and pressed; light and dark; Win+Alt+T (--test-chord, the hook's
/// own road) hides and shows it; and while hidden the real cursor resting on the top edge peeks it. The app bar is a DRY
/// run in --test: the work area never moves. Every run uses its own profile and MANTLE_TEST_TOPBAR's made-up app
/// (Notepad++) and stats, so nothing is read from the runner's own windows for the strip.
/// </summary>
[TestClass]
public class TopBarSurfaceTests
{
    const string Title = "Mantle top bar";

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "", Profile = "";
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    static ProcessStartInfo Start(string profile, string ud, string theme, params string[] extra)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", "--theme=" + theme }) psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        psi.Environment["MANTLE_TEST_TOPBAR"] = "app=npp&stats=ok";
        return psi;
    }

    static App Launch(string profile, string theme = "light")
    {
        Host.Guard();
        Win32.DpiAware();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        var psi = Start(profile, ud, theme);
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud, Profile = profile };
        var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Bar(app) is var b && b != IntPtr.Zero ? (object)b : null, 30000);
        if (h is not IntPtr) Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its top bar was made" : "no '" + Title + "' window in 30 s:\n" + Tail(app.Log));
        return app;
    }

    static void Second(App app, string theme, params string[] extra)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, theme, extra))!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + string.Join(' ', extra) + ") did not exit in 15 s");
        Assert.AreEqual(0, p.ExitCode, "the second start (" + string.Join(' ', extra) + "): " + o.Result + e.Result);
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(40));

    /// The top bar window: made at start, so it exists (hidden) before the feature shows it.
    static IntPtr Bar(App app)
    {
        var all = new List<IntPtr>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); if (pid == app.Proc.Id && Win32.Title(h) == Title) all.Add(h); return true; }, IntPtr.Zero);
        return all.Count > 0 ? all[0] : IntPtr.Zero;
    }

    /// Parked, then shown by the feature; at the primary's top-left, full width, 28 px tall at 100 %; never topmost and the
    /// work area untouched (the app bar is a dry run in --test).
    static IntPtr Up(App app)
    {
        Host.LogContains(() => app.Log, "surface topbar: made", "the topbar surface was not made at start", 15000);
        Host.LogContains(() => app.Log, "topbar: made hidden and parked (test: visible)", "the surface did not park its window");
        IntPtr bar = Bar(app);
        Host.LogContains(() => app.Log, "topbar: shown (", "the feature never showed the bar", 15000);
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(bar), 3000), "the top bar window is not visible after 'shown'");
        Host.LogContains(() => app.Log, "topbar: the feature knows the window", "the surface's topbar.placed was never answered");
        Host.LogContains(() => app.Log, "dry run (--test): would have reserved", "the --test app bar step left no dry-run line", 8000);
        Assert.IsFalse(app.Log.Contains("topbar: app bar the window is ready: reserved", StringComparison.Ordinal), "a --test run reserved the strip");
        Assert.AreEqual(0, FrameProbe.WorkArea().Top, "the work area's top moved: a --test run took space on the screen");
        Assert.IsTrue((GetWindowLongPtrW(bar, -20).ToInt64() & 0x8) == 0, "a --test top bar is WS_EX_TOPMOST");
        Assert.IsTrue((GetWindowLongPtrW(bar, -20).ToInt64() & 0x80) != 0, "the top bar has a taskbar button (no WS_EX_TOOLWINDOW)");
        return bar;
    }

    /// The window is at the primary display's top-left, the display's full width and round(28 x scale) px tall.
    static void Geometry(App app, IntPtr bar, int pct)
    {
        var mon = Win32.MonitorRect(bar);
        int want = (int)Math.Round(28 * pct / 100.0, MidpointRounding.AwayFromZero);
        string line = $"placed at {mon.Left},{mon.Top} {mon.Right - mon.Left}x{want} px at {pct} % (dpi {Math.Round(96 * pct / 100.0)})";
        Host.LogContains(() => app.Log, line, $"{pct} %: no placement line for {want} px", 15000);
        FrameNativeRect(bar, out var r);
        string got = $"{pct} %: window {r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}, display {mon.Left},{mon.Top} {mon.Right - mon.Left}x{mon.Bottom - mon.Top}";
        Console.WriteLine(got + (app.Log.Contains(", cropped to ", StringComparison.Ordinal) ? " (Windows kept it taller; a region crops it)" : " (an exact height, no crop)"));
        Assert.AreEqual(mon.Left, r.Left, "left: " + got);
        Assert.AreEqual(mon.Top, r.Top, "top: " + got);
        Assert.AreEqual(mon.Right - mon.Left, r.Right - r.Left, "width: " + got);
        Assert.AreEqual(want, r.Bottom - r.Top, "height (28 DIP): " + got);
    }

    static string ReadShared(string f) { try { using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs).ReadToEnd(); } catch (IOException) { return ""; } }

    static void FrameNativeRect(IntPtr h, out Win32.RECT r) => FrameProbe.GetWindowRect(h, out r);

    /// The strip's face where nothing is drawn on it: the 22 DIP controls are centred in the 28 DIP strip, so rows 1..2
    /// DIP from the top hold only the fill, across the middle half of the width.
    static double Face(IntPtr bar)
    {
        FrameProbe.GetWindowRect(bar, out var r);
        double s = Win32.GetDpiForWindow(bar) / 96.0;
        int w = r.Right - r.Left;
        return Host.Luminance(new Rectangle(r.Left + w / 4, r.Top + (int)Math.Ceiling(1 * s), w / 2, Math.Max(1, (int)(1.5 * s))));
    }

    static AutomationElement Find(AutomationElement win, string id)
    {
        var e = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId(id)), 4000);
        Assert.IsNotNull(e, "no " + id + " on the top bar");
        return e!;
    }

    /// The control's own road through UI Automation (Invoke), then its log line.
    static void Press(App app, AutomationElement win, string id, string line)
    {
        Find(win, id).AsButton().Invoke();
        Host.LogContains(() => app.Log, line, id + " was pressed but logged nothing", 4000);
    }

    /// TOP-001/002/004/018..025/201/215/216/218/222/240: made, parked, shown at the top, the strip's controls, both themes.
    [TestMethod]
    public void Top_bar_is_parked_then_shown_at_the_top_never_topmost_every_control_pressed_and_themed()
    {
        using var app = Launch("topbar-strip");
        IntPtr bar = Up(app);
        Geometry(app, bar, FrameProbe.PrimaryScale());
        Host.LogContains(() => app.Log, "focus: Notepad++, ", "the made-up app's focus never reached the strip", 6000);
        var win = Host.Uia.FromHandle(bar);

        // the left: the Mantle glyph, the bold app name, the app's first menu; » only when the menus do not fit
        Assert.AreEqual("Mantle", Find(win, "topbar.title.mantle").Name);
        Assert.AreEqual("Notepad++", Find(win, "topbar.title.app").Name);
        Assert.AreEqual("File", Find(win, "topbar.title.m0").Name);
        Host.LogContains(() => app.Log, "topbar: fit: ", "the strip never fitted its titles", 4000);
        Host.Screenshot("topbar-light");
        Press(app, win, "topbar.title.mantle", "topbar: title pressed: mantle (open;");
        Press(app, win, "topbar.title.mantle", "topbar: title pressed: mantle (closed;");
        Press(app, win, "topbar.title.app", "topbar: title pressed: app (open;");
        Press(app, win, "topbar.title.m0", "topbar: title pressed: m0 (open;");
        Press(app, win, "topbar.title.m0", "topbar: title pressed: m0 (closed;");
        if (System.Text.RegularExpressions.Regex.IsMatch(app.Log, @"topbar: fit: \d+ of \d+ titles shown[^\r\n]*the rest in »"))
        {
            Press(app, win, "topbar.more", "topbar: title pressed: more (open;");
            Press(app, win, "topbar.more", "topbar: title pressed: more (closed;");   // closed: the titles are fitted again
        }
        else Console.WriteLine("every menu fits on this display, so there is no » to press");

        // the right: the stat cells (fixed decimals from the made-up stats) and the two tools
        foreach (var (k, label) in new[] { ("cpu", "CPU 23%"), ("mem", "RAM 9.7/15.7 GB"), ("gpu", "GPU 8%"), ("net", "NET ↓2.4 MB/s ↑88 KB/s"), ("ping", "PING 23 ms") })
        {
            Assert.AreEqual(label, Find(win, "topbar.stat." + k).Name, "the " + k + " cell");
            Press(app, win, "topbar.stat." + k, "topbar: stat pressed: " + k);
        }
        Assert.AreEqual("Timer and stopwatch", Find(win, "topbar.tool.timer").Name);
        Press(app, win, "topbar.tool.timer", "topbar: tool pressed: timer");
        Assert.AreEqual("Quick notes", Find(win, "topbar.tool.notes").Name);
        Press(app, win, "topbar.tool.notes", "topbar: tool pressed: notes");
        Assert.IsFalse(app.Proc.HasExited, "the app exited while the strip's controls were pressed");

        // dark: the strip follows the host's theme event (theme.set, as a page calls it)
        Thread.Sleep(400);
        double light = Face(bar);
        Second(app, "light", "--test-theme=dark");
        Host.LogContains(() => app.Log, "mantle: test-theme dark: ok", "theme.set dark was not answered", 8000);
        Host.LogContains(() => app.Log, "topbar: theme dark", "the strip did not repaint in dark", 4000);
        Thread.Sleep(600);
        double dark = Face(bar);
        Host.Screenshot("topbar-dark");
        Console.WriteLine($"the strip's face reads {light:N0} light, {dark:N0} dark");
        Assert.IsTrue(dark < light - 40, $"the dark strip reads {dark:N0}, not clearly darker than light's {light:N0}");
        Assert.IsTrue(light > 140, $"the light strip reads {light:N0} (want > 140: a 62 % white fill over any backdrop)");
    }

    /// TOP-203/282/283/286/287/289..296, BAR-348/349: Win+Alt+T hides it (the strip stays a dry run), the real cursor
    /// resting on the top edge peeks it and leaving hides it again, then Win+Alt+T shows it. Started in dark (--theme).
    [TestMethod]
    public void Win_Alt_T_hides_it_the_top_edge_peeks_it_and_Win_Alt_T_shows_it_again_in_dark()
    {
        FrameProbe.CursorAway();
        using var app = Launch("topbar-chord", "dark");
        IntPtr bar = Up(app);
        Host.LogContains(() => app.Log, "topbar: theme dark", "a --theme=dark start did not paint the strip dark", 4000);
        Thread.Sleep(400);
        double dark = Face(bar);
        Host.Screenshot("topbar-dark-start");
        Assert.IsTrue(dark < 110, $"the dark strip reads {dark:N0} (want < 110)");

        Second(app, "dark", "--test-chord=Win+Alt+T");
        Host.LogContains(() => app.Log, "topbar: hidden by hand (Win+Alt+T); Win+Alt+T or \"Show top bar\" in quick settings shows it again", "Win+Alt+T did not hide the bar", 6000);
        Host.LogContains(() => app.Log, "topbar: hidden by hand: a --test instance holds no strip, so there was none to let go", "the hide was not a dry run");
        Host.LogContains(() => app.Log, "topbar: hidden (", "the surface did not hide the window");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(bar), 3000), "the window is still visible after Win+Alt+T");
        string? store = Host.Until(() => Directory.GetFiles(app.Ud, "topbar.json", SearchOption.AllDirectories).FirstOrDefault(f =>
            System.Text.RegularExpressions.Regex.IsMatch(ReadShared(f), "\"hidden\"\\s*:\\s*true")), 3000);
        Assert.IsNotNull(store, "hidden by hand was not written to a topbar.json in the run's own user data (" + app.Ud + ")");
        Console.WriteLine("hidden by hand is kept in " + store);
        Host.LogContains(() => app.Log, "topbar: peek: watching the top edge", "the peek did not start watching once hidden", 4000);

        // the peek: the real cursor rests on the top edge for over 500 ms, then leaves for over 600 ms
        FrameProbe.GetWindowRect(bar, out var r);
        FrameProbe.SetCursorPos(r.Left + (r.Right - r.Left) / 2, r.Top);
        Host.LogContains(() => app.Log, "topbar: peek shown: the cursor rested on the top edge for ", "the cursor on the top edge did not peek the bar", 6000);
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(bar), 3000), "the peek was logged but the window is not visible");
        Assert.AreEqual(0, FrameProbe.WorkArea().Top, "the peek moved the work area (it must stay over the screen)");
        Host.Screenshot("topbar-peek");
        FrameProbe.CursorAway();
        Host.LogContains(() => app.Log, "topbar: peek hidden after ", "leaving the bar did not hide the peek", 6000);
        Host.LogContains(() => app.Log, "the cursor left the bar ");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(bar), 3000), "the peek's window is still visible after the cursor left");

        Second(app, "dark", "--test-chord=Win+Alt+T");
        Host.LogContains(() => app.Log, "topbar: app bar shown by hand: a --test instance never reserves space on the screen", "Win+Alt+T did not show the bar again", 6000);
        Host.LogContains(() => app.Log, "topbar: peek: stopped watching the top edge: shown by hand");
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(bar), 4000), "the window is not visible after the second Win+Alt+T");
        Assert.AreEqual(0, FrameProbe.WorkArea().Top, "showing it again moved the work area in --test");
    }

    /// TOP-215/218/240/254..256: a scale change re-places the bar at once for the new dpi, round(28 x scale) px tall,
    /// without a restart. Ends back at 100 %.
    [TestMethod]
    public void A_scale_change_re_places_it_28_DIP_tall_at_150_percent_without_a_restart()
    {
        Win32.DpiAware();
        if (FrameProbe.PrimaryScale() != 100) { FrameProbe.SetScale(100); Thread.Sleep(3000); }
        Assert.AreEqual(100, FrameProbe.PrimaryScale(), "the runner's display is not at 100 % to start from");
        using var app = Launch("topbar-dpi");
        IntPtr bar = Up(app);
        try
        {
            Geometry(app, bar, 100);
            foreach (int pct in new[] { 150, 100 })
            {
                string set = FrameProbe.SetScale(pct);
                Assert.IsTrue(set.StartsWith("ok", StringComparison.Ordinal), $"the runner refused {pct} %: {set}");
                uint dpi = (uint)Math.Round(96 * pct / 100.0);
                Assert.IsTrue(Host.Until(() => Win32.GetDpiForWindow(bar) == dpi, 15000), $"{pct} %: the top bar's dpi stayed {Win32.GetDpiForWindow(bar)} (want {dpi})");
                Geometry(app, bar, pct);
                Host.LogContains(() => app.Log, "topbar: display changed (", $"{pct} %: the feature did not hear the display change", 6000);
                Assert.AreEqual(bar, Bar(app), $"{pct} %: the window was replaced, not re-placed");
                Assert.IsTrue(Win32.IsWindowVisible(bar), $"{pct} %: the bar is not visible after the change");
                Assert.AreEqual(0, FrameProbe.WorkArea().Top, $"{pct} %: the work area moved in --test");
                Host.Screenshot("topbar-dpi-" + pct);
            }
        }
        finally { if (FrameProbe.PrimaryScale() != 100) FrameProbe.SetScale(100); }
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
}
