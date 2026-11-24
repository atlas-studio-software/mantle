// LevitateMotion.cs - the levitated look's motion on composition (lane r10-newlook, LOOK.md): sink-and-lift of views,
// the sliding highlight, pop-overs, press, the first load and the morph, from Mantle.Core Motion's tracks. Transform and
// opacity ONLY (Translation, Scale, Opacity on the element's visual), never width or blur. Each curve is sampled from
// Core's Bezier into linear keyframes (the way PopupWindow plays the bar's spring), so ease-lift's hair of overshoot is
// exactly Core's and the pure tests cover it. Composition runs the animations: no frame loop of our own, idle at rest.
// The OS reduce-motion setting (Animation effects off): no travel, quick fades only (Motion.Reduce). No pointer parallax
// or cursor-follow: nothing here runs unless a surface calls it.
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Mantle.App.Shell.Levitate;

internal static class LevitateMotion
{
    /// Keyframes per curve (plus the last).
    public const int Samples = 24;

    /// Windows' "Animation effects" is off (UISettings.AnimationsEnabled false); read each time (the user may flip it).
    public static bool Reduced()
    {
        try { return !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { return false; }
    }

    static Visual Vis(UIElement el)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        return v;
    }

    /// The composition channel a Core track animates (a scale track animates both Scale.X and Scale.Y).
    static string[] Channels(string property) => property switch {
        "opacity" => new[] { "Opacity" },
        "translateX" => new[] { "Translation.X" },
        "translateY" => new[] { "Translation.Y" },
        "scale" => new[] { "Scale.X", "Scale.Y" },
        "scaleX" => new[] { "Scale.X" },
        "scaleY" => new[] { "Scale.Y" },
        _ => Array.Empty<string>(),
    };

    static ScalarKeyFrameAnimation Scalar(Compositor c, Track t)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        var lin = c.CreateLinearEasingFunction();
        a.Duration = TimeSpan.FromMilliseconds(Math.Max(1, t.DurMs));
        a.DelayTime = TimeSpan.FromMilliseconds(t.DelayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        for (int i = 0; i <= Samples; i++) {
            double x = i / (double)Samples;
            double y = t.DurMs <= 0 ? 1 : t.Curve.Y(x);   // a reduced transform: straight to its end
            a.InsertKeyFrame((float)x, (float)(t.From + (t.To - t.From) * y), lin);
        }
        return a;
    }

    /// Lifting in (a view arriving): from translateY 28 and scale 0.965, fade and rise on ease-lift (Motion.LiftIn).
    public static void LiftIn(UIElement el, Action? done = null) => Play(el, Motion.LiftIn, done);

    /// Sinking out (a view leaving): fade out, sink 12 DIP and shrink to 0.955 about 50 % 35 % (Motion.SinkOut).
    public static void SinkOut(UIElement el, Action? done = null) => Play(el, Motion.SinkOut, done);

    /// A pop-over opening / closing (Motion.PopIn / PopOut), about its top centre.
    public static void PopIn(UIElement el, Action? done = null) => Play(el, Motion.PopIn, done, 0.5, 0);
    public static void PopOut(UIElement el, Action? done = null) => Play(el, Motion.PopOut, done, 0.5, 0);

    /// A press / release at one of LevitateTokens' press scales (0.94 small buttons, 0.97 pills, 0.985 big tiles), about
    /// the centre.
    public static void Press(UIElement el, double scale) => Play(el, new[] { Motion.Press(scale) }, null, 0.5, 0.5);
    public static void Release(UIElement el, double scale) => Play(el, new[] { Motion.Release(scale) }, null, 0.5, 0.5);

    static readonly ConditionalWeakTable<UIElement, object> Risen = new();

    /// The first load only: the piece at <paramref name="index"/> rises 10 with opacity (Motion.FirstLoad). Never replayed:
    /// a second call on the same element does nothing.
    public static void FirstLoad(UIElement el, int index)
    {
        if (Risen.TryGetValue(el, out _)) return;
        Risen.Add(el, new object());
        Play(el, Motion.FirstLoad(index));
    }

