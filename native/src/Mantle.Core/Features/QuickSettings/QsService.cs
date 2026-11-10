using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>The panel's whole picture, as qs.get answers it (the <c>"qs"</c> event's payload).</summary>
public sealed record QsSnapshot(JsonElement Body);

/// <summary>What a qs.set did: ok (with the part read back) or why not.</summary>
public sealed record QsSetReply(bool Ok, string Part, JsonObject? State, JsonNode? Value, string? Reason, bool DryRun, double Ms)
{
    public JsonObject Json() => new() {
        ["ok"] = Ok, ["part"] = Part, ["value"] = Value?.DeepClone(), ["state"] = State?.DeepClone(),
        ["reason"] = Reason, ["dryRun"] = DryRun, ["ms"] = Math.Round(Ms, 1),
    };
}

public sealed class QsOptions
{
    public required IPlatform Platform { get; init; }
    public required IEvents Events { get; init; }
    public required IHostScheduler Host { get; init; }
    /// A --test run: every qs.set is a dry run (logged, never sent to Windows).
    public required bool Test { get; init; }
    public required Action<string> Log { get; init; }
    /// A --test run's made-up readings (MANTLE_TEST_QS, JSON: any of the six parts, plus <c>sessions</c>): read in
    /// place of the sidecar, so a UI test or a screenshot pair sees the same panel on every runner. Ignored outside --test.
    public string? Fixture { get; init; }
}

/// <summary>
/// The host-thread half of quick settings (main.js qsLatest/qsStore/qsRead/qs.get/qs.set/qs.watch/qs.audio.sessions):
/// the latest reading of each part, kept from the sidecar's reads, its pushes and every set's read-back; a set's reply
/// IS its read-back. In a --test run a set is a dry run: it is checked exactly as a live one, logged with what it would
/// have sent, never sent, and the part then reads the asked value marked <c>dryRun</c> (so a panel under test moves),
/// while Windows is not touched. Host thread only.
/// </summary>
public sealed partial class QsService
{
    public const string Event = "qs";
    public const int PartMs = 4000, AudioMs = 6000, LeaseMs = 60000, RenewMs = 20000, PollMs = 2000;
    public const string TestRunWords = "a test run, so Windows was not changed";

    readonly QsOptions _o;
    readonly Dictionary<string, JsonObject> _latest = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _at = new(StringComparer.Ordinal);
    readonly SetLog _setLog;
    readonly JsonObject? _fixture;
    IDisposable? _renew, _poll, _flush;
    bool _watching;
    string? _via;

    public QsService(QsOptions o)
    {
        _o = o;
        _setLog = new SetLog(o.Log, (t, a) => o.Host.Delay(t, a));
        if (o.Test && !string.IsNullOrWhiteSpace(o.Fixture)) {
            try { _fixture = JsonNode.Parse(o.Fixture!) as JsonObject; } catch (JsonException e) { o.Log($"qs: MANTLE_TEST_QS is not JSON ({e.Message}), so the sidecar is read instead"); }
            if (_fixture != null) {
                foreach (var p in QsParts.All)
                    if (_fixture[p.Key] is JsonObject f) Store(p.Key, QsParts.Normalize(f, p.Label), 0, publish: false);
                o.Log("qs: a --test run with MANTLE_TEST_QS: the panel reads the made-up parts (" + string.Join(", ", _latest.Keys) + "), never the sidecar");
            }
        }
        if (_fixture == null) {
            // the sidecar's own watch pushes {ev:"qs", part, state}; its CoreAudio listener pushes {ev:"volume", level, muted}
            o.Platform.On("qs", e => OnPush(e.Body));
            o.Platform.On("volume", e => { if (e.Body.ValueKind == JsonValueKind.Object) Store("volume", QsParts.Normalize(Detach(e.Body, "ev"), "the volume"), _o.Host.NowMs, publish: true); });
            o.Platform.On("btdevices", e => OnBtPush(e.Body));
        }
    }

    public bool UsesFixture => _fixture != null;
    public bool Watching => _watching;
    public IReadOnlyDictionary<string, JsonObject> Latest => _latest;

