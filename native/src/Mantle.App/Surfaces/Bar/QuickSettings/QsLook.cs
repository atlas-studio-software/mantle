using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Bar.QuickSettings;

/// <summary>
/// Lane r15-quick-settings: quick settings in the islands look (apps-r3/mantle-bar panels/quick.css), read in code.
/// The numbers are Theme/LvQuick.xaml's (pinned by Mantle.Core.Tests Look/QuickSpecTests), each with the same value as
/// its fallback here so a missing key never breaks the panel (it is said once). The inks: the panel's code still names
/// the Electron look's Lg* tokens; in the islands look each name is turned into its LvBar* twin (Theme/Levitate.xaml,
/// both themes, through Shell/Levitate/IslandsInk) and the accent is IslandsInk.Accent. The Electron look
/// (MANTLE_TEST_GLASS=electron, the parity pairs) keeps the Lg* tokens and the full layout.
/// </summary>
internal static class QsLook
{
    /// The islands look is on (not the Electron pairs' face): Surfaces.Bar.Frame.BarGeometry.Islands.
    public static bool Islands => global::Mantle.App.Surfaces.Bar.Frame.BarGeometry.Islands;

    static readonly HashSet<string> Said = new();

    static object? Top(string key)
    {
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, 0) is { } v) return v;
        return null;
    }
    static object? Find(ResourceDictionary d, string key, int depth)
    {
        if (d is Microsoft.UI.Xaml.Controls.XamlControlsResources) return null;
        if (d.TryGetValue(key, out var v)) return v;
        if (depth >= 4) return null;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, depth + 1) is { } x) return x;
        return null;
    }

    static void Missing(string key, Action<string>? log)
    {
        if (Said.Add(key)) log?.Invoke($"qs: Theme/LvQuick.xaml has no {key}, so the prototype's number is used");
    }

    /// An x:Double of LvQuick.xaml.
    public static double N(string key, double fallback, Action<string>? log = null)
    {
        if (Top(key) is double d) return d;
        Missing(key, log);
        return fallback;
    }

    /// A Thickness of LvQuick.xaml.
    public static Thickness T(string key, Thickness fallback, Action<string>? log = null)
    {
        if (Top(key) is Thickness t) return t;
        Missing(key, log);
        return fallback;
    }

    /// The Lg* name the panel's code uses, as the islands token that stands in for it (LIGHT.md section 4 names).
    /// LgInk3 is a sub line or a value: readable text is never text-3 in the islands look, so it is text-2.
    static readonly Dictionary<string, string> Twin = new(StringComparer.Ordinal) {
        ["LgInk"] = "LvBarInk", ["LgInk2"] = "LvBarInk2", ["LgInk3"] = "LvBarInk2",
        ["LgTile"] = "LvBarFillHover", ["LgCard"] = "LvBarFillHover", ["LgField"] = "LvBarFillHover", ["LgHover"] = "LvBarFillHover",
        ["LgPress"] = "LvBarFillOn", ["LgSunk"] = "LvBarSunk",
        ["LgEdgeOut"] = "LvBarHair", ["LgEdgeIn"] = "LvBarHair", ["LgEdgeStrong"] = "LvBarHair", ["LgDivider"] = "LvBarHair",
        ["LgWarn"] = "LvBarWarn",
        ["LgAcc"] = "LvBarAccent", ["LgAccLine"] = "LvBarAccent", ["LgAccOn"] = "LvBarAccentInk",
    };

    /// The resource name a XAML string ({ThemeResource ...}) should use for <paramref name="lg"/> in this look.
    public static string Key(string lg) => Islands && Twin.TryGetValue(lg, out var lv) ? lv : lg;

    /// The islands brush for an Lg* name, or null when the name has no twin (the caller keeps its Lg* token).
    public static Brush? Ink(bool dark, string lg, Action<string>? log)
    {
        if (lg == "LgFill0") return new SolidColorBrush();
        if (lg is "LgAcc" or "LgAccLine") return global::Mantle.App.Shell.Levitate.IslandsInk.AccentBrush(dark, log);
        if (lg == "LgAccOn") return global::Mantle.App.Shell.Levitate.IslandsInk.AccentInkBrush(dark, log);
        if (lg == "LgAccSoft") { var a = global::Mantle.App.Shell.Levitate.IslandsInk.Accent(dark, log).Acc; a.A = (byte)(dark ? 41 : 31); return new SolidColorBrush(a); }   // look.css 23 / light.css 23: .16 / .12
        if (lg.StartsWith("LvQs", StringComparison.Ordinal) || lg.StartsWith("LvBar", StringComparison.Ordinal)) return global::Mantle.App.Shell.Levitate.IslandsInk.Token(dark, lg, log);
        return Twin.TryGetValue(lg, out var lv) ? global::Mantle.App.Shell.Levitate.IslandsInk.Token(dark, lv, log) : null;
    }
}
