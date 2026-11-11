using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Calendar;

/// <summary>What the engine is given (a test gives fakes: no network, no token, no real timers).</summary>
public sealed class GcalEngineDeps
{
    public required IGcalStore Store { get; init; }
    public required IGcalGoogle Google { get; init; }
    /// The browser sign-in (GcalAuth.SignInAsync in a live run).
    public required Func<GcalClient, Task<GcalToken>> SignIn { get; init; }
    public required Func<IReadOnlyDictionary<string, bool>> Choices { get; init; }
    public required Action<Dictionary<string, bool>> SaveChoices { get; init; }
    /// (status, events or null): the feature broadcasts both.
    public required Action<JsonObject, JsonObject?> Publish { get; init; }
    public Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// Run the action after ms on the engine's thread; disposing cancels it.
    public required Func<Action, long, IDisposable> After { get; init; }
    /// A LOCAL check (no request): is there a network at all?
    public Func<bool>? IsOnline { get; init; }
    public Action<string> Log { get; init; } = _ => { };
    public Func<double>? Random { get; init; }
    public TimeZoneInfo? Zone { get; init; }
}

/// <summary>
/// The Google Calendar engine (lib/features/_gcal-sync.js): sign-in state, the background sync, the cache, backoff.
/// WHEN IT SYNCS (never on the flyout's open path; the bar draws from what was last published):
///   - 4 s after start when signed in, then every 10 minutes;
///   - on resume from sleep, and when the network comes back after an offline failure (a local check every 30 s);
///   - after the flyout opened, when the last good sync is older than 2 minutes (<see cref="Opened"/>);
///   - when the flyout shows a month outside the cache (<see cref="Want"/>): that month +-1 is fetched and merged.
/// FAILURES: 1, 2, 4 ... 30 min backoff with jitter; Retry-After obeyed; during a rate limit nothing asks early.
/// Single-threaded: every member runs on the feature's (host) thread.
/// </summary>
public sealed class GcalEngine
{
    public const long PeriodMs = 10 * 60_000, StaleMs = 2 * 60_000, FirstMs = 4000, ResumeMs = 8000, OnlinePollMs = 30_000;
    public const int MaxEvents = 5000;

    sealed class State
    {
        public GcalClient? Client; public string? ClientBroken;
        public GcalToken? Token; public string? Account;
        public List<GcalCalendar> Calendars = new();
        public List<GcalEvent> Events = new();
        public List<(long From, long To)> Spans = new();
        public long SyncedAt; public bool Syncing;
        public GcalProblem? Error; public long ErrorAt;
        public bool NeedsSignIn; public int Failures; public long NextAt; public bool SigningIn; public bool Stopped = true;
    }

    readonly GcalEngineDeps _d;
    readonly State S = new();
    readonly TimeZoneInfo _tz;
    IDisposable? _timer, _onlineTimer;
    Task<JsonObject>? _inflight;
    (string Why, GcalWindow? W)? _again;
    int _eventsVer;
    /// Bumped by Sign out and by a new sign-in: a sync begun under an older one is dropped when it answers; it makes no
    /// more requests and writes nothing to memory, the bar or disk (privacy claim 6).
    int _gen;

    public GcalEngine(GcalEngineDeps d) { _d = d; _tz = d.Zone ?? TimeZoneInfo.Local; }

    void Live(int g) { if (g != _gen) throw new GcalError("stale", "signed out while syncing"); }

    // ------------------------------------------------------------------ what the pages see

    public JsonObject Status()
    {
        var cals = new JsonArray();
        foreach (var c in S.Calendars) cals.Add((JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["color"] = c.Color, ["primary"] = c.Primary, ["on"] = c.On });
        return new JsonObject {
            ["enabled"] = true, ["client"] = S.Client != null, ["clientReason"] = S.ClientBroken,
            ["connected"] = S.Token != null && !S.NeedsSignIn, ["signedIn"] = S.Token != null, ["needsSignIn"] = S.NeedsSignIn,
            ["signingIn"] = S.SigningIn, ["account"] = S.Account, ["calendars"] = cals, ["syncedAt"] = S.SyncedAt, ["syncing"] = S.Syncing,
            ["error"] = S.Error == null ? null : new JsonObject { ["kind"] = S.Error.Kind, ["reason"] = S.Error.Reason, ["at"] = S.ErrorAt },
            ["nextAt"] = S.Error != null ? S.NextAt : 0, ["canEncrypt"] = _d.Store.CanEncrypt(), ["ver"] = _eventsVer,
        };
    }