    /// qs.get: the last whole picture at once when every part has been read (<c>cached: true</c>), else a full read.
    public async ValueTask<JsonObject> GetAsync()
    {
        bool all = true;
        foreach (var p in QsParts.All) if (!_latest.ContainsKey(p.Key)) { all = false; break; }
        if (all || _fixture != null) { var c = QsParts.Compose(_latest); c["cached"] = true; return c; }
        return await ReadAllAsync().ConfigureAwait(true);
    }

    /// qsRead: all six parts at once, never throwing; a read that started before a push is older than the push and
    /// does not overwrite it (qsStore's `at`).
    public async ValueTask<JsonObject> ReadAllAsync()
    {
        if (_fixture != null) return QsParts.Compose(_latest);
        double started = _o.Host.NowMs;
        var tasks = new List<Task<JsonObject>>();
        foreach (var p in QsParts.All) tasks.Add(ReadPartAsync(p).AsTask());
        await Task.WhenAll(tasks).ConfigureAwait(true);
        for (int i = 0; i < QsParts.All.Count; i++) Store(QsParts.All[i].Key, tasks[i].Result, started, publish: false);
        var snap = QsParts.Compose(_latest);
        Publish();
        return snap;
    }

    /// qsPart: one part, a refusal or a timeout as {ok:false, reason}.
    public async ValueTask<JsonObject> ReadPartAsync(QsPart p, JsonObject? args = null)
    {
        var r = await _o.Platform.CallAsync(p.Verb, args ?? new JsonObject(), TimeSpan.FromMilliseconds(PartMs)).ConfigureAwait(true);
        if (!r.Ok) return QsParts.Fail($"{p.Label} cannot be read: {r.Error ?? r.Outcome.ToString()}");
        return QsParts.Normalize(Node(r.Result), p.Label);
    }

    /// <summary>
    /// qs.set {part, value}: checked, sent (or, in a --test run, only logged), and answered with the part READ BACK.
    /// A switch Windows refused comes back as ok:false with Windows' reason, and the part's read-back still lands in the
    /// panel (so a tile springs back). Every call is one line in the log (SetLog; a dragged slider's stream summed).
    /// </summary>
    public async ValueTask<QsSetReply> SetAsync(string part, JsonElement value)
    {
        var sw = Stopwatch.StartNew();
        QsSetReply Done(QsSetReply r)
        {
            r = r with { Ms = sw.Elapsed.TotalMilliseconds };
            _setLog.Record(part, QsParts.Fmt(value), r.Ok, r.Ok ? QsParts.Fmt(r.Value) : null, r.Reason, r.Ms);
            return r;
        }
        if (!QsParts.Writes.TryGetValue(part ?? "", out var w))
            return Done(new(false, part ?? "", null, null, $"qs.set does not know the part \"{part}\" (it does {QsParts.KnownWrites})", false, 0));
        if (w.Check(value) is string bad) return Done(new(false, part!, null, null, bad, false, 0));

        if (_o.Test) {
            _latest.TryGetValue(w.Key, out var before);
            var after = QsParts.DryRun(w, before, value, out var refused);
            string sent = $"{w.Verb} {w.Args(value).ToJsonString()}";
            if (after == null) {
                _o.Log($"qs: dry run (--test): {part}={QsParts.Fmt(value)} refused before anything was sent: {refused}");
                return Done(new(false, part!, before, null, refused, true, 0));
            }
            _o.Log($"qs: dry run (--test): {part}={QsParts.Fmt(value)} would ask the sidecar {sent}; nothing was sent, Windows was not changed");
            Store(w.Key, after, _o.Host.NowMs, publish: true);
            return Done(new(true, part!, after, w.Read(after), null, true, 0));
        }

        var r = await _o.Platform.CallAsync(w.Verb, w.Args(value), TimeSpan.FromMilliseconds(PartMs)).ConfigureAwait(true);
        if (!r.Ok) return Done(new(false, part!, null, null, $"{w.Label} could not be changed: {r.Error ?? r.Outcome.ToString()}", false, 0));
        var set = Node(r.Result) as JsonObject;
        bool refusedBy = set != null && QsParts.IsFalse(set["ok"]);
        JsonObject now;
        if (!refusedBy) now = QsParts.Normalize(set, w.Label);
        else if (set!["read"] is JsonObject read) now = QsParts.Normalize(read, w.Label);
        else now = await ReadPartAsync(QsParts.Find(w.Key)!).ConfigureAwait(true);
        Store(w.Key, now, _o.Host.NowMs, publish: true);
        bool ok = !refusedBy && QsParts.IsOk(now);
        string? reason = refusedBy ? (QsParts.Str(set!["reason"]) ?? $"{w.Label} was not changed, and the sidecar gave no reason")
            : ok ? null : QsParts.Str(now["reason"]);
        return Done(new(ok, part!, now, QsParts.IsOk(now) ? w.Read(now) : null, reason, false, 0));
    }

