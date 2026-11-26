using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Mantle.UiTests;

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static void Init(TestContext _)
    {
        Win32.DpiAware();
        if (Host.Allowed) Win32.TaskbarAutoHide(true);   // the runner's own taskbar must not cover the bar
    }

    [AssemblyCleanup]
    public static void Done()
    {
        Host.KillAll();
        // The runner taskbar stays on auto-hide: later CI stages (bench, parity) measure the bar at the
        // bottom edge too, and switching it back would undo their setup.
    }
}

/// <summary>One running copy of the native app, launched as a visible test instance with its own profile.</summary>
internal sealed class BarApp : IDisposable
{
    public const string WindowTitle = "Mantle bar";
    public Process Proc { get; }
    public string Profile { get; }
    public IntPtr Hwnd { get; private set; }
    public AutomationElement Window { get; private set; } = null!;

    BarApp(Process p, string profile) { Proc = p; Profile = profile; }

    public static BarApp Launch(string profile, string theme = "light", bool wait = true, IDictionary<string, string>? env = null, IEnumerable<string>? extra = null)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath))
            Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
        Directory.CreateDirectory(ud);
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", $"--theme={theme}" }) psi.ArgumentList.Add(a);
        if (extra != null) foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var app = new BarApp(Host.Track(Process.Start(psi)!), profile);
        if (wait) app.WaitWindow(30000);
        return app;
    }

    public void WaitWindow(int ms)
    {
        var h = Host.Until(() =>
        {
            if (Proc.HasExited) return (object)"exited";
            var w = Win32.WindowsOf(Proc.Id, WindowTitle);
            return w.Count > 0 ? (object)w[0] : null;
        }, ms);
        if (h is string) Assert.Fail($"the app exited (code {Proc.ExitCode}) before its \"{WindowTitle}\" window appeared");
        if (h == null)
        {
            var titles = string.Join(" | ", Win32.WindowsOf(Proc.Id).Select(Win32.Title));
            Assert.Fail($"no visible \"{WindowTitle}\" window from pid {Proc.Id} in {ms} ms; its visible windows: [{titles}]");
        }
        Hwnd = (IntPtr)h!;
        Window = Host.Uia.FromHandle(Hwnd);
    }

    public AutomationElement? TryFind(string id, int ms = 8000) =>
        Host.Until(() => Window.Properties.AutomationId.ValueOrDefault == id ? Window : Window.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);

    public AutomationElement Find(string id, int ms = 8000)
    {
        var e = TryFind(id, ms);
        if (e == null) Assert.Fail($"no element with AutomationId '{id}'; ids present: [{string.Join(", ", SeenIds())}]");
        return e!;
    }

    /// <summary>
    /// The AutomationIds in the window's tree now, for a failure message. The list is a snapshot: an element the app
    /// removes after FindAllDescendants (a dock cell whose window closed, a collapsed pip, a rebuilt tray strip) has no
    /// id left to read, and UIA says so with UIA_E_ELEMENTNOTAVAILABLE (0x80040201, which COM words as "an event was
    /// unable to invoke any of the subscribers"). Such an element is not in the tree, so it is counted as gone, not
    /// listed; any other failure still throws (run 45751506798: this walk threw out of a passing contract check).
    /// </summary>
    public IEnumerable<string> SeenIds()
    {
        var ids = new List<string>();
        int gone = 0;
        foreach (var x in Window.FindAllDescendants())
        {
            string id;
            try { id = x.Properties.AutomationId.ValueOrDefault ?? ""; }
            catch (Exception e) when (Gone(e)) { gone++; continue; }
            if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
        }
        if (gone > 0) ids.Add($"({gone} element{(gone == 1 ? "" : "s")} left the tree while it was read)");
        return ids;
    }

    /// UIA's "that element is no longer there": FlaUI's own exception, or the raw HRESULT it lets through on a property read.
    public static bool Gone(Exception e) =>
        e is FlaUI.Core.Exceptions.ElementNotAvailableException || e is System.Runtime.InteropServices.COMException { HResult: UiaElementNotAvailable };

    const int UiaElementNotAvailable = unchecked((int)0x80040201);

    public void Dispose() => Host.Kill(Proc);
}

