using FlaUI.Core.AutomationElements;
using Mantle.UiTests.QuickSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Startup;

/// <summary>
/// A click on the bar before Mantle's host is up (lane native-r8-startup). The bar is shown and clickable before the
/// host thread hands its pop-ups their roads to Mantle's commands, so a quick first click could open quick settings
/// with no road: its Hot corners row said "This bar has no road to Mantle's commands" until the panel was opened again
/// (CornersRowTests failed that way once, run 43850529950, 0.3 s into the start). MANTLE_TEST_HOST_LATE holds the host
/// thread's start 3 s, so the click lands in that window every time; the open panel must fill in once the road arrives.
/// </summary>
[TestClass]
public class EarlyClickTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }

    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    [TestMethod]
    public void Quick_settings_opened_before_the_host_is_up_fills_in_its_corners_row_once_it_is()
    {
        string dir = FrameProbe.NewDir("early-qs");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = QuickSettingsTests.Fixture, ["MANTLE_TEST_HOST_LATE"] = "3000" };
        using var app = BarApp.Launch("early-qs", "light", env: env, extra: new[] { "--bench-out=" + dir });
        Host.LogContains(() => FrameProbe.Log(dir), "mantle: test: the host thread starts 3000 ms late (MANTLE_TEST_HOST_LATE)", "the host was not held");
        ClickEl(app.Find("bar.volume"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open");
        // the click came before the road: the row waits ("Loading…"), it does not give up
        Host.LogContains(() => FrameProbe.Log(dir), "qs: hot corners row: no link to the host yet (Mantle is still starting)", "the panel opened after the host was up, so this run did not test the early click");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("features: started", StringComparison.Ordinal), "the host was already up when the panel opened");
        var w = Host.Uia.FromHandle(h);
        // lane r15-quick-launcher: the sparse quick settings keep the Hot corners row one level down, under "More settings"
        AutomationElement? row = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("qs.more.row")), 3000);
        Assert.IsNotNull(row, "no qs.more.row in the quick-settings pop-up");
        ClickEl(row!);
        Host.LogContains(() => FrameProbe.Log(dir), "qs: the More settings view opened", "More settings did not open", 3000);
        AutomationElement? Sub() => w.FindFirstDescendant(cf => cf.ByAutomationId("qs.corners.sub"));
        Assert.AreEqual("Loading…", Sub()?.Name, "the row did not wait for the host");

        // the road arrives: the open panel reads now, and the row shows the defaults like a later open would
        Host.LogContains(() => FrameProbe.Log(dir), "qs: the panel was opened before the host was up; its rows read now", "the panel did not read when the host attached", 15000);
        Assert.IsTrue(Host.Until(() => Sub()?.Name == "2 on · push the pointer in", 8000),
            $"the sub-caption says \"{Sub()?.Name}\" and the first line \"{w.FindFirstDescendant(cf => cf.ByAutomationId("qs.corners.line.0"))?.Name}\"");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("no road to Mantle's commands", StringComparison.Ordinal), "the row said it had no road");
        Host.Screenshot("early-qs-filled");
    }
}
