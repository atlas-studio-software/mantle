using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Features.Notifications;

// lib/notifications.js, the bell's half (lane native-r7-notif): main's side of Mantle's notification centre. The list comes
// from the sidecar's `notifications` verb (op list / watch / remove / open); it is shaped with the unread flags and main's
// own notes and published as the "notifications" event (replayed, so the bell is right from its first frame).
//
// WHAT THE BAR MAY DO, checked here and not trusted to the panel: dismiss, dismiss an app's, clear all and open act ONLY on
// ids in the list last published (MaxIds per ask, de-duplicated, integers only). "Clear all" is those ids, one dismissal
// each, never Windows' ClearNotifications().
//
// UNREAD: opening the panel marks everything in it seen; seen ids live in <data>/notifications-seen.json (through the ONE
// write gate, tmp then rename) so a restart does not re-badge old toasts. A file that cannot be read or written is said in
// the payload's seenNote, never swallowed.
//
// --test: Windows' notifications are never read or changed. MANTLE_TEST_NOTIFS (a JSON list of toasts, or a payload
// {toasts, ok, reason, detail, via, fail:{seen, remove, open}}) is the list instead, so every runner draws the same panel;
// without it the list is empty (or, when Windows would not let Mantle read notifications, the same failure the bar's chip
// says). Dismiss, clear and open are DRY RUNS: logged "notifs: dry run (--test): ...", the fixture list loses those ids (as
// Windows' would), nothing is sent.

/// <summary>What the centre needs from the host.</summary>
public sealed class NotificationCentreOptions
{
    public required IPlatform Platform { get; init; }
    public required IEvents Events { get; init; }
    public required HostClock Clock { get; init; }
    public bool Test { get; init; }
    public Action<string>? Log { get; init; }
    /// The write gate's file writer and the seen file (null = seen ids are kept in memory only).
    public SafeFile? Files { get; init; }
    public string? SeenFile { get; init; }
    /// MANTLE_TEST_NOTIFS (test runs only).
    public string? Fixture { get; init; }
    /// A --test run without a fixture: why Windows would not let Mantle read notifications (null = it would).
    public Func<string?>? AccessProblem { get; init; }
    public IShellStateHub? State { get; init; }
}

/// <summary>The "notifications" event's payload (a reference type, so an empty replay is skipped).</summary>
public sealed record NotificationsPayload(JsonElement Body);

