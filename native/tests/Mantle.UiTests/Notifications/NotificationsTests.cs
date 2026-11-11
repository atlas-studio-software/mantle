using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Notifications;

/// <summary>
/// The bar's right cluster from outside (lane native-r7-notif): the bell and its notification centre, the tray strip
/// with its per-theme tones, the chips on the "more" pip and the More menu. MANTLE_TEST_NOTIFS and MANTLE_TEST_TRAY are
/// the lists (the runner's own are never read), and every dismiss, clear, open, tray click and key press is a DRY RUN:
/// the log says what would have been sent. Ids: docs/lanes/notifications.md.
/// </summary>
[TestClass]
public class NotificationsTests
{
    static long Ago(int min) => DateTimeOffset.Now.ToUnixTimeMilliseconds() - min * 60000L;

    /// Three apps, four toasts, two unread (the newest two), one that cannot be opened.
    static string Toasts() =>
        "[{\"id\":104,\"aumid\":\"com.squirrel.Discord.Discord\",\"app\":\"Discord\",\"timeMs\":" + Ago(2) + ",\"title\":\"Alex\",\"body\":\"are you still coming to dinner tonight?\",\"attribution\":\"#general\",\"opens\":\"app\",\"unread\":true}," +
        "{\"id\":103,\"aumid\":\"com.squirrel.Discord.Discord\",\"app\":\"Discord\",\"timeMs\":" + Ago(26) + ",\"title\":\"Jordan\",\"body\":\"sent a picture\",\"opens\":\"app\",\"unread\":true}," +
        "{\"id\":101,\"aumid\":\"com.readdle.SparkDesktop\",\"app\":\"Spark\",\"timeMs\":" + Ago(180) + ",\"title\":\"Calendar update\",\"body\":\"Your Saturday meeting has moved.\",\"opens\":\"app\",\"unread\":false}," +
        "{\"id\":90,\"aumid\":\"NotifyIconGeneratedAumid_1\",\"app\":\"A notification-area icon\",\"timeMs\":" + Ago(300) + ",\"title\":\"Sync paused\",\"body\":\"Resume from the tray icon.\",\"opens\":\"none\",\"opensWhy\":\"it came from a notification-area icon\",\"unread\":false}]";

