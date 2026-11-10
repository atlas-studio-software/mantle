using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.QuickSettings;

/// <summary>
/// Quick settings' second tile row (QsPanel.Tiles.cs, ui/bar/qs-tiles.js): Airplane mode, Do not disturb, Focus,
/// Project, Cast, drawn from MANTLE_TEST_QS's <c>qstiles</c> and <c>project</c> (Electron's mock state), and every click
/// a DRY RUN: the log says what would have been asked, the tile's note says "Test run: would ...", nothing is sent and
/// nothing opens. The runner's airplane mode, Do not disturb and displays are never changed.
/// </summary>
[TestClass]
public class QuickSettingsTilesTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Help(AutomationElement e) => e.Properties.HelpText.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why) => Host.LogContains(() => FrameProbe.Log(dir), needle, why);

    static AutomationElement El(AutomationElement w, string id)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), 3000);
        Assert.IsNotNull(e, $"no {id} in the quick-settings pop-up");
        return e!;
    }

    static bool Noted(AutomationElement w, string text)
    {
        for (int i = 0; i < 8; i++) {
            var n = w.FindFirstDescendant(cf => cf.ByAutomationId("qs.tiles.note." + i));
            if (n == null) return false;
            if (n.Name == text) return true;
        }
        return false;
    }

    [TestMethod]
    public void The_five_tiles_draw_the_fixture_and_every_click_is_a_dry_run()
    {
        string dir = FrameProbe.NewDir("qs-tiles");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = QuickSettingsTests.Fixture };
        using var app = BarApp.Launch("qs-tiles", "light", env: env, extra: new[] { "--bench-out=" + dir });
        Has(dir, "qs: a --test run with MANTLE_TEST_QS", "the fixture was not taken");
        ClickEl(app.Find("bar.volume"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open");
        var w = Host.Uia.FromHandle(h);
        Has(dir, "qs: the panel opened from bar.volume", "no opened line");

        // the row as Electron's mock draws it (qs-tiles.js + _qstiles-core.js + _project-core.js)
        var want = new (string Id, string Sub)[] { ("airplane", "Off"), ("dnd", "Off"), ("focus", "30 min"), ("project", "Extend"), ("cast", "Windows’ panel") };
        foreach (var (id, sub) in want) {
            var t = El(w, "qs.tile." + id);
            Assert.IsTrue(Host.Until(() => Help(t) == sub, 3000), $"qs.tile.{id} says \"{Help(t)}\", not \"{sub}\"");
            Assert.AreEqual("off", Status(t), $"qs.tile.{id} is not drawn off");
        }
        Host.Screenshot("qs-tiles-light");

        // each switch: the dry-run line, the tile's note, the tile back where the state is (a test never changes it)
        ClickEl(El(w, "qs.tile.airplane"));
        Has(dir, "qs: dry run (--test): airplane mode on would ask the sidecar netair.set {\"on\":true}; nothing was sent, Windows was not changed", "no airplane dry run");
        Assert.IsTrue(Host.Until(() => Noted(w, "Test run: would turn airplane mode on (Wi-Fi, Bluetooth and mobile data go off)"), 3000), "the airplane tile has no note");
        Assert.IsTrue(Host.Until(() => Status(El(w, "qs.tile.airplane")) == "off", 2000), "the airplane tile stayed on after a dry run");

        ClickEl(El(w, "qs.tile.dnd"));
        Has(dir, "qs: dry run (--test): Do not disturb on would ask the sidecar sysnotify.dnd {\"on\":true}; nothing was sent, Windows was not changed", "no Do not disturb dry run");
        Assert.IsTrue(Host.Until(() => Noted(w, "Test run: would turn Do not disturb on (its tile)"), 3000), "the Do not disturb tile has no note");

        ClickEl(El(w, "qs.tile.focus"));
        Has(dir, "qs: dry run (--test): Focus would start a 30 min timer labelled \"Focus\" and ask the sidecar sysnotify.dnd {\"on\":true}; nothing was started", "no Focus dry run");
        Assert.IsTrue(Host.Until(() => Noted(w, "Test run: would start a 30 min Focus timer and turn Do not disturb on"), 3000), "the Focus tile has no note");

        // the ways out: nothing opens, the panel stays, the note says what would have
        ClickEl(El(w, "qs.tile.project"));
        Has(dir, "qs: dry run (--test): would open Windows' Project choices (sidecar keys win+p); nothing was opened", "no Project dry run");
        ClickEl(El(w, "qs.tile.cast"));
        Has(dir, "qs: dry run (--test): would open Windows' Cast panel (sidecar keys win+k); nothing was opened", "no Cast dry run");
        Assert.IsTrue(Host.Until(() => Noted(w, "Test run: would open Windows' Cast panel (Win+K)"), 3000), "the Cast tile has no note");
        Assert.IsTrue(FrameProbe.Showing(h), "a dry-run way out closed the panel");
        Host.Screenshot("qs-tiles-notes-light");

        // no live line (QsService.Tiles logs "qs: Do not disturb on (...)", "qs: airplane mode on", "handed to ...")
        string log = FrameProbe.Log(dir);
        Assert.IsFalse(log.Contains("qs: Do not disturb on (") || log.Contains("qs: airplane mode on") || log.Contains("handed to Windows'"), "a switch went live in a test run");
    }
}
