using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Media;

/// <summary>Who the media back end talks to, and how this run is set.</summary>
public sealed class MediaOptions
{
    public required IPlatform Platform { get; init; }
    public required IEvents Events { get; init; }
    public required IHostScheduler Host { get; init; }
    /// A --test run: every media.control is a DRY RUN (logged, never sent to the sidecar).
    public required bool Test { get; init; }
    public required Action<string> Log { get; init; }
    /// The data folder media-guard.json is read from (once, on the first use).
    public string DataDir { get; init; } = "";
    /// --media-guard / --media-controls.
    public bool FlagGuard { get; init; }
    public bool FlagControls { get; init; }
    /// MANTLE_TEST_MEDIA (test runs only): made-up sessions read in place of the sidecar's, so every runner draws the
    /// same chip and card. JSON {sessions:[...], events?, eventsReason?, fail?}. Ignored outside --test.
    public string? Fixture { get; init; }
    /// The poll and retry paces (main.js MEDIA_POLL_MS / MEDIA_RETRY_MS); a unit test shortens nothing.
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(10000);
    public TimeSpan RetryEvery { get; init; } = TimeSpan.FromMilliseconds(5000);
}

/// <summary>
/// The media back end (main.js mediaRead / mediaPush / mediaPollArm / mediaPickArm and the media.get, media.select and
/// media.control commands): reads Windows' media sessions through the sidecar's <c>media</c> verb, picks the one the bar
/// shows (<see cref="MediaPick"/>, under the <see cref="MediaGuard"/> rule) and publishes the <c>"media"</c> payload
/// (replayed on subscribe). Driven by the sidecar's change events; polls every 10 s only when those could not be hooked,
/// and every 5 s after a read that failed in a way a retry can fix. Reads coalesce: triggers during a read make exactly
/// one more. Host thread only.
/// </summary>
public sealed class MediaService
{
    public const string Event = "media";
    public const string MantleAumid = "com.owensilva.mantle";
    public static readonly string[] Actions = { "play", "pause", "toggle", "next", "previous", "seek" };
    public const string OldExeRead = "this sidecar exe has no \"media\" command yet (run npm run build:sidecar)";
    public const string OldExeControl = "this sidecar exe has no \"media\" command yet, so nothing can be played or paused (run npm run build:sidecar)";

    readonly MediaOptions _o;
    MediaGuardConfig? _guard;
    JsonObject? _last;
    MediaPickHeld? _pick;
    MediaPickDropped? _dropped;
    IDisposable? _pickTimer, _pollTimer;
    Task<JsonObject?>? _reading;
    bool _again;
    readonly List<Action<JsonObject>> _listeners = new();
    readonly JsonObject? _fixture;
    long _fixtureRev = 1;
    bool _stopped;

    /// Set from the bar (media.card): the card is open, so position-only changes are read too.
    public bool CardOpen { get; set; }
    public JsonObject? Last => _last;

    public MediaService(MediaOptions o)
    {
        _o = o;
        if (o.Test && o.Fixture is { Length: > 0 } fx) {
            try { _fixture = JsonNode.Parse(fx) as JsonObject; } catch (JsonException e) { o.Log($"media: MANTLE_TEST_MEDIA is not JSON ({e.Message}), so the sidecar is read"); }
            if (_fixture != null) o.Log("media: a --test run with MANTLE_TEST_MEDIA: the sessions are its made-up ones, never the sidecar's");
        }
        o.Events.Replay(Event, () => El(_last ?? Failed("what is playing has not been read yet", true)));
        if (_fixture != null) { o.Host.Post(() => _ = Push("the test fixture is loaded")); return; }
        o.Platform.OnReady(() => _ = WatchAsync());
        o.Platform.On("media", e => OnEvent(e.Body));
    }

    /// ctx.host.onMedia: features that read the sessions (the top bar's full-screen video rule). A throwing listener is logged.
    public IDisposable OnMedia(Action<JsonObject> fn) { _listeners.Add(fn); return new Off(() => _listeners.Remove(fn)); }

    public void Stop() { _stopped = true; _pickTimer?.Dispose(); _pollTimer?.Dispose(); _pickTimer = _pollTimer = null; }

