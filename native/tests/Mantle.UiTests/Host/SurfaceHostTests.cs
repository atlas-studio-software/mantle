using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Surfaces;

/// <summary>
/// The surface host (Mantle.App/Shell/Host): every [Surface] of the generated catalog is made by the host, AtStart ones
/// at start (hidden, parked), the others on first use; surface.show/hide/toggle reach them; --open from a second start
/// opens one; quit destroys them. Driven with the app's two test stand-ins ("test-parked", AtStart, and "test-lazy"),
/// which exist only in a --test run with MANTLE_TEST_SURFACES=1. Every run is the test's own profile.
/// </summary>
[TestClass]
public class SurfaceHostTests
{
    const string Prefix = "Mantle surface: ";

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "", Profile = "";
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    static ProcessStartInfo Start(string profile, string ud, bool standIns, params string[] extra)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", "--theme=light" }) psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        if (standIns) psi.Environment["MANTLE_TEST_SURFACES"] = "1";
        return psi;
    }

    static App Launch(string profile, bool standIns)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        var psi = Start(profile, ud, standIns);
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud, Profile = profile };
        var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle) is { Count: > 0 } w ? w[0] : null, 30000);
        if (h is not IntPtr) Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its bar appeared" : "no bar window in 30 s");
        return app;
    }

    /// A second start on the same profile: its exit code and what it printed.
    static (int Code, string Out) Second(App app, bool standIns, params string[] extra)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, standIns, extra))!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + string.Join(' ', extra) + ") did not exit in 15 s");
        return (p.ExitCode, o.Result + e.Result);
    }

    [TestMethod]
    public void Catalog_surfaces_are_made_parked_shown_on_first_use_driven_by_commands_and_destroyed_at_quit()
    {
        using var app = Launch("uitest-surfaces", standIns: true);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("surface test-parked: made at start, hidden and parked"), 15000), "the AtStart stand-in was not made at start:\n" + Tail(app.Log));
        StringAssert.Contains(app.Log, "test-parked: made hidden (test stand-in); quiet False; state mode test");
        // made, parked and hidden: its window exists but is not on screen; the on-first-use one does not exist yet
        Assert.IsTrue(Host.Until(() => Windows(app.Proc.Id, Prefix + "test-parked").Count == 1, 5000), "no window for the parked stand-in");
        Assert.IsFalse(Win32.IsWindowVisible(Windows(app.Proc.Id, Prefix + "test-parked")[0]), "the parked stand-in is on screen before anyone showed it");
        Assert.AreEqual(0, Windows(app.Proc.Id, Prefix + "test-lazy").Count, "the on-first-use stand-in was made before its first use");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("test-parked: theme light reached it on its UI thread: True"), 5000), "the theme replay never reached the parked stand-in:\n" + Tail(app.Log));

        // --open from a second start: made on first use, shown, and its context answers a host command on its UI thread
        var open = Second(app, true, "--open=test-lazy");
        Assert.AreEqual(0, open.Code, "the --open second start: " + open.Out);
        StringAssert.Contains(open.Out, "asked the running Mantle");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: opened test-lazy for a second start (--open=test-lazy, a test stand-in)"), 8000), "--open=test-lazy did not open it:\n" + Tail(app.Log));
        StringAssert.Contains(app.Log, "surface test-lazy: made on first use (a second start (--open=test-lazy))");
        Assert.IsTrue(Host.Until(() => Win32.WindowsOf(app.Proc.Id, Prefix + "test-lazy").Count == 1, 5000), "the opened stand-in is not on screen");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("test-lazy: state.get answered ok (mode test) on its UI thread: True"), 5000), "the stand-in's command did not come back on its UI thread:\n" + Tail(app.Log));
        var lazy = Host.Uia.FromHandle(Win32.WindowsOf(app.Proc.Id, Prefix + "test-lazy")[0]);
        var text = Host.Until(() => lazy.FindFirstDescendant(cf => cf.ByAutomationId("surface.test-lazy")), 5000);
        Assert.IsNotNull(text, "no surface.test-lazy text in the stand-in");
        StringAssert.Contains(text!.Name, "test-lazy: sidecar ");   // it renders from the State snapshot it was given

        // surface.toggle / surface.hide through the generated commands
        Assert.AreEqual(0, Second(app, true, "--test-surface=toggle:test-parked").Code);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-surface toggle:test-parked: ok: test-parked is shown"), 8000), "toggle did not show it:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Win32.WindowsOf(app.Proc.Id, Prefix + "test-parked").Count == 1, 3000), "the toggled stand-in is not on screen");
        Assert.AreEqual(0, Second(app, true, "--test-surface=hide:test-parked").Code);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-surface hide:test-parked: ok: test-parked is hidden"), 8000), "hide did not hide it:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Win32.WindowsOf(app.Proc.Id, Prefix + "test-parked").Count == 0, 3000), "the hidden stand-in is still on screen");
        Assert.AreEqual(1, Windows(app.Proc.Id, Prefix + "test-parked").Count, "hide destroyed the parked window instead of keeping it");

        // a name nobody built, and an allowed --open with no surface yet: words, never a silent no-op
        Second(app, true, "--test-surface=show:nothing-here");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-surface show:nothing-here: NOT ok: there is no nothing-here surface in this build, so nothing was shown"), 8000), "an unknown surface was not answered in words:\n" + Tail(app.Log));
        Assert.AreEqual(0, Second(app, true, "--open=wallpapers").Code);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: a second start asked for Wallpapers (--open=wallpapers), but it did not open: there is no wallpapers surface in this build"), 8000), "--open=wallpapers was not answered:\n" + Tail(app.Log));

        // quit: the ONE shutdown destroys every surface
        var quit = Second(app, true, "--quit");
        Assert.AreEqual(0, quit.Code, "--quit: " + quit.Out);
        Assert.IsTrue(app.Proc.WaitForExit(20000), "the app did not quit in 20 s:\n" + Tail(app.Log));
        string log = app.Log;
        StringAssert.Contains(log, "surface test-parked: destroyed (");
        StringAssert.Contains(log, "surface test-lazy: destroyed (");
        StringAssert.Contains(log, "test-lazy: destroyed (");
        Assert.IsFalse(log.Contains("UNHANDLED"), "an unhandled error in the run:\n" + Tail(log));
    }

    [TestMethod]
    public void A_test_run_that_does_not_ask_for_the_stand_ins_makes_none_and_says_why()
    {
        using var app = Launch("uitest-surfaces-off", standIns: false);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("test stand-ins not made in this run: test-lazy, test-parked"), 15000), "the host did not say it skipped the stand-ins:\n" + Tail(app.Log));
        Thread.Sleep(500);
        Assert.AreEqual(0, Windows(app.Proc.Id, null).Count(h => Win32.Title(h).StartsWith(Prefix, StringComparison.Ordinal)), "a stand-in window was made in a run that did not ask for one");
        Assert.AreEqual(0, Second(app, false, "--open=test-lazy").Code);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("but it did not open: test-lazy is a test stand-in; this --test run did not ask for it (MANTLE_TEST_SURFACES=1)"), 8000), "--open of a skipped stand-in was not answered:\n" + Tail(app.Log));
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(30));

    // every top-level window of the process, shown or not (Win32.WindowsOf lists visible ones only)
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static List<IntPtr> Windows(int pid, string? title)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid && (title == null || Win32.Title(h) == title)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
