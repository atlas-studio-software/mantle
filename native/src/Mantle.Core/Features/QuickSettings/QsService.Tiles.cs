using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>
/// Quick settings' second tile row (lib/features/qstiles.js, the Project and Cast half of lib/features/project.js):
/// Airplane mode, Do not disturb, Focus, Project, Cast. The bar holds the last state and paints from it; it asks
/// <c>qs.tiles.refresh</c> after the paint, never before. The reads are the sidecar's own (sysnotify.dndGet, netair.get,
/// toasts.quiet, sysdisplay.project), all read only.
/// A --test run never changes the runner: every switch (Do not disturb, airplane mode, Focus) and every way out (the
/// Project choices, Windows' Cast panel) is a DRY RUN, logged with what it would have asked and never sent; the reply
/// is ok:true + dryRun with Electron's "test run: would ..." words (the tile's note), and the state is not changed.
/// MANTLE_TEST_QS may carry <c>qstiles</c> ({dnd, airplane, focus, timer}) and <c>project</c> ({on, read}), read in
/// place of the sidecar so every runner draws the same row.
/// Focus runs on Mantle's timer, which the native app does not have yet: outside a fixture the tile is dead with that
/// reason (Electron's words when its timer feature is off), so a live Focus never starts.
/// </summary>
public sealed partial class QsService
{
    public const string TilesEvent = "qs.tiles";
    public const int DndGetMs = 4000, DndSetMs = 6000, AirGetMs = 8000, AirSetMs = 8000, QuietMs = 2000, ProjectMs = 4000, KeysMs = 3000;
    public const string NoTimer = "Mantle’s timer is not in the native app yet, so Focus has nothing to run on";
    public const string HelperDown = "Mantle's helper did not answer. Try again in a moment";

    JsonObject? _tDnd, _tAir, _tFocus, _tTimer, _tRead;
    string? _tReadError, _tProjError;
    double _tReadAt;
    bool _tInit;
    readonly HashSet<string> _tBusy = new(StringComparer.Ordinal);
    Task? _tReading;

    void TilesInit()
    {
        if (_tInit) return;
        _tInit = true;
        if (_fixture == null) return;
        if (_fixture["qstiles"] is JsonObject q) {
            _tDnd = q["dnd"]?.DeepClone() as JsonObject;
            _tAir = q["airplane"]?.DeepClone() as JsonObject;
            _tFocus = q["focus"]?.DeepClone() as JsonObject;
            _tTimer = q["timer"]?.DeepClone() as JsonObject;
            _tReadAt = 1;
        } else _tReadError = "MANTLE_TEST_QS has no \"qstiles\", and a test run with made-up parts never reads the sidecar";
        if (_fixture["project"] is JsonObject p) _tRead = p["read"]?.DeepClone() as JsonObject;
        else _tProjError = "MANTLE_TEST_QS has no \"project\"";
    }

    /// qstiles.state + project.state, folded into one.
    public JsonObject TilesState()
    {
        TilesInit();
        var busy = new JsonArray();
        foreach (var b in _tBusy) busy.Add(JsonValue.Create(b));   // the non-generic Add (the AOT build refuses Add<T>)
        return new JsonObject {
            ["on"] = true,
            ["dnd"] = _tDnd?.DeepClone(), ["airplane"] = _tAir?.DeepClone(), ["focus"] = _tFocus?.DeepClone(),
            ["timer"] = _tTimer?.DeepClone() ?? new JsonObject { ["ok"] = false, ["reason"] = NoTimer },
            ["project"] = new JsonObject { ["on"] = true, ["read"] = _tRead?.DeepClone(), ["readError"] = _tProjError },
            ["readAt"] = _tReadAt, ["readError"] = _tReadError, ["busy"] = busy, ["test"] = _o.Test,
        };
    }

    void TilesPublish() => _o.Events.Publish(TilesEvent, El(TilesState()));

