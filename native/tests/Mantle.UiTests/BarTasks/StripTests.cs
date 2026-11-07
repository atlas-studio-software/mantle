using System.Diagnostics;
using System.Drawing;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// The bar's strip (native-bar-tasks; docs/parity/bar.md "Pins, running apps, clicks"): pins come from a scratch
/// TaskBar folder of real .lnk files (MANTLE_TEST_PINS_DIR; a test run never reads the user's pins), running apps
/// from the sidecar's windows push, with fixture windows the test starts and closes by their own PID. Each cell's
/// UI Automation ItemStatus says what it draws ("pinned" / "running", "open", "active", "after divider"); the
/// running line is also checked in the real screen pixels. A test run never launches anything: a click on a pin
/// that is not running is a dry run that the log names.
/// </summary>
[TestClass]
public class StripTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestCleanup]
    public void Cleanup()
    {
        if (TestContext.CurrentTestOutcome != UnitTestOutcome.Passed && Host.Allowed) Host.Screenshot(TestContext.TestName ?? "test");
        Host.KillAll();
    }

    static string PowerShellExe => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    static string CharmapExe => Path.Combine(Environment.SystemDirectory, "charmap.exe");
    static string RegeditExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "regedit.exe");

    /// <summary>A scratch TaskBar folder: one .lnk per pin, written by WScript.Shell as Windows writes its own.</summary>
    static string PinFolder(string tag, params (string Name, string Target)[] pins)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-pins-" + tag);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var ps = new StringBuilder("$ErrorActionPreference = 'Stop'; $sh = New-Object -ComObject WScript.Shell; ");
        foreach (var (name, target) in pins)
            ps.Append($"$l = $sh.CreateShortcut({Q(Path.Combine(dir, name + ".lnk"))}); $l.TargetPath = {Q(target)}; $l.Save(); ");
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ps.ToString())) }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30000)) { Host.Kill(p); Assert.Fail("making the scratch pins took over 30 s"); }
        foreach (var (name, _) in pins)
            if (!File.Exists(Path.Combine(dir, name + ".lnk"))) Assert.Fail($"the scratch pin '{name}.lnk' was not made: {errTask.Result}");
        return dir;
    }

    sealed record Cell(int Index, string Name, string Status, Rectangle Rect, AutomationElement E)
    {
        public bool Pinned => Status.StartsWith("pinned", StringComparison.Ordinal);
        public bool Open => Status.Contains("open", StringComparison.Ordinal);
        public bool Active => Status.Contains("active", StringComparison.Ordinal);
        public bool AfterDivider => Status.Contains("after divider", StringComparison.Ordinal);
    }

    static List<Cell> Cells(BarApp app)
    {
        var list = new List<Cell>();
        foreach (var e in Host.AppButtons(app.Window)) {
            try {
                string id = e.Properties.AutomationId.ValueOrDefault ?? "";
                if (!int.TryParse(id.AsSpan("bar.app.".Length), out int n)) continue;
                list.Add(new Cell(n, e.Name ?? "", e.Properties.ItemStatus.ValueOrDefault ?? "", e.BoundingRectangle, e));
            } catch (Exception) { /* a cell re-rendered while it was read: the next poll reads it again */ }
        }
        return list.OrderBy(c => c.Index).ToList();
    }

    static string Describe(IEnumerable<Cell> cs) => "[" + string.Join(" | ", cs.Select(c => $"{c.Index}:{c.Name} ({c.Status}) x{c.Rect.X}")) + "]";

    static Cell? Named(List<Cell> cs, string name) => cs.FirstOrDefault(c => c.Name.Contains(name, StringComparison.Ordinal));

    /// <summary>
    /// The pixels that differ from the band's median under the icon, where the running line sits (bar.css .cell .ind:
    /// 3 px tall, 6 px or 16 px wide, 13 DIP below the cell's centre). A cell with no line has next to none.
    /// </summary>
    static int LinePixels(Cell c)
    {
        double s = c.Rect.Width / 36.0;   // the cell is 36 DIP wide
        int cx = c.Rect.X + c.Rect.Width / 2, cy = c.Rect.Y + c.Rect.Height / 2;
        int x = cx - (int)Math.Round(10 * s), y = cy + (int)Math.Round(10 * s), w = (int)Math.Round(20 * s), h = (int)Math.Round(8 * s);
        var px = Win32.Grab(x, y, w, h);
        var lum = new double[w * h];
        for (int i = 0; i < lum.Length; i++) lum[i] = 0.114 * px[i * 4] + 0.587 * px[i * 4 + 1] + 0.299 * px[i * 4 + 2];
        var sorted = lum.OrderBy(v => v).ToArray();
        double median = sorted[sorted.Length / 2];
        return lum.Count(v => Math.Abs(v - median) > 30);
    }

    static DateTime _lastClick = DateTime.MinValue;

    /// A click at the cell's centre; never within 700 ms of the last one, so two clicks on one cell are never a double-click.
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// The process whose top-level window is under a screen point (the runner's own taskbar can come up over the bar
    /// when the window in front goes away; a click there opens Windows' search instead of reaching the bar).
    static uint PidAt(Point p) { GetWindowThreadProcessId(GetAncestor(WindowFromPoint(p), 2), out uint pid); return pid; }

    static void Click(Cell c, MouseButton button = MouseButton.Left, BarApp? app = null)
    {
        var wait = TimeSpan.FromMilliseconds(700) - (DateTime.UtcNow - _lastClick);
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        var at = new Point(c.Rect.X + c.Rect.Width / 2, c.Rect.Y + c.Rect.Height / 2);
        if (app != null && !Host.Until(() => PidAt(at) == (uint)app.Proc.Id, 3000, 100)) {
            Win32.TaskbarAutoHide(true);
            if (!Host.Until(() => PidAt(at) == (uint)app.Proc.Id, 3000, 100))
                Assert.Fail($"the cell at {at} is covered by another window ({Win32.Title(GetAncestor(WindowFromPoint(at), 2))}): the click would not reach the bar");
        }
        Mouse.Click(at, button);
        _lastClick = DateTime.UtcNow;
        Thread.Sleep(400);
    }

    static string LogPath(string profile) => Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile, "mantle-native.log");

    static string LogSince(string profile, long from)
    {
        var path = LogPath(profile);
        if (!File.Exists(path)) return "";
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length <= from) return "";
        f.Seek(from, SeekOrigin.Begin);
        using var r = new StreamReader(f, Encoding.UTF8);
        return r.ReadToEnd();
    }

    /// The app's own lines about clicks, for a failure message.
    static string ClickLines(string profile) =>
        string.Join(" / ", LogSince(profile, 0).Split('\n').Where(l => l.Contains("bar tasks: click", StringComparison.Ordinal) || l.Contains("bar.open", StringComparison.Ordinal)).TakeLast(8).Select(l => l.Trim()));

    static long LogLength(string profile) => File.Exists(LogPath(profile)) ? new FileInfo(LogPath(profile)).Length : 0;

    [TestMethod]
    public void Pins_come_first_in_order_with_the_running_line_and_running_apps_follow_the_divider()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        // made out of order on purpose: with no Favorites value the pins fall back to alphabetical order (lib/pinned.js)
        var pins = PinFolder("order-" + tag, ("Gamma Reg", RegeditExe), ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        const string profile = "uitest-strip-order";
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
        using var a = new Host.Fixture($"Strip pinned {tag}", 60, 60);                 // powershell.exe: Alpha Shell's app
        using var b = new Host.Fixture($"Strip running {tag}", 520, 60, "pwsh.exe");   // pinned by nobody: after the divider

        var cs = new List<Cell>();
        bool all = Host.Until(() => {
            cs = Cells(app);
            return Named(cs, a.Title) is { Open: true } && Named(cs, b.Title) is { Open: true } && Named(cs, "Beta Map") != null && Named(cs, "Gamma Reg") != null;
        }, 20000, 250);
        Assert.IsTrue(all, $"in 20 s the strip did not show the three pins and both fixture windows; cells: {Describe(cs)}");

        // pins first, in their order; the pin of a running app is named after its window (bar.js updateCell)
        var first3 = cs.Take(3).ToList();
        Assert.IsTrue(first3.All(c => c.Pinned), $"the first three cells are not the three pins: {Describe(cs)}");
        CollectionAssert.AreEqual(new[] { a.Title, "Beta Map", "Gamma Reg" }, first3.Select(c => c.Name).ToArray(), $"pin order: {Describe(cs)}");
        Assert.AreEqual(3, cs.Count(c => c.Pinned), $"a pin shows twice, or a running app was taken for a pin: {Describe(cs)}");
        // on screen in the same order, left to right
        CollectionAssert.AreEqual(cs.OrderBy(c => c.Rect.X).Select(c => c.Index).ToArray(), cs.Select(c => c.Index).ToArray(), $"on-screen order differs from bar.app.<n>: {Describe(cs)}");

        // running indicator: the pinned app with a window, and the running-only app; none on the pins not running
        var alpha = Named(cs, a.Title)!; var beta = Named(cs, "Beta Map")!; var gamma = Named(cs, "Gamma Reg")!; var run = Named(cs, b.Title)!;
        Assert.IsTrue(alpha.Pinned && alpha.Open, $"Alpha Shell's pin did not claim its running window: {Describe(cs)}");
        Assert.IsFalse(beta.Open || gamma.Open, $"a pin that is not running says open: {Describe(cs)}");
        Assert.IsFalse(run.Pinned, $"the unpinned app is marked pinned: {Describe(cs)}");

        // running-only apps after the pins, the first of them after the hairline (one hairline only)
        var firstRun = cs.First(c => !c.Pinned);
        Assert.AreEqual(3, firstRun.Index, $"the first running-only app is not right after the pins: {Describe(cs)}");
        Assert.IsTrue(firstRun.AfterDivider, $"no hairline before the first running-only app: {Describe(cs)}");
        Assert.AreEqual(1, cs.Count(c => c.AfterDivider), $"more than one hairline: {Describe(cs)}");
        Assert.IsTrue(run.Index > 2, $"the unpinned fixture sits among the pins: {Describe(cs)}");

        // the line in the real pixels: under the open pin, not under a pin that is not running
        Thread.Sleep(600);
        cs = Cells(app);
        int onAlpha = LinePixels(Named(cs, a.Title)!), onBeta = LinePixels(Named(cs, "Beta Map")!);
        Assert.IsTrue(onAlpha >= 8, $"no running line drawn under the open pin ({onAlpha} px off the band's median; Beta Map {onBeta}); cells: {Describe(cs)}");
        Assert.IsTrue(onBeta <= 3, $"a line is drawn under a pin that is not running ({onBeta} px; the open pin {onAlpha})");

        // the active line follows the window in front. B opened last, so it may be in front, and a click on the app in
        // front minimises it: bring A forward first, then B, then A again
        void Front(Host.Fixture f, Host.Fixture other, string what) => Assert.IsTrue(Host.Until(() => {
            cs = Cells(app);
            return Win32.GetForegroundWindow() == f.Hwnd && Named(cs, f.Title) is { Active: true } && Named(cs, other.Title) is { Active: false };
        }, 6000, 200), $"{what}: {Describe(cs)} (foreground: {Win32.Title(Win32.GetForegroundWindow())}); the app's click lines: {ClickLines(profile)}");
        if (Win32.GetForegroundWindow() != a.Hwnd) { Click(Named(cs, a.Title)!); Front(a, b, "after a click on the pin its window is not the active one"); }
        Click(Named(cs, b.Title)!);
        Front(b, a, "after a click on the running-only app its window is not the active one");
        Click(Named(cs, a.Title)!);
        Front(a, b, "after a click on the pin its window is not the active one");
        Thread.Sleep(600);
        cs = Cells(app);
        int active = LinePixels(Named(cs, a.Title)!);
        Assert.IsTrue(active > onAlpha, $"the active line ({active} px) is not wider than the running line ({onAlpha} px)");

        // a window that closes leaves its pin in place, without the line; the running-only app goes
        a.Dispose(); b.Dispose();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return Named(cs, "Alpha Shell") is { Open: false } && Named(cs, b.Title) == null; }, 10000, 250),
            $"after both fixtures closed: {Describe(cs)}");
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }

    [TestMethod]
    public void Clicks_launch_a_pin_as_a_dry_run_focus_and_minimise_its_window_and_middle_click_asks_for_another()
    {
        const string profile = "uitest-strip-click";
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("click-" + tag, ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
        using var other = new Host.Fixture($"Strip other {tag}", 520, 60, "pwsh.exe");   // takes the front when A minimises
        using var a = new Host.Fixture($"Strip click {tag}", 60, 60);
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return Named(cs, a.Title) is { Open: true } && Named(cs, "Beta Map") != null; }, 20000, 250),
            $"in 20 s the strip did not show both pins with the fixture's window on Alpha Shell; cells: {Describe(cs)}");
        int charmaps = Process.GetProcessesByName("charmap").Length;

        // a pin that is not running: a test run says what it would have opened, and opens nothing (BAR-201)
        long at = LogLength(profile);
        Click(Named(cs, "Beta Map")!);
        string log = "";
        Assert.IsTrue(Host.Until(() => { log = LogSince(profile, at); return log.Contains("launch dry run", StringComparison.Ordinal); }, 6000, 200),
            $"no dry-run line after a click on a pin that is not running; the log since: {Tail(log)}");
        StringAssert.Contains(log, "would open", $"the dry run does not say it would open the shortcut: {Tail(log)}");
        StringAssert.Contains(log, "Beta Map.lnk", $"the dry run names another shortcut: {Tail(log)}");
        Thread.Sleep(800);
        Assert.AreEqual(charmaps, Process.GetProcessesByName("charmap").Length, "a test run started the pinned app");
        Assert.IsFalse(LogSince(profile, at).Contains("App did not open", StringComparison.Ordinal), "the dry run was reported as a failed open");

        // the pin of a running app (bar.open, BAR-192, BAR-198): the window in front minimises, any other comes to the front
        // (restored if minimised). The bar decides from its own marks (the sidecar's windows push), so each click waits
        // until they agree with Windows: a window outside the strip (the runner's search flyout once) can take the front
        bool Fg() => Win32.GetForegroundWindow() == a.Hwnd;
        Cell Alpha() { cs = Cells(app); return Named(cs, a.Title) ?? throw new AssertFailedException($"Alpha Shell's cell is gone: {Describe(cs)}"); }
        string Why() => $"{Describe(cs)} (foreground: {Win32.Title(Win32.GetForegroundWindow())}); the app's click lines: {ClickLines(profile)}";
        int minimised = 0, restored = 0, focused = 0;
        for (int k = 1; k <= 4; k++) {
            Assert.IsTrue(Host.Until(() => Alpha().Active == (Fg() && !Win32.IsIconic(a.Hwnd)), 4000, 100),
                $"click {k}: the bar's active mark never agreed with Windows: {Why()}");
            Thread.Sleep(300);
            bool wasActive = Alpha().Active, wasIconic = Win32.IsIconic(a.Hwnd);
            Click(Alpha(), app: app);
            if (wasActive) {
                Assert.IsTrue(Host.Until(() => Win32.IsIconic(a.Hwnd), 4000), $"click {k} on the pin of the window in front did not minimise it: {Why()}");
                Assert.IsTrue(Host.Until(() => !Alpha().Active && Alpha().Open, 4000), $"click {k}: the minimised window kept the active line or lost its running line: {Why()}");
                minimised++;
            } else {
                Assert.IsTrue(Host.Until(() => Fg() && !Win32.IsIconic(a.Hwnd), 4000), $"click {k} did not bring the pinned app's window to the front{(wasIconic ? " from minimised" : "")}: {Why()}");
                Assert.IsTrue(Host.Until(() => Alpha().Active, 4000), $"click {k}: the focused pin is not the active one: {Why()}");
                if (wasIconic) restored++; else focused++;
            }
        }
        Assert.IsTrue(minimised >= 1 && restored >= 1, $"4 clicks: {minimised} minimise(s), {restored} restore(s), {focused} focus(es); want at least one minimise and one restore");

        // middle-click: another copy, from the pin's shortcut, never the window already open (BAR-200)
        at = LogLength(profile);
        bool iconicBefore = Win32.IsIconic(a.Hwnd);
        Click(Alpha(), MouseButton.Middle, app);
        Assert.IsTrue(Host.Until(() => { log = LogSince(profile, at); return log.Contains("launch dry run", StringComparison.Ordinal); }, 6000, 200),
            $"no dry-run line after a middle-click on a running pin; the log since: {Tail(log)}");
        StringAssert.Contains(log, "would another copy of open", $"a middle-click did not ask for another copy: {Tail(log)}");
        StringAssert.Contains(log, "Alpha Shell.lnk", $"the middle-click dry run names another shortcut: {Tail(log)}");
        Assert.AreEqual(iconicBefore, Win32.IsIconic(a.Hwnd), "a middle-click minimised or restored the open window");
        StringAssert.Contains(log, "1 window(s), fresh", $"the middle-click was not a fresh open: {Tail(log)}");
        Assert.IsFalse(LogSince(profile, at).Contains("1 window(s), active", StringComparison.Ordinal), "a middle-click was also taken as a plain click");
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }

    static string Tail(string s) => s.Length <= 1500 ? s : "..." + s[^1500..];
}
