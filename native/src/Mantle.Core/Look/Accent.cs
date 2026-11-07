using System;
using System.Globalization;

namespace Mantle.Core.Look;

/// <summary>The colours one accent hue gives in one theme (lib/accent.js <c>tokens()</c>).</summary>
public sealed record AccentTokens(string Acc, string Strong, string On, string Soft, string Line, string Select, string Glow, byte R, byte G, byte B);

/// <summary>
/// C# port of lib/accent.js (vendored from the look-sync project, accent.js at 4f82c4b; Look Sync contract
/// section 3). A custom hue keeps the EXACT saturation and lightness of the authored hexes (dark #9fb4cc /
/// #b7c8db / #11161c, light #4d6784 / #3c536d / white), so hue 212 gives back the shipped steel blue.
/// Hue null means "keep Mantle's own accent" (no tokens).
/// </summary>
public static class Accent
{
    public const int DefaultHue = 212;

    /// The reason a bad hue is refused, word for word (lib/look-host.js#setAccent, LOOK-033).
    public const string BadHue = "accent hue must be null or an integer 0..360";

    static readonly (double S, double L) DarkAcc = SlOf(0x9fb4cc), DarkStrong = SlOf(0xb7c8db), DarkOn = SlOf(0x11161c);
    static readonly (double S, double L) LightAcc = SlOf(0x4d6784), LightStrong = SlOf(0x3c536d);

    /// ALPHA in accent.js: soft, line, select, glow per theme.
    public static (double Soft, double Line, double Select, double Glow) Alpha(bool dark)
        => dark ? (0.14, 0.42, 0.32, 0.22) : (0.11, 0.40, 0.20, 0.22);

    static (double, double) SlOf(int n)
    {
        double r = (n >> 16 & 255) / 255.0, g = (n >> 8 & 255) / 255.0, b = (n & 255) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (s, l);
    }

    /// accent.js validHue: an integer 0..360.
    public static bool ValidHue(double hue) => hue >= 0 && hue <= 360 && Math.Floor(hue) == hue;

    static int JsRound(double v) => (int)Math.Floor(v + 0.5);   // Math.round in JavaScript

    public static (byte R, byte G, byte B) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
            : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return ((byte)JsRound((r + m) * 255), (byte)JsRound((g + m) * 255), (byte)JsRound((b + m) * 255));
    }

    static string Hex((byte R, byte G, byte B) c) => "#" + c.R.ToString("x2", CultureInfo.InvariantCulture) + c.G.ToString("x2", CultureInfo.InvariantCulture) + c.B.ToString("x2", CultureInfo.InvariantCulture);

    static string Rgba((byte R, byte G, byte B) c, double a)
        => "rgba(" + c.R + ", " + c.G + ", " + c.B + ", " + Data.JsJson.Number(a) + ")";

    /// <summary>accent.js tokens(hue, theme): null for a null hue; throws for a bad one (callers check <see cref="ValidHue"/>).</summary>
    public static AccentTokens? Tokens(int? hue, string theme)
    {
        if (hue == null) return null;
        if (!ValidHue(hue.Value)) throw new ArgumentOutOfRangeException(nameof(hue), "accent hue must be an integer 0..360, got " + hue);
        bool dark = theme != "light";
        var acc = HslToRgb(hue.Value, dark ? DarkAcc.S : LightAcc.S, dark ? DarkAcc.L : LightAcc.L);
        var strong = HslToRgb(hue.Value, dark ? DarkStrong.S : LightStrong.S, dark ? DarkStrong.L : LightStrong.L);
        var on = dark ? HslToRgb(hue.Value, DarkOn.S, DarkOn.L) : ((byte)255, (byte)255, (byte)255);
        var al = Alpha(dark);
        return new AccentTokens(Hex(acc), Hex(strong), Hex(on), Rgba(acc, al.Soft), Rgba(acc, al.Line), Rgba(acc, al.Select), Rgba(acc, al.Glow), acc.R, acc.G, acc.B);
    }
}
