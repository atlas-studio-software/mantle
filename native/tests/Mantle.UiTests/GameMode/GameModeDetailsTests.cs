using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mantle.UiTests.QuickSettings;

namespace Mantle.UiTests.GameMode;

/// <summary>
/// The Game mode row's leftovers (lane native-r7-media5), over the real [Feature("gamemode")] as a DRY RUN: Details while
/// off (the heading and six plan rows, BAR-812) and on (a row per action with its badge, BAR-813), the line under the
/// switch while the boost waits (BAR-786), a double click on the switch sends one call (BAR-808), Esc inside Details closes
/// it and leaves quick settings open (BAR-815), the 30 s replan with its time injected (BAR-781), and the dark theme
/// (BAR-818). MANTLE_TEST_QS_KEYS=1 lets the test panel take the keyboard (a CI runner only).
/// </summary>
[TestClass]
public class GameModeDetailsTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why, int ms = 8000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);
    static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }

    static AutomationElement El(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, $"no {id} in the quick-settings pop-up");
        return e!;
    }

    static (IntPtr H, AutomationElement W) OpenQs(BarApp app, string dir)
    {
        ClickEl(app.Find("bar.volume"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open");
        Has(dir, "qs: the panel opened from bar.volume", "no opened line");
        return (h, Host.Uia.FromHandle(h));
    }

    [TestMethod]
    public void Details_off_and_on_the_boost_line_one_call_per_double_click_Esc_and_the_injected_replan()
    {
        string dir = FrameProbe.NewDir("gamemode-details");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = QuickSettingsTests.Fixture, ["MANTLE_TEST_QS_KEYS"] = "1", ["MANTLE_TEST_GAMEMODE_REPLAN_MS"] = "1000" };
        using var app = BarApp.Launch("gamemode-details", "dark", env: env, extra: new[] { "--bench-out=" + dir });
        Has(dir, "gamemode: test run: MANTLE_TEST_GAMEMODE_REPLAN_MS=1000: while on, the plan is redone every 1000 ms (live: 30 s)", "the injected replan time was not taken");
        var (h, w) = OpenQs(app, dir);
        var sw = El(w, "qs.gamemode.switch");
        Assert.IsTrue(Host.Until(() => sw.Name == "Game mode: Turn on", 5000), $"the switch says \"{sw.Name}\"");

        // BAR-812: Details while off: "When it is on" and the six plan rows
        ClickEl(El(w, "qs.gamemode.more"));
        Has(dir, "qs: game mode row: details open", "Details did not open");
        Assert.AreEqual("When it is on", El(w, "qs.gamemode.plan.head").Name, "the plan's heading");
        foreach (var id in new[] { "power-mode", "background-apps", "mantle-itself", "boost", "wallpaper", "mantle-resting" })
            Assert.AreEqual("plan", Status(El(w, "qs.gamemode.row.plan." + id)), "the plan row " + id);

        // BAR-815: Esc inside the open Details closes it, gives Details the focus back, and leaves quick settings open
        Has(dir, "qs: game mode row: details took the keyboard (test)", "the test panel did not take the keyboard for Details");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Has(dir, "qs: game mode row: details closed (Esc); the focus is back on Details: yes", "Esc did not close Details (or the focus did not go back)");
        Assert.AreEqual("collapsed", Status(El(w, "qs.gamemode.more")), "Details is not drawn closed after Esc");
        Assert.IsTrue(Host.Until(() => El(w, "qs.gamemode.more").Properties.HasKeyboardFocus.ValueOrDefault, 3000), "Details does not have the focus after Esc");
        Thread.Sleep(400);
        Assert.IsTrue(FrameProbe.Showing(h), "Esc in Details closed quick settings");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("pop-up Quick settings: closed (Esc)"), "the pop-up heard the Esc too");

        // BAR-808: a double click on the switch: pending at once, ONE gamemode.set
        int before = Count(FrameProbe.Log(dir), "qs: game mode row: switched ");
        var r = El(w, "qs.gamemode.switch").BoundingRectangle;
        Mouse.DoubleClick(new System.Drawing.Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
        Has(dir, "gamemode: on (manual, DRY RUN)", "game mode did not turn on as a dry run", 15000);
        Has(dir, "qs: game mode row: switched on (on=true)", "the row did not hear the switch");
        Thread.Sleep(1500);
        Assert.AreEqual(before + 1, Count(FrameProbe.Log(dir), "qs: game mode row: switched "), "a double click on the switch sent more than one call");
        Assert.AreEqual(1, Count(FrameProbe.Log(dir), "gamemode: on (manual, DRY RUN)"), "game mode turned on twice");

        // BAR-786: while the boost waits, the line under the switch says what it eased and when memory is handed back
        Assert.IsTrue(Host.Until(() => El(w, "qs.gamemode.why").Name.StartsWith("Boost: ", StringComparison.Ordinal) || El(w, "qs.gamemode.why").Name.StartsWith("Test run: the boost would", StringComparison.Ordinal), 8000),
            $"the line under the switch says \"{El(w, "qs.gamemode.why").Name}\"");
        // BAR-813: Details while on: a row per action with its badge (a test run's are "Test run" / "No change" / "Not done")
        ClickEl(El(w, "qs.gamemode.more"));
        var power = El(w, "qs.gamemode.row.power");
        Assert.IsTrue(new[] { "dry", "same", "blocked", "failed" }.Contains(Status(power)), $"the power row's status is {Status(power)}");
        Assert.AreEqual("info", Status(El(w, "qs.gamemode.row.game")), "no row for the game itself");
        Assert.IsNull(w.FindFirstDescendant(cf => cf.ByAutomationId("qs.gamemode.row.why")), "a game turned on by hand has a \"Why it is on\" row");
        Assert.IsNull(w.FindFirstDescendant(cf => cf.ByAutomationId("qs.gamemode.plan.head")), "the plan heading is still there while on");

        // BAR-781: while on, the plan is redone (the interval injected: 1 s here, 30 s live)
        Has(dir, "gamemode: replan 2: the plan was redone while on (every 1000 ms)", "the plan was not redone while on", 20000);
        Host.Screenshot("gamemode-details-on-dark");   // BAR-818: the row in dark, on its own tokens

        ClickEl(El(w, "qs.gamemode.switch"));
        Has(dir, "gamemode: off: turned off by hand", "game mode did not turn off");
        string log = FrameProbe.Log(dir);
        Assert.IsFalse(log.Contains("gamemode: slowed ") || log.Contains("restore (turned off by hand)"), "a write went live in a test run");
    }
}
