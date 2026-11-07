using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mantle.Core.Data;

namespace Mantle.Core.Look;

/// A colour with alpha (glass-adapt's <c>{r, g, b, a}</c>).
public readonly record struct Rgba(double R, double G, double B, double A = 1);

/// A CSS filter as glass-adapt reads it: <c>blur(12px) saturate(140%) brightness(.86)</c>.
public readonly record struct GlassFilter(double Blur, double Saturate, double Brightness);

/// What glass-adapt's guard reads from the tokens (<c>guardTokens</c>, or look-guard's <c>tokens</c>).
public sealed record GuardTokens(Rgba Fill, double Ceil, Rgba Ink, double Need, GlassFilter Filter);

/// glassTint's answer: the thinnest tint, the worst contrast at it, and whether even the ceiling falls short.
public readonly record struct TintAnswer(double Alpha, double Ratio, bool Short);

/// guardRect's answer: a tint answer, whether it rose above the floor, and the tint as CSS.
public sealed record GuardRectAnswer(double Alpha, double Ratio, bool Short, bool Engaged, string Tint);

/// The picture as the compositor draws it: w x h pixels, flat <c>[r, g, b, ...]</c>.
public sealed record GlassPicture(double[] Px, int W, int H);

/// A rectangle in picture pixels.
public readonly record struct PicRect(double X, double Y, double W, double H);

/// <summary>
/// The readability guard's maths: ui/shared/glass-adapt.js (the ONE guard every surface shares) ported exactly,
/// number for number. The top bar's darkening, Helm's strip and the look.json desktop guard all come from these
/// functions, so the native surfaces must reach the same alpha as the Electron ones on the same picture
/// (PEND-005; LookGoldenTests compares with values the JavaScript itself wrote).
/// </summary>
public static class GlassAdapt
{
    /// GUARD: need 4.7 (not 4.5: the model blurs a downscale, the drawn pixels peak a little brighter), step .02, hold 4.
    public const double GuardNeed = 4.7, GuardStep = 0.02;
    public const int GuardHold = 4;

