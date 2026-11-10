using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>How one tile is drawn: its words, its tooltip, on/dead, and (for Bluetooth) the Settings page a click opens
/// when Mantle cannot switch it.</summary>
public sealed record QsTileView(string Key, string Label, string Sub, string Tip, bool On, bool Dead, int Bars = 0, string? Opens = null, string? OpensWhat = null, bool Live = true, bool Reading = false);

/// <summary>How a slider row is drawn.</summary>
public sealed record QsSliderView(bool Live, double? Level, string Value, string Why);

/// <summary>
/// The panel's words from its parts (bar.js buildQsPop paint(), bt.js tile()), pure so every tile's sub line,
/// tooltip and the notes are unit tested without a window.
/// </summary>
public static class QsView
{
    static bool Ok(JsonObject? p) => QsParts.IsOk(p);
    static string Why(JsonObject? p, string what) => QsParts.Str(p?["reason"]) is { Length: > 0 } r ? r : $"the shell gave no reason why {what} is unavailable";
    static bool On(JsonObject? p) => QsParts.IsTrue(p?["on"]);

    public static QsTileView Wifi(JsonObject? w)
    {
        bool ok = Ok(w), present = w == null || !QsParts.IsFalse(w["present"]);
        int bars = w != null && QsParts.IsTrue(w["connected"]) ? Math.Max(1, Math.Min(3, (int)Math.Ceiling((QsParts.Num(w["signal"]) ?? 0) / 34))) : 0;
        string ssid = QsParts.Str(w?["ssid"]) is { Length: > 0 } s ? s : "Connected", band = QsParts.Str(w?["band"]) is { Length: > 0 } b ? " · " + b : "";
        string sub = w == null ? "Unknown" : !ok ? "Unavailable" : !present ? "No adapter" : !On(w) ? "Off"
            : QsParts.IsTrue(w["connected"]) ? ssid + band : "Not connected";
        string tip = !ok ? "Wi-Fi — " + Why(w, "Wi-Fi") : !present ? "No Wi-Fi adapter on this machine" : "Turn Wi-Fi " + (On(w) ? "off" : "on");
        return new("wifi", "Wi-Fi", sub, tip, On(w) && ok && present, !ok || !present, On(w) ? Math.Max(bars, 1) : 0);
    }

    const string BtSettings = "ms-settings:bluetooth", PlaneSettings = "ms-settings:network-airplanemode", RadioSettings = "ms-settings:privacy-radios";

