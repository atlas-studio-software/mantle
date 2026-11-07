using System;
using System.Collections.Generic;

namespace Mantle.Core.Keys;

// The names the keyboard hook understands, in the protocol's order (lib/features.js CHORDS / KEYS,
// sidecar/AltTab.cs KeyMachine.ChordNames / KeyNames). The table only grows at the end, in the order
// the sidecar learned each chord, so the same set of chords is always the same request.
//
// Exact chords: Win+I, Win+E, Win+R, Win+X, Win+G, Win+P, Win+. and Win+; are taken only with no other
// modifier held (Win+Shift+E stays Windows'); Ctrl+Shift+Esc only with exactly Ctrl+Shift. The bare
// volume keys are chords too (Windows' own flyout cannot be hidden on 25H2), and a held one is one
// event per repeat. Observed keys are never taken: every keydown is reported and Windows still acts.
public static class ChordTable
{
    /// Every chord a feature may claim, in the sidecar's order.
    public static readonly IReadOnlyList<string> Chords = new[]
    {
        "Win+S", "Win+V", "Win+Tab", "Win+Left", "Win+Right", "Win+Up", "Win+Down", "Win+Z", "Win+Shift+S", "PrintScreen",
        "VolumeUp", "VolumeDown", "VolumeMute", "Win+Alt+T", "Win+Ctrl+T", "Win+Alt+N", "Win+I", "Ctrl+Shift+Esc", "Win+E", "Win+R",
        "Win+X", "Win+G", "Win+P", "Win+.", "Win+;",
    };

    /// Every key a feature may observe (reported, never swallowed), in the sidecar's order.
    public static readonly IReadOnlyList<string> ObservedKeys = new[] { "VolumeUp", "VolumeDown", "VolumeMute" };

    public static bool IsChord(string? name) => name != null && IndexOf(Chords, name) >= 0;

    public static bool IsObservedKey(string? name) => name != null && IndexOf(ObservedKeys, name) >= 0;

    /// The chords of <paramref name="set"/> in table order (the same set is always the same request).
    public static string[] InTableOrder(IReadOnlyCollection<string> set, IReadOnlyList<string> table)
    {
        var out_ = new List<string>(set.Count);
        foreach (var c in table) if (Contains(set, c)) out_.Add(c);
        return out_.ToArray();
    }

    static bool Contains(IReadOnlyCollection<string> set, string c)
    {
        if (set is ISet<string> s) return s.Contains(c);
        foreach (var x in set) if (string.Equals(x, c, StringComparison.Ordinal)) return true;
        return false;
    }

    static int IndexOf(IReadOnlyList<string> list, string name)
    {
        for (var i = 0; i < list.Count; i++) if (string.Equals(list[i], name, StringComparison.Ordinal)) return i;
        return -1;
    }
}
