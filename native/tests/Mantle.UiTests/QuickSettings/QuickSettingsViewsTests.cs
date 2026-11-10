using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.QuickSettings;

/// <summary>
/// Quick settings one level down (QsPanel.More.cs, ui/bar/qs-more.js): the Wi-Fi, Bluetooth and sound "›" views open,
/// draw MANTLE_TEST_QS's made-up rows, and every click on them is a DRY RUN: the log says what would have been asked
/// and nothing reaches Windows. The password field (Q-W6) takes the keyboard only with MANTLE_TEST_QS_KEYS=1, and its
/// password never reaches the log.
/// </summary>
[TestClass]
public class QuickSettingsViewsTests
{
    /// Part 1's fixture plus the made-up lists the views draw.
    public static readonly string Fixture = QuickSettingsTests.Fixture[..^1] +
        ",\"wifiList\":{\"ok\":true,\"present\":true,\"on\":true,\"networks\":[" +
            "{\"ssid\":\"Mantle test\",\"hex\":\"4d\",\"signal\":80,\"secure\":true,\"security\":\"WPA2-Personal\",\"known\":true,\"connected\":true,\"action\":\"disconnect\"}," +
            "{\"ssid\":\"Cafe upstairs\",\"hex\":\"43\",\"signal\":50,\"secure\":true,\"security\":\"WPA2-Personal\",\"known\":false,\"connected\":false,\"action\":\"password\"}," +
            "{\"ssid\":\"Library guest\",\"hex\":\"4c\",\"signal\":20,\"secure\":false,\"known\":false,\"connected\":false,\"action\":\"open\"}]}," +
        "\"btList\":{\"ok\":true,\"present\":true,\"on\":true,\"devices\":[" +
            "{\"id\":\"bt-1\",\"name\":\"Mantle buds\",\"kind\":\"headphones\",\"connected\":true,\"battery\":60,\"canConnect\":true}," +
            "{\"id\":\"bt-2\",\"name\":\"Desk mouse\",\"kind\":\"mouse\",\"connected\":false,\"canConnect\":false,\"connectWhy\":\"Windows connects a mouse by itself\"}]}," +
        "\"devices\":[{\"id\":\"dev-1\",\"name\":\"Speakers\",\"isDefault\":true,\"roles\":{\"communications\":true}},{\"id\":\"dev-2\",\"name\":\"Headset\",\"isDefault\":false}]," +
        "\"sessions\":[{\"key\":\"app-1\",\"name\":\"Music player\",\"level\":40,\"muted\":false,\"active\":true,\"path\":\"C:\\\\Apps\\\\music.exe\"},{\"key\":\"sys\",\"system\":true,\"level\":100,\"muted\":false,\"active\":false}]}";

    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(300); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Help(AutomationElement e) => e.Properties.HelpText.ValueOrDefault ?? "";

