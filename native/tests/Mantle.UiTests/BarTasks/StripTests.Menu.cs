using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// The app menu (right-click; ui/bar/jumplist.js; lane native-r7-strip): made-up jump lists (MANTLE_TEST_JUMPLIST: never
/// the user's own lists, no sidecar read), a made-up Windows for keep on top (MANTLE_TEST_ONTOP_FAKE=1: no window is
/// changed), and every open, remove, close and launch a dry run the log names. Pin / Unpin writes this test profile's own
/// pin list. The pop-up is the bar's own window, "Mantle pop-up: &lt;app&gt; menu".
/// </summary>
public partial class StripTests
{
    const string MenuFixture = "{\"apps\":{" +
        "\"powershell.exe\":{\"sections\":[" +
        "{\"kind\":\"tasks\",\"name\":\"Tasks\",\"items\":[{\"title\":\"Run as admin\",\"item\":\"c:0:0\",\"tip\":\"C:\\\\fake\\\\task\"}]}," +
        "{\"kind\":\"recent\",\"name\":\"Recent\",\"items\":[{\"title\":\"notes.txt\",\"item\":\"a:0\",\"tip\":\"C:\\\\fake\\\\notes.txt\",\"canRemove\":true}]}]}," +
        "\"charmap.exe\":{\"sections\":[],\"none\":\"this app doesn't publish one\"}}}";