[TestClass]
public class BarTests
{
    public TestContext TestContext { get; set; } = null!;

    static readonly string[] ContractIds =
    {
        "bar.window", "bar.pill", "bar.start", "bar.apps", "bar.clock", "bar.clock.time", "bar.clock.date",
        "bar.downloads", "bar.volume", "bar.network", "bar.notifications", "bar.more",
    };
    static readonly string[] ClusterIds = { "bar.start", "bar.downloads", "bar.volume", "bar.network", "bar.notifications", "bar.more" };

    [TestCleanup]
    public void Cleanup()
    {
        if (TestContext.CurrentTestOutcome != UnitTestOutcome.Passed && Host.Allowed) Host.Screenshot(TestContext.TestName ?? "test");
        Host.KillAll();
    }

    static Rectangle Rect(AutomationElement e) => e.BoundingRectangle;
    static Point Centre(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [TestMethod]
    public void Window_has_the_contract_title_and_every_contract_id()
    {
        using var app = BarApp.Launch("uitest-ids");
        Assert.AreEqual(BarApp.WindowTitle, Win32.Title(app.Hwnd));
        // Each id is its own fresh query of the live tree (TryFind). The whole-tree listing is only for the failure
        // message, so it runs only on a failure: MSTest 3.6 builds an Assert's message before the check.
        var missing = ContractIds.Where(id => app.TryFind(id, id == ContractIds[0] ? 8000 : 500) == null).ToList();
        if (missing.Count > 0) Assert.Fail($"missing ids: {string.Join(", ", missing)}; present: [{string.Join(", ", app.SeenIds())}]");
    }

    [TestMethod]
    public void Pill_sits_where_the_spec_says()
    {
        using var app = BarApp.Launch("uitest-geo");
        var r = Rect(app.Find("bar.pill"));
        var mon = Win32.MonitorRect(app.Hwnd);
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        int monW = mon.Right - mon.Left;
        // lane r13-bar-islands: the pill is the islands' band, the screen less 12 DIP each side (no 1120 cap), 44 tall, 12 up
        int wantW = monW - 2 * (int)Math.Round(12 * s), wantH = (int)Math.Round(44 * s), wantGap = (int)Math.Round(12 * s);
        string got = $"pill {r.X},{r.Y} {r.Width}x{r.Height} on monitor {mon.Left},{mon.Top}-{mon.Right},{mon.Bottom} at {s * 100:0}%";
        Assert.IsTrue(Math.Abs(r.Width - wantW) <= 2, $"width {r.Width}, want {wantW}: {got}");
        Assert.IsTrue(Math.Abs(r.Height - wantH) <= 2, $"height {r.Height}, want {wantH}: {got}");
        Assert.IsTrue(Math.Abs(mon.Bottom - r.Bottom - wantGap) <= 2, $"gap to the bottom edge {mon.Bottom - r.Bottom}, want {wantGap}: {got}");
        Assert.IsTrue(Math.Abs((r.Left + r.Right) / 2 - (mon.Left + mon.Right) / 2) <= 2, $"not centred: {got}");
    }

    [TestMethod]
    public void Clock_shows_the_system_time_and_date()
    {
        using var app = BarApp.Launch("uitest-clock");
        var timeEl = app.Find("bar.clock.time");
        var dateEl = app.Find("bar.clock.date");
        // Read, then compare with the clock read around it. Lane r14-bar-review: the islands clock is the prototype's
        // h:mm (shell.js tick), 12- or 24-hour by the locale, never seconds, so it ticks on the minute.
        var before = DateTime.Now;
        string t = timeEl.Name ?? "", d = dateEl.Name ?? "";
        var after = DateTime.Now;
        var f = CultureInfo.CurrentCulture.DateTimeFormat;
        string[] forms = { "h:mm tt", "H:mm" };
        bool parsed = DateTime.TryParseExact(t, forms, CultureInfo.InvariantCulture, DateTimeStyles.None, out var shown)
            || DateTime.TryParseExact(t, forms, CultureInfo.CurrentCulture, DateTimeStyles.None, out shown);
        Assert.IsTrue(parsed, $"time text '{t}' is not h:mm (12-hour with {f.AMDesignator}/{f.PMDesignator}, or 24-hour), or it has seconds");
        var shownToday = before.Date + shown.TimeOfDay;
        Assert.IsTrue(shownToday >= before.AddSeconds(-before.Second - 2.5) && shownToday <= after.AddSeconds(1.5),
            $"time '{t}' vs the system clock {before:HH:mm:ss.fff}-{after:HH:mm:ss.fff}");
        var want = new[] { before.ToString("d", CultureInfo.CurrentCulture), after.ToString("d", CultureInfo.CurrentCulture) };
        CollectionAssert.Contains(want, d, $"date text '{d}', want '{want[0]}'");
        // It keeps ticking: at the next minute.
        int toNext = (60 - DateTime.Now.Second) * 1000 + 3000;
        Assert.IsTrue(Host.Until(() => (timeEl.Name ?? "") != t, toNext), $"the time stayed '{t}' past the next minute ({toNext} ms)");
    }

    [TestMethod]
    public void Running_app_button_focuses_minimizes_and_restores_its_window()
    {
        using var app = BarApp.Launch("uitest-apps");
        string tag = Guid.NewGuid().ToString("N")[..6];
        using var a = new Host.Fixture($"UiFixture A {tag}", 60, 60);
        using var b = new Host.Fixture($"UiFixture B {tag}", 520, 60, "pwsh.exe");   // another exe: the bar groups by app
        AutomationElement? Btn(Host.Fixture f) => Host.AppButtons(app.Window).FirstOrDefault(x => (x.Name ?? "").Contains(f.Title));
        var btnA = Host.Until(() => Btn(a), 10000);
        var btnB = Host.Until(() => Btn(b), 10000);
        var names = string.Join(" | ", Host.AppButtons(app.Window).Select(x => $"{x.Properties.AutomationId.ValueOrDefault}={x.Name}"));
        Assert.IsNotNull(btnA, $"no bar.app.<n> button named '{a.Title}' in 10 s; app buttons: [{names}]");
        Assert.IsNotNull(btnB, $"no bar.app.<n> button named '{b.Title}' in 10 s; app buttons: [{names}]");
        Assert.AreEqual("powershell.exe", (btnA!.Properties.HelpText.ValueOrDefault ?? "").ToLowerInvariant(), "HelpText = the exe file name");

        void Click(AutomationElement e) { Mouse.Click(Centre(Rect(e))); Thread.Sleep(400); }
        // Running-only apps keep their first-seen place (BAR-289, better: than Electron, whose z-order order moves a
        // focused app to the front and a minimised one to the back). Each click still finds the button again once it
        // has stopped moving (two reads 1.2 s apart agree), and the button is checked not to have moved.
        AutomationElement Settled(Host.Fixture f)
        {
            AutomationElement? e = null;
            Host.Until(() => {
                var r1 = Btn(f) is { } x1 ? Rect(x1) : default;
                Thread.Sleep(1200);
                e = Btn(f);
                return e != null && Rect(e) == r1;
            }, 8000, 0);
            return e ?? throw new AssertFailedException($"{f.Title}'s button is gone");
        }
        bool Fg(IntPtr h) => Win32.GetForegroundWindow() == h;

        // Make sure A is not the window in front before the first click (a click on the front app minimizes it).
        if (Fg(a.Hwnd)) { Click(Settled(b)); Assert.IsTrue(Host.Until(() => Fg(b.Hwnd), 4000), "clicking B's button did not bring B forward"); }

        var at0 = Rect(Settled(a));
        Click(Settled(a));
        Assert.IsTrue(Host.Until(() => Fg(a.Hwnd) && !Win32.IsIconic(a.Hwnd), 4000), $"click 1: A not in front (foreground {Win32.Title(Win32.GetForegroundWindow())})");
        var at1 = Rect(Settled(a));
        Click(Settled(a));
        Assert.IsTrue(Host.Until(() => Win32.IsIconic(a.Hwnd), 4000), "click 2 on the app in front: A was not minimized");
        var at2 = Rect(Settled(a));
        Assert.AreEqual(at0, at1, "BAR-289: A's button moved when A was focused (running-only apps keep their first-seen place)");
        Assert.AreEqual(at0, at2, "BAR-289: A's button moved when A was minimised (running-only apps keep their first-seen place)");
        Click(Settled(a));
        Assert.IsTrue(Host.Until(() => !Win32.IsIconic(a.Hwnd) && Fg(a.Hwnd), 4000), "click 3: A was not restored to the front");

        a.Dispose();
        Assert.IsTrue(Host.Until(() => Btn(a) == null, 8000), "A's button stayed after its window closed");
        Assert.IsNotNull(Btn(b), "B's button went away with A's");
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }

    [TestMethod]
    public void Right_cluster_buttons_invoke_without_crashing()
    {
        using var app = BarApp.Launch("uitest-cluster");
        var before = new HashSet<IntPtr>(Win32.VisibleTitled());
        foreach (var id in ClusterIds)
        {
            var e = app.Find(id, 3000);
            Assert.IsTrue(e.IsEnabled, $"{id} is disabled");
            Assert.IsTrue(e.Patterns.Invoke.IsSupported, $"{id} has no Invoke pattern");
            e.Patterns.Invoke.Pattern.Invoke();
            Thread.Sleep(500);
            Keyboard.Type(VirtualKeyShort.ESCAPE);   // close whatever it opened
            Thread.Sleep(200);
            Assert.IsFalse(app.Proc.HasExited, $"the app exited after {id} was invoked (code {(app.Proc.HasExited ? app.Proc.ExitCode : 0)})");
        }
        Assert.IsTrue(Win32.IsWindow(app.Hwnd) && Win32.IsWindowVisible(app.Hwnd), "the bar window is gone after the cluster clicks");
        // Close what the buttons opened (e.g. a File Explorer or console window): later CI stages share this desktop.
        var opened = Win32.VisibleTitled().Where(h => !before.Contains(h) && h != app.Hwnd).ToList();
        foreach (var h in opened) { Console.WriteLine($"closing a window the cluster opened: {Win32.Title(h)}"); Win32.PostMessageW(h, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Host.Until(() => opened.All(h => !Win32.IsWindowVisible(h)), 5000);
    }

    [TestMethod]
    public void Second_launch_with_the_same_profile_exits_and_another_profile_runs_alongside()
    {
        using var first = BarApp.Launch("uitest-single");
        using var second = BarApp.Launch("uitest-single", wait: false);
        Assert.IsTrue(second.Proc.WaitForExit(15000), "the second copy with the same profile is still running after 15 s");
        Assert.IsFalse(first.Proc.HasExited, "the first copy exited when the second started");
        Assert.IsTrue(Win32.IsWindowVisible(first.Hwnd), "the first copy's bar is gone");
        using var other = BarApp.Launch("uitest-single-other");
        Assert.IsFalse(other.Proc.HasExited, "a copy with another profile did not keep running");
        Assert.IsFalse(first.Proc.HasExited, "the first copy exited when another profile started");
    }

    [TestMethod]
    public void Close_and_kill_leave_no_process_behind()
    {
        int baseline = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Host.ExePath)).Length;
        using (var app = BarApp.Launch("uitest-close"))
        {
            Win32.PostMessageW(app.Hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Assert.IsTrue(app.Proc.WaitForExit(10000), "WM_CLOSE on the bar window: still running after 10 s");
        }
        using (var app = BarApp.Launch("uitest-kill"))
        {
            app.Proc.Kill();
            Assert.IsTrue(app.Proc.WaitForExit(5000), "kill: still running after 5 s");
        }
        Assert.IsTrue(Host.Until(() => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Host.ExePath)).Length <= baseline, 5000),
            "an app process is still running after close + kill");
    }

