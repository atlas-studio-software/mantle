using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Theme;

/// <summary>
/// The GC-then-theme regression: WinUI's SystemBackdrop base OnDefaultSystemBackdropConfigurationChanged throws when it
/// is called with a null target, which happens on a theme change after a full garbage collection, and that stopped
/// Mantle about 6 ms after the bar turned dark. The other theme tests switch before the first full GC, so they never saw
/// it. This one forces a full, blocking GC inside the running app (a --test second start's --test-gc) before every
/// theme change (theme.set light, dark, light from a dark start, so all three are real changes), and after each one
/// checks that the app is alive and that the bar (made at start) and the launcher, the OSD and the toasts (shown
/// through the test road, --test-surface=show:&lt;name&gt;) still answer through UIA. Everything is the test's own profile.
/// </summary>
[TestClass]
public class GcThemeTests
{
    const string Profile = "uitest-gctheme";
    const string LauncherTitle = "Mantle launcher", OsdTitle = "Mantle volume", ToastsTitle = "Mantle notifications";
    const int ToastBase = 990000;   // ToastsFeature's TestIdBase: testShow's banners are 990001, 990002, ...

    // one sticky banner per show: the first before the GCs, one more after each theme change
    static readonly string Toasts = "[" + string.Join(",", Enumerable.Range(1, 4).Select(i =>
        $"{{\"cmd\":\"testShow\",\"n\":1,\"sticky\":true,\"title\":\"GC theme {i}\",\"body\":\"A test banner shown before a theme change.\"}}")) + "]";

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "";
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    static ProcessStartInfo Start(string ud, params string[] extra)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--test", $"--profile-name={Profile}", $"--user-data={ud}" }) psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        psi.Environment["MANTLE_TEST_OSD"] = "{\"level\":62,\"ms\":60000}";
        psi.Environment["MANTLE_TEST_TOASTS"] = Toasts;
        return psi;
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(40));

    static void Second(App app, string arg)
    {
        Alive(app, "before " + arg);
        using var p = Process.Start(Start(app.Ud, arg))!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), $"the second start ({arg}) did not exit in 15 s");
        Assert.AreEqual(0, p.ExitCode, $"the second start ({arg}): " + o.Result + e.Result);
    }

    static void Alive(App app, string when)
    {
        if (app.Proc.HasExited) Assert.Fail($"Mantle stopped {when} (exit code {app.Proc.ExitCode}):\n" + Tail(app.Log));
    }

    static AutomationElement Window(App app, string title, string what)
    {
        var h = Host.Until(() => Win32.WindowsOf(app.Proc.Id, title) is { Count: > 0 } w ? (object)w[0] : null, 8000);
        if (h is not IntPtr hwnd) throw new AssertFailedException($"no {what} window ('{title}') {Tail(app.Log)}");
        return Host.Uia.FromHandle(hwnd);
    }

    /// The bar, then each shown surface (shown again first: a show of a shown surface is harmless), answers through UIA.
    static void Answers(App app, string when, int toast)
    {
        Alive(app, when);
        var bar = Window(app, BarApp.WindowTitle, "bar");
        Assert.IsNotNull(Host.Until(() => bar.FindFirstDescendant(cf => cf.ByAutomationId("bar.pill")), 8000), $"the bar does not answer through UIA {when}:\n" + Tail(app.Log));

        Second(app, "--test-surface=show:launcher");
        var launcher = Window(app, LauncherTitle, "launcher");
        Assert.IsNotNull(Host.Until(() => launcher.FindFirstDescendant(cf => cf.ByAutomationId("q")), 8000), $"the launcher does not answer through UIA {when}:\n" + Tail(app.Log));

        Second(app, "--test-surface=show:osd");
        var osd = Window(app, OsdTitle, "OSD");
        var num = Host.Until(() => osd.FindFirstDescendant(cf => cf.ByAutomationId("num")), 8000);
        Assert.IsNotNull(num, $"the OSD does not answer through UIA {when}:\n" + Tail(app.Log));
        Assert.AreEqual("62", num!.Name, $"the OSD's number {when}");

        Second(app, "--test-surface=show:toasts");
        var toasts = Window(app, ToastsTitle, "toasts");
        string id = "title-" + (ToastBase + toast);
        Assert.IsNotNull(Host.Until(() => toasts.FindFirstDescendant(cf => cf.ByAutomationId(id)), 8000),
            $"the toasts do not answer through UIA {when} (no {id}):\n" + Tail(app.Log));
        Alive(app, when);
        Console.WriteLine($"{when}: bar, launcher, OSD and toasts answer through UIA");
    }

    [TestMethod]
    public void A_full_GC_before_each_theme_change_leaves_the_app_and_every_surface_answering()
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + Profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        File.WriteAllText(Path.Combine(ud, "theme.json"), "{\n  \"mode\": \"dark\"\n}\n");   // dark, so light, dark, light are three real changes
        var psi = Start(ud);
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        using var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud };
        try
        {
            var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle) is { Count: > 0 } w ? w[0] : null, 30000);
            if (h is not IntPtr) Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its bar appeared" : "no bar window in 30 s");
            Assert.IsTrue(Host.Until(() => app.Log.Contains("theme: Mantle is dark"), 8000), "the host never said its theme:\n" + Tail(app.Log));
            Thread.Sleep(1500);   // first frame + backdrop
            Answers(app, "at start (dark)", 1);

            int round = 0;
            foreach (var mode in new[] { "light", "dark", "light" })
            {
                round++;
                const string gcSaid = "mantle: test-gc: ok: ";
                int gcs = app.Log.Split('\n').Count(l => l.Contains(gcSaid));
                Second(app, "--test-gc");
                Assert.IsTrue(Host.Until(() => app.Log.Split('\n').Count(l => l.Contains(gcSaid)) > gcs, 15000), $"the full GC of round {round} was not logged:\n" + Tail(app.Log));
                Console.WriteLine(app.Log.Split('\n').Last(l => l.Contains(gcSaid)).Trim());
                Alive(app, $"after the full GC of round {round}");

                string said = $"mantle: test-theme {mode}: ok: ";
                int sets = app.Log.Split('\n').Count(l => l.Contains(said));
                Second(app, "--test-theme=" + mode);
                Assert.IsTrue(Host.Until(() => app.Proc.HasExited || app.Log.Split('\n').Count(l => l.Contains(said)) > sets, 8000), $"theme.set {mode} did not answer ok:\n" + Tail(app.Log));
                Alive(app, $"on the theme change to {mode} after a full GC (round {round})");
                Thread.Sleep(1500);   // the crash came about 6 ms after the bar turned; give every surface time to take the theme
                Answers(app, $"after the full GC and the change to {mode} (round {round})", round + 1);
            }
        }
        finally
        {
            File.WriteAllText(Path.Combine(Host.OutDir, "uitest-gctheme-native-log.txt"), app.Log);
        }
    }
}
