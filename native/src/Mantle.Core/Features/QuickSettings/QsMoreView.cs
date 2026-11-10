using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>What a whole view shows when it is not a list: a title, why, and its one way forward.</summary>
public sealed record QsViewState(string State, string? Title, string? Why);

/// <summary>One attempt's failure on a row, kept until the next attempt (qs-more.js failure()).</summary>
public sealed record QsRowFail(string Op, string Lead, string Reason, string? CodeHex = null)
{
    public string Short => Op switch {
        "disconnect" => "Couldn’t disconnect", "kbd" => "Needs Windows’ Wi-Fi list", "kbdlost" => "Keyboard went elsewhere", _ => "Couldn’t connect",
    };
    /// A connect (or keyboard) failure shows on a row that is not connected, a disconnect failure on one that still is.
    public bool Shows(bool connected) => Op == "disconnect" ? connected : !connected;
}

/// <summary>One paired Bluetooth device's row (ui/bar/bt.js describe()).</summary>
public sealed record QsBtRow(string Id, string Name, string Kind, string KindLabel, string State, string Sub, bool Connected,
    int? Battery, string? BatteryWhy, string Action, string ActionLabel, string? ActionWhy, string? FailLead, string? FailWhy, string Tip);

/// <summary>
/// The words of the quick-settings panel one level down (ui/bar/qs-more.js and ui/bar/bt.js), pure: the Wi-Fi list's
/// rows and states, the password rule, the Bluetooth rows and states, the sound output rows and the mixer's words.
/// </summary>
public static class QsMoreView
{
    public const int ViewHeightDip = 360;
    public const int WifiPollMs = 4000, SoundPollMs = 1500, DevicesEvery = 3, BtRenewMs = 30000, BtPollMs = 3000;

    /// Windows' commonest reason codes in plain words (qs-more.js LEADS; WlanReasonCodeToString's texts beside each).
    public static readonly IReadOnlyDictionary<long, string> Leads = new Dictionary<long, string> {
        [294932] = "The password looks wrong.",                 // 0x48014 "PSK mismatch suspected"
        [294930] = "That password is not in a valid format.",   // 0x48012 "Entered key is not in a valid format"
        [229379] = "The network did not answer in time.",       // 0x38003 "Association with the network timed out."
        [163842] = "The network is out of range.",              // 0x28002 "The wireless network is not visible."
    };

    static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    static readonly Regex Printable = new("^[\\x20-\\x7e]+$", RegexOptions.CultureInvariant);

    /// A Wi-Fi key is 8-63 printable ASCII characters, or exactly 64 hex digits (the sidecar's Wlan.CheckPassword). Null = fine.
    public static string? PasswordProblem(string pw)
    {
        pw ??= "";
        if (pw.Length == 64 && Hex64.IsMatch(pw)) return null;
        if (pw.Length < 8) return "A Wi-Fi password is at least 8 characters.";
        if (pw.Length > 63) return "A Wi-Fi password is at most 63 characters (or exactly 64 hex digits).";
        if (!Printable.IsMatch(pw)) return "A Wi-Fi password can only use letters, digits, spaces and the usual symbols.";
        return null;
    }

    public static int Bars(double? signal) => Math.Max(1, Math.Min(3, (int)Math.Ceiling((signal ?? 0) / 34)));

    public static string Ago(double? ms)
    {
        long s = (long)Math.Round((ms is double d && double.IsFinite(d) ? d : 0) / 1000);
        return s < 5 ? "just now" : s < 60 ? s + " s ago" : Math.Round(s / 60.0).ToString(CultureInfo.InvariantCulture) + " min ago";
    }

