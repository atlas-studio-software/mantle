using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// The ping test in the PING dropdown (TOP-139..159) on the made-up run (MANTLE_TEST_TOPBAR: PingTestFake, so nothing is
/// pinged or downloaded): Run shows the run as it goes and the dropdown stays open; the result marks exactly the bad
/// values amber; closing the dropdown does not cancel; the history keeps each run in the test profile's ping-tests.json,
/// filters by network, saves a name (by UI Automation and by the keyboard), deletes on a second press and compares two.
/// </summary>
public partial class TopBarSurfaceTests
{
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Aid(AutomationElement e) => e.Properties.AutomationId.ValueOrDefault ?? "";

    [TestMethod]
    public void Ping_test_runs_on_demand_shows_its_result_and_keeps_a_history()
    {
        using var app = Launch("topbar-pingtest");
        IntPtr bar = Up(app);
        Host.LogContains(() => app.Log, "focus: Notepad++, ", "the made-up app's focus never reached the strip", 6000);
        var win = Host.Uia.FromHandle(bar);
        Assert.IsFalse(app.Log.Contains("a made-up run started"), "a ping test ran before anyone asked for one");

        // idle: the tabs, Run ping test with its cost (TOP-139/140)
        Invoke(win, "topbar.stat.ping");
        Host.LogContains(() => app.Log, "topbar: stat pressed: ping (open; 7 rows)", "the PING dropdown did not open", 4000);
        Assert.AreEqual("Test", NameOf(win, "topbar.pt.tab.test"));
        Assert.AreEqual("History", NameOf(win, "topbar.pt.tab.history"));
        Assert.AreEqual("Run ping test", NameOf(win, "topbar.pt.pt-start"));
        Host.Screenshot("topbar-pingtest-idle");

        // Run: starting at once, the dropdown stays open, then a progress bar and Cancel, then the result (TOP-141/142)
        Invoke(win, "topbar.pt.pt-start");
        Host.LogContains(() => app.Log, "topbar: ping test: a made-up run started", "Run ping test did not start a run", 4000);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.pt.pt-cancel")) != null, 3000), "a running test has no Cancel row");
        Assert.IsNotNull(win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0")), "the dropdown closed when the test started");
        Host.LogContains(() => app.Log, "topbar: ping test: the made-up run ended (done)", "the made-up run never finished", 6000);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.pt.pt-start"))?.Name == "Run again", 3000), "a finished test does not offer Run again");
        StringAssert.StartsWith(NameOf(win, "topbar.pt.head.2"), "Ping test · ");

        // the result (TOP-146/147): the best server tagged, amber on exactly US East's avg and jitter and Illinois' loss
        Assert.AreEqual("US West (N. California) best", NameOf(win, "topbar.pt.c2.0"));
        Assert.AreEqual("102 ms", NameOf(win, "topbar.pt.c6.1"));
        Assert.AreEqual("amber", Status(Find(win, "topbar.pt.c6.1")));
        Assert.AreEqual("amber", Status(Find(win, "topbar.pt.c6.3")));
        Assert.AreEqual("amber", Status(Find(win, "topbar.pt.c5.4")));
        Assert.AreEqual("", Status(Find(win, "topbar.pt.c2.1")));
        Assert.AreEqual("", Status(Find(win, "topbar.pt.c1.1")));
        Assert.AreEqual("Download 62.0 Mbps", NameOf(win, "topbar.pt.speed.download"));
        Host.Screenshot("topbar-pingtest-done");

        // closing does not cancel (TOP-143): a second run goes on with the dropdown closed and is there on reopen
        Invoke(win, "topbar.pt.pt-start");
        Host.LogContains(() => app.Log, "topbar: ping test: topbar.pingTest start (answered)", "the second start was not asked for", 4000);
        Invoke(win, "topbar.stat.ping");
        Host.LogContains(() => app.Log, "topbar: stat pressed: ping (closed; refocus)", "a second press on PING did not close it", 4000);
        Assert.IsTrue(Host.Until(() => Count(app.Log, "the made-up run ended (done)") == 2, 6000), "the run stopped when its dropdown closed");
        Assert.IsFalse(app.Log.Contains("topbar.pingTest cancel"), "closing the dropdown cancelled the run");
        Invoke(win, "topbar.stat.ping");
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.pt.pt-start"))?.Name == "Run again", 3000), "the finished run is not there on reopen");
        Assert.IsTrue(Host.Until(() => win.FindAllDescendants().Any(e => (e.Name ?? "").StartsWith("The lag starts at your Wi-Fi", StringComparison.Ordinal)), 3000),
            "the second run (a slow router) does not say the lag starts at home");

        // the history (TOP-155/156): two runs on two networks, kept in the test profile's ping-tests.json
        Invoke(win, "topbar.pt.tab.history");
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.pt.tab.history"))?.Name == "History · 2", 3000), "the History tab does not count 2 tests");
        Assert.AreEqual("All", NameOf(win, "topbar.pt.chip.0"));
        Assert.AreEqual("on", Status(Find(win, "topbar.pt.chip.0")));
        string file = Directory.GetFiles(app.Ud, "ping-tests.json", SearchOption.AllDirectories).SingleOrDefault() ?? "";
        Assert.IsTrue(file.Length > 0, "no ping-tests.json in the test profile " + app.Ud);
        Assert.IsFalse(file.Contains(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase), "the history was written outside the test profile: " + file);
        StringAssert.Contains(File.ReadAllText(file), "\"version\": 1");
        var rows = win.FindAllDescendants().Where(e => Aid(e).StartsWith("topbar.pt.h.", StringComparison.Ordinal)).ToArray();
        Assert.AreEqual(2, rows.Length, "the history does not list both runs");
        Host.Screenshot("topbar-pingtest-history");

        // open the older run, Save… with a name typed through UI Automation, then Save (TOP-153/157)
        rows[1].AsButton().Invoke();
        Invoke(win, "topbar.pt.pt-save");
        Find(win, "topbar.pt.name").AsTextBox().Text = "upstairs";
        Invoke(win, "topbar.pt.pt-commit");
        Assert.IsTrue(Host.Until(() => File.ReadAllText(file).Contains("\"name\": \"upstairs\""), 3000), "the saved name never reached ping-tests.json");
        Assert.AreEqual("Rename…", NameOf(win, "topbar.pt.pt-rename"));

        // Rename… with the keyboard (TOP-091/154): a real click in the box, typed letters, Enter saves; the dropdown stays
        Invoke(win, "topbar.pt.pt-rename");
        var box = Find(win, "topbar.pt.name");
        Assert.AreEqual("upstairs", box.AsTextBox().Text);
        var br = box.BoundingRectangle;
        ZOrder.Click(br.Right - 6, br.Top + br.Height / 2);
        if (Win32.GetForegroundWindow() != bar) Assert.IsTrue(ZOrder.Foreground(bar), "the bar never took the foreground, so the name box cannot get keys");
        FrameProbe.Tap(0x23);   // End
        FrameProbe.Tap(0x32);   // "2"
        FrameProbe.Tap(0x0D);   // Enter
        Host.LogContains(() => app.Log, "topbar: ping test name box: Enter", "Enter in the name box was not the box's", 4000);
        Assert.IsTrue(Host.Until(() => File.ReadAllText(file).Contains("\"name\": \"upstairs2\""), 3000), "Enter did not rename the test");
        Assert.IsNotNull(win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0")), "Enter in the name box closed the dropdown");

        // compare with the other (TOP-158), back, then Delete asks once (TOP-157)
        Invoke(win, "topbar.pt.pt-cmp");
        var pick = Host.Until(() => win.FindAllDescendants().FirstOrDefault(e => Aid(e).StartsWith("topbar.pt.h.", StringComparison.Ordinal)), 3000);
        Assert.IsNotNull(pick, "Compare with… lists no test to pick");
        pick!.AsButton().Invoke();
        Assert.IsNotNull(Find(win, "topbar.pt.compare"), "the comparison has no table");
        Host.Screenshot("topbar-pingtest-compare");
        Invoke(win, "topbar.pt.pt-uncmp");
        rows = Host.Until(() => win.FindAllDescendants().Where(e => Aid(e).StartsWith("topbar.pt.h.", StringComparison.Ordinal)).ToArray() is { Length: 2 } a ? a : null, 3000)!;
        Assert.IsNotNull(rows, "Back to history did not list both tests");
        rows[0].AsButton().Invoke();
        Invoke(win, "topbar.pt.pt-del");
        Assert.AreEqual("Delete: click again to confirm", NameOf(win, "topbar.pt.pt-del"));
        Invoke(win, "topbar.pt.pt-del");
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.pt.tab.history"))?.Name == "History · 1", 3000), "the second Delete press did not delete the test");
        FrameProbe.Tap(0x1B);
        Host.LogContains(() => app.Log, "topbar: menu closed (Escape; refocus yes)", "Escape did not close the dropdown", 4000);
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the ping test");
    }

    static int Count(string s, string part) { int n = 0, i = 0; while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; } return n; }
}
