using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Notifications;

// The bar's notification area (lane native-r7-notif): main.js startTrayPoll / trayForBar / 'tray.list' / 'tray.click' and
// 'sys.keys'. The list is the sidecar's UI Automation tray ({id, title, icon, exe, promoted, clickable, reason}) minus
// Mantle's own icon, read every 5 s (once a minute while it cannot be read) and published as the "tray" event
// {status: ok|unavailable, reason, items} (replayed), only when it changed.
//
// --test: the runner's tray is never read or clicked. MANTLE_TEST_TRAY (a JSON list of icons, or {status, reason, items})
// is the list; without it the strip is empty and says why. A click is a DRY RUN: "tray: dry run (--test): would
// <button>-click the notification-area icon <id>; nothing was sent". The log never carries an icon's tooltip text (an app's
// tooltip can carry an account or network name). sys.keys presses only win+a, win+n, win+d, and never in a test run.

/// <summary>The "tray" event's payload.</summary>
public sealed record TrayPayload(JsonElement Body);

public sealed class NotificationAreaOptions
{
    public required IPlatform Platform { get; init; }
    public required IEvents Events { get; init; }
    public required HostClock Clock { get; init; }
    public bool Test { get; init; }
    public Action<string>? Log { get; init; }
    /// MANTLE_TEST_TRAY (test runs only).
    public string? Fixture { get; init; }
    /// This process's exe: its own tray icon is left out (main.js isMantleTrayIcon).
    public string? OwnExe { get; init; }
}

public sealed class NotificationArea : IDisposable
{
    public const string Event = "tray";
    public const int PollMs = 5000, BackoffMs = 60000;
    public static readonly string[] KeysAllowed = { "win+a", "win+n", "win+d" };
    static readonly Regex MantleElectron = new(@"[\\/]mantle(?:-[^\\/]*)?[\\/]node_modules[\\/]electron[\\/]dist[\\/]electron\.exe$", RegexOptions.IgnoreCase);
    static readonly Regex MantleTitle = new(@"^Mantle \((live|test)\)");

    readonly NotificationAreaOptions _o;
    readonly Action<string> _log;
    readonly List<IDisposable> _offs = new();
    JsonObject _now;
    string _sig = "";
    IDisposable? _timer;
    bool _gone, _started;

    public NotificationArea(NotificationAreaOptions o)
    {
        _o = o; _log = o.Log ?? (_ => { });
        _now = new JsonObject { ["status"] = "ok", ["reason"] = "not read yet", ["items"] = new JsonArray() };
    }

    public JsonObject Now => _now;

    /// main.js isMantleTrayIcon: Electron Mantle's own icon, this process's, or a "Mantle (live|test)" tooltip.
    public static bool IsMantleIcon(JsonObject t, string? ownExe)
    {
        string exe = NotificationCentre.Str(t["exe"]) ?? "";
        if (exe.Length > 0 && (MantleElectron.IsMatch(exe) || (ownExe != null && string.Equals(Full(exe), Full(ownExe), StringComparison.OrdinalIgnoreCase)))) return true;
        return MantleTitle.IsMatch(NotificationCentre.Str(t["title"]) ?? NotificationCentre.Str(t["tooltip"]) ?? "");
    }
    static string Full(string p) { try { return System.IO.Path.GetFullPath(p); } catch (Exception) { return p; } }

    public static JsonArray ForBar(JsonNode? list, string? ownExe) =>
        new((list as JsonArray ?? new JsonArray()).OfType<JsonObject>().Where(t => !IsMantleIcon(t, ownExe)).Select(t => (JsonNode)t.DeepClone()).ToArray());

    public void Start()
    {
        if (_started) return;
        _started = true;
        _offs.Add(_o.Events.Replay<TrayPayload?>(Event, () => new TrayPayload(NotificationCentre.El(_now))));
        if (_o.Test) { _o.Clock.Post(TestList); return; }
        _o.Clock.Post(() => _ = Tick());
    }