    /// qstiles.js plainErr(): the helper's own jargon becomes plain words.
    public static string Plain(string? e)
    {
        string m = (e ?? "no reason given").Split('\n')[0];
        return Regex.IsMatch(m, @"sidecar|ipc|timed? ?out|undefined|\bnull\b|passive", RegexOptions.IgnoreCase) || Regex.IsMatch(m, @"\bE[A-Z]{3,}\b") ? HelperDown : m;
    }

    /// qstiles.refresh (+ project.refresh): the four reads at once, one round at a time; with made-up parts nothing is read.
    public async ValueTask<JsonObject> TilesRefreshAsync(string why)
    {
        TilesInit();
        if (_fixture != null) return TilesState();
        if (_tReading != null) { await _tReading.ConfigureAwait(true); return TilesState(); }
        var t = TilesReadAsync(why);
        _tReading = t;
        try { await t.ConfigureAwait(true); } finally { _tReading = null; }
        TilesPublish();
        return TilesState();
    }

    async Task TilesReadAsync(string why)
    {
        double t0 = _o.Host.NowMs;
        var dT = _o.Platform.CallAsync("sysnotify.dndGet", new JsonObject(), TimeSpan.FromMilliseconds(DndGetMs)).AsTask();
        var aT = _o.Platform.CallAsync("netair.get", new JsonObject(), TimeSpan.FromMilliseconds(AirGetMs)).AsTask();
        var qT = _o.Platform.CallAsync("toasts.quiet", new JsonObject(), TimeSpan.FromMilliseconds(QuietMs)).AsTask();
        var pT = _o.Platform.CallAsync("sysdisplay.project", new JsonObject(), TimeSpan.FromMilliseconds(ProjectMs)).AsTask();
        await Task.WhenAll(dT, aT, qT, pT).ConfigureAwait(true);
        var errs = new List<string>();
        var d = dT.Result; var a = aT.Result; var q = qT.Result; var p = pT.Result;
        if (d.Ok && Node(d.Result) is JsonObject x) {
            var qr = q.Ok ? Node(q.Result) as JsonObject : null;
            double qd = QsParts.Num(qr?["dnd"]) ?? 0;
            bool active = qd == 1 || qd == 2, on = QsParts.IsTrue(x["on"]);
            _tDnd = new JsonObject {
                ["ok"] = !QsParts.IsFalse(x["ok"]), ["on"] = x["on"] == null ? (JsonNode?)null : JsonValue.Create(on), ["writable"] = !QsParts.IsFalse(x["writable"]),
                ["reason"] = QsParts.Str(x["reason"]), ["rule"] = !on && active ? (qd == 2 ? "alarms only" : "priority only") : null,
            };
        } else { _tDnd = new JsonObject { ["ok"] = false, ["on"] = null, ["writable"] = false, ["reason"] = Plain(d.Error) }; errs.Add("Do not disturb: " + (d.Error ?? "no answer")); }
        if (a.Ok && Node(a.Result) is JsonObject y) {
            _tAir = new JsonObject {
                ["ok"] = !QsParts.IsFalse(y["ok"]), ["on"] = y["airplane"] == null ? (JsonNode?)null : JsonValue.Create(QsParts.IsTrue(y["airplane"])),
                ["canSet"] = QsParts.IsTrue(y["canSet"]), ["reason"] = QsParts.Str(y["reason"]),
            };
        } else { _tAir = new JsonObject { ["ok"] = false, ["on"] = null, ["canSet"] = false, ["reason"] = Plain(a.Error) }; errs.Add("airplane mode: " + (a.Error ?? "no answer")); }
        if (p.Ok && Node(p.Result) is JsonObject z) { _tRead = z; _tProjError = null; }
        else { _tRead = null; _tProjError = Plain(p.Error); }
        _tReadAt = _o.Host.NowMs;
        _tReadError = errs.Count > 0 ? string.Join("; ", errs) : null;
        if (errs.Count > 0) _o.Log($"qs: the tiles were read ({why}) in {Math.Round(_tReadAt - t0)} ms with problems: {_tReadError}");
    }

