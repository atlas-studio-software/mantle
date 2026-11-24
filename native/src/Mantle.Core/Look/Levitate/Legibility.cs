using System;
using System.Collections.Generic;

namespace Mantle.Core.Look.Levitate;

/// <summary>A colour in 0..255 per channel, not rounded (a dimmed sample is rgb * (1 - dim)).</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public Rgb Times(double k) => new(R * k, G * k, B * k);
}

/// <summary>
/// The v2 brightness check (the legibility floor) of the levitated look (prototype quartz-home/app.js, 2026-11-24), pure:
/// what this panel shows behind its text, and the extra neutral film rgba(10, 12, 14, a) that keeps text readable over it.
/// <list type="number">
/// <item>Sample the blurred copy THIS panel shows (soft if <see cref="Island.IsBig"/>, else light) on an 8 x 5 grid,
/// sample (i, j) at x + ((i + 0.5) / 8) * w, y + ((j + 0.5) / 5) * h, mapped into the bitmap through the cover geometry and
/// clamped to it (<see cref="Grid"/>).</item>
/// <item>Each sample times (1 - dim): what is behind is the picture as dimmed.</item>
/// <item>WCAG luminance; the valid samples sorted by it, brightest first (a stable sort, as JS's); bright =
/// sorted[floor(n * 0.1)], n the number of valid samples (index 4 of 40).</item>
/// <item>alpha = round2(k * 0.04) for the smallest k with alpha &gt;= 0.56 or lumOf(bright, alpha) &lt;= 0.163; the answer
/// min(0.56, alpha). Over a dark area 0. Rounded to 2 decimals at every step, so the values are exact (0.48, not the
/// 0.4799999999999999 that adding 0.04 in doubles gives).</item>
/// </list>
/// "Automatic" (look.json <c>glass.&lt;theme&gt;.dimAuto</c> absent or true) = the check on; false = off (film 0).
/// A one-to-one port of look-sync v2's <c>liftAlpha(samples, dim)</c> (look-sync 9a53e89): <see cref="LiftAlpha(double[][], double)"/>
/// over plain arrays; look-sync's test vectors (Mantle.Core.Tests Fixtures/look/v2/lift-v2.json) must all pass.
/// </summary>
public static class Legibility
{
    /// The sampling grid: 8 across, 5 down.
    public const int Cols = 8, Rows = 5;
    /// The bright sample is the one at floor(n * BrightRank) of the n sorted brightest first (index 4 of 40).
    public const double BrightRank = 0.1;

    /// sRGB channel (0..255, not rounded) to linear: v/255 &lt;= 0.04045 ? (v/255)/12.92 : ((v/255 + 0.055)/1.055)^2.4.
    public static double Lin(double v)
    {
        double c = v / 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// Relative luminance: 0.2126 lin(r) + 0.7152 lin(g) + 0.0722 lin(b).
    public static double L(Rgb c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);

    /// The luminance of <paramref name="c"/> under the film at alpha a: L(c * (1 - a) + FILM * a).
    public static double LumOf(Rgb c, double a) => L(new Rgb(
        c.R * (1 - a) + LevitateTokens.FilmR * a,
        c.G * (1 - a) + LevitateTokens.FilmG * a,
        c.B * (1 - a) + LevitateTokens.FilmB * a));

    /// The dim the check uses: the given value if it is in 0..1, else 0.20 (look-sync's seed; NaN fails both tests).
    /// Not the slider clamp: that is <see cref="LevitateDim"/>'s 0.20..0.90; a dim of 0 is checked as 0.
    public static double CheckDim(double dim) => dim >= 0 && dim <= 1 ? dim : LevitateTokens.DimDefault;

    /// The alpha at step k: round2(k * 0.04), i.e. JS Math.round(k * 0.04 * 100) / 100 (a half rounds up).
    public static double Step(int k) => Math.Floor(k * LevitateTokens.FilmStep * 100 + 0.5) / 100;

    static double Ch(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// THE check, look-sync v2's <c>liftAlpha</c> one to one: the film alpha for a panel's samples (r, g, b 0..255, as read
    /// off the blurred copy, BEFORE dim) at this dim. Pure, never throws. A sample that is null, has fewer than 3 entries or
    /// a non-finite channel is skipped; channels are clamped to 0..255; no valid sample left = 0.
    public static double LiftAlpha(double[]?[]? samples, double dim)
    {
        var seen = new List<Rgb>();
        if (samples != null)
            foreach (var s in samples) {
                if (s == null || s.Length < 3 || !double.IsFinite(s[0]) || !double.IsFinite(s[1]) || !double.IsFinite(s[2])) continue;
                seen.Add(new Rgb(Ch(s[0]), Ch(s[1]), Ch(s[2])));
            }
        return Alpha(seen, dim);
    }

    /// The check on, or (<paramref name="dimAuto"/> false) off: 0.
    public static double LiftAlpha(bool dimAuto, double[]?[]? samples, double dim) => dimAuto ? LiftAlpha(samples, dim) : 0;

    /// <see cref="LiftAlpha(double[][], double)"/> over <see cref="Rgb"/> samples (what <see cref="Samples"/> reads), same rules.
    public static double Film(Rgb[]? samples, double dim)
    {
        var seen = new List<Rgb>();
        if (samples != null)
            foreach (var c in samples) {
                if (!double.IsFinite(c.R) || !double.IsFinite(c.G) || !double.IsFinite(c.B)) continue;
                seen.Add(new Rgb(Ch(c.R), Ch(c.G), Ch(c.B)));
            }
        return Alpha(seen, dim);
    }

    /// Automatic = the check on, manual = off (film 0).
    public static double Film(bool auto, Rgb[]? samples, double dim) => auto ? Film(samples, dim) : 0;

    /// The valid, clamped samples (undimmed) to the alpha.
    static double Alpha(List<Rgb> seen, double dim)
    {
        int n = seen.Count;
        if (n == 0) return 0;
        double keep = 1 - CheckDim(dim);
        var s = new Rgb[n];
        var l = new double[n];
        var order = new int[n];
        for (int i = 0; i < n; i++) { s[i] = seen[i].Times(keep); l[i] = L(s[i]); order[i] = i; }
        // brightest first; equal luminance keeps the input order (JS's Array.prototype.sort is stable)
        Array.Sort(order, (p, q) => l[q] != l[p] ? l[q].CompareTo(l[p]) : p.CompareTo(q));
        Rgb bright = s[order[(int)Math.Floor(n * BrightRank)]];
        int k = 0;
        while (Step(k) < LevitateTokens.FilmCap && LumOf(bright, Step(k)) > LevitateTokens.FilmTarget) k++;
        return Math.Min(LevitateTokens.FilmCap, Step(k));
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

    /// The whole check for one panel: the copy it shows (soft if big, else light), its samples, the film for this dim and
    /// mode. <paramref name="vw"/> x <paramref name="vh"/> is the monitor (DIP); (x, y, w, h) the panel's layout rect on it.
    public static double ForPanel(FrostBitmap light, FrostBitmap soft, double vw, double vh, double x, double y, double w, double h, double dim, bool auto)
    {
        if (!auto) return 0;
        var copy = Island.IsBig(w, h) ? soft : light;
        var fit = Island.Cover(copy.W, copy.H, vw, vh);
        return Film(Samples(copy, fit, x, y, w, h), dim);
    }
}
