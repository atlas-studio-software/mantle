using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;

namespace Mantle.UiTests.Theme;

/// <summary>
/// The bar's theme follows the host (native-core-3's look wiring): another app's theme written to look.json
/// (LOOK-044) and Windows' own app mode under Match Windows (LOOK-016, the 1 s of LOOK-025), measured on the
/// real composited pixels of the pill. Windows' mode comes from a SCRATCH key under HKCU handed to the app as
/// MANTLE_TEST_THEME_KEY (a --test run only), never the user's own key; every file is the test's own profile.
/// </summary>
[TestClass]
public class ThemeTests
{
    public TestContext TestContext { get; set; } = null!;

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "";
        public AutomationElement Window = null!;
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); }
    }

    /// --test in a fresh profile of its own; <paramref name="theme"/> null = no --theme pin (theme.json decides).
    static App Launch(string profile, string? theme, Action<string>? seed = null, IDictionary<string, string>? env = null)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        seed?.Invoke(ud);
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}" }) psi.ArgumentList.Add(a);
        if (theme != null) psi.ArgumentList.Add("--theme=" + theme);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud };
        var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle) is { Count: > 0 } w ? w[0] : null, 30000);
        if (h is not IntPtr hwnd) { Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its bar appeared" : "no bar window in 30 s"); return app; }
        app.Window = Host.Uia.FromHandle(hwnd);
        return app;
    }

    /// An empty stretch of glass (right of Start, left of the centred apps), as BarTests' theme test reads it.
    static Rectangle Probe(App app)
    {
        var pill = Host.Until(() => app.Window.FindFirstDescendant(cf => cf.ByAutomationId("bar.pill")), 8000) ?? throw new AssertFailedException("no bar.pill");
        var r = pill.BoundingRectangle;
        return new Rectangle(r.X + 64, r.Y + r.Height / 2 - 6, Math.Min(160, r.Width / 5), 12);
    }

    /// Poll the pill until it is clearly darker than <paramref name="light"/>: the ms it took, or -1 after <paramref name="ms"/>.
    static long DarkWithin(Rectangle probe, double light, Stopwatch since, int ms, out double last)
    {
        last = light;
        while (since.ElapsedMilliseconds < ms) {
            last = Host.Luminance(probe);
            if (last + 30 < light) return since.ElapsedMilliseconds;
            Thread.Sleep(25);
        }
        return -1;
    }

    [TestMethod]
    public void Another_apps_theme_in_look_json_reaches_the_bar_within_1_s()
    {
        // LOOK-044: Helm writes theme dark into the shared look.json; Mantle follows it, and the bar shows it within 1 s
        using var app = Launch("uitest-look044", null, ud => {
            // light on both sides before the start, so the first read adopts light whatever the runner's own Windows mode is
            File.WriteAllText(Path.Combine(ud, "theme.json"), "{\n  \"mode\": \"light\"\n}\n");
            Directory.CreateDirectory(Path.Combine(ud, "look-sync"));
            File.WriteAllText(Path.Combine(ud, "look-sync", "look.json"), "{\n  \"schema\": 1,\n  \"rev\": 3,\n" +
                "  \"writer\": { \"app\": \"helm\", \"instance\": \"uitest-seed\", \"pid\": 1, \"at\": \"2026-01-02T03:04:05.000Z\" },\n  \"look\": {\n" +
                "    \"theme\": { \"v\": \"light\", \"rev\": 3, \"by\": \"helm\" },\n    \"accent\": { \"v\": { \"hue\": null }, \"rev\": 2, \"by\": \"helm\" },\n" +
                "    \"glass\": { \"v\": { \"dark\": { \"blurPx\": 5, \"dim\": 0.5, \"panel\": 0 }, \"light\": { \"blurPx\": 12, \"dim\": 0.45, \"panel\": 0 }, \"render\": \"picture\" }, \"rev\": 1, \"by\": \"helm\" }\n" +
                "  }\n}\n");
        });
        var probe = Probe(app);
        string file = Path.Combine(app.Ud, "look-sync", "look.json");
        Assert.IsTrue(Host.Until(() => File.Exists(file) && app.Log.Contains("look: sync with Helm and Quartz is on"), 8000), "look sync never seeded the test's own look.json:\n" + Tail(app.Log));
        Thread.Sleep(1500);   // first frame + backdrop, and the seed settled
        double light = Host.Luminance(probe);

        // the write Helm makes: one field changed, the document's rev and the field's rev one higher, Helm as the writer
        var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;
        double rev = doc["rev"]!.GetValue<double>() + 1;
        doc["rev"] = rev;
        doc["writer"] = new JsonObject { ["app"] = "helm", ["instance"] = "uitest", ["pid"] = 1, ["at"] = DateTime.UtcNow.ToString("o") };
        doc["look"]!["theme"] = new JsonObject { ["v"] = "dark", ["rev"] = rev, ["by"] = "helm" };
        string tmp = file + ".helm.1.abcdef.tmp";
        File.WriteAllText(tmp, doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        var sw = Stopwatch.StartNew();
        File.Move(tmp, file, overwrite: true);
        long took = DarkWithin(probe, light, sw, 3000, out double dark);
        Console.WriteLine($"LOOK-044: pill luminance light {light:0.0} -> {dark:0.0}; the bar showed Helm's dark {took} ms after the write");
        Assert.IsTrue(took >= 0, $"the bar never went dark within 3 s of Helm's write (luminance {light:0.0} -> {dark:0.0}):\n" + Tail(app.Log));
        Assert.IsTrue(took < 1000, $"the bar went dark {took} ms after Helm's write; the target is under 1 s");
        StringAssert.Contains(app.Log, "look: following Helm");
    }

    [TestMethod]
    public void Match_Windows_flips_the_bar_within_1_s_of_a_scratch_key_change()
    {
        // LOOK-016 / the 1 s of LOOK-025: Match Windows, and Windows' app mode moves light -> dark
        string sub = @"Software\Mantle-native-uitests\theme-" + Guid.NewGuid().ToString("N")[..8];
        using (var k = Registry.CurrentUser.CreateSubKey(sub)) k.SetValue("AppsUseLightTheme", 1, RegistryValueKind.DWord);
        try {
            using var app = Launch("uitest-matchwin", "system", null, new Dictionary<string, string> { ["MANTLE_TEST_THEME_KEY"] = sub });
            var probe = Probe(app);
            Assert.IsTrue(Host.Until(() => app.Log.Contains("theme: Mantle is light"), 8000), "the host never said its theme:\n" + Tail(app.Log));
            StringAssert.Contains(app.Log, "(MANTLE_TEST_THEME_KEY), never the user's own");
            Thread.Sleep(1200);
            double light = Host.Luminance(probe);
            Assert.IsFalse(app.Log.Contains("Windows apps are dark now"), "the log said dark before the key changed:\n" + Tail(app.Log));
            var sw = Stopwatch.StartNew();
            using (var k = Registry.CurrentUser.OpenSubKey(sub, true)!) k.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
            Assert.IsTrue(Host.Until(() => app.Log.Contains("theme: Windows apps are dark now, so Mantle is dark"), 3000, 20), "the host did not follow the scratch key:\n" + Tail(app.Log));
            long host = sw.ElapsedMilliseconds;
            Console.WriteLine($"Match Windows: the host followed the scratch key in {host} ms (log poll, 20 ms steps)");
            Assert.IsTrue(host < 1000, $"the host followed Windows {host} ms after the change; the target is under 1 s");
            if (app.Log.Contains("the bar still follows theme.json itself"))
                Assert.Inconclusive($"the host followed Windows in {host} ms, but the bar does not take the host's theme yet: BarWindow has to subscribe MantleApp.ThemeShown (windows lane); its own Resolve reads Windows' real key");
            long took = DarkWithin(probe, light, sw, 3000, out double dark);
            Console.WriteLine($"Match Windows: pill luminance light {light:0.0} -> {dark:0.0}; the bar showed dark {took} ms after the key changed");
            Assert.IsTrue(took >= 0 && took < 1000, $"the bar showed dark {took} ms after Windows changed (luminance {light:0.0} -> {dark:0.0}); the target is under 1 s");
        } finally {
            Registry.CurrentUser.DeleteSubKeyTree(sub, false);
        }
    }

    [TestMethod]
    public void Theme_set_switches_the_bar_within_1_s_of_the_call_and_answers_the_full_view()
    {
        // LOOK-011 / LOOK-025: theme.set {mode} (the call a page makes, here from a --test second start) switches every
        // Mantle window at once (the bar is the only one in this build) and answers the full view plus value
        const string profile = "uitest-themeset";
        using var app = Launch(profile, null, ud => File.WriteAllText(Path.Combine(ud, "theme.json"), "{\n  \"mode\": \"light\"\n}\n"));
        var probe = Probe(app);
        Assert.IsTrue(Host.Until(() => app.Log.Contains("theme: Mantle is light"), 8000), "the host never said its theme:\n" + Tail(app.Log));
        Thread.Sleep(1500);   // first frame + backdrop
        double light = Host.Luminance(probe);
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={app.Ud}", "--test-theme=dark" }) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        var sw = Stopwatch.StartNew();
        using (var second = Process.Start(psi)!) {
            long handed = -1, dark = -1;
            double last = light;
            while (sw.ElapsedMilliseconds < 8000 && (handed < 0 || dark < 0)) {
                if (handed < 0 && app.Log.Contains("a second launch handed off: --test --profile-name=" + profile)) handed = sw.ElapsedMilliseconds;
                if (dark < 0 && (last = Host.Luminance(probe)) + 30 < light) dark = sw.ElapsedMilliseconds;
                Thread.Sleep(20);
            }
            second.WaitForExit(10000);
            Console.WriteLine($"theme.set: second start handed off at {handed} ms, the pill went dark at {dark} ms (luminance {light:0.0} -> {last:0.0}); from the call {(dark >= 0 && handed >= 0 ? Math.Max(0, dark - handed) : -1)} ms");
            Assert.IsTrue(handed >= 0, "the running Mantle never logged the hand-off:\n" + Tail(app.Log));
            Assert.IsTrue(dark >= 0, $"the bar never went dark within 8 s of theme.set (luminance {light:0.0} -> {last:0.0}):\n" + Tail(app.Log));
            Assert.IsTrue(dark - handed < 1000, $"the bar went dark {dark - handed} ms after the call reached Mantle; the target is under 1 s");
        }
        Assert.IsTrue(Host.Until(() => app.Log.Contains("mantle: test-theme dark: ok: "), 3000), "theme.set did not answer ok:\n" + Tail(app.Log));
        string answer = app.Log.Split('\n').Last(l => l.Contains("mantle: test-theme dark: ok: "));
        foreach (var part in new[] { "\"ok\":true", "\"mode\":\"dark\"", "\"theme\":\"dark\"", "\"value\":\"dark\"" })
            StringAssert.Contains(answer, part, "theme.set's answer is not the full view plus value");
        // the host's theme reached the bar (ThemeShown); theme.json's own path may have got there first ("already shows")
        Assert.IsTrue(Host.Until(() => app.Log.Contains("look: the host's theme is dark (Dark); the bar "), 3000), "the host's theme never reached the bar:\n" + Tail(app.Log));
    }

    [TestMethod]
    public void Resume_and_unlock_re_read_the_shared_look()
    {
        // main.js powerMonitor 'resume' / 'unlock-screen' -> lookHost.refresh: the session window hears both
        using var app = Launch("uitest-wake", "light");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("look: sync with Helm and Quartz is on"), 8000), "look sync never started:\n" + Tail(app.Log));
        IntPtr session = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => (session = SessionWindowOf(app.Proc.Id)) != IntPtr.Zero, 5000), "no MantleNativeSession window in the app");
        Assert.IsTrue(Win32.PostMessageW(session, 0x02B1 /* WM_WTSSESSION_CHANGE */, (IntPtr)0x8 /* WTS_SESSION_UNLOCK */, IntPtr.Zero));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("look: re-reading the shared look (the screen was unlocked)"), 3000), "unlock did not re-read the look:\n" + Tail(app.Log));
        Assert.IsTrue(Win32.PostMessageW(session, 0x0218 /* WM_POWERBROADCAST */, (IntPtr)0x12 /* PBT_APMRESUMEAUTOMATIC */, IntPtr.Zero));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("look: re-reading the shared look (the PC woke from sleep)"), 3000), "resume did not re-read the look:\n" + Tail(app.Log));
    }

    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(25));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    static IntPtr SessionWindowOf(int pid)
    {
        for (IntPtr h = FindWindowExW(IntPtr.Zero, IntPtr.Zero, "MantleNativeSession", null); h != IntPtr.Zero; h = FindWindowExW(IntPtr.Zero, h, "MantleNativeSession", null)) {
            GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid) return h;
        }
        return IntPtr.Zero;
    }
}
