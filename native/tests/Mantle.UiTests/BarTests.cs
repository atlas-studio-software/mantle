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

    public static BarApp Launch(string profile, string theme = "light", bool wait = true, IDictionary<string, string>? env = null)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath))
            Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
        Directory.CreateDirectory(ud);
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", $"--theme={theme}" }) psi.ArgumentList.Add(a);
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

    public IEnumerable<string> SeenIds() =>
        Window.FindAllDescendants().Select(x => x.Properties.AutomationId.ValueOrDefault ?? "").Where(s => s.Length > 0).Distinct();

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
        var missing = ContractIds.Where(id => app.TryFind(id, id == ContractIds[0] ? 8000 : 500) == null).ToList();
        Assert.AreEqual(0, missing.Count, $"missing ids: {string.Join(", ", missing)}; present: [{string.Join(", ", app.SeenIds())}]");
    }

    [TestMethod]
    public void Pill_sits_where_the_spec_says()
    {
        using var app = BarApp.Launch("uitest-geo");
        var r = Rect(app.Find("bar.pill"));
        var mon = Win32.MonitorRect(app.Hwnd);
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        int monW = mon.Right - mon.Left;
        int wantW = (int)Math.Round(Math.Min(1120, monW / s - 28) * s), wantH = (int)Math.Round(48 * s), wantGap = (int)Math.Round(14 * s);
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
        // Read, then compare with the clock read around it: the bar ticks once a second.
        var before = DateTime.Now;
        string t = timeEl.Name ?? "", d = dateEl.Name ?? "";
        var after = DateTime.Now;
        Assert.IsTrue(DateTime.TryParseExact(t, "h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var shown),
            $"time text '{t}' is not h:mm:ss AM/PM");
        var shownToday = before.Date + shown.TimeOfDay;
        Assert.IsTrue(shownToday >= before.AddSeconds(-2.5) && shownToday <= after.AddSeconds(1.5),
            $"time '{t}' vs the system clock {before:HH:mm:ss.fff}-{after:HH:mm:ss.fff}");
        var want = new[] { before.ToString("d", CultureInfo.CurrentCulture), after.ToString("d", CultureInfo.CurrentCulture) };
        CollectionAssert.Contains(want, d, $"date text '{d}', want '{want[0]}'");
        // It keeps ticking.
        Assert.IsTrue(Host.Until(() => (timeEl.Name ?? "") != t, 3000), $"the time stayed '{t}' for 3 s");
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
        bool Fg(IntPtr h) => Win32.GetForegroundWindow() == h;

        // Make sure A is not the window in front before the first click (a click on the front app minimizes it).
        if (Fg(a.Hwnd)) { Click(btnB!); Assert.IsTrue(Host.Until(() => Fg(b.Hwnd), 4000), "clicking B's button did not bring B forward"); }

        Click(btnA);
        Assert.IsTrue(Host.Until(() => Fg(a.Hwnd) && !Win32.IsIconic(a.Hwnd), 4000), $"click 1: A not in front (foreground {Win32.Title(Win32.GetForegroundWindow())})");
        Click(btnA);
        Assert.IsTrue(Host.Until(() => Win32.IsIconic(a.Hwnd), 4000), "click 2 on the app in front: A was not minimized");
        Click(btnA);
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

    [TestMethod]
    public void Dark_theme_draws_a_darker_pill_than_light()
    {
        double Lum(string theme)
        {
            using var app = BarApp.Launch("uitest-theme-" + theme, theme);
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
