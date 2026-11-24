using System;

namespace Mantle.Core.Look.Levitate;

/// <summary>The still laid out "cover" over a monitor (DIP): its scale, its drawn size and its top-left on the monitor.</summary>
public readonly record struct CoverFit(double S, double Cw, double Ch, double Ox, double Oy);

/// <summary>
/// Where a floating panel ("island") shows its part of the blurred still, so every panel lines up with the screen and the
/// picture stays put while panels move over it (prototype quartz-home/app.js, 2026-11-24).
/// </summary>
public static class Island
{
    /// A panel is BIG (it shows the soft copy, else the light one) when min(w, h) &gt;= 48 AND w * h &gt;= 20000. The panel's
    /// LAYOUT size in DIP, never an animated rect (a panel lifting in or sinking out keeps its copy).
    public static bool IsBig(double w, double h) => Math.Min(w, h) >= LevitateTokens.BigMinSide && w * h >= LevitateTokens.BigMinArea;

    /// The still (iw x ih, any unit: only its aspect matters) laid out "cover" over a vw x vh monitor (DIP):
    /// s = max(vw/iw, vh/ih), cw = iw*s, ch = ih*s, ox = (vw-cw)/2, oy = (vh-ch)/2. No rounding.
    public static CoverFit Cover(double iw, double ih, double vw, double vh)
    {
        if (!(iw > 0) || !(ih > 0) || !(vw > 0) || !(vh > 0)) return new CoverFit(0, 0, 0, 0, 0);
        double s = Math.Max(vw / iw, vh / ih);
        double cw = iw * s, ch = ih * s;
        return new CoverFit(s, cw, ch, (vw - cw) / 2, (vh - ch) / 2);
    }

    /// Where a panel at (left, top) on the monitor (DIP) draws the blurred copy: at (ox - left, oy - top) in its own space,
    /// size (cw, ch).
    public static (double X, double Y, double W, double H) Draw(CoverFit fit, double left, double top) =>
        (fit.Ox - left, fit.Oy - top, fit.Cw, fit.Ch);

    /// The bitmap pixel (bw x bh, the blurred copy) under the monitor point (sx, sy) in DIP, through the cover geometry:
    /// floor((sx - ox) / cw * bw), clamped to the bitmap (the same for y).
    public static (int X, int Y) ToBitmap(CoverFit fit, int bw, int bh, double sx, double sy)
    {
        int x = fit.Cw > 0 ? (int)Math.Floor((sx - fit.Ox) / fit.Cw * bw) : 0;
        int y = fit.Ch > 0 ? (int)Math.Floor((sy - fit.Oy) / fit.Ch * bh) : 0;
        return (Math.Clamp(x, 0, Math.Max(0, bw - 1)), Math.Clamp(y, 0, Math.Max(0, bh - 1)));
    }
}
