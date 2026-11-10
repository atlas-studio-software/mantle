using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>
/// Quick settings one level down (main.js qsMore, qs.wifi.*, qs.audio.devices / qs.audio.default, qs.bt.*): the Wi-Fi
/// networks, the playback devices and the paired Bluetooth devices. Unlike a part read, the sidecar's own ok/reason
/// WIN (a list read with the radio off is ok:true with its reason). In a --test run every write (a connect, a
/// disconnect, the output device, a Bluetooth connect) is a DRY RUN: checked as a live one, logged with what it would
/// have asked, never sent, and answered ok:false "this is a test run, so Windows was not changed". MANTLE_TEST_QS may
/// carry <c>wifiList</c>, <c>devices</c> and <c>btList</c>, read in place of the sidecar so every runner draws the same rows.
/// THE PASSWORD: taken out of the call's arguments first, never logged (its length at most), never kept or put in an
/// answer; live it goes into the one sidecar call and is dropped from the arguments as soon as the call is made.
/// </summary>
public sealed partial class QsService
{
    public const string BtEvent = "qs.btdevices";
    public const string DryRunWords = "this is a test run, so Windows was not changed";
    public const int ListMs = 8000, ScanMs = 15000, ConnectMs = 40000, DisconnectMs = 15000, BtListMs = 8000, BtWatchMs = 6000, BtToggleMs = 25000;

    /// qsMore: the sidecar's answer merged over {ok:true, reason:null}; a failed call is {ok:false, reason: label: why}.
    async ValueTask<JsonObject> MoreAsync(string verb, JsonObject args, string label, int ms)
    {
        var r = await _o.Platform.CallAsync(verb, args, TimeSpan.FromMilliseconds(ms)).ConfigureAwait(true);
        if (!r.Ok) return QsParts.Fail($"{label}: {r.Error ?? r.Outcome.ToString()}");
        var o = new JsonObject { ["ok"] = true, ["reason"] = null };
        if (Node(r.Result) is JsonObject got) foreach (var kv in got) o[kv.Key] = kv.Value?.DeepClone();
        return o;
    }

    JsonObject? Fixed(string key, string what)
    {
        if (_fixture == null) return null;
        if (_fixture[key] is JsonObject f) { var o = (JsonObject)f.DeepClone(); if (!o.ContainsKey("ok")) o["ok"] = true; return o; }
        if (_fixture[key] is JsonArray a) return new JsonObject { ["ok"] = true, ["reason"] = null, [key] = a.DeepClone() };
        return QsParts.Fail($"{what}: MANTLE_TEST_QS has no \"{key}\", and a test run with made-up parts never reads the sidecar");
    }

    JsonObject DryRun(string what, string verb, JsonObject shown, string? extra = null)
    {
        // names and ids are shown as their length only (LOG-006: a Wi-Fi name or a device id never reaches the log)
        var rest = (JsonObject)shown.DeepClone();
        string op = QsParts.Str(rest["op"]) ?? "";
        rest.Remove("op");
        _o.Log($"qs: dry run (--test): {what} would ask the sidecar {verb} {op} {CallLog.Shown(rest)}{extra}; nothing was sent, Windows was not changed");
        return new JsonObject { ["ok"] = false, ["dryRun"] = true, ["reason"] = DryRunWords };
    }

    /// After a connect, a disconnect, an output switch or a Bluetooth toggle, an open panel reads its parts again
    /// (main.js qsPush): the tile, the volume slider of a new output.
    void ReReadIfWatched() { if (_watching && _fixture == null) _ = ReadAllAsync(); }

    // ------------------------------------------------------------------ Wi-Fi

    public async ValueTask<JsonObject> WifiListAsync()
        => Fixed("wifiList", "the Wi-Fi networks cannot be read") ?? await MoreAsync("wlan", new JsonObject { ["op"] = "list" }, "the Wi-Fi networks cannot be read", ListMs).ConfigureAwait(true);

    /// A scan only reads (Windows' WlanScan); with made-up parts it reads the made-up list and says nothing was scanned.
    public async ValueTask<JsonObject> WifiScanAsync()
    {
        if (Fixed("wifiList", "the Wi-Fi scan did not finish") is JsonObject f) {
            f["scan"] = new JsonObject { ["ok"] = true, ["reason"] = null };
            _o.Log("qs: a --test run with MANTLE_TEST_QS: the scan reads the made-up networks; nothing was scanned");
            return f;
        }
        return await MoreAsync("wlan", new JsonObject { ["op"] = "scan" }, "the Wi-Fi scan did not finish", ScanMs).ConfigureAwait(true);
    }

