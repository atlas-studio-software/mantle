using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// The pure half of Google Calendar in the clock's calendar (lib/features/_gcal-core.js): PKCE, the consent URL, the
/// client file, the sync window, Google's events turned into the small rows the bar draws, plain-words errors and the
/// backoff. No network, no disk.
///
/// PRIVACY (the public privacy page; every claim binds this code):
///   - the ONLY scope is calendar.readonly (<see cref="Scope"/>; the consent URL carries nothing else);
///   - the only hosts are Google's (<see cref="Hosts"/>): accounts.google.com (the consent page, opened in the user's own
///     browser), oauth2.googleapis.com (token + revoke) and www.googleapis.com/calendar/v3 (the Calendar API).
/// </summary>
public static class GcalCore
{
    public const string Scope = "https://www.googleapis.com/auth/calendar.readonly";
    public const string AuthUrlBase = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    public const string RevokeUrl = "https://oauth2.googleapis.com/revoke";
    public const string Api = "https://www.googleapis.com/calendar/v3";
    public static readonly IReadOnlyList<string> Hosts = new[] { "accounts.google.com", "oauth2.googleapis.com", "www.googleapis.com" };

    public static string B64Url(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// RFC 7636: a 43-character verifier from 32 random bytes, the S256 challenge.
    public static (string Verifier, string Challenge, string Method) Pkce(Func<int, byte[]>? randomBytes = null)
    {
        var rb = randomBytes ?? RandomNumberGenerator.GetBytes;
        string verifier = B64Url(rb(32));
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge, "S256");
    }

    public static string NewState(Func<int, byte[]>? randomBytes = null) => B64Url((randomBytes ?? RandomNumberGenerator.GetBytes)(24));

