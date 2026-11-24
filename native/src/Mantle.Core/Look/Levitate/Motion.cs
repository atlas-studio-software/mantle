using System;

namespace Mantle.Core.Look.Levitate;

/// <summary>A CSS <c>cubic-bezier(x1, y1, x2, y2)</c> timing curve: <see cref="Y"/> is the eased progress at time progress x.</summary>
public readonly record struct Bezier(double X1, double Y1, double X2, double Y2)
{
    static double Coord(double p1, double p2, double t) => 3 * p1 * t * (1 - t) * (1 - t) + 3 * p2 * t * t * (1 - t) + t * t * t;
    static double Slope(double p1, double p2, double t) => 3 * p1 * (1 - t) * (1 - t) + 6 * (p2 - p1) * t * (1 - t) + 3 * (1 - p2) * t * t;

    /// The curve's parameter t at time progress x (0..1): Newton from t = x, bisection when the slope is flat (the browsers' way).
    public double T(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        double t = x;
        for (int k = 0; k < 8; k++) {
            double e = Coord(X1, X2, t) - x;
            if (Math.Abs(e) < 1e-7) return t;
            double d = Slope(X1, X2, t);
            if (Math.Abs(d) < 1e-6) break;
            t -= e / d;
            if (t < 0 || t > 1) break;
        }
        double lo = 0, hi = 1;
        t = x;
        for (int k = 0; k < 60 && hi - lo > 1e-9; k++) {
            if (Coord(X1, X2, t) < x) lo = t; else hi = t;
            t = (lo + hi) / 2;
        }
        return t;
    }

    /// The eased progress at time progress x; 0 at or before 0, 1 at or after 1 (ease-lift goes a hair above 1 in between).
    public double Y(double x) => x <= 0 ? 0 : x >= 1 ? 1 : Coord(Y1, Y2, T(x));

    /// "x1,y1,x2,y2", the way Theme/Levitate.xaml writes a curve (Tokens.xaml's *Bezier strings).
    public override string ToString() => FormattableString.Invariant($"{X1},{Y1},{X2},{Y2}");
}

/// <summary>One animated property: from, to, how long, after what delay, on which curve.</summary>
/// <param name="Property">"opacity" | "translateX" | "translateY" | "scale" | "scaleX" | "scaleY" (transform and opacity
/// only: never width or blur)</param>
public readonly record struct Track(string Property, double From, double To, double DurMs, double DelayMs, Bezier Curve);

/// <summary>
/// The levitated look's motion primitives (LOOK.md, look-sync design commit 97aa001), pure: what a surface animates and the
/// value of a track at a time. The WinUI side (Mantle.App/Shell/Levitate/LevitateMotion.cs) plays them on composition.
/// Transform and opacity ONLY, never width or blur.
/// <list type="bullet">
/// <item>Views: <see cref="SinkOut"/> (leaving) and <see cref="LiftIn"/> (arriving). Page-to-page swaps only the content,
/// with a cross-fade.</item>
/// <item>The sliding highlight: ONE element slides to the selected item (translateX + scaleX, NOT width) over
/// <see cref="SlideMs"/> on ease-lift, carrying the accent underline <see cref="UnderlineW"/> x <see cref="UnderlineH"/>;
/// an instant mode for its first placement.</item>
/// <item>Pop-overs <see cref="PopIn"/> / <see cref="PopOut"/>; press <see cref="Press"/>; the first load
/// <see cref="FirstLoad"/> (never replayed); the morph of a small box into a big panel <see cref="Morph"/> and its
/// fallback.</item>
/// <item>Reduced motion: no travel, quick fades only (<see cref="Reduce(Track)"/>).</item>
/// </list>
/// No pointer parallax or cursor-follow of any kind: nothing here moves on its own or follows the pointer.
/// </summary>
public static class Motion
{
    /// Sinking out (a view leaving): opacity -> 0 over 200 ms + translateY 0 -> 12 and scale 1 -> 0.955 over 300 ms, ease.
    public static readonly Track[] SinkOut =
    {
        new("opacity", 1, 0, 200, 0, LevitateTokens.Ease),
        new("translateY", 0, 12, 300, 0, LevitateTokens.Ease),
        new("scale", 1, 0.955, 300, 0, LevitateTokens.Ease),
    };

