using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// An app both kept on top and showing a count (lane native-r7-strip-3; ui/bar/helmbadge.css
/// <c>.cell.hb-on .ontop-badge { right: auto; left: 3px; }</c>): the count disc keeps the icon's top-right corner and the
/// keep-on-top pin moves to the top-left, so neither covers the other. Keep on top goes through the made-up Windows
/// (MANTLE_TEST_ONTOP_FAKE=1: no window is changed); the count comes from a temp badge file this test profile's own
/// helmbadge setting points at (never Helm's real one).
/// </summary>
public partial class StripTests
{
    static void WriteBadge(string file, string json) { File.WriteAllText(file + ".tmp", json); File.Move(file + ".tmp", file, true); }

    /// The pin glyph's centre x inside the cell, or null while it is not drawn.
    static double? PinX(Cell c)
    {
        var pin = c.E.FindFirstDescendant(cf => cf.ByAutomationId("bar.ontop.pin"));
        if (pin == null || pin.Properties.IsOffscreen.ValueOrDefault) return null;
        var r = pin.BoundingRectangle;
        return r.Width > 0 ? r.Left + r.Width / 2.0 : null;
    }

    [TestMethod]
    public void A_kept_on_top_app_with_a_count_shows_the_pin_top_left_and_the_count_top_right()
    {
        const string profile = "uitest-strip-pincount";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        string ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
        string bdir = Path.Combine(Path.GetTempPath(), "mantle-hb-pin-" + tag);
        Directory.CreateDirectory(bdir);
        string badge = Path.Combine(bdir, "badge.json");
        WriteBadge(badge, "{\"v\":1,\"waiting\":0}");
        Directory.CreateDirectory(Path.Combine(ud, "features"));
        File.WriteAllText(Path.Combine(ud, "features", "helmbadge.json"),
            "{\"sources\":[{\"exe\":\"powershell.exe\",\"file\":" + JsonSerializer.Serialize(badge) + ",\"label\":\"Alpha Shell\"}]}");
        var pins = PinFolder("pincount-" + tag, ("Alpha Shell", PowerShellExe));
        string fx = Path.Combine(Path.GetTempPath(), "mantle-jl-fixture-" + tag + ".json");
        File.WriteAllText(fx, MenuFixture);
        try {
            using var app = BarApp.Launch(profile, env: new Dictionary<string, string> {
                ["MANTLE_TEST_PINS_DIR"] = pins, ["MANTLE_TEST_JUMPLIST"] = fx, ["MANTLE_TEST_ONTOP_FAKE"] = "1" });
            using var a = new Host.Fixture($"Strip pin count {tag}", 60, 60);
            Func<string> log = () => LogSince(profile, 0);
            Assert.IsTrue(Host.Until(() => Named(Cells(app), a.Title) is { Open: true }, 20000, 250), $"the fixture's window never showed on Alpha Shell; cells: {Describe(Cells(app))}");
            Host.LogContains(log, "bar tasks: keep on top: the pin state is known", "the strip never learned the keep-on-top state", 15000);

            // kept on top, no count: the pin sits top-right (ui/bar/jumplist.css .cell .ontop-badge)
            Click(Named(Cells(app), a.Title)!, MouseButton.Right, app);
            Invoke(Jl(Menu(app, "Alpha Shell", profile), "bar.jl.ontop"));
            Host.LogContains(log, "would keep window " + a.Hwnd.ToInt64() + " on top; nothing in Windows changed", "keep on top did not reach the made-up Windows", 5000);
            Cell? c = null;
            Assert.IsTrue(Host.Until(() => (c = Named(Cells(app), a.Title))?.Status.Contains("kept on top", StringComparison.Ordinal) == true && PinX(c!) != null, 5000),
                "the cell is not kept on top with its pin drawn: " + Describe(Cells(app)));
            double mid = c!.Rect.Left + c.Rect.Width / 2.0;
            Assert.IsTrue(PinX(c)! > mid, $"with no count the pin is not top-right (pin x {PinX(c)}, cell {c.Rect})");

            // a count arrives: the disc takes the top-right and the pin moves top-left; both stay in the status
            WriteBadge(badge, "{\"v\":1,\"waiting\":2}");
            Assert.IsTrue(Host.Until(() => (c = Named(Cells(app), a.Title))?.Status.EndsWith("; kept on top; badge 2", StringComparison.Ordinal) == true, 15000),
                "the cell does not say kept on top and badge 2: " + Describe(Cells(app)));
            Assert.IsTrue(Host.Until(() => PinX(c!) is double x && x < mid, 3000), $"with a count the pin did not move top-left (pin x {PinX(c!)}, cell {c!.Rect})");
            Host.Screenshot("strip-pin-left-with-count");

            // the count goes back to 0: the pin returns top-right
            WriteBadge(badge, "{\"v\":1,\"waiting\":0}");
            Assert.IsTrue(Host.Until(() => (c = Named(Cells(app), a.Title))?.Status.EndsWith("; kept on top", StringComparison.Ordinal) == true, 15000),
                "the count did not clear: " + Describe(Cells(app)));
            Assert.IsTrue(Host.Until(() => PinX(c!) is double x && x > mid, 3000), $"with the count gone the pin did not go back top-right (pin x {PinX(c!)}, cell {c!.Rect})");
        } finally { try { Directory.Delete(bdir, true); } catch (IOException) { } }
    }
}
