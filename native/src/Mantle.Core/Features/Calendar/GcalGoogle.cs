using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Calendar;

/// Google's answer to a code exchange or a refresh.
public sealed record GcalToken(string Access, string? Refresh, long ExpiresAt, IReadOnlyList<string> Scopes, string? Account = null)
{
    public JsonObject ToJson()
    {
        var sc = new JsonArray();
        foreach (var s in Scopes) sc.Add((JsonNode?)JsonValue.Create(s));
        return new JsonObject { ["access"] = Access, ["refresh"] = Refresh, ["expiresAt"] = ExpiresAt, ["scopes"] = sc, ["account"] = Account };
    }

    public static GcalToken? Of(JsonObject? o)
    {
        if (o == null || GcalCore.Str(o["refresh"]) is not { Length: > 0 } refresh) return null;
        var sc = (o["scopes"] as JsonArray)?.Select(x => GcalCore.Str(x)).Where(x => x != null).Select(x => x!).ToArray() ?? new[] { GcalCore.Scope };
        return new GcalToken(GcalCore.Str(o["access"]) ?? "", refresh, GcalCore.Num(o["expiresAt"]) ?? 0, sc, GcalCore.Str(o["account"]));
    }
}

/// One page walk's result.
public sealed record GcalPage(JsonArray Items, string? TimeZone, bool Truncated);

/// Every request Mantle makes to Google (a test gives a fake that never reaches the network).
public interface IGcalGoogle
{
    Task<GcalToken> ExchangeAsync(GcalClient client, string code, string verifier, string redirectUri);
    Task<GcalToken> RefreshAsync(GcalClient client, string refreshToken);
    Task RevokeAsync(string token);
    Task<JsonArray> CalendarListAsync(string access);
    Task<GcalPage> EventsAsync(string access, string calId, long from, long to);
}

/// <summary>
/// The only requests (lib/features/_gcal-google.js), through ONE send function. Hosts (privacy claim 5):
/// oauth2.googleapis.com/token and /revoke, www.googleapis.com/calendar/v3; <see cref="Go"/> refuses any other host
/// (and anything not https) before a byte is sent. Redirects are never followed, so a redirect can never carry a request
/// or a token to a host the guard did not check. Each request has 20 s. The access token goes in the Authorization
/// header, never the URL; tokens are never put in an error or the log. Only GET (calendarList, events) goes to the
/// Calendar API; POST goes only to token and revoke (Mantle cannot create, change or delete events).
/// </summary>
public sealed class GcalGoogle : IGcalGoogle
{
    public const int TimeoutMs = 20000;
    public const int MaxPages = 20;   // 20 x 2500 events: far past any real month window

