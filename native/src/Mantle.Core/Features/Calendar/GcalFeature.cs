using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// Google Calendar in the clock's calendar: live, READ-ONLY (lib/features/gcal.js). The flyout (Surfaces/Bar/Calendar)
/// draws a dot under each day with events and the selected day's agenda from the last "gcal.events" push; this feature
/// wires the engine (GcalEngine) to the store (GcalStore), Google (GcalGoogle) and the browser sign-in (GcalAuth).
/// Commands (Electron's names): gcal.status, gcal.signIn, gcal.signOut, gcal.pickClient, gcal.setCalendar, gcal.opened,
/// gcal.want, gcal.open. Events: gcal.status, gcal.events (both replayed on subscribe).
///
/// THE OPEN PATH HAS NO NETWORK AND NO CALL: the bar holds the last push and draws from it; only AFTER the first frame
/// does it send gcal.opened (a refresh when the cache is over 2 minutes old).
///
/// PRIVACY (the public privacy page): scope calendar.readonly only; data used only for the dots and the day's list
/// (Settings also shows the account and calendar names); token only on this PC, encrypted for the user's Windows account;
/// the cache only on this PC; no servers of Mantle's own, no analytics; traffic only to Google plus opening an event's
/// Google page in the user's browser; Sign out revokes at Google and deletes the token AND the cache. Nothing here logs an
/// event title, a calendar name, an address or a token.
///
/// --test: no request ever leaves the PC (every Google call is refused); sign-in, sign-out, the file picker and opening
/// an event answer a dry run. MANTLE_TEST_GCAL (test only): {status, events} drawn in place of the engine's, so every
/// runner shows the same flyout; MANTLE_GCAL_SELFTEST=1 (test only): the in-app self-test (GcalSelfTest).
/// </summary>
[Feature("gcal", "Google Calendar in the clock's calendar", true, Rest = true)]
public sealed class GcalFeature : IFeature
{
    public const string TestWhy = "test run: nothing is sent to Google";
    public const string FixtureEnv = "MANTLE_TEST_GCAL", SelfTestEnv = "MANTLE_GCAL_SELFTEST";
    public const string StatusEvent = "gcal.status", EventsEvent = "gcal.events";

    /// <summary>Where the files live and how the browser and the file picker are reached (App.xaml.cs, block "calendar wiring").</summary>
    public sealed record Host(string DataRoot, SafeFile Files, Func<string, string?>? OpenExternal = null, Func<string, string, Task<string?>>? PickFile = null);
    static Host? _host;
    public static void Configure(Host h) => _host = h;