/// <summary>main's notification centre (see the file comment). Host thread.</summary>
public sealed class NotificationCentre : IDisposable
{
    public const string Event = "notifications";
    public const int PollMs = 10000, MaxIds = 500;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8), OpenTimeout = TimeSpan.FromSeconds(12);

    /// lib/notifications.js SAY: every word the bar shows is plain; the technical cause goes to the log.
    public static class Say
    {
        public const string OldExe = "This copy of Mantle is missing its notification reader. Updating Mantle fixes it.";
        public const string CannotRead = "Mantle couldn't read your notifications just now. It keeps trying.";
        public const string SeenRead = "Mantle couldn't load which notifications you have already seen, so some may show as new.";
        public const string SeenSave = "Mantle couldn't save which notifications you have seen, so some may show as new after a restart.";
        public const string PlainSource = "Showing text only: pictures and links are not available on this PC right now.";
        public const string Slow = "New notifications may take a few seconds to appear.";
        public const string NotShown = "That notification is no longer in the list (it may have just gone).";
        public const string Gone = "Windows didn't answer in time. Try again.";
        public const string Refused = "Windows didn't let Mantle open it.";
        public const string NotDismissed = "It opened, but it could not be dismissed.";
    }

    static readonly Regex Tech = new(@"sidecar|\bipc\b|hwnd|aumid|0x[0-9a-f]{4,}|\bE[A-Z]{4,}\b|undefined|\bnull\b|NaN|\[object|exception|hresult|winrt|\.(js|cs|exe|dll)\b|npm ", RegexOptions.IgnoreCase);

    /// A sidecar reason is shown only when it reads like a sentence for a person.
    public static string PlainOr(string? reason, string fallback)
    {
        string s = reason == null ? "" : Regex.Replace(reason, @"\s+", " ").Trim();
        return s.Length > 0 && s.Length <= 300 && !Tech.IsMatch(s) ? s : fallback;
    }

    public static bool IsOldExe(string? error)
    {
        string m = (error ?? "").ToLowerInvariant();
        return m.Contains("unknown command") || m.Contains("unknown cmd");
    }

    readonly NotificationCentreOptions _o;
    readonly Action<string> _log;
    readonly HashSet<long> _seen = new();
    readonly List<IDisposable> _offs = new();
    readonly bool _fixture;
    readonly JsonObject? _fail;
    readonly string? _fixReadFail;
    JsonObject? _raw, _last;
    string? _seenNote, _lastLog, _detailLogged, _watchReason;
    bool _watching, _again, _gone, _started;
    IDisposable? _poll;
    TaskCompletionSource<JsonObject?>? _reading;

    public NotificationCentre(NotificationCentreOptions o)
    {
        _o = o;
        _log = o.Log ?? (_ => { });
        if (o.Test && !string.IsNullOrWhiteSpace(o.Fixture)) {
            try {
                var n = JsonNode.Parse(o.Fixture!);
                var obj = n as JsonObject ?? new JsonObject { ["toasts"] = n is JsonArray a ? a.DeepClone() : new JsonArray() };
                if (n is JsonObject) obj = (JsonObject)obj.DeepClone();
                _fail = obj["fail"] as JsonObject;
                obj.Remove("fail");
                if (IsFalse(obj["ok"])) _fixReadFail = Str(obj["reason"]) ?? Say.CannotRead;
                obj["ok"] = true; obj["available"] = true;
                obj["via"] ??= "test fixture";
                obj["rev"] ??= 1;
                if (obj["toasts"] is not JsonArray) obj["toasts"] = new JsonArray();
                foreach (var t in (JsonArray)obj["toasts"]!)
                    if (t is JsonObject to && Id(to) is long id && IsFalse(to["unread"])) _seen.Add(id);
                _raw = obj; _fixture = true;
                Log($"a --test run with MANTLE_TEST_NOTIFS: {((JsonArray)obj["toasts"]!).Count} made-up notifications{(_fixReadFail != null ? " (a list that cannot be read)" : "")}; Windows' own are never read or changed");
            } catch (JsonException e) {
                Log($"MANTLE_TEST_NOTIFS is not JSON ({e.Message}), so the test list is empty");
            }
        }
        if (!_fixture) LoadSeen();
    }

    /// The seen ids are read once, at construction (a fixture run seeds them from the fixture instead).
    void Log(string line) => _log("notifs: " + line);
    void LogOnce(string line) { if (_lastLog != line) { _lastLog = line; Log(line); } }

    /// The last payload published (null before the first read).
    public JsonObject? Last => _last;
    public bool Watching => _watching;
    public string? WatchReason => _watchReason;
    /// Raised after every publish (the toasts feature may listen here instead of reading the list itself).
    public event Action<JsonObject>? Published;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _offs.Add(_o.Events.Replay<NotificationsPayload?>(Event, () => _last != null ? new NotificationsPayload(El(_last)) : null));
        if (_o.Test) {
            if (!_fixture) Log("a --test run never reads Windows' notifications: the list is empty (MANTLE_TEST_NOTIFS gives made-up ones)");
            _o.Clock.Post(() => _ = Push(_fixture ? "the test list" : "a test run"));
            return;
        }
        _offs.Add(_o.Platform.OnReady(() => _o.Clock.Post(() => _ = Arm())));
        _offs.Add(_o.Platform.On("notifications", ev => _o.Clock.Post(() => OnChanged(ev))));
        if (_o.Platform.Status.Ready) _o.Clock.Post(() => _ = Arm());
    }

    void OnChanged(PlatformEvent ev)
    {
        string? rev = ev.Body.ValueKind == JsonValueKind.Object && ev.Body.TryGetProperty("rev", out var r) ? r.GetRawText() : null;
        if (_last != null && rev != null && _last["rev"] is JsonNode lr && lr.ToJsonString() == rev) return;   // the same rev: nothing moved
        _ = Push($"the list changed (rev {rev ?? "?"})");
    }

    /// Armed on EVERY sidecar start: a restarted sidecar has forgotten the watch. Without it, a 10 s poll.
    async Task Arm()
    {
        var r = await _o.Platform.CallAsync("notifications", new JsonObject { ["op"] = "watch", ["on"] = true }, CallTimeout).ConfigureAwait(true);
        if (_gone) return;
        if (r.Ok) {
            var x = r.Result;
            _watching = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("watching", out var w) && w.ValueKind == JsonValueKind.True;
            _watchReason = _watching ? null : (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null)
                ?? "the sidecar did not start its watch and gave no reason";
            if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String) Log($"watch armed without the fast change signal: {d.GetString()}");
        } else {
            _watching = false;
            bool old = IsOldExe(r.Error);
            _watchReason = old ? null : $"the sidecar's watch could not start ({r.Error})";
            if (old) { _ = Push("the sidecar is ready (no notifications command)"); return; }
        }
        if (_watchReason != null) Log($"not watching: {_watchReason}; polling every {PollMs / 1000} s");
        PollArm();
        _ = Push("the sidecar is ready");
    }

    void PollArm()
    {
        _poll?.Dispose(); _poll = null;
        if (_watching || _gone) return;
        _poll = _o.Clock.Later(TimeSpan.FromMilliseconds(PollMs), () => { _poll = null; if (_gone || _watching) return; _ = Push("the fallback poll"); PollArm(); });
    }

    /// Read once and publish; a trigger that lands mid-read gets one more read after it (a burst is one extra read).
    public Task<JsonObject?> Push(string why)
    {
        if (_reading != null) { _again = true; return _reading.Task; }
        var tcs = _reading = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Loop(why, tcs);
        return tcs.Task;
    }

    async Task Loop(string why, TaskCompletionSource<JsonObject?> tcs)
    {
        try {
            do {
                _again = false;
                var p = await ReadAsync().ConfigureAwait(true);
                if (_gone) break;
                Publish(p);
            } while (_again && !_gone);
        } catch (Exception e) {
            Log($"the notifications push ({why}) threw: {e.GetType().Name}: {e.Message}");
        } finally {
            _reading = null;
            tcs.TrySetResult(_last);
        }
    }

    async Task<JsonObject> ReadAsync()
    {
        if (_o.Test) {
            if (_fixture) return _fixReadFail != null ? Shape(null, PlainOr(_fixReadFail, Say.CannotRead)) : Shape(_raw, null);
            string? why;
            try { why = _o.AccessProblem?.Invoke(); } catch (Exception e) { why = "Windows would not say whether Mantle may read notifications (" + e.GetType().Name + ")"; }
            if (why != null) return Shape(null, PlainOr(why, Say.CannotRead));
            _raw = new JsonObject { ["ok"] = true, ["available"] = true, ["via"] = "test run", ["rev"] = 0, ["toasts"] = new JsonArray() };
            return Shape(_raw, null);
        }
        var r = await _o.Platform.CallAsync("notifications", new JsonObject { ["op"] = "list" }, CallTimeout).ConfigureAwait(true);
        if (!r.Ok) {
            bool old = IsOldExe(r.Error);
            LogOnce(old ? "this sidecar exe has no \"notifications\" command yet (run npm run build:sidecar)" : $"the list could not be read: {r.Error}");
            return Shape(null, old ? Say.OldExe : Say.CannotRead);
        }
        if (r.Result.ValueKind != JsonValueKind.Object) { LogOnce("the sidecar answered \"notifications\" with nothing"); return Shape(null, Say.CannotRead); }
        var raw = (JsonObject)JsonNode.Parse(r.Result.GetRawText())!;
        if (IsFalse(raw["ok"]) && (raw["toasts"] is not JsonArray || IsFalse(raw["available"]))) {
            LogOnce($"the list is unavailable: {Str(raw["reason"]) ?? "ok:false with no reason"}");
            return Shape(null, PlainOr(Str(raw["reason"]), Say.CannotRead));
        }
        if (Str(raw["detail"]) is { } det && det != _detailLogged) { _detailLogged = det; Log($"plainer source: {det}"); }
        _lastLog = null;
        _raw = raw;
        return Shape(raw, null);
    }

    /// The bar's payload: the list plus unread flags and main's own notes (lib/notifications.js shape).
    public JsonObject Shape(JsonObject? raw, string? failReason)
    {
        if (failReason != null || raw == null)
            return new JsonObject {
                ["ok"] = false, ["available"] = false, ["reason"] = failReason ?? Say.CannotRead, ["toasts"] = new JsonArray(),
                ["unread"] = 0, ["count"] = 0, ["detail"] = null, ["seenNote"] = _seenNote,
            };
        var toasts = raw["toasts"] as JsonArray ?? new JsonArray();
        var live = new HashSet<long>(toasts.OfType<JsonObject>().Select(Id).Where(i => i != null).Select(i => i!.Value));
        if (_seen.RemoveWhere(id => !live.Contains(id)) > 0) SaveSeen();   // the file never grows past the live list
        var list = new JsonArray();
        int unread = 0;
        foreach (var t in toasts.OfType<JsonObject>()) {
            var c = (JsonObject)t.DeepClone();
            bool u = Id(t) is long id && !_seen.Contains(id);
            c["unread"] = u;
            if (u) unread++;
            list.Add((JsonNode)c);
        }
        string detail = string.Join(" ", new[] { Str(raw["detail"]) != null ? Say.PlainSource : null, _watchReason != null ? Say.Slow : null }.Where(x => x != null));
        return new JsonObject {
            ["ok"] = !IsFalse(raw["ok"]), ["available"] = !IsFalse(raw["available"]),
            ["reason"] = Str(raw["reason"]) is { } why ? PlainOr(why, Say.CannotRead) : null,
            ["access"] = Str(raw["access"]), ["via"] = Str(raw["via"]), ["rev"] = raw["rev"]?.DeepClone(),
            ["count"] = list.Count, ["unread"] = unread, ["toasts"] = list,
            ["detail"] = detail.Length > 0 ? detail : null, ["seenNote"] = _seenNote,
        };
    }

    void Publish(JsonObject p)
    {
        _last = p;
        _o.Events.Publish<NotificationsPayload?>(Event, new NotificationsPayload(El(p)));
        bool ok = !IsFalse(p["ok"]);
        int count = (int)(Int(p["count"]) ?? 0), unread = (int)(Int(p["unread"]) ?? 0);
        string reason = ok ? $"{count} notification{(count == 1 ? "" : "s")}, {unread} unread ({Str(p["via"]) ?? "no source"})" : Str(p["reason"]) ?? Say.CannotRead;
        try { _o.State?.Set(s => s with { Notifications = new NotificationsPiece(ok, count, unread, reason, _watching ? "Windows tells Mantle about new notifications" : _watchReason) }); }
        catch (Exception e) { Log($"the notifications state piece could not be set ({e.Message})"); }
        try { Published?.Invoke(p); } catch (Exception e) { Log($"a listener failed: {e.Message}"); }
    }

    // ------------------------------------------------------------------ seen

    void LoadSeen()
    {
        if (_o.SeenFile == null) return;
        try {
            if (!File.Exists(_o.SeenFile)) return;
            var n = JsonNode.Parse(File.ReadAllText(_o.SeenFile));
            if (n is JsonObject o && o["ids"] is JsonArray a) {
                foreach (var x in a) if (Int(x) is long id) _seen.Add(id);
            } else {
                _seenNote = Say.SeenRead;
                Log("the seen list was not in the expected shape, so it was not used and older ones may show as new");
            }
        } catch (Exception e) {
            _seenNote = Say.SeenRead;
            Log($"the seen list could not be read ({(e is JsonException ? "it is not whole JSON" : e.GetType().Name + ": " + e.Message)}), so older ones may show as new");
        }
    }

    void SaveSeen()
    {
        if (_o.SeenFile == null || _o.Files == null) return;
        var bytes = Encoding.UTF8.GetBytes(new JsonObject { ["ids"] = new JsonArray(_seen.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()) }.ToJsonString());
        string? err = _o.Files.WriteAtomic(_o.SeenFile, bytes, _o.SeenFile + ".tmp");
        if (err == null) { _seenNote = null; return; }
        _seenNote = Say.SeenSave;
        Log($"the seen list could not be saved ({err}), so after a restart they may show as new again");
    }

    // ------------------------------------------------------------------ the bar's asks (notifs.*)

    /// Only ids the list last published may be acted on: the panel is not the guard.
    public List<long> KnownIds(JsonNode? asked)
    {
        var shown = new HashSet<long>((_last?["toasts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(Id).Where(i => i != null).Select(i => i!.Value));
        var ids = new List<long>();
        if (asked is not JsonArray a) return ids;
        foreach (var x in a.Take(MaxIds)) {
            long? id = Int(x) ?? (x is JsonValue v && v.TryGetValue<string>(out var str) && long.TryParse(str, out var p) ? p : null);
            if (id is long k && shown.Contains(k) && !ids.Contains(k)) ids.Add(k);
        }
        return ids;
    }

    public async Task<JsonObject> GetAsync() => _last ?? await Push("the bar asked").ConfigureAwait(true) ?? Shape(null, Say.CannotRead);

    /// The panel opened: everything in it is now seen. Unknown ids are ignored, not stored.
    public JsonObject Seen(JsonNode? ids)
    {
        if (_fixture && Str(_fail?["seen"]) is { } f) return new JsonObject { ["ok"] = false, ["reason"] = f };
        bool changed = false;
        foreach (var id in KnownIds(ids)) changed |= _seen.Add(id);
        if (changed) {
            SaveSeen();
            if (_raw != null && _fixReadFail == null) Publish(Shape(_raw, null));
        }
        return new JsonObject { ["ok"] = true, ["reason"] = null, ["unread"] = (int)(Int(_last?["unread"]) ?? 0) };
    }

    public Task<JsonObject> RemoveAsync(JsonNode? ids) => RemoveIds(KnownIds(ids), "dismiss");

    /// Clear all, or one app's (case-insensitive AUMID): the ids the panel showed, never "whatever Windows has now".
    public Task<JsonObject> ClearAsync(string? aumid)
    {
        aumid = string.IsNullOrEmpty(aumid) ? null : aumid;
        var ids = (_last?["toasts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(t => aumid == null || string.Equals(Str(t["aumid"]) ?? "", aumid, StringComparison.OrdinalIgnoreCase))
            .Select(Id).Where(i => i != null).Select(i => i!.Value).ToList();
        return RemoveIds(ids, aumid != null ? "dismiss that app's notifications" : "clear all");
    }

    static JsonArray Arr(IEnumerable<long> ids) => new(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());

    async Task<JsonObject> RemoveIds(List<long> ids, string what)
    {
        JsonObject Gone() => new() { ["ok"] = false, ["reason"] = Say.Gone, ["removed"] = new JsonArray(), ["missing"] = new JsonArray(),
            ["failed"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["id"] = i, ["reason"] = Say.Gone }).ToArray()) };
        if (ids.Count == 0) return new JsonObject { ["ok"] = false, ["reason"] = Say.NotShown, ["removed"] = new JsonArray(), ["missing"] = new JsonArray(), ["failed"] = new JsonArray() };
        if (_o.Test) {
            if (Str(_fail?["remove"]) is { } f) {
                Log($"dry run (--test): {what}: the test list says Windows kept them ({f})");
                return new JsonObject { ["ok"] = false, ["reason"] = PlainOr(f, "Windows kept some of them."), ["removed"] = new JsonArray(), ["missing"] = new JsonArray(),
                    ["failed"] = new JsonArray(ids.Select(i => (JsonNode)new JsonObject { ["id"] = i, ["reason"] = PlainOr(f, "Windows kept it.") }).ToArray()) };
            }
            Log($"dry run (--test): {what}: would dismiss {ids.Count} notification{(ids.Count == 1 ? "" : "s")} ({string.Join(", ", ids)}) in Windows; nothing was sent, Windows was not changed");
            Drop(ids);
            await Push(what).ConfigureAwait(true);
            return new JsonObject { ["ok"] = true, ["reason"] = null, ["removed"] = Arr(ids), ["missing"] = new JsonArray(), ["failed"] = new JsonArray(), ["dryRun"] = true };
        }
        var r = await _o.Platform.CallAsync("notifications", new JsonObject { ["op"] = "remove", ["ids"] = Arr(ids) }, CallTimeout).ConfigureAwait(true);
        if (!r.Ok) { Log($"{what} failed: {r.Error}"); _ = Push($"{what} failed"); return Gone(); }
        await Push(what).ConfigureAwait(true);
        if (r.Result.ValueKind != JsonValueKind.Object) { Log($"{what}: the sidecar answered with nothing"); return Gone(); }
        var x = (JsonObject)JsonNode.Parse(r.Result.GetRawText())!;
        var failed = new JsonArray();
        foreach (var f in x["failed"] as JsonArray ?? new JsonArray())
            failed.Add((JsonNode)new JsonObject { ["id"] = f is JsonObject fo ? fo["id"]?.DeepClone() : f?.DeepClone(), ["reason"] = PlainOr(f is JsonObject fr ? Str(fr["reason"]) : null, "Windows kept it.") });
        if (failed.Count > 0) Log($"{what}: {failed.Count} of {ids.Count} not dismissed: {Str(x["reason"]) ?? "no reason"}");
        return new JsonObject {
            ["ok"] = !IsFalse(x["ok"]),
            ["reason"] = Str(x["reason"]) is { } why ? PlainOr(why, $"Windows kept {(failed.Count > 0 ? failed.Count.ToString() : "some")} of them.") : null,
            ["removed"] = (x["removed"] as JsonArray)?.DeepClone() ?? new JsonArray(), ["missing"] = (x["missing"] as JsonArray)?.DeepClone() ?? new JsonArray(), ["failed"] = failed,
        };
    }

    void Drop(List<long> ids)
    {
        if (_raw?["toasts"] is not JsonArray a) return;
        foreach (var t in a.OfType<JsonObject>().Where(t => Id(t) is long i && ids.Contains(i)).ToList()) a.Remove(t);
        _raw["rev"] = (Int(_raw["rev"]) ?? 0) + 1;
    }

    /// Click or Enter on a card: a link toast opens its link, otherwise the app starts (the sidecar's `open`).
    public async Task<JsonObject> OpenAsync(JsonNode? id)
    {
        var known = KnownIds(new JsonArray(id?.DeepClone()));
        if (known.Count == 0) return new JsonObject { ["ok"] = false, ["reason"] = Say.NotShown };
        long k = known[0];
        if (_o.Test) {
            var t = (_last?["toasts"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => Id(x) == k);
            string opens = Str(t?["opens"]) ?? "app";
            if (opens == "none") return new JsonObject { ["ok"] = false, ["reason"] = PlainOr(Str(t?["opensWhy"]), Say.Refused) };
            if (Str(_fail?["open"]) is { } f) { Log($"dry run (--test): open {k}: the test list says Windows refused ({f})"); return new JsonObject { ["ok"] = false, ["reason"] = PlainOr(f, Say.Refused) }; }
            Log($"dry run (--test): would open notification {k} ({(opens == "protocol" ? "its link" : "its app")}) and dismiss it; nothing was opened or sent");
            Drop(new List<long> { k });
            _ = Push("opened");
            return new JsonObject { ["ok"] = true, ["via"] = opens, ["note"] = null, ["dismissed"] = true, ["reason"] = null, ["dryRun"] = true };
        }
        var r = await _o.Platform.CallAsync("notifications", new JsonObject { ["op"] = "open", ["toast"] = k }, OpenTimeout).ConfigureAwait(true);
        if (!r.Ok) { Log($"open failed: {r.Error}"); _ = Push("open failed"); return new JsonObject { ["ok"] = false, ["reason"] = Say.Gone }; }
        _ = Push("opened");
        if (r.Result.ValueKind != JsonValueKind.Object) { Log("open: the sidecar answered with nothing"); return new JsonObject { ["ok"] = false, ["reason"] = Say.Gone }; }
        var x = (JsonObject)JsonNode.Parse(r.Result.GetRawText())!;
        bool ok = !IsFalse(x["ok"]);
        if (Str(x["reason"]) is { } why) Log($"open: {(ok ? "note" : "refused")}: {why}");
        return new JsonObject {
            ["ok"] = ok, ["via"] = Str(x["via"]), ["note"] = Str(x["note"]), ["dismissed"] = x["dismissed"] is JsonValue dv && dv.TryGetValue<bool>(out var dd) && dd,
            ["reason"] = Str(x["reason"]) is { } rs ? PlainOr(rs, ok ? Say.NotDismissed : Say.Refused) : null,
        };
    }

    // ------------------------------------------------------------------ helpers

    public static long? Id(JsonObject t) => Int(t["id"]);
    /// A JSON number that is a whole number (whatever .NET type backs the node: a parsed element, an int, a long, a double).
    public static long? Int(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : null;
    public static bool IsFalse(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && !b;
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static JsonElement El(JsonNode o) { using var d = JsonDocument.Parse(o.ToJsonString()); return d.RootElement.Clone(); }

    public void Dispose()
    {
        _gone = true;
        _poll?.Dispose(); _poll = null;
        foreach (var o in _offs) o.Dispose();
        _offs.Clear();
    }
}