    static AutomationElement Menu(BarApp app, string name, string profile)
    {
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: " + name + " menu")), 4000),
            $"the {name} menu did not open; log: {Tail(LogSince(profile, 0))}");
        var w = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl")), 3000), "the menu has no bar.jl");
        return w;
    }

    static bool MenuGone(BarApp app, string name) =>
        Host.Until(() => !FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, "Mantle pop-up: " + name + " menu")), 3000);

    static AutomationElement Jl(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, "the menu has no " + id);
        return e!;
    }

    static void Invoke(AutomationElement e) { e.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(300); }

    static void RightClick(AutomationElement e)
    {
        var r = e.BoundingRectangle;
        Mouse.Click(new Point(r.X + r.Width / 2, r.Y + r.Height / 2), MouseButton.Right);
        Thread.Sleep(400);
    }

    [TestMethod]
    public void Right_click_opens_the_app_menu_with_its_jump_list_and_every_action_is_a_dry_run()
    {
        const string profile = "uitest-strip-menu";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("menu-" + tag, ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        string fx = Path.Combine(Path.GetTempPath(), "mantle-jl-fixture-" + tag + ".json");
        File.WriteAllText(fx, MenuFixture);
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> {
            ["MANTLE_TEST_PINS_DIR"] = pins, ["MANTLE_TEST_JUMPLIST"] = fx, ["MANTLE_TEST_ONTOP_FAKE"] = "1" });
        using var a = new Host.Fixture($"Strip menu {tag}", 60, 60);
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return Named(cs, a.Title) is { Open: true } && Named(cs, "Beta Map") != null; }, 20000, 250),
            $"in 20 s the strip did not show both pins with the fixture's window on Alpha Shell; cells: {Describe(cs)}");
        Func<string> log = () => LogSince(profile, 0);
        Host.LogContains(log, "jump list: a --test run with MANTLE_TEST_JUMPLIST", "the made-up jump lists were not taken", 5000);
        Host.LogContains(log, "bar tasks: keep on top: the pin state is known (0 window(s) kept on top)", "the strip never learned the keep-on-top state", 15000);

        // BAR-207, 208, 209, 315, 316: the jump list's sections over the app row, Unpin, Keep on top, Close window
        Click(Named(cs, a.Title)!, MouseButton.Right, app);
        var w = Menu(app, "Alpha Shell", profile);
        Assert.AreEqual("Alpha Shell menu", Jl(w, "bar.jl").Name);
        Host.LogContains(log, "bar tasks: jump list: get: tasks:1 recent:1 in", "the jump list was not read from the fixture", 5000);
        Assert.AreEqual("TASKS", Jl(w, "bar.jl.h.0").Name);
        Assert.AreEqual("RECENT", Jl(w, "bar.jl.h.1").Name);
        Assert.AreEqual("Run as admin", Jl(w, "bar.jl.item.0").Name);
        Assert.AreEqual(@"C:\fake\notes.txt", Jl(w, "bar.jl.item.1").HelpText, "an item's tooltip is not its path");
        Assert.AreEqual("Alpha Shell", Jl(w, "bar.jl.app").Name);
        Assert.AreEqual("Unpin from taskbar", Jl(w, "bar.jl.pin").Name);
        Assert.AreEqual("Keep on top", Jl(w, "bar.jl.ontop").Name);
        StringAssert.Contains(Jl(w, "bar.jl.ontop").HelpText, "Win+Ctrl+T");
        Assert.AreEqual("Close window", Jl(w, "bar.jl.close").Name);
        // BAR-220, 328: a test instance refuses the keyboard, and the menu's tooltip says why
        Assert.IsTrue(Host.Until(() => Jl(w, "bar.jl").HelpText.StartsWith("Arrow keys do not reach this menu: a test instance never takes the keyboard", StringComparison.Ordinal), 3000),
            "the menu's tooltip does not say why the arrow keys do not reach it: " + Jl(w, "bar.jl").HelpText);

        // BAR-211..213, 320, 321: an item's own actions; Remove is a dry run naming the token, never the title; success closes the menu
        RightClick(Jl(w, "bar.jl.item.1"));
        Assert.AreEqual("Open", Jl(w, "bar.jl.sub.open").Name);
        Assert.AreEqual("Pin to this list: Windows has no public way for another program to pin a jump list item", Jl(w, "bar.jl.sub.why").Name);
        Invoke(Jl(w, "bar.jl.sub.remove"));
        Host.LogContains(log, "dry run (--test): would remove from the list a:0 via the test's made-up jump list", "Remove was not a dry run", 5000);
        Assert.IsTrue(MenuGone(app, "Alpha Shell"), "the menu stayed open after a successful action");
        Assert.IsFalse(log().Contains("notes.txt", StringComparison.Ordinal), "the log named a jump list item");

        // a second right-click on the same icon closes it (BAR-207)
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        Menu(app, "Alpha Shell", profile);
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        Assert.IsTrue(MenuGone(app, "Alpha Shell"), "a second right-click did not close the menu");

        // BAR-219, 319, 327: an item opens as a dry run; BAR-218, 326: Close window is a dry run of WM_CLOSE
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        Invoke(Jl(Menu(app, "Alpha Shell", profile), "bar.jl.item.0"));
        Host.LogContains(log, "would open c:0:0 via the test's made-up jump list", "an item did not open as a dry run", 5000);
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        Invoke(Jl(Menu(app, "Alpha Shell", profile), "bar.jl.close"));
        Host.LogContains(log, "would send WM_CLOSE to 1 window(s): " + a.Hwnd.ToInt64(), "Close window was not a dry run of WM_CLOSE", 5000);
        Assert.IsFalse(a.Proc.HasExited, "a test run closed the window");

        // BAR-217, 258, 325, 333, 336, 343: keep on top through the made-up Windows: the badge and the tooltip line, then off again
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        Invoke(Jl(Menu(app, "Alpha Shell", profile), "bar.jl.ontop"));
        Host.LogContains(log, "would keep window " + a.Hwnd.ToInt64() + " on top; nothing in Windows changed", "keep on top did not reach the made-up Windows", 5000);
        Assert.IsTrue(Host.Until(() => Named(Cells(app), a.Title)?.Status.Contains("kept on top", StringComparison.Ordinal) == true, 5000),
            "the cell does not say it is kept on top: " + Describe(Cells(app)));
        // BAR-341: the result pop-up: its own window at the volume pop-up's place, 396 x 80 DIP, topmost, click-through, never
        // focused, saying how to undo the pin; hidden by 4.3 s whatever happens
        IntPtr tw = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(tw = FrameProbe.Find(app.Proc.Id, "Mantle keep on top")), 3000), $"the keep-on-top pop-up did not show; log: {Tail(log())}");
        var pane = Host.Until(() => Host.Uia.FromHandle(tw).FindFirstDescendant(cf => cf.ByAutomationId("ontop.toast")), 3000);
        Assert.IsNotNull(pane, "the pop-up has no ontop.toast pane");
        Assert.AreEqual("Win+Ctrl+T again to stop", pane!.HelpText, "a new pin does not say how to undo it");
        long ex = GetWindowLongPtrW(tw, -20).ToInt64();
        Assert.IsTrue((ex & 0x8) != 0 && (ex & 0x20) != 0 && (ex & 0x08000000) != 0, $"the pop-up is not topmost, click-through and unfocusable (ex style 0x{ex:x})");
        Assert.AreNotEqual(tw, Win32.GetForegroundWindow(), "the pop-up took the foreground");
        FrameProbe.GetWindowRect(tw, out var trc);
        double ts = GetDpiForWindow(tw) / 96.0;
        Assert.IsTrue(Math.Abs(trc.Right - trc.Left - 396 * ts) <= 2 && Math.Abs(trc.Bottom - trc.Top - 80 * ts) <= 2, $"the pop-up is {trc.Right - trc.Left}x{trc.Bottom - trc.Top} px, not 396x80 DIP at {ts:0.##}");
        var pill = app.Window.FindFirstDescendant(cf => cf.ByAutomationId("bar.pill")).BoundingRectangle;
        Assert.IsTrue(trc.Bottom <= pill.Top + 2 && trc.Bottom >= pill.Top - 40 * ts, $"the pop-up (bottom {trc.Bottom}) is not just above the bar (pill top {pill.Top})");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, "Mantle keep on top")), 4800), "the pop-up was still up after 4.3 s");
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        var w2 = Menu(app, "Alpha Shell", profile);
        Assert.AreEqual("Stop keeping on top", Jl(w2, "bar.jl.ontop").Name);
        Invoke(Jl(w2, "bar.jl.ontop"));
        Assert.IsTrue(Host.Until(() => Named(Cells(app), a.Title)?.Status.Contains("kept on top", StringComparison.Ordinal) == false, 5000),
            "the cell still says it is kept on top: " + Describe(Cells(app)));

        // BAR-206: a middle-click closes the open menu and asks for another copy
        Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
        Menu(app, "Alpha Shell", profile);
        Click(Named(Cells(app), a.Title)!, MouseButton.Middle, app);
        Assert.IsTrue(MenuGone(app, "Alpha Shell"), "a middle-click left the menu open");

        // BAR-210, 214, 317, 322: an app with no list says so in words; its app row starts it (a dry run)
        Click(Named(Cells(app), "Beta Map")!, MouseButton.Right, app);
        var wb = Menu(app, "Beta Map", profile);
        Assert.IsTrue(Host.Until(() => wb.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl.quiet.0"))?.Name == "No jump list: this app doesn't publish one", 4000),
            "the menu does not say the app has no jump list");
        Assert.IsNull(wb.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl.close")), "a menu for an app with no window offers Close");
        Assert.IsNull(wb.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl.ontop")), "a menu for an app with no window offers Keep on top");
        int charmaps = Process.GetProcessesByName("charmap").Length;
        Invoke(Jl(wb, "bar.jl.app"));
        Host.LogContains(log, "would another copy of open", "the app row did not start a new copy as a dry run", 5000);
        StringAssert.Contains(log(), "Beta Map.lnk");
        Assert.AreEqual(charmaps, Process.GetProcessesByName("charmap").Length, "a test run started the app");

        // BAR-216, 280, 324: Unpin writes this profile's own pin list (a Windows pin is hidden, never deleted) and the strip drops it
        Click(Named(Cells(app), "Beta Map")!, MouseButton.Right, app);
        Invoke(Jl(Menu(app, "Beta Map", profile), "bar.jl.pin"));
        Host.LogContains(log, "bar tasks: jump list: unpin: hid (in this test profile)", "Unpin did not write Mantle's own list", 5000);
        Assert.IsTrue(Host.Until(() => Named(Cells(app), "Beta Map") == null, 5000), "the unpinned app is still on the strip: " + Describe(Cells(app)));
        Assert.IsTrue(File.Exists(Path.Combine(pins, "Beta Map.lnk")), "Unpin touched the pinned shortcut itself");
        try { File.Delete(fx); } catch (IOException) { }
    }

    [TestMethod]
    public void Without_the_keep_on_top_channel_every_app_tooltip_and_the_menu_say_the_bar_cannot_show_it()
    {
        // BAR-334 (ui/bar/ontop.js CANT): MANTLE_TEST_ONTOP_CHANNEL=0 attaches the strip as if it had no event channel
        const string profile = "uitest-strip-ontop-cant";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("cant-" + tag, ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        string fx = Path.Combine(Path.GetTempPath(), "mantle-jl-fixture-cant-" + tag + ".json");
        File.WriteAllText(fx, MenuFixture);
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> {
            ["MANTLE_TEST_PINS_DIR"] = pins, ["MANTLE_TEST_JUMPLIST"] = fx, ["MANTLE_TEST_ONTOP_CHANNEL"] = "0" });
        using var a = new Host.Fixture($"Strip cant {tag}", 60, 60);
        Func<string> log = () => LogSince(profile, 0);
        Host.LogContains(log, "bar tasks: keep on top: the bar has no event channel (MANTLE_TEST_ONTOP_CHANNEL=0)", "the strip did not say it has no keep-on-top channel", 15000);
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return Named(cs, a.Title) is { Open: true } c && c.Status.Contains("keep on top unknown", StringComparison.Ordinal); }, 20000, 250),
            $"the running app's status does not say keep on top is unknown; cells: {Describe(cs)}");
        Assert.IsFalse(Named(cs, "Beta Map")!.Status.Contains("keep on top", StringComparison.Ordinal), $"a pin with no window says something about keep on top: {Describe(cs)}");
        Click(Named(cs, a.Title)!, MouseButton.Right, app);
        var w = Menu(app, "Alpha Shell", profile);
        Assert.AreEqual("Keep on top: the bar can't show which windows are kept on top right now (restart Mantle to fix it)", Jl(w, "bar.jl.ontop").HelpText);
        Assert.IsFalse(log().Contains("the pin state is known", StringComparison.Ordinal), "the pin state arrived with no channel");
    }

    [TestMethod]
    public void The_menu_draws_its_bottom_rows_while_it_reads_and_the_arrow_keys_move_over_every_row()
    {
        // BAR-208, 316: the bottom rows at once, "Reading the jump list…" until the (made-up, 2.5 s slow) list arrives;
        // BAR-220, 328: with the keyboard (MANTLE_TEST_JL_KEYS=1) Up/Down wrap, Home/End, Right/Left the item's actions, Esc
        const string profile = "uitest-strip-menu-keys";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("keys-" + tag, ("Alpha Shell", PowerShellExe));
        string fx = Path.Combine(Path.GetTempPath(), "mantle-jl-fixture-keys-" + tag + ".json");
        File.WriteAllText(fx, MenuFixture.Replace("\"powershell.exe\":{\"sections\"", "\"powershell.exe\":{\"delayMs\":2500,\"sections\"", StringComparison.Ordinal));
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> {
            ["MANTLE_TEST_PINS_DIR"] = pins, ["MANTLE_TEST_JUMPLIST"] = fx, ["MANTLE_TEST_ONTOP_FAKE"] = "1", ["MANTLE_TEST_JL_KEYS"] = "1" });
        using var a = new Host.Fixture($"Strip keys {tag}", 60, 60);
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return Named(cs, a.Title) is { Open: true }; }, 20000, 250), $"the pin never showed its window; cells: {Describe(cs)}");
        Func<string> log = () => LogSince(profile, 0);
        Host.LogContains(log, "bar tasks: keep on top: the pin state is known", "the strip never learned the keep-on-top state", 15000);

        Click(Named(cs, a.Title)!, MouseButton.Right, app);
        var w = Menu(app, "Alpha Shell", profile);
        Assert.AreEqual("Reading the jump list…", Jl(w, "bar.jl.quiet.0", 1000).Name, "no reading line while the list is read");
        foreach (var id in new[] { "bar.jl.app", "bar.jl.pin", "bar.jl.ontop", "bar.jl.close" }) Jl(w, id, 1000);
        Assert.IsNull(w.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl.item.0")), "the list was there before its read finished");
        Assert.IsFalse(log().Contains("bar tasks: menu: the jump list arrived", StringComparison.Ordinal), "the slow read finished at once");
        Jl(w, "bar.jl.item.1", 6000);
        Assert.IsNull(w.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl.quiet.0")), "the reading line stayed after the list arrived");

        Host.LogContains(log, "bar tasks: menu: the bar has the keyboard for the menu", "the menu never took the keyboard");
        string Focused() { try { return Host.Uia.FocusedElement()?.AutomationId ?? ""; } catch (Exception) { return ""; } }
        void Key(VirtualKeyShort k, string want, string what)
        {
            Keyboard.Type(k);
            Assert.IsTrue(Host.Until(() => Focused() == want, 2000), $"{what}: focus is on '{Focused()}', not {want}; log: {Tail(log())}");
        }
        Key(VirtualKeyShort.HOME, "bar.jl.item.0", "Home takes the first row");   // (from no row: JumpListTests)
        Key(VirtualKeyShort.UP, "bar.jl.close", "Up from the first wraps to the last");
        Key(VirtualKeyShort.DOWN, "bar.jl.item.0", "Down from the last wraps to the first");
        Key(VirtualKeyShort.END, "bar.jl.close", "End");
        Key(VirtualKeyShort.HOME, "bar.jl.item.0", "Home");
        Key(VirtualKeyShort.DOWN, "bar.jl.item.1", "Down");
        Keyboard.Type(VirtualKeyShort.RIGHT);
        Jl(w, "bar.jl.sub.open", 2000);
        Keyboard.Type(VirtualKeyShort.LEFT);
        Assert.IsTrue(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("bar.jl.sub.open")) == null, 2000), "Left did not close the item's actions");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.IsTrue(MenuGone(app, "Alpha Shell"), "Esc did not close the menu");
        Host.LogContains(log, "gave the keyboard back", "the keyboard was not given back on close");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
}