    JsonObject TileReply(string tile, bool ok, string? reason, bool dry = false) => new() {
        ["tile"] = tile, ["ok"] = ok, ["reason"] = reason, ["dryRun"] = dry, ["state"] = TilesState(),
    };

    void TileDryRun(string what, string verb, JsonObject args)
        => _o.Log($"qs: dry run (--test): {what} would ask the sidecar {verb} {args.ToJsonString()}; nothing was sent, Windows was not changed");

    /// qstiles.set {tile, on}: Do not disturb, airplane mode or Focus. Always an answer {tile, ok, reason, dryRun, state}.
    public async ValueTask<JsonObject> TilesSetAsync(string tile, bool on)
    {
        TilesInit();
        if (tile is not ("dnd" or "airplane" or "focus")) return TileReply(tile, false, $"There is no \"{tile}\" tile");
        if (_tBusy.Contains(tile)) return TileReply(tile, false, "Still switching: wait a moment");
        _tBusy.Add(tile);
        TilesPublish();
        JsonObject r;
        try {
            r = tile switch {
                "dnd" => await SetDndAsync(on, "its tile").ConfigureAwait(true),
                "airplane" => await SetAirplaneAsync(on).ConfigureAwait(true),
                _ => on ? StartFocus() : EndFocus("its tile"),
            };
        } catch (Exception e) { r = TileReply(tile, false, Plain(e.Message)); }
        _tBusy.Remove(tile);
        bool dry = QsParts.IsTrue(r["dryRun"]);
        if (!dry && _fixture == null) _ = TilesRefreshAsync("after a switch");
        TilesPublish();
        r["state"] = TilesState();
        return r;
    }

    async ValueTask<JsonObject> SetDndAsync(bool on, string why)
    {
        var args = new JsonObject { ["on"] = on };
        if (_o.Test) {
            TileDryRun("Do not disturb " + (on ? "on" : "off"), "sysnotify.dnd", args);
            return TileReply("dnd", true, $"test run: would turn Do not disturb {(on ? "on" : "off")} ({why})", dry: true);
        }
        var r = await _o.Platform.CallAsync("sysnotify.dnd", args, TimeSpan.FromMilliseconds(DndSetMs)).ConfigureAwait(true);
        if (!r.Ok) return TileReply("dnd", false, "Do not disturb did not change: " + Plain(r.Error));
        var got = Node(r.Result) as JsonObject;
        var d = (JsonObject?)_tDnd?.DeepClone() ?? new JsonObject { ["ok"] = true, ["writable"] = true };
        d["on"] = got != null && got["on"] != null ? QsParts.IsTrue(got["on"]) : on;
        d["rule"] = null;
        _tDnd = d;
        // turning it off by hand while Focus holds it: Focus keeps its timer but no longer owns Do not disturb
        if (!on && _tFocus != null && QsParts.IsTrue(_tFocus["dndByFocus"])) _tFocus["dndByFocus"] = false;
        _o.Log($"qs: Do not disturb {(on ? "on" : "off")} ({why})");
        return TileReply("dnd", true, null);
    }

    async ValueTask<JsonObject> SetAirplaneAsync(bool on)
    {
        var args = new JsonObject { ["on"] = on };
        if (_o.Test) {
            TileDryRun("airplane mode " + (on ? "on" : "off"), "netair.set", args);
            bool now = QsParts.IsTrue(_tAir?["on"]);
            string would = now == on ? "change nothing: airplane mode is already " + (on ? "on" : "off")
                : on ? "turn airplane mode on (Wi-Fi, Bluetooth and mobile data go off)" : "turn airplane mode off (radios go back to how they were)";
            return TileReply("airplane", true, "test run: would " + would, dry: true);
        }
        var r = await _o.Platform.CallAsync("netair.set", args, TimeSpan.FromMilliseconds(AirSetMs)).ConfigureAwait(true);
        if (!r.Ok) return TileReply("airplane", false, "Airplane mode did not change: " + Plain(r.Error));
        var got = Node(r.Result) as JsonObject;
        bool now2 = got != null && got["now"] != null ? QsParts.IsTrue(got["now"]) : on;
        var a = (JsonObject?)_tAir?.DeepClone() ?? new JsonObject { ["ok"] = true, ["canSet"] = true };
        a["on"] = now2;
        _tAir = a;
        _o.Log($"qs: airplane mode {(now2 ? "on" : "off")}");
        return TileReply("airplane", now2 == on, now2 == on ? null : "Windows still reports airplane mode " + (now2 ? "on" : "off"));
    }

