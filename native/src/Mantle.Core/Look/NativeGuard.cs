using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Mantle.Core.Look;

/// <summary>
/// The readability guard's need profile of ONE still on ONE monitor for ONE theme (look.md SEE-017): for every 8 DIP strip
/// of screen rows, the least wash alpha (hundredths) at which the theme's secondary ink reaches 4.5:1 by each measure.
/// Worked out once per (still, monitor size) from the very pixels the windows paint; a window then takes the worst strip
/// over its own rows (<see cref="NativeGuard.ForWindow"/>), which is a max over at most a few hundred numbers.
/// </summary>
public sealed class GuardRows
{
    internal GuardRows(bool dark, int w, int h, double scale, int boxPx, byte[] box, byte[] row)
    {
        Dark = dark; W = w; H = h; Scale = scale; BoxPx = boxPx; BoxNeed = box; RowNeed = row;
    }

    /// The theme the profile is for (dark: light ink over the dark wash; light: dark ink over white).
    public bool Dark { get; }
    /// The monitor in physical px (the decoded still's size) and its DPI scale.
    public int W { get; }
    public int H { get; }
    public double Scale { get; }
    /// The glyph box's side in physical px (4 DIP at this scale).
    public int BoxPx { get; }
    /// Per strip: the least alpha in hundredths (0..100) at which the brightest (dark) or darkest (light) box of the strip
    /// reads at the target; 101 = not even at a full wash.
    public byte[] BoxNeed { get; }
    /// Per strip: the same for the strip's 95th percentile luminance (dark) or its 5th (light): thin lines a box dilutes.
    public byte[] RowNeed { get; }
    public int Strips => BoxNeed.Length;
    /// The monitor's height in DIP.
    public double HeightDip => H / Scale;

    /// The first physical row of strip <paramref name="i"/> (strips are 8 DIP; the last one may be shorter).
    public int StripTop(int i) => Math.Min(H, (int)Math.Floor(i * NativeGuard.StripDip * Scale + 0.5));

    /// The worst strip touching DIP rows [<paramref name="y0"/>, <paramref name="y1"/>) of the monitor: its need (0..1.01, the
    /// larger of the two measures) and its index.
    public (double Need, int Strip) NeedOver(double y0, double y1)
    {
        if (Strips == 0) return (0, -1);
        int a = (int)Math.Floor(Math.Max(0, y0) / NativeGuard.StripDip), b = (int)Math.Ceiling(Math.Min(HeightDip, y1) / NativeGuard.StripDip) - 1;
        a = Math.Clamp(a, 0, Strips - 1); b = Math.Clamp(b, a, Strips - 1);
        int worst = a, n = -1;
        for (int i = a; i <= b; i++) { int v = Math.Max(BoxNeed[i], RowNeed[i]); if (v > n) { n = v; worst = i; } }
        return (n / 100.0, worst);
    }
}

/// <summary>
/// The native readability guard over the SHARP still (look.md SEE-017; look-sync inventory/guard-quality-2026-11-11.md,
/// "What the native Mantle rebuild should do"). Electron Mantle's guard lets the 48 brightest pixels of a 100 DIP top band
/// decide at 4.2:1, so over a sharp picture a few stars or snow glints drive the wash to .78. This one:
/// <list type="number">
/// <item>judges glyph-sized patches: the brightest (dark) or darkest (light) 4 x 4 DIP box (its mean LINEAR luminance,
/// about one glyph body, so a 1 px star weighs 1/16) and the 95th percentile of each text row (which catches a thin line
/// that a box dilutes);</item>
/// <item>aims for 4.5:1 for the secondary ink (#a9aaa7 over rgb(9 10 11) in dark, #333A4E over white in light);</item>
/// <item>works out a need per 8 DIP strip of the whole screen height, so each window takes the worst strip over its own
/// rows (at least a 140 DIP band), wherever it sits;</item>
/// <item>samples the physical pixels the window paints (the decoded still, one px per screen px, so a 4 DIP box is 5 px
/// at 125 %);</item>
/// <item>composites in 8 bits the way the screen does: the tint's alpha is a byte and each channel is
/// round(c (1 - a) + wash a).</item>
/// </list>
/// Pure: the pixels in, the numbers out. The cost is once per still and monitor size (the App runs it on the pool thread
/// right after the decode), never per frame or per move.
/// </summary>
public static class NativeGuard
{
    public const double Target = 4.5;
    public const double StripDip = 8, BoxDip = 4, BandMinDip = 140, Percentile = 0.95;
    /// The record's floor-and-step rule (look-guard's): from Dim up in steps of .02, at most max(.88, Dim).
    public const double Step = 0.02, Ceil = 0.88;
    /// The secondary inks and the washes (theme-tokens: dark --lg-ink-2 over --lg-fill-bar's colour; light ink-2 over white).
    public static readonly (int R, int G, int B) DarkInk = (169, 170, 167), DarkWash = (9, 10, 11), LightInk = (51, 58, 78), LightWash = (255, 255, 255);

