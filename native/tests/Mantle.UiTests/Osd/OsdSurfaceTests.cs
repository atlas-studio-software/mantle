using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Osd;

/// <summary>
/// The volume pop-up surface ([Surface("osd")], Mantle.App/Surfaces/Osd) with its feature ([Feature("osd")]): made at
/// start and parked; shown through the test road (a second start's --test-surface=show:osd, which calls osd.testShow with
/// MANTLE_TEST_OSD's made-up reading, so nothing is read from or written to Windows); put on top of a TOPMOST window of
/// another process that holds the foreground; the glyph and chevron clicked; the theme flipped; and a volume key
/// (--test-chord, a DRY step in --test: the runner's volume is never changed) that fades by itself. Every run is the
/// test's own profile; no real volume key is pressed.
/// </summary>
[TestClass]
public class OsdSurfaceTests
{
    const string Title = "Mantle volume";

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
        psi.Environment["MANTLE_TEST_OSD"] = "{\"level\":62}";
        return psi;
    }

    static App Launch(string profile)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        var psi = Start(profile, ud);
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud, Profile = profile };
        var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle) is { Count: > 0 } w ? w[0] : null, 30000);
        if (h is not IntPtr) Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its bar appeared" : "no bar window in 30 s");
        return app;
    }

    static void Second(App app, params string[] extra)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, extra))!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + string.Join(' ', extra) + ") did not exit in 15 s");
        Assert.AreEqual(0, p.ExitCode, "the second start (" + string.Join(' ', extra) + "): " + o.Result + e.Result);
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(40));

    /// The OSD window: made at start, so it exists (hidden) before any show.
    static IntPtr Osd(App app)
    {
        var all = new List<IntPtr>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); if (pid == app.Proc.Id && Win32.Title(h) == Title) all.Add(h); return true; }, IntPtr.Zero);
        return all.Count > 0 ? all[0] : IntPtr.Zero;
    }

    /// A TOPMOST window of another process, activated so it holds the foreground (the case where a plain show can be
    /// refused the top: the pop-up must still end up above it).
    sealed class TopFixture : IDisposable
    {
        public Process Proc; public IntPtr Hwnd; public string Title;
        public TopFixture(string title)
        {
            Title = title;
            string ps = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
                        $"$f.Text = '{title}'; $f.TopMost = $true; $f.StartPosition = 'Manual'; " +
                        "$s = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea; $f.Location = New-Object System.Drawing.Point($s.X, $s.Y); " +
                        "$f.Size = New-Object System.Drawing.Size($s.Width, $s.Height); $f.BackColor = [System.Drawing.Color]::White; " +
                        "$f.Add_Shown({ $f.Activate() }); [System.Windows.Forms.Application]::Run($f)";
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)));
            Proc = Host.Track(Process.Start(psi)!);
            var h = Host.Until(() => Win32.WindowsOf(Proc.Id, title).Cast<IntPtr?>().FirstOrDefault() is IntPtr w ? (object)w : null, 20000);
            if (h == null) Assert.Fail($"the topmost fixture '{title}' did not appear in 20 s");
            Hwnd = (IntPtr)h!;
        }
        public void Dispose() => Host.Kill(Proc);
    }

    [TestMethod]
    public void Osd_is_parked_at_start_shown_on_top_of_a_topmost_foreground_app_clicked_themed_and_a_key_fades_it()
    {
        Win32.DpiAware();
        using var app = Launch("uitest-osd");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("surface osd: made at start, hidden and parked"), 15000), "the osd surface was not made at start:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Osd(app) != IntPtr.Zero, 5000), "no '" + Title + "' window after start");
        IntPtr osd = Osd(app);
        Assert.IsFalse(Win32.IsWindowVisible(osd), "the pop-up is on screen before anything showed it");
        Assert.IsTrue(Host.Until(() => System.Text.RegularExpressions.Regex.IsMatch(app.Log, @"features: started [^;\r\n]*\bosd\b"), 10000), "the osd feature did not start:\n" + Tail(app.Log));
        StringAssert.DoesNotMatch(app.Log, new System.Text.RegularExpressions.Regex("features: keys .*taking VolumeUp"), "a --test run took the volume keys from Windows");

        using var top = new TopFixture("OsdFixture " + Guid.NewGuid().ToString("N")[..4]);
        Thread.Sleep(800);
        bool fixtureInFront = Win32.GetForegroundWindow() == top.Hwnd;
        Console.WriteLine($"the topmost fixture holds the foreground: {fixtureInFront}");

        // the test road: surface.show -> osd.testShow {level 62} -> osd.show event -> the surface shows, raises, reports
        Second(app, "--test-surface=show:osd");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("osd: shown (testShow, volume 62, seq 1)"), 8000), "the pop-up was not shown:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(osd), 3000), "the pop-up window is not visible after the show");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("osd: raised: "), 3000), "no raise verdict:\n" + Tail(app.Log));
        StringAssert.Contains(app.Log, "osd: raised: on top of everything", "the pop-up was not put on top:\n" + Tail(app.Log));
        Assert.IsTrue((GetWindowLongPtrW(osd, -20).ToInt64() & 0x8) != 0, "the pop-up is not WS_EX_TOPMOST");
        Assert.IsTrue(IsAbove(osd, top.Hwnd), "the topmost fixture of another process is above the pop-up");
        Assert.IsTrue((GetWindowLongPtrW(osd, -20).ToInt64() & 0x08000000) != 0, "the pop-up can take the keyboard (no WS_EX_NOACTIVATE)");
        Assert.AreNotEqual(osd, Win32.GetForegroundWindow(), "the pop-up took the foreground");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("osd: page drew 324x82"), 4000), "the pill did not draw at 288 x 46 DIP + the 18 DIP margin:\n" + Tail(app.Log));

        var win = Host.Uia.FromHandle(osd);
        var num = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("num")), 4000);
        Assert.IsNotNull(num, "no number on the pill");
        Assert.AreEqual("62", num!.Name);
        Host.Screenshot("osd-light-62");

        double s = Win32.GetDpiForWindow(osd) / 96.0;
        GetWindowRect(osd, out var r);
        int pillY = (int)num.BoundingRectangle.Top + (int)(num.BoundingRectangle.Height / 2);
        var face = new Rectangle(r.Left + (int)(56 * s), pillY - (int)(19 * s), Math.Max(2, (int)(5 * s)), Math.Max(2, (int)(5 * s)));
        double light = Host.Luminance(face);
        Assert.IsTrue(light > 200, $"the light face reads {light:N0} (want > 200)");

        // the glyph: a click mutes through qs.set; this build has no qs.set, so the refusal is a line of why (never silent)
        Mouse.Click(new Point(r.Left + (int)(41 * s), pillY));
        var why = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("why")) is { } w && (w.Name ?? "").Length > 0 ? w : null, 4000);
        Assert.IsNotNull(why, "the glyph click left no line of why:\n" + Tail(app.Log));
        StringAssert.StartsWith(why!.Name, "Mute did not take: ");
        Assert.AreEqual("62", num.Name, "a refused mute changed the number");

        // the chevron: the panel opens over the pill, wider, with the apps (here the "can't be read" block)
        GetWindowRect(osd, out r);
        pillY = (int)num.BoundingRectangle.Top + (int)(num.BoundingRectangle.Height / 2);
        Mouse.Click(new Point(r.Right - (int)(41 * s), pillY));
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByName("Sound")) != null, 4000), "the chevron did not open the panel:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("(panel open)") && app.Log.Contains("osd: page drew 384x"), 4000), "the open panel did not report its size:\n" + Tail(app.Log));
        Assert.IsNotNull(Host.Until(() => win.FindFirstDescendant(cf => cf.ByName("The apps can’t be read")), 4000), "the panel shows no apps and no state block");
        Host.Screenshot("osd-light-panel");

        // dark: the face follows the host's theme event
        Second(app, "--test-theme=dark");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-theme dark: ok"), 8000), "theme.set dark was not answered:\n" + Tail(app.Log));
        Thread.Sleep(600);
        GetWindowRect(osd, out r);
        pillY = (int)num.BoundingRectangle.Top + (int)(num.BoundingRectangle.Height / 2);
        face = new Rectangle(r.Left + (int)(56 * s), pillY - (int)(19 * s), face.Width, face.Height);
        double dark = Host.Luminance(face);
        Host.Screenshot("osd-dark-panel");
        Assert.IsTrue(dark < 110, $"the dark face reads {dark:N0} (want < 110; light read {light:N0})");

        // surface.hide: gone, and the feature hears it faded
        Second(app, "--test-surface=hide:osd");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(osd), 3000), "surface.hide left the pop-up on screen");
        StringAssert.Contains(app.Log, "osd: hidden (");

        // a volume key in --test: a DRY step (or the runner's "no playback device"), shown, then it fades by itself
        Second(app, "--test-chord=VolumeUp");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("osd: shown (VolumeUp, volume"), 6000), "the volume key showed no pop-up:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("osd: faded and hidden (seq 2)"), 6000), "the key's pop-up did not fade by itself (1.5 s hold + the fade):\n" + Tail(app.Log));
        Assert.IsFalse(Win32.IsWindowVisible(osd), "the faded pop-up's window is still on screen");
        StringAssert.DoesNotMatch(app.Log, new System.Text.RegularExpressions.Regex("osd.volume.*\"dry\":false"), "a --test run stepped the real volume");
    }

    /// a is above b in the z-order (walk up from b: GW_HWNDPREV).
    static bool IsAbove(IntPtr a, IntPtr b)
    {
        for (IntPtr h = GetWindow(b, 3); h != IntPtr.Zero; h = GetWindow(h, 3)) if (h == a) return true;
        return false;
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);
}
