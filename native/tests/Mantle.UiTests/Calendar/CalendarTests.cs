using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Calendar;

/// <summary>
/// The clock's calendar flyout (Surfaces/Bar/Calendar, lane native-r7-calendar) from outside: it opens from the clock,
/// draws the 42-day month with a dot under each day that has events, the selected day's list, moves with the arrow
/// keys, in light and dark. The events are MANTLE_TEST_GCAL's made-up ones (the feature draws them in place of
/// Google's in --test), the clock is frozen by MANTLE_TEST_CAL_NOW, so every runner sees the same month. Nothing
/// is sent to Google and no browser opens. Ids: docs/lanes/calendar.md.
/// </summary>
[TestClass]
public class CalendarTests
{
    /// 2026-11-11 12:00 UTC: a Wednesday; November 2026 starts on a Sunday.
    public const long Now = 1794398400000;

    /// Three days of an all-day event from today, one timed event today, one on the 20th.
    public const string Fixture = "{\"status\":{\"enabled\":true,\"client\":true,\"connected\":true,\"signedIn\":true,\"needsSignIn\":false,\"signingIn\":false," +
        "\"account\":\"alex@example.com\",\"calendars\":[{\"id\":\"alex@example.com\",\"name\":\"Alex\",\"color\":\"#039be5\",\"primary\":true,\"on\":true}," +
        "{\"id\":\"club@group.calendar.google.com\",\"name\":\"Club\",\"color\":\"#33b679\",\"primary\":false,\"on\":true}]," +
        "\"syncedAt\":1794398340000,\"syncing\":false,\"error\":null,\"nextAt\":0,\"canEncrypt\":true,\"ver\":1}," +
        "\"events\":{\"ver\":1,\"spans\":[{\"from\":1790812800000,\"to\":1798761600000}]," +
        "\"calendars\":[{\"id\":\"alex@example.com\",\"color\":\"#039be5\",\"name\":\"Alex\"},{\"id\":\"club@group.calendar.google.com\",\"color\":\"#33b679\",\"name\":\"Club\"}]," +
        "\"events\":[{\"k\":\"m1\",\"cal\":\"club@group.calendar.google.com\",\"title\":\"Away weekend\",\"allDay\":true,\"s\":\"2026-11-11\",\"e\":\"2026-11-14\"}," +
        "{\"k\":\"m2\",\"cal\":\"alex@example.com\",\"title\":\"Morning run\",\"allDay\":false,\"s\":1794409200000,\"e\":1794412800000,\"link\":\"https://www.google.com/calendar/event?eid=m2\"}," +
        "{\"k\":\"m3\",\"cal\":\"alex@example.com\",\"title\":\"Dentist\",\"allDay\":false,\"s\":1795167000000,\"e\":1795168800000}]}}";

    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(300); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";