    static readonly double[] Lin = MakeLin();
    static readonly double[] W3 = { 0.2126, 0.7152, 0.0722 };

    static double[] MakeLin()
    {
        var t = new double[256];
        for (int i = 0; i < 256; i++) { double c = i / 255.0; t[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return t;
    }

    /// WCAG relative luminance of an 8-bit colour.
    public static double Lum(int r, int g, int b) => W3[0] * Lin[r] + W3[1] * Lin[g] + W3[2] * Lin[b];
    public static double Contrast(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

    /// The alpha the screen really lays: the tint's alpha byte (GlassLayer writes round(alpha x 255)).
    public static double Alpha8(double alpha) => Math.Floor(Math.Clamp(alpha, 0, 1) * 255 + 0.5) / 255;

    /// One channel under the wash at <paramref name="a8"/>, in 8 bits: round(c (1 - a) + wash a).
    public static int Over(int c, int wash, double a8) => Math.Clamp((int)Math.Floor(c * (1 - a8) + wash * a8 + 0.5), 0, 255);

    /// The background luminance bound the theme's ink needs for <see cref="Target"/>: dark, at most this; light, at least.
    public static double Bound(bool dark)
    {
        var ink = dark ? DarkInk : LightInk;
        double li = Lum(ink.R, ink.G, ink.B);
        return dark ? (li + 0.05) / Target - 0.05 : Target * (li + 0.05) - 0.05;
    }

    /// The weighted linear luminance of each channel value under the wash at each hundredth of alpha: [k * 768 + c * 256 + v].
    static double[] Table(bool dark)
    {
        var w = dark ? DarkWash : LightWash;
        int[] wc = { w.R, w.G, w.B };
        var t = new double[101 * 768];
        for (int k = 0; k <= 100; k++) {
            double a8 = Alpha8(k / 100.0);
            for (int c = 0; c < 3; c++)
                for (int v = 0; v < 256; v++) t[k * 768 + c * 256 + v] = W3[c] * Lin[Over(v, wc[c], a8)];
        }
        return t;
    }

    /// The glyph box's side in physical px at a DPI scale (4 DIP, at least 1 px).
    public static int BoxPx(double scale) => Math.Max(1, (int)Math.Floor(BoxDip * scale + 0.5));

    /// <summary>The need profile of a still decoded at the monitor's physical size (<paramref name="bgra"/>: w x h, 4 bytes a
    /// pixel, B G R A, opaque) for the dark or the light wash. Never throws on a well-formed buffer; a short buffer throws.</summary>
    public static GuardRows Rows(ReadOnlySpan<byte> bgra, int w, int h, double scale, bool dark)
    {
        if (w <= 0 || h <= 0) return new GuardRows(dark, Math.Max(0, w), Math.Max(0, h), scale, 1, Array.Empty<byte>(), Array.Empty<byte>());
        if (bgra.Length < w * h * 4) throw new ArgumentException($"the picture has {bgra.Length} bytes, not {w * h * 4} for {w}x{h}");
        scale = scale > 0 ? scale : 1;
        var lut = Table(dark);
        double bound = Bound(dark);
        int kb = Math.Min(Math.Min(BoxPx(scale), h), w);
        int strips = Math.Max(1, (int)Math.Ceiling(h / (StripDip * scale) - 1e-9));
        var boxNeed = new byte[strips];
        var rowNeed = new byte[strips];
        var probe = new GuardRows(dark, w, h, scale, kb, boxNeed, rowNeed);
        var hist = new int[102];
        var lum = Array.Empty<double>();
        var col = new double[w];
        for (int s = 0; s < strips; s++) {
            int r0 = probe.StripTop(s), r1 = s + 1 < strips ? probe.StripTop(s + 1) : h;
            if (r1 <= r0) { boxNeed[s] = s > 0 ? boxNeed[s - 1] : (byte)0; rowNeed[s] = s > 0 ? rowNeed[s - 1] : (byte)0; continue; }

            // the row measure: each pixel's own need, then the percentile of those needs (exact: a pixel's luminance under
            // the wash moves one way as the alpha grows, so the percentile passes at k exactly when enough pixels do)
            Array.Clear(hist);
            for (int y = r0; y < r1; y++) {
                int p = y * w * 4;
                for (int x = 0; x < w; x++, p += 4) hist[PixelNeed(lut, bgra[p + 2], bgra[p + 1], bgra[p], bound, dark)]++;
            }
            long n = (long)(r1 - r0) * w;
            long j = (long)Math.Floor((dark ? Percentile : 1 - Percentile) * (n - 1));
            long want = dark ? j + 1 : n - j;
            long cum = 0; int need = 101;
            for (int k = 0; k <= 101; k++) { cum += hist[k]; if (cum >= want) { need = k; break; } }
            rowNeed[s] = (byte)need;

            // the box measure: the boxes whose top row lies in the strip (one row of boxes at the very bottom covers the rest)
            int t0 = Math.Min(r0, h - kb), t1 = Math.Max(t0 + 1, Math.Min(r1, h - kb + 1));
            int rows = t1 - t0 + kb - 1;
            if (lum.Length < rows * w) lum = new double[rows * w];
            // the least hundredth that passes (a box's mean moves one way as the alpha grows); a span cannot ride in a lambda
            int bn;
            if (BoxPasses(bgra, w, t0, t1, kb, lut, 0, bound, dark, lum, col)) bn = 0;
            else if (!BoxPasses(bgra, w, t0, t1, kb, lut, 100, bound, dark, lum, col)) bn = 101;
            else {
                int lo = 0, hi = 100;
                while (hi - lo > 1) { int m = (lo + hi) >> 1; if (BoxPasses(bgra, w, t0, t1, kb, lut, m, bound, dark, lum, col)) hi = m; else lo = m; }
                bn = hi;
            }
            boxNeed[s] = (byte)bn;
        }
        return probe;
    }

    /// The least hundredth k at which a pixel reads (101: not even at a full wash).
    static int PixelNeed(double[] lut, int r, int g, int b, double bound, bool dark)
    {
        bool Ok(int k) { int o = k * 768; double l = lut[o + r] + lut[o + 256 + g] + lut[o + 512 + b]; return dark ? l <= bound : l >= bound; }
        if (Ok(0)) return 0;
        if (!Ok(100)) return 101;
        int lo = 0, hi = 100;
        while (hi - lo > 1) { int m = (lo + hi) >> 1; if (Ok(m)) hi = m; else lo = m; }
        return hi;
    }

    /// Every kb x kb box with its top row in [t0, t1) reads at hundredth k (mean linear luminance under the wash).
    static bool BoxPasses(ReadOnlySpan<byte> bgra, int w, int t0, int t1, int kb, double[] lut, int k, double bound, bool dark, double[] lum, double[] col)
    {
        int o = k * 768, rows = t1 - t0 + kb - 1;
        for (int y = 0; y < rows; y++) {
            int p = (t0 + y) * w * 4, q = y * w;
            for (int x = 0; x < w; x++, p += 4) lum[q + x] = lut[o + bgra[p + 2]] + lut[o + 256 + bgra[p + 1]] + lut[o + 512 + bgra[p]];
        }
        double lim = bound * kb * kb;
        Array.Clear(col);
        for (int y = 0; y < kb; y++) for (int x = 0; x < w; x++) col[x] += lum[y * w + x];
        for (int top = 0; ; top++) {
            double sum = 0;
            for (int x = 0; x < kb; x++) sum += col[x];
            for (int x = kb; ; x++) {
                if (dark ? sum > lim : sum < lim) return false;
                if (x >= w) break;
                sum += col[x] - col[x - kb];
            }
            if (top + 1 >= t1 - t0) return true;
            int add = (top + kb) * w, sub = top * w;
            for (int x = 0; x < w; x++) col[x] += lum[add + x] - lum[sub + x];
        }
    }

    /// <summary>A window's band of monitor rows in DIP: its own rows, at least <see cref="BandMinDip"/> tall (kept on the
    /// monitor: a short window near the bottom takes the 140 DIP above its bottom edge).</summary>
    public static (double Y0, double Y1) Band(double topDip, double heightDip, double monitorDip)
    {
        double h = Math.Max(heightDip, BandMinDip);
        double y0 = topDip, y1 = topDip + h;
        if (y1 > monitorDip) { y1 = monitorDip; y0 = Math.Max(0, Math.Min(topDip, monitorDip - h)); }
        if (y0 < 0) { y1 = Math.Min(monitorDip, y1 - y0); y0 = 0; }
        return (y0, Math.Max(y0, y1));
    }

    /// The floor-and-step rule: from <paramref name="dim"/> up in steps of .02 to the first step at or above the need, at most
    /// max(.88, Dim). -> (alpha, short: even the ceiling does not reach the target)
    public static (double Alpha, bool Short) Step02(double dim, double need)
    {
        double ceil = Math.Max(Ceil, dim);
        if (need <= dim + 1e-9) return (dim, false);
        double a = Math.Round(dim + Math.Ceiling((need - dim) / Step - 1e-9) * Step, 2);
        return a > ceil + 1e-9 ? (ceil, true) : (a, false);
    }

    static string F2(double n) => n.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>washAlpha's automatic mode for a native window (look-sync CONTRACT 2026-11-11 "Automatic"): Manual stays exactly
    /// Dim; Automatic is max(Dim, this guard's worst strip over the window's band), on the .02 steps from Dim, instead of the
    /// published 100 DIP top band (which does not describe a window lower on the screen). Without a profile the shared
    /// answer stands. Never throws.</summary>
    /// <param name="shared">Wash.Alpha's answer for the theme (the mode, the Dim and the fallback)</param>
    /// <param name="rows">the profile of the still this window paints, for the theme in effect, or null</param>
    /// <param name="topDip">the window's top, DIP below its monitor's top</param>
    /// <param name="heightDip">the window's height in DIP</param>
    public static WashResult ForWindow(WashResult shared, GuardRows? rows, double topDip, double heightDip)
    {
        if (shared.Mode != "auto" || rows == null || rows.Strips == 0) return shared;
        double dim = shared.Dim;
        var (y0, y1) = Band(topDip, heightDip, rows.HeightDip);
        var (need, strip) = rows.NeedOver(y0, y1);
        var (a, sh) = Step02(dim, need);
        string where = "this window's part of the picture";
        string at = FormattableString.Invariant($" (rows {y0:0}-{y1:0} DIP; the worst, at {rows.StripTop(strip) / rows.Scale:0} DIP, needs {F2(Math.Min(need, 1))})");
        if (sh)
            return new WashResult(a, "auto", true, true, "raised to " + F2(a) + ", the most it goes; text over " + where + " still reads below 4.5:1" + at, dim, null,
                "Automatic: " + F2(dim) + ", raised to " + F2(a) + " (the most it goes) so text reads as well as it can over " + where);
        if (a > dim + 1e-9)
            return new WashResult(a, "auto", true, true, "raised to " + F2(a) + " so text stays readable over " + where + at, dim, null,
                "Automatic: " + F2(dim) + ", raised to " + F2(a) + " so text stays readable over " + where);
        return new WashResult(dim, "auto", false, true, "text already reads over " + where + " at this Dim" + at, dim, null,
            "Automatic: " + F2(dim) + ", text already reads over " + where + " at this Dim");
    }

    /// <summary>The worst contrasts of the theme's ink at <paramref name="alpha"/> (laid as a byte, composited in 8 bits) over
    /// the strips touching DIP rows [y0, y1): by the glyph box and by the row percentile. For the published record's
    /// <c>contrast</c> and for the tests; it sorts each strip, so it is not for a hot path.</summary>
    public static (double Box, double Row) Measure(ReadOnlySpan<byte> bgra, int w, int h, double scale, bool dark, double alpha, double y0, double y1)
    {
        var ink = dark ? DarkInk : LightInk; var wash = dark ? DarkWash : LightWash;
        double li = Lum(ink.R, ink.G, ink.B), a8 = Alpha8(alpha);
        scale = scale > 0 ? scale : 1;
        int kb = Math.Min(Math.Min(BoxPx(scale), h), w);
        var probe = new GuardRows(dark, w, h, scale, kb, new byte[Math.Max(1, (int)Math.Ceiling(h / (StripDip * scale) - 1e-9))], Array.Empty<byte>());
        int a = (int)Math.Floor(Math.Max(0, y0) / StripDip), b = (int)Math.Ceiling(Math.Min(h / scale, y1) / StripDip) - 1;
        a = Math.Clamp(a, 0, probe.Strips - 1); b = Math.Clamp(b, a, probe.Strips - 1);
        double box = double.PositiveInfinity, row = double.PositiveInfinity;
        var lumAt = new double[w * h];
        for (int i = 0, p = 0; i < w * h; i++, p += 4) lumAt[i] = Lum(Over(bgra[p + 2], wash.R, a8), Over(bgra[p + 1], wash.G, a8), Over(bgra[p], wash.B, a8));
        double L(int x, int y) => lumAt[y * w + x];
        for (int s = a; s <= b; s++) {
            int r0 = probe.StripTop(s), r1 = s + 1 < probe.Strips ? probe.StripTop(s + 1) : h;
            if (r1 <= r0) continue;
            var all = new double[(r1 - r0) * w];
            for (int y = r0; y < r1; y++) for (int x = 0; x < w; x++) all[(y - r0) * w + x] = L(x, y);
            Array.Sort(all);
            long n = all.Length, j = (long)Math.Floor((dark ? Percentile : 1 - Percentile) * (n - 1));
            row = Math.Min(row, Contrast(li, all[j]));
            int t0 = Math.Min(r0, h - kb), t1 = Math.Max(t0 + 1, Math.Min(r1, h - kb + 1));
            double worst = dark ? double.NegativeInfinity : double.PositiveInfinity;
            for (int ty = t0; ty < t1; ty++)
                for (int tx = 0; tx + kb <= w; tx++) {
                    double sum = 0;
                    for (int dy = 0; dy < kb; dy++) for (int dx = 0; dx < kb; dx++) sum += L(tx + dx, ty + dy);
                    double m = sum / (kb * kb);
                    worst = dark ? Math.Max(worst, m) : Math.Min(worst, m);
                }
            box = Math.Min(box, Contrast(li, worst));
        }
        return (box, row);
    }

    /// <summary>The <c>desktop.guard.&lt;theme&gt;</c> entry other apps read (CONTRACT "Shared top-band darkening" and "Light look"):
    /// <c>{ alpha, contrast, dim, blurPx, region: { topDip: 0, heightDip: 140 }, monitor: [w, h] }</c>, the alpha being this
    /// guard's over the top 140 DIP (floored at Dim, .02 steps, ceiling max(.88, Dim)), worked out for the STORED glass
    /// (dim and blurPx as stored; the picture is painted sharp whatever the stored blur). The shape is unchanged, so Quartz
    /// and Helm keep reading it.</summary>
    public static JsonObject Entry(GuardRows rows, ReadOnlySpan<byte> bgra, double dim, double blurPx)
    {
        var (need, _) = rows.NeedOver(0, BandMinDip);
        var (a, _) = Step02(dim, need);
        var m = Measure(bgra, rows.W, rows.H, rows.Scale, rows.Dark, a, 0, BandMinDip);
        double c = Math.Round(Math.Min(m.Box, m.Row), 2);
        return new JsonObject {
            ["alpha"] = a, ["contrast"] = c, ["dim"] = dim, ["blurPx"] = blurPx,
            ["region"] = new JsonObject { ["topDip"] = 0, ["heightDip"] = BandMinDip },
            ["monitor"] = new JsonArray((JsonNode)Math.Round(rows.W / rows.Scale), (JsonNode)Math.Round(rows.H / rows.Scale)),
        };
    }
}