    /// bt.js tile(): a radio Mantle cannot switch still clicks (it opens Windows' own page for it).
    public static QsTileView Bluetooth(JsonObject? b)
    {
        static string Said(string s) { s = s.Trim(); return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..]; }
        static string Then(string what) => "\nClick to open " + what + ".";
        if (b == null) return new("bluetooth", "Bluetooth", "Unknown", "Bluetooth has not been read yet", false, true, Live: false);
        if (QsParts.IsTrue(b["pending"]))
            return new("bluetooth", "Bluetooth", "Checking…", Said(QsParts.Str(b["reason"]) ?? "Bluetooth is still being read for the first time"), false, true, Live: false);
        bool slow = QsParts.Str(b["via"]) is string via && via.Contains("powershell", StringComparison.OrdinalIgnoreCase);
        bool plane = QsParts.IsTrue(b["airplane"]);
        if (!Ok(b))
            return new("bluetooth", "Bluetooth", "Unavailable", "Bluetooth can’t be read: " + Said(QsParts.Str(b["reason"]) ?? "the shell gave no reason why Bluetooth is unavailable") + Then("Bluetooth settings"), false, false, Opens: BtSettings, OpensWhat: "Bluetooth settings", Live: false);
        if (QsParts.IsFalse(b["present"]))
            return new("bluetooth", "Bluetooth", "No radio", "No Bluetooth radio: " + Said(QsParts.Str(b["reason"]) ?? "Windows lists no Bluetooth radio on this PC") + Then("Bluetooth settings"), false, false, Opens: BtSettings, OpensWhat: "Bluetooth settings", Live: false);
        if (QsParts.Str(b["radio"]) == "disabled") {
            string what = plane ? "airplane mode settings" : "Bluetooth settings";
            return new("bluetooth", "Bluetooth", plane ? "Airplane mode" : "Disabled",
                Said(QsParts.Str(b["reason"]) ?? (plane ? "Bluetooth is turned off by airplane mode" : "Bluetooth is disabled by a hardware switch or the firmware")) +
                "\nMantle can’t switch it on: Windows refuses a disabled radio." + Then(what), false, false, Opens: plane ? PlaneSettings : BtSettings, OpensWhat: what, Live: false);
        }
        if (QsParts.IsFalse(b["canSwitch"])) {
            bool blocked = QsParts.Str(b["access"]) is string acc && acc != "Allowed";
            string what = blocked ? "radio privacy settings" : "Bluetooth settings";
            return new("bluetooth", "Bluetooth", (On(b) ? "On" : "Off") + " · locked",
                "Bluetooth is " + (On(b) ? "on" : "off") + ", and Mantle can’t switch it: " + Said(QsParts.Str(b["reason"]) ?? "Windows does not let Mantle switch this radio") + Then(what),
                On(b), false, Opens: blocked ? RadioSettings : BtSettings, OpensWhat: what, Live: false);
        }
        string sub = On(b) ? "On" : "Off" + (plane ? " · airplane mode" : "");
        string tip = (slow ? "Switched via PowerShell, so it takes a second or two\n" : "") + "Turn Bluetooth " + (On(b) ? "off" : "on") +
            (!On(b) && plane ? "\nAirplane mode is on; Bluetooth can still be switched on by itself" : "");
        return new("bluetooth", "Bluetooth", sub + (slow ? " · slow" : ""), tip, On(b), false);
    }

    public static QsTileView NightLight(JsonObject? n)
    {
        bool ok = Ok(n), present = n == null || !QsParts.IsFalse(n["present"]);
        string sub = n == null ? "Unknown" : !ok ? "Unavailable" : !present ? "Not set up" : On(n) ? "On" : "Off";
        string tip = !ok || !present ? "Night light — " + Why(n, "night light") : "Turn night light " + (On(n) ? "off" : "on");
        return new("nightlight", "Night light", sub, tip, On(n) && ok && present, !ok || !present);
    }

    /// Energy saver: its state; a click opens Windows' own page (no other app may switch it).
    public static QsTileView Saver(JsonObject? p)
    {
        bool ok = Ok(p), saver = QsParts.IsTrue(p?["saver"]);
        string sub = p == null ? "Unknown" : !ok ? "Unavailable" : (saver ? "On" : "Off") + " · Settings";
        string tip = !ok ? "Energy saver — " + Why(p, "energy saver")
            : "Energy saver is " + (saver ? "on" : "off") + ".\nWindows lets only its own Settings switch it: click to open that page";
        return new("saver", "Energy saver", sub, tip, saver && ok && p != null, !ok || p == null, Opens: "ms-settings:batterysaver", OpensWhat: "energy saver settings");
    }

    /// paintSlider(): live when read and present; the number 0-100 or a dash; "unavailable" with its why when dead.
    public static QsSliderView Slider(JsonObject? part, bool brightness)
    {
        bool ok = Ok(part), present = part != null && !QsParts.IsFalse(part["present"]);
        double? lvl = QsParts.Num(part?["level"]);
        bool live = ok && present;
        string why = brightness
            ? (part == null ? "brightness was not reported" : !ok ? Why(part, "brightness") : "This display does not report a brightness control")
            : (ok ? "volume was not reported" : Why(part, "volume"));
        return new(live, lvl, live ? (lvl is double v ? Math.Round(v, MidpointRounding.AwayFromZero) + "%" : "—") : "unavailable", live ? "" : why);
    }

    /// The notes: one line per part that is not ok (never a blank tile with no explanation), then each set that did not take.
    public static List<string> Problems(IReadOnlyDictionary<string, JsonObject> parts, IEnumerable<string> setNotes)
    {
        JsonObject? P(string k) => parts.TryGetValue(k, out var v) ? v : null;
        var o = new List<string>();
        var w = P("wifi"); if (!Ok(w)) o.Add("Wi-Fi: " + Why(w, "Wi-Fi"));
        var b = P("bluetooth"); if (!Ok(b)) o.Add("Bluetooth: " + Why(b, "Bluetooth"));
        var n = P("nightlight"); if (!Ok(n)) o.Add("Night light: " + Why(n, "night light"));
        var p = P("power"); if (!Ok(p)) o.Add("Power: " + Why(p, "power"));
        var br = P("brightness"); if (br != null && !Ok(br)) o.Add("Brightness: " + Why(br, "brightness"));
        var v = P("volume"); if (v != null && !Ok(v)) o.Add("Volume: " + Why(v, "volume"));
        o.AddRange(setNotes);
        return o;
    }

    /// qsWhy(): "&lt;Part&gt; did not change: &lt;why&gt;", unless the reason already names the part.
    public static string SetNote(string part, string? reason)
    {
        string label = part switch { "wifi" => "Wi-Fi", "bluetooth" => "Bluetooth", "nightlight" => "Night light", "brightness" => "Brightness", "volume" => "Volume", "mute" => "Mute", _ => part };
        string why = string.IsNullOrEmpty(reason) ? "no reason was given" : reason;
        return why.StartsWith(label, StringComparison.OrdinalIgnoreCase) ? why : label + " did not change: " + why;
    }

    public static readonly (string Mode, string Label, string Full)[] PowerModes =
        { ("efficiency", "Efficiency", "Best power efficiency"), ("balanced", "Balanced", "Balanced"), ("performance", "Performance", "Best performance") };
}
