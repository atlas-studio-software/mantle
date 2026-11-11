using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
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
}