    static (BarApp App, string Dir) Launch(string profile, string theme, string? fixture = Fixture, bool keys = false)
    {
        string dir = FrameProbe.NewDir(profile);
        var env = new Dictionary<string, string> { ["MANTLE_TEST_CAL_NOW"] = Now.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (fixture != null) env["MANTLE_TEST_GCAL"] = fixture;
        if (keys) env["MANTLE_TEST_CAL_KEYS"] = "1";
        var app = BarApp.Launch(profile, theme, env: env, extra: new[] { "--bench-out=" + dir });
        return (app, dir);
    }

    static void Has(string dir, string needle, string why, int ms = 5000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);

    static AutomationElement Open(BarApp app, string dir)
    {
        ClickEl(app.Find("bar.clock"));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Calendar")), 4000), "the calendar did not open from the clock");
        Has(dir, "cal: the calendar opened (", "no opened line");
        var w = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("cal.panel")), 3000), "the pop-up has no cal.panel");
        return w;
    }

    static AutomationElement El(AutomationElement w, string id)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), 3000);
        Assert.IsNotNull(e, $"no {id} in the calendar");
        return e!;
    }

    static string[] Ids(AutomationElement w, string prefix) =>
        w.FindAllDescendants().Select(e => e.Properties.AutomationId.ValueOrDefault ?? "").Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    static string[] Rows(AutomationElement w) =>
        w.FindAllDescendants().Where(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("cal.row.", StringComparison.Ordinal)).Select(e => e.Name).ToArray();

    [TestMethod]
    public void Opens_from_the_clock_with_42_days_dots_on_the_event_days_and_todays_list()
    {
        var (app, dir) = Launch("cal-light", "light");
        using var _ = app;
        Has(dir, "a --test run with MANTLE_TEST_GCAL: the calendar draws its made-up status and 3 events", "the fixture was not taken", 15000);
        Has(dir, "cal: MANTLE_TEST_CAL_NOW: the flyout's clock is frozen at 2026-11-1", "the clock was not frozen");
        Has(dir, "cal: gcal.events arrived (ver 1,", "the bar never got the events");
        var w = Open(app, dir);
        Has(dir, "Google Calendar on", "the flyout does not say Google Calendar is on");

        Assert.AreEqual(42, Ids(w, "cal.day.").Distinct().Count(), "the month is not 42 day cells");
        Assert.AreEqual("November 2026", El(w, "cal.title").Name);
        Assert.AreEqual(7, Ids(w, "cal.dow.").Length, "not 7 weekday heads");

        var today = Status(El(w, "cal.day.2026-11-11"));
        StringAssert.Contains(today, "today");
        StringAssert.Contains(today, "selected");
        StringAssert.Contains(today, "dot");
        foreach (var d in new[] { "2026-11-12", "2026-11-13", "2026-11-20" }) StringAssert.Contains(Status(El(w, "cal.day." + d)), "dot", d + " has no dot");
        foreach (var d in new[] { "2026-11-14", "2026-11-19", "2026-11-21" }) Assert.IsFalse(Status(El(w, "cal.day." + d)).Contains("dot"), d + " has a dot");
        StringAssert.Contains(Status(El(w, "cal.day.2026-12-01")), "other");

        Assert.AreEqual("Today", El(w, "cal.agenda.head").Name);
        Assert.IsTrue(Host.Until(() => Rows(w).Length == 2, 3000), "today's list is not 2 rows: " + string.Join(" | ", Rows(w)));
        CollectionAssert.AreEquivalent(new[] { "Away weekend", "Morning run" }, Rows(w));
        Host.Screenshot("cal-open-light");

        // another day: its list
        ClickEl(El(w, "cal.day.2026-11-20"));
        Has(dir, "cal: day 2026-11-20 selected (1 events)", "the 20th was not selected");
        Assert.IsTrue(Host.Until(() => Rows(w).SequenceEqual(new[] { "Dentist" }), 3000), "the 20th's list is not the dentist: " + string.Join(" | ", Rows(w)));
        StringAssert.Contains(Status(El(w, "cal.day.2026-11-20")), "selected");

        // the next month: the grid moves, and the month is asked for (a dry run in --test)
        ClickEl(El(w, "cal.next"));
        Has(dir, "cal: shows December 2026", "the next month did not show");
        Assert.IsTrue(Host.Until(() => El(w, "cal.title").Name == "December 2026", 2000), "the title did not move");
        Assert.AreEqual(42, Ids(w, "cal.day.").Distinct().Count());
        ClickEl(El(w, "cal.today"));
        Has(dir, "cal: shows November 2026", "Today did not come back to November");

        // the clock again closes it
        ClickEl(app.Find("bar.clock"));
        IntPtr h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Calendar");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(h), 3000), "the calendar did not close from the clock");
        Has(dir, "cal: the calendar closed (", "no closed line");
        Assert.IsFalse(FrameProbe.Log(dir).Contains("eid=m2"), "an event's link reached the bar's log");
    }

    [TestMethod]
    public void Dark_moves_with_the_arrow_keys_and_page_keys_and_a_right_click_opens_it_too()
    {
        var (app, dir) = Launch("cal-dark", "dark", keys: true);
        using var _ = app;
        Has(dir, "cal: MANTLE_TEST_CAL_KEYS=1", "the keys switch was not taken", 15000);
        Has(dir, "cal: gcal.events arrived (ver 1,", "the bar never got the events");
        var w = Open(app, dir);
        Host.Screenshot("cal-open-dark");
        Assert.AreEqual("November 2026", El(w, "cal.title").Name);
        Assert.AreEqual(42, Ids(w, "cal.day.").Distinct().Count());
        Thread.Sleep(400);   // the keyboard is asked for after the first frame

        Keyboard.Type(VirtualKeyShort.RIGHT);
        Has(dir, "cal: day 2026-11-12 selected", "Right did not move to the 12th");
        Keyboard.Type(VirtualKeyShort.DOWN);
        Has(dir, "cal: day 2026-11-19 selected", "Down did not move a week");
        Keyboard.Type(VirtualKeyShort.HOME);
        Has(dir, "cal: day 2026-11-15 selected", "Home did not go to the week's Sunday");
        Keyboard.Type(VirtualKeyShort.NEXT);   // Page Down
        Has(dir, "cal: shows December 2026", "Page Down did not move a month");
        Has(dir, "cal: day 2026-12-15 selected", "Page Down did not keep the day of the month");
        Keyboard.Type(VirtualKeyShort.PRIOR);  // Page Up
        Has(dir, "cal: day 2026-11-15 selected", "Page Up did not come back");

        // the clock closes it; a right-click opens it again
        ClickEl(app.Find("bar.clock"));
        Has(dir, "cal: the calendar closed (", "no closed line");
        var c = app.Find("bar.clock").BoundingRectangle;
        Mouse.RightClick(new System.Drawing.Point(c.Left + c.Width / 2, c.Top + c.Height / 2));
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Calendar")), 4000), "a right-click on the clock did not open the calendar");
    }

    [TestMethod]
    public void Signed_out_it_still_draws_the_month_offers_Connect_and_Notifications_is_a_dry_run()
    {
        var (app, dir) = Launch("cal-none", "light", fixture: null);
        using var _ = app;
        var w = Open(app, dir);
        Has(dir, "Google Calendar not connected", "the flyout does not say it is not connected");
        Assert.AreEqual(42, Ids(w, "cal.day.").Distinct().Count());
        Assert.AreEqual(0, Ids(w, "cal.day.").Count(id => Status(El(w, id)).Contains("dot")), "a dot with no events");
        El(w, "cal.connect");
        ClickEl(El(w, "cal.notifications"));
        Has(dir, "cal: dry run (--test): would press win+n", "Notifications pressed something in a test run");
    }
}
