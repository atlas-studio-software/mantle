using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Launcher;

/// <summary>
/// The launcher surface (Mantle.App/Surfaces/Launcher) in the real app: made at start, hidden and parked; opened with
/// the host's surface.show (a --test second start's --test-surface=show:launcher; Alt+Space is not registered in a
/// test run); typing, the rows, the arrow keys, Enter (a --test launch is a dry run) and Esc. The index reads ONLY a
/// scratch Start Menu this test writes (MANTLE_TEST_LAUNCHER_DIR), never the machine's.
/// </summary>
[TestClass]
public class LauncherTests
{
    const string Title = "Mantle launcher";
    const int WS_EX_NOACTIVATE = 0x08000000;

    sealed class App : IDisposable
    {
        public Process Proc = null!;
        public string Ud = "", Profile = "", Fixture = "";
        public bool Keys;
        public string Log { get { try { using var fs = new FileStream(Path.Combine(Ud, "mantle-native.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return new StreamReader(fs, Encoding.UTF8).ReadToEnd(); } catch (IOException) { return ""; } } }
        public void Dispose() { Host.Kill(Proc); try { Directory.Delete(Fixture, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// A bare MS-SHLLINK file (the 76-byte header, no target): enough for the Start Menu walk, as make-golden.cjs writes.
    static byte[] Lnk()
    {
        var b = new byte[0x4C];
        BitConverter.GetBytes(0x4C).CopyTo(b, 0);
        new byte[] { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 }.CopyTo(b, 4);
        BitConverter.GetBytes(0x80).CopyTo(b, 0x14);    // IsUnicode
        BitConverter.GetBytes(1).CopyTo(b, 0x3C);       // ShowCommand: normal
        return b;
    }

    /// The scratch fixture, in IndexInputs.ForTestRun's layout (pd = all users, home\AppData\Roaming = you).
    static string WriteFixture()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mantle-launcher-uitest-" + Guid.NewGuid().ToString("N")[..6]);
        string pd = Path.Combine(dir, "pd", "Microsoft", "Windows", "Start Menu", "Programs");
        string you = Path.Combine(dir, "home", "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
        foreach (var (root, rel) in new[] { (pd, "Zephyr Notes.lnk"), (pd, "Zephyr Paint.lnk"), (you, @"Zephyr Tools\Zephyr Mail.lnk"), (pd, "Quill Writer.lnk") })
        {
            string f = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, Lnk());
        }
        return dir;
    }

    static ProcessStartInfo Start(string profile, string ud, string fixture, bool keys, params string[] extra)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", $"--user-data={ud}", "--theme=light" }) psi.ArgumentList.Add(a);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        psi.Environment["MANTLE_TEST_LAUNCHER_DIR"] = fixture;
        if (keys) psi.Environment["MANTLE_TEST_LAUNCHER_KEYS"] = "1";
        return psi;
    }

    static App Launch(string profile, bool keys)
    {
        Host.Guard();
        if (!File.Exists(Host.ExePath)) Assert.Fail($"no app at {Host.ExePath}: publish it first (native/tools/ci.ps1 -Stages build) or set MANTLE_NATIVE_EXE");
        var ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(ud);
        string fx = WriteFixture();
        var psi = Start(profile, ud, fx, keys);
        psi.RedirectStandardOutput = false; psi.RedirectStandardError = false;
        var app = new App { Proc = Host.Track(Process.Start(psi)!), Ud = ud, Profile = profile, Fixture = fx, Keys = keys };
        var h = Host.Until(() => app.Proc.HasExited ? (object)"exited" : Win32.WindowsOf(app.Proc.Id, BarApp.WindowTitle) is { Count: > 0 } w ? w[0] : null, 30000);
        if (h is not IntPtr) Assert.Fail(h is string ? $"the app exited (code {app.Proc.ExitCode}) before its bar appeared" : "no bar window in 30 s");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("surface launcher: made at start") || app.Log.Contains("surface launcher: could not be made"), 15000),
            "the host never made (or tried to make) the launcher:\n" + Tail(app.Log));
        Assert.IsFalse(app.Log.Contains("surface launcher: could not be made"), "the launcher could not be made at start:\n" + Tail(app.Log));
        return app;
    }

    static void Second(App app, params string[] extra)
    {
        using var p = Process.Start(Start(app.Profile, app.Ud, app.Fixture, app.Keys, extra))!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + string.Join(' ', extra) + ") did not exit in 15 s");
        Assert.AreEqual(0, p.ExitCode, "the second start (" + string.Join(' ', extra) + "): " + o.Result + e.Result);
    }

    static IntPtr LauncherHwnd(App app)
    {
        var all = AllWindows(app.Proc.Id, Title);
        Assert.AreEqual(1, all.Count, "the app has " + all.Count + " launcher window(s), not 1");
        return all[0];
    }

    static void Open(App app, IntPtr h)
    {
        int before = Count(app.Log, ": launcher: shown (");
        Second(app, "--test-surface=show:launcher");
        Assert.IsTrue(Host.Until(() => Count(app.Log, ": launcher: shown (") > before && Win32.IsWindowVisible(h), 8000), "surface.show did not put the launcher on screen:\n" + Tail(app.Log));
    }

    static List<AutomationElement> Rows(AutomationElement win)
    {
        var results = win.FindFirstDescendant(cf => cf.ByAutomationId("results"));
        return results == null ? [] : results.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).ToList();
    }

    static string Selected(AutomationElement win) =>
        Rows(win).FirstOrDefault(r => r.Properties.ItemStatus.ValueOrDefault == "selected")?.Name ?? "(none)";

    [TestMethod]
    public void Launcher_is_made_at_start_parked_and_a_test_show_never_takes_the_keyboard()
    {
        using var app = Launch("uitest-launcher-parked", keys: false);
        StringAssert.Contains(app.Log, "launcher: created, hidden and parked (test, visible: never activated)");
        StringAssert.Contains(app.Log, "launcher: a test run indexes only the fixture in ");   // the log writes the profile folder as %USERPROFILE%
        StringAssert.Contains(app.Log, Path.GetFileName(app.Fixture) + " (MANTLE_TEST_LAUNCHER_DIR)");
        var h = LauncherHwnd(app);
        Assert.IsFalse(Win32.IsWindowVisible(h), "the parked launcher is on screen before anyone opened it");
        Assert.AreNotEqual(0, GetWindowLongPtrW(h, -20).ToInt64() & WS_EX_NOACTIVATE, "a --test launcher must be WS_EX_NOACTIVATE (never takes the keyboard)");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: index ") && app.Log.Contains(" rows in "), 10000), "the index was not warmed at start:\n" + Tail(app.Log));

        IntPtr fg = Win32.GetForegroundWindow();
        Open(app, h);
        Assert.AreEqual(fg, Win32.GetForegroundWindow(), "opening a --test launcher changed the foreground window");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: first frame with rows "), 5000), "no first frame with rows after the open:\n" + Tail(app.Log));
        var win = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("q")), 5000), "no search line (q) in the launcher");
        Assert.IsNotNull(win.FindFirstDescendant(cf => cf.ByAutomationId("panel")), "no panel group in the launcher");
        var hint = win.FindFirstDescendant(cf => cf.ByAutomationId("hint"));
        Assert.IsNotNull(hint, "no key hint (hint) in the footer");
        StringAssert.Contains(hint!.Name, "Esc Close");

