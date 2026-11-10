using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>
/// The quick-settings footer's battery line (ui/bar/battery.js describe(): <c>footer</c> and <c>tip</c>, from the
/// <c>power</c> part), so the panel words the battery exactly as Electron does. Pure.
/// </summary>
public static class QsBattery
{
    static readonly Regex Jargon = new(@"sidecar|npm |\.exe\b|\bIPC\b|renderer|hwnd|dryRun|undefined|\bnull\b|\bNaN\b|\[object|HRESULT|0x[0-9a-f]{4,}|Win32|\berror \d|exception|battery\.get|window\.mantle|preload|\.js\b|timed? ?out|within \d+ ?ms", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Errno = new(@"\bE[A-Z]{3,}\b|^Error\b", RegexOptions.CultureInvariant);
    static readonly Regex TechBracket = new(@"\s*\([^()]*\b(?:error|Win32|HRESULT|0x[0-9a-f]+|failed)\b[^()]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public const string PlainFail = "Mantle could not get the battery status from Windows. It updates by itself when Windows reports a change";

    /// battery.js plain(): a reason shown as it is when plain; a technical bracket dropped; anything technical one sentence.
    public static string Plain(string? reason)
    {
        var s = (reason ?? "").Trim();
        if (s.Length == 0) return "";
        s = TechBracket.Replace(s, "");
        return Jargon.IsMatch(s) || Errno.IsMatch(s) ? PlainFail : s;
    }

    /// <summary>The footer line and its tooltip for a <c>power</c> part (null: not read yet).</summary>
    public static (string Footer, string Tip) Describe(JsonObject? r)
    {
        if (r == null) return ("Reading the battery…", "Battery status: reading…");
        if (QsParts.IsFalse(r["ok"])) {
            string why = Plain(QsParts.Str(r["reason"]));
            if (why.Length == 0) why = "Windows did not say why the battery could not be read";
            return ("Battery: " + why, "Battery status: unknown\n" + why);
        }
        double? pctRaw = QsParts.Num(r["percent"]);
        int? pct = pctRaw is double d && d >= 0 && d <= 100 ? (int)Math.Round(d, MidpointRounding.AwayFromZero) : null;
        bool? ac = Bool(r["ac"]) ?? (Bool(r["onBattery"]) is bool ob ? !ob : null);
        bool? present = Bool(r["present"]) ?? Bool(r["hasBattery"]) ?? (pct != null ? true : null);
        bool? charging = Bool(r["charging"]);
        if (present == false) return ("No battery is detected", "Battery status: no battery is detected");
        if (pct == null || ac == null || present == null) {
            string why = Plain(QsParts.Str(r["reason"]));
            if (why.Length == 0) why = pct == null ? "Windows does not know the battery percentage yet"
                : ac == null ? "Windows does not know whether the charger is plugged in"
                : "Windows cannot tell whether this machine has a battery";
            string words = pct == null ? (ac == true ? "plugged in" : ac == false ? "unknown remaining" : "unknown") : pct + "%";
            return (pct == null ? Cap(words) : $"{pct}% · {why}", "Battery status: " + words + "\n" + why);
        }
        if (ac == true) {
            if (charging == true) return ($"{pct}% · charging", $"Battery status: {pct}% available (plugged in, charging)");
            if (pct == 100 && charging == false) return ("Fully charged", "Battery status: fully charged (100%)");
            string nc = charging == false ? ", not charging" : "";
            return ($"{pct}% · plugged in{nc}", $"Battery status: {pct}% available (plugged in{nc})");
        }
        // on battery: the power part carries no time left (QuickSettings.cs keys), so the estimate is said as pending
        double low = QsParts.Num(r["lowAt"]) is double l && l >= 0 ? l : 10, crit = QsParts.Num(r["criticalAt"]) is double c && c >= 0 ? c : 5;
        string foot = pct <= crit ? $"{pct}% · very low, plug in now" : pct <= low ? $"{pct}% · low, plug in soon" : $"{pct}% · estimating time left";
        return (foot, $"Battery status: {pct}% remaining");
    }

    static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