    /// Lifting in (a view arriving), FROM translateY 28 and scale 0.965: opacity over 240 ms after 40 ms, the transform over
    /// 460 ms after 20 ms, ease-lift.
    public static readonly Track[] LiftIn =
    {
        new("opacity", 0, 1, 240, 40, LevitateTokens.EaseLift),
        new("translateY", 28, 0, 460, 20, LevitateTokens.EaseLift),
        new("scale", 0.965, 1, 460, 20, LevitateTokens.EaseLift),
    };

    /// The sink's transform origin, as fractions of the element's size: 50 % 35 % (the first spec's; LOOK.md does not
    /// change it).
    public const double SinkOriginX = 0.5, SinkOriginY = 0.35;

    /// The sliding highlight's duration (translateX and scaleX together, ease-lift).
    public const double SlideMs = 320;
    /// The accent underline the highlight carries (DIP).
    public const double UnderlineW = 16, UnderlineH = 2;

    /// The slide from one place (x, width) to another of an element laid out at x 0 with <paramref name="baseW"/>: its
    /// translateX and its scaleX (about its left edge; width / baseW). A baseW of 0 or less counts as 1.
    public static Track[] Slide(double fromX, double fromW, double toX, double toW, double baseW)
    {
        double b = baseW > 0 ? baseW : 1;
        return new Track[]
        {
            new("translateX", fromX, toX, SlideMs, 0, LevitateTokens.EaseLift),
            new("scaleX", fromW / b, toW / b, SlideMs, 0, LevitateTokens.EaseLift),
        };
    }

    /// Where the underline sits under an item at x, width w: centred, x + (w - 16) / 2.
    public static double UnderlineX(double x, double w) => x + (w - UnderlineW) / 2;

    /// The underline's own track (translateX only, it is never stretched) along the same slide.
    public static Track SlideUnderline(double fromX, double fromW, double toX, double toW) =>
        new("translateX", UnderlineX(fromX, fromW), UnderlineX(toX, toW), SlideMs, 0, LevitateTokens.EaseLift);

    /// How long the slide runs: 0 for its first placement (instant) and under reduced motion (it is travel), else 320.
    public static double SlideDuration(bool instant, bool reduceMotion) => instant || reduceMotion ? 0 : SlideMs;

    /// A pop-over opening, from translateY -6 and scale 0.97: opacity 160 ms, translate + scale 260 ms, ease-lift.
    public static readonly Track[] PopIn =
    {
        new("opacity", 0, 1, 160, 0, LevitateTokens.EaseLift),
        new("translateY", -6, 0, 260, 0, LevitateTokens.EaseLift),
        new("scale", 0.97, 1, 260, 0, LevitateTokens.EaseLift),
    };

    /// A pop-over closing, back to translateY -6 and scale 0.97: opacity 120 ms, translate + scale 160 ms (ease).
    public static readonly Track[] PopOut =
    {
        new("opacity", 1, 0, 120, 0, LevitateTokens.Ease),
        new("translateY", 0, -6, 160, 0, LevitateTokens.Ease),
        new("scale", 1, 0.97, 160, 0, LevitateTokens.Ease),
    };

    /// Hover and press run 160 ms (<see cref="LevitateTokens.DurMs"/>); a press scales to 0.94 (small buttons), 0.97
    /// (pills) or 0.985 (big tiles), ease.
    public static Track Press(double scale) => new("scale", 1, scale, LevitateTokens.DurMs, 0, LevitateTokens.Ease);
    /// Letting go: back to 1 the same way.
    public static Track Release(double scale) => new("scale", scale, 1, LevitateTokens.DurMs, 0, LevitateTokens.Ease);

    /// The first load only (never replayed): pieces rise 10 with opacity over 520 ms, staggered 45 ms.
    public const double FirstLoadMs = 520, FirstLoadStaggerMs = 45, FirstLoadRise = 10;

    /// The first load's tracks for the piece at <paramref name="index"/> (0 first; a negative index counts as 0).
    public static Track[] FirstLoad(int index)
    {
        double delay = Math.Max(0, index) * FirstLoadStaggerMs;
        return new Track[]
        {
            new("opacity", 0, 1, FirstLoadMs, delay, LevitateTokens.Ease),
            new("translateY", FirstLoadRise, 0, FirstLoadMs, delay, LevitateTokens.EaseLift),
        };
    }