    /// The morph: the panel <paramref name="el"/> (laid out at its final rect, DIP in the same space as the box) grows out
    /// of the small box over 480 ms (Motion.Morph, about its top-left). The surface plays MorphOld / MorphNew / MorphScrim
    /// on the contents and the scrim; MorphFallback when the box is not known.
    public static void Morph(UIElement el, Windows.Foundation.Rect from, Windows.Foundation.Rect to, Action? done = null) =>
        Play(el, Motion.Morph(from.X, from.Y, from.Width, from.Height, to.X, to.Y, to.Width, to.Height), done, 0, 0);

    /// Plays a set of tracks on an element's visual about (<paramref name="ox"/>, <paramref name="oy"/>) as fractions of its
    /// size (default the sink's 50 % 35 %); <paramref name="done"/> runs on the UI thread when they end. Under reduce-motion
    /// the set is Motion.Reduce's (transforms jump to their end, fades are quick).
    public static void Play(UIElement el, Track[] tracks, Action? done = null, double ox = Motion.SinkOriginX, double oy = Motion.SinkOriginY)
    {
        var v = Vis(el);
        var c = v.Compositor;
        var sz = el.ActualSize;
        v.CenterPoint = new Vector3(sz.X * (float)ox, sz.Y * (float)oy, 0);
        foreach (var t in tracks) foreach (var ch in Channels(t.Property)) v.StopAnimation(ch);
        if (Reduced()) tracks = Motion.Reduce(tracks);
        var q = DispatcherQueue.GetForCurrentThread();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (var t in tracks) foreach (var ch in Channels(t.Property)) v.StartAnimation(ch, Scalar(c, t));
        batch.End();
        if (done is { } d) batch.Completed += (_, _) => { if (q == null || !q.TryEnqueue(() => d())) d(); };
    }

    /// The sliding highlight: ONE indicator (laid out at x 0 with its base width) moves to x and takes the width over
    /// 320 ms on ease-lift as Translation.X + Scale.X about its left edge (Motion.Slide; never its Width). The accent
    /// underline (16 x 2, a separate element laid out at x 0, never stretched) rides along centred under it
    /// (Motion.SlideUnderline). <paramref name="instant"/>: its first placement, no transition (so it does not fly in
    /// from 0). Reduce-motion: the same, no travel.
    public static void Slide(FrameworkElement indicator, double fromX, double fromW, double toX, double toW, bool instant, UIElement? underline = null)
    {
        var v = Vis(indicator);
        var c = v.Compositor;
        double baseW = indicator.ActualWidth > 0 ? indicator.ActualWidth : 1;
        v.CenterPoint = new Vector3(0, v.CenterPoint.Y, 0);
        v.StopAnimation("Translation.X");
        v.StopAnimation("Scale.X");
        var u = underline == null ? null : Vis(underline);
        u?.StopAnimation("Translation.X");
        var tracks = Motion.Slide(fromX, fromW, toX, toW, baseW);
        var line = Motion.SlideUnderline(fromX, fromW, toX, toW);
        if (Motion.SlideDuration(instant, Reduced()) <= 0) {
            SetX(v, (float)tracks[0].To);
            v.Scale = new Vector3((float)tracks[1].To, v.Scale.Y, 1);
            if (u != null) SetX(u, (float)line.To);
            return;
        }
        v.StartAnimation("Translation.X", Scalar(c, tracks[0]));
        v.StartAnimation("Scale.X", Scalar(c, tracks[1]));
        u?.StartAnimation("Translation.X", Scalar(c, line));
    }

    static void SetX(Visual v, float x)
    {
        var tr = v.Properties.TryGetVector3("Translation", out var have) == CompositionGetValueStatus.Succeeded ? have : Vector3.Zero;
        tr.X = x;
        v.Properties.InsertVector3("Translation", tr);
    }
}