    IFeatureContext _ctx = null!;
    GcalEngine? _engine;
    SynchronizationContext? _sc;
    bool _test, _running;
    JsonObject? _lastStatus, _lastEvents;
    JsonObject? _fxStatus;
    readonly Dictionary<string, string?> _fxLinks = new(StringComparer.Ordinal);
    readonly List<IDisposable> _timers = new();

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _test = ctx.Flags.Test;
        _sc = SynchronizationContext.Current;
        var host = _host;
        string root = host?.DataRoot ?? Path.Combine(Path.GetTempPath(), "mantle-gcal-unconfigured");
        var files = host?.Files ?? new SafeFile(WriteGate.Never("Google Calendar was given no place to keep its files in this run"));
        var store = new GcalStore(Path.Combine(root, "gcal"), files);
        if (_test && Environment.GetEnvironmentVariable(FixtureEnv) is { Length: > 0 } fx) TakeFixture(fx);
        IGcalGoogle google = _test ? new RefusingGoogle() : new GcalGoogle();
        _engine = new GcalEngine(new GcalEngineDeps {
            Store = store, Google = google,
            SignIn = client => GcalAuth.SignInAsync(new GcalAuthOptions { Client = client, Google = google, OpenExternal = OpenUrl }),
            Choices = Choices, SaveChoices = SaveChoices,
            After = After,
            IsOnline = () => { try { return System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); } catch (Exception) { return true; } },
            Log = ctx.Log,
            Publish = (status, events) => {
                if (_fxStatus != null) return;   // a fixture run draws the fixture, never the engine
                _lastStatus = Tag(status, ctx.Enabled);
                ctx.Broadcast(StatusEvent, El(_lastStatus));
                if (events != null) { _lastEvents = events; ctx.Broadcast(EventsEvent, El(events)); }
            },
        });
        ctx.Replay(StatusEvent, () => El(_lastStatus ?? Tag(_engine.Status(), ctx.Enabled)));
        ctx.Replay(EventsEvent, () => El(_lastEvents ?? _engine.EventsPayload()));

        ctx.Command("status", _ => Ok(Tag(_fxStatus?.DeepClone() as JsonObject ?? _engine.Status(), ctx.Enabled)));
        ctx.Command("signIn", _ => Answer(SignInAsync()));
        ctx.Command("signOut", _ => Answer(SignOutAsync()));
        ctx.Command("pickClient", _ => Answer(PickClientAsync()));
        ctx.Command("setCalendar", c => Ok(_fxStatus != null ? DryRun(TestWhy) : _engine.SetCalendar(Str(c.Args, "calendar"), Bool(c.Args, "on"))));
        ctx.Command("opened", _ => Ok(_running && !_test ? _engine.Opened() : new JsonObject { ["ok"] = true, ["synced"] = false }));
        ctx.Command("want", c => Ok(_running && !_test ? _engine.Want(Int(c.Args, "year"), Int(c.Args, "month"))
            : new JsonObject { ["ok"] = true, ["covered"] = false, ["reason"] = _test ? TestWhy : "Google Calendar is turned off" }));
        ctx.Command("open", c => Answer(OpenAsync(Str(c.Args, "event"))));

        ctx.OnEnabled(on => { if (on) Begin(); else Pause(); });
        Begin();
        if (!_running) Pause();
        if (_test && Environment.GetEnvironmentVariable(SelfTestEnv) == "1")
            _timers.Add(After(() => _ = GcalSelfTest.RunAsync(Path.Combine(root, "gcal-selftest"), files, _lastStatus, ctx.Log), 1500));
        ctx.Health(true);   // no DPAPI is said by Sign in itself; the flyout works either way
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _engine?.Stop(); _running = false;
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
        return ValueTask.CompletedTask;
    }

    void Begin()
    {
        if (_running || !_ctx.Enabled) return;
        _running = true;
        if (_fxStatus != null) { Publish(Tag((JsonObject)_fxStatus.DeepClone(), true), _lastEvents); return; }
        _engine!.Start();
    }

    void Pause()
    {
        _engine?.Stop(); _running = false;
        var s = Tag(_fxStatus?.DeepClone() as JsonObject ?? _engine!.Status(), false);
        _lastStatus = s;
        _ctx.Broadcast(StatusEvent, El(s));
    }

    void Publish(JsonObject status, JsonObject? events)
    {
        _lastStatus = status;
        _ctx.Broadcast(StatusEvent, El(status));
        if (events != null) _ctx.Broadcast(EventsEvent, El(events));
    }

    JsonObject Tag(JsonObject s, bool enabled) { s["enabled"] = enabled; s["test"] = _test; return s; }

    /// MANTLE_TEST_GCAL: {status, events}; events may carry `link` (kept here for gcal.open, never sent to the bar).
    void TakeFixture(string fx)
    {
        JsonObject? o;
        try { o = JsonNode.Parse(fx) as JsonObject; } catch (Exception) { o = null; }
        if (o?["status"] is not JsonObject st) { _ctx.Log("MANTLE_TEST_GCAL is not {status, events}, so it was not taken"); return; }
        _fxStatus = (JsonObject)st.DeepClone();
        var evs = new JsonArray();
        foreach (var e in (o["events"] as JsonObject)?["events"] as JsonArray ?? new JsonArray()) {
            if (e is not JsonObject eo || GcalCore.Str(eo["k"]) is not { } k) continue;
            _fxLinks[k] = GcalCore.Str(eo["link"]);
            var c = (JsonObject)eo.DeepClone(); c.Remove("link");
            evs.Add((JsonNode)c);
        }
        var ev = o["events"] as JsonObject is { } eobj ? (JsonObject)eobj.DeepClone() : new JsonObject();
        ev["events"] = evs;
        _lastEvents = ev;
        _ctx.Log($"a --test run with {FixtureEnv}: the calendar draws its made-up status and {evs.Count} events, never Google's");
    }

    // ------------------------------------------------------------------ the commands

    async Task<JsonObject> SignInAsync()
    {
        if (_test) { _ctx.Log("dry run (--test): Sign in would open Google's consent page in the browser; nothing was opened or sent"); return DryRun(TestWhy); }
        if (!_ctx.Enabled) return Fail("Google Calendar is turned off in Mantle's tray menu");
        return await _engine!.SignIn().ConfigureAwait(true);
    }

    async Task<JsonObject> SignOutAsync()
    {
        if (_test) { _ctx.Log("dry run (--test): Sign out would revoke at Google and delete token.bin and cache.json; nothing was sent or deleted"); return DryRun(TestWhy); }
        return await _engine!.SignOut().ConfigureAwait(true);
    }

    async Task<JsonObject> PickClientAsync()
    {
        if (_test) return DryRun("test run: no file dialog is opened");
        if (_host?.PickFile is not { } pick) return Fail("choosing the Google client file is not in the native app yet");
        string? path;
        try { path = await pick("Choose your Google client file", ClientFolder()).ConfigureAwait(true); }
        catch (Exception e) { return Fail("the file dialog did not open (" + GcalCore.Cut(e.Message, 60) + ")"); }
        if (string.IsNullOrEmpty(path)) return new JsonObject { ["ok"] = false, ["cancelled"] = true, ["reason"] = "no file was chosen" };
        string text;
        try {
            if (new FileInfo(path).Length > 64 * 1024) return Fail("that file is too big to be a Google client file");
            text = File.ReadAllText(path);
        } catch (Exception e) { return Fail("that file could not be read (" + SafeFile.Code(e) + ")"); }
        return _engine!.SetClient(text);
    }

    /// Where "Choose Google client file…" opens: %USERPROFILE%\.secrets\google when that folder exists, else Downloads.
    public static string ClientFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string mine = Path.Combine(home, ".secrets", "google");
        return Directory.Exists(mine) ? mine : Path.Combine(home, "Downloads");
    }

    Task<JsonObject> OpenAsync(string? k)
    {
        string? link = _fxStatus != null ? (k != null && _fxLinks.TryGetValue(k, out var l) ? l ?? "" : null) : _engine!.LinkOf(k);
        if (link == null) return Task.FromResult(Fail("that event is no longer in the calendar"));
        if (link.Length == 0) return Task.FromResult(Fail("Google gave no web page for this event"));
        if (_test) { _ctx.Log("dry run (--test): an event's Google page would open in the browser; nothing was opened"); return Task.FromResult(DryRun("test run: the browser is not opened")); }
        if (OpenUrl(link) is { } why) return Task.FromResult(Fail("your browser could not be opened (" + GcalCore.Cut(why, 60) + ")"));
        return Task.FromResult(new JsonObject { ["ok"] = true });
    }

    /// The user's own default browser (shell open); null when it opened, else why not.
    static string? OpenUrl(string url)
    {
        if (_host?.OpenExternal is { } open) return open(url);
        try { using var p = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return null; }
        catch (Exception e) { return e.Message; }
    }

    IReadOnlyDictionary<string, bool> Choices()
    {
        var d = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (_ctx.Settings.TryGet("calendars", out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject()) if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) d[p.Name] = p.Value.GetBoolean();
        return d;
    }

    void SaveChoices(Dictionary<string, bool> ch)
    {
        var o = new JsonObject();
        foreach (var kv in ch) o[kv.Key] = kv.Value;
        _ = Save(o);
        async Task Save(JsonObject value)
        {
            var w = await _ctx.Settings.SetAsync("calendars", value).ConfigureAwait(true);
            if (!w.Ok) _ctx.Log("the calendar choice could not be saved: " + w.Reason);
        }
    }

    /// The action after ms, on the host thread (where it was installed); disposing cancels it.
    IDisposable After(Action a, long ms)
    {
        var cts = new CancellationTokenSource();
        var sc = _sc;
        _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, ms)), cts.Token).ContinueWith(t => {
            if (t.IsCanceled || cts.IsCancellationRequested) return;
            void Go() { if (!cts.IsCancellationRequested) { try { a(); } catch (Exception e) { _ctx.Log("a timer failed: " + e.GetType().Name); } } }
            if (sc != null) sc.Post(_ => Go(), null); else Go();
        }, TaskScheduler.Default);
        return new Off(cts);
    }

    sealed class Off : IDisposable
    {
        readonly CancellationTokenSource _c;
        public Off(CancellationTokenSource c) => _c = c;
        public void Dispose() { try { _c.Cancel(); } catch (ObjectDisposedException) { } }
    }

    /// --test: every Google call is refused before anything is sent.
    sealed class RefusingGoogle : IGcalGoogle
    {
        static Task<T> No<T>() => Task.FromException<T>(new GcalError("other", TestWhy));
        public Task<GcalToken> ExchangeAsync(GcalClient client, string code, string verifier, string redirectUri) => No<GcalToken>();
        public Task<GcalToken> RefreshAsync(GcalClient client, string refreshToken) => No<GcalToken>();
        public Task RevokeAsync(string token) => No<bool>();
        public Task<JsonArray> CalendarListAsync(string access) => No<JsonArray>();
        public Task<GcalPage> EventsAsync(string access, string calId, long from, long to) => No<GcalPage>();
    }

    static JsonObject DryRun(string why) => new() { ["ok"] = true, ["dryRun"] = true, ["reason"] = why };
    static JsonObject Fail(string why) => new() { ["ok"] = false, ["reason"] = why };
    static ValueTask<CommandResult> Ok(JsonObject o) => ValueTask.FromResult(CommandResult.Success(El(o)));
    static async ValueTask<CommandResult> Answer(Task<JsonObject> work) => CommandResult.Success(El(await work.ConfigureAwait(true)));
    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
    static string? Str(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static int? Int(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
