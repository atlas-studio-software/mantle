using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// BAR-329 (ui/bar/jumplist.css; lane native-r7-strip-3): in the dark theme the app menu is Helm's menu, rows min 30
/// DIP at 5 / 10 with a 10 DIP gap, so a row's words start 37 DIP in (10 padding + 1 ring + 16 icon + 10 gap; light: 8 +
/// 1.5 + 16 + 9). A --test run with --theme=dark (its own profile), made-up jump lists, nothing in Windows changed.
/// </summary>
public partial class StripTests
{
    [TestMethod]
    public void In_the_dark_theme_the_app_menu_is_Helms_menu()
    {
        const string profile = "uitest-strip-menulook";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("menulook-" + tag, ("Beta Map", CharmapExe));
        string fx = Path.Combine(Path.GetTempPath(), "mantle-jl-fixture-look-" + tag + ".json");
        File.WriteAllText(fx, MenuFixture);
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins, ["MANTLE_TEST_JUMPLIST"] = fx },
            extra: new[] { "--theme=dark" });
        Func<string> log = () => LogSince(profile, 0);
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => Named(cs = Cells(app), "Beta Map") != null, 20000, 250), "the pin never showed: " + Describe(cs));

        Click(Named(cs, "Beta Map")!, MouseButton.Right, app);
        var w = Menu(app, "Beta Map", profile);
        Host.LogContains(log, "bar tasks: menu: look dark (Helm's menu): rows min 30 at 5 / 10, gap 10, hover LgPress, keyboard focus LgPress with a 1 px LgEdgeOut ring",
            "the menu was not built in Helm's dark look", 5000);
        var row = Jl(w, "bar.jl.pin");
        double s = Win32.GetDpiForWindow(app.Hwnd) / 96.0;
        var rr = row.BoundingRectangle;
        Assert.IsTrue(rr.Height >= 30 * s - 1, $"a dark row is {rr.Height} px high, under 30 DIP ({30 * s:0.#} px)");
        AutomationElement? words = row.FindFirstDescendant(cf => cf.ByName("Unpin from taskbar").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)));
        Assert.IsNotNull(words, "the row's words are not in its UI Automation tree");
        double inset = words!.BoundingRectangle.Left - rr.Left;
        Assert.AreEqual(37 * s, inset, 2.0, $"the row's words start {inset} px in, not 37 DIP ({37 * s:0.#} px)");
        Host.Screenshot("strip-menu-dark-helm");
    }
}