        // an open over an open launcher does nothing (LAU-009); hide parks it again
        Second(app, "--test-surface=show:launcher");
        Second(app, "--test-surface=hide:launcher");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden ("), 8000), "surface.hide did not park the launcher:\n" + Tail(app.Log));
        Assert.AreEqual(1, Count(app.Log, ": launcher: shown ("), "a show over a shown launcher showed it again");
        Assert.AreEqual(1, AllWindows(app.Proc.Id, Title).Count, "hiding made or lost a launcher window");
    }

    [TestMethod]
    public void Typing_shows_the_fixture_rows_and_the_arrows_Enter_and_Esc_work()
    {
        using var app = Launch("uitest-launcher-keys", keys: true);
        StringAssert.Contains(app.Log, "launcher: created, hidden and parked (test, visible; MANTLE_TEST_LAUNCHER_KEYS=1");
        var h = LauncherHwnd(app);
        Open(app, h);
        var win = Host.Uia.FromHandle(h);
        var q = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("q")), 5000);
        Assert.IsNotNull(q, "no search line (q)");
        Mouse.Click(Mid(q!));   // the runner may refuse SetForegroundWindow; a real click gives it the keyboard
        Thread.Sleep(300);

        Keyboard.Type("zephyr");
        // the rows for the WHOLE word: a prefix's rows ("ze") can already hold the 3 apps, and reading them while the last
        // letters are still being searched mixed two answers (run 42772697675: a "Search the web for “ze”" row read late)
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: typed \"zephyr\": 4 row(s) on screen "), 5000), "no keystroke-to-rows line for \"zephyr\":\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => Rows(win).Count(r => r.Name.StartsWith("Zephyr ")) == 3, 5000), "typing \"zephyr\" did not list the 3 fixture apps; rows: " +
            string.Join(", ", Rows(win).Select(r => r.Name)) + "\n" + Tail(app.Log));
        var names = Rows(win).Select(r => r.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "Zephyr Mail", "Zephyr Notes", "Zephyr Paint" }, names.Where(n => n.StartsWith("Zephyr ")).ToList(), "the app rows for \"zephyr\"");
        Assert.AreEqual(4, names.Count, "the rows for \"zephyr\": the 3 apps and the web row (as Electron): " + string.Join(", ", names));
        int last = names.Count - 1;
        Assert.AreEqual(names[0], Selected(win), "the first row is not the selected one after typing");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: typed \"zephyr\": 4 row(s) on screen "), 5000), "no keystroke-to-rows line:\n" + Tail(app.Log));

        Keyboard.Type(VirtualKeyShort.DOWN);
        Assert.IsTrue(Host.Until(() => Selected(win) == names[1], 3000), "Down did not move to row 2 (selected: " + Selected(win) + ")");
        Keyboard.Type(VirtualKeyShort.END);
        Assert.IsTrue(Host.Until(() => Selected(win) == names[last], 3000), "End did not move to the last row");
        Keyboard.Type(VirtualKeyShort.DOWN);
        Assert.IsTrue(Host.Until(() => Selected(win) == names[0], 3000), "Down on the last row did not wrap to the first (LAU-048)");
        Keyboard.Type(VirtualKeyShort.UP);
        Assert.IsTrue(Host.Until(() => Selected(win) == names[last], 3000), "Up on the first row did not wrap to the last");
        Keyboard.Type(VirtualKeyShort.HOME);
        Assert.IsTrue(Host.Until(() => Selected(win) == names[0], 3000), "Home did not move to the first row");
        StringAssert.Contains(app.Log, "launcher: key Down: row 2 of 4");

        // Enter: a --test launch is a dry run that says what it would open, then the launcher hides
        Keyboard.Type(VirtualKeyShort.RETURN);
        Assert.IsTrue(Host.Until(() => app.Log.Contains($"launcher: Enter on \"{names[0]}\": ok (dry run"), 5000), "Enter did not launch the selected row (dry run):\n" + Tail(app.Log));
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (launched)"), 5000), "the launcher did not hide after Enter");

        // Esc closes it
        Open(app, h);
        Mouse.Click(Mid(q));
        Thread.Sleep(300);
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (Esc)"), 5000), "Esc did not close the launcher:\n" + Tail(app.Log));
    }

    /// The intermittent "Down did not move to row 2": a second answer for the same text landed after the arrow and put the
    /// selection back on row 1. Typed fast and then Down at once, five times over: Down must act on the rows for what was
    /// typed, and stay there (no late answer may move it).
    [TestMethod]
    public void A_key_right_after_typing_acts_on_the_rows_for_what_was_typed()
    {
        using var app = Launch("uitest-launcher-race", keys: true);
        var h = LauncherHwnd(app);
        Open(app, h);
        var win = Host.Uia.FromHandle(h);
        var q = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("q")), 5000);
        Assert.IsNotNull(q, "no search line (q)");
        Mouse.Click(Mid(q!));
        Thread.Sleep(300);
        for (int round = 1; round <= 5; round++)
        {
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type("zephyr");
            int said = Count(app.Log, "launcher: typed \"zephyr\": 4 row(s)");
            Keyboard.Type(VirtualKeyShort.DOWN);                               // no wait between the last letter and the arrow
            Assert.IsTrue(Host.Until(() => Count(app.Log, "launcher: typed \"zephyr\": 4 row(s)") > said, 5000), $"round {round}: no rows for the whole word\n" + Tail(app.Log));
            Assert.IsTrue(Host.Until(() => Rows(win).Count == 4, 5000), $"round {round}: \"zephyr\" did not list its 4 rows: " + string.Join(", ", Rows(win).Select(r => r.Name)));
            var names = Rows(win).Select(r => r.Name).ToList();
            Assert.IsTrue(Host.Until(() => Selected(win) == names[1], 3000), $"round {round}: Down did not move to row 2 (selected: {Selected(win)})\n" + Tail(app.Log));
            Thread.Sleep(400);                                                  // a late answer would have landed by now
            Assert.AreEqual(names[1], Selected(win), $"round {round}: the selection moved back after Down\n" + Tail(app.Log));
        }
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (Esc)"), 5000), "Esc did not close the launcher:\n" + Tail(app.Log));
    }

    /// Icons and glyphs (launcher.js iconCell): the web row draws its search glyph, never a letter tile; and the log says
    /// how many app rows got a picture from the sidecar (the fixture's bare shortcuts get Windows' generic file icon).
    [TestMethod]
    public void The_web_row_draws_a_glyph_and_the_app_icons_are_asked()
    {
        using var app = Launch("uitest-launcher-icons", keys: true);
        var h = LauncherHwnd(app);
        Open(app, h);
        var win = Host.Uia.FromHandle(h);
        var q = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("q")), 5000);
        Assert.IsNotNull(q, "no search line (q)");
        Mouse.Click(Mid(q!));
        Thread.Sleep(300);
        Keyboard.Type("zephyr");
        Assert.IsTrue(Host.Until(() => Rows(win).Count == 4, 5000), "\"zephyr\" did not list its 4 rows");
        var web = Rows(win).Last();
        StringAssert.StartsWith(web.Name, "Search the web for");
        // the row's icon cell: a glyph is a Path (no Text element showing the letter "S")
        var letters = web.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(t => t.Name).ToList();
        CollectionAssert.DoesNotContain(letters, "S", "the web row shows a letter tile instead of its search glyph: " + string.Join(" | ", letters));
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: icons for "), 15000), "the index never said how its icons went:\n" + Tail(app.Log));
    }

    static System.Drawing.Point Mid(AutomationElement e) { var r = e.BoundingRectangle; return new(r.X + r.Width / 2, r.Y + r.Height / 2); }
    static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
    static string Tail(string log) => string.Join("\n", log.Split('\n').TakeLast(30));

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);

    static List<IntPtr> AllWindows(int pid, string? title)
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