    /// The islands' case of the theme tint check below (which runs the electron face), lane r14-bar-review: the frost is the
    /// same picture in both themes, but in light the island is lifted toward #F4F7F8 (apps-r3/LIGHT.md section 4b; over the
    /// flat #2c5258 fallback a test run shows, 0.60 at the seed dim) and in dark it is not (dark film over a pale picture),
    /// so the dark island is clearly the darker. Probed on the left island above the M, the pointer kept away.
    [TestMethod]
    public void Dark_theme_islands_tint_follows_the_theme()
    {
        string said = "";
        double Lum(string theme)
        {
            using var app = BarApp.Launch("uitest-islands-tint-" + theme, theme);
            var r = Rect(app.Find("bar.island.left"));
            double s = r.Height / 44.0;   // the island is 44 DIP tall
            Mouse.Position = new Point(r.Right + (int)Math.Round(400 * s), Math.Max(0, r.Y - (int)Math.Round(300 * s)));   // no hover sheen
            string log = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-uitest-islands-tint-" + theme, "mantle-native.log");
            Host.Until(() => Read(log).Contains("levitate bar left island: "), 8000);
            Thread.Sleep(1500);   // first frame, and the dim and the lift laid
            var probe = new Rectangle(r.X + (int)Math.Round(14 * s), r.Y + (int)Math.Round(3 * s), Math.Max(4, (int)Math.Round(16 * s)), Math.Max(2, (int)Math.Round(5 * s)));
            said += $" {theme}: " + string.Join(" / ", Read(log).Split('\n').Where(l => l.Contains("levitate bar left island: ", StringComparison.Ordinal)).TakeLast(2).Select(l => l.Trim()));
            return Host.Luminance(probe);
        }
        double light = Lum("light"), dark = Lum("dark");
        Console.WriteLine($"left island luminance: light {light:0.0}, dark {dark:0.0};{said}");
        Assert.IsTrue(dark + 30 < light, $"the dark island ({dark:0.0}) is not clearly darker than the light one ({light:0.0}): the light lift did not reach the islands?{said}");
    }

    static string Read(string path)
    {
        try {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        } catch (IOException) { return ""; }
    }

    [TestMethod]
    public void Dark_theme_draws_a_darker_pill_than_light()
    {
        double Lum(string theme)
        {
            // the pill's tint is the electron face's (lane r13-bar-islands: the islands' frost is the same picture in both
            // themes, only the film differs, and the band's gaps show the wallpaper), so this run keeps that face
            var env = new Dictionary<string, string> { ["MANTLE_TEST_GLASS"] = "electron" };
            using var app = BarApp.Launch("uitest-theme-" + theme, theme, env: env);
            var r = Rect(app.Find("bar.pill"));
            Thread.Sleep(1200);   // first frame + backdrop
            // An empty stretch of glass: right of the start button, left of the centred apps.
            var probe = new Rectangle(r.X + 64, r.Y + r.Height / 2 - 6, Math.Min(160, r.Width / 5), 12);
            return Host.Luminance(probe);
        }
        double light = Lum("light"), dark = Lum("dark");
        Console.WriteLine($"pill luminance: light {light:0.0}, dark {dark:0.0}");
        Assert.IsTrue(dark + 30 < light, $"dark {dark:0.0} is not clearly darker than light {light:0.0}");
    }
}