    /// qs.wifi.connect {ssid | hex, password?, auto}.
    public async ValueTask<JsonObject> WifiConnectAsync(JsonElement a)
    {
        // THE PASSWORD LEAVES THE ARGUMENTS FIRST: from here it lives in this one local and, live, the one sidecar call
        string? password = null;
        JsonElement pw = default;
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("password", out pw)) {
            if (pw.ValueKind == JsonValueKind.String) password = pw.GetString();
            else if (pw.ValueKind != JsonValueKind.Null) return new JsonObject { ["ok"] = false, ["reason"] = "the Wi-Fi password must be text" };
        }
        string? ssid = StrArg(a, "ssid"), hex = StrArg(a, "hex");
        if (ssid == null && hex == null) return QsParts.Fail("qs.wifi.connect needs ssid (the network's name) or hex");
        bool auto = !(a.ValueKind == JsonValueKind.Object && a.TryGetProperty("auto", out var au) && au.ValueKind == JsonValueKind.False);
        var args = new JsonObject { ["op"] = "connect", ["auto"] = auto };
        if (hex != null) args["hex"] = hex; else args["ssid"] = ssid;
        if (_o.Test) {
            if (!string.IsNullOrEmpty(password) && QsMoreView.PasswordProblem(password) is string bad) {
                _o.Log($"qs: dry run (--test): a Wi-Fi connect was refused before anything was sent: {bad}");
                password = null;
                return new JsonObject { ["ok"] = false, ["dryRun"] = true, ["reason"] = bad };
            }
            int len = password?.Length ?? 0;
            password = null;
            return DryRun("a Wi-Fi connect", "wlan", args, len > 0 ? $" with a password of {len} characters (the password itself is never logged)" : " with no password");
        }
        if (!string.IsNullOrEmpty(password)) args["password"] = password;
        password = null;
        var pending = MoreAsync("wlan", args, $"Mantle could not connect to \"{ssid ?? "that network"}\"", ConnectMs);
        args.Remove("password");   // already written to the sidecar's stdin by CallAsync
        var r = await pending.ConfigureAwait(true);
        r.Remove("password");
        ReReadIfWatched();
        return r;
    }

    public async ValueTask<JsonObject> WifiDisconnectAsync()
    {
        var args = new JsonObject { ["op"] = "disconnect" };
        if (_o.Test) return DryRun("a Wi-Fi disconnect", "wlan", args);
        var r = await MoreAsync("wlan", args, "Wi-Fi could not be disconnected", DisconnectMs).ConfigureAwait(true);
        ReReadIfWatched();
        return r;
    }

    // ------------------------------------------------------------------ sound output

    public async ValueTask<JsonObject> AudioDevicesAsync()
        => Fixed("devices", "the playback devices cannot be read") ?? await MoreAsync("audio", new JsonObject { ["op"] = "devices" }, "the playback devices cannot be read", AudioMs).ConfigureAwait(true);

    /// qs.audio.default {device}: the default output for all three roles ("device", not "id": the protocol's own).
    public async ValueTask<JsonObject> AudioDefaultAsync(JsonElement a)
    {
        string? device = StrArg(a, "device");
        if (device == null) return QsParts.Fail("qs.audio.default needs device: a playback device's id from qs.audio.devices");
        var args = new JsonObject { ["op"] = "default", ["device"] = device };
        if (_o.Test) return DryRun("the output device", "audio", args);
        var r = await MoreAsync("audio", args, "the output device could not be changed", AudioMs).ConfigureAwait(true);
        ReReadIfWatched();
        return r;
    }

    // ------------------------------------------------------------------ Bluetooth devices

    public async ValueTask<JsonObject> BtListAsync()
        => Fixed("btList", "the Bluetooth devices cannot be read") ?? await MoreAsync("btdevices", new JsonObject { ["op"] = "list" }, "the Bluetooth devices cannot be read", BtListMs).ConfigureAwait(true);

    /// qs.bt.watch {on}: the sidecar's DeviceWatcher while the Bluetooth view is open (it lapses by itself 90 s after
    /// the last renewal); its pushes reach the panel as <see cref="BtEvent"/>. Made-up devices have nothing to watch.
    public async ValueTask<JsonObject> BtWatchAsync(bool on)
    {
        if (_fixture != null) return new JsonObject { ["ok"] = true, ["watching"] = on, ["via"] = "fixture", ["reason"] = "the devices are MANTLE_TEST_QS's, so there is nothing to watch" };
        return await MoreAsync("btdevices", new JsonObject { ["op"] = "watch", ["on"] = on }, $"the Bluetooth device watch could not be turned {(on ? "on" : "off")}", BtWatchMs).ConfigureAwait(true);
    }

    /// qs.bt.connect / qs.bt.disconnect {device}. A failure's reason never names a device (BAR-694).
    public async ValueTask<JsonObject> BtToggleAsync(string op, JsonElement a)
    {
        string? device = StrArg(a, "device");
        if (device == null) return new JsonObject { ["ok"] = false, ["op"] = op, ["reason"] = $"qs.bt.{op} needs device: a row's id from qs.bt.list" };
        var args = new JsonObject { ["op"] = op, ["device"] = device };
        if (_o.Test) { var d = DryRun($"a Bluetooth {op}", "btdevices", args); d["op"] = op; return d; }
        var r = await MoreAsync("btdevices", args, $"the Bluetooth device could not be {(op == "connect" ? "connected" : "disconnected")}", BtToggleMs).ConfigureAwait(true);
        if (QsParts.IsFalse(r["ok"]) && QsParts.Str(r["reason"]) is string why) r["reason"] = QsMoreView.BtPrivateReason(why, QsParts.Str(r["name"]));
        ReReadIfWatched();
        return r;
    }

    /// The sidecar's watch push: the whole list after a change settled, or word that the watch stopped. Relayed as-is.
    void OnBtPush(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return;
        _o.Events.Publish(BtEvent, El(Detach(body, "ev")));
    }

    static string? StrArg(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
