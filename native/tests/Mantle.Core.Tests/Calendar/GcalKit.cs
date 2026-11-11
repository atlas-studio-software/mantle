using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Calendar;

namespace Mantle.Core.Tests.Calendar;

/// Made-up Google data for the calendar tests. Every "secret" below is a marker string the privacy tests look for in
/// files, request bodies and log lines; none of it is real and nothing here ever reaches the network.
static class GcalFake
{
    public const string Access = "ya29.FAKE-ACCESS-SECRET-1", Access2 = "ya29.FAKE-ACCESS-SECRET-2", Refresh = "1//FAKE-REFRESH-SECRET";
    public const string ClientSecret = "FAKE-CLIENT-SECRET-xyz", ClientId = "123-fake.apps.googleusercontent.com";
    public const string Title1 = "FAKE-TITLE-dentist", Title2 = "FAKE-TITLE-trip", Account = "me@example.com";
    public static readonly GcalClient Client = new(ClientId, ClientSecret, "fake-project");
    public static readonly long Now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public const string CalendarsJson = """
        [{"id":"me@example.com","summary":"me@example.com","primary":true,"backgroundColor":"#4285F4","timeZone":"UTC"},
         {"id":"work@group.calendar.google.com","summary":"Work","selected":true,"backgroundColor":"#0b8043","timeZone":"UTC"},
         {"id":"holidays","summary":"Holidays","backgroundColor":"#d50000"}]
        """;
    public const string PrimaryEventsJson = """
        [{"id":"e1","status":"confirmed","summary":"FAKE-TITLE-dentist","start":{"dateTime":"2026-11-11T15:00:00Z"},"end":{"dateTime":"2026-11-11T16:00:00Z"},"htmlLink":"https://www.google.com/calendar/event?eid=e1"},
         {"id":"gone","status":"cancelled","summary":"cancelled one","start":{"date":"2026-11-12"},"end":{"date":"2026-11-13"}}]
        """;
    public const string WorkEventsJson = """
        [{"id":"e2","summary":"FAKE-TITLE-trip","start":{"date":"2026-11-12"},"end":{"date":"2026-11-14"},"htmlLink":"https://www.google.com/calendar/event?eid=e2"}]
        """;

    public static GcalToken Token(long now, string access = Access) => new(access, Refresh, now + 3_600_000, new[] { GcalCore.Scope });
}

/// The Google seam as the engine sees it: answers from the made-up data, every call written down.
sealed class FakeGoogle : IGcalGoogle
{
    public readonly List<string> Calls = new();
    public long Now = GcalFake.Now;
    public string CalendarsJson = GcalFake.CalendarsJson;
    public readonly Dictionary<string, string> Events = new(StringComparer.Ordinal) {
        ["me@example.com"] = GcalFake.PrimaryEventsJson, ["work@group.calendar.google.com"] = GcalFake.WorkEventsJson,
    };
    public Exception? ListThrows, RevokeThrows, RefreshThrows;
    public readonly Dictionary<string, Exception> EventsThrow = new(StringComparer.Ordinal);
    public TaskCompletionSource? ListGate;   // set: calendarList waits for it (a sync still out)
    public readonly List<string> Revoked = new();
    public readonly List<string> AccessUsed = new();

    public Task<GcalToken> ExchangeAsync(GcalClient client, string code, string verifier, string redirectUri)
    { Calls.Add("exchange"); return Task.FromResult(GcalFake.Token(Now)); }

    public Task<GcalToken> RefreshAsync(GcalClient client, string refreshToken)
    {
        Calls.Add("refresh");
        if (RefreshThrows != null) return Task.FromException<GcalToken>(RefreshThrows);
        return Task.FromResult(new GcalToken(GcalFake.Access2, null, Now + 3_600_000, new[] { GcalCore.Scope }));
    }

    public Task RevokeAsync(string token)
    {
        Calls.Add("revoke"); Revoked.Add(token);
        return RevokeThrows != null ? Task.FromException(RevokeThrows) : Task.CompletedTask;
    }

    public async Task<JsonArray> CalendarListAsync(string access)
    {
        Calls.Add("list"); AccessUsed.Add(access);
        if (ListGate != null) await ListGate.Task.ConfigureAwait(false);
        if (ListThrows != null) throw ListThrows;
        return (JsonArray)JsonNode.Parse(CalendarsJson)!;
    }

    public Task<GcalPage> EventsAsync(string access, string calId, long from, long to)
    {
        Calls.Add("events " + calId); AccessUsed.Add(access);
        if (EventsThrow.TryGetValue(calId, out var e)) return Task.FromException<GcalPage>(e);
        var items = (JsonArray)JsonNode.Parse(Events.GetValueOrDefault(calId) ?? "[]")!;
        return Task.FromResult(new GcalPage(items, "UTC", false));
    }
}