    /// <summary>
    /// qs.watch {on}: while the panel is open the sidecar's own watch pushes each part as it changes, on a lease this
    /// renews every 20 s (a host that dies costs the sidecar at most 60 s of reads); a sidecar that cannot watch gets
    /// the full re-read every 2 s instead. A --test run with made-up parts has nothing to watch and says so.
    /// </summary>
    public async ValueTask<JsonObject> WatchAsync(bool on, string why)
    {
        if (!on) return Unwatch(why);
        if (_fixture != null) { _watching = true; _via = "fixture"; return WatchReply(true, "the parts are MANTLE_TEST_QS's, so there is nothing to watch"); }
        bool was = _watching;
        _watching = true;
        await LeaseAsync(why).ConfigureAwait(true);
        return WatchReply(!was, null);
    }

    JsonObject WatchReply(bool started, string? reason) => new() { ["ok"] = true, ["watching"] = _watching, ["started"] = started, ["via"] = _via, ["reason"] = reason };

    async ValueTask LeaseAsync(string why)
    {
        if (!_watching) return;
        _renew?.Dispose();
        _renew = _o.Host.Delay(TimeSpan.FromMilliseconds(RenewMs), () => { _renew = null; _ = LeaseAsync("the lease is renewed"); });
        var r = await _o.Platform.CallAsync("qswatch", new JsonObject { ["on"] = true, ["leaseMs"] = LeaseMs }, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
        if (!_watching) return;
        if (r.Ok) {
            if (_via != "push") _o.Log($"qs: the sidecar pushes each change ({why})");
            _via = "push";
            _poll?.Dispose(); _poll = null;
        } else {
            if (_via != "poll") _o.Log($"qs: the sidecar's quick-settings watch did not start ({r.Error ?? r.Outcome.ToString()}), so the panel re-reads every {PollMs / 1000} s instead");
            _via = "poll";
            if (_poll == null) Poll();
        }
    }

    void Poll()
    {
        _poll = _o.Host.Delay(TimeSpan.FromMilliseconds(PollMs), async () => {
            if (!_watching || _via != "poll") { _poll = null; return; }
            await ReadAllAsync().ConfigureAwait(true);
            if (_watching && _via == "poll") Poll();
        });
    }

    JsonObject Unwatch(string why)
    {
        bool was = _watching;
        _watching = false;
        _renew?.Dispose(); _renew = null;
        _poll?.Dispose(); _poll = null;
        if (was && _fixture == null) {
            _ = _o.Platform.CallAsync("qswatch", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500));
            _o.Log($"qs: the watch stopped ({why})");
        }
        _via = null;
        return WatchReply(false, null);
    }