    /// A reason that stands alone: a capital first letter and a full stop (bt.js said()).
    public static string Said(string? s)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s[1..];
        return Ends(s) ? s : s + ".";
    }

    /// A full stop if it has none (qs-more.js sentence()).
    public static string Sentence(string? s) { s ??= ""; return Ends(s) ? s : s + "."; }
    static bool Ends(string s) => s.Length > 0 && ".!?…".IndexOf(s[^1]) >= 0;

    // ------------------------------------------------------------------ Wi-Fi

    public static string NetKey(JsonObject n) => QsParts.Str(n["hex"]) is { Length: > 0 } h ? h : "ssid:" + QsParts.Str(n["ssid"]);

    /// usable(): read, an adapter, the radio on; only such a list is scanned.
    public static bool Usable(JsonObject? r) => r != null && !QsParts.IsFalse(r["ok"]) && !QsParts.IsFalse(r["present"]) && !QsParts.IsFalse(r["on"]);

    /// The whole Wi-Fi view when it is not a list (renderWifi's state blocks).
    public static QsViewState Wifi(JsonObject? r, bool scanning)
    {
        if (r == null) return new("loading", "Looking for networks…", null);
        if (QsParts.IsFalse(r["ok"])) return new("error", "Wi-Fi can’t be read", QsParts.Str(r["reason"]) ?? "no reason given");
        if (QsParts.IsFalse(r["present"])) return new("none", "No Wi-Fi adapter", QsParts.Str(r["reason"]) ?? "Windows lists no wireless adapter on this machine");
        if (QsParts.IsFalse(r["on"])) return new("off", "Wi-Fi is off", QsParts.Str(r["reason"]) ?? "the Wi-Fi radio is switched off");
        if (r["networks"] is not JsonArray { Count: > 0 })
            return new("empty", "No networks found", scanning ? "Scanning for networks…" : "Windows sees no Wi-Fi networks in range right now.");
        return new("list", null, null);
    }

    /// A row's sub line (subline()).
    public static string WifiSub(JsonObject n, string? busyWhat, QsRowFail? fail)
    {
        if (busyWhat == "connect" || QsParts.IsTrue(n["connecting"])) return "Connecting…";
        if (busyWhat == "disconnect") return "Disconnecting…";
        bool secure = QsParts.IsTrue(n["secure"]);
        if (QsParts.IsTrue(n["connected"])) return secure ? "Connected, secured" : "Connected, open";
        if (fail != null) return fail.Short;
        return (QsParts.IsTrue(n["known"]) ? "Saved · " : "") + (secure ? QsParts.Str(n["security"]) ?? "Secured" : "Open");
    }

    public static string WifiName(JsonObject n) => QsParts.IsTrue(n["hidden"]) ? "Hidden network" : QsParts.Str(n["ssid"]) ?? "";

    public static string WifiTip(JsonObject n)
    {
        bool secure = QsParts.IsTrue(n["secure"]);
        return (QsParts.IsTrue(n["hidden"]) ? "A network that hides its name" : QsParts.Str(n["ssid"])) + "\n" +
            (secure ? QsParts.Str(n["security"]) : "Open (no password)") + " · signal " + Fmt(QsParts.Num(n["signal"])) + "%" +
            (QsParts.IsTrue(n["known"]) ? " · saved in Windows" : "");
    }

    /// The sidecar remembers the last connect or disconnect a while: one that failed after the panel closed is told
    /// the next time the list opens, unless a row already shows its own failure (lastNote()).
    public static string WifiLastNote(JsonObject? r, bool rowFails)
    {
        if (r?["last"] is not JsonObject l || !QsParts.IsFalse(l["ok"]) || QsParts.Str(l["ssid"]) is not string ssid || ssid.Length == 0 || rowFails) return "";
        return "Last attempt (" + Ago(QsParts.Num(l["agoMs"])) + "): " +
            (QsParts.Str(l["op"]) == "disconnect" ? "Wi-Fi did not disconnect" : "Mantle could not connect to “" + ssid + "”") +
            " — " + (QsParts.Str(l["reason"]) ?? "no reason given");
    }

    /// A failed connect's lead line by Windows' reason code.
    public static string ConnectLead(JsonObject? r) =>
        QsParts.Num(r?["code"]) is double c && Leads.TryGetValue((long)c, out var lead) ? lead : "Couldn’t connect.";

    // ------------------------------------------------------------------ Bluetooth (ui/bar/bt.js)

    public static readonly IReadOnlyDictionary<string, string> BtKinds = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["headphones"] = "Headphones", ["speaker"] = "Speaker", ["mouse"] = "Mouse", ["keyboard"] = "Keyboard",
        ["phone"] = "Phone", ["gamepad"] = "Game controller", ["other"] = "Bluetooth device",
    };

    public static string BtKind(JsonObject? d) => QsParts.Str(d?["kind"]) is string k && BtKinds.ContainsKey(k) ? k : "other";

    /// The level to show, or null: only a whole 0-100 number, and only while connected.
    public static int? BtBattery(JsonObject? d)
    {
        if (d == null || !QsParts.IsTrue(d["connected"])) return null;
        return QsParts.Num(d["battery"]) is double b && double.IsFinite(b) && b >= 0 && b <= 100 ? (int)Math.Round(b) : null;
    }

    public static QsBtRow BtDescribe(JsonObject d, string? busyId, string? busyWhat, IReadOnlyDictionary<string, QsRowFail>? fails)
    {
        string id = QsParts.Str(d["id"]) ?? "";
        string kind = BtKind(d);
        string name = QsParts.Str(d["name"]) is string n && n.Trim().Length > 0 ? n : "Unnamed device";
        string? busy = busyId != null && busyId == id ? busyWhat : null;
        bool connected = QsParts.IsTrue(d["connected"]);
        QsRowFail? f = fails != null && id.Length > 0 && fails.TryGetValue(id, out var ff) ? ff : null;
        var failed = busy == null && f != null && f.Shows(connected) ? f : null;
        string state = busy == "connect" ? "connecting" : busy == "disconnect" ? "disconnecting" : failed != null ? "failed" : connected ? "connected" : "disconnected";
        string sub = state switch {
            "connecting" => "Connecting…", "disconnecting" => "Disconnecting…",
            "failed" => failed!.Op == "disconnect" ? "Couldn’t disconnect" : "Couldn’t connect",
            "connected" => "Connected", _ => "Not connected",
        };
        int? pct = BtBattery(d);
        string? batteryWhy = connected && pct == null ? (QsParts.Str(d["batteryWhy"]) is { Length: > 0 } bw ? bw : "Windows gave no battery level for it") : null;
        string action = QsParts.IsTrue(d["canConnect"]) ? (connected ? "disconnect" : "connect") : "settings";
        string? actionWhy = action == "settings" ? (QsParts.Str(d["connectWhy"]) is { Length: > 0 } cw ? cw : "Mantle has no way to connect this device") : null;
        string actionLabel = action == "settings" ? "Open Bluetooth settings"
            : busy == action ? (action == "connect" ? "Connecting…" : "Disconnecting…")
            : action == "connect" ? "Connect" : "Disconnect";
        string tip = name + "\n" + BtKinds[kind] + " · " + (connected ? "Connected" : "Not connected") +
            (pct != null ? " · battery " + pct + "%" : "") + (batteryWhy != null ? "\nBattery: " + batteryWhy : "") + (actionWhy != null ? "\n" + actionWhy : "");
        return new QsBtRow(id, name, kind, BtKinds[kind], state, sub, connected, pct, batteryWhy, action, actionLabel, actionWhy,
            failed != null ? (failed.Lead.Length > 0 ? failed.Lead : failed.Op == "disconnect" ? "Couldn’t disconnect." : "Couldn’t connect.") : null,
            failed != null ? (failed.Reason.Length > 0 ? failed.Reason : "no reason given") : null, tip);
    }

    public static QsViewState Bt(JsonObject? r)
    {
        if (r == null) return new("loading", "Looking for devices…", null);
        if (QsParts.IsFalse(r["ok"])) return new("error", "Bluetooth devices can’t be read", QsParts.Str(r["reason"]) ?? "no reason given");
        if (QsParts.IsFalse(r["present"]))
            return new("none", "No Bluetooth adapter", Said(QsParts.Str(r["reason"]) ?? QsParts.Str(r["radioReason"]) ?? "Windows lists no Bluetooth radio on this machine"));
        if (QsParts.IsFalse(r["on"])) return new("off", "Bluetooth is off", "Turn it on to connect your paired devices.");
        if (r["devices"] is not JsonArray { Count: > 0 }) return new("empty", "No paired devices", "Pair a device in Windows’ Bluetooth settings, and it shows up here.");
        return new("list", null, null);
    }

    /// The radio could not be read but the list could: the list shows, and this says why its on/off is a guess.
    public static string BtRadioNote(JsonObject? r)
    {
        if (r == null || QsParts.IsFalse(r["ok"]) || QsParts.IsFalse(r["present"])) return "";
        if (!QsParts.IsTrue(r["on"]) && !QsParts.IsFalse(r["on"]))
            return "Bluetooth’s on/off state could not be read" + (QsParts.Str(r["radioReason"]) is { Length: > 0 } rr ? ": " + rr : "");
        return "";
    }

    public static string BtLastNote(JsonObject? r, IReadOnlyDictionary<string, QsRowFail>? fails)
    {
        if (r?["last"] is not JsonObject l || !QsParts.IsFalse(l["ok"])) return "";
        if (fails != null && QsParts.Str(l["device"]) is string dev && fails.ContainsKey(dev)) return "";
        string who = QsParts.Str(l["name"]) ?? "the device";
        return "Last attempt (" + Ago(QsParts.Num(l["agoMs"])) + "): " + who + (QsParts.Str(l["op"]) == "disconnect" ? " did not disconnect" : " did not connect") +
            " — " + (QsParts.Str(l["reason"]) ?? "no reason given");
    }

    /// The sidecar's own in-flight connect (from a panel that has since closed), as the view's busy.
    public static (string Id, string What)? BtBusyOf(JsonObject? r)
    {
        if (r?["busy"] is not JsonObject b || QsParts.Str(b["device"]) is not { Length: > 0 } dev) return null;
        string? op = QsParts.Str(b["op"]);
        return op is "connect" or "disconnect" ? (dev, op) : null;
    }

    public static string BtFallbackNote(string why) => "Re-reading every " + BtPollMs / 1000 + " s, because the live watch is off: " + Sentence(why);

    /// BAR-694 (security review #20): a failed toggle's reason never names a device; the busy reply names the OTHER one.
    public static string BtPrivateReason(string reason, string? name)
    {
        if (name is { Length: > 1 }) reason = reason.Replace(name, "the device", StringComparison.Ordinal);
        return Regex.Replace(reason, "^Mantle is still (connecting|disconnecting) [\\s\\S]+?; wait for it to finish$", "Mantle is still $1 another device; wait for it to finish");
    }

    // ------------------------------------------------------------------ sound

    public static QsViewState Devices(JsonObject? r)
    {
        if (r == null) return new("loading", "Reading playback devices…", null);
        if (QsParts.IsFalse(r["ok"])) return new("error", "Playback devices can’t be read", QsParts.Str(r["reason"]) ?? "no reason given");
        if (r["devices"] is not JsonArray { Count: > 0 })
            return new("empty", "No playback devices", "Windows lists no active speakers or headphones. Plug one in, or enable one in Sound settings.");
        return new("list", null, null);
    }

    /// A device row's small right-hand word, and its tooltip (devRow()).
    public static (string? Sub, string Tip) Device(JsonObject d, bool switching)
    {
        string name = QsParts.Str(d["name"]) ?? "A device";
        bool isDefault = QsParts.IsTrue(d["isDefault"]);
        var roles = d["roles"] as JsonObject;
        bool partly = isDefault && QsParts.IsFalse(roles?["communications"]);
        bool calls = !isDefault && QsParts.IsTrue(roles?["communications"]);
        string? sub = switching ? "Switching…" : calls ? "Calls" : null;
        string tip = isDefault
            ? name + " is the output" + (partly ? " (calls still go to another device; click another device and back to move them too)" : "")
            : "Play sound through " + name + (calls ? "\n(already the device for calls)" : "");
        return (sub, tip);
    }

    public static QsViewState Mixer(JsonObject? r)
    {
        if (r == null) return new("loading", null, null);
        if (QsParts.IsFalse(r["ok"])) return new("error", "The mixer can’t be read", QsParts.Str(r["reason"]) ?? "no reason given");
        if (r["sessions"] is not JsonArray { Count: > 0 }) {
            string dev = (r["device"] as JsonObject)?["name"] is JsonNode dn && QsParts.Str(dn) is { Length: > 0 } dname ? " on " + dname : "";
            return new("empty", "No apps are playing sound", QsParts.Str(r["reason"]) ?? "Apps appear here while they have sound open" + dev + ".");
        }
        return new("list", null, null);
    }

    public static string AppName(JsonObject s) => QsParts.IsTrue(s["system"]) ? "System sounds" : QsParts.Str(s["name"]) is { Length: > 0 } n ? n : "An app";

    public static string AppTip(JsonObject s) =>
        (QsParts.IsTrue(s["system"]) ? "Windows system sounds" : QsParts.Str(s["name"]) ?? "") +
        (QsParts.Str(s["path"]) is { Length: > 0 } p ? "\n" + p : "") +
        (QsParts.Num(s["sessions"]) is double c && c > 1 ? "\n" + c.ToString(CultureInfo.InvariantCulture) + " audio sessions, one slider" : "") +
        (QsParts.IsTrue(s["active"]) ? "" : "\nNot playing right now");

    /// The letter drawn in place of an icon (letter()).
    public static string AppLetter(JsonObject s)
    {
        var c = (QsParts.Str(s["name"]) ?? "?").FirstOrDefault(char.IsAsciiLetterOrDigit);
        return c == default ? "?" : char.ToUpperInvariant(c).ToString();
    }

    public static string MuteTip(JsonObject s) =>
        (QsParts.IsTrue(s["muted"]) ? "Unmute " : "Mute ") + (QsParts.IsTrue(s["system"]) ? "system sounds" : QsParts.Str(s["name"]) is { Length: > 0 } n ? n : "this app");

    static string Fmt(double? v) => v is double d ? d.ToString(CultureInfo.InvariantCulture) : "?";
}
