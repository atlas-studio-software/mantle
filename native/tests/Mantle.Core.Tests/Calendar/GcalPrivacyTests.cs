using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Calendar;
using Xunit;

namespace Mantle.Core.Tests.Calendar;

/// The six claims of Mantle's public privacy page for Google Calendar, one test each (named Claim1..Claim6), plus the
/// log rule. Made-up data, a scratch folder and a fake wire; nothing reaches Google and no real account is read.
public class GcalPrivacyTests
{
    static bool B(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static readonly string[] Secrets = { GcalFake.Access, GcalFake.Access2, GcalFake.Refresh, GcalFake.ClientSecret, GcalFake.Title1, GcalFake.Title2 };

    static string QueryParam(string url, string key)
    {
        var q = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2);
        return Uri.UnescapeDataString(q.Single(p => p[0] == key)[1].Replace('+', ' '));
    }

    [Fact]
    public void Claim1_only_the_calendar_readonly_scope_is_asked_for()
    {
        string url = GcalCore.AuthUrl(GcalFake.ClientId, "http://127.0.0.1:50123/", "challenge", "state");
        Assert.StartsWith(GcalCore.AuthUrlBase + "?", url);
        Assert.Equal("https://www.googleapis.com/auth/calendar.readonly", QueryParam(url, "scope"));   // one scope, nothing else
        Assert.Equal(GcalCore.Scope, QueryParam(url, "scope"));
        Assert.Equal(1, url.Split("scope=").Length - 1);
        Assert.DoesNotContain("include_granted_scopes", url);   // no scope from an earlier grant is folded in
        foreach (var other in new[] { "email", "profile", "openid", "calendar.events", "auth/calendar " })
            Assert.DoesNotContain(other, QueryParam(url, "scope"));
    }

    [Fact]
    public async Task Claim2_the_data_is_used_only_for_the_dots_and_the_day_list()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        // what the bar is given: per event the key, calendar, title, all-day flag and start/end; no link, no attendees, nothing else
        foreach (var p in k.Pubs.Where(p => p.Events != null))
            foreach (var e in (JsonArray)p.Events!["events"]!)
                Assert.Equal(new[] { "allDay", "cal", "e", "k", "s", "title" }, ((JsonObject)e!).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        // the only requests: the sign-in exchange, the calendar list and each ticked calendar's events (read-only)
        Assert.All(k.G.Calls, c => Assert.Matches("^(exchange|list|events .+|refresh|revoke)$", c));
        // and a ticked-off calendar's rows leave the bar at once
        k.Engine.SetCalendar("work@group.calendar.google.com", false);
        Assert.DoesNotContain(((JsonArray)k.LastEvents["events"]!), e => GcalCore.Str(e!["cal"]) == "work@group.calendar.google.com");
    }

    [Fact]
    public void Claim3_the_token_is_stored_only_on_this_PC_encrypted_with_DPAPI_for_the_current_user()
    {
        using var k = new GcalKit();
        k.Store.PutToken(GcalFake.Token(k.Now).ToJson());
        Assert.Equal(new[] { "token.bin" }, k.Files());
        Assert.StartsWith(k.Dir.Path, k.F("token.bin"));
        byte[] raw = File.ReadAllBytes(k.F("token.bin"));
        foreach (var enc in new Encoding[] { Encoding.UTF8, Encoding.Unicode })
            foreach (var s in new[] { GcalFake.Access, GcalFake.Refresh, "FAKE-ACCESS", "FAKE-REFRESH" })
                Assert.DoesNotContain(s, enc.GetString(raw));
        // DPAPI CurrentUser: this user's Windows account opens it
        Assert.Equal(GcalFake.Refresh, GcalCore.Str(JsonNode.Parse(Dpapi.Unprotect(raw))?["refresh"]));
        // no encryption = no token file at all, never plain text
        using var k2 = new GcalKit(canEncrypt: () => false);
        var e = Assert.Throws<GcalError>(() => k2.Store.PutToken(GcalFake.Token(k2.Now).ToJson()));
        Assert.Contains("DPAPI", e.Reason);
        Assert.Empty(k2.Files());
    }

