using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Launcher;

/// <summary>
/// BAR-1037..1045, Quick links (Win+X) in the real app: Win+X (--test-chord, the hook's path) opens the launcher Mantle
/// already has in its Quick links mode (one window, no new one); the rows and underlined letters are Windows'; a letter
/// runs its row (a dry run that says what it would do); a double click sends one call; Shut down or sign out opens the
/// submenu whose ask-first rows arm on the first press; Esc goes back, then closes; Win+X again closes it. Every action
/// in --test is a dry run that is logged, not done.
/// </summary>
public partial class LauncherTests
{
    [TestMethod]
    public void Win_X_opens_Quick_links_letters_run_rows_as_dry_runs_and_the_submenu_asks_first()
    {
        using var app = Launch("uitest-launcher-quick", keys: true);
        var h = LauncherHwnd(app);
        var win = Host.Uia.FromHandle(h);

        // Win+X: the same launcher window, in Quick links (BAR-1037)
        Second(app, "--test-chord=Win+X");
        Assert.IsTrue(Host.Until(() => app.Log.Contains("launcher: shown (Win+X)") && Win32.IsWindowVisible(h), 8000), "Win+X did not show the launcher:\n" + Tail(app.Log));
        Host.LogContains(() => app.Log, "launcher: quick links: 18 rows in 3 groups (Tools, Open, Power and desktop)");
        Assert.AreEqual(1, AllWindows(app.Proc.Id, Title).Count, "Win+X made another launcher window");
        // BAR-1038: Windows' rows in its order, each with its access letter
        Assert.IsTrue(Host.Until(() => Rows(win).Count == 18, 4000), "Quick links did not list 18 rows: " + string.Join(", ", Rows(win).Select(r => r.Name)));
        var rows = Rows(win);
        CollectionAssert.AreEqual(new[] { "Installed apps", "Mobility Center", "Power Options", "Event Viewer", "System", "Device Manager", "Network Connections",
            "Disk Management", "Computer Management", "Terminal", "Terminal (Admin)", "Task Manager", "Settings", "File Explorer", "Search", "Run",
            "Shut down or sign out", "Desktop" }, rows.Select(r => r.Name).ToList(), "the Quick links rows");
        Assert.AreEqual("P", rows[0].Properties.AccessKey.ValueOrDefault, "Installed apps' access letter");
        Assert.AreEqual("M", rows[5].Properties.AccessKey.ValueOrDefault, "Device Manager's access letter");
        Assert.AreEqual("Installed apps", Selected(win), "the first row is not selected on open");

        var q = Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("q")), 5000);
        Assert.IsNotNull(q, "no query line (q)");
        Mouse.Click(Mid(q!));
        Thread.Sleep(300);

        // BAR-1039/1041/1045: a letter (any case) picks its row; a --test run's row is a dry run, said under the row, and it stays up
        Keyboard.Type("v");
        Host.LogContains(() => app.Log, "quicklinks: events: dry run (dry run: would run ShellExecute(\"eventvwr.msc\"))");
        Host.LogContains(() => app.Log, "launcher: quick links: Event Viewer: dry run");
        Assert.AreEqual("Event Viewer", Selected(win), "V did not select Event Viewer");
        Assert.IsTrue(Win32.IsWindowVisible(h), "a dry run closed the launcher");
        Assert.AreEqual(1, Count(app.Log, "quicklinks: events: "), "one V sent more than one call");
        // BAR-1042: Terminal (Admin) through UAC, said as a dry run
        Keyboard.Type("A");
        Host.LogContains(() => app.Log, "as administrator (Windows' UAC prompt)");
        // a double click on a row sends one call (once per decision)
        var dm = Rows(win).First(r => r.Name == "Device Manager");
        Mouse.DoubleClick(Mid(dm));
        Host.LogContains(() => app.Log, "quicklinks: devices: dry run");
        Thread.Sleep(700);
        Assert.AreEqual(1, Count(app.Log, "quicklinks: devices: "), "a double click on Device Manager sent more than one call:\n" + Tail(app.Log));

        // BAR-1044: Shut down or sign out opens the submenu; Shut down asks first (U arms, U again does it as a dry run)
        Mouse.Click(Mid(q));
        Thread.Sleep(200);
        Keyboard.Type("u");
        Host.LogContains(() => app.Log, "launcher: quick links: the Shut down or sign out submenu (5 rows)");
        Assert.IsTrue(Host.Until(() => Rows(win).Select(r => r.Name).SequenceEqual(new[] { "Sign out", "Sleep", "Hibernate", "Shut down", "Restart" }), 3000),
            "the submenu rows: " + string.Join(", ", Rows(win).Select(r => r.Name)));
        Keyboard.Type("u");
        Host.LogContains(() => app.Log, "launcher: quick links: Shut down is armed: Press U again to shut down");
        Assert.IsFalse(app.Log.Contains("quicklinks: power shutdown"), "the first U sent Shut down");
        Thread.Sleep(400);
        Keyboard.Type("u");
        Host.LogContains(() => app.Log, "quicklinks: power shutdown: dry run (dry run: would run ");
        // Esc goes back to the main list, then closes
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Host.LogContains(() => app.Log, "launcher: quick links: the main list (18 rows)");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (Esc)"), 5000), "Esc on the main list did not close it:\n" + Tail(app.Log));

        // Win+X again over Quick links closes it; over the plain launcher it switches to Quick links
        Second(app, "--test-chord=Win+X");
        Assert.IsTrue(Host.Until(() => Count(app.Log, "launcher: shown (Win+X)") == 2 && Win32.IsWindowVisible(h), 8000), "the second Win+X did not show it");
        Second(app, "--test-chord=Win+X");
        Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(h) && app.Log.Contains("launcher: hidden (Win+X again)"), 5000), "Win+X over Quick links did not close it:\n" + Tail(app.Log));
        Open(app, h);
        Assert.IsTrue(Host.Until(() => Rows(win).Any(r => r.Name.StartsWith("Zephyr ")), 5000), "the plain launcher after Quick links does not show the apps");
        Second(app, "--test-chord=Win+X");
        Host.LogContains(() => app.Log, "launcher: quick links: Win+X over the open launcher");
        Assert.IsTrue(Host.Until(() => Rows(win).Count == 18, 4000), "Win+X over the plain launcher did not switch to Quick links");
    }
}
