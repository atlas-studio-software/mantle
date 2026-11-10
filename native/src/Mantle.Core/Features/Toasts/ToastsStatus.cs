using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Toasts;

// Port of lib/features/_toasts-status.js: whose pop-ups the user gets right now, in ONE line and a reason
// (Settings > System > Notifications; the "No silent failures" rule). Pure: every input is a plain value.
// The native list has no early-signal or quick-check stages (lib/notifications.js watchState's coming / looks /
// missed), so of the watch only "watching or not, and why" is read; the rest of statusOf is as Electron's.

/// <summary>{mode: mantle | windows | none, line, why, tone: ok | info | warn, at}</summary>
public sealed record ToastsStatusLine(string Mode, string Line, string? Why, string Tone, double? At);

public static class ToastsStatus
{
    public static readonly IReadOnlyDictionary<string, string> Lines = new Dictionary<string, string>
    {
        ["mantle"] = "Banners: Mantle", ["windows"] = "Banners: Windows' own", ["none"] = "Banners: none",
    };

    /// 100 -> "0.1 s", 10000 -> "10 s".
    public static string Secs(double ms)
    {
        double s = ms / 1000;
        return (s == Math.Floor(s) ? s.ToString(CultureInfo.InvariantCulture) : Math.Round(s, 2).ToString(CultureInfo.InvariantCulture)) + " s";
    }

    /// A sentence for inside parentheses: no trailing period, first letter lower unless it names Windows/Mantle.
    public static string Inner(string? s)
    {
        string t = Regex.Replace((s ?? "").Trim(), @"[.\s]+$", "");
        if (Regex.IsMatch(t, "^[A-Z][a-z]") && !Regex.IsMatch(t, @"^(Windows|Mantle)\b")) t = char.ToLowerInvariant(t[0]) + t[1..];
        return t;
    }

    static ToastsStatusLine Make(string mode, string? why, string tone, double? at = null) => new(mode, Lines[mode], why != null ? Inner(why) : null, tone, at);

    /// <param name="hhmm">the clock's HH:mm of a unix-ms time (local in the app, fixed in tests)</param>
    public static ToastsStatusLine Of(bool on, string? accessWhy, string? applyWhy, bool helperReady, SafetyState? safety,
        bool? watching, string? watchWhy, double now, Func<double, string> hhmm)
    {
        if (!on) return Make("windows", "Mantle's style is off", "info");
        var down = safety?.Down;
        var last = safety?.Last;
        if (down != null || !helperReady)
        {
            double since = down?.At ?? now;
            var ran = last != null && last.At >= since ? last : null;
            bool starting = down?.Starting == true;
            string what = starting ? "Mantle's notification helper has not started yet" : "Mantle's notification helper stopped";
            string when = starting ? $"{what} (since {hhmm(since)})" : $"{what} at {hhmm(since)}";
            if (ran is { Ok: true, Nothing: true }) return Make("windows", $"{what}; Windows shows its own pop-ups", "warn", ran.At);
            if (ran is { Ok: true }) return Make("windows", $"{what}; restored at {hhmm(ran.At)}", "warn", ran.At);
            if (ran != null) return Make("none", $"{what} and Windows' pop-ups could not be put back: {Inner(ran.Why)}", "warn", ran.At);
            if (safety?.Running == true) return Make("none", $"{when}; Windows' own pop-ups are being put back now", "warn", since);
            string wait = safety is { GraceMs: > 0 } ? $"within {Secs(safety.GraceMs)}" : "shortly";
            return Make("none", $"{when}; Windows' own pop-ups come back {wait} unless it starts", "warn", since);
        }
        if (accessWhy != null) return Make("windows", accessWhy, "warn");
        var bits = new List<string>();
        string tone = "ok";
        if (watching == false)
        {
            tone = "warn";
            bits.Add($"new notifications are seen only when the list is read again, because {Inner(watchWhy ?? "the notification watch would not start")}");
        }
        if (applyWhy != null) { bits.Add($"some apps still get Windows' own pop-up too: {Inner(applyWhy)}"); tone = "warn"; }
        return Make("mantle", bits.Count > 0 ? string.Join("; ", bits) : null, tone);
    }
}