    static readonly Lazy<HttpClient> Shared = new(() => new HttpClient(new SocketsHttpHandler {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = true,
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan });

    readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    readonly int _timeoutMs;
    readonly Func<long> _now;

    /// <param name="send">null = the real network (a live run only); a test passes a fake.</param>
    public GcalGoogle(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null, int timeoutMs = TimeoutMs, Func<long>? now = null)
    {
        _send = send ?? ((m, c) => Shared.Value.SendAsync(m, HttpCompletionOption.ResponseContentRead, c));
        _timeoutMs = timeoutMs;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// The host guard: true when <paramref name="url"/> may be asked at all.
    public static bool Allowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" && GcalCore.Hosts.Contains(u.Host) && u.IsDefaultPort;

    public async Task<JsonObject> Go(HttpMethod method, string url, string? form = null, string? bearer = null)
    {
        if (!Allowed(url)) throw new GcalError("other", "Mantle only talks to Google");
        if (method != HttpMethod.Get && method != HttpMethod.Post) throw new GcalError("other", "Mantle only reads from Google");
        if (method == HttpMethod.Post && url != GcalCore.TokenUrl && url != GcalCore.RevokeUrl) throw new GcalError("other", "Mantle only reads from Google");
        using var cts = new CancellationTokenSource(_timeoutMs);
        using var req = new HttpRequestMessage(method, url);
        if (form != null) req.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage res;
        try { res = await _send(req, cts.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { throw GcalError.Network("timed out", "ETIMEDOUT"); }
        catch (HttpRequestException e) { throw GcalError.Network("fetch failed: " + GcalCore.Cut(e.Message, 60), "ENOTFOUND"); }
        using (res) {
            string text;
            try { text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(true); } catch (Exception) { text = ""; }
            JsonNode? body = null;
            try { body = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (Exception) { body = null; }
            if (!res.IsSuccessStatusCode) {
                string? ra = res.Headers.RetryAfter is { } r ? (r.Delta is { } d ? ((long)d.TotalSeconds).ToString(CultureInfo.InvariantCulture) : r.Date?.ToString("R", CultureInfo.InvariantCulture)) : null;
                throw new GcalError((int)res.StatusCode, body, ra);
            }
            return body as JsonObject ?? new JsonObject();
        }
    }

    /// The code from the loopback answer -> tokens; the PKCE verifier proves this PC started the sign-in.
    public async Task<GcalToken> ExchangeAsync(GcalClient client, string code, string verifier, string redirectUri)
    {
        var j = await Go(HttpMethod.Post, GcalCore.TokenUrl, GcalCore.Form(new[] {
            ("code", code), ("client_id", client.ClientId), ("client_secret", client.ClientSecret), ("redirect_uri", redirectUri),
            ("grant_type", "authorization_code"), ("code_verifier", verifier),
        })).ConfigureAwait(true);
        return TokenOf(j, null);
    }

    public async Task<GcalToken> RefreshAsync(GcalClient client, string refreshToken)
    {
        var j = await Go(HttpMethod.Post, GcalCore.TokenUrl, GcalCore.Form(new[] {
            ("client_id", client.ClientId), ("client_secret", client.ClientSecret), ("refresh_token", refreshToken), ("grant_type", "refresh_token"),
        })).ConfigureAwait(true);
        return TokenOf(j, refreshToken);
    }

    GcalToken TokenOf(JsonObject j, string? oldRefresh)
    {
        if (GcalCore.Str(j["access_token"]) is not { } access) throw new GcalError("other", "Google did not send a sign-in token");
        var scopes = GcalCore.Str(j["scope"]) is { } s ? s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : new[] { GcalCore.Scope };
        long expires = Math.Max(60, GcalCore.Num(j["expires_in"]) is long e && e > 0 ? e : 3600);
        string? refresh = GcalCore.Str(j["refresh_token"]) is { Length: > 0 } r ? r : oldRefresh;
        return new GcalToken(access, refresh, _now() + expires * 1000, scopes);
    }

    /// Revoking the refresh token ends the grant (and every access token from it) at Google.
    public async Task RevokeAsync(string token) =>
        await Go(HttpMethod.Post, GcalCore.RevokeUrl, GcalCore.Form(new[] { ("token", token) })).ConfigureAwait(true);

    async Task<GcalPage> Pages(string url, string access)
    {
        var items = new JsonArray();
        string? pageToken = null;
        for (int i = 0; i < MaxPages; i++) {
            string u = pageToken == null ? url : url + "&pageToken=" + Uri.EscapeDataString(pageToken);
            var j = await Go(HttpMethod.Get, u, bearer: access).ConfigureAwait(true);
            if (j["items"] is JsonArray a) foreach (var it in a.ToArray()) { a.Remove(it); items.Add(it); }
            pageToken = GcalCore.Str(j["nextPageToken"]) is { Length: > 0 } p ? p : null;
            if (pageToken == null) return new GcalPage(items, GcalCore.Str(j["timeZone"]), false);
        }
        return new GcalPage(items, null, true);
    }

    public async Task<JsonArray> CalendarListAsync(string access) =>
        (await Pages(GcalCore.Api + "/users/me/calendarList?minAccessRole=reader&maxResults=250&fields=" +
            Uri.EscapeDataString("nextPageToken,items(id,summary,summaryOverride,backgroundColor,primary,selected,deleted,timeZone)"), access).ConfigureAwait(true)).Items;

    public Task<GcalPage> EventsAsync(string access, string calId, long from, long to) =>
        Pages(GcalCore.Api + "/calendars/" + Uri.EscapeDataString(calId) + "/events?" + GcalCore.Form(new[] {
            ("singleEvents", "true"), ("orderBy", "startTime"),
            ("timeMin", Iso(from)), ("timeMax", Iso(to)),
            ("maxResults", "2500"), ("showDeleted", "false"),
            ("fields", "nextPageToken,timeZone,items(id,status,summary,start,end,htmlLink,attendees(self,responseStatus))"),
        }), access);

    /// JS toISOString: "2026-10-01T04:00:00.000Z".
    public static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