/// A timer the test fires by hand.
sealed class KitTimer : IDisposable
{
    public KitTimer(Action a, long ms) { Action = a; Ms = ms; }
    public Action Action { get; }
    public long Ms { get; }
    public bool Off { get; private set; }
    public void Dispose() => Off = true;
}

/// The engine on a real <see cref="GcalStore"/> in a scratch folder (plain DPAPI: no Electron "Local State" there),
/// a fake Google, a hand-driven clock and timers. Never the real %APPDATA%\mantle.
sealed class GcalKit : IDisposable
{
    public readonly TestDir Dir = new("gcal");
    public readonly FakeGoogle G = new();
    public readonly List<string> Log = new();
    public readonly List<(JsonObject Status, JsonObject? Events)> Pubs = new();
    public readonly List<KitTimer> Timers = new();
    public readonly Dictionary<string, bool> Saved = new(StringComparer.Ordinal);
    public long Now = GcalFake.Now;
    public readonly GcalStore Store;
    public readonly GcalEngine Engine;

    public GcalKit(IGcalGoogle? google = null, Func<bool>? canEncrypt = null)
    {
        Store = new GcalStore(Path.Combine(Dir.Path, "gcal"), new SafeFile(WriteGate.Scratch(Dir.Path)), canEncrypt: canEncrypt);
        var g = google ?? G;
        Engine = new GcalEngine(new GcalEngineDeps {
            Store = Store, Google = g,
            SignIn = c => g.ExchangeAsync(c, "fake-code", "fake-verifier", "http://127.0.0.1:1/"),
            Choices = () => Saved, SaveChoices = d => { Saved.Clear(); foreach (var kv in d) Saved[kv.Key] = kv.Value; },
            Publish = (s, e) => Pubs.Add(((JsonObject)s.DeepClone(), e?.DeepClone() as JsonObject)),
            Now = () => Now,
            After = (a, ms) => { var t = new KitTimer(a, ms); Timers.Add(t); return t; },
            Log = Log.Add, Random = () => 0.5, Zone = TimeZoneInfo.Utc,
        });
    }

    public string F(string name) => Path.Combine(Store.Dir, name);
    public string[] Files() => Directory.Exists(Store.Dir) ? Directory.GetFiles(Store.Dir).Select(x => Path.GetFileName(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    public JsonObject Status => Engine.Status();
    public JsonObject LastEvents => Pubs.Last(p => p.Events != null).Events!;
    public KitTimer? Live => Timers.LastOrDefault(t => !t.Off);

    /// A client file chosen, the engine started, signed in and synced once.
    public async Task<JsonObject> SignedIn()
    {
        Store.PutClient(GcalFake.Client);
        Engine.Start();
        return await Engine.SignIn();
    }

    public void Dispose() => Dir.Dispose();
}

/// Records every request <see cref="GcalGoogle"/> makes and answers it like Google would (made-up data).
sealed class FakeWire
{
    public sealed record Sent(HttpMethod Method, string Url, string Body, string? Auth);
    public readonly List<Sent> Requests = new();
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override;

    public async Task<HttpResponseMessage> Send(HttpRequestMessage m, CancellationToken c)
    {
        string body = m.Content == null ? "" : await m.Content.ReadAsStringAsync(c).ConfigureAwait(false);
        Requests.Add(new Sent(m.Method, m.RequestUri!.ToString(), body, m.Headers.Authorization?.ToString()));
        if (Override?.Invoke(m) is { } o) return o;
        var u = m.RequestUri!;
        if (u.Host == "oauth2.googleapis.com" && u.AbsolutePath == "/token")
            return Json("{\"access_token\":\"" + GcalFake.Access2 + "\",\"expires_in\":3599,\"scope\":\"" + GcalCore.Scope + "\",\"token_type\":\"Bearer\"}");
        if (u.Host == "oauth2.googleapis.com" && u.AbsolutePath == "/revoke") return Json("{}");
        if (u.AbsolutePath.EndsWith("/users/me/calendarList", StringComparison.Ordinal)) return Json("{\"items\":" + GcalFake.CalendarsJson + "}");
        if (u.AbsolutePath.Contains("/calendars/me%40example.com/events", StringComparison.Ordinal) || u.AbsolutePath.Contains("/calendars/me@example.com/events", StringComparison.Ordinal))
            return Json("{\"timeZone\":\"UTC\",\"items\":" + GcalFake.PrimaryEventsJson + "}");
        if (u.AbsolutePath.Contains("/events", StringComparison.Ordinal)) return Json("{\"timeZone\":\"UTC\",\"items\":" + GcalFake.WorkEventsJson + "}");
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