    /// The morph (a small box opening into a big panel): 480 ms on cubic-bezier(0.22, 1, 0.36, 1); the old content fades
    /// out over 200, the new fades in over 300 after 90, the scrim over 360; the fallback is fade + translateY 12 + scale
    /// 0.97 over 320.
    public const double MorphMs = 480, MorphOldFadeMs = 200, MorphNewFadeMs = 300, MorphNewDelayMs = 90, MorphScrimMs = 360, MorphFallbackMs = 320;

    /// The panel (laid out at its final rect t) starts over the small box f and grows into place: translateX / translateY
    /// and scaleX / scaleY about its top-left corner, all to identity. A final size of 0 or less scales from 1.
    public static Track[] Morph(double fx, double fy, double fw, double fh, double tx, double ty, double tw, double th) => new Track[]
    {
        new("translateX", fx - tx, 0, MorphMs, 0, LevitateTokens.EaseMorph),
        new("translateY", fy - ty, 0, MorphMs, 0, LevitateTokens.EaseMorph),
        new("scaleX", tw > 0 ? fw / tw : 1, 1, MorphMs, 0, LevitateTokens.EaseMorph),
        new("scaleY", th > 0 ? fh / th : 1, 1, MorphMs, 0, LevitateTokens.EaseMorph),
    };

    /// The morph's three fades: the old content out, the new in (after 90), the scrim in.
    public static readonly Track MorphOld = new("opacity", 1, 0, MorphOldFadeMs, 0, LevitateTokens.EaseMorph);
    public static readonly Track MorphNew = new("opacity", 0, 1, MorphNewFadeMs, MorphNewDelayMs, LevitateTokens.EaseMorph);
    public static readonly Track MorphScrim = new("opacity", 0, 1, MorphScrimMs, 0, LevitateTokens.EaseMorph);

    /// The morph's fallback: fade in, translateY 12 -> 0 and scale 0.97 -> 1 over 320 ms, ease.
    public static readonly Track[] MorphFallback =
    {
        new("opacity", 0, 1, MorphFallbackMs, 0, LevitateTokens.Ease),
        new("translateY", 12, 0, MorphFallbackMs, 0, LevitateTokens.Ease),
        new("scale", 0.97, 1, MorphFallbackMs, 0, LevitateTokens.Ease),
    };

    /// Under reduced motion a fade is at most this long (and starts at once): "quick fades only". LOOK.md names no number;
    /// this is the hover / press duration.
    public const double ReducedFadeMs = LevitateTokens.DurMs;

    /// A track under reduced motion: no travel (a transform jumps to its end: duration 0, no delay); an opacity track stays
    /// a fade, at most <see cref="ReducedFadeMs"/> long, with no delay.
    public static Track Reduce(Track t) => t.Property == "opacity"
        ? t with { DurMs = Math.Min(t.DurMs, ReducedFadeMs), DelayMs = 0 }
        : t with { DurMs = 0, DelayMs = 0 };

    /// <see cref="Reduce(Track)"/> over a set.
    public static Track[] Reduce(Track[] tracks)
    {
        var o = new Track[tracks.Length];
        for (int i = 0; i < tracks.Length; i++) o[i] = Reduce(tracks[i]);
        return o;
    }

    /// When the last track of a set has ended (ms): the delay plus the duration of the longest (of the reduced set under
    /// reduced motion).
    public static double TotalMs(Track[] tracks, bool reduceMotion)
    {
        double end = 0;
        foreach (var t0 in tracks) {
            var t = reduceMotion ? Reduce(t0) : t0;
            end = Math.Max(end, t.DelayMs + t.DurMs);
        }
        return end;
    }

    /// A track's value <paramref name="ms"/> after the start: From until its delay is over, then along its curve to To, To
    /// after. Under reduced motion the track is <see cref="Reduce(Track)"/>'s (a transform is To at once).
    public static double At(Track t, double ms, bool reduceMotion)
    {
        if (reduceMotion) t = Reduce(t);
        if (t.DurMs <= 0) return t.To;
        double x = (ms - t.DelayMs) / t.DurMs;
        if (x <= 0) return t.From;
        if (x >= 1) return t.To;
        return t.From + (t.To - t.From) * t.Curve.Y(x);
    }
}