    async Task WatchAsync()
    {
        var r = await _o.Platform.CallAsync("media", new JsonObject { ["op"] = "watch", ["on"] = true }).ConfigureAwait(true);
        if (!r.Ok) {
            if (ProcessPlatform.IsOldExeError(r.Error)) _o.Log($"mantle: media watch not armed: {r.Error}");
            await Push("the sidecar is ready (no media watch)").ConfigureAwait(true);
            return;
        }
        if (Node(r.Result) is JsonObject o && o["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b)
            _o.Log($"mantle: media watch: {Str(o["reason"]) ?? "refused without a reason"}; the bar polls instead");
        await Push("the sidecar is ready").ConfigureAwait(true);
    }

    /// sidecar.on('media'): the same rev is ignored; timeline-only changes are read only while the card is open.
    public void OnEvent(JsonElement body)
    {
        var ev = Node(body) as JsonObject;
        if (_last != null && ev != null && ev.ContainsKey("rev") && JsonNode.DeepEquals(ev["rev"], _last["rev"])) return;
        var reasons = (ev?["reasons"] as JsonArray)?.Select(x => Str(x) ?? "").ToList() ?? new List<string>();
        bool timelineOnly = reasons.Count > 0 && reasons.All(r => r == "timeline");
        if (timelineOnly && !CardOpen) return;
        _ = Push($"media changed ({(reasons.Count > 0 ? string.Join(", ", reasons) : "no reason given")})");
    }

    // ------------------------------------------------------------------ the guard

    MediaGuardConfig Guard()
    {
        if (_guard != null) return _guard;
        _guard = MediaGuard.Read(_o.DataDir);
        if (_guard.Found || (_guard.Reason != null && !_guard.Reason.StartsWith("no ", StringComparison.Ordinal))) {
            var st = MediaGuard.State(_guard, _o.FlagGuard, _o.FlagControls);
            _o.Log($"mantle: protected media apps: {(_guard.Apps.Count > 0 ? string.Join(", ", _guard.Apps) : "none")}, rule {(st.On ? "on" : "off")} ({st.By}){(_guard.Reason != null ? "; " + _guard.Reason : "")}");
        }
        return _guard;
    }
    bool GuardOn() => MediaGuard.State(Guard(), _o.FlagGuard, _o.FlagControls).On;
    string? ProtectedOf(JsonObject? s) => s == null ? null : MediaGuard.Match(Guard(), Str(s["aumid"]), Str(s["app"]?["name"]), Str(s["title"]));
    JsonObject Controls(string app)
    {
        var st = MediaGuard.State(Guard(), _o.FlagGuard, _o.FlagControls);
        return new JsonObject { ["ok"] = false, ["reason"] = MediaGuard.ReasonFor(Guard(), app), ["how"] = MediaGuard.HowFor(st) };
    }
    /// lib/mantle-apps.js mediaApp(): Mantle's own session is Mantle Player, never the raw AUMID.
    static JsonObject? OwnApp(string? aumid) => string.Equals(aumid, MantleAumid, StringComparison.OrdinalIgnoreCase)
        ? new JsonObject { ["name"] = "Mantle Player", ["icon"] = null, ["mantleApp"] = "player" } : null;

    // ------------------------------------------------------------------ the read

    JsonObject Failed(string reason, bool retry) => new() {
        ["ok"] = false, ["present"] = false, ["reason"] = reason, ["retry"] = retry, ["rev"] = null,
        ["guard"] = MediaGuard.Summary(Guard(), _o.FlagGuard, _o.FlagControls), ["sessions"] = new JsonArray(), ["shown"] = null,
        ["pick"] = null, ["events"] = false, ["eventsReason"] = null,
    };

    async Task<JsonObject> Ask(JsonObject? extra)
    {
        if (_fixture != null) return FixtureAnswer(extra);
        var args = new JsonObject { ["op"] = "get", ["art"] = true };
        if (extra != null) foreach (var kv in extra) args[kv.Key] = kv.Value?.DeepClone();
        var r = await _o.Platform.CallAsync("media", args).ConfigureAwait(true);
        if (!r.Ok) {
            bool old = ProcessPlatform.IsOldExeError(r.Error);
            return new JsonObject { ["ok"] = false, ["old"] = old, ["reason"] = old ? OldExeRead : $"the sessions could not be read: {r.Error}" };
        }
        return Node(r.Result) as JsonObject ?? new JsonObject { ["ok"] = false, ["reason"] = "the sidecar answered \"media\" with nothing" };
    }

    JsonObject FixtureAnswer(JsonObject? extra)
    {
        var f = _fixture!;
        if (Str(f["fail"]) is { } why) return new JsonObject { ["ok"] = false, ["reason"] = why };
        var all = (f["sessions"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        if (Str(extra?["aumid"]) is { } want) {
            var one = all.FirstOrDefault(s => MediaPick.SameAumid(Str(s["aumid"]), want));
            return one != null ? new JsonObject { ["ok"] = true, ["session"] = one.DeepClone() }
                : new JsonObject { ["ok"] = true, ["session"] = null, ["sessionReason"] = $"no media session from {want} is open" };
        }
        var cur = all.FirstOrDefault(s => Bool(s["current"]));
        return new JsonObject {
            ["ok"] = true, ["present"] = all.Count > 0, ["rev"] = _fixtureRev,
            ["reason"] = all.Count == 0 ? "nothing is playing: no app has a media session open" : null,
            ["current"] = cur?.DeepClone(), ["sessions"] = new JsonArray(all.Select(s => (JsonNode?)s.DeepClone()).ToArray()),
            ["events"] = f["events"] is JsonValue ev && ev.TryGetValue<bool>(out var e) ? e : true,
            ["eventsReason"] = f["eventsReason"]?.DeepClone(),
        };
    }

    /// mediaRead(): never throws; a failure is a payload with a reason.
    public async Task<JsonObject> ReadAsync()
    {
        var guard = MediaGuard.Summary(Guard(), _o.FlagGuard, _o.FlagControls);
        var snap = await Ask(null).ConfigureAwait(true);
        if (snap["ok"] is JsonValue okv && okv.TryGetValue<bool>(out var okb) && !okb)
            return Failed(Str(snap["reason"]) ?? "the sidecar answered \"media\" with ok:false and gave no reason", !Bool(snap["old"]));
        var rows = new JsonArray();
        var lite = new List<MediaPickSession>();
        foreach (var s in (snap["sessions"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) {
            string? prot = ProtectedOf(s);
            string? aumid = Str(s["aumid"]);
            var app = OwnApp(aumid) ?? (s["app"] is JsonObject a ? new JsonObject { ["name"] = a["name"]?.DeepClone(), ["icon"] = a["icon"]?.DeepClone() } : null);
            rows.Add(new JsonObject {
                ["aumid"] = aumid, ["app"] = app, ["title"] = Str(s["title"]), ["artist"] = Str(s["artist"]),
                ["status"] = Str(s["status"]) ?? "unknown", ["playing"] = Bool(s["playing"]), ["current"] = Bool(s["current"]),
                ["protectedApp"] = prot != null, ["label"] = prot != null ? Guard().Label : null,
            });
            lite.Add(new MediaPickSession(aumid, Str(app?["name"]), Bool(s["playing"]), prot != null));
        }
        var curObj = snap["current"] as JsonObject;
        var cur0 = curObj != null ? new MediaPickSession(Str(curObj["aumid"]), Str(curObj["app"]?["name"]), Bool(curObj["playing"]), ProtectedOf(curObj) != null) : null;
        // read and written with no await between: a media.select during the sidecar read above is seen here
        var choice = MediaPick.Choose(_pick, lite, cur0, GuardOn(), _o.Host.NowMs);
        _pick = choice.Pick;
        if (choice.Dropped != null) { _dropped = choice.Dropped; _o.Log($"mantle: media pick dropped: {choice.Dropped.Reason}"); }
        PickArm(choice.Waiting);
        JsonObject PickNote(JsonObject p)
        {
            if (choice.Waiting is { } w) {
                p["waiting"] = new JsonObject { ["aumid"] = w.Aumid, ["name"] = w.Name, ["goneMs"] = w.GoneMs, ["graceMs"] = w.GraceMs, ["leftMs"] = w.LeftMs, ["reason"] = w.Reason };
                p["reason"] = w.Reason;
            }
            if (choice.Auto != null) p["auto"] = choice.Auto;
            if (_pick == null && _dropped != null) p["dropped"] = new JsonObject { ["aumid"] = _dropped.Aumid, ["name"] = _dropped.Name, ["reason"] = _dropped.Reason };
            return p;
        }
        var rev = snap.ContainsKey("rev") ? snap["rev"]?.DeepClone() : null;
        bool events = !(snap["events"] is JsonValue evv && evv.TryGetValue<bool>(out var evb) && !evb);
        if (choice.ProtectedOnly)
            return new JsonObject {
                ["ok"] = true, ["present"] = true,
                ["reason"] = "only protected media apps have a media session, so the now-playing chip stays hidden (the protected media apps rule)",
                ["rev"] = rev, ["guard"] = guard, ["sessions"] = rows, ["shown"] = null,
                ["pick"] = PickNote(new JsonObject { ["aumid"] = null, ["by"] = "protected-only" }),
                ["events"] = events, ["eventsReason"] = snap["eventsReason"]?.DeepClone(),
            };
        var shown = curObj?.DeepClone() as JsonObject;
        string? shownReason = null;
        if (choice.Want != null && !(shown != null && MediaPick.SameAumid(Str(shown["aumid"]), choice.Want))) {
            var s2 = await Ask(new JsonObject { ["aumid"] = choice.Want }).ConfigureAwait(true);
            if (!(s2["ok"] is JsonValue o2 && o2.TryGetValue<bool>(out var b2) && !b2) && s2["session"] is JsonObject ss) shown = (JsonObject)ss.DeepClone();
            else shownReason = Str(s2["sessionReason"]) ?? Str(s2["reason"]) ?? $"the session from {choice.Want} could not be read";
        }
        if (shown != null) {
            string? prot = ProtectedOf(shown);
            shown["protectedApp"] = prot != null;
            if (OwnApp(Str(shown["aumid"])) is { } own) {
                if (shown["app"] is JsonObject had) foreach (var kv in own) had[kv.Key] = kv.Value?.DeepClone();
                else shown["app"] = own;
            }
            shown["controls"] = prot != null && GuardOn() ? Controls(prot) : new JsonObject { ["ok"] = true, ["reason"] = null, ["how"] = null };
        }
        return new JsonObject {
            ["ok"] = true, ["present"] = Bool(snap["present"]),
            ["reason"] = Str(snap["reason"]) ?? shownReason,
            ["rev"] = rev, ["guard"] = guard, ["sessions"] = rows, ["shown"] = shown,
            ["pick"] = shown != null ? PickNote(new JsonObject { ["aumid"] = Str(shown["aumid"]), ["by"] = choice.By }) : null,
            ["events"] = events, ["eventsReason"] = snap["eventsReason"]?.DeepClone(),
        };
    }

    /// mediaPickArm(): a held pick is re-read 250 ms after its grace runs out, so "waiting" never sticks.
    void PickArm(MediaPickWaiting? w)
    {
        _pickTimer?.Dispose(); _pickTimer = null;
        if (w == null || _stopped) return;
        _pickTimer = _o.Host.Delay(TimeSpan.FromMilliseconds(w.LeftMs + 250), () => {
            _pickTimer = null;
            _ = Push($"the picked source ({w.Name ?? w.Aumid}) may have used up its {(long)Math.Round(w.GraceMs / 1000)} s grace");
        });
    }

    /// mediaPush(why): read and publish. Coalesces: triggers during a read cause exactly one more read.
    public Task<JsonObject?> Push(string why)
    {
        if (_reading != null) { _again = true; return _reading; }
        _reading = PushLoop(why);
        return _reading;
    }

    async Task<JsonObject?> PushLoop(string why)
    {
        try {
            do {
                _again = false;
                var p = await ReadAsync().ConfigureAwait(true);
                p["why"] = why;
                _last = p;
                _o.Events.Publish(Event, El(p));
                foreach (var fn in _listeners.ToArray()) {
                    try { fn(p); } catch (Exception e) { _o.Log($"mantle: a media listener threw: {e.Message}"); }
                }
                PollArm(p);
            } while (_again && !_stopped);
        } catch (Exception e) {
            _o.Log($"mantle: the media push ({why}) threw: {e}");
        } finally {
            _reading = null;
        }
        return _last;
    }

    /// mediaPollArm(): the poll runs only while change events are not hooked (10 s) or the last read failed in a way a
    /// retry can fix (5 s); an old exe is never retried.
    void PollArm(JsonObject p)
    {
        bool okFalse = p["ok"] is JsonValue ov && ov.TryGetValue<bool>(out var ob) && !ob;
        bool failedRead = okFalse && !(p["retry"] is JsonValue rv && rv.TryGetValue<bool>(out var rb) && !rb);
        bool need = failedRead || (Bool(p["ok"]) && p["events"] is JsonValue ev && ev.TryGetValue<bool>(out var eb) && !eb);
        if (!need) { _pollTimer?.Dispose(); _pollTimer = null; return; }
        if (_pollTimer != null || _stopped) return;
        _pollTimer = _o.Host.Delay(failedRead ? _o.RetryEvery : _o.PollEvery, () => {
            _pollTimer = null;
            _ = Push(failedRead ? "a retry after a failed media read" : "the slow media poll (no change events)");
        });
    }

    // ------------------------------------------------------------------ the commands

    /// media.select {aumid}: pick a source (null = back to automatic), then read and publish.
    public Task<JsonObject?> Select(string? aumid)
    {
        var known = aumid != null ? (_last?["sessions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => MediaPick.SameAumid(Str(s["aumid"]), aumid)) : null;
        var lite = known != null ? new MediaPickSession(Str(known["aumid"]), Str(known["app"]?["name"]), Bool(known["playing"]), Bool(known["protectedApp"]))
            : new MediaPickSession(aumid, null, false, aumid != null && MediaGuard.Match(Guard(), aumid, null, null) != null);
        _pick = MediaPick.Make(aumid, lite, _o.Host.NowMs);
        _dropped = null;
        return Push(_pick != null ? $"a source was picked in the media card ({_pick.Aumid})" : "the media card went back to automatic");
    }

    /// media.control {aumid, action, positionMs?}. A --test run never sends it: logged as a dry run.
    public async Task<JsonObject> Control(string action, string? aumid, double? positionMs)
    {
        if (!Actions.Contains(action))
            return new JsonObject { ["ok"] = false, ["action"] = action, ["reason"] = $"media.control does not know the action \"{action}\" (it does {string.Join(", ", Actions)})" };
        if (string.IsNullOrEmpty(aumid))
            return new JsonObject { ["ok"] = false, ["action"] = action, ["reason"] = "media.control needs the AUMID of the session the button belongs to, so a press can never land on a different app" };
        if (GuardOn()) {
            var known = (_last?["sessions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(s => MediaPick.SameAumid(Str(s["aumid"]), aumid));
            if (ProtectedOf(known ?? new JsonObject { ["aumid"] = aumid }) is { } prot) {
                var c = Controls(prot);
                return new JsonObject { ["ok"] = false, ["action"] = action, ["aumid"] = aumid, ["reason"] = $"{Str(c["reason"])}. {Str(c["how"])}." };
            }
        }
        var args = new JsonObject { ["op"] = "control", ["action"] = action, ["aumid"] = aumid };
        if (action == "seek") args["positionMs"] = positionMs is double pm && double.IsFinite(pm) ? pm : null;   // JS Number(undefined) = NaN -> null
        if (_o.Test) {
            _o.Log($"media: dry run (--test): {action} for {aumid} would ask the sidecar media {args.ToJsonString()}; nothing was sent");
            if (_fixture != null) FixtureControl(action, aumid);
            _ = Push($"media {action} was sent (dry run)");
            return new JsonObject { ["ok"] = true, ["dryRun"] = true, ["action"] = action, ["aumid"] = aumid, ["reason"] = null };
        }
        var r = await _o.Platform.CallAsync("media", args).ConfigureAwait(true);
        if (!r.Ok)
            return new JsonObject { ["ok"] = false, ["action"] = action, ["aumid"] = aumid, ["reason"] = ProcessPlatform.IsOldExeError(r.Error) ? OldExeControl : r.Error };
        _ = Push($"media {action} was sent");
        return Node(r.Result) as JsonObject ?? new JsonObject { ["ok"] = true, ["action"] = action, ["aumid"] = aumid, ["reason"] = null };
    }

    /// A dry-run toggle in a fixture run moves the made-up session, so a test sees the chip follow (as qs.set's dry run does).
    void FixtureControl(string action, string aumid)
    {
        var s = (_fixture!["sessions"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => MediaPick.SameAumid(Str(x["aumid"]), aumid));
        if (s == null || action is not ("toggle" or "play" or "pause")) return;
        bool play = action == "play" || (action == "toggle" && !Bool(s["playing"]));
        s["playing"] = play;
        s["status"] = play ? "playing" : "paused";
        _fixtureRev++;
    }

    // ------------------------------------------------------------------ helpers

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
    static JsonNode? Node(JsonElement e) => e.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) { _a = a; }
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }
}
