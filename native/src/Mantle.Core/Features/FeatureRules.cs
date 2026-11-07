using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mantle.Core.Keys;
using Mantle.Core.State;

namespace Mantle.Core.Features;

/// <summary>What the keyboard side knows when a feature's piece is worked out (lib/features.js K).</summary>
/// <param name="Test">A --test run: the hook is never asked for anything.</param>
/// <param name="Live">Mantle owns the shell (applyLive ran, released did not).</param>
/// <param name="ReleasedWhy">Why the keys are Windows' after a restore, else null.</param>
/// <param name="PlatformDown">Why the platform cannot be asked, else null.</param>
/// <param name="Taken">The chords the hook last said it holds, or null when it has not said.</param>
/// <param name="Error">The last refusal of the hook call, or null.</param>
/// <param name="Applying">A hook call is in flight.</param>
public sealed record KeysView(bool Test, bool Live, string? ReleasedWhy, string? PlatformDown, IReadOnlySet<string>? Taken, string? Error, bool Applying);

/// <summary>One feature's facts, as the piece needs them.</summary>
public sealed record FeatureFacts(string Label, bool On, bool Ok, string Reason, IReadOnlyList<string> Chords, bool Stopped, string? Resting, string? KeysAway, string? SettingsNote);

/// <summary>lib/features.js validate + keysPiece/pieceWords, PURE.</summary>
public static class FeatureRules
{
    static readonly Regex NameRe = new("^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant);

    /// Names a feature may not take: Mantle's own command prefixes and event names (a feature's name is its command prefix).
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        "features", "state", "sys", "windows", "bar", "launcher", "desktop", "switcher", "notifs",
        "qs", "wallpaper", "shell", "tray", "media", "battery", "volume", "net", "apps", "mantle", "test", "theme",
    };

    /// <summary>Null, or the plain-words reason a descriptor breaks the feature contract.</summary>
    public static string? Validate(FeatureDescriptor? d)
    {
        if (d == null) return "it is not a feature descriptor";
        if (d.Name == null || !NameRe.IsMatch(d.Name)) return $"its name \"{d.Name}\" is not lower-case letters and digits";
        if (Reserved.Contains(d.Name)) return $"its name \"{d.Name}\" is one of Mantle's own command prefixes";
        if (string.IsNullOrWhiteSpace(d.Label)) return "it has no label (the tray checkbox text)";
        foreach (var (key, list, known) in new[] { ("chords", d.Chords, ChordTable.Chords), ("observe", d.Observe, ChordTable.ObservedKeys) })
        {
            if (list == null) return $"{key} must be a list";
            foreach (var n in list)
                if (!known.Contains(n)) return $"{key} names \"{n}\", which is not one of {string.Join(", ", known)}";
            if (list.Distinct(StringComparer.Ordinal).Count() != list.Count) return $"{key} lists the same name twice";
        }
        var both = d.Chords.FirstOrDefault(c => d.Observe.Contains(c));
        if (both != null) return $"it lists {both} as a chord AND an observed key (a taken key is already delivered to it)";
        if (d.Create == null) return "it has no way to make an instance (Create)";
        return FeatureOptions.Validate(d.Options);
    }

    /// <summary>The state piece <c>features.&lt;name&gt;</c> (lib/features.js pieceWords).</summary>
    public static FeaturePiece Piece(FeatureFacts f, KeysView x)
    {
        var p = KeysPiece(f, x);
        if (f.SettingsNote != null && p.Broken == null) p = p with { Broken = f.SettingsNote };
        return p;
    }

    static FeaturePiece KeysPiece(FeatureFacts f, KeysView x)
    {
        var resting = f.Resting != null;
        var stopped = f.Stopped && !resting;
        FeaturePiece P(bool keys, string why, string? broken) => new(f.Label, f.On, f.Ok, f.Reason ?? "", keys, why, broken, stopped, resting);
        if (f.Chords.Count == 0) return P(false, "it takes no keys from Windows", null);
        var list = string.Join(", ", f.Chords);
        if (resting) return P(false, $"{list}: Windows' own while Mantle rests ({f.Resting})", null);
        if (f.Stopped) return P(false, $"{list}: Windows' own (the feature was stopped)", null);
        if (!f.On) return P(false, $"{list}: Windows' own (turned off in the tray menu)", null);
        if (!f.Ok) return P(false, $"{list}: Windows' own until it is healthy again", null);
        if (f.KeysAway != null) return P(false, $"{list}: Windows' own for now ({f.KeysAway})", null);
        if (x.Test) return P(false, $"{list}: Windows' own (test mode never hooks the keyboard; --test-chord or features.fire drives it)", null);
        if (!x.Live) return P(false, $"{list}: Windows' own ({x.ReleasedWhy ?? "Mantle has not taken the shell yet"})", null);
        if (x.PlatformDown != null) return P(false, $"{list}: Windows' own ({x.PlatformDown})", null);
        if (x.Taken != null && f.Chords.All(x.Taken.Contains)) return P(true, $"{list}: Mantle's", null);
        if (x.Error != null) { var w = $"{list}: still Windows' own: {x.Error}"; return P(false, w, w); }
        if (x.Applying) return P(false, $"{list}: being taken from Windows", null);
        var why = $"{list}: still Windows' own: the sidecar has not confirmed the keyboard hook took them";
        return P(false, why, why);
    }

    /// "resting while VALORANT runs".
    public static string RestWords(string why) => $"resting while {why}";

    /// lib/features.js oneLine: newlines flattened, trimmed.
    public static string OneLine(string? s) => Regex.Replace(s ?? "", @"\s*[\r\n]+\s*", " ").Trim();

    /// lib/features.js fmtS.
    public static string Seconds(TimeSpan t) => t.TotalMilliseconds >= 1000 ? $"{Math.Round(t.TotalSeconds)} s" : $"{(long)t.TotalMilliseconds} ms";
}
