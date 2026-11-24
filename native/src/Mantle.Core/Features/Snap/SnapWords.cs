using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Snap;

/// <summary>
/// The snap feature's words (lib/features/snap.js: ZONE_WORDS, cap, appOf, why, namesOf, unname). lane r9-snap.
/// The notice names the app; the LOG never does (lane r7-names privacy rule): every app name, title and exe
/// (with and without .exe) is replaced by "the window" before a line is logged.
/// </summary>
public static class SnapWords
{
    public static readonly IReadOnlyDictionary<string, string> Zones = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["left"] = "left half", ["right"] = "right half", ["tl"] = "top-left quarter", ["tr"] = "top-right quarter",
        ["bl"] = "bottom-left quarter", ["br"] = "bottom-right quarter", ["full"] = "whole screen",
    };

    public const string NoReason = "Windows gave no reason.";

    /// <summary>A reason as a sentence: trimmed, capital first letter, full stop unless it already ends in . ! or ?.</summary>
    public static string Cap(string? t)
    {
        string s = (t ?? "").Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s.Substring(1);
        char last = s[s.Length - 1];
        return last == '.' || last == '!' || last == '?' ? s : s + ".";
    }

    /// <summary>A string field of a sidecar answer, null when missing or not a string.</summary>
    public static string? Str(JsonElement x, string key) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The app's own name ("Task Manager"), else its exe, else "this window" (an empty string counts as none).</summary>
    public static string AppOf(JsonElement x)
    {
        string? app = Str(x, "app");
        if (!string.IsNullOrEmpty(app)) return app;
        string? exe = Str(x, "exe");
        return !string.IsNullOrEmpty(exe) ? exe : "this window";
    }

    /// <summary>cap(x.reason || x.error || 'Windows gave no reason.').</summary>
    public static string Why(JsonElement x)
    {
        string? r = Str(x, "reason");
        if (string.IsNullOrEmpty(r)) r = Str(x, "error");
        return Cap(string.IsNullOrEmpty(r) ? NoReason : r);
    }

    /// <summary>The names the log must not carry: app, title, exe and the exe without ".exe".</summary>
    public static string[] NamesOf(JsonElement x)
    {
        string exe = Str(x, "exe") ?? "";
        return new[] { Str(x, "app") ?? "", Str(x, "title") ?? "", exe, Regex.Replace(exe, @"\.exe$", "", RegexOptions.IgnoreCase) };
    }

    /// <summary>Every name (over one character, longest first, any case) replaced by "the window".</summary>
    public static string Unname(string? t, IEnumerable<string?>? names)
    {
        string text = t ?? "";
        if (names == null) return text;
        var ns = names.Where(n => n != null && n.Length > 1).Select(n => n!).Distinct(StringComparer.Ordinal)
                      .OrderByDescending(n => n.Length).ToList();
        foreach (string n in ns)
            text = Regex.Replace(text, Regex.Escape(n), "the window", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    /// <summary>"Could not maximize X" / "Could not minimize X" / "Could not snap X" for a refused move.</summary>
    public static string RefusedTitle(string? action, string app) =>
        "Could not " + (action == "maximize" ? "maximize" : action == "minimize" ? "minimize" : "snap") + " " + app;

    /// <summary>"<App> does not fit the <zone words>": cap() then the last character cut (snap.js's .replace(/.$/, '')).</summary>
    public static string NoFitTitle(string app, string? to)
    {
        string c = Cap(app);
        if (c.Length > 0) c = c.Substring(0, c.Length - 1);
        return c + " does not fit the " + (to != null && Zones.TryGetValue(to, out var z) ? z : "zone");
    }
}
