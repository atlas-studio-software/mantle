using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Desktop;

/// <summary>
/// The desktop layer ([Surface("desktop")], Mantle.App/Surfaces/Desktop) and its menu ([Surface("deskmenu")], the test
/// road), and hot corners ([Feature("corners")]) through their self-test. Every run is --test with the test's own
/// profile: the layer is an ordinary window that is NEVER put under the icons (a dry run, logged), the right-click hook
/// is never armed, the corners never read or move the real cursor and every corner action is a dry run.
/// MANTLE_TEST_DESKTOP freezes the clock and the readings, so every runner draws the same panes.
/// </summary>
[TestClass]
public class DesktopSurfaceTests
{
    const string Title = "Mantle desktop", MenuTitle = "Mantle desktop menu";
    public const string Fixture = "{\"now\":\"2026-11-06T16:15:32\",\"stats\":{\"cpu\":7,\"memUsed\":20830591385,\"memTotal\":34359738368,\"battery\":{\"percent\":81,\"charging\":true},\"batteryReason\":\"ok\"}," +
        "\"mc\":\"connect ECONNREFUSED 127.0.0.1:7373\",\"strip\":[[\"Sidecar\",\"it exited (code 3), so Mantle cannot read the taskbar or the tray; restart Mantle\"],[\"Desktop layer\",\"the desktop layer could not go under the wallpaper, so it is floating as an ordinary window\"]],\"menuAt\":[300,200]}";

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "", Profile = "";
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    static ProcessStartInfo Start(string profile, string ud, string theme, IDictionary<string, string>? env, params string[] extra)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", $"--theme={theme}" }) psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        psi.Environment["MANTLE_TEST_DESKTOP"] = Fixture;
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    static App Launch(string profile, string theme = "light", IDictionary<string, string>? env = null)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        var psi = Start(profile, ud, theme, env);
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud, Profile = profile };
        var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, Title) is { Count: > 0 } w ? w[0] : null, 30000);
        if (h is not IntPtr) Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its desktop layer appeared" : "no desktop layer window in 30 s");
        return app;
    }

    static void Second(App app, params string[] extra)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, "light", null, extra))!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + string.Join(' ', extra) + ") did not exit in 15 s");
        Assert.AreEqual(0, p.ExitCode, "the second start (" + string.Join(' ', extra) + "): " + o.Result + e.Result);
    }

    static IntPtr Layer(App app) => Win32.WindowsOf(app.Proc.Id, Title).FirstOrDefault();

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);

    static AutomationElement Find(AutomationElement root, string id, int ms = 5000)
    {
        var el = Host.Until(() => root.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(el, $"no element {id} in {ms} ms");
        return el!;
    }

    static void Keep(App app, string name)
    {
        var dir = Path.Combine(Host.OutDir, "..", "parity", "desktop");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"uitest-{name}-native-log.txt"), app.Log);
    }

    [TestMethod]
    public void Desktop_layer_is_made_shown_never_attached_placed_drawn_themed_and_parked()
    {
        Win32.DpiAware();
        using var app = Launch("uitest-desktop");
        try {
            Host.LogContains(() => app.Log, "desktop: shown as an ordinary window (a test run never puts it under the icons)", "the layer is shown at start", 15000);
            Host.LogContains(() => app.Log, "desktop: dry run (--test): would ask the sidecar to put the layer under the icons (desktop {hwnd}); nothing was sent, Explorer was not touched", "the attach is a dry run");
            Host.LogContains(() => app.Log, "deskmenu: dry run (--test): would arm the desktop right-click hook", "the right-click hook is never armed in --test", 15000);
            Host.LogContains(() => app.Log, "desktop: the clock is frozen at 4:15:32 PM (MANTLE_TEST_DESKTOP)", "the fixture's clock");
            IntPtr h = Layer(app);
            Assert.IsTrue(Win32.IsWindowVisible(h), "the layer is visible");
            Assert.AreNotEqual(h, Win32.GetForegroundWindow(), "the layer never takes the foreground");
            double scale = Win32.GetDpiForWindow(h) / 96.0;
            GetWindowRect(h, out var wr);
            Assert.AreEqual(Math.Round(960 * scale), wr.Right - wr.Left, 2, "the test layer is 960 DIP wide");
            Assert.AreEqual(Math.Round(540 * scale), wr.Bottom - wr.Top, 2, "the test layer is 540 DIP tall");

            // the pane rects (physical px on screen): a 540 DIP window is "short" (desktop.css max-height 760): 30 / 22, 420 wide
            Host.LogContains(() => app.Log, "desktop: panes (physical px on screen): clock=", "the pane rects are reported", 10000);
            var m = Regex.Matches(app.Log, @"desktop: panes \(physical px on screen\): clock=(-?\d+),(-?\d+),(\d+),(\d+) stats=(-?\d+),(-?\d+),(\d+),(\d+) mc=(-?\d+),(-?\d+),(\d+),(\d+) strip=(-?\d+),(-?\d+),(\d+),(\d+)").Last();
            int G(int i) => int.Parse(m.Groups[i].Value);
            Assert.AreEqual(wr.Left + Math.Round(30 * scale), G(1), 2, "clock x");
            Assert.AreEqual(wr.Top + Math.Round(22 * scale), G(2), 2, "clock y");
            Assert.AreEqual(Math.Round(420 * scale), G(3), 2, "clock width");
            Assert.AreEqual(G(2) + G(4) + Math.Round(11 * scale), G(6), 2, "the System tile is 11 DIP under the clock");
            Assert.AreEqual(G(6) + G(8) + Math.Round(11 * scale), G(10), 2, "Mission Control is 11 DIP under the System tile");
            Assert.IsTrue(G(15) > 0 && G(16) > 0, "the strip has rows, so it is drawn");

            var win = Host.Uia.FromHandle(h);
            var clock = Find(win, "desktop.pane.clock");
            var b = clock.BoundingRectangle;
            Assert.AreEqual(G(1), b.X, 2, "UIA agrees with the reported clock x"); Assert.AreEqual(G(3), b.Width, 2, "UIA agrees with the clock width");
            Assert.AreEqual("4:15:32 PM", Find(win, "desktop.clock").Name, "the clock reads 12-hour with seconds");
            Assert.AreEqual("Friday, November 6", Find(win, "desktop.date").Name.Trim(), "the date line (en-US runner)");
            Assert.AreEqual("7%", Find(win, "desktop.cpu").Name);
            Assert.AreEqual("19.4 GB / 32.0 GB", Find(win, "desktop.mem").Name);
            Assert.AreEqual("81% · charging", Find(win, "desktop.battery").Name);
            StringAssert.StartsWith(Find(win, "desktop.mc.note").Name, "Mission Control offline: connect ECONNREFUSED 127.0.0.1:7373. Next check in 30 s.");
            StringAssert.StartsWith(Find(win, "desktop.strip.row.0").Name, "Sidecar — it exited (code 3)");
            StringAssert.StartsWith(Find(win, "desktop.strip.row.1").Name, "Desktop layer — the desktop layer could not go under");
            Assert.AreEqual("light", Find(win, "desktop.layer").Properties.ItemStatus.ValueOrDefault, "light at start");

            // the theme flips live
            Second(app, "--test-theme=dark");
            Host.LogContains(() => app.Log, "desktop: theme dark: the panes repainted", "the theme reaches the layer");
            Assert.IsTrue(Host.Until(() => Find(win, "desktop.layer").Properties.ItemStatus.ValueOrDefault == "dark", 3000), "the layer is dark");
            Second(app, "--test-theme=light");
            Host.LogContains(() => app.Log, "desktop: theme light: the panes repainted", "and back");

            // parked and shown again (the surface host's verbs)
            Second(app, "--test-surface=hide:desktop");
            Host.LogContains(() => app.Log, "desktop: parked, hidden", "parked");
            Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h), 3000), "parked = not visible");
            Assert.IsTrue(Win32.IsWindow(h), "parked, not destroyed");
            Second(app, "--test-surface=show:desktop");
            Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(h), 3000), "shown again");
            Host.Screenshot("desktop-layer-light");
        } finally { Keep(app, "desktop"); }
    }

    [TestMethod]
    public void Desktop_menu_opens_placed_lists_its_rows_hides_the_widgets_and_keeps_a_failure_up()
    {
        Win32.DpiAware();
        using var app = Launch("uitest-deskmenu");
        try {
            Host.LogContains(() => app.Log, "desktop: shown as an ordinary window", "the layer is up", 15000);
            IntPtr h = Layer(app);
            GetWindowRect(h, out var wr);
            Second(app, "--test-surface=show:deskmenu");
            Host.LogContains(() => app.Log, $"desktop: menu opened at", "the menu opens through the test road");
            var mm = Regex.Match(app.Log, @"desktop: menu opened at (-?\d+),(-?\d+) (\d+)x(\d+) px for the point (-?\d+),(-?\d+)");
            Assert.IsTrue(mm.Success);
            double scale = Win32.GetDpiForWindow(h) / 96.0;
            int px = int.Parse(mm.Groups[5].Value), py = int.Parse(mm.Groups[6].Value);
            Assert.AreEqual(wr.Left + 300, px); Assert.AreEqual(wr.Top + 200, py);
            Assert.AreEqual(px - Math.Round(20 * scale), int.Parse(mm.Groups[1].Value), 1, "the panel's corner is on the point (20 DIP margin)");
            Assert.AreEqual(py - Math.Round(20 * scale), int.Parse(mm.Groups[2].Value), 1, "right of and below it");
            IntPtr mh = IntPtr.Zero;
            Assert.IsTrue(Host.Until(() => (mh = Win32.WindowsOf(app.Proc.Id, MenuTitle).FirstOrDefault()) != IntPtr.Zero && Win32.IsWindowVisible(mh), 3000), "the menu window is shown");
            Assert.AreNotEqual(mh, Win32.GetForegroundWindow(), "the menu never takes the foreground");
            var menu = Host.Uia.FromHandle(mh);
            string[] ids = { "deskmenu.wallpaper", "deskmenu.display", "deskmenu.personalize", "deskmenu.widgets", "deskmenu.settings" };
            string[] names = { "Change wallpaper", "Display settings", "Personalize", "Hide desktop widgets", "Mantle Settings" };
            for (int i = 0; i < ids.Length; i++) Assert.AreEqual(names[i], Find(menu, ids[i]).Name, ids[i]);
            Host.Screenshot("desktop-menu-light");

            // Hide desktop widgets: saved in the deskmenu feature's settings, the panes hidden in place, the menu closes
            Find(menu, "deskmenu.widgets").AsButton().Invoke();
            Host.LogContains(() => app.Log, "deskmenu: the desktop widgets are hidden (the desktop menu)", "the switch is saved");
            Host.LogContains(() => app.Log, "desktop: widgets hidden (the desktop menu)", "the panes hide");
            Host.LogContains(() => app.Log, "desktop: menu closed (a pick)", "a successful pick closes the menu");
            var layer = Host.Uia.FromHandle(h);
            Assert.IsTrue(Host.Until(() => Find(layer, "desktop.pane.clock").Properties.ItemType.ValueOrDefault == "widget hidden", 3000), "the clock pane is hidden in place");
            var file = Directory.GetFiles(app.Ud, "deskmenu.json", SearchOption.AllDirectories).FirstOrDefault();
            Assert.IsNotNull(file, "the switch is kept in features\\deskmenu.json (not the page's localStorage)");
            Assert.IsTrue(Regex.IsMatch(File.ReadAllText(file!), "\"widgets\"\\s*:\\s*\"off\""), "widgets: off is saved");

            // open again: the row now says Show; a row whose command is not there keeps the menu up with why
            Second(app, "--test-surface=show:deskmenu");
            Assert.IsTrue(Host.Until(() => (mh = Win32.WindowsOf(app.Proc.Id, MenuTitle).FirstOrDefault()) != IntPtr.Zero && Win32.IsWindowVisible(mh), 3000), "the menu is shown again");
            menu = Host.Uia.FromHandle(mh);
            Assert.AreEqual("Show desktop widgets", Find(menu, "deskmenu.widgets").Name);
            Find(menu, "deskmenu.wallpaper").AsButton().Invoke();
            Host.LogContains(() => app.Log, "desktop: menu: Change wallpaper:", "the row ran");
            if (Regex.IsMatch(app.Log, "desktop: menu: Change wallpaper: did not work")) {
                var note = Find(menu, "deskmenu.note");
                StringAssert.StartsWith(note.Name, "Change wallpaper did not work: ", "a failed row keeps the menu up with its reason");
                Assert.IsTrue(Win32.IsWindowVisible(mh), "the menu stays up after a failed row");
            }
            Second(app, "--test-surface=hide:deskmenu");
            Host.LogContains(() => app.Log, "desktop: menu closed", "closed through the test road");
        } finally { Keep(app, "deskmenu"); }
    }

    [TestMethod]
    public void Hot_corners_self_test_every_corner_and_action_is_a_dry_run_and_the_real_cursor_is_never_used()
    {
        Win32.DpiAware();
        GetCursorPos(out var before);
        using var app = Launch("uitest-corners", env: new Dictionary<string, string> { ["MANTLE_CORNERS_SELFTEST"] = "1", ["MANTLE_CORNERS_TEST_GAME"] = "MantleNative.exe" });
        try {
            Host.LogContains(() => app.Log, "corners: stopped watching the corners: a test run never reads the real cursor", "the watch never reads the real cursor in --test", 15000);
            Host.LogContains(() => app.Log, "corners: SELFTEST: driving", "the self-test started", 15000);
            Assert.IsTrue(Host.Until(() => Regex.IsMatch(app.Log, "corners: SELFTEST (ok|FAILED)"), 90000), "the self-test finished in 90 s");
            string line = app.Log.Split('\n').Last(l => l.Contains("corners: SELFTEST ok") || l.Contains("corners: SELFTEST FAILED"));
            StringAssert.Contains(line, "corners: SELFTEST ok: 20 of 20 corner x action dry runs", line);
            StringAssert.Contains(line, "the game stand-in refused: MantleNative.exe is running (a known game)", line);
            StringAssert.Contains(app.Log, "corners: SELFTEST case br x desktop: DRY RUN (--test): would have called Shell.Application ToggleDesktop (IShellDispatch4)");
            StringAssert.Contains(app.Log, "corners: SELFTEST case tl x lock: DRY RUN (--test): would have called user32 LockWorkStation");
            GetCursorPos(out var after);
            Assert.AreEqual((before.X, before.Y), (after.X, after.Y), "the real cursor never moved");
        } finally { Keep(app, "corners"); }
    }

    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
}