    void TestList()
    {
        if (string.IsNullOrWhiteSpace(_o.Fixture)) {
            Set(new JsonObject { ["status"] = "ok", ["reason"] = "a test run shows no tray icons: it never reads the runner's own (MANTLE_TEST_TRAY gives made-up ones)", ["items"] = new JsonArray() });
            _log("tray: " + NotificationCentre.Str(_now["reason"]));
            return;
        }
        try {
            var n = JsonNode.Parse(_o.Fixture!);
            var o = n is JsonObject obj ? (JsonObject)obj.DeepClone() : new JsonObject { ["status"] = "ok", ["items"] = n is JsonArray a ? a.DeepClone() : new JsonArray() };
            o["items"] = ForBar(o["items"], _o.OwnExe);
            o["status"] = NotificationCentre.Str(o["status"]) == "unavailable" ? "unavailable" : "ok";
            o["reason"] ??= $"test mode: {((JsonArray)o["items"]!).Count} made-up notification-area items (MANTLE_TEST_TRAY)";
            Set(o);
            _log($"tray: a --test run with MANTLE_TEST_TRAY: {((JsonArray)o["items"]!).Count} made-up icons ({o["status"]}); the runner's own tray is never read or clicked");
        } catch (JsonException e) {
            Set(new JsonObject { ["status"] = "unavailable", ["reason"] = $"MANTLE_TEST_TRAY is not JSON ({e.Message})", ["items"] = new JsonArray() });
        }
    }

    async Task Tick()
    {
        if (_gone) return;
        int delay = PollMs;
        var r = await _o.Platform.CallAsync("tray", null, TimeSpan.FromSeconds(8)).ConfigureAwait(true);
        if (_gone) return;
        if (r.Ok) {
            var items = ForBar(r.Result.ValueKind == JsonValueKind.Array ? JsonNode.Parse(r.Result.GetRawText()) : null, _o.OwnExe);
            Set(new JsonObject { ["status"] = "ok", ["reason"] = $"{items.Count} notification-area items", ["items"] = items });
        } else {
            delay = BackoffMs;   // a tray that cannot be read will not change by asking twelve times a minute
            Set(new JsonObject { ["status"] = "unavailable", ["reason"] = $"{r.Error}; retrying once a minute", ["items"] = (_now["items"] as JsonArray)?.DeepClone() ?? new JsonArray() });
        }
        _timer = _o.Clock.Later(TimeSpan.FromMilliseconds(delay), () => _ = Tick());
    }

    void Set(JsonObject o)
    {
        string sig = o.ToJsonString();
        _now = o;
        if (sig == _sig) return;   // the bar redraws only what moved; nothing is pushed while nothing changes
        _sig = sig;
        _o.Events.Publish<TrayPayload?>(Event, new TrayPayload(NotificationCentre.El(o)));
    }

