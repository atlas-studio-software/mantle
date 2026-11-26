// IslandsInk.cs - the islands look's theme-dependent inks, read in code (lane r15-bar-light): Theme/Levitate.xaml's
// LvBar* ThemeDictionaries block (dark and light, apps-r3/LIGHT.md section 4) for a given theme, and the islands accent.
// Used only where the islands look is built: the bar's three islands (BarWindow.Islands.cs and the bar's glyph code),
// the levitated bar pop-ups (Downloads today) and LevitatePanel's light hover sheen. Everything else keeps Tokens.xaml's
// Lg* inks and the steel accent.
//
// The accent: Mantle.Core Accent's "islands" profile at its base hue in both themes (accent.js tokens(hue, theme,
// "islands"), look-sync v2.2: acc, strong as the hover / pressed fill, on as the ink on it). Dark is base 185 (#a9e7ed /
// #c4f0f4 on #052a2f), light base 189 (LIGHT.md 4 and 4.2: #083f49 / #05313a on #f4f7f8). There is no local copy:
// Theme/Levitate.xaml holds no LvBarAccent* brush (BarLightTokensTests pins both, and Core's output in light).
using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using CoreAccent = global::Mantle.Core.Look.Accent;

namespace Mantle.App.Shell.Levitate;

internal static class IslandsInk
{
    static readonly HashSet<string> Missing = new();

    /// The LvBar* token of a theme (Theme/Levitate.xaml's ThemeDictionaries, merged through Theme.xaml), or null.
    public static Brush? Find(bool dark, string key)
    {
        string theme = dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d is not Microsoft.UI.Xaml.Controls.XamlControlsResources && Find(d, key, theme, 0) is Brush b) return b;
        return null;
    }

    /// The token as a brush; a missing one is said once (in <paramref name="log"/>) and drawn clear.
    public static Brush Token(bool dark, string key, Action<string>? log = null)
    {
        if (Find(dark, key) is Brush b) return b;
        if (Missing.Add(key + (dark ? "/dark" : "/light")))
            log?.Invoke($"islands ink: Theme/Levitate.xaml has no {(dark ? "Dark" : "Light")} {key}, so that part is drawn clear");
        return new SolidColorBrush();
    }

    /// The token's colour (a clear colour when it is missing or not a solid brush).
    public static global::Windows.UI.Color Color(bool dark, string key, Action<string>? log = null)
        => Token(dark, key, log) is SolidColorBrush s ? s.Color : default;

    static object? Find(ResourceDictionary d, string key, string theme, int depth)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        if (depth >= 4) return null;   // App.xaml -> Theme.xaml -> Levitate.xaml is two levels; the cap only stops a cycle
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme, depth + 1) is { } x) return x;
        return null;
    }

    /// The islands accent of a theme: the accent, its hover / pressed fill and the ink on it (see the file's head).
    public static (global::Windows.UI.Color Acc, global::Windows.UI.Color Hover, global::Windows.UI.Color On) Accent(bool dark, Action<string>? log = null)
    {
        string theme = dark ? "dark" : "light";
        var t = CoreAccent.Tokens(CoreAccent.BaseHue(CoreAccent.Islands, theme), theme, CoreAccent.Islands)!;   // a base hue: never null
        return (Rgb(t.Acc), Rgb(t.Strong), Rgb(t.On));
    }

    /// A new brush in the islands accent of a theme.
    public static SolidColorBrush AccentBrush(bool dark, Action<string>? log = null) => new(Accent(dark, log).Acc);

    /// A new brush in the ink that sits on the islands accent.
    public static SolidColorBrush AccentInkBrush(bool dark, Action<string>? log = null) => new(Accent(dark, log).On);

    /// Core's "#rrggbb" as an opaque colour.
    static global::Windows.UI.Color Rgb(string hex)
    {
        int n = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new global::Windows.UI.Color { A = 255, R = (byte)((n >> 16) & 255), G = (byte)((n >> 8) & 255), B = (byte)(n & 255) };
    }
}
