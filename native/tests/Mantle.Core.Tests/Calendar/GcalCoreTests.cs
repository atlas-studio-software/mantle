using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.Calendar;
using Xunit;

namespace Mantle.Core.Tests.Calendar;

/// lib/features/_gcal-core.js and ui/bar/gcal-days.js, ported: PKCE, the consent URL, the client file, events, words,
/// the backoff, the day index, the dots, the agenda words. Made-up data only; no network, no disk.
public class GcalCoreTests
{
    static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Fact]
    public void PKCE_is_RFC_7636s_S256_and_the_state_is_24_random_bytes()
    {
        // RFC 7636 appendix B: these 32 octets give exactly this verifier and challenge
        byte[] rfc = { 116, 24, 223, 180, 151, 153, 224, 37, 79, 250, 96, 125, 216, 173, 187, 186, 22, 212, 37, 77, 105, 214, 191, 240, 91, 88, 5, 88, 83, 132, 141, 121 };
        var pk = GcalCore.Pkce(n => { Assert.Equal(32, n); return rfc; });
        Assert.Equal("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk", pk.Verifier);
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", pk.Challenge);
        Assert.Equal("S256", pk.Method);
        Assert.Equal(43, GcalCore.Pkce().Verifier.Length);
        string st = GcalCore.NewState();
        Assert.Equal(32, st.Length);
        Assert.True(GcalCore.StateMatches(st, st));
        Assert.False(GcalCore.StateMatches(st, st[..^1]));
        Assert.False(GcalCore.StateMatches(st, st[..^1] + (st[^1] == 'A' ? 'B' : 'A')));
        Assert.False(GcalCore.StateMatches("", ""));
    }

    [Fact]
    public void The_consent_URL_carries_ONLY_calendar_readonly_and_comes_back_only_to_127_0_0_1()
    {
        string url = GcalCore.AuthUrl("1-x.apps.googleusercontent.com", "http://127.0.0.1:53111/", "chal", "st");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.True(GcalSelfTest.OnlyScope(url));
        foreach (var part in new[] { "response_type=code", "code_challenge=chal", "code_challenge_method=S256", "state=st", "access_type=offline", "prompt=consent",
            "redirect_uri=http%3A%2F%2F127.0.0.1%3A53111%2F", "scope=https%3A%2F%2Fwww.googleapis.com%2Fauth%2Fcalendar.readonly" })
            Assert.Contains(part, url);
        Assert.DoesNotContain("email", url);
        var e = Assert.Throws<GcalError>(() => GcalCore.AuthUrl("1-x.apps.googleusercontent.com", "http://localhost:53111/", "c", "s"));
        Assert.Equal("the sign-in answer must come back to this PC (127.0.0.1)", e.Reason);
    }

    [Fact]
    public void The_client_file_is_a_Desktop_app_client_and_every_refusal_is_in_words()
    {
        var c = GcalCore.ParseClientFile("﻿{\"installed\":{\"client_id\":\" 1-abc.apps.googleusercontent.com \",\"client_secret\":\"s3\",\"project_id\":\"proj\"}}");
        Assert.Equal(("1-abc.apps.googleusercontent.com", "s3", "proj"), (c.ClientId, c.ClientSecret, c.Project));
        string Why(string t) => Assert.Throws<GcalError>(() => GcalCore.ParseClientFile(t)).Reason!;
        Assert.Equal("that file is not a Google client file (it is not JSON)", Why("nope"));
        Assert.Equal("that is a \"Web application\" client; Mantle needs a \"Desktop app\" client (Google Cloud > Clients > Create client > Desktop app)", Why("{\"web\":{}}"));
        Assert.Equal("that file is not a Google \"Desktop app\" client file (it has no \"installed\" part)", Why("{}"));
        Assert.Equal("the client file has no valid client ID", Why("{\"installed\":{\"client_id\":\"x\",\"client_secret\":\"s\"}}"));
        Assert.Equal("the client file has no client secret", Why("{\"installed\":{\"client_id\":\"1-a.apps.googleusercontent.com\"}}"));
    }

    [Fact]
    public void Events_drop_cancelled_and_my_declines_keep_others_and_read_all_day_and_timed_rows()
    {
        GcalEvent? N(string json) => GcalCore.NormalizeEvent(JsonNode.Parse(json), "cal@x", null, Utc);
        Assert.Null(N("{\"id\":\"a\",\"status\":\"cancelled\",\"start\":{\"date\":\"2026-11-11\"}}"));
        Assert.Null(N("{\"id\":\"a\",\"start\":{\"date\":\"2026-11-11\"},\"attendees\":[{\"self\":true,\"responseStatus\":\"declined\"}]}"));
        Assert.NotNull(N("{\"id\":\"a\",\"start\":{\"date\":\"2026-11-11\"},\"attendees\":[{\"self\":false,\"responseStatus\":\"declined\"}]}"));
        var ad = N("{\"id\":\"a\",\"summary\":\"  \",\"start\":{\"date\":\"2026-11-30\"},\"htmlLink\":\"https://www.google.com/calendar/event?eid=1\"}")!;
        Assert.Equal((true, "2026-11-30", "2026-12-01", "(No title)"), (ad.AllDay, ad.SDate, ad.EDate, ad.Title));
        Assert.Equal("5:cal@xa", ad.K);
        Assert.Equal("https://www.google.com/calendar/event?eid=1", ad.Link);
        var t = N("{\"id\":\"b\",\"summary\":\"" + new string('x', 250) + "\",\"start\":{\"dateTime\":\"2026-11-11T10:00:00-05:00\"},\"end\":{\"dateTime\":\"2026-11-11T09:00:00-05:00\"},\"htmlLink\":\"https://evil.example/x\"}")!;
        Assert.False(t.AllDay);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), t.SMs);
        Assert.Equal(t.SMs, t.EMs);                 // an end before the start is clamped
        Assert.Equal(200, t.Title.Length);
        Assert.Null(t.Link);                        // only Google Calendar's own pages
        // no offset: read in the event's zone (an IANA name), DST-correct
        long ny = GcalCore.ParseDateTime("2026-07-01T09:00:00", "America/New_York")!.Value;
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 13, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), ny);
    }

    [Fact]
    public void Calendars_primary_and_selected_by_default_the_users_choice_wins_and_the_account_is_the_primary_id()
    {
        var items = (JsonArray)JsonNode.Parse("[{\"id\":\"b@g\",\"summary\":\"Beta\",\"selected\":true,\"backgroundColor\":\"#ABCDEF\"},{\"id\":\"me@example.com\",\"summary\":\"Me\",\"primary\":true}," +
            "{\"id\":\"c@g\",\"summary\":\"Gamma\"},{\"id\":\"d@g\",\"summary\":\"Gone\",\"deleted\":true,\"selected\":true},{\"id\":\"a@g\",\"summary\":\"Alpha\",\"summaryOverride\":\"Aardvark\",\"backgroundColor\":\"red\"}]")!;
        var cals = GcalCore.PickCalendars(items, new System.Collections.Generic.Dictionary<string, bool> { ["c@g"] = true, ["b@g"] = false });
        Assert.Equal(new[] { "me@example.com", "a@g", "b@g", "c@g" }, cals.Select(c => c.Id));
        Assert.Equal(new[] { true, false, false, true }, cals.Select(c => c.On));
        Assert.Equal("Aardvark", cals[1].Name);
        Assert.Null(cals[1].Color);
        Assert.Equal("#abcdef", cals[2].Color);
        Assert.Equal("me@example.com", GcalCore.AccountOf(items));
    }

    [Fact]
    public void Errors_become_plain_words_with_their_kind()
    {
        GcalProblem C(int status, string? body = null, string? ra = null) => GcalCore.Classify(new GcalError(status, body == null ? null : JsonNode.Parse(body), ra), 0);
        Assert.Equal(("auth", "Google ended Mantle's sign-in; sign in again in Settings"), (C(400, "{\"error\":\"invalid_grant\"}").Kind, C(400, "{\"error\":\"invalid_grant\"}").Reason));
        Assert.Equal("denied", C(401, "{\"error\":\"invalid_client\"}").Kind);
        Assert.Equal("Google refused Mantle's sign-in; sign in again in Settings", C(401).Reason);
        var rate = C(429, null, "120");
        Assert.Equal(("rate", 120_000L), (rate.Kind, rate.RetryAfterMs));
        Assert.Equal("rate", C(403, "{\"error\":{\"errors\":[{\"reason\":\"userRateLimitExceeded\"}]}}").Kind);
        Assert.Equal("the Google Calendar API is not turned on in your Google Cloud project", C(403, "{\"error\":{\"errors\":[{\"reason\":\"accessNotConfigured\"}]}}").Reason);
        Assert.Equal("auth", C(403, "{\"error\":{\"errors\":[{\"reason\":\"insufficientPermissions\"}]}}").Kind);
        Assert.Equal(("server", "Google Calendar had a problem (error 503)"), (C(503).Kind, C(503).Reason));
        Assert.Equal("Google answered with error 404", C(404).Reason);
        Assert.Equal(("offline", "no internet connection"), (GcalCore.Classify(GcalError.Network("timed out", "ETIMEDOUT")).Kind, GcalCore.Classify(GcalError.Network("timed out", "ETIMEDOUT")).Reason));
        Assert.Equal(3_600_000, GcalCore.RetryAfter("99999", 0));
    }

    [Fact]
    public void Backoff_is_1_2_4_min_capped_at_30_with_jitter_and_a_longer_Retry_After_wins_and_ago_reads_like_Electron()
    {
        Assert.Equal(60_000, GcalCore.BackoffMs(1, 0, () => 0.5));
        Assert.Equal(240_000, GcalCore.BackoffMs(3, 0, () => 0.5));
        Assert.Equal(1_800_000, GcalCore.BackoffMs(9, 0, () => 0.5));
        Assert.Equal(48_000, GcalCore.BackoffMs(1, 0, () => 0));
        Assert.Equal(72_000, GcalCore.BackoffMs(1, 0, () => 1));
        Assert.Equal(500_000, GcalCore.BackoffMs(1, 500_000, () => 0.5));
        Assert.Equal("just now", GcalCore.Ago(0, 59_000));
        Assert.Equal("12 min ago", GcalCore.Ago(0, 12 * 60_000));
        Assert.Equal("3 h ago", GcalCore.Ago(0, 3 * 3_600_000));
        Assert.Equal("3 days ago", GcalCore.Ago(0, 72 * 3_600_000));
    }

    static long Ms(int y, int mo, int d, int h, int mi = 0) => new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    static JsonObject Payload() => O("{\"ver\":3,\"spans\":[{\"from\":" + Ms(2026, 10, 1, 0) + ",\"to\":" + Ms(2027, 1, 1, 0) + "}]," +
        "\"calendars\":[{\"id\":\"a\",\"color\":\"#039be5\",\"name\":\"Alex\"},{\"id\":\"b\",\"color\":null,\"name\":\"Club\"}]," +
        "\"events\":[" +
        "{\"k\":\"1\",\"cal\":\"a\",\"title\":\"Morning run\",\"allDay\":false,\"s\":" + Ms(2026, 11, 11, 6, 45) + ",\"e\":" + Ms(2026, 11, 11, 8) + "}," +
        "{\"k\":\"2\",\"cal\":\"b\",\"title\":\"Retreat\",\"allDay\":true,\"s\":\"2026-11-11\",\"e\":\"2026-11-14\"}," +
        "{\"k\":\"3\",\"cal\":\"a\",\"title\":\"Night train\",\"allDay\":false,\"s\":" + Ms(2026, 11, 14, 22, 30) + ",\"e\":" + Ms(2026, 11, 17, 6) + "}," +
        "{\"k\":\"4\",\"cal\":\"a\",\"title\":\"Ends at midnight\",\"allDay\":false,\"s\":" + Ms(2026, 11, 20, 23) + ",\"e\":" + Ms(2026, 11, 21, 0) + "}," +
        "{\"k\":\"5\",\"cal\":\"gone\",\"title\":\"Unticked\",\"allDay\":true,\"s\":\"2026-11-20\",\"e\":\"2026-11-21\"}]}");

    [Fact]
    public void The_day_index_puts_events_on_their_local_days_once_and_draws_one_dot_per_day()
    {
        var x = GcalDays.Index(Payload(), Utc);
        Assert.Equal(3, x.Ver);
        Assert.Equal(new[] { "2", "1" }, x.On("2026-11-11").Select(r => r.K));       // all-day first
        Assert.Equal(new[] { "2" }, x.On("2026-11-13").Select(r => r.K));            // end exclusive: not on the 14th
        Assert.Equal(new[] { "3" }, x.On("2026-11-14").Select(r => r.K));
        Assert.Equal(new[] { "4" }, x.On("2026-11-20").Select(r => r.K));            // the unticked calendar's event is left out
        Assert.Empty(x.On("2026-11-21"));                                             // ending exactly at midnight
        Assert.Equal(new GcalDays.Dot(null, true, 2), x.DotOf("2026-11-11"));          // two calendars: one neutral dot
        Assert.Equal(new GcalDays.Dot("#039be5", false, 1), x.DotOf("2026-11-16"));
        Assert.Null(x.DotOf("2026-11-25"));
        var train = x.On("2026-11-14")[0];
        Assert.Equal("10:30 PM", x.TimeOf(train, "2026-11-14"));
        Assert.Equal("All day", x.TimeOf(train, "2026-11-15"));
        Assert.Equal("Until 6:00 AM", x.TimeOf(train, "2026-11-17"));
        Assert.True(x.Covers(2026, 10));
        Assert.False(x.Covers(2027, 0));
        Assert.True(x.CoversDay("2026-12-31"));
        Assert.False(x.CoversDay("2027-01-01"));
    }

    [Fact]
    public void The_agenda_words_heading_range_and_status_line_read_like_Electrons()
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-US");
        try {
            var x = GcalDays.Index(Payload(), Utc);
            var today = new DateTime(2026, 11, 11);
            Assert.Equal("Today", GcalDays.DayTitle("2026-11-11", today));
            Assert.Equal("Tomorrow", GcalDays.DayTitle("2026-11-12", today));
            Assert.Equal("Yesterday", GcalDays.DayTitle("2026-11-10", today));
            Assert.Equal("Monday, November 16", GcalDays.DayTitle("2026-11-16", today));
            Assert.Equal("All day, Wed, Nov 11 to Fri, Nov 13", x.RangeOf(x.On("2026-11-13")[0]));
            Assert.Equal("6:45 AM to 8:00 AM", x.RangeOf(x.On("2026-11-11")[1]));
            Assert.Equal("Sat, Nov 14 10:30 PM to Tue, Nov 17 6:00 AM", x.RangeOf(x.On("2026-11-14")[0]));
            Assert.Equal("Wednesday, November 11, 2026", GcalDays.Words.Long(today));
            Assert.Equal("November 2026", GcalDays.Words.MonthYear(today));
            Assert.Equal("Su", GcalDays.Words.Dow(DayOfWeek.Sunday));
            long now = Ms(2026, 11, 11, 12);
            var shown = new DateTime(2026, 11, 1);
            Assert.Equal("", GcalDays.StatusLine(O("{\"signedIn\":true,\"syncedAt\":" + (now - 60_000) + "}"), null, null, true, shown, now));
            Assert.Equal("Syncing with Google…", GcalDays.StatusLine(O("{\"signedIn\":true,\"syncing\":true,\"syncedAt\":0}"), null, null, false, shown, now));
            Assert.Equal("Last synced 12 min ago · no internet connection",
                GcalDays.StatusLine(O("{\"syncedAt\":" + (now - 12 * 60_000) + ",\"error\":{\"kind\":\"offline\",\"reason\":\"no internet connection\"}}"), null, null, true, shown, now));
            Assert.Equal("Not synced yet · Google ended Mantle's sign-in; sign in again in Settings", GcalDays.StatusLine(O("{\"needsSignIn\":true,\"syncedAt\":0}"), null, null, true, shown, now));
            Assert.Equal("Loading November from Google…", GcalDays.StatusLine(O("{\"syncedAt\":" + now + "}"), null, "loading", false, shown, now));
            Assert.Equal("Last synced just now · November is not on this PC yet (test run: nothing is sent to Google)",
                GcalDays.StatusLine(O("{\"syncedAt\":" + now + "}"), null, "no:test run: nothing is sent to Google", false, shown, now));
            Assert.Equal("The event did not open: x", GcalDays.StatusLine(O("{}"), "The event did not open: x", null, true, shown, now));
        } finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void A_calendar_colour_is_data_and_the_title_mix_is_oklab()
    {
        Assert.Equal(new GcalColors.Rgba(3, 155, 229, 1), GcalColors.Parse("#039be5"));
        Assert.Null(GcalColors.Parse("red"));
        var a = new GcalColors.Rgba(3, 155, 229, 1); var ink = new GcalColors.Rgba(20, 20, 20, 1);
        Assert.Equal(a, GcalColors.MixOklab(a, ink, 1));
        Assert.Equal(ink, GcalColors.MixOklab(a, ink, 0));
        var m = GcalColors.MixOklab(a, ink, 0.6);
        Assert.InRange(m.B, ink.B + 1, a.B - 1);   // between the two, nearer the calendar's colour
    }
}
