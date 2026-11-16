using FlaUI.Core.AutomationElements;
using Mantle.UiTests.QuickSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.GameMode;

/// <summary>
/// The Game mode row in quick settings (Surfaces/Bar/GameMode/GameModeRow.cs, ui/bar/gamemode.js) over the real
/// [Feature("gamemode")]: the switch turns game mode on by hand and off again as a DRY RUN (a --test run never sends a
/// write: no power mode, Efficiency mode or priority changes on the runner), Details lists what it would do, and a
/// restore record left in the TEST profile is put back as a dry run at start and dropped (never %APPDATA%\mantle).
/// </summary>
[TestClass]
public class GameModeTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why, int ms = 8000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);

    static AutomationElement El(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, $"no {id} in the quick-settings pop-up");
        return e!;
    }

    static AutomationElement OpenQs(BarApp app, string dir)
    {
        ClickEl(app.Find("bar.volume"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open");
        Has(dir, "qs: the panel opened from bar.volume", "no opened line");
        return Host.Uia.FromHandle(h);
    }

    [TestMethod]
    public void The_switch_turns_game_mode_on_and_off_as_a_dry_run_and_Details_say_what_it_does()
    {
        string dir = FrameProbe.NewDir("gamemode");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = QuickSettingsTests.Fixture };
        using var app = BarApp.Launch("gamemode", "light", env: env, extra: new[] { "--bench-out=" + dir });
        var w = OpenQs(app, dir);
        var sw = El(w, "qs.gamemode.switch");
        Assert.IsTrue(Host.Until(() => sw.Name == "Game mode: Turn on", 5000), $"the switch says \"{sw.Name}\", not \"Game mode: Turn on\"");
        StringAssert.StartsWith(El(w, "qs.gamemode.sub").Name, "Off");
        Assert.IsTrue(Host.Until(() => El(w, "qs.gamemode.why").Name == "Test run: everything is planned and shown, nothing on this PC is changed", 8000),
            $"the line under the switch says \"{El(w, "qs.gamemode.why").Name}\"");

        // Details while off: the plan
        ClickEl(El(w, "qs.gamemode.more"));
        Has(dir, "qs: game mode row: details open", "Details did not open");
        Assert.AreEqual("plan", Status(El(w, "qs.gamemode.row.plan.power-mode")), "no plan row for the power mode");
        Assert.IsNotNull(El(w, "qs.gamemode.opt.boost"), "no Boost option");
        Host.Screenshot("gamemode-details-off-light");

        // on by hand: a dry run
        ClickEl(sw);
        Has(dir, "gamemode: on (manual, DRY RUN)", "game mode did not turn on as a dry run", 15000);
        Has(dir, "qs: game mode row: switched on (on=true)", "the row did not hear the switch");
        Assert.IsTrue(Host.Until(() => Status(El(w, "qs.gamemode.switch")).StartsWith("on", StringComparison.Ordinal), 5000), "the switch is not drawn on");
        StringAssert.StartsWith(El(w, "qs.gamemode.sub").Name, "On");
        var power = El(w, "qs.gamemode.row.power");
        Assert.IsTrue(new[] { "dry", "same", "blocked", "failed" }.Contains(Status(power)), $"the power row's status is {Status(power)}");
        Assert.AreEqual("info", Status(El(w, "qs.gamemode.row.game")), "no row for the game itself");
        Host.Screenshot("gamemode-on-light");

        // off by hand
        ClickEl(El(w, "qs.gamemode.switch"));
        Has(dir, "gamemode: off: turned off by hand", "game mode did not turn off");
        Assert.IsTrue(Host.Until(() => El(w, "qs.gamemode.switch").Name == "Game mode: Turn on", 5000), "the switch did not come back to Turn on");
        Assert.IsTrue(Host.Until(() => (w.FindFirstDescendant(cf => cf.ByAutomationId("qs.gamemode.last"))?.Name ?? "").Contains("test run: nothing had been changed", StringComparison.OrdinalIgnoreCase), 5000), "no last-game line");

        string log = FrameProbe.Log(dir);
        Assert.IsFalse(log.Contains("gamemode: slowed ") || log.Contains("restore (turned off by hand)"), "a write went live in a test run");
    }

    [TestMethod]
    public void A_restore_record_in_the_test_profile_is_put_back_as_a_dry_run_and_dropped()
    {
        string profile = "gamemode-crash";
        string ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
        Directory.CreateDirectory(ud);
        string rec = Path.Combine(ud, "gamemode-restore.json");
        File.WriteAllText(rec, "{\"v\":1,\"since\":1,\"how\":\"auto\",\"game\":{\"file\":\"standin-game.exe\",\"pid\":4242},\"power\":{\"ac\":{\"was\":\"balanced\",\"set\":\"performance\"}},\"saver\":null," +
            "\"procs\":[{\"pid\":4243,\"created\":\"1\",\"file\":\"standin-app.exe\",\"group\":\"background\",\"eco\":{\"was\":{\"control\":0,\"state\":0},\"set\":{\"control\":1,\"state\":1}},\"prio\":{\"was\":\"normal\",\"set\":\"belownormal\"}}]," +
            "\"wallpaper\":false,\"lively\":null,\"trim\":{\"pids\":[]}}");
        string dir = FrameProbe.NewDir("gamemode-crash");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = QuickSettingsTests.Fixture };
        using var app = BarApp.Launch(profile, "light", env: env, extra: new[] { "--bench-out=" + dir });
        Has(dir, "gamemode: dry run (--test): would ask the sidecar gamemode.apply {\"items\":[{\"pid\":4243", "the crash put-back did not plan the apps' restore", 20000);
        Has(dir, "gamemode: restore (left over from before Mantle restarted): test run: would put 1 apps back to normal; plugged in back to balanced", "no restore line");
        Has(dir, "gamemode: test run: the restore record was read from the test profile and dropped; nothing was sent", "the record was not dropped");
        Assert.IsTrue(Host.Until(() => !File.Exists(rec), 3000), "the test profile's record is still there");
        var w = OpenQs(app, dir);
        Assert.IsTrue(Host.Until(() => El(w, "qs.gamemode.why").Name.StartsWith("After Mantle restarted: test run: would put 1 apps back to normal", StringComparison.Ordinal), 8000),
            $"the line under the switch says \"{El(w, "qs.gamemode.why").Name}\"");
        Host.Screenshot("gamemode-crash-light");
    }
}
