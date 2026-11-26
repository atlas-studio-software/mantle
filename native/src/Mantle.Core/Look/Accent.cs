using System;
using System.Globalization;

namespace Mantle.Core.Look;

/// <summary>The colours one accent hue gives in one theme (lib/accent.js <c>tokens()</c>).</summary>
public sealed record AccentTokens(string Acc, string Strong, string On, string Soft, string Line, string Select, string Glow, byte R, byte G, byte B);

/// <summary>
/// C# port of look-sync accent.js at 6e7e729 (v2.2; Look Sync contract section 3 and "Accent profiles", 2026-11-25; before
/// it c29ba47, and the steel-only accent.js at 4f82c4b). A custom hue keeps the EXACT saturation and lightness of the authored hexes, and at a
/// profile's base hue (per theme) the tokens ARE the authored hexes. Two profiles:
/// <list type="bullet">
/// <item>steel (the default, the v1 look): dark #9fb4cc / #b7c8db / #11161c, light #4d6784 / #3c536d / white; base 212.</item>
/// <item>islands (the v2 islands look): dark ice blue #a9e7ed / #c4f0f4 / #052a2f, base 185; light (v2.2) LIGHT.md's deep
/// teal #083f49 / #05313a / #f4f7f8, base 189.</item>
/// </list>
/// Any other profile name (absent included) is steel. Hue null means "keep Mantle's own accent" (no tokens).
/// The per-theme rule (CONTRACT v2.2): a caller passes "islands" only in a theme whose islands look it has built, and steel
/// (or nothing) in the other, so nobody's light changes until that light look is built. LookHost passes no profile.
/// Pinned by the shared vectors (Mantle.Core.Tests/Look/accent-v2.json, look-sync test/fixtures at 6e7e729, rev 2).
/// </summary>
public static class Accent
{
    public const int DefaultHue = 212;
    /// accent.js ISLANDS_HUE: ice blue, profile islands' dark base hue (light is 189: <see cref="BaseHue"/>).
    public const int IslandsHue = 185;
    public const string Steel = "steel", Islands = "islands";

    /// The reason a bad hue is refused, word for word (lib/look-host.js#setAccent, LOOK-033).
    public const string BadHue = "accent hue must be null or an integer 0..360";

    /// accent.js AUTHORED: acc, strong and on (null = white) per profile and theme.
    sealed record Authored(int Acc, int Strong, int? On, int BaseHue)
    {
        public readonly (double S, double L) AccSl = SlOf(Acc), StrongSl = SlOf(Strong);
        public readonly (double S, double L)? OnSl = On is int o ? SlOf(o) : null;
    }

    static readonly Authored SteelDark = new(0x9fb4cc, 0xb7c8db, 0x11161c, 212), SteelLight = new(0x4d6784, 0x3c536d, null, 212);
    // islands light (v2.2): LIGHT.md section 4's deep teal, strong = its hover/pressed; base 189 = #083f49's own hue (189.23,
    // rounded to the whole degree accent.hue holds)
    static readonly Authored IslandsDark = new(0xa9e7ed, 0xc4f0f4, 0x052a2f, 185), IslandsLight = new(0x083f49, 0x05313a, 0xf4f7f8, 189);

    /// accent.js profileOf: "islands" or "steel" (anything else, null included).
    public static string ProfileOf(string? profile) => profile == Islands ? Islands : Steel;

    /// accent.js baseHue(profile, theme): where Custom starts, and where the tokens are exactly the authored hexes
    /// (dark unless <paramref name="theme"/> is "light").
    public static int BaseHue(string? profile, string? theme = null) => Pick(profile, theme != "light").BaseHue;

    static Authored Pick(string? profile, bool dark) => ProfileOf(profile) == Islands ? (dark ? IslandsDark : IslandsLight) : (dark ? SteelDark : SteelLight);

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

    static (byte R, byte G, byte B) RgbOf(int n) => ((byte)(n >> 16 & 255), (byte)(n >> 8 & 255), (byte)(n & 255));

    /// <summary>accent.js tokens(hue, theme, profile): null for a null hue; throws for a bad one (callers check
    /// <see cref="ValidHue"/>). <paramref name="profile"/>: "islands", anything else (null included) is steel.</summary>
    public static AccentTokens? Tokens(int? hue, string theme, string? profile = null)
    {
        if (hue == null) return null;
        if (!ValidHue(hue.Value)) throw new ArgumentOutOfRangeException(nameof(hue), "accent hue must be an integer 0..360, got " + hue);
        bool dark = theme != "light";
        var au = Pick(profile, dark);
        bool exact = hue.Value == au.BaseHue;   // the base hue IS the authored colour, to the last digit
        var acc = exact ? RgbOf(au.Acc) : HslToRgb(hue.Value, au.AccSl.S, au.AccSl.L);
        var strong = exact ? RgbOf(au.Strong) : HslToRgb(hue.Value, au.StrongSl.S, au.StrongSl.L);
        var on = au.On is int o ? (exact ? RgbOf(o) : HslToRgb(hue.Value, au.OnSl!.Value.S, au.OnSl!.Value.L)) : ((byte)255, (byte)255, (byte)255);
        var al = Alpha(dark);
        return new AccentTokens(Hex(acc), Hex(strong), Hex(on), Rgba(acc, al.Soft), Rgba(acc, al.Line), Rgba(acc, al.Select), Rgba(acc, al.Glow), acc.R, acc.G, acc.B);
    }
}