    public static double Lin(double c) { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    public static double Lum(double r, double g, double b) => 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    public static double Contrast(double l1, double l2) { double hi = Math.Max(l1, l2), lo = Math.Min(l1, l2); return (hi + 0.05) / (lo + 0.05); }

    /// Math.round: half rounds up (towards +infinity), as in JavaScript.
    public static double JsRound(double v)
    {
        if (!double.IsFinite(v)) return v;
        double f = Math.Floor(v);
        return v - f >= 0.5 ? f + 1 : f;
    }

    static readonly Regex Hex = new("^#([0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Rgb = new(@"^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+%?))?\s*\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex FilterFn = new(@"(blur|saturate|brightness)\(\s*([\d.]+)(px|%)?\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// parseFloat for the digit-and-dot runs these patterns capture.
    static double PF(string s)
    {
        int end = 0, dots = 0;
        while (end < s.Length && (char.IsAsciiDigit(s[end]) || (s[end] == '.' && dots++ == 0))) end++;
        string t = s[..end];
        if (t.Length == 0 || t == ".") return double.NaN;
        return double.Parse(t.EndsWith('.') ? t + "0" : t, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>parseColor: <c>rgba(30, 31, 34, 0.34)</c>, <c>rgb(1 2 3 / .5)</c>, <c>#rrggbb</c>, <c>#rgb</c>, else null.</summary>
    public static Rgba? ParseColor(string? s)
    {
        s = (s ?? "").Trim();
        var m = Hex.Match(s);
        if (m.Success) {
            string hx = m.Groups[1].Value;
            if (hx.Length == 3) hx = string.Concat(hx.Select(c => new string(c, 2)));
            int P(int i) => int.Parse(hx.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Rgba(P(0), P(2), P(4), 1);
        }
        m = Rgb.Match(s);
        if (!m.Success) return null;
        double a = !m.Groups[4].Success ? 1 : m.Groups[4].Value.EndsWith('%') ? PF(m.Groups[4].Value) / 100 : PF(m.Groups[4].Value);
        return new Rgba(Plus(m.Groups[1].Value), Plus(m.Groups[2].Value), Plus(m.Groups[3].Value), a);
    }

    /// Unary + on a captured run of digits and dots: Number("1.2.3") and Number(".") are NaN, unlike parseFloat.
    static double Plus(string s)
    {
        if (s.Count(c => c == '.') > 1 || s == ".") return double.NaN;
        return double.Parse(s.EndsWith('.') ? s + "0" : s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    /// <summary>parseFilter: <c>"blur(12px) saturate(140%) brightness(.86)"</c> -> {12, 1.4, .86}; the last of each wins.</summary>
    public static GlassFilter ParseFilter(string? s)
    {
        double blur = 0, sat = 1, br = 1;
        foreach (Match m in FilterFn.Matches(s ?? "")) {
            double n = PF(m.Groups[2].Value);
            if (m.Groups[3].Value == "%") n /= 100;
            switch (m.Groups[1].Value.ToLowerInvariant()) {
                case "blur": blur = n; break;
                case "saturate": sat = n; break;
                default: br = n; break;
            }
        }
        return new GlassFilter(blur, sat, br);
    }

    static double Clamp255(double v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>glassPixels: the blurred picture as the compositor draws it. A gaussian of <paramref name="sigma"/> image px
    /// with mirrored edges (only above .3), then the Filter Effects saturate matrix, then brightness. <paramref name="rgba"/> is
    /// unpremultiplied; a transparent pixel counts as white. The working copy is single precision, as the module's Float32Array.</summary>
    public static double[] GlassPixels(ReadOnlySpan<byte> rgba, int w, int h, double sigma, double saturate = 1, double brightness = 1)
    {
        int n = w * h;
        var src = new float[n * 3];
        for (int i = 0; i < n; i++) {
            double al = rgba[i * 4 + 3] / 255.0;
            for (int c = 0; c < 3; c++) src[i * 3 + c] = (float)(rgba[i * 4 + c] * al + 255 * (1 - al));
        }
        sigma = Math.Max(0, double.IsNaN(sigma) ? 0 : sigma);
        if (sigma > 0.3) {
            int rad = (int)Math.Ceiling(sigma * 3);
            var ker = new double[2 * rad + 1];
            double sum = 0;
            for (int k = -rad; k <= rad; k++) { double v = Math.Exp(-(double)(k * k) / (2 * sigma * sigma)); ker[k + rad] = v; sum += v; }
            for (int k = 0; k < ker.Length; k++) ker[k] /= sum;
            static int Mir(int p, int len)
            {
                if (len == 1) return 0;
                int per = 2 * (len - 1);
                p = ((p % per) + per) % per;
                return p < len ? p : per - p;
            }
            var tmp = new float[n * 3];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++) {
                double acc = 0;
                for (int k = -rad; k <= rad; k++) acc += ker[k + rad] * src[(y * w + Mir(x + k, w)) * 3 + c];
                tmp[(y * w + x) * 3 + c] = (float)acc;
            }
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++) {
                double acc2 = 0;
                for (int k = -rad; k <= rad; k++) acc2 += ker[k + rad] * tmp[(Mir(y + k, h) * w + x) * 3 + c];
                src[(y * w + x) * 3 + c] = (float)acc2;
            }
        }
        double s = saturate, br = brightness;
        var o = new double[n * 3];
        for (int i = 0; i < n; i++) {
            double R = src[i * 3], G = src[i * 3 + 1], B = src[i * 3 + 2];
            double r2 = (0.213 + 0.787 * s) * R + (0.715 - 0.715 * s) * G + (0.072 - 0.072 * s) * B;
            double g2 = (0.213 - 0.213 * s) * R + (0.715 + 0.285 * s) * G + (0.072 - 0.072 * s) * B;
            double b2 = (0.213 - 0.213 * s) * R + (0.715 - 0.715 * s) * G + (0.072 + 0.928 * s) * B;
            o[i * 3] = Clamp255(Clamp255(r2) * br); o[i * 3 + 1] = Clamp255(Clamp255(g2) * br); o[i * 3 + 2] = Clamp255(Clamp255(b2) * br);
        }
        return o;
    }

    /// <summary>glassTint: the thinnest tint from <paramref name="floor"/> up in steps that keeps <paramref name="ink"/> at
    /// <paramref name="need"/>:1 over every pixel. Only the 48 brightest pixels can be the worst, so only they are tried.</summary>
    public static TintAnswer GlassTint(IReadOnlyList<double> px, Rgba ink, Rgba tint, double need = 0, double? floor = null, double? ceil = null, double step = 0)
    {
        if (!(need != 0) || double.IsNaN(need)) need = GuardNeed;
        if (!(step != 0) || double.IsNaN(step)) step = GuardStep;
        double fl = floor ?? 0, cl = ceil ?? 0.9;
        var all = new List<(double L, double R, double G, double B)>();
        for (int i = 0; i + 2 < px.Count; i += 3) all.Add((Lum(px[i], px[i + 1], px[i + 2]), px[i], px[i + 1], px[i + 2]));
        var cand = all.OrderByDescending(c => c.L).Take(48).ToArray();   // a stable sort, as Array.prototype.sort
        double li = Lum(ink.R, ink.G, ink.B);
        double Worst(double a)
        {
            double w = double.PositiveInfinity;
            foreach (var c in cand) {
                double l = Lum(c.R * (1 - a) + tint.R * a, c.G * (1 - a) + tint.G * a, c.B * (1 - a) + tint.B * a);
                double rr = Contrast(li, l);
                if (rr < w) w = rr;
            }
            return cand.Length > 0 ? w : Contrast(li, Lum(tint.R, tint.G, tint.B));
        }
        for (double a = fl; a <= cl + 1e-9; a = JsRound((a + step) * 1000) / 1000) {
            double r = Worst(a);
            if (r >= need) return new TintAnswer(JsRound(a * 1000) / 1000, JsRound(r * 100) / 100, false);
        }
        return new TintAnswer(cl, JsRound(Worst(cl) * 100) / 100, true);
    }

    /// <summary>glassSettle: thicker at once; thinner only by a real step (>= .10 at once, or >= .04 held for <paramref name="hold"/> pictures).</summary>
    public static (double Cur, int Lower) GlassSettle(double? cur, int lower, double next, int hold = 0)
    {
        if (cur == null || next >= cur) return (next, 0);
        double d = cur.Value - next;
        if (d >= 0.1) return (next, 0);
        if (d < 0.04) return (cur.Value, 0);
        lower++;
        return lower >= (hold != 0 ? hold : GuardHold) ? (next, 0) : (cur.Value, lower);
    }

    /// glassPicture: blur ONCE per picture (the guard then judges each rectangle). Null when there is no picture.
    public static GlassPicture? Picture(ReadOnlySpan<byte> rgba, int w, int h, GuardTokens? t, double picPerCss)
    {
        if (!(w > 0) || !(h > 0) || rgba.Length < w * h * 4 || t == null) return null;
        var f = t.Filter;
        return new GlassPicture(GlassPixels(rgba, w, h, f.Blur * (picPerCss > 0 ? picPerCss : 1), f.Saturate, f.Brightness), w, h);
    }

    /// guardRect: the tint over one rectangle of a blurred picture (all of it when <paramref name="rect"/> is null).
    public static GuardRectAnswer? GuardRect(GlassPicture? pic, GuardTokens? t, PicRect? rect)
    {
        if (pic == null || t == null) return null;
        IReadOnlyList<double> px = pic.Px;
        if (rect is PicRect r) {
            int x0 = (int)Math.Max(0, Math.Floor(r.X)), y0 = (int)Math.Max(0, Math.Floor(r.Y));
            int x1 = (int)Math.Min(pic.W, Math.Ceiling(r.X + r.W)), y1 = (int)Math.Min(pic.H, Math.Ceiling(r.Y + r.H));
            if (x1 <= x0 || y1 <= y0) return null;
            var cut = new List<double>((x1 - x0) * (y1 - y0) * 3);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y * pic.W + x) * 3; cut.Add(pic.Px[i]); cut.Add(pic.Px[i + 1]); cut.Add(pic.Px[i + 2]); }
            px = cut;
        }
        var a = GlassTint(px, t.Ink, t.Fill, t.Need != 0 ? t.Need : GuardNeed, t.Fill.A, Math.Max(t.Fill.A, t.Ceil), GuardStep);
        return new GuardRectAnswer(a.Alpha, a.Ratio, a.Short, a.Alpha > t.Fill.A + 1e-9, TintCss(t.Fill, a.Alpha));
    }

    public static string TintCss(Rgba fill, double alpha)
        => "rgba(" + JsJson.Number(fill.R) + "," + JsJson.Number(fill.G) + "," + JsJson.Number(fill.B) + "," + JsJson.Number(alpha) + ")";
}

/// Where the band sits in the still (look-guard.js bandGeometry).
public sealed record BandGeometry(double Scale, double Ws, int SrcX, int SrcY, int SrcW, int SrcH, int OutW, int OutH, int BandRows, double Band);

/// What the top bar does with the shared darkening (look-guard.js use).
public sealed record GuardUse(bool Use, double? Alpha, double? Contrast, double? Rev, string? Why, bool Failure, bool Soft);

/// <summary>
/// lib/look-guard.js's pure half: the dark and light top-band rules, the band's geometry, the solve over a still's
/// pixels, the record checks and keys, and <see cref="Use"/> (what the top bar does). The worker and nativeImage glue
/// are the platform's; the numbers are here. Rules as data: docs/theme-tokens.md "The guard rules".
/// </summary>
public static class LookGuard
{
    /// THE DARK RULE: --lg-ink-2 (dark) #a9aaa7 keeps 4.2:1 (--lg-guard-bar) under --lg-fill-bar's colour, up to --lg-fill-pop's alpha.
    public static readonly (Rgba Ink, double Need, Rgba Tint, double Ceil, double Step) Rule = (new Rgba(169, 170, 167), 4.2, new Rgba(9, 10, 11), 0.88, 0.02);
    /// THE LIGHT RULE: --lg-ink-2 (light) #333A4E keeps 4.7:1 against the 48 DARKEST pixels, under white, saturate 150%.
    public static readonly (Rgba Ink, double Need, Rgba Tint, double Ceil, double Step, double Saturate) RuleLight = (new Rgba(51, 58, 78), 4.7, new Rgba(255, 255, 255), 0.88, 0.02, 1.5);
    public const int WorstN = 48;
    /// 100 DIP, not 144: the band height moves the answer, and the user was promised his mountain photo stays at .50.
    public const int BandDip = 100;
    public const double MaxSigmaPx = 8;

    static double R2(double n) => GlassAdapt.JsRound(n * 100) / 100;
    static string Num(double d) => JsJson.Number(d);

    public static BandGeometry? Geometry(double iw, double ih, double mw, double mh, double blurPx, double bandDip = BandDip)
    {
        if (!(iw > 0 && ih > 0 && mw > 0 && mh > 0)) return null;
        double band = Math.Min(mh, bandDip > 0 ? bandDip : BandDip);
        double blur = blurPx > 0 ? blurPx : 0;
        double ws = blur > MaxSigmaPx ? MaxSigmaPx / blur : 1;
        double s = Math.Max(mw / iw, mh / ih);
        double ox = (iw * s - mw) / 2, oy = (ih * s - mh) / 2;
        double marginDip = Math.Min(mh - band, Math.Ceiling(3 * blur * ws) / ws);
        double rowsDip = band + marginDip;
        double sx = Math.Max(0, GlassAdapt.JsRound(ox / s)), sy = Math.Max(0, GlassAdapt.JsRound(oy / s));
        double sw = Math.Max(1, Math.Min(iw - sx, GlassAdapt.JsRound(mw / s))), sh = Math.Max(1, Math.Min(ih - sy, GlassAdapt.JsRound(rowsDip / s)));
        return new BandGeometry(s, ws, (int)sx, (int)sy, (int)sw, (int)sh,
            (int)Math.Max(1, GlassAdapt.JsRound(mw * ws)), (int)Math.Max(1, GlassAdapt.JsRound(rowsDip * ws)),
            (int)Math.Max(1, GlassAdapt.JsRound(band * ws)), band);
    }

    /// The tokens guardRect reads, for a dim and a blur.
    public static GuardTokens Tokens(double dim, double blurPx, bool light)
        => light
            ? new GuardTokens(RuleLight.Tint with { A = dim }, RuleLight.Ceil, RuleLight.Ink, RuleLight.Need, new GlassFilter(blurPx, RuleLight.Saturate, 1))
            : new GuardTokens(Rule.Tint with { A = dim }, Rule.Ceil, Rule.Ink, Rule.Need, new GlassFilter(blurPx, 1, 1));

    /// BGRA (a Windows bitmap) -> RGBA, opaque.
    public static byte[] BgraToRgba(ReadOnlySpan<byte> buf)
    {
        var o = new byte[buf.Length];
        for (int i = 0; i + 3 < buf.Length; i += 4) { o[i] = buf[i + 2]; o[i + 1] = buf[i + 1]; o[i + 2] = buf[i]; o[i + 3] = 255; }
        return o;
    }

    /// <summary>fromPixels: the band's answer. Dark: the brightest pixels of the band rows under the dark rule. Light: the
    /// 48 darkest pixels under white. -> (alpha, contrast, short) or null.</summary>
    public static TintAnswer? FromPixels(ReadOnlySpan<byte> rgba, int width, int height, double dim, double blurPx, bool light, double ws, int bandRows)
    {
        var t = Tokens(dim, blurPx, light);
        var pic = GlassAdapt.Picture(rgba, width, height, t, ws > 0 ? ws : 1);
        if (pic == null) return null;
        if (light) {
            int rows = Math.Max(1, Math.Min(pic.H, bandRows));
            int n = pic.W * rows;
            var lum = new double[n];
            for (int i = 0; i < n; i++) lum[i] = GlassAdapt.Lum(pic.Px[i * 3], pic.Px[i * 3 + 1], pic.Px[i * 3 + 2]);
            var order = Enumerable.Range(0, n).OrderBy(i => lum[i]).Take(WorstN);   // stable, as Array.prototype.sort
            var px = new List<double>();
            foreach (int i in order) { px.Add(pic.Px[i * 3]); px.Add(pic.Px[i * 3 + 1]); px.Add(pic.Px[i * 3 + 2]); }
            return GlassAdapt.GlassTint(px, t.Ink, t.Fill, t.Need, t.Fill.A, Math.Max(t.Fill.A, t.Ceil), RuleLight.Step);
        }
        var r = GlassAdapt.GuardRect(pic, t, new PicRect(0, 0, width, bandRows));
        return r == null ? null : new TintAnswer(r.Alpha, r.Ratio, r.Short);
    }

    /// A glass the guard can be worked out for: null, or why not.
    public static string? ValidGlass(double dim, double blurPx, string? look)
    {
        if (!(dim >= 0 && dim <= 1)) return "the " + (look ?? "dark") + " glass has no usable dim";
        if (!(blurPx >= 0 && blurPx <= 200)) return "the " + (look ?? "dark") + " glass has no usable blur";
        return null;
    }

    static bool Num01(JsonNode? v) => LookSyncCore.TryNum(v, out var d) && d >= 0 && d <= 1;

    /// checkGuard: a guard record's dark value is usable. -> null | reason
    public static string? CheckGuard(JsonNode? g)
    {
        if ((g as JsonObject)?["dark"] is not JsonObject d) return "the record has no dark value";
        if (!Num01(d["alpha"])) return "its alpha is not a number from 0 to 1";
        if (!LookSyncCore.IsNumber(d["dim"]) || !LookSyncCore.IsNumber(d["blurPx"])) return "it does not say which dim and blur it was worked out for";
        return null;
    }

    /// checkGuardLight: a guard record's light value is usable. -> null | reason
    public static string? CheckGuardLight(JsonNode? g)
    {
        if ((g as JsonObject)?["light"] is not JsonObject l) return "the record has no light value";
        if (!Num01(l["alpha"])) return "its light alpha is not a number from 0 to 1";
        if (!LookSyncCore.IsNumber(l["dim"]) || !LookSyncCore.IsNumber(l["blurPx"])) return "its light value does not say which dim and blur it was worked out for";
        return null;
    }

    /// `${v}` of a JSON value, undefined when absent.
    static string JsText(JsonNode? v, bool present)
    {
        if (!present) return "undefined";
        if (v == null) return "null";
        if (LookSyncCore.TryNum(v, out var d)) return Num(d);
        if (LookSyncCore.Str(v) is string s) return s;
        if (LookSyncCore.IsBool(v, out var b)) return b ? "true" : "false";
        if (v is JsonArray a) return string.Join(",", a.Select(x => x == null ? "" : JsText(x, true)));
        return "[object Object]";
    }

    static string KeyOf(JsonNode? dim, JsonNode? blurPx, JsonNode? monitor, JsonNode? band, bool bandPresent)
    {
        if (!LookSyncCore.TryNum(dim, out var d) || !LookSyncCore.TryNum(blurPx, out var b) || monitor is not JsonArray m) return "";
        return Num(R2(d)) + "|" + Num(R2(b)) + "|" + JsText(m.Count > 0 ? m[0] : null, m.Count > 0) + "x" + JsText(m.Count > 1 ? m[1] : null, m.Count > 1) + "|" + JsText(band, bandPresent);
    }

    static string LightKey(JsonNode? l)
        => l is JsonObject o && LookSyncCore.TryNum(o["dim"], out var d) && LookSyncCore.TryNum(o["blurPx"], out var b) ? "|L" + Num(R2(d)) + "|" + Num(R2(b)) : "|L-";

    /// guardKey: the inputs a guard is worked out for, as one key ('' when there is none). The band height and the light glass are part of it.
    public static string GuardKey(JsonNode? dim, JsonNode? blurPx, JsonNode? monitor, JsonNode? light)
    {
        string k = KeyOf(dim, blurPx, monitor, JsonValue.Create((double)BandDip), true);
        return k.Length > 0 ? k + LightKey(light) : "";
    }

    /// keyOfGuard: the key a stored guard record was worked out for.
    public static string KeyOfGuard(JsonNode? g)
    {
        if ((g as JsonObject)?["dark"] is not JsonObject d) return "";
        var region = d["region"] as JsonObject;
        bool has = region != null && region.ContainsKey("heightDip");
        string k = KeyOf(d["dim"], d["blurPx"], d["monitor"], has ? region!["heightDip"] : null, has);
        return k.Length > 0 ? k + LightKey(((JsonObject)g!)["light"]) : "";
    }

    /// <summary>use: what the TOP BAR does in dark with look sync on (the shared alpha replaces its own guard), else why not.
    /// <c>Failure</c> = a real fault the bar shows; <c>Soft</c> = it may only be a moment early (look-host holds it for a grace).</summary>
    public static GuardUse Use(bool dark, bool syncOn, JsonObject? record, double? glassDim, double? glassBlur, bool working)
    {
        if (!dark) return new(false, null, null, null, "light: the top bar keeps its light frost", false, false);
        if (!syncOn) return new(false, null, null, null, "look sync is off, so the top bar works out its own darkening", false, false);
        if (record == null) return new(false, null, null, null, "Mantle has not shared the desktop wallpaper yet, so the top bar works out its own darkening", !working, true);
        var g = record["guard"];
        bool recNotOk = LookSyncCore.IsBool(record["ok"], out var ok) && !ok;
        if (g == null || (LookSyncCore.IsBool(g, out var gb) && !gb) || (LookSyncCore.TryNum(g, out var gn) && (gn == 0 || double.IsNaN(gn))) || LookSyncCore.Str(g) == "") {
            if (working) return new(false, null, null, null, "the shared darkening is being worked out", false, false);
            string? gr = LookSyncCore.Str(record["guardReason"]) is { Length: > 0 } x ? x : null;
            string? why = gr;
            if (why == null && recNotOk && Truthy(record["reason"])) why = JsText(record["reason"], true);
            why ??= "the desktop record carries no shared darkening";
            return new(false, null, null, null, "the shared darkening is missing (" + why + "), so the top bar works out its own", true, gr == null && !recNotOk);
        }
        string? bad = CheckGuard(g);
        if (bad != null) return new(false, null, null, null, "the shared darkening is not usable (" + bad + "), so the top bar works out its own", true, false);
        var d = (JsonObject)((JsonObject)g)["dark"]!;
        LookSyncCore.TryNum(d["dim"], out var dd);
        LookSyncCore.TryNum(d["blurPx"], out var db);
        if (glassDim is double gd && glassBlur is double gbl && (R2(gd) != R2(dd) || R2(gbl) != R2(db))) {
            if (working) return new(false, null, null, null, "the shared darkening is being worked out for the new glass", false, false);
            return new(false, null, null, null, "the shared darkening is out of date (worked out for tint " + Num(GlassAdapt.JsRound(dd * 100)) + "% and blur " + Num(db)
                + " px, the glass is now " + Num(GlassAdapt.JsRound(gd * 100)) + "% and " + Num(gbl) + " px), so the top bar works out its own", true, true);
        }
        LookSyncCore.TryNum(d["alpha"], out var alpha);
        double? contrast = LookSyncCore.TryNum(d["contrast"], out var c) ? c : null;
        double? rev = LookSyncCore.IsInt(record["rev"], out var rv) ? rv : null;
        return new(true, alpha, contrast, rev, null, false, false);
    }

    /// JavaScript truthiness of a JSON value.
    static bool Truthy(JsonNode? v)
    {
        if (v == null) return false;
        if (LookSyncCore.IsBool(v, out var b)) return b;
        if (LookSyncCore.TryNum(v, out var d)) return d != 0 && !double.IsNaN(d);
        if (LookSyncCore.Str(v) is string s) return s.Length > 0;
        return true;
    }
}
