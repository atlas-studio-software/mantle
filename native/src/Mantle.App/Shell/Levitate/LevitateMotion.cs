// LevitateMotion.cs - the levitated look's motion on composition (lane r10-newlook): sink-and-lift on switches and the
// sliding highlight, from Mantle.Core Motion's tracks. Each curve is sampled from Core's Bezier into linear keyframes (the
// way PopupWindow plays the bar's spring), so ease-lift's hair of overshoot is exactly Core's and the pure tests cover it.
// The OS reduce-motion setting (Animation effects off): no movement, just the end state. No pointer-follow parallax, no
// drift: nothing here runs unless a surface calls it.
using System;
using System.Numerics;
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
        _ => Array.Empty<string>(),   // "width" is the slide's own (Scale.X over the indicator's base width)
    };

    static ScalarKeyFrameAnimation Scalar(Compositor c, Track t, double k = 1)
    {
        var a = c.CreateScalarKeyFrameAnimation();
        var lin = c.CreateLinearEasingFunction();
        a.Duration = TimeSpan.FromMilliseconds(Math.Max(1, t.DurMs));
        a.DelayTime = TimeSpan.FromMilliseconds(t.DelayMs);
        a.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        for (int i = 0; i <= Samples; i++) {
            double x = i / (double)Samples;
            a.InsertKeyFrame((float)x, (float)((t.From + (t.To - t.From) * t.Curve.Y(x)) * k), lin);
        }
        return a;
    }

    /// Lifting in (a panel switched to): fade in and rise 10 DIP on ease-lift (Motion.LiftIn).
    public static void LiftIn(UIElement el, Action? done = null) => Play(el, Motion.LiftIn, done);

    /// Sinking out (a panel switched away from): fade out, sink 12 DIP and shrink to 0.955 about 50 % 35 % (Motion.SinkOut).
    public static void SinkOut(UIElement el, Action? done = null) => Play(el, Motion.SinkOut, done);

    /// Plays a set of tracks on an element's visual; <paramref name="done"/> runs on the UI thread when they end (at once
    /// under reduce-motion, which sets the end state).
    public static void Play(UIElement el, Track[] tracks, Action? done = null)
    {
        var v = Vis(el);
        var c = v.Compositor;
        var sz = el.ActualSize;
        v.CenterPoint = new Vector3(sz.X * (float)Motion.SinkOriginX, sz.Y * (float)Motion.SinkOriginY, 0);
        foreach (var t in tracks) foreach (var ch in Channels(t.Property)) v.StopAnimation(ch);
        if (Reduced()) { End(v, tracks); done?.Invoke(); return; }
        var q = DispatcherQueue.GetForCurrentThread();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (var t in tracks) foreach (var ch in Channels(t.Property)) v.StartAnimation(ch, Scalar(c, t));
        batch.End();
        if (done is { } d) batch.Completed += (_, _) => { if (q == null || !q.TryEnqueue(() => d())) d(); };
    }

    /// The tracks' end state, no movement.
    static void End(Visual v, Track[] tracks)
    {
        var tr = v.Properties.TryGetVector3("Translation", out var have) == CompositionGetValueStatus.Succeeded ? have : Vector3.Zero;
        foreach (var t in tracks) {
            float to = (float)Motion.At(t, 0, reduceMotion: true);
            switch (t.Property) {
                case "opacity": v.Opacity = to; break;
                case "translateX": tr.X = to; break;
                case "translateY": tr.Y = to; break;
                case "scale": v.Scale = new Vector3(to, to, 1); break;
            }
        }
        v.Properties.InsertVector3("Translation", tr);
    }

    /// The sliding highlight: the selection indicator (laid out at x 0 with its base width) moves to x and stretches to
    /// width over 320 ms on ease-lift (translateX + width as Translation.X and Scale.X about its left edge).
    /// <paramref name="instant"/>: its first placement, no transition (so it does not fly in from 0). Reduce-motion: the
    /// same, no movement.
    public static void Slide(FrameworkElement indicator, double fromX, double fromW, double toX, double toW, bool instant)
    {
        var v = Vis(indicator);
        var c = v.Compositor;
        double baseW = indicator.ActualWidth > 0 ? indicator.ActualWidth : 1;
        v.CenterPoint = new Vector3(0, v.CenterPoint.Y, 0);
        v.StopAnimation("Translation.X");
        v.StopAnimation("Scale.X");
        if (Motion.SlideDuration(instant, Reduced()) <= 0) {
            var tr = v.Properties.TryGetVector3("Translation", out var have) == CompositionGetValueStatus.Succeeded ? have : Vector3.Zero;
            tr.X = (float)toX;
            v.Properties.InsertVector3("Translation", tr);
            v.Scale = new Vector3((float)(toW / baseW), v.Scale.Y, 1);
            return;
        }
        var tracks = Motion.Slide(fromX, fromW, toX, toW);
        v.StartAnimation("Translation.X", Scalar(c, tracks[0]));
        v.StartAnimation("Scale.X", Scalar(c, tracks[1], 1 / baseW));
    }
}
