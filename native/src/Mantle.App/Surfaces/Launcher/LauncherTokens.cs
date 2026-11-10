using System;
using System.Collections.Generic;
using Mantle.Core.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The launcher's design tokens (Theme/Tokens.xaml, the ui/shared/glass-light.css values) read once per theme from the
/// app's resources and handed out as brushes. The launcher builds its elements in code (no XamlReader / FindName: under
/// Native AOT an element handed back as a plain object can come back as a bare COM object, and the cast fails), so it
/// sets every brush itself on a theme change instead of leaning on {ThemeResource}.
/// </summary>
internal sealed class LauncherTokens
{
    readonly Dictionary<string, Color> _light = new(), _dark = new();
    readonly ILogSink _log;
    readonly HashSet<string> _missing = new();

    /// No fill at all (alpha 0): not a colour, so not a token.
    public static readonly Color None = default;

    public LauncherTokens(ILogSink log) { _log = log; }

    /// A token's colour in the given theme (transparent, with one log line, when Tokens.xaml has no such key).
    public Color Of(string key, bool dark)
    {
        var cache = dark ? _dark : _light;
        if (cache.TryGetValue(key, out var c)) return c;
        string theme = dark ? "Dark" : "Light";
        object? v = null;
        foreach (var md in Application.Current.Resources.MergedDictionaries)
            if (md is not Microsoft.UI.Xaml.Controls.XamlControlsResources && Find(md, theme, key, 0) is object found) { v = found; break; }
        if (v is SolidColorBrush b) c = b.Color;
        else
        {
            c = None;
            if (_missing.Add(theme + key)) _log.Line("Tokens.xaml has no " + theme + " " + key + " brush, so it is drawn transparent");
        }
        cache[key] = c;
        return c;
    }

    readonly Dictionary<string, SolidColorBrush> _brushLight = new(), _brushDark = new();

    /// One shared brush per token and theme: a row re-render hands out the same objects instead of making ~10 brushes
    /// a row (the keystroke-to-rows path). Never mutate a brush from here; make your own for that.
    public SolidColorBrush Brush(string key, bool dark)
    {
        var cache = dark ? _brushDark : _brushLight;
        if (!cache.TryGetValue(key, out var b)) cache[key] = b = new SolidColorBrush(Of(key, dark));
        return b;
    }

    static object? Find(ResourceDictionary d, string theme, string key, int depth)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out object? td) && td is ResourceDictionary rd && rd.TryGetValue(key, out object? v))
            return v;
        if (depth >= 4) return null;
        foreach (var md in d.MergedDictionaries)
            if (Find(md, theme, key, depth + 1) is object f) return f;
        return null;
    }

    /// Paints layers bottom to top (CSS background layers) into the one colour that looks the same over anything.
    public static Color Over(params Color[] layers)
    {
        double a = 0, r = 0, g = 0, bl = 0;   // premultiplied
        foreach (var l in layers)
        {
            double la = l.A / 255.0;
            r = l.R * la + r * (1 - la); g = l.G * la + g * (1 - la); bl = l.B * la + bl * (1 - la);
            a = la + a * (1 - la);
        }
        if (a <= 0) return None;
        return new Color { A = (byte)Math.Round(a * 255), R = (byte)Math.Round(r / a), G = (byte)Math.Round(g / a), B = (byte)Math.Round(bl / a) };
    }
}
