using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Toasts;

/// <summary>
/// Mantle's notification banners ([Surface("toasts")] + [Feature("toasts")]) in a --test run, driven ONLY through the
/// test-only road: each second start's --test-surface=show:toasts runs the next step of MANTLE_TEST_TOASTS (toasts.testShow:
/// banners with no Windows toast behind them; toasts.testOwn: Mantle's own banner through the real OwnBanners;
/// toasts.testIds: the list "changed"). Nothing is posted to, read from or removed from Windows' notification centre, and a
/// --test run never takes Windows' banners (asserted from the log). Proven here: no window before the first banner; the
/// window raised above a topmost foreground app of another process without taking the foreground (WS_EX_NOACTIVATE); a
/// card leaves by itself after its 6 s; at most 3 on screen with "+N more"; Mantle's own card first, its buttons answered,
/// updated in place, its X going to its poster; a card whose toast left the list leaves.
/// </summary>
[TestClass]
public class ToastsSurfaceTests
{
    const string Title = "Mantle notifications";

    const string Steps = "[" +
        "{\"cmd\":\"testShow\",\"n\":1,\"title\":\"First banner\",\"body\":\"It leaves by itself after six seconds.\"}," +
        "{\"cmd\":\"testShow\",\"n\":4,\"sticky\":true}," +
        "{\"cmd\":\"testOwn\",\"key\":\"upd\",\"dnd\":0,\"quns\":5,\"title\":\"Mantle v0.2.0 is out\",\"body\":\"Update downloads it and restarts Mantle.\",\"detail\":\"C:\\\\mantle\\\\update.log\"," +
        "\"actions\":[{\"id\":\"later\",\"label\":\"Later\"},{\"id\":\"go\",\"label\":\"Update\",\"primary\":true}]}," +
        "{\"cmd\":\"testOwn\",\"key\":\"upd\",\"title\":\"Updating Mantle\",\"progress\":\"Downloading v0.2.0\",\"pct\":30,\"actions\":[{\"id\":\"later\",\"label\":\"Later\"}]}," +
        "{\"cmd\":\"testIds\",\"ids\":[]}" +
        "]";

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
        psi.Environment["MANTLE_TEST_TOASTS"] = Steps;
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

