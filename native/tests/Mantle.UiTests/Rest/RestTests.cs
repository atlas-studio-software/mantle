using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Rest;

/// <summary>
/// Mantle at rest for a game (main.js restNow / wakeNow; App: RestCoordinator + ShellRestParts): a rest destroys every
/// catalog surface's window and keeps the bar; a wake makes the AtStart ones again (hidden), the others wait for their
/// next use; nothing is left reserved on the screen at any point, nor after the quit. Driven by the test-only second
/// start --test-rest=on|off:&lt;why&gt; (host.rest, the call game mode makes), with the app's test stand-ins
/// ("test-parked", AtStart, and "test-lazy", on first use). Every run is the test's own profile.
/// </summary>
[TestClass]
public class RestTests
{
    const string Prefix = "Mantle surface: ";

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "", Profile = "";
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    static ProcessStartInfo Start(string profile, string ud, params string[] extra)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", "--theme=light" }) psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        psi.Environment["MANTLE_TEST_SURFACES"] = "1";
        return psi;
    }

    static (int Code, string Out) Second(App app, params string[] extra)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, extra))!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + string.Join(' ', extra) + ") did not exit in 15 s");
        return (p.ExitCode, o.Result + e.Result);
    }

    [TestMethod]
    public void A_rest_destroys_the_surfaces_windows_keeps_the_bar_and_a_wake_brings_the_AtStart_ones_back_with_nothing_reserved()
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var workBefore = WorkArea();
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-rest-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        var psi = Start("uitest-rest", ud, "--dock");   // --dock asks for an app bar: a test run's passive sidecar must refuse it
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        using var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud, Profile = "uitest-rest" };
        var bar = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle) is { Count: > 0 } w ? w[0] : null, 30000);
        if (bar is not IntPtr) Assert.Fail(bar is string ? $"the app exited (code {app.Proc.ExitCode}) before its bar appeared" : "no bar window in 30 s");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("surface test-parked: made at start, hidden and parked"), 15000), "the AtStart stand-in was not made at start:\n" + Tail(app.Log));
        bool launcher = app.Log.Contains("surface launcher: made at start");
        Assert.AreEqual(0, Second(app, "--open=test-lazy").Code);
        Assert.IsTrue(Host.Until(() => Windows(app.Proc.Id, Prefix + "test-lazy").Count == 1, 8000), "--open=test-lazy made no window:\n" + Tail(app.Log));
        Assert.AreEqual(1, Windows(app.Proc.Id, Prefix + "test-parked").Count, "no parked stand-in window before the rest");
        AssertWorkArea(workBefore, "before the rest");

        // the rest: every surface window goes; the bar stays
        Assert.AreEqual(0, Second(app, "--test-rest=on:uitest-game runs").Code);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-rest on: ok: closed "), 15000), "the rest did not answer:\n" + Tail(app.Log));
        string restLine = Line(app.Log, "mantle: test-rest on: ok: ");
        StringAssert.Contains(restLine, "test-parked");
        StringAssert.Contains(restLine, "test-lazy");
        if (launcher) StringAssert.Contains(restLine, "launcher");
        StringAssert.Contains(restLine, "kept: bar, the link to Windows and the host");
        Host.LogContains(() => app.Log, "mantle: rest: resting while uitest-game runs in ");
        Host.LogContains(() => app.Log, "surfaces: rest: destroyed ");
        Assert.AreEqual(0, Windows(app.Proc.Id, Prefix + "test-parked").Count, "the parked stand-in's window outlived the rest");
        Assert.AreEqual(0, Windows(app.Proc.Id, Prefix + "test-lazy").Count, "the opened stand-in's window outlived the rest");
        Assert.AreEqual(1, Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle).Count, "the bar did not stay through the rest");
        Assert.AreEqual(0, Windows(app.Proc.Id, null).Count(h => Win32.Title(h).StartsWith(Prefix, StringComparison.Ordinal)), "a surface window is left during the rest");
        AssertWorkArea(workBefore, "during the rest");
        // nothing is shown while resting, and it says why
        Second(app, "--test-surface=show:test-lazy");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-surface show:test-lazy: NOT ok: Mantle is resting while a game runs (uitest-game runs), so test-lazy was not shown"), 8000), "a show during the rest was not refused in words:\n" + Tail(app.Log));
        Assert.AreEqual(0, Windows(app.Proc.Id, Prefix + "test-lazy").Count, "a surface was made during the rest");

        // the wake: the AtStart ones are made again (hidden), the on-first-use one waits for its next use
        Assert.AreEqual(0, Second(app, "--test-rest=off:uitest-game exited").Code);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-rest off: ok: back after 0 min (uitest-game exited): "), 15000), "the wake did not answer:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Windows(app.Proc.Id, Prefix + "test-parked").Count == 1, 5000), "the AtStart stand-in was not made again on the wake:\n" + Tail(app.Log));
        Assert.IsFalse(Win32.IsWindowVisible(Windows(app.Proc.Id, Prefix + "test-parked")[0]), "the stand-in made again on the wake is on screen");
        Assert.AreEqual(0, Windows(app.Proc.Id, Prefix + "test-lazy").Count, "the on-first-use stand-in was made on the wake, before its next use");
        Host.LogContains(() => app.Log, "surfaces: wake: made again ");
        if (launcher) StringAssert.Contains(Line(app.Log, "mantle: test-rest off: ok: "), "launcher");
        Assert.IsTrue(Host.Until(() => Count(app.Log, "test-parked: theme light reached it on its UI thread: True") >= 2, 5000), "the stand-in made again did not get the theme:\n" + Tail(app.Log));
        // it comes back on use, as before the rest
        Assert.AreEqual(0, Second(app, "--open=test-lazy").Code);
        Assert.IsTrue(Host.Until(() => Win32.WindowsOf(app.Proc.Id, Prefix + "test-lazy").Count == 1, 8000), "test-lazy did not open after the wake:\n" + Tail(app.Log));
        AssertWorkArea(workBefore, "after the wake");

        // quit: through the ONE shutdown; nothing reserved afterwards
        Assert.AreEqual(0, Second(app, "--quit").Code);
        Assert.IsTrue(app.Proc.WaitForExit(20000), "the app did not quit in 20 s:\n" + Tail(app.Log));
        string log = app.Log;
        StringAssert.Contains(log, "the app bar was not reserved");   // --dock asked; the passive sidecar refused it
        Assert.IsFalse(log.Contains("dock: removing the app bar took over 4 s"), "the bar waited on a sidecar that had stopped:\n" + Tail(log));
        Assert.IsFalse(log.Contains("UNHANDLED"), "an unhandled error in the run:\n" + Tail(log));
        Host.Until(() => Process.GetProcessesByName("Mantle.Sidecar").All(p => { try { return p.HasExited || p.StartTime < app.Proc.StartTime; } catch (Exception) { return true; } }), 5000);
        AssertWorkArea(workBefore, "after the quit");
    }

    static string Line(string log, string start) => log.Split('\n').Select(l => l.TrimEnd('\r')).LastOrDefault(l => l.Contains(start)) ?? "";
    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(30));
    static int Count(string log, string what) { int n = 0, i = 0; while ((i = log.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top} {Right}x{Bottom}"; }
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, out RECT r, uint ini);
    static RECT WorkArea() { SystemParametersInfoW(0x0030, 0, out var r, 0); return r; }   // SPI_GETWORKAREA
    static void AssertWorkArea(RECT before, string when)
    {
        var now = WorkArea();
        Assert.AreEqual(before.ToString(), now.ToString(), $"the work area moved {when}: something is reserved on the screen");
    }

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