    static (BarApp App, string Dir, AutomationElement Pop) Open(string profile, bool keys)
    {
        string dir = FrameProbe.NewDir(profile);
        var env = new Dictionary<string, string> { ["MANTLE_TEST_QS"] = Fixture };
        if (keys) env["MANTLE_TEST_QS_KEYS"] = "1";
        var app = BarApp.Launch(profile, "light", env: env, extra: new[] { "--bench-out=" + dir });
        Host.LogContains(() => FrameProbe.Log(dir), "qs: a --test run with MANTLE_TEST_QS", "the fixture was not taken", 15000);
        ClickEl(app.Find("bar.volume"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Quick settings")), 3000), "quick settings did not open");
        return (app, dir, Host.Uia.FromHandle(h));
    }

    static AutomationElement El(AutomationElement w, string id)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), 3000);
        Assert.IsNotNull(e, $"no {id} in the quick-settings pop-up");
        return e!;
    }

    static void Has(string dir, string needle, string why) => Host.LogContains(() => FrameProbe.Log(dir), needle, why);

    [TestMethod]
    public void The_three_views_open_draw_their_rows_and_every_click_is_a_dry_run()
    {
        var (app, dir, w) = Open("qs-views", keys: false);
        using var _ = app;

        // ---- Wi-Fi
        ClickEl(El(w, "qs.more.wifi"));
        Has(dir, "qs: the Wi-Fi view opened", "the Wi-Fi view did not open");
        Has(dir, "pop-up Quick settings: resized to 384 DIP high", "the pop-up was not re-sized for the view");
        Assert.AreEqual("Wi-Fi", El(w, "qs.view.title").Name);
        var home = El(w, "qs.wifi.net.0");
        Assert.AreEqual("Mantle test", home.Name);
        Assert.AreEqual("Connected, secured", Help(home));
        Assert.AreEqual("WPA2-Personal", Help(El(w, "qs.wifi.net.1")));
        Assert.AreEqual("Open", Help(El(w, "qs.wifi.net.2")));
        Has(dir, "qs: a --test run with MANTLE_TEST_QS: the scan reads the made-up networks; nothing was scanned", "the opening scan did not run");
        Host.Screenshot("qs-view-wifi");

        ClickEl(home);
        ClickEl(El(w, "qs.wifi.disconnect"));
        Has(dir, "qs: dry run (--test): a Wi-Fi disconnect would ask the sidecar wlan disconnect {}; nothing was sent, Windows was not changed", "no disconnect dry run");
        Assert.IsTrue(Host.Until(() => El(w, "qs.wifi.error").Name.Contains("this is a test run"), 3000), "the row did not say the disconnect was a test run");

        ClickEl(El(w, "qs.wifi.net.1"));   // a password network: Connect asks for the keyboard, which a test run refuses
        ClickEl(El(w, "qs.wifi.connect"));
        Has(dir, "qs: the password field did not open: a test instance never takes the keyboard", "the keyboard was not refused");
        Assert.IsTrue(Host.Until(() => El(w, "qs.wifi.error").Name.StartsWith("A password can’t be typed here."), 3000), "the row did not say why");
        Assert.IsNotNull(El(w, "qs.wifi.windows"), "no way to Windows' own list");

        ClickEl(El(w, "qs.wifi.net.2"));   // an open network: a connect with no password
        ClickEl(El(w, "qs.wifi.connect"));
        Has(dir, "qs: dry run (--test): a Wi-Fi connect would ask the sidecar wlan connect {\"auto\":true,\"hex\":\"<2 chars>\"} with no password; nothing was sent", "no connect dry run");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("Library guest"), "a Wi-Fi name reached the log");

        ClickEl(El(w, "qs.back"));
        Has(dir, "qs: the Wi-Fi view closed (the back arrow)", "the back arrow did not leave the view");

        // ---- Bluetooth
        ClickEl(El(w, "qs.more.bluetooth"));
        Has(dir, "qs: the Bluetooth view opened", "the Bluetooth view did not open");
        var buds = El(w, "qs.bt.dev.0");
        Assert.AreEqual("Mantle buds", buds.Name);
        Assert.AreEqual("Connected", Help(buds));
        Assert.AreEqual("Not connected", Help(El(w, "qs.bt.dev.1")));
        Assert.IsNotNull(El(w, "qs.bt.add"));
        Host.Screenshot("qs-view-bt");
        ClickEl(buds);
        ClickEl(El(w, "qs.bt.toggle"));
        Has(dir, "qs: dry run (--test): a Bluetooth disconnect would ask the sidecar btdevices disconnect {\"device\":\"<4 chars>\"}; nothing was sent", "no Bluetooth dry run");
        Assert.IsTrue(Host.Until(() => El(w, "qs.bt.error").Name.Contains("this is a test run"), 3000), "the row did not say the toggle was a test run");
        ClickEl(El(w, "qs.back"));

        // ---- sound output and the mixer
        ClickEl(El(w, "qs.more.sound"));
        Has(dir, "qs: the Sound output view opened", "the sound view did not open");
        Assert.AreEqual("default", Status(El(w, "qs.sound.dev.0")));
        Assert.AreEqual("Headset", El(w, "qs.sound.dev.1").Name);
        Assert.AreEqual("40%", El(w, "qs.app.0.value").Name);
        Assert.AreEqual("Volume for Music player", El(w, "qs.app.0.volume").Name);
        Host.Screenshot("qs-view-sound");
        ClickEl(El(w, "qs.sound.dev.1"));
        Has(dir, "qs: dry run (--test): the output device would ask the sidecar audio default {\"device\":\"<5 chars>\"}; nothing was sent", "no output dry run");
        Assert.IsTrue(Host.Until(() => El(w, "qs.sound.error").Name.Contains("this is a test run"), 3000), "the list did not say the switch was a test run");
        ClickEl(El(w, "qs.app.0.mute"));
        Has(dir, "qs: dry run (--test): an app's volume would ask the sidecar audio", "no per-app dry run");

        // closing from a view: the view lets go too, and the next open is the main view
        ClickEl(app.Find("bar.volume"));
        Has(dir, "qs: the Sound output view closed (the panel closed)", "the view did not let go on the close");
        Has(dir, "qs: the panel closed (its button was clicked again); its watch is let go", "no closed line");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("NOT DONE"), "a set failed");
    }

    [TestMethod]
    public void The_password_field_takes_the_keyboard_on_ask_and_the_password_never_reaches_the_log()
    {
        var (app, dir, w) = Open("qs-pw", keys: true);
        using var _ = app;
        Has(dir, "qs: MANTLE_TEST_QS_KEYS=1", "the keys opt-in was not taken");
        ClickEl(El(w, "qs.more.wifi"));
        ClickEl(El(w, "qs.wifi.net.1"));
        ClickEl(El(w, "qs.wifi.connect"));
        Has(dir, "qs: the password field has the keyboard", "the password field did not take the keyboard");
        Has(dir, "pop-up Quick settings: took the keyboard on its own ask", "the pop-up did not take the keyboard");
        var pw = El(w, "qs.wifi.password");
        Assert.IsTrue(Host.Until(() => pw.Properties.HasKeyboardFocus.ValueOrDefault, 3000), "the password field has no keyboard focus");

        Keyboard.Type("short");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Assert.IsTrue(Host.Until(() => El(w, "qs.wifi.problem").Name == "A Wi-Fi password is at least 8 characters.", 3000), "a short password was not caught");

        Keyboard.Type("-and-long");
        Keyboard.Type(VirtualKeyShort.ENTER);
        Has(dir, "with a password of 14 characters (the password itself is never logged); nothing was sent", "no connect dry run with the password's length");
        Assert.IsTrue(Host.Until(() => El(w, "qs.wifi.error").Name.Contains("this is a test run"), 3000), "the row did not say the connect was a test run");
        string log = FrameProbe.Log(dir);
        Assert.IsFalse(log.Contains("short-and-long") || log.Contains("and-long"), "the password reached the log");

        // Esc in the field closes it and gives the keyboard back to the window that had it
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Has(dir, "pop-up Quick settings: gave the keyboard back (the password field closed)", "Esc did not give the keyboard back");
        Assert.IsTrue(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("qs.wifi.password")) == null, 3000), "the password field stayed after Esc");
        ClickEl(app.Find("bar.volume"));
        Has(dir, "qs: the panel closed (its button was clicked again)", "the panel did not close");
    }
}