    /// The rows for the bar: NO links (a row is opened by its key; the feature looks the link up).
    public JsonObject EventsPayload()
    {
        var spans = new JsonArray();
        foreach (var s in S.Spans) spans.Add((JsonNode)new JsonObject { ["from"] = s.From, ["to"] = s.To });
        var cals = new JsonArray();
        foreach (var c in S.Calendars.Where(c => c.On)) cals.Add((JsonNode)new JsonObject { ["id"] = c.Id, ["color"] = c.Color, ["name"] = c.Name });
        var evs = new JsonArray();
        foreach (var e in S.Events) evs.Add((JsonNode)EventJson(e, false));
        return new JsonObject { ["ver"] = _eventsVer, ["spans"] = spans, ["calendars"] = cals, ["events"] = evs };
    }

    public static JsonObject EventJson(GcalEvent e, bool withLink)
    {
        var o = new JsonObject { ["k"] = e.K, ["cal"] = e.Cal, ["title"] = e.Title, ["allDay"] = e.AllDay };
        if (e.AllDay) { o["s"] = e.SDate; o["e"] = e.EDate; } else { o["s"] = e.SMs; o["e"] = e.EMs; }
        if (withLink) o["link"] = e.Link;
        return o;
    }

    static GcalEvent? EventOf(JsonNode? n)
    {
        if (GcalDays.RowOf(n) is not { } r) return null;
        return new GcalEvent(r.K, r.Cal, r.Title, r.AllDay, r.SDate, r.EDate, r.SMs, r.EMs, GcalCore.Str(n!["link"]));
    }