    [Fact]
    public async Task Claim4_the_event_cache_is_one_file_on_this_PC_and_no_request_carries_an_event()
    {
        var wire = new FakeWire();
        using var k = new GcalKit(new GcalGoogle(wire.Send, now: () => GcalFake.Now));
        k.Store.PutClient(GcalFake.Client);
        k.Store.PutToken(new GcalToken("", GcalFake.Refresh, 0, new[] { GcalCore.Scope }).ToJson());   // expired: the sync refreshes over the wire
        k.Engine.Start();
        await k.Engine.SyncNow("test", force: true);
        Assert.Equal(2, ((JsonArray)k.LastEvents["events"]!).Count);
        // the events are on disk in exactly one place: <data>\gcal\cache.json
        var holders = Directory.GetFiles(k.Dir.Path, "*", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains(GcalFake.Title1, StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { k.F("cache.json") }, holders);
        // and they never travel: no URL, body or header of any request names an event or a calendar's events
        Assert.NotEmpty(wire.Requests);
        foreach (var r in wire.Requests)
            foreach (var t in new[] { GcalFake.Title1, GcalFake.Title2, "FAKE-TITLE" })
                Assert.DoesNotContain(t, r.Url + "\n" + r.Body + "\n" + r.Auth);
    }

    [Fact]
    public async Task Claim5_it_talks_only_to_Google_reads_only_and_never_follows_a_redirect()
    {
        // every request of a whole sign-in-to-sign-out run went to Google's three hosts over https
        var wire = new FakeWire();
        using (var k = new GcalKit(new GcalGoogle(wire.Send, now: () => GcalFake.Now))) {
            k.Store.PutClient(GcalFake.Client);
            k.Store.PutToken(new GcalToken("", GcalFake.Refresh, 0, new[] { GcalCore.Scope }).ToJson());
            k.Engine.Start();
            await k.Engine.SyncNow("test", force: true);
            await k.Engine.SignOut();
        }
        Assert.NotEmpty(wire.Requests);
        foreach (var r in wire.Requests) {
            var u = new Uri(r.Url);
            Assert.Equal("https", u.Scheme);
            Assert.Contains(u.Host, new[] { "oauth2.googleapis.com", "www.googleapis.com" });
            if (r.Method == HttpMethod.Post) Assert.Contains(r.Url, new[] { GcalCore.TokenUrl, GcalCore.RevokeUrl });
            else { Assert.Equal(HttpMethod.Get, r.Method); Assert.StartsWith(GcalCore.Api + "/", r.Url); }
        }

        // the guard refuses anything else before a byte is sent
        var none = new FakeWire();
        var g = new GcalGoogle(none.Send);
        foreach (var url in new[] { "https://evil.example/calendar/v3/x", "http://www.googleapis.com/calendar/v3/x", "https://www.googleapis.com:8443/calendar/v3/x",
                                    "https://www.googleapis.com.evil.example/x", "https://analytics.google.com/collect", "file:///C:/x" }) {
            Assert.False(GcalGoogle.Allowed(url), url);
            await Assert.ThrowsAsync<GcalError>(() => g.Go(HttpMethod.Get, url));
        }
        await Assert.ThrowsAsync<GcalError>(() => g.Go(HttpMethod.Post, GcalCore.Api + "/calendars/primary/events"));   // no writing to the calendar
        await Assert.ThrowsAsync<GcalError>(() => g.Go(HttpMethod.Delete, GcalCore.Api + "/calendars/primary/events/e1"));
        await Assert.ThrowsAsync<GcalError>(() => g.Go(HttpMethod.Put, GcalCore.Api + "/calendars/primary"));
        Assert.Empty(none.Requests);

        // a redirect is an error, never a second request (the real handler has AllowAutoRedirect = false too)
        var redir = new FakeWire { Override = m => {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/steal");
            return r;
        } };
        var e = await Assert.ThrowsAsync<GcalError>(() => new GcalGoogle(redir.Send).CalendarListAsync(GcalFake.Access));
        Assert.Equal(302, e.Status);
        Assert.Single(redir.Requests);
        Assert.Equal("www.googleapis.com", new Uri(redir.Requests[0].Url).Host);
        Assert.DoesNotContain(GcalFake.Access, redir.Requests[0].Url);   // the token rides in the header, never the URL
    }

    [Fact]
    public async Task Claim6_sign_out_deletes_the_token_and_the_cache_and_every_tmp_and_revokes_at_Google()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        Assert.True(File.Exists(k.F("token.bin")));
        Assert.True(File.Exists(k.F("cache.json")));
        File.WriteAllText(k.F("token.bin.9999.tmp"), "a crash left this");
        File.WriteAllText(k.F("cache.json.9999.tmp"), "a crash left this");
        var o = await k.Engine.SignOut();
        Assert.True(B(o, "ok"), o.ToJsonString());
        Assert.True(B(o, "revoked"));
        Assert.Equal(new[] { GcalFake.Refresh }, k.G.Revoked);   // the refresh token: it ends the whole grant
        Assert.Equal(new[] { "client.bin" }, k.Files());   // the user's own app registration stays; nothing of theirs does
        Assert.Equal(0, ((JsonArray)k.LastEvents["events"]!).Count);
        Assert.False(B(k.Status, "signedIn"));
        Assert.Null(k.Status["account"]);
        Assert.Null(k.Live);   // no sync is planned

        // Google out of reach: still signed out here, and the answer says so and where to finish it
        using var k2 = new GcalKit();
        await k2.SignedIn();
        k2.G.RevokeThrows = GcalError.Network("fetch failed", "ENOTFOUND");
        var o2 = await k2.Engine.SignOut();
        Assert.True(B(o2, "ok"));
        Assert.False(B(o2, "revoked"));
        Assert.Contains("myaccount.google.com/permissions", GcalCore.Str(o2["reason"]));
        Assert.Equal(new[] { "client.bin" }, k2.Files());
    }

