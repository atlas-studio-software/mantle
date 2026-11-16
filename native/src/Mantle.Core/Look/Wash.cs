using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Look;

/// <summary>washAlpha's answer (look-sync.js): <c>{ alpha, mode, raised, guarded, reason, dim, contrast, text }</c>.</summary>
/// <param name="Mode">"auto" | "manual"</param>
/// <param name="Guarded">Mantle's top band was valid and taken into the max (automatic only)</param>
/// <param name="Text">the Settings row, e.g. "Automatic: 0.30, raised to 0.46 so text stays readable over this picture" or "Manual: 0.20"</param>
public sealed record WashResult(double Alpha, string Mode, bool Raised, bool Guarded, string Reason, double Dim, double? Contrast, string Text);

/// <summary>
/// The tint (wash alpha) every app lays over the painted desktop: look-sync.js <c>washAlpha(look, desktop, theme, opts)</c>,
/// ported one to one from its pure block (CONTRACT.md "2026-11-11: sharp painted glass + tint override"), so Helm, Quartz
/// and Mantle compute the same tint from the same look.json.
/// <list type="bullet">
/// <item>Manual (<c>glass.&lt;theme&gt;.dimAuto</c> false): exactly Dim, the guard ignored.</item>
/// <item>Automatic (dimAuto absent or true): max(Dim, <c>desktop.guard.&lt;theme&gt;.alpha</c>) when that guard is valid (an
/// alpha 0..1, worked out for this Dim and blur to 2 decimals, at most max(.88, Dim)), else Dim, with the reason.</item>
/// </list>
/// Never throws. The seed Dim (a missing or out-of-range Dim) is look-sync's 2026-11-11 seed, .30 in both themes.
/// </summary>
public static class Wash
{
    /// look-sync.js SEED.glass.&lt;theme&gt; (2026-11-11): the same in both themes.
    public const double SeedDim = 0.30, SeedBlurPx = 0;
    /// The guard's ceiling (WASH_CEIL: Mantle's dark --lg-fill-pop alpha).
    public const double Ceil = 0.88;
    static readonly (double Lo, double Hi) DimRange = (0.20, 0.90), BlurRange = (0, 64);

    /// Math.round(n * 100) / 100 (JavaScript rounds a half up, toward +infinity).
    static double R2(double n) => Math.Floor(n * 100 + 0.5) / 100;

    /// n.toFixed(2) for a finite number.
    static string F2(double n) => n.ToString("0.00", CultureInfo.InvariantCulture);

    /// a2: toFixed(2) for a finite number, else JSON.stringify (an absent key is "undefined").
    static string A2(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v)) return "undefined";
        if (LookSyncCore.TryNum(v, out var d) && double.IsFinite(d)) return F2(d);
        return LookSyncCore.J(v);
    }

    static bool Num(JsonNode? v, (double Lo, double Hi) r, out double d) => LookSyncCore.IsFinite(v, out d) && d >= r.Lo && d <= r.Hi;

    /// <param name="look">the look values <c>{ glass }</c>, or shared()'s <c>{ glass: { v } }</c></param>
    /// <param name="desktop">the desktop record the app is painting (doc.desktop), or null</param>
    /// <param name="theme">"dark" | "light": the theme in effect (anything else is dark)</param>
    /// <param name="unusable">the app's reason it can't use this record's top band now (automatic then stays at Dim)</param>
    public static WashResult Alpha(JsonNode? look, JsonNode? desktop, string? theme, string? unusable = null)
    {
        string t = theme == "light" ? "light" : "dark";
        JsonObject? glass = look is JsonObject lo ? lo["glass"] as JsonObject : null;
        if (glass != null && glass["v"] is JsonObject gv && glass[t] is not JsonObject) glass = gv;   // shared() shape
        var g = glass?[t] as JsonObject;
        double gdim = 0;
        bool dimOk = g != null && Num(g["dim"], DimRange, out gdim);
        double dim = dimOk ? gdim : SeedDim;
        double blur = g != null && Num(g["blurPx"], BlurRange, out var bp) ? bp : SeedBlurPx;
        string? badDim = dimOk ? null
            : "glass." + t + ".dim is missing or outside " + F2(DimRange.Lo) + "-" + F2(DimRange.Hi) + ", so the default " + F2(SeedDim);
        if (g != null && LookSyncCore.IsBool(g["dimAuto"], out var auto) && !auto) {
            return new WashResult(dim, "manual", false, false, badDim ?? "Dim is set by hand, so the picture never changes it", dim, null,
                "Manual: " + F2(dim) + (badDim != null ? " (" + badDim + ")" : ""));
        }
        WashResult Plain(string why)
        {
            why = badDim != null ? badDim + "; " + why : why;
            return new WashResult(dim, "auto", false, false, why, dim, null, "Automatic: " + F2(dim) + ", not raised: " + why);
        }
        if (!string.IsNullOrEmpty(unusable)) return Plain(unusable);
        if (desktop is not JsonObject d) return Plain("no desktop picture from Mantle");
        if (LookSyncCore.IsBool(d["ok"], out var ok) && !ok) {
            string? r = LookSyncCore.Str(d["reason"]);
            return Plain("Mantle has no desktop picture" + (!string.IsNullOrEmpty(r) ? " (" + r + ")" : ""));
        }
        if (string.IsNullOrEmpty(LookSyncCore.Str(d["still"]))) return Plain("Mantle's desktop record names no picture");
        if (d["guard"] is not JsonObject gd) {
            string? gr = LookSyncCore.Str(d["guardReason"]);
            return Plain(!string.IsNullOrEmpty(gr) ? "Mantle couldn't work out the top band (" + gr + ")" : "Mantle has not published a top band for this picture");
        }
        if (gd[t] is not JsonObject e) return Plain("Mantle has not published the " + t + " top band yet");
        if (!LookSyncCore.IsFinite(e["alpha"], out var ea) || ea < 0 || ea > 1) return Plain("Mantle's " + t + " top band " + A2(e, "alpha") + " is not an alpha (0-1)");
        if (!LookSyncCore.IsFinite(e["dim"], out var ed)) return Plain("Mantle's " + t + " top band names no Dim");
        if (!LookSyncCore.IsFinite(e["blurPx"], out var eb)) return Plain("Mantle's " + t + " top band names no blur");
        if (R2(ed) != R2(dim) || R2(eb) != R2(blur)) {
            return Plain("Mantle worked out the " + t + " top band for Dim " + F2(ed) + " and blur " + JsJson.Number(eb) +
                " px, not this Dim " + F2(dim) + " and blur " + JsJson.Number(blur) + " px (waiting for Mantle to work it out again)");
        }
        double ceil = Math.Max(Ceil, dim);
        double a = R2(ea);
        if (a > ceil + 1e-9) return Plain("Mantle's " + t + " top band " + F2(a) + " is above the " + F2(ceil) + " ceiling");
        double? contrast = LookSyncCore.IsFinite(e["contrast"], out var c) ? c : null;
        if (a > dim + 1e-9) {
            return new WashResult(a, "auto", true, true, "raised to " + F2(a) + " so text stays readable over this picture", dim, contrast,
                "Automatic: " + F2(dim) + ", raised to " + F2(a) + " so text stays readable over this picture");
        }
        string pre = badDim != null ? badDim + "; " : "";
        return new WashResult(dim, "auto", false, true, pre + "text already reads over this picture at this Dim", dim, contrast,
            "Automatic: " + F2(dim) + ", " + pre + "text already reads over this picture at this Dim");
    }
}
