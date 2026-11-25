using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Mantle.Core.Look.Levitate;

/// <summary>A colour in 0..255 per channel, not rounded (a dimmed sample is rgb * (1 - dim)).</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public Rgb Times(double k) => new(R * k, G * k, B * k);
}

/// <summary>panelLift's answer (look-sync.js): <c>{ alpha, check, dim, film, text }</c>.</summary>
/// <param name="Alpha">the film alpha: 0, or a 2-decimal step of 0.04 up to 0.72</param>
/// <param name="Check">"on" | "off" (<c>glass.&lt;theme&gt;.dimAuto</c> false)</param>
/// <param name="Dim">the Dim in effect: the saved one if it is a number in 0.20..0.90, else the seed 0.20</param>
/// <param name="Film">the neutral to lay at Alpha: rgb(10, 12, 14) in dark, #F4F7F8 (244, 247, 248) in light</param>
/// <param name="Text">the Settings row, e.g. "Brightness check on: Dim 0.20, darkened 0.72 where the picture is bright"</param>
public sealed record PanelLiftResult(double Alpha, string Check, double Dim, (byte R, byte G, byte B) Film, string Text);

/// <summary>
/// The v2.1 brightness check (the legibility floor) of the levitated look, pure: what this panel shows behind its text, and
/// the neutral film that keeps text readable over it. One to one with look-sync v2.1's <c>liftAlpha(samples, dim, theme)</c>
/// and <c>panelLift(look, theme, samples)</c> (look-sync 5d9e4f6, the Engineering Manager's branch, not merged yet).
/// <list type="number">
/// <item>Sample the blurred copy THIS panel shows (soft if <see cref="Island.IsBig"/>, else light) on an 8 x 5 grid,
/// sample (i, j) at x + ((i + 0.5) / 8) * w, y + ((j + 0.5) / 5) * h, mapped into the bitmap through the cover geometry and
/// clamped to it (<see cref="Grid"/>).</item>
/// <item>Each sample times (1 - dim): Dim is a black wash in BOTH themes.</item>
/// <item>WCAG luminance; the valid samples sorted by it (a stable sort, as JS's): DARK brightest first, LIGHT darkest
/// first; the pick = sorted[floor(n * 0.1)], n the number of valid samples (index 4 of 40).</item>
/// <item>alpha = round2(k * 0.04) for the smallest k with alpha &gt;= 0.72 or the pick under the film at alpha reads:
/// DARK film rgb(10, 12, 14), reads when its luminance is &lt;= 0.066; LIGHT film #F4F7F8 (244, 247, 248), reads when its
/// luminance is &gt;= 0.386. The answer min(0.72, alpha). Over a dark area (dark) or a pale one (light) 0. Rounded to 2
/// decimals at every step (never added up in doubles), so the values are exact (0.72, not 0.7200000000000001).</item>
/// </list>
/// "Automatic" (look.json <c>glass.&lt;theme&gt;.dimAuto</c> absent or true) = the check on; false = off (film 0); the
/// switch is the theme's own: dark switches the dark film, light the light lift. Only one runs at a time.
/// look-sync's shared vectors (Mantle.Core.Tests Fixtures/look/v2/lift-v2.json, dark, and lift-light.json, light, both
/// rev 2.1) must all pass exactly.
/// Native draws the DARK film only: the light Levitate look is not built yet, and until it is, native keeps today's light
/// look and never lays the dark film in light (<see cref="ForPanel"/> is the dark check; LevitatePanel lays it in dark only).
/// </summary>
public static class Legibility
{
    /// The sampling grid: 8 across, 5 down.
    public const int Cols = 8, Rows = 5;
    /// The pick is the sample at floor(n * BrightRank) of the n sorted (index 4 of 40): brightest first in dark, darkest
    /// first in light (look-sync LIFT.quantile).
    public const double BrightRank = 0.1;

    /// look-sync's theme rule: "light" is the light lift, anything else (null, "dark", "Light", "system") is dark.
    public static bool IsLight(string? theme) => theme == "light";

    /// The neutral the check lays in this theme: rgb(10, 12, 14) in dark, #F4F7F8 (244, 247, 248) in light (panelLift's film).
    public static (byte R, byte G, byte B) FilmRgb(string? theme) => IsLight(theme)
        ? (LevitateTokens.LightFilmR, LevitateTokens.LightFilmG, LevitateTokens.LightFilmB)
        : (LevitateTokens.FilmR, LevitateTokens.FilmG, LevitateTokens.FilmB);