    JsonObject TimerNow() => _tTimer ?? new JsonObject { ["ok"] = false, ["reason"] = NoTimer };

    /// startFocus(): in a --test run only what it would do. Live it is refused before anything is touched, since the
    /// timer it runs on is not in the native app yet (TimerNow).
    JsonObject StartFocus()
    {
        var t = TimerNow();
        if (QsParts.IsFalse(t["ok"])) return TileReply("focus", false, "Focus did not start: " + (QsParts.Str(t["reason"]) ?? NoTimer));
        bool dndWasOn = QsParts.IsTrue(_tDnd?["on"]);
        bool canQuiet = _tDnd != null && !QsParts.IsFalse(_tDnd["ok"]) && !QsParts.IsFalse(_tDnd["writable"]);
        bool quiet = !dndWasOn && canQuiet;
        if (_o.Test) {
            _o.Log($"qs: dry run (--test): Focus would start a {QsTiles.FocusMin} min timer labelled \"Focus\"" +
                   (quiet ? " and ask the sidecar sysnotify.dnd {\"on\":true}" : "") + "; nothing was started, Windows was not changed");
            return TileReply("focus", true, $"test run: would start a {QsTiles.FocusMin} min Focus timer" + (quiet ? " and turn Do not disturb on" : ""), dry: true);
        }
        return TileReply("focus", false, "Focus did not start: " + NoTimer);
    }

    JsonObject EndFocus(string why)
    {
        var f = _tFocus;
        if (f == null || !QsParts.IsTrue(f["on"])) return TileReply("focus", true, null);
        bool byFocus = QsParts.IsTrue(f["dndByFocus"]);
        if (_o.Test) {
            _o.Log($"qs: dry run (--test): Focus would end ({why})" + (byFocus ? " and ask the sidecar sysnotify.dnd {\"on\":false}" : "") +
                   "; nothing was stopped, Windows was not changed");
            return TileReply("focus", true, "test run: would end Focus" + (byFocus ? " and turn Do not disturb back off" : ""), dry: true);
        }
        _tFocus = null;
        return TileReply("focus", true, null);
    }

    /// project.open / project.cast {tile}: Windows' own Project choices (Win+P) or Cast panel (Win+K) through the
    /// sidecar's keys verb. A --test run opens nothing: it says what it would open.
    public async ValueTask<JsonObject> TilesOpenAsync(string tile)
    {
        if (tile is not ("project" or "cast")) return TileReply(tile, false, $"There is no \"{tile}\" tile to open");
        bool cast = tile == "cast";
        string what = cast ? "Windows' Cast panel" : "Windows' Project choices", combo = cast ? "win+k" : "win+p", chord = cast ? "Win+K" : "Win+P";
        if (_o.Test) {
            _o.Log($"qs: dry run (--test): would open {what} (sidecar keys {combo}); nothing was opened");
            return TileReply(tile, true, $"test run: would open {what} ({chord})", dry: true);
        }
        var r = await _o.Platform.CallAsync("keys", new JsonObject { ["combo"] = combo }, TimeSpan.FromMilliseconds(KeysMs)).ConfigureAwait(true);
        _o.Log($"qs: {tile}: {(r.Ok ? $"handed to {what} ({chord})" : r.Error)}");
        return r.Ok ? TileReply(tile, true, null) : TileReply(tile, false, $"{what} did not open ({Plain(r.Error)}); press {chord}");
    }
}