    void Publish(bool withEvents)
    {
        if (S.Stopped) return;
        if (withEvents) _eventsVer++;
        try { _d.Publish(Status(), withEvents ? EventsPayload() : null); } catch (Exception e) { _d.Log("publish failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ start / stop

    public void Start()
    {
        S.Stopped = false;
        var cl = _d.Store.GetClient();
        if (cl.Broken != null) { S.ClientBroken = "the saved Google client could not be read: " + cl.Broken + "; choose the client file again"; S.Client = null; }
        else S.Client = cl.Value is { } c && GcalCore.Str(c["clientId"]) is { Length: > 0 } id ? new GcalClient(id, GcalCore.Str(c["clientSecret"]) ?? "", GcalCore.Str(c["project"])) : null;
        var tok = _d.Store.GetToken();
        if (tok.Broken != null) {
            S.Token = null; S.NeedsSignIn = true;
            SetError(new GcalProblem("auth", "the saved Google sign-in could not be read: " + tok.Broken + "; sign in again"));
        } else S.Token = GcalToken.Of(tok.Value);
        if (S.Token != null) {
            S.Account = S.Token.Account;
            if (_d.Store.GetCache() is { } cache) {
                S.Calendars = (cache["calendars"] as JsonArray ?? new JsonArray()).Select(CalOf).Where(x => x != null).Select(x => x!).ToList();
                S.Events = (cache["events"] as JsonArray)!.Select(EventOf).Where(x => x != null).Select(x => x!).Take(MaxEvents).ToList();
                S.Spans = (cache["spans"] as JsonArray ?? new JsonArray()).Select(x => (GcalCore.Num(x?["from"]), GcalCore.Num(x?["to"])))
                    .Where(x => x.Item1 != null && x.Item2 != null).Select(x => (x.Item1!.Value, x.Item2!.Value)).ToList();
                S.SyncedAt = GcalCore.Num(cache["syncedAt"]) ?? 0;
                if (GcalCore.Str(cache["account"]) is { } a) S.Account = a;
            }
            Plan(FirstMs);
        } else {
            // no sign-in: no events may stay on this PC (privacy claim 6: a sign-out cut short)
            try { _d.Store.DeleteCache(); } catch (Exception e) { _d.Log("could not delete a leftover cache: " + Code(e)); }
        }
        Publish(true);
    }

    static GcalCalendar? CalOf(JsonNode? n) =>
        n is JsonObject o && GcalCore.Str(o["id"]) is { } id
            ? new GcalCalendar(id, GcalCore.Str(o["name"]) ?? id, GcalCore.Str(o["color"]), GcalCore.Bool(o["primary"]), GcalCore.Bool(o["on"])) : null;

    public void Stop()
    {
        S.Stopped = true;
        _timer?.Dispose(); _timer = null;
        _onlineTimer?.Dispose(); _onlineTimer = null;
    }

    public bool Running => !S.Stopped;

    // ------------------------------------------------------------------ timers

    void Plan(long ms)
    {
        if (S.Stopped) return;
        _timer?.Dispose();
        _timer = _d.After(() => { _timer = null; _ = SyncNow("timer"); }, Math.Max(0, ms));
    }

    void WatchOnline(bool on)
    {
        _onlineTimer?.Dispose(); _onlineTimer = null;
        if (!on || _d.IsOnline == null || S.Stopped) return;
        void Poll()
        {
            _onlineTimer = null;
            if (S.Stopped || S.Error?.Kind != "offline") return;
            bool up;
            try { up = _d.IsOnline(); } catch (Exception) { up = false; }
            if (up) { _d.Log("the network is back: syncing"); _ = SyncNow("online", force: true); }
            else _onlineTimer = _d.After(Poll, OnlinePollMs);
        }
        _onlineTimer = _d.After(Poll, OnlinePollMs);
    }

    /// May a sync start now? During a backoff only a forced reason may go early, and never during a rate limit.
    bool MayGo(bool force)
    {
        if (S.Token == null || S.NeedsSignIn || S.Stopped) return false;
        if (S.Error == null || _d.Now() >= S.NextAt) return true;
        if (S.Error.Kind == "rate") return false;
        return force;
    }

    void SetError(GcalProblem? p) { S.Error = p; S.ErrorAt = p == null ? 0 : _d.Now(); }

    // ------------------------------------------------------------------ the access token

    async Task<string> Access(int g)
    {
        Live(g);
        var t0 = S.Token!;
        if (t0.Access.Length > 0 && t0.ExpiresAt - 60000 > _d.Now()) return t0.Access;
        if (S.Client == null) throw new GcalError("denied", "the Google client file is missing; choose it again in Settings");
        GcalToken t;
        try { t = await _d.Google.RefreshAsync(S.Client, t0.Refresh!).ConfigureAwait(true); }
        catch (Exception e) {
            Live(g);
            var c = GcalCore.Classify(e);
            if (c.Kind == "auth" || (e is GcalError ge && GcalCore.Str(ge.Body?["error"]) == "invalid_grant")) S.NeedsSignIn = true;
            throw new GcalError(c.Kind, c.Reason, c.LogReason) { RetryAfterMs = c.RetryAfterMs };
        }
        Live(g);
        S.Token = t with { Refresh = t.Refresh ?? t0.Refresh, Account = t0.Account ?? S.Account };
        try { _d.Store.PutToken(S.Token.ToJson()); } catch (Exception e) { _d.Log("could not save the refreshed sign-in: " + Words(e)); }
        return S.Token.Access;
    }

    // ------------------------------------------------------------------ the sync

    bool Overlaps(GcalEvent ev, GcalWindow w)
    {
        long s = ev.AllDay ? GcalCore.LocalMs(GcalDays.DateOf(ev.SDate!), _tz) : ev.SMs;
        long e = ev.AllDay ? GcalCore.LocalMs(GcalDays.DateOf(ev.EDate!), _tz) : Math.Max(ev.EMs, ev.SMs + 1);
        return s < w.To && e > w.From;
    }

    sealed record Fetched(List<GcalCalendar> Cals, string? Account, List<GcalEvent> Evs, List<string> Notes);

    async Task<Fetched> FetchWindow(GcalWindow w, bool retried, int g)
    {
        string tok = await Access(g).ConfigureAwait(true);
        JsonArray list;
        try { list = await _d.Google.CalendarListAsync(tok).ConfigureAwait(true); }
        catch (Exception e) {
            Live(g);
            if (e is GcalError { Status: 401 } && !retried) { S.Token = S.Token! with { ExpiresAt = 0 }; return await FetchWindow(w, true, g).ConfigureAwait(true); }
            throw Problem(GcalCore.Classify(e));
        }
        Live(g);
        var cals = GcalCore.PickCalendars(list, _d.Choices());
        string? account = GcalCore.AccountOf(list);
        var tzOf = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var n in list) if (GcalCore.Str(n?["id"]) is { } id) tzOf[id] = GcalCore.Str(n!["timeZone"]);
        var evs = new List<GcalEvent>(); var notes = new List<string>();
        int okCals = 0;
        foreach (var cal in cals.Where(c => c.On)) {
            try {
                var r = await _d.Google.EventsAsync(tok, cal.Id, w.From, w.To).ConfigureAwait(true);
                Live(g);
                foreach (var it in r.Items)
                    if (GcalCore.NormalizeEvent(it, cal.Id, id => tzOf.GetValueOrDefault(id) ?? r.TimeZone, _tz) is { } n) evs.Add(n);
                if (r.Truncated) notes.Add(cal.Name + " has more events than Mantle reads");
                okCals++;
            } catch (Exception e) when (!(e is GcalError { Kind: "stale" })) {
                Live(g);
                if (e is GcalError { Status: 401 } && !retried) { S.Token = S.Token! with { ExpiresAt = 0 }; return await FetchWindow(w, true, g).ConfigureAwait(true); }
                var c = GcalCore.Classify(e);
                if (c.Kind is "rate" or "offline" or "auth") throw Problem(c);
                notes.Add("\"" + cal.Name + "\" could not be read (" + c.Reason + ")");
            }
        }
        // the reason names the calendar (it is shown to the user); the log line gets one without names
        if (okCals == 0 && cals.Any(c => c.On)) throw new GcalError("other", notes.Count > 0 ? notes[0] : "no calendar could be read", "no calendar could be read");
        return new Fetched(cals, account, evs, notes);
    }

    static GcalError Problem(GcalProblem c) => new(c.Kind, c.Reason, c.LogReason) { RetryAfterMs = c.RetryAfterMs };

    public Task<JsonObject> SyncNow(string why, bool force = false, GcalWindow? window = null, bool again = false)
    {
        if (!MayGo(force)) return Task.FromResult(Status());
        if (_inflight != null) { if (window != null || again) _again ??= (why, window); return _inflight; }
        var b = GcalCore.WindowAround(_d.Now(), _tz);
        var w = window ?? b;
        bool isBase = window == null || (window.From == b.From && window.To == b.To);
        int g = _gen;
        S.Syncing = true; Publish(false);
        var tcs = new TaskCompletionSource<JsonObject>();
        _inflight = tcs.Task;
        _ = Run(tcs, w, isBase, why, g);
        return tcs.Task;
    }

    async Task Run(TaskCompletionSource<JsonObject> tcs, GcalWindow w, bool isBase, string why, int g)
    {
        try {
            try {
                var r = await FetchWindow(w, false, g).ConfigureAwait(true);
                if (g == _gen) Took(r, w, isBase, why);
            } catch (Exception e) {
                if (g == _gen) Failed(e, why);
            }
            if (_inflight == tcs.Task) _inflight = null;
            if (g != _gen) { tcs.TrySetResult(Status()); return; }
            S.Syncing = false;
            Publish(true);
            var a = _again; _again = null;
            if (a is { } next) { tcs.TrySetResult(await SyncNow(next.Why, window: next.W).ConfigureAwait(true)); return; }
            tcs.TrySetResult(Status());
        } catch (Exception e) {
            if (_inflight == tcs.Task) _inflight = null;
            _d.Log("sync threw: " + e.GetType().Name);
            tcs.TrySetResult(Status());
        }
    }

    void Took(Fetched r, GcalWindow w, bool isBase, string why)
    {
        S.Calendars = r.Cals;
        if (r.Account != null) S.Account = r.Account;
        if (S.Token != null && r.Account != null && S.Token.Account != r.Account) {
            S.Token = S.Token with { Account = r.Account };
            try { _d.Store.PutToken(S.Token.ToJson()); } catch (Exception) { /* the next refresh saves it */ }
        }
        if (isBase) { S.Events = r.Evs; S.Spans = new List<(long, long)> { (w.From, w.To) }; }
        else {
            S.Events = S.Events.Where(e => !Overlaps(e, w)).Concat(r.Evs).ToList();
            var sp = S.Spans.Where(s => !(s.From == w.From && s.To == w.To)).Append((w.From, w.To)).ToList();
            S.Spans = sp.Skip(Math.Max(0, sp.Count - 3)).ToList();
        }
        S.Events.Sort(GcalCore.Compare);
        if (S.Events.Count > MaxEvents) S.Events = S.Events.Take(MaxEvents).ToList();
        S.SyncedAt = _d.Now();
        SetError(r.Notes.Count > 0 ? new GcalProblem("partial", string.Join("; ", r.Notes)) : null);
        S.Failures = 0; S.NextAt = 0;
        try { _d.Store.PutCache(CacheJson()); }
        catch (Exception e) { SetError(new GcalProblem("other", "the events could not be saved on this PC (" + Code(e) + ")")); }
        _d.Log("synced (" + why + "): " + S.Events.Count + " events from " + r.Cals.Count(c => c.On) + " calendars");
        WatchOnline(false);
        Plan(PeriodMs);
    }

    void Failed(Exception e, string why)
    {
        var c = GcalCore.Classify(e);
        S.Failures++;
        if (c.Kind == "auth") S.NeedsSignIn = true;
        SetError(c);
        S.NextAt = _d.Now() + GcalCore.BackoffMs(S.Failures, c.RetryAfterMs, _d.Random);
        _d.Log("sync failed (" + why + "): " + c.Kind + ": " + (c.LogReason ?? c.Reason) + "; next try in " +
            Math.Round((S.NextAt - _d.Now()) / 1000.0).ToString(CultureInfo.InvariantCulture) + " s");
        if (S.NeedsSignIn) { _timer?.Dispose(); _timer = null; }
        else Plan(S.NextAt - _d.Now());
        WatchOnline(c.Kind == "offline");
    }

    JsonObject CacheJson()
    {
        var cals = new JsonArray();
        foreach (var c in S.Calendars) cals.Add((JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name, ["color"] = c.Color, ["primary"] = c.Primary, ["on"] = c.On });
        var evs = new JsonArray();
        foreach (var e in S.Events) evs.Add((JsonNode)EventJson(e, true));
        var spans = new JsonArray();
        foreach (var s in S.Spans) spans.Add((JsonNode)new JsonObject { ["from"] = s.From, ["to"] = s.To });
        return new JsonObject { ["account"] = S.Account, ["calendars"] = cals, ["events"] = evs, ["spans"] = spans, ["syncedAt"] = S.SyncedAt };
    }

    /// A new sign-in or a sign-out: whatever sync is still out belongs to the old one and is dropped.
    void NewGeneration() { _gen++; _inflight = null; _again = null; S.Syncing = false; }

    bool Covered(long from, long to) => S.Spans.Any(s => s.From <= from && s.To >= to);

    // ------------------------------------------------------------------ what pages and the feature ask

    public Task<JsonObject> SyncForced(string? why) => SyncNow(why ?? "asked", force: true);

    /// The flyout opened: a background refresh when the last good sync is older than 2 minutes.
    public JsonObject Opened()
    {
        if (S.Token == null || S.NeedsSignIn || S.Syncing) return new JsonObject { ["ok"] = true, ["synced"] = false };
        if (_d.Now() - S.SyncedAt < StaleMs) return new JsonObject { ["ok"] = true, ["synced"] = false };
        if (!MayGo(false)) return new JsonObject { ["ok"] = true, ["synced"] = false, ["reason"] = S.Error?.Reason };
        _ = SyncNow("opened");
        return new JsonObject { ["ok"] = true, ["synced"] = true };
    }

    /// The flyout shows a month the cache does not cover: fetch it +-1 month.
    public JsonObject Want(int? year, int? month)
    {
        if (year is not int y || month is not int m || m < 0 || m > 11 || y < 1970 || y > 2200) return new JsonObject { ["ok"] = false, ["reason"] = "that is not a month" };
        var first = new DateTime(y, 1, 1).AddMonths(m);
        if (Covered(GcalCore.LocalMs(first, _tz), GcalCore.LocalMs(first.AddMonths(1), _tz))) return new JsonObject { ["ok"] = true, ["covered"] = true };
        if (!MayGo(false)) return new JsonObject { ["ok"] = true, ["covered"] = false, ["reason"] = S.Error?.Reason ?? "not signed in" };
        _ = SyncNow("month", window: GcalCore.MonthWindow(y, m, _tz));
        return new JsonObject { ["ok"] = true, ["covered"] = false, ["loading"] = true };
    }

    public void Resume() { if (S.Token != null && !S.NeedsSignIn) Plan(ResumeMs); }
    public void Online() { if (S.Error?.Kind == "offline") _ = SyncNow("online", force: true); }

    public JsonObject SetClient(string? text)
    {
        GcalClient c;
        try { c = GcalCore.ParseClientFile(text); } catch (GcalError e) { return Fail(e.Reason ?? e.Message); }
        try { _d.Store.PutClient(c); } catch (Exception e) { return Fail(e is GcalError ge && ge.Reason != null ? ge.Reason : "it could not be saved (" + e.Message + ")"); }
        S.Client = c; S.ClientBroken = null;
        Publish(false);
        return new JsonObject { ["ok"] = true, ["project"] = c.Project };
    }

    public async Task<JsonObject> SignIn()
    {
        if (S.SigningIn) return Fail("a sign-in is already open in your browser");
        if (S.Client == null) return Fail(S.ClientBroken ?? "choose your Google client file first");
        if (!_d.Store.CanEncrypt()) return Fail("Windows encryption (DPAPI) is not available, so Mantle will not store a Google sign-in");
        S.SigningIn = true; Publish(false);
        try {
            var tok = await _d.SignIn(S.Client).ConfigureAwait(true);
            S.Token = tok with { Account = null };
            _d.Store.PutToken(S.Token.ToJson());
            NewGeneration();
            S.NeedsSignIn = false; SetError(null); S.Failures = 0; S.NextAt = 0;
            _d.Log("signed in");
        } catch (Exception e) {
            S.SigningIn = false; Publish(false);
            return Fail(e is GcalError ge && ge.Reason != null ? ge.Reason : e.Message is { Length: > 0 } m ? m : "the sign-in did not finish");
        }
        S.SigningIn = false;
        await SyncNow("signed in", force: true).ConfigureAwait(true);
        return new JsonObject { ["ok"] = true, ["account"] = S.Account };
    }

    /// Privacy claim 6: revoke at Google, delete the token AND the cached events from this PC.
    public async Task<JsonObject> SignOut()
    {
        var tok = S.Token;
        NewGeneration();
        S.Token = null;   // no sync may start while Google is being told (no token, no timers)
        _timer?.Dispose(); _timer = null; WatchOnline(false);
        bool revoked = false; string? why = null;
        if (tok != null && (tok.Refresh is { Length: > 0 } || tok.Access.Length > 0)) {
            try { await _d.Google.RevokeAsync(tok.Refresh is { Length: > 0 } r ? r : tok.Access).ConfigureAwait(true); revoked = true; }
            catch (Exception e) {
                // 400 invalid_token: Google had already ended it, which is what revoking is for
                if (e is GcalError { Status: 400 }) revoked = true;
                else why = GcalCore.Classify(e).Reason;
            }
        }
        bool deleted = true; string? delWhy = null;
        try { _d.Store.DeleteToken(); } catch (Exception e) { deleted = false; delWhy = "the sign-in file could not be deleted (" + Code(e) + ")"; }
        try { _d.Store.DeleteCache(); } catch (Exception e) { deleted = false; delWhy = (delWhy != null ? delWhy + "; " : "") + "the cached events could not be deleted (" + Code(e) + ")"; }
        _timer?.Dispose(); _timer = null; WatchOnline(false);
        S.Token = null; S.Account = null; S.Calendars = new(); S.Events = new(); S.Spans = new(); S.SyncedAt = 0;
        SetError(null); S.NeedsSignIn = false; S.Failures = 0; S.NextAt = 0;
        _d.Log("signed out" + (revoked ? "" : " (not revoked at Google)"));
        Publish(true);
        if (!deleted) return Fail(delWhy!);
        if (!revoked && tok != null)
            return new JsonObject { ["ok"] = true, ["revoked"] = false, ["reason"] = "Signed out on this PC, but Google could not be reached to end Mantle's access (" + why + "). Remove Mantle at myaccount.google.com/permissions" };
        return new JsonObject { ["ok"] = true, ["revoked"] = tok != null };
    }

    public JsonObject SetCalendar(string? calId, bool on)
    {
        if (calId == null || !S.Calendars.Any(c => c.Id == calId)) return Fail("that calendar is not in your list");
        var ch = new Dictionary<string, bool>(_d.Choices(), StringComparer.Ordinal) { [calId] = on };
        _d.SaveChoices(ch);
        S.Calendars = S.Calendars.Select(c => c.Id == calId ? c with { On = on } : c).ToList();
        if (!on) S.Events = S.Events.Where(e => e.Cal != calId).ToList();
        Publish(true);
        if (on) _ = SyncNow("calendar ticked", force: true, again: true);
        return new JsonObject { ["ok"] = true, ["value"] = on };
    }

    /// The event's Google page: null when no such event is held, "" when Google gave none.
    public string? LinkOf(string? k) => S.Events.FirstOrDefault(x => x.K == k) is { } e ? e.Link ?? "" : null;

    static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };
    static string Code(Exception e) => e is GcalStoreError se ? se.Code : e.GetType().Name;
    static string Words(Exception e) => e is GcalError ge && ge.Reason != null ? ge.Reason : e.Message;
}