    /// 'tray.click' {id, button}: the checks of main.js, then the sidecar's trayClick (a dry run in --test).
    public async Task<(bool Ok, string? Reason, JsonObject? Result)> ClickAsync(string? id, string? button)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 400) return (false, "tray.click needs the id of a notification-area item", null);
        button ??= "left";
        if (button is not ("left" or "right" or "middle" or "double")) return (false, "tray.click clicks with the left or right button only", null);
        var known = (_now["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(t => NotificationCentre.Str(t["id"]) == id);
        if (known != null && NotificationCentre.IsFalse(known["clickable"])) {
            _log("tray: a click on a notification-area icon Mantle cannot click was refused (the bar shows which, and why)");
            return (false, NotificationCentre.Str(known["reason"]) ?? $"{NotificationCentre.Str(known["title"]) ?? "that icon"} cannot be clicked from Mantle, and Windows did not say why", null);
        }
        if (_o.Test) {
            _log($"tray: dry run (--test): would {button}-click the notification-area icon {id}; nothing was sent");
            return (true, null, new JsonObject { ["ok"] = true, ["dryRun"] = true });
        }
        var r = await _o.Platform.CallAsync("trayClick", new JsonObject { ["icon"] = id, ["button"] = button }, TimeSpan.FromSeconds(8)).ConfigureAwait(true);
        if (!r.Ok) return (false, r.Error ?? "the click did not go through", null);
        return (true, null, r.Result.ValueKind == JsonValueKind.Object ? JsonNode.Parse(r.Result.GetRawText()) as JsonObject : null);
    }

    /// 'sys.keys' {combo}: a WHITELIST (a key press goes to whatever is in front).
    public async Task<JsonObject> KeysAsync(string? combo)
    {
        combo = (combo ?? "").Trim().ToLowerInvariant();
        if (combo.Length == 0) return new JsonObject { ["ok"] = false, ["reason"] = "sys.keys needs a combo" };
        if (!KeysAllowed.Contains(combo)) return new JsonObject { ["ok"] = false, ["reason"] = $"Mantle will not press \"{combo}\": the bar may only send {string.Join(", ", KeysAllowed)}" };
        if (_o.Test) {
            _log($"sys.keys: dry run (--test): would press {combo}; nothing was pressed");
            return new JsonObject { ["ok"] = true, ["reason"] = null, ["combo"] = combo, ["dryRun"] = true };
        }
        var r = await _o.Platform.CallAsync("keys", new JsonObject { ["combo"] = combo }, TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (r.Ok) return new JsonObject { ["ok"] = true, ["reason"] = null, ["combo"] = combo };
        return new JsonObject { ["ok"] = false, ["reason"] = NotificationCentre.IsOldExe(r.Error)
            ? $"the sidecar exe has no \"keys\" command yet, so {combo} did nothing: run npm run build:sidecar" : $"{combo} was not pressed: {r.Error}" };
    }

    public void Dispose()
    {
        _gone = true;
        _timer?.Dispose(); _timer = null;
        foreach (var o in _offs) o.Dispose();
        _offs.Clear();
    }
}

/// <summary>bar.js toneOf / toneKeep: a tray icon's alpha-weighted mean luminance, and the bounded cache of results.</summary>
public static class TrayTone
{
    public const double Light = 0.75, Dark = 0.22, MinCover = 6;
    public const int Max = 48;

    /// "light" (a white glyph), "dark" (a black one) or "normal", from straight-alpha BGRA pixels.
    public static string Of(ReadOnlySpan<byte> bgra)
    {
        double cover = 0, sum = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4) {
            double a = bgra[i + 3] / 255.0;
            cover += a;
            sum += a * (0.2126 * bgra[i + 2] + 0.7152 * bgra[i + 1] + 0.0722 * bgra[i]) / 255.0;
        }
        if (cover >= MinCover && sum / cover > Light) return "light";
        if (cover >= MinCover && sum / cover < Dark) return "dark";
        return "normal";
    }

    /// bar.js strip signature: the strip is rebuilt only when what is drawn would change (icons never blink).
    public static string Signature(bool unavailable, JsonArray? items) =>
        (unavailable ? "U" : "A") + "\u0001" + string.Join("\u0001", (items ?? new JsonArray()).OfType<JsonObject>().Select(it => {
            string icon = NotificationCentre.Str(it["icon"]) ?? "";
            return string.Join("\u0002", NotificationCentre.Str(it["id"]), NotificationCentre.Str(it["title"]), it["clickable"]?.ToJsonString(), it["promoted"]?.ToJsonString(),
                NotificationCentre.Str(it["reason"]), icon.Length, icon.Length > 24 ? icon[^24..] : icon);
        }));
}

/// <summary>The tone cache: at most <see cref="TrayTone.Max"/> settled entries (oldest dropped), in-flight ones kept for
/// their waiters, so a redrawing icon (a spinner, a badge) does not grow memory. UI thread.</summary>
public sealed class TrayToneCache
{
    sealed class Entry { public string? Tone, Why; public List<Action<string, string?>>? Waiters; }
    readonly Dictionary<string, Entry> _m = new(StringComparer.Ordinal);
    readonly List<string> _order = new();

    public int Count => _m.Count;
    public bool Has(string src) => _m.ContainsKey(src);

    /// True when the caller must measure <paramref name="src"/> and call <see cref="Settle"/>; otherwise
    /// <paramref name="done"/> ran now (settled) or will run when the measuring caller settles it.
    public bool Begin(string src, Action<string, string?> done)
    {
        if (_m.TryGetValue(src, out var e)) {
            if (e.Waiters == null) done(e.Tone!, e.Why); else e.Waiters.Add(done);
            return false;
        }
        _m[src] = new Entry { Waiters = new List<Action<string, string?>> { done } };
        _order.Add(src);
        for (int i = 0; _order.Count > TrayTone.Max && i < _order.Count;) {
            var k = _order[i];
            if (_m.TryGetValue(k, out var x) && x.Waiters != null) { i++; continue; }   // still being measured
            _m.Remove(k); _order.RemoveAt(i);
        }
        return true;
    }

    public void Settle(string src, string tone, string? why)
    {
        if (!_m.TryGetValue(src, out var e)) e = _m[src] = new Entry();
        var waiters = e.Waiters;
        e.Tone = tone; e.Why = why; e.Waiters = null;
        foreach (var f in waiters ?? new List<Action<string, string?>>()) { try { f(tone, why); } catch (Exception) { /* one caller's bug is not another's */ } }
    }
}
