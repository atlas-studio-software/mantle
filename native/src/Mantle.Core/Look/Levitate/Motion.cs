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
/// <param name="Property">"opacity" | "translateY" | "translateX" | "scale" | "width"</param>
public readonly record struct Track(string Property, double From, double To, double DurMs, double DelayMs, Bezier Curve);

/// <summary>
/// The levitated look's motion primitives (prototype quartz-home/style.css, 2026-11-24), pure: what a surface animates and the
/// value of a track at a time. The WinUI side (Mantle.App/Shell/Levitate/LevitateMotion.cs) plays them on composition.
/// <list type="bullet">
/// <item>Sink-and-lift on switches: <see cref="LiftIn"/> (fade in, rise 10 px with ease-lift) and <see cref="SinkOut"/>
/// (fade out, sink 12 px and shrink to 0.955 about <see cref="SinkOriginX"/>, <see cref="SinkOriginY"/>).</item>
/// <item>The sliding highlight: the selection indicator moves (translateX + width) over <see cref="SlideMs"/> on ease-lift;
/// an instant mode (no transition) for its first placement.</item>
/// <item>The OS reduce-motion setting: no movement, just the end state (<see cref="At"/> with reduce).</item>
/// </list>
/// No pointer-follow parallax and no drift: nothing here moves on its own or follows the pointer.
/// </summary>
public static class Motion
{
    /// Lifting in: opacity 0 -> 1 over 280 ms (ease, 60 ms delay) + translateY 10 -> 0 over 420 ms (ease-lift, 40 ms delay).
    public static readonly Track[] LiftIn =
    {
        new("opacity", 0, 1, 280, 60, LevitateTokens.Ease),
        new("translateY", 10, 0, 420, 40, LevitateTokens.EaseLift),
    };

    /// Sinking out: opacity -> 0 over 200 ms + translateY 0 -> 12 and scale 1 -> 0.955 over 300 ms (ease), no delay.
    public static readonly Track[] SinkOut =
    {
        new("opacity", 1, 0, 200, 0, LevitateTokens.Ease),
        new("translateY", 0, 12, 300, 0, LevitateTokens.Ease),
        new("scale", 1, 0.955, 300, 0, LevitateTokens.Ease),
    };

    /// The sink's transform origin, as fractions of the element's size: 50 % 35 %.
    public const double SinkOriginX = 0.5, SinkOriginY = 0.35;

    /// The sliding highlight's duration (translateX and width together, ease-lift).
    public const double SlideMs = 320;

    /// The slide's two tracks from one place (x, width) to another.
    public static Track[] Slide(double fromX, double fromW, double toX, double toW) => new Track[]
    {
        new("translateX", fromX, toX, SlideMs, 0, LevitateTokens.EaseLift),
        new("width", fromW, toW, SlideMs, 0, LevitateTokens.EaseLift),
    };

    /// How long the slide runs: 0 for its first placement (instant) and under reduce-motion, else <see cref="SlideMs"/>.
    public static double SlideDuration(bool instant, bool reduceMotion) => instant || reduceMotion ? 0 : SlideMs;

    /// When the last track of a set has ended (ms): the delay plus the duration of the longest; 0 under reduce-motion.
    public static double TotalMs(Track[] tracks, bool reduceMotion)
    {
        if (reduceMotion) return 0;
        double end = 0;
        foreach (var t in tracks) end = Math.Max(end, t.DelayMs + t.DurMs);
        return end;
    }

    /// A track's value <paramref name="ms"/> after the start: From until its delay is over, then along its curve to To, To after.
    /// Under reduce-motion it is To at once (no movement, just the end state).
    public static double At(Track t, double ms, bool reduceMotion)
    {
        if (reduceMotion || t.DurMs <= 0) return t.To;
        double x = (ms - t.DelayMs) / t.DurMs;
        if (x <= 0) return t.From;
        if (x >= 1) return t.To;
        return t.From + (t.To - t.From) * t.Curve.Y(x);
    }
}
