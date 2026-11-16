using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Mantle.UiTests.QuickSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Corners;

/// <summary>
/// Quick settings' Hot corners row (Surfaces/Desktop/CornersRow.cs, ui/bar/corners.js) over the real [Feature("corners")]:
/// the four tiles laid out like the screen with the defaults, a tile opens its choices, a chip saves at once (in the TEST
/// profile's features\corners.json, never %APPDATA%\mantle) and the tile says the new action, the current chip just
/// closes, and Esc closes the choices first and the panel on the next Esc. A --test run never reads the real cursor,
/// so no corner fires here; the row's words are the Core tests' (CornerRowTests).
/// </summary>
[TestClass]
public class CornersRowTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why, int ms = 8000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);
    static void Esc() { Keyboard.Press(VirtualKeyShort.ESCAPE); Thread.Sleep(120); Keyboard.Release(VirtualKeyShort.ESCAPE); Thread.Sleep(350); }

    static AutomationElement El(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, $"no {id} in the quick-settings pop-up");
        return e!;
    }

    static bool Gone(AutomationElement w, string id) => w.FindFirstDescendant(cf => cf.ByAutomationId(id)) is not { IsOffscreen: false };

    [TestMethod]
    public void The_row_shows_the_corners_like_the_screen_a_chip_saves_at_once_and_Esc_closes_the_choices_first()
    {
        string dir = FrameProbe.NewDir("corners-row");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = QuickSettingsTests.Fixture };
        using var app = BarApp.Launch("corners-row", "light", env: env, extra: new[] { "--bench-out=" + dir });
        ClickEl(app.Find("bar.volume"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open");
        var w = Host.Uia.FromHandle(h);

        // the defaults: Task view top left, Show desktop bottom right, the other two Off
        Assert.IsTrue(Host.Until(() => El(w, "qs.corners.sub").Name == "2 on · push the pointer in", 6000), $"the sub-caption says \"{El(w, "qs.corners.sub").Name}\" and the first line \"{w.FindFirstDescendant(cf => cf.ByAutomationId("qs.corners.line.0"))?.Name}\"; log: {string.Join(" / ", FrameProbe.Log(dir).Split((char)10).Where(l => l.Contains("corners")).TakeLast(6))}");
        Assert.AreEqual("Hot corners: pick what each screen corner does", El(w, "qs.corners.grid").Name);
        Assert.AreEqual("Top left corner: Task view", El(w, "qs.corners.tile.tl").Name);
        Assert.AreEqual("Top right corner: Off", El(w, "qs.corners.tile.tr").Name);
        Assert.AreEqual("Bottom right corner: Show desktop", El(w, "qs.corners.tile.br").Name);
        var tl = El(w, "qs.corners.tile.tl").BoundingRectangle; var br = El(w, "qs.corners.tile.br").BoundingRectangle;
        Assert.IsTrue(br.Left > tl.Left && br.Top > tl.Top, $"the tiles are not laid out like the screen (tl {tl}, br {br})");
        Assert.IsTrue(Host.Until(() => El(w, "qs.corners.line.0").Name == "This is a test copy of Mantle: corners only pretend to act.", 3000), "no test-copy line");
        Assert.IsTrue(Gone(w, "qs.corners.chip.off"), "choices show before a tile was clicked");
        Host.Screenshot("corners-row-light");

        // a tile opens its choices; a different chip saves at once
        ClickEl(El(w, "qs.corners.tile.tr"));
        Has(dir, "qs: hot corners row: tr open", "the top-right tile did not open");
        StringAssert.StartsWith(Status(El(w, "qs.corners.tile.tr")), "expanded");
        Assert.AreEqual("pressed", Status(El(w, "qs.corners.chip.off")), "the current choice (Off) is not marked");
        Assert.IsNotNull(El(w, "qs.corners.chip.lock"));
        Host.Screenshot("corners-row-open-light");
        ClickEl(El(w, "qs.corners.chip.lock"));
        Has(dir, "corners: the top-right corner is now Lock screen (quick settings)", "the feature did not save the choice");
        Has(dir, "qs: hot corners row: set tr to lock: saved", "the row did not hear the save");
        Assert.IsTrue(Host.Until(() => El(w, "qs.corners.tile.tr").Name == "Top right corner: Lock screen", 3000), $"the tile says \"{El(w, "qs.corners.tile.tr").Name}\"");
        Assert.IsTrue(Host.Until(() => El(w, "qs.corners.sub").Name == "3 on · push the pointer in", 3000), "the sub-caption did not count the new corner");
        Assert.IsTrue(Host.Until(() => Gone(w, "qs.corners.chip.lock"), 3000), "the choices stayed open after a save");
        Assert.IsFalse(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mantle", "features", "corners.json"))
            && File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mantle", "features", "corners.json")).Contains("\"tr\":\"lock\""), "the choice went to %APPDATA%\\mantle");

        // the current chip just closes (no save)
        ClickEl(El(w, "qs.corners.tile.tr"));
        ClickEl(El(w, "qs.corners.chip.lock"));
        Has(dir, "qs: hot corners row: tr closed (its own choice)", "the current chip did not just close");

        // Esc: the choices first, the panel on the next Esc
        ClickEl(El(w, "qs.corners.tile.tl"));
        Has(dir, "qs: hot corners row: tl open", "the top-left tile did not open");
        Esc();
        Has(dir, "qs: hot corners row: Esc closed the tl choices", "Esc did not close the choices");
        Assert.IsTrue(FrameProbe.Showing(h), "the first Esc closed the whole panel");
        Esc();
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(h), 3000), "the second Esc did not close the panel");
    }
}