    static void Step(App app)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, "--test-surface=show:toasts"))!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start did not exit in 15 s");
        Assert.AreEqual(0, p.ExitCode, "the second start: " + o.Result + e.Result);
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(40));

    static IntPtr Win(App app)
    {
        var all = new List<IntPtr>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); if (pid == app.Proc.Id && Win32.Title(h) == Title) all.Add(h); return true; }, IntPtr.Zero);
        return all.Count > 0 ? all[0] : IntPtr.Zero;
    }

    sealed class TopFixture : IDisposable
    {
        public Process Proc; public IntPtr Hwnd;
        public TopFixture(string title)
        {
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
    public void Toasts_arrive_on_top_without_focus_time_out_stack_with_more_and_Mantles_own_card_answers_its_buttons()
    {
        Win32.DpiAware();
        using var app = Launch("uitest-toasts");
        try { Run(app); }
        finally
        {
            var dir = Path.Combine(Host.OutDir, "..", "parity", "toasts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "uitest-toasts-native-log.txt"), app.Log);
        }
    }

    static void Run(App app)
    {
        Assert.IsTrue(Host.Until(() => app.Log.Contains("toasts: listening; the window is made on the first banner"), 15000), "the toasts surface did not start:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => System.Text.RegularExpressions.Regex.IsMatch(app.Log, @"features: started [^;\r\n]*\btoasts\b"), 10000), "the toasts feature did not start:\n" + Tail(app.Log));
        // THE TAKEOVER GATE: a --test run makes no banner owner, so nothing of Windows' is taken
        Host.LogContains(() => app.Log, "toasts: not a --live run, so Windows' own banners are never taken here");
        StringAssert.DoesNotMatch(app.Log, new System.Text.RegularExpressions.Regex("Windows' own pop-up(s)? (is|are) (off|taken)|took \\d+ app"), "a --test run took Windows' banners");
        Assert.AreEqual(IntPtr.Zero, Win(app), "a banner window exists before any banner (it is made on demand)");
        Mouse.Position = new System.Drawing.Point(5, 5);   // never over the stack (a hover holds every timer)

        using var top = new TopFixture("ToastFixture " + Guid.NewGuid().ToString("N")[..4]);
        Thread.Sleep(800);

        // 1: one banner arrives, on top of the topmost foreground app, never taking the foreground
        Step(app);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card 990001 arrived"), 8000), "the first banner did not arrive:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Win(app) is var w && w != IntPtr.Zero && Win32.IsWindowVisible(w), 4000), "no visible banner window:\n" + Tail(app.Log));
        IntPtr win = Win(app);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("toasts: raised: "), 3000), "no raise verdict:\n" + Tail(app.Log));
        Host.LogContains(() => app.Log, "toasts: raised: on top of everything");
        Assert.IsTrue(IsAbove(win, top.Hwnd), "the topmost fixture of another process is above the banners");
        Assert.AreNotEqual(win, Win32.GetForegroundWindow(), "the banner window took the foreground");
        Assert.AreNotEqual(0L, GetWindowLongPtrW(win, -20).ToInt64() & 0x08000000L, "the banner window lacks WS_EX_NOACTIVATE");
        Assert.IsTrue(GetWindowRect(win, out var r));
        double scale = Win32.GetDpiForWindow(win) / 96.0;
        Assert.AreEqual(Math.Ceiling(392 * scale), r.Right - r.Left, 1, "the window is not 392 DIP wide (a 360 card + 16 each side)");
        var uia = Host.Uia.FromHandle(win);
        Assert.IsNotNull(Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("title-990001")), 4000), "the card has no title");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card 990001 left (its time was up)"), 9000), "the banner did not leave by itself after its 6 s:\n" + Tail(app.Log));

        // 2: four more (sticky, so the steps below never race their 6 s): three on screen, "+1 more" on top
        Step(app);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card 990005 arrived"), 8000), "the four banners did not arrive:\n" + Tail(app.Log));
        win = Host.Until(() => Win(app) is var w && w != IntPtr.Zero ? (object)w : null, 4000) is IntPtr w2 ? w2 : IntPtr.Zero;
        uia = Host.Uia.FromHandle(win);
        var more = Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("more")), 4000);
        Assert.IsNotNull(more, "no \"+N more\" with four banners:\n" + Tail(app.Log));
        StringAssert.StartsWith(more!.Name, "1 more");
        // the cards are found by their titles (a card's own Grid has no automation peer, so UIA never lists it)
        Assert.IsNotNull(Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("title-990005")), 4000), "the newest of four is not on screen");
        Assert.IsNull(uia.FindFirstDescendant(cf => cf.ByAutomationId("title-990002")), "the oldest of four is on screen (at most three)");

        // 3: Mantle's own card: on screen before the apps' (never under "+N more"), its buttons answered
        Step(app);
        var go = Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("act--1-go")), 6000);
        Assert.IsNotNull(go, "Mantle's own card has no Update button:\n" + Tail(app.Log));
        StringAssert.StartsWith(Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("more")) is { } m && m.Name.StartsWith("2") ? m : null, 4000)?.Name ?? "", "2 more",
            "Mantle's own card did not push an app's card under \"+N more\"");
        Assert.IsNotNull(uia.FindFirstDescendant(cf => cf.ByAutomationId("detail--1")), "the detail is not drawn");
        uia.FindFirstDescendant(cf => cf.ByAutomationId("act--1-later"))!.AsButton().Invoke();
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card -1: \"later\" answered ok=true keep=true"), 4000), "the Later button was not answered:\n" + Tail(app.Log));
        uia.FindFirstDescendant(cf => cf.ByAutomationId("copy--1"))!.AsButton().Invoke();
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card -1: Not copied: a test run never writes to the clipboard"), 4000), "Copy did not say why it did not copy:\n" + Tail(app.Log));

        // 4: updated in place: same card, new words, a progress line
        Step(app);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card -1 updated in place"), 4000), "the own card was not updated in place:\n" + Tail(app.Log));
        Assert.IsNotNull(Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("progress--1")), 4000), "no progress line after the update");
        Assert.AreEqual("Updating Mantle", uia.FindFirstDescendant(cf => cf.ByAutomationId("title--1"))?.Name);

        // 5: the list "changed": every app card whose toast is gone leaves; Mantle's own stays
        Step(app);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card 990005 left (its notification is gone from the list)"), 4000), "the apps' cards did not leave with their toasts:\n" + Tail(app.Log));
        Assert.IsFalse(app.Log.Contains("card -1 left"), "Mantle's own card left with the apps'");

        // its X goes to its poster (keep:false) and the card leaves
        Host.Until(() => uia.FindFirstDescendant(cf => cf.ByAutomationId("x--1")), 4000)!.AsButton().Invoke();
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card -1: \"close\" answered ok=true keep=false"), 4000), "the X was not answered by its poster:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("card -1 left ("), 4000), "the own card did not leave after its X:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("toasts: page drew 0 card(s)"), 4000), "the empty page was not reported:\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(win), 4000), "the empty banner window is still on screen");
    }

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
