// LevitateShadow.cs - the levitated look's soft shadows (lane r10-newlook): float and float-hi, drawn the way Chromium
// draws a CSS box-shadow by the OSD's CssShadow (sigma = blur / 2, spread, clipped to outside the box), from the layers in
// Mantle.Core LevitateTokens: float (every island), float-hi (hover, pop-overs, lifted) and sheet (a page / content
// sheet). The only edge treatment of the look: no hairline, ring or top highlight; no sheen at rest.
using System.Collections.Generic;
using Mantle.App.Surfaces.Osd;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Shell.Levitate;

internal static class LevitateShadow
{
    static CssShadow.Layer[] Css(ShadowLayer[] ls)
    {
        var o = new CssShadow.Layer[ls.Length];
        for (int i = 0; i < ls.Length; i++) o[i] = new CssShadow.Layer(ls[i].Dx, ls[i].Dy, ls[i].Blur, ls[i].Spread, ls[i].R, ls[i].G, ls[i].B, ls[i].A);
        return o;
    }

    /// float: every island.
    public static readonly CssShadow.Layer[] Float = Css(LevitateTokens.Float);
    /// float-hi: hover, pop-overs, lifted.
    public static readonly CssShadow.Layer[] FloatHi = Css(LevitateTokens.FloatHi);
    /// sheet: a page / content sheet.
    public static readonly CssShadow.Layer[] Sheet = Css(LevitateTokens.Sheet);

    /// The shadow bitmap (w x h physical px, at <paramref name="scale"/> px per DIP) round each island box (DIP, in the
    /// bitmap's space, with its radius). The window needs room round its islands for it: the float layers reach
    /// 30 + 56 - 26 = 60 DIP below an island, float-hi 44 + 72 - 28 = 88 DIP, sheet 60 + 100 - 40 = 120 DIP; Gap (12 DIP) is
    /// the air to the screen edge.
    public static WriteableBitmap Bitmap(int w, int h, double scale, IReadOnlyList<CssShadow.Box> islands, bool hi = false) =>
        CssShadow.Bitmap(w, h, scale, islands, hi ? FloatHi : Float);

    /// The same with any of the three (<see cref="Float"/>, <see cref="FloatHi"/>, <see cref="Sheet"/>).
    public static WriteableBitmap Bitmap(int w, int h, double scale, IReadOnlyList<CssShadow.Box> islands, CssShadow.Layer[] layers) =>
        CssShadow.Bitmap(w, h, scale, islands, layers);
}