    /// A 16 px square in one colour as a data: PNG (the tray's picture shape).
    static string Png(Color c)
    {
        using var b = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        for (int y = 3; y < 13; y++) for (int x = 3; x < 13; x++) b.SetPixel(x, y, c);
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    const string Hid = "is in Windows' hidden-icons flyout, which only exists while that flyout is open";
    static string Tray() =>
        "[{\"id\":\"n:1\",\"title\":\"Windows Update\",\"icon\":\"" + Png(Color.FromArgb(255, 255, 255, 255)) + "\",\"promoted\":true,\"clickable\":true,\"reason\":null}," +
        "{\"id\":\"n:5\",\"title\":\"Sync\",\"icon\":\"" + Png(Color.FromArgb(255, 21, 23, 27)) + "\",\"promoted\":true,\"clickable\":true,\"reason\":null}," +
        "{\"id\":\"n:7\",\"title\":\"Lettered\",\"icon\":null,\"promoted\":null,\"clickable\":false,\"reason\":\"Windows did not say whether it can be clicked\"}," +
        "{\"id\":\"n:3\",\"title\":\"Tailscale\",\"icon\":\"" + Png(Color.FromArgb(255, 47, 111, 224)) + "\",\"promoted\":false,\"clickable\":false,\"reason\":\"Tailscale " + Hid + "\"}]";

    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    // UI Automation Invoke: a button scrolled out of the list (it caps at 300) cannot be reached by the mouse
    static void Press(AutomationElement e) { e.Patterns.Invoke.Pattern.Invoke(); Thread.Sleep(350); }
    static void RightClickEl(AutomationElement e) { var r = e.BoundingRectangle; FlaUI.Core.Input.Mouse.RightClick(new Point(r.Left + r.Width / 2, r.Top + r.Height / 2)); Thread.Sleep(350); }

    static (BarApp App, string Dir) Launch(string profile, string theme = "light", bool notifs = true, bool tray = false)
    {
        string dir = FrameProbe.NewDir(profile);
        var env = new Dictionary<string, string>();
        if (notifs) env["MANTLE_TEST_NOTIFS"] = Toasts();
        if (tray) env["MANTLE_TEST_TRAY"] = Tray();
        var app = BarApp.Launch(profile, theme, env: env, extra: new[] { "--bench-out=" + dir });
        return (app, dir);
    }

    static Func<string> Log(string dir) => () => FrameProbe.Log(dir);

    static AutomationElement Pop(BarApp app, string dir, string name, string panelId)
    {
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: " + name)), 3000), name + " did not open. Log:\n" + Tail(dir));
        var w = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(panelId)), 3000), "the pop-up has no " + panelId);
        return w;
    }

    static AutomationElement El(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, "no " + id);
        return e!;
    }

    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Help(AutomationElement e) => e.Properties.HelpText.ValueOrDefault ?? "";
    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 30))); }

    [TestMethod]
    public void Bell_counts_the_new_ones_and_the_centre_draws_groups_dismisses_and_clears_as_dry_runs()
    {
        var (app, dir) = Launch("ntf-centre");
        using var _ = app;
        Host.LogContains(Log(dir), "notifs: a --test run with MANTLE_TEST_NOTIFS: 4 made-up notifications", "the fixture was not taken", 15000);
        var bell = app.Find("bar.notifications");
        Assert.IsTrue(Host.Until(() => Status(bell) == "new 2", 5000), "the bell does not count 2 new: " + Status(bell));
        Assert.AreEqual("Notifications — 4 (2 new)", bell.Name);
        Assert.IsNotNull(app.TryFind("bar.notifications.badge", 2000), "no count beside the bell");
        Assert.IsTrue(bell.BoundingRectangle.Width > 36 * Win32.GetDpiForWindow(app.Hwnd) / 96.0, "the bell did not widen for its count");
        Host.Screenshot("notifs-bell-new");

        ClickEl(bell);
        var w = Pop(app, dir, "Notifications", "notifs.panel");
        Host.LogContains(Log(dir), "notifs: the panel opened (4 notifications, 2 new)");
        Assert.AreEqual("Notifications · 4", El(w, "notifs.head").Name);
        Assert.AreEqual("Discord", El(w, "notifs.group.0").Name);
        Assert.AreEqual("Spark", El(w, "notifs.group.1").Name);
        Assert.AreEqual("unread", Status(El(w, "notifs.card.104")));
        Assert.AreEqual("seen", Status(El(w, "notifs.card.101")));
        Assert.AreEqual("seen dead", Status(El(w, "notifs.card.90")));
        StringAssert.StartsWith(Help(El(w, "notifs.card.90")), "Mantle cannot open this one: it came from a notification-area icon");
        Assert.AreEqual("#general · 2 min ago", El(w, "notifs.card.104.meta").Name);
        // opening the panel marks them seen: the count goes, the dot stays on what was new at the open
        Assert.IsTrue(Host.Until(() => Status(bell) == "some", 3000), "the bell still counts after the open: " + Status(bell));
        Host.Screenshot("notifs-centre-light");

        Press(El(w, "notifs.card.104.x"));
        Host.LogContains(Log(dir), "notifs: dry run (--test): dismiss: would dismiss 1 notification (104) in Windows; nothing was sent, Windows was not changed");
        Assert.IsTrue(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("notifs.card.104")) == null, 3000), "the dismissed card is still drawn");
        Assert.IsTrue(Host.Until(() => El(w, "notifs.head").Name == "Notifications · 3", 2000), "the head did not count 3");

        Press(El(w, "notifs.group.1.x"));
        Host.LogContains(Log(dir), "notifs: dry run (--test): dismiss that app's notifications: would dismiss 1 notification (101) in Windows");

        Press(El(w, "notifs.clear"));
        Host.LogContains(Log(dir), "notifs: dry run (--test): clear all: would dismiss 2 notifications (103, 90) in Windows");
        Assert.IsTrue(Host.Until(() => El(w, "notifs.empty").Name == "No notifications", 3000), "no empty state after Clear all");
        Assert.IsTrue(Host.Until(() => Status(bell) == "none", 2000), "the bell did not go to none: " + Status(bell));

        // the link to Windows' own centre: the panel closes and Win+N is a dry run
        ClickEl(El(w, "notifs.windows"));
        Host.LogContains(Log(dir), "sys.keys: dry run (--test): would press win+n; nothing was pressed");
        IntPtr h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Notifications");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(h), 2000), "the centre did not close for Windows' own");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("notifs: the panel could not be resized"), "a resize failed:\n" + Tail(dir));
    }

    [TestMethod]
    public void A_list_that_cannot_be_read_mutes_the_bell_says_why_and_a_right_click_opens_it_in_dark()
    {
        var (app, dir) = Launch("ntf-off", theme: "dark", notifs: false);
        using var _ = app;
        Host.LogContains(Log(dir), "notifs: the bell says \"Notifications unavailable", "the bell never said unavailable", 15000);
        var bell = app.Find("bar.notifications");
        Assert.IsTrue(Host.Until(() => Status(bell) == "off", 3000), "the bell is not off: " + Status(bell));
        var pip = app.TryFind("bar.more.pip", 5000);
        Assert.IsNotNull(pip, "no pip on more");
        StringAssert.StartsWith(Help(pip!), "warn: notifications off");
        // the bar's first moments may not take a right-click yet: tried until the centre shows (3 tries)
        for (int i = 0; i < 3 && !FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Notifications")); i++) { RightClickEl(bell); Thread.Sleep(700); }
        var w = Pop(app, dir, "Notifications", "notifs.panel");
        Assert.AreEqual("Notifications unavailable", El(w, "notifs.empty").Name);
        Host.Screenshot("notifs-centre-off-dark");
        // a right-click only opens: a second one leaves it open; the bell's click closes it
        RightClickEl(bell);
        IntPtr h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Notifications");
        Assert.IsTrue(FrameProbe.Showing(h), "a right-click closed the centre");
        ClickEl(bell);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(h), 2000), "the bell did not close the centre");
    }

    [TestMethod]
    public void Tray_strip_draws_tones_dims_the_unclickable_and_clicks_are_dry_runs_and_the_more_menu_lists_the_hidden()
    {
        var (app, dir) = Launch("ntf-tray", notifs: false, tray: true);
        using var _ = app;
        Host.LogContains(Log(dir), "tray: a --test run with MANTLE_TEST_TRAY: 4 made-up icons", "the tray fixture was not taken", 15000);
        Host.LogContains(Log(dir), "tray: the strip shows 3 icons, 1 hidden in Windows' flyout", "the strip was not drawn", 5000);
        var white = app.Find("bar.tray.n:1");
        Assert.IsTrue(Host.Until(() => Status(white) == "light", 3000), "the white icon is not measured light: " + Status(white));
        Assert.IsTrue(Host.Until(() => Status(app.Find("bar.tray.n:5")) == "dark", 3000), "the black icon is not measured dark");
        Assert.AreEqual("normal off", Status(app.Find("bar.tray.n:7")));
        Assert.IsNull(app.TryFind("bar.tray.n:3", 500), "a hidden-flyout icon is on the strip");
        // the strip sits left of the separator: before the downloads button
        Assert.IsTrue(white.BoundingRectangle.Right < app.Find("bar.downloads").BoundingRectangle.Left, "the strip is not left of the cluster");
        Host.Screenshot("tray-strip-light");

        ClickEl(white);
        Host.LogContains(Log(dir), "tray: dry run (--test): would left-click the notification-area icon n:1; nothing was sent");
        RightClickEl(app.Find("bar.tray.n:5"));
        Host.LogContains(Log(dir), "tray: dry run (--test): would right-click the notification-area icon n:5; nothing was sent");
        ClickEl(app.Find("bar.tray.n:7"));
        var pip = app.Find("bar.more.pip");
        Assert.IsTrue(Host.Until(() => Help(pip).Contains("can’t click Lettered: Windows did not say whether it can be clicked", StringComparison.Ordinal), 3000), "the refused click raised no chip: " + Help(pip));
        Assert.IsFalse(FrameProbe.Log(dir).Contains("notification-area icon n:7"), "an unclickable icon was sent a click");

        // the More menu: the chips in words, the hidden icon (dimmed, its click says why), the items as dry runs
        ClickEl(app.Find("bar.more"));
        var m = Pop(app, dir, "More", "more.panel");
        Assert.AreEqual("Test copy: it changes nothing on this PC", El(m, "more.who").Name);
        StringAssert.StartsWith(Help(El(m, "more.prob.trayclick")), "warn: Windows did not say");
        var hidden = El(m, "bar.tray.n:3");
        Assert.IsTrue(Host.Until(() => Status(hidden) == "normal off", 2000), "the hidden icon is not dimmed: " + Status(hidden));
        Host.Screenshot("more-menu-light");
        Press(hidden);
        // the click says, under the row, the same reason its tooltip gives (the sidecar's words, or Mantle's plain fallback)
        string said = Help(hidden).Split("\n\n").Last();
        Assert.IsTrue(Host.Until(() => El(m, "more.hidwhy").Name == said, 2000), "the hidden icon's click did not say why: " + El(m, "more.hidwhy").Name + " (tooltip: " + said + ")");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("notification-area icon n:3"), "a hidden icon was sent a click");
        var quit = El(m, "more.quit");
        Press(quit);
        Assert.IsTrue(Host.Until(() => Status(El(m, "more.quit")) == "armed", 2000), "Quit did not arm");
        Assert.AreEqual("Click again to quit", El(m, "more.quit").Name);
        Press(El(m, "more.settings"));
        Host.LogContains(Log(dir), "more: dry run (--test): would open Settings (ms-settings:); nothing was opened");
        Assert.IsFalse(app.Proc.HasExited, "the test copy quit");
    }

    [TestMethod]
    public void Tray_tones_hold_in_dark_and_follow_a_theme_switch()
    {
        var (app, dir) = Launch("ntf-tray-dark", theme: "dark", notifs: false, tray: true);
        using var _ = app;
        Host.LogContains(Log(dir), "tray: the strip shows 3 icons", "the strip was not drawn", 15000);
        Assert.IsTrue(Host.Until(() => Status(app.Find("bar.tray.n:1")) == "light", 3000), "the white icon is not light in dark");
        Assert.IsTrue(Host.Until(() => Status(app.Find("bar.tray.n:5")) == "dark", 3000), "the black icon is not dark in dark");
        Host.Screenshot("tray-strip-dark");
    }
}