    /// <summary>
    /// qs.audio.sessions: every app playing sound (the sidecar's <c>audio {op:"sessions"}</c>), the sidecar's own
    /// ok/reason winning. Icons are not read by this build yet: each row says so in <c>iconReason</c>.
    /// </summary>
    public async ValueTask<JsonObject> AudioSessionsAsync()
    {
        JsonObject o;
        if (_fixture != null) {
            o = _fixture["sessions"] is JsonArray a ? new JsonObject { ["ok"] = true, ["reason"] = null, ["sessions"] = a.DeepClone() }
                : new JsonObject { ["ok"] = true, ["reason"] = null, ["sessions"] = new JsonArray() };
        } else {
            var r = await _o.Platform.CallAsync("audio", new JsonObject { ["op"] = "sessions" }, TimeSpan.FromMilliseconds(AudioMs)).ConfigureAwait(true);
            if (!r.Ok) return new JsonObject { ["ok"] = false, ["reason"] = $"the apps playing sound cannot be read: {r.Error ?? r.Outcome.ToString()}" };
            o = new JsonObject { ["ok"] = true, ["reason"] = null };
            if (Node(r.Result) is JsonObject got) foreach (var kv in got) o[kv.Key] = kv.Value?.DeepClone();
        }
        if (o["sessions"] is JsonArray rows)
            foreach (var row in rows)
                if (row is JsonObject s && !s.ContainsKey("icon")) {
                    bool system = QsParts.IsTrue(s["system"]);
                    s["icon"] = null;
                    s["iconReason"] = system ? null : string.IsNullOrEmpty(QsParts.Str(s["path"])) ? "no icon: Windows gave no program path for this app" : "no icon: this build does not read app icons for this list yet";
                }
        return o;
    }

    /// qs.audio.session {key, level?, muted?} (main.js): one app's volume or mute. A --test run logs what it would send
    /// and answers dryRun without sending it (the sidecar's audio verb is not refused by a passive sidecar).
    public async ValueTask<JsonObject> AudioSessionAsync(JsonElement a)
    {
        string? key = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (string.IsNullOrEmpty(key)) return QsParts.Fail("qs.audio.session needs key: a row's key from qs.audio.sessions");
        var args = new JsonObject { ["op"] = "session", ["key"] = key };
        if (a.TryGetProperty("level", out var lv)) args["level"] = QsParts.Number(lv);
        if (a.TryGetProperty("muted", out var mu)) args["muted"] = JsonNode.Parse(mu.GetRawText());
        if (_o.Test) {
            _o.Log($"qs: dry run (--test): an app's volume would ask the sidecar audio {args.ToJsonString()}; nothing was sent");
            return new JsonObject { ["ok"] = true, ["dryRun"] = true, ["reason"] = TestRunWords, ["key"] = key };
        }
        var r = await _o.Platform.CallAsync("audio", args, TimeSpan.FromMilliseconds(AudioMs)).ConfigureAwait(true);
        if (!r.Ok) return QsParts.Fail($"that app's volume could not be changed: {r.Error ?? r.Outcome.ToString()}");
        var o = new JsonObject { ["ok"] = true, ["reason"] = null };
        if (Node(r.Result) is JsonObject got) foreach (var kv in got) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    // ---- the store (main.js qsStore): a part's newest reading; `at` is when it was ASKED FOR

    void OnPush(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return;
        string? key = body.TryGetProperty("part", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (key == null) {
            string why = body.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString()! : "no reason given";
            _o.Log("qs: the sidecar's watch said: " + why);
            return;
        }
        var p = QsParts.Find(key);
        if (p == null || !body.TryGetProperty("state", out var st)) return;
        Store(key, QsParts.Normalize(Node(st), p.Label), _o.Host.NowMs, publish: true);
    }

    bool Store(string key, JsonObject state, double at, bool publish)
    {
        if (_at.TryGetValue(key, out var held) && at < held) return false;
        _at[key] = at;
        _latest[key] = state;
        if (publish) PublishSoon();
        return true;
    }

    /// The open panel hears a burst in one go (main.js broadcasts 8 ms after the first store).
    void PublishSoon()
    {
        if (_flush != null) return;
        _flush = _o.Host.Delay(TimeSpan.FromMilliseconds(8), () => { _flush = null; Publish(); });
    }

    void Publish() => _o.Events.Publish(Event, new QsSnapshot(El(QsParts.Compose(_latest))));

    public void FlushLog() => _setLog.FlushAll();

    // ---- JSON helpers

    public static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

    static JsonNode? Node(JsonElement e) => e.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());

    static JsonObject Detach(JsonElement e, string drop)
    {
        var o = JsonNode.Parse(e.GetRawText()) as JsonObject ?? new JsonObject();
        o.Remove(drop);
        return o;
    }
}
