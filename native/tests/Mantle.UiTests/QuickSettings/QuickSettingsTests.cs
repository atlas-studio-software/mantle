using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.QuickSettings;

/// <summary>
/// The quick-settings pop-up (Surfaces/Bar/QuickSettings, lane native-bar-qs) from outside: it opens from the bar's
/// speaker, network and battery buttons, draws MANTLE_TEST_QS's made-up readings (so every runner sees the same panel),
/// and every click is a DRY RUN in --test: the log says what would have been sent, and the runner's Wi-Fi, volume and
/// power mode are never changed. Ids: docs/lanes/quick-settings.md.
/// </summary>
[TestClass]
public class QuickSettingsTests
{
    public const string Fixture = "{\"volume\":{\"level\":62,\"muted\":false}," +
        "\"wifi\":{\"ok\":true,\"present\":true,\"on\":true,\"connected\":true,\"ssid\":\"Mantle test\",\"band\":\"5 GHz\",\"signal\":80}," +
        "\"bluetooth\":{\"ok\":true,\"present\":true,\"on\":false,\"radio\":\"off\",\"canSwitch\":true}," +
        "\"brightness\":{\"ok\":true,\"present\":true,\"level\":70}," +
        "\"nightlight\":{\"ok\":true,\"present\":true,\"on\":false}," +
        "\"power\":{\"ok\":true,\"present\":true,\"saver\":false,\"percent\":72,\"ac\":true,\"charging\":true}," +
        "\"powermode\":{\"ok\":true,\"mode\":\"balanced\",\"inForce\":\"balanced\",\"available\":true,\"settable\":true,\"source\":\"ac\",\"saver\":false,\"reason\":null}," +
        // the second tile row (Electron's mock: ui/bar/mock-mantle.js qxState / pjState)
        "\"qstiles\":{\"on\":true,\"dnd\":{\"ok\":true,\"on\":false,\"writable\":true,\"reason\":null,\"rule\":null}," +
        "\"airplane\":{\"ok\":true,\"on\":false,\"canSet\":true,\"reason\":null},\"focus\":null,\"timer\":{\"ok\":true,\"reason\":null}}," +
        "\"project\":{\"on\":true,\"read\":{\"current\":\"extend\",\"available\":[\"internal\",\"clone\",\"extend\",\"external\"],\"unavailable\":{},\"connected\":2}}}";

    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 30))); }

    static (BarApp App, string Dir) Launch(string profile, string theme = "light", string? fixture = Fixture)
    {
        string dir = FrameProbe.NewDir(profile);
        var env = new Dictionary<string, string>();
        if (fixture != null) env["MANTLE_TEST_QS"] = fixture;
        var app = BarApp.Launch(profile, theme, env: env, extra: new[] { "--bench-out=" + dir });
        return (app, dir);
    }

    static AutomationElement Panel(BarApp app, string dir)
    {
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open. Log:\n" + Tail(dir));
        var w = Host.Uia.FromHandle(h);
        var p = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("qs.panel")), 3000);
        Assert.IsNotNull(p, "the pop-up has no qs.panel");
        return w;
    }

    static AutomationElement El(AutomationElement w, string id)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), 3000);
        Assert.IsNotNull(e, $"no {id} in the quick-settings pop-up");
        return e!;
    }

    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Help(AutomationElement e) => e.Properties.HelpText.ValueOrDefault ?? "";

    [TestMethod]
    public void Opens_from_the_speaker_draws_the_readings_and_every_click_is_a_dry_run()
    {
        var (app, dir) = Launch("qs-dry");
        using var _ = app;
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: a --test run with MANTLE_TEST_QS", 15000), "the fixture was not taken. Log:\n" + Tail(dir));
        ClickEl(app.Find("bar.volume"));
        var w = Panel(app, dir);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the panel opened from bar.volume", 2000), "no opened line. Log:\n" + Tail(dir));

        var wifi = El(w, "qs.tile.wifi");
        Assert.IsTrue(Host.Until(() => Status(wifi).StartsWith("on"), 3000), "Wi-Fi is not drawn on: " + Status(wifi));
        Assert.AreEqual("Mantle test · 5 GHz", Help(wifi));
        Assert.AreEqual("off", Status(El(w, "qs.tile.bluetooth")));
        Assert.AreEqual("Off · Settings", Help(El(w, "qs.tile.saver")));
        Assert.AreEqual("62%", El(w, "qs.volume.value").Name);
        Assert.AreEqual("70%", El(w, "qs.brightness.value").Name);
        Assert.AreEqual("72% · charging", El(w, "qs.power").Name);
        Host.Screenshot("qs-open-light");

        // Wi-Fi off: the tile flips, and nothing is sent to Windows
        ClickEl(wifi);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: dry run (--test): wifi=false would ask the sidecar wifi {\"on\":false}; nothing was sent, Windows was not changed", 3000), "no dry-run line. Log:\n" + Tail(dir));
        Assert.IsTrue(Host.Until(() => Status(wifi).StartsWith("off"), 2000), "the Wi-Fi tile did not flip: " + Status(wifi));
        Assert.IsTrue(FrameProbe.LogHas(dir, "mantle: qs.set wifi=false -> ok, reads false", 2000), "no set line. Log:\n" + Tail(dir));

        // mute: the same
        var mute = El(w, "qs.mute");
        ClickEl(mute);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: dry run (--test): mute=true would ask the sidecar volume {\"muted\":true}", 3000), "no mute dry run. Log:\n" + Tail(dir));
        Assert.IsTrue(Host.Until(() => Status(El(w, "qs.mute")) == "muted", 2000), "mute did not show muted");

        // power mode: one level down since lane r15-quick-settings (the sparse layout), so More settings first
        ClickEl(El(w, "qs.more.row"));
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the More settings view opened", 2000), "More settings did not open. Log:\n" + Tail(dir));
        Assert.AreEqual("chosen", Status(El(w, "qs.powermode.balanced")));
        // a change in a test run is refused with Electron's words, and the row says so
        ClickEl(El(w, "qs.powermode.performance"));
        Assert.IsTrue(FrameProbe.LogHas(dir, "powermode: set performance (dry run): refused (this is a test run, so Windows was not changed)", 3000), "no power mode line. Log:\n" + Tail(dir));
        // the refusal is the row's note (powermode.js .pmnote), as Electron words it
        Assert.IsTrue(Host.Until(() => El(w, "qs.powermode.note").Name == "Power mode did not change: this is a test run, so Windows was not changed.", 2000), "the power mode row did not say why");
        Assert.AreEqual("chosen", Status(El(w, "qs.powermode.balanced")), "the mode moved although Windows was not changed");

        // its own button again closes it, and the watch is let go
        ClickEl(app.Find("bar.volume"));
        IntPtr h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(h), 2000), "quick settings did not close on its own button");
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the panel closed (its button was clicked again); its watch is let go", 2000), "no closed line. Log:\n" + Tail(dir));
        Assert.IsFalse(FrameProbe.Log(dir).Contains("NOT DONE"), "a set failed:\n" + Tail(dir));
    }

    /// Lane r15-quick-settings, the sparse layout (apps-r3/mantle-bar panels/quick.js): the main view has the tiles, the
    /// two sliders, ONE row "More settings" and the footer, and no power mode, game mode or hot corners row; that row goes
    /// one level down to a view with a back arrow, the title "More settings" and those rows, and no footer.
    [TestMethod]
    public void Sparse_main_view_has_the_more_settings_row_and_its_view_holds_the_rows()
    {
        var (app, dir) = Launch("qs-sparse");
        using var _ = app;
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: a --test run with MANTLE_TEST_QS", 15000), "the fixture was not taken. Log:\n" + Tail(dir));
        ClickEl(app.Find("bar.volume"));
        var w = Panel(app, dir);
        var row = El(w, "qs.more.row");
        Assert.AreEqual("More settings", row.Name);
        Assert.AreEqual("Power mode, game mode, hot corners", Help(row));
        foreach (var id in new[] { "qs.tile.wifi", "qs.more.wifi", "qs.more.bluetooth", "qs.brightness", "qs.volume", "qs.mute", "qs.more.sound", "qs.power", "qs.windows", "qs.gear" })
            El(w, id);
        foreach (var id in new[] { "qs.powermode.balanced", "qs.gamemode.switch", "qs.corners.grid", "qs.view.more-settings" })
            Assert.IsNull(w.FindFirstDescendant(cf => cf.ByAutomationId(id)), id + " is drawn on the main view; the sparse layout keeps it one level down");
        Host.Screenshot("qs-sparse-light");

        ClickEl(row);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the More settings view opened", 2000), "More settings did not open. Log:\n" + Tail(dir));
        Assert.IsNotNull(El(w, "qs.view.more-settings"));
        Assert.AreEqual("More settings", El(w, "qs.more.title").Name);
        var back = El(w, "qs.more.back");
        Assert.AreEqual("Back to quick settings", back.Name);
        foreach (var id in new[] { "qs.powermode.balanced", "qs.powermode.performance", "qs.gamemode.switch", "qs.corners.grid" })
            El(w, id);
        // no footer one level down: the main view (and its footer) is gone once its sink ends
        Assert.IsTrue(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("qs.power")) == null && w.FindFirstDescendant(cf => cf.ByAutomationId("qs.more.row")) == null, 2000),
            "the main view (its footer, the More settings row) is still drawn under the More settings view");
        Host.Screenshot("qs-more-settings-light");

        ClickEl(back);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the More settings view closed (the back arrow)", 2000), "the back arrow did not leave the view. Log:\n" + Tail(dir));
        Assert.IsNotNull(El(w, "qs.more.row"));
        Assert.IsTrue(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("qs.view.more-settings")) == null, 2000), "the More settings view is still drawn after the back arrow");
    }

    /// The prototype's More settings view holds four rows; native quick settings has no Top bar row to put there (the
    /// other three are checked above), so this check is a visible skip until one is built.
    [TestMethod]
    public void More_settings_view_has_a_top_bar_row()
    {
        Assert.Inconclusive("not built: native quick settings has no Top bar row (the prototype's quick.js sysHTML has one); power mode, game mode and hot corners are checked by Sparse_main_view_has_the_more_settings_row_and_its_view_holds_the_rows");
    }

    /// Lane r15-quick-settings, the offline state (the prototype's ?net=off): MANTLE_TEST_NET=off draws the bar's network
    /// button offline whatever the runner's network is, and the Wi-Fi tile follows the same switch (BarWindow.Network.cs
    /// sets QsPanel.ForcedOffline): on, "Not connected" (one bar lit, the rest faint), never the fixture's network.
    [TestMethod]
    public void Offline_test_switch_draws_the_wifi_tile_not_connected_like_the_bar_button()
    {
        string dir = FrameProbe.NewDir("qs-netoff");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = Fixture, ["MANTLE_TEST_NET"] = "off" };
        using var app = BarApp.Launch("qs-netoff", "light", env: env, extra: new[] { "--bench-out=" + dir });
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: a --test run with MANTLE_TEST_QS", 15000), "the fixture was not taken. Log:\n" + Tail(dir));
        var net = app.Find("bar.network");
        Assert.IsTrue(Host.Until(() => (net.Name ?? "") == "Not connected, Wi-Fi is on but not connected to a network", 8000), $"the network button is named \"{net.Name}\"");
        ClickEl(net);
        var w = Panel(app, dir);
        var wifi = El(w, "qs.tile.wifi");
        Assert.IsTrue(Host.Until(() => Help(wifi) == "Not connected", 3000), $"the Wi-Fi tile says \"{Help(wifi)}\", not \"Not connected\"");
        StringAssert.StartsWith(Status(wifi), "on", "the Wi-Fi tile is not drawn on (Wi-Fi on, nothing connected)");
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: MANTLE_TEST_NET=off: the Wi-Fi tile is drawn on and not connected", 2000), "no offline line. Log:\n" + Tail(dir));
        Host.Screenshot("qs-offline-light");
    }

    [TestMethod]
    public void Opens_from_network_and_battery_in_dark_and_reads_the_runner_without_a_fixture()
    {
        var (app, dir) = Launch("qs-dark", "dark", fixture: null);
        using var _ = app;
        ClickEl(app.Find("bar.network"));
        var w = Panel(app, dir);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the panel opened from bar.network", 2000), "no opened line. Log:\n" + Tail(dir));
        // the runner's own readings (read only): every tile says something, never a blank
        foreach (var id in new[] { "qs.tile.wifi", "qs.tile.bluetooth", "qs.tile.nightlight", "qs.tile.saver",
                                  "qs.tile.airplane", "qs.tile.dnd", "qs.tile.focus", "qs.tile.project", "qs.tile.cast" })
            Assert.IsTrue(Host.Until(() => Help(El(w, id)).Length > 0, 3000), id + " has no words");
        Assert.IsTrue(El(w, "qs.power").Name.Length > 0, "the footer has no battery line");
        Host.Screenshot("qs-open-dark-runner");
        // lane r14-bar-review: with no battery the bar has no battery button (it takes no room), so the battery click is
        // only made on a machine with one; on the runner the test says so instead of passing quietly
        var (hasBattery, noBattery) = Win32.Battery();
        if (!hasBattery) {
            Assert.IsNull(app.TryFind("bar.battery", 500), $"this machine has no battery ({noBattery}), yet the bar shows a battery button");
            Assert.IsFalse(FrameProbe.Log(dir).Contains("qs.set"), "a set was made without a click");
            Assert.Inconclusive($"the battery button was not clicked: {noBattery}, so the bar draws no battery button (the network part ran)");
        }
        ClickEl(app.Find("bar.battery"));   // another anchor of the same pop-up: closes it (one toggle per key)
        IntPtr h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(h), 2000), "the battery button did not close it");
        ClickEl(app.Find("bar.battery"));
        Panel(app, dir);
        Assert.IsTrue(FrameProbe.LogHas(dir, "qs: the panel opened from bar.battery", 2000), "the battery button did not open it. Log:\n" + Tail(dir));
        Assert.IsFalse(FrameProbe.Log(dir).Contains("qs.set"), "a set was made without a click");
    }
}