    /// sRGB channel (0..255, not rounded) to linear: v/255 &lt;= 0.04045 ? (v/255)/12.92 : ((v/255 + 0.055)/1.055)^2.4.
    public static double Lin(double v)
    {
        double c = v / 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// Relative luminance: 0.2126 lin(r) + 0.7152 lin(g) + 0.0722 lin(b).
    public static double L(Rgb c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

    /// The luminance of <paramref name="c"/> under this theme's film at alpha a: L(c * (1 - a) + FILM * a), FILM
    /// <see cref="FilmRgb"/> (look-sync's relLum(filmOver(rgb, a, film))). No theme = dark.
    public static double LumOf(Rgb c, double a, string? theme = null)
    {
        var f = FilmRgb(theme);
        return L(new Rgb(c.R * (1 - a) + f.R * a, c.G * (1 - a) + f.G * a, c.B * (1 - a) + f.B * a));
    }

    /// The pick still does not read at alpha a (look-sync's short): dark L &gt; 0.066, light L &lt; 0.386.
    static bool Short(Rgb pick, double a, bool light) => light
        ? LumOf(pick, a, "light") < LevitateTokens.LightFilmTarget
        : LumOf(pick, a) > LevitateTokens.FilmTarget;

    /// The dim the check uses: the given value if it is in 0..1, else 0.20 (look-sync's seed; NaN fails both tests).
    /// Not the slider clamp: that is <see cref="LevitateDim"/>'s 0.20..0.90; a dim of 0 is checked as 0.
    public static double CheckDim(double dim) => dim >= 0 && dim <= 1 ? dim : LevitateTokens.DimDefault;

    /// The alpha at step k: round2(k * 0.04), i.e. JS Math.round(k * 0.04 * 100) / 100 (a half rounds up).
    public static double Step(int k) => Math.Floor(k * LevitateTokens.FilmStep * 100 + 0.5) / 100;

    static double Ch(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// THE check, look-sync v2.1's <c>liftAlpha(samples, dim, theme)</c> one to one: the film alpha for a panel's samples
    /// (r, g, b 0..255, as read off the blurred copy, BEFORE dim) at this dim, in this theme ("light" = the light lift toward
    /// #F4F7F8, anything else = the dark film). Pure, never throws. A sample that is null, has fewer than 3 entries or a
    /// non-finite channel is skipped; channels are clamped to 0..255; a dim outside 0..1 (or NaN) is checked as 0.20 in
    /// both themes; no valid sample left = 0.
    public static double LiftAlpha(double[]?[]? samples, double dim, string? theme = null)
    {
        var seen = new List<Rgb>();
        if (samples != null)
            foreach (var s in samples) {
                if (s == null || s.Length < 3 || !double.IsFinite(s[0]) || !double.IsFinite(s[1]) || !double.IsFinite(s[2])) continue;
                seen.Add(new Rgb(Ch(s[0]), Ch(s[1]), Ch(s[2])));
            }
        return Alpha(seen, dim, IsLight(theme));
    }

    /// The check on, or (<paramref name="dimAuto"/> false: this theme's <c>glass.&lt;theme&gt;.dimAuto</c>) off: 0.
    public static double LiftAlpha(bool dimAuto, double[]?[]? samples, double dim, string? theme = null) =>
        dimAuto ? LiftAlpha(samples, dim, theme) : 0;

    /// <see cref="LiftAlpha(double[][], double, string)"/> over <see cref="Rgb"/> samples (what <see cref="Samples"/> reads), same rules.
    public static double Film(Rgb[]? samples, double dim, string? theme = null)
    {
        var seen = new List<Rgb>();
        if (samples != null)
            foreach (var c in samples) {
                if (!double.IsFinite(c.R) || !double.IsFinite(c.G) || !double.IsFinite(c.B)) continue;
                seen.Add(new Rgb(Ch(c.R), Ch(c.G), Ch(c.B)));
            }
        return Alpha(seen, dim, IsLight(theme));
    }

    /// Automatic = the check on, manual = off (film 0).
    public static double Film(bool auto, Rgb[]? samples, double dim, string? theme = null) => auto ? Film(samples, dim, theme) : 0;

    /// The valid, clamped samples (undimmed) to the alpha.
    static double Alpha(List<Rgb> seen, double dim, bool light)
    {
        int n = seen.Count;
        if (n == 0) return 0;
        double keep = 1 - CheckDim(dim);
        var s = new Rgb[n];
        var l = new double[n];
        var order = new int[n];
        for (int i = 0; i < n; i++) { s[i] = seen[i].Times(keep); l[i] = L(s[i]); order[i] = i; }
        // dark: brightest first; light: darkest first. Equal luminance keeps the input order (JS's Array.prototype.sort is
        // stable; C#'s is not, so ties break by index).
        if (light) Array.Sort(order, (p, q) => l[p] != l[q] ? l[p].CompareTo(l[q]) : p.CompareTo(q));
        else Array.Sort(order, (p, q) => l[q] != l[p] ? l[q].CompareTo(l[p]) : p.CompareTo(q));
        Rgb pick = s[order[(int)Math.Floor(n * BrightRank)]];
        int k = 0;
        while (Step(k) < LevitateTokens.FilmCap && Short(pick, Step(k), light)) k++;
        return Math.Min(LevitateTokens.FilmCap, Step(k));
    }

    static bool Num(JsonNode? v, double lo, double hi, out double d) => LookSyncCore.IsFinite(v, out d) && d >= lo && d <= hi;

    /// a2: n.toFixed(2) (the values here are finite).
    static string A2(double n) => n.ToString("0.00", CultureInfo.InvariantCulture);

    /// look-sync v2.1's <c>panelLift(look, theme, samples)</c> one to one: one panel's film in the theme in effect, with the
    /// Settings row. <paramref name="look"/>: the look values <c>{ glass }</c> (or shared()'s <c>{ glass: { v } }</c>);
    /// <paramref name="theme"/>: "light", anything else is dark; <paramref name="samples"/> as <see cref="LiftAlpha(double[][], double, string)"/>.
    /// Dim: <c>glass.&lt;theme&gt;.dim</c> if it is a number in 0.20..0.90, else the seed 0.20 (NOT clamped: look-sync's
    /// rule; <see cref="LevitateDim.Resolve"/> clamps). Check on (<c>dimAuto</c> absent or true): alpha =
    /// LiftAlpha(samples, dim, theme); off (<c>dimAuto</c> false): alpha 0, the panel is pure blur over the dimmed picture.
    /// Film: rgb(10, 12, 14) in dark, #F4F7F8 in light. A surface that has not built its light look does not call this in
    /// light. Never throws.
    public static PanelLiftResult PanelLift(JsonNode? look, string? theme, double[]?[]? samples)
    {
        string t = IsLight(theme) ? "light" : "dark";
        JsonObject? glass = look is JsonObject lo ? lo["glass"] as JsonObject : null;
        if (glass != null && glass["v"] is JsonObject gv && glass[t] is not JsonObject) glass = gv;   // shared() shape
        var g = glass?[t] as JsonObject;
        double dim = g != null && Num(g["dim"], LevitateTokens.DimMin, LevitateTokens.DimMax, out var gd) ? gd : LevitateTokens.DimDefault;
        var film = FilmRgb(t);
        if (g != null && LookSyncCore.IsBool(g["dimAuto"], out var auto) && !auto)
            return new PanelLiftResult(0, "off", dim, film, "Brightness check off: Dim " + A2(dim) + " only");
        double alpha = LiftAlpha(samples, dim, t);
        bool light = t == "light";
        string did = light ? "lightened" : "darkened", where = light ? "dark" : "bright", verb = light ? "lighten" : "darken";
        return new PanelLiftResult(alpha, "on", dim, film, alpha > 0
            ? "Brightness check on: Dim " + A2(dim) + ", " + did + " " + A2(alpha) + " where the picture is " + where
            : "Brightness check on: Dim " + A2(dim) + ", nothing to " + verb + " here");
    }

    /// The film alpha for display: Math.round(a * 100) / 100 (JS rounds a half up). The check's answers are already exact
    /// 2-decimal values, so this changes none of them.
    public static double Show(double a) => Math.Floor(a * 100 + 0.5) / 100;

    /// The 8 x 5 sampling grid of a panel at (x, y), w x h (DIP, on the monitor) in a bw x bh copy laid out by
    /// <paramref name="fit"/>: entry i * 5 + j is sample (i, j) at x + ((i + 0.5) / 8) * w, y + ((j + 0.5) / 5) * h, as a
    /// bitmap pixel clamped to the bitmap (<see cref="Island.ToBitmap"/>).
    public static (int X, int Y)[] Grid(CoverFit fit, int bw, int bh, double x, double y, double w, double h)
    {
        var g = new (int X, int Y)[Cols * Rows];
        for (int i = 0; i < Cols; i++)
            for (int j = 0; j < Rows; j++)
                g[i * Rows + j] = Island.ToBitmap(fit, bw, bh, x + ((i + 0.5) / Cols) * w, y + ((j + 0.5) / Rows) * h);
        return g;
    }

    /// The panel's 40 samples off a blurred copy (BGRA), in <see cref="Grid"/>'s order.
    public static Rgb[] Samples(FrostBitmap copy, CoverFit fit, double x, double y, double w, double h)
    {
        var g = Grid(fit, copy.W, copy.H, x, y, w, h);
        var s = new Rgb[g.Length];
        for (int k = 0; k < g.Length; k++) {
            int o = (g[k].Y * copy.W + g[k].X) * 4;
            s[k] = new Rgb(copy.Bgra[o + 2], copy.Bgra[o + 1], copy.Bgra[o]);
        }
        return s;
    }

    /// The whole DARK check for one panel: the copy it shows (soft if big, else light), its samples, the dark film for this
    /// dim and mode. <paramref name="vw"/> x <paramref name="vh"/> is the monitor (DIP); (x, y, w, h) the panel's layout rect
    /// on it. Dark only on purpose (lane r11-lift21): native has no light Levitate look yet, so nothing asks for the light
    /// lift here; when the light look is built, pass the theme through to <see cref="Film(Rgb[], double, string)"/>.
    public static double ForPanel(FrostBitmap light, FrostBitmap soft, double vw, double vh, double x, double y, double w, double h, double dim, bool auto)
    {
        if (!auto) return 0;
        var copy = Island.IsBig(w, h) ? soft : light;
        var fit = Island.Cover(copy.W, copy.H, vw, vh);
        return Film(Samples(copy, fit, x, y, w, h), dim);
    }
}