    /// Constant-time, exact-length: the loopback answer must carry the exact state that was sent.
    public static bool StateMatches(string? sent, string? got)
    {
        if (string.IsNullOrEmpty(sent) || got == null || sent.Length != got.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), Encoding.UTF8.GetBytes(got));
    }

    static readonly Regex Loopback = new(@"^http://127\.0\.0\.1:\d{1,5}/$", RegexOptions.CultureInvariant);

    /// The consent page URL: ONLY calendar.readonly; access_type=offline + prompt=consent so Google sends a refresh token every time.
    public static string AuthUrl(string clientId, string redirectUri, string challenge, string state)
    {
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(redirectUri) || string.IsNullOrEmpty(challenge) || string.IsNullOrEmpty(state))
            throw new GcalError("other", "the sign-in link is missing a part");
        if (!Loopback.IsMatch(redirectUri)) throw new GcalError("other", "the sign-in answer must come back to this PC (127.0.0.1)");
        return AuthUrlBase + "?" + Form(new[] {
            ("client_id", clientId), ("redirect_uri", redirectUri), ("response_type", "code"), ("scope", Scope),
            ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", state),
            ("access_type", "offline"), ("prompt", "consent"),
        });
    }

    /// application/x-www-form-urlencoded, as URLSearchParams writes it (space = '+').
    public static string Form(IEnumerable<(string K, string V)> pairs) =>
        string.Join("&", pairs.Select(p => Enc(p.K) + "=" + Enc(p.V)));
    static string Enc(string s) => Uri.EscapeDataString(s).Replace("%20", "+");

    static readonly Regex ClientId = new(@"^[\w.-]+\.apps\.googleusercontent\.com$", RegexOptions.CultureInvariant);

    /// <summary>The JSON Google Cloud hands out for a "Desktop app" client ({installed:{client_id, client_secret, project_id}}).
    /// A "Web application" client cannot answer on a random loopback port, so it is refused in words. Nothing from the
    /// file is ever logged.</summary>
    public static GcalClient ParseClientFile(string? text)
    {
        JsonNode? j;
        try { j = JsonNode.Parse((text ?? "").TrimStart('﻿')); }
        catch (Exception) { throw new GcalError("other", "that file is not a Google client file (it is not JSON)"); }
        if (j is not JsonObject o) throw new GcalError("other", "that file is not a Google client file");
        if (o["web"] != null && o["installed"] == null)
            throw new GcalError("other", "that is a \"Web application\" client; Mantle needs a \"Desktop app\" client (Google Cloud > Clients > Create client > Desktop app)");
        if (o["installed"] is not JsonObject c) throw new GcalError("other", "that file is not a Google \"Desktop app\" client file (it has no \"installed\" part)");
        string id = Str(c["client_id"])?.Trim() ?? "", secret = Str(c["client_secret"])?.Trim() ?? "";
        if (!ClientId.IsMatch(id)) throw new GcalError("other", "the client file has no valid client ID");
        if (secret.Length == 0) throw new GcalError("other", "the client file has no client secret");
        string? project = Str(c["project_id"]);
        return new GcalClient(id, secret, project == null ? null : project.Length > 80 ? project[..80] : project);
    }

    // ------------------------------------------------------------------ the sync window

    /// The month shown +-1 month: [1st of month-1, 1st of month+2), local time. <paramref name="month"/> is 0-11.
    public static GcalWindow MonthWindow(int year, int month, TimeZoneInfo? tz = null)
    {
        var z = tz ?? TimeZoneInfo.Local;
        var first = new DateTime(year, 1, 1).AddMonths(month);
        return new GcalWindow(LocalMs(first.AddMonths(-1), z), LocalMs(first.AddMonths(2), z), $"{first.Year}-{first.Month:00}");
    }

    public static GcalWindow WindowAround(long nowMs, TimeZoneInfo? tz = null)
    {
        var z = tz ?? TimeZoneInfo.Local;
        var d = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(nowMs), z);
        return MonthWindow(d.Year, d.Month - 1, z);
    }

    /// Epoch ms of a local wall time (a time Windows skips at a DST change is read an hour on, as a JS Date does).
    public static long LocalMs(DateTime wall, TimeZoneInfo? tz = null)
    {
        var z = tz ?? TimeZoneInfo.Local;
        var u = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (z.IsInvalidTime(u)) u = u.AddHours(1);
        return new DateTimeOffset(u, z.GetUtcOffset(u)).ToUnixTimeMilliseconds();
    }

    // ------------------------------------------------------------------ calendars and events

    static readonly Regex Hex = new("^#[0-9a-f]{6}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// Which calendars are read: the user's own choice per calendar wins; with none, the primary calendar plus those
    /// Google marks selected. Deleted ones are dropped; primary first, then by name.
    public static List<GcalCalendar> PickCalendars(JsonArray? items, IReadOnlyDictionary<string, bool>? choices)
    {
        var outp = new List<GcalCalendar>();
        foreach (var n in items ?? new JsonArray()) {
            if (n is not JsonObject c || Str(c["id"]) is not { } id || Bool(c["deleted"])) continue;
            bool auto = Bool(c["primary"]) || Bool(c["selected"]);
            bool on = choices != null && choices.TryGetValue(id, out var ch) ? ch : auto;
            string name = Str(c["summaryOverride"]) is { Length: > 0 } so ? so : Str(c["summary"]) is { Length: > 0 } s ? s : id;
            string? bg = Str(c["backgroundColor"]);
            outp.Add(new GcalCalendar(id, name.Length > 120 ? name[..120] : name, bg != null && Hex.IsMatch(bg) ? bg.ToLowerInvariant() : null, Bool(c["primary"]), on));
        }
        outp.Sort((a, b) => a.Primary != b.Primary ? (a.Primary ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        return outp;
    }

    /// The primary calendar's id IS the account's address: "Signed in as" with calendar.readonly alone (no email scope).
    public static string? AccountOf(JsonArray? items)
    {
        foreach (var n in items ?? new JsonArray())
            if (n is JsonObject c && Bool(c["primary"])) return Str(c["id"]) is { } id && id.IndexOf('@') > 0 ? id : null;
        return null;
    }

    static readonly Regex Date = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    static bool Declined(JsonObject ev) =>
        ev["attendees"] is JsonArray a && a.Any(x => x is JsonObject o && Bool(o["self"]) && Str(o["responseStatus"]) == "declined");

    /// <summary>One Google event -> the row the bar draws, or null to leave it out. All-day: 'YYYY-MM-DD' dates, end
    /// exclusive, no time zone. Timed: epoch ms instants. The title is trimmed (200), "(No title)" when blank.</summary>
    public static GcalEvent? NormalizeEvent(JsonNode? node, string calId, Func<string, string?>? tzOf, TimeZoneInfo? local = null)
    {
        if (node is not JsonObject ev || Str(ev["id"]) is not { } id) return null;
        if (Str(ev["status"]) == "cancelled" || Declined(ev)) return null;
        var st = ev["start"] as JsonObject ?? new JsonObject();
        var en = ev["end"] as JsonObject ?? new JsonObject();
        string title = Str(ev["summary"]) is { } t && t.Trim().Length > 0 ? t.Trim() : "(No title)";
        if (title.Length > 200) title = title[..200];
        string k = calId.Length.ToString(CultureInfo.InvariantCulture) + ":" + calId + id;
        string? link = SafeLink(Str(ev["htmlLink"]));
        if (Str(st["date"]) is { } sd && Date.IsMatch(sd)) {
            string? ed = Str(en["date"]);
            string e = ed != null && Date.IsMatch(ed) && string.CompareOrdinal(ed, sd) > 0 ? ed : NextDate(sd);
            return new GcalEvent(k, calId, title, true, sd, e, 0, 0, link);
        }
        if (Str(st["dateTime"]) is { } sdt) {
            string? stz = Str(st["timeZone"]);
            long? s = ParseDateTime(sdt, stz ?? tzOf?.Invoke(calId), local);
            if (s == null) return null;
            long? e = Str(en["dateTime"]) is { } edt ? ParseDateTime(edt, Str(en["timeZone"]) ?? stz ?? tzOf?.Invoke(calId), local) : s;
            if (e == null || e < s) e = s;
            return new GcalEvent(k, calId, title, false, null, null, s.Value, e.Value, link);
        }
        return null;
    }

    public static string NextDate(string ymd)
    {
        var d = DateTime.ParseExact(ymd, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return d.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    static readonly Regex HasOffset = new(@"[zZ]$|[+-]\d{2}:\d{2}$", RegexOptions.CultureInvariant);
    static readonly Regex Wall = new(@"^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?", RegexOptions.CultureInvariant);

    /// RFC 3339 with an offset is an absolute instant; without one the wall time is read in <paramref name="tz"/>
    /// (an IANA name), else the PC's zone. Null when it cannot be read.
    public static long? ParseDateTime(string s, string? tz, TimeZoneInfo? local = null)
    {
        if (HasOffset.IsMatch(s))
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : null;
        var m = Wall.Match(s);
        if (!m.Success) return null;
        int I(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        DateTime wall;
        try { wall = new DateTime(I(1), I(2), I(3), I(4), I(5), I(6)); } catch (ArgumentException) { return null; }
        if (string.IsNullOrEmpty(tz)) return LocalMs(wall, local);
        long w = new DateTimeOffset(wall, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return w - ZoneOffsetMs(tz, w);
    }

    /// The offset of zone <paramref name="tz"/> at about <paramref name="utcMs"/>; two passes settle DST edges; 0 when the zone is unknown.
    public static long ZoneOffsetMs(string tz, long utcMs)
    {
        try {
            var z = TimeZoneInfo.FindSystemTimeZoneById(tz);
            long At(long ms) => (long)z.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(ms)).TotalMilliseconds;
            long o1 = At(utcMs);
            return At(utcMs - o1);
        } catch (Exception) { return 0; }
    }

    /// Only Google Calendar's own event pages (https www.google.com / calendar.google.com, up to 2000 chars) are kept.
    public static string? SafeLink(string? u)
    {
        if (u == null || u.Length > 2000) return null;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var x) || x.Scheme != "https") return null;
        if (x.Host != "www.google.com" && x.Host != "calendar.google.com") return null;
        return x.AbsoluteUri;
    }

    /// The agenda's order: all-day first, then by start, then by title.
    public static int Compare(GcalEvent a, GcalEvent b)
    {
        if (a.AllDay != b.AllDay) return a.AllDay ? -1 : 1;
        int c = a.AllDay ? string.CompareOrdinal(a.SDate, b.SDate) : a.SMs.CompareTo(b.SMs);
        return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.CurrentCulture);
    }

    // ------------------------------------------------------------------ errors in plain words

    static readonly Regex OfflineCode = new("ENOTFOUND|EAI_AGAIN|ECONNREFUSED|ECONNRESET|ENETUNREACH|EHOSTUNREACH|ETIMEDOUT|UND_ERR|ABORT", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex OfflineMsg = new("fetch failed|network|timed out|aborted", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An HTTP answer or a failed request -> a problem in plain words. kind: offline | auth (sign in again) |
    /// denied (a setup problem) | rate (slow down) | server | other | partial | stale.</summary>
    public static GcalProblem Classify(Exception? err, long nowMs = 0)
    {
        if (err == null) return new GcalProblem("other", "something went wrong and no reason came back");
        if (err is not GcalError g) return new GcalProblem("other", "Google could not be reached (" + Cut(err.Message, 80) + ")");
        if (g.Kind != null) return new GcalProblem(g.Kind, g.Reason ?? g.Message, g.RetryAfterMs, g.LogReason);
        int status = g.Status;
        var bo = g.Body as JsonObject;   // Google sends {error: "invalid_grant"} for OAuth and {error: {errors: [...]}} for the API
        var reasons = ((bo?["error"] as JsonObject)?["errors"] as JsonArray)?.Select(x => Str((x as JsonObject)?["reason"])).Where(x => x != null).Select(x => x!).ToList() ?? new List<string>();
        string gerr = Str(bo?["error"]) ?? "";
        long ra = RetryAfter(g.RetryAfter, nowMs);
        if (status == 0) {
            if (OfflineCode.IsMatch(g.Code ?? "") || OfflineMsg.IsMatch(g.Message)) return new GcalProblem("offline", "no internet connection");
            return new GcalProblem("other", "Google could not be reached (" + Cut(g.Message.Length > 0 ? g.Message : "no reason", 80) + ")");
        }
        if (gerr == "invalid_grant") return new GcalProblem("auth", "Google ended Mantle's sign-in; sign in again in Settings");
        if (gerr is "invalid_client" or "unauthorized_client") return new GcalProblem("denied", "Google did not accept the client file; choose it again in Settings");
        if (status == 401) return new GcalProblem("auth", "Google refused Mantle's sign-in; sign in again in Settings");
        if (status == 429 || reasons.Any(r => Regex.IsMatch(r, "rateLimitExceeded|userRateLimitExceeded|quotaExceeded")))
            return new GcalProblem("rate", "Google asked Mantle to slow down; it will try again later", ra);
        if (status == 403 && reasons.Any(r => Regex.IsMatch(r, "accessNotConfigured|SERVICE_DISABLED", RegexOptions.IgnoreCase)))
            return new GcalProblem("denied", "the Google Calendar API is not turned on in your Google Cloud project");
        if (status == 403 && reasons.Any(r => Regex.IsMatch(r, "insufficientPermissions|ACCESS_TOKEN_SCOPE_INSUFFICIENT", RegexOptions.IgnoreCase)))
            return new GcalProblem("auth", "Mantle does not have read access to your calendar; sign in again and tick the calendar box");
        if (status == 403) return new GcalProblem("denied", "Google refused the request (error 403)");
        if (status >= 500) return new GcalProblem("server", "Google Calendar had a problem (error " + status.ToString(CultureInfo.InvariantCulture) + ")", ra);
        return new GcalProblem("other", "Google answered with error " + status.ToString(CultureInfo.InvariantCulture));
    }

    /// Retry-After in seconds or as an HTTP date, capped at 1 h, as ms.
    public static long RetryAfter(string? v, long nowMs)
    {
        if (string.IsNullOrEmpty(v)) return 0;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return (long)(Math.Max(0, Math.Min(n, 3600)) * 1000);
        if (DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return Math.Max(0, Math.Min(t.ToUnixTimeMilliseconds() - nowMs, 3600000));
        return 0;
    }

    public const long BackoffBaseMs = 60_000, BackoffMaxMs = 30 * 60_000;

    /// The wait before try n (1-based) after a failure: 1, 2, 4, 8 ... min, capped at 30, +-20% jitter; a longer Retry-After wins.
    public static long BackoffMs(int n, long retryAfterMs, Func<double>? rand = null)
    {
        double r = rand?.Invoke() ?? Random.Shared.NextDouble();
        double exp = Math.Min(BackoffMaxMs, BackoffBaseMs * Math.Pow(2, Math.Max(0, n - 1)));
        long jit = (long)Math.Round(exp * (0.8 + 0.4 * r), MidpointRounding.AwayFromZero);
        return Math.Max(jit, retryAfterMs);
    }

    /// "12 min ago" for the status line.
    public static string Ago(long ms, long nowMs)
    {
        long s = (long)Math.Max(0, Math.Round((nowMs - ms) / 1000.0, MidpointRounding.AwayFromZero));
        if (s < 60) return "just now";
        long m = (long)Math.Round(s / 60.0, MidpointRounding.AwayFromZero);
        if (m < 60) return m.ToString(CultureInfo.InvariantCulture) + " min ago";
        long h = (long)Math.Round(m / 60.0, MidpointRounding.AwayFromZero);
        if (h < 48) return h.ToString(CultureInfo.InvariantCulture) + " h ago";
        return Math.Round(h / 24.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + " days ago";
    }

    // ------------------------------------------------------------------ JSON helpers

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static long? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return (long)d;
        return null;
    }
    public static string Cut(string? s, int n) => s == null ? "" : s.Length > n ? s[..n] : s;
}

/// The chosen Desktop-app client (kept encrypted as client.bin).
public sealed record GcalClient(string ClientId, string ClientSecret, string? Project);

/// A sync window [From, To) in epoch ms, and its month key.
public sealed record GcalWindow(long From, long To, string Key);

public sealed record GcalCalendar(string Id, string Name, string? Color, bool Primary, bool On);

/// One row: all-day events carry SDate/EDate ('YYYY-MM-DD', end exclusive), timed ones SMs/EMs (epoch ms).
/// Link is kept for gcal.open only and never sent to the bar.
public sealed record GcalEvent(string K, string Cal, string Title, bool AllDay, string? SDate, string? EDate, long SMs, long EMs, string? Link);

/// A failure in plain words. LogReason, when set, is what the log gets instead (no calendar names).
public sealed record GcalProblem(string Kind, string Reason, long RetryAfterMs = 0, string? LogReason = null);

/// <summary>
/// Every failure the calendar code throws: either already in words (Kind + Reason) or a raw HTTP / network failure
/// (Status, Body, RetryAfter, Code) that <see cref="GcalCore.Classify"/> turns into words. Never carries a token.
/// </summary>
public sealed class GcalError : Exception
{
    public GcalError(string kind, string reason, string? logReason = null) : base(reason) { Kind = kind; Reason = reason; LogReason = logReason; }
    public GcalError(int status, JsonNode? body, string? retryAfter) : base("HTTP " + status.ToString(CultureInfo.InvariantCulture)) { Status = status; Body = body; RetryAfter = retryAfter; }
    GcalError(string message, string? code, bool network) : base(message) { Code = code; }
    /// A request that never got an answer (timed out, no route, refused): Code is Node's (ETIMEDOUT, ENOTFOUND ...).
    public static GcalError Network(string message, string? code) => new(message, code, true);
    public string? Kind { get; }
    public string? Reason { get; }
    public string? LogReason { get; }
    public long RetryAfterMs { get; init; }
    public int Status { get; }
    public JsonNode? Body { get; }
    public string? RetryAfter { get; }
    public string? Code { get; }
}