    [Fact]
    public async Task No_log_line_carries_an_event_title_a_token_or_the_client_secret()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        k.G.EventsThrow["work@group.calendar.google.com"] = new GcalError(403, JsonNode.Parse("{\"error\":{\"message\":\"" + GcalFake.Title2 + " " + GcalFake.Access + "\"}}"), null);
        await k.Engine.SyncNow("test", force: true);
        k.G.EventsThrow.Clear();
        k.G.ListThrows = new GcalError(401, JsonNode.Parse("{\"error\":\"" + GcalFake.Access + "\"}"), null);
        await k.Engine.SyncNow("test", force: true);
        k.G.RevokeThrows = new GcalError(500, JsonNode.Parse("{\"error\":\"" + GcalFake.Refresh + "\"}"), null);
        await k.Engine.SignOut();

        // and the real wire's own failure words (a dead network) on a sync that carries a live token
        var wire = new FakeWire { Override = m => throw new HttpRequestException("No such host is known. (www.googleapis.com:443)") };
        using var k2 = new GcalKit(new GcalGoogle(wire.Send, now: () => GcalFake.Now));
        k2.Store.PutClient(GcalFake.Client);
        k2.Store.PutToken(GcalFake.Token(GcalFake.Now).ToJson());
        k2.Engine.Start();
        await k2.Engine.SyncNow("test", force: true);

        var lines = k.Log.Concat(k2.Log).ToArray();
        Assert.NotEmpty(lines);
        foreach (var l in lines)
            foreach (var s in Secrets.Append("FAKE-ACCESS").Append("FAKE-REFRESH").Append("FAKE-CLIENT").Append("FAKE-TITLE"))
                Assert.False(l.Contains(s, StringComparison.Ordinal), "a log line carries " + s + ": " + l);
    }
}
