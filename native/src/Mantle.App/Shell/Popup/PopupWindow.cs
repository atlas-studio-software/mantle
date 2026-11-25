using System;
using System.Numerics;
using Mantle.App.Native;
using Mantle.App.Shell.Glass;
using Mantle.App.Shell.Levitate;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core.Look.Levitate;
using Mantle.App.Shell.Windowing;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;

namespace Mantle.App.Shell.Popups;

/// One pop-up window (WIN-006): frameless, see-through, clipped to its shape so clicks outside it reach what is
/// below (popup spike 5), always on top, no frame line. A no-focus pop-up never takes the keyboard (WS_EX_NOACTIVATE);
/// a focusable one asks for it after the show (WIN-015). An instant kind is parked (cloaked, ~1 MB, 0 CPU) between
/// opens instead of being closed (popup spike 7: one parked window per instant pop-up, everything else on demand).
internal sealed class PopupWindow
{
    public string Key { get; }
    public string Name { get; }
    public bool Focusable { get; }
    public bool Instant { get; }
    public Window Win { get; }
    public IntPtr Hwnd { get; }
    public Grid Root { get; }
    public Win32.RECT Rect { get; private set; }
    public bool Parked { get; private set; }
    public bool Painted { get; set; }
    bool _activated;

    public PopupWindow(string key, string name, bool focusable, bool instant, bool dark, UIElement body, bool levitate = false)
    {
        Key = key; Name = name; Focusable = focusable; Instant = instant; Levitate = levitate;
        Root = new Grid { RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light };
        const string X = "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' ";
        Border face;
        if (levitate) {
            // lane r12-levitate-host: the levitated look (Shell/Levitate, Mantle.Core PopupLevitate): the pop-up's part of the
            // frosted still lined up with the screen, the dim, the dark film and the hover-only sheen under the body, all
            // rounded 14 (pop-overs); no hairline edge, no tint, no sheen at rest. The body paints no background of its own.
            var r = new CornerRadius(PopupLevitate.Radius);
            var lev = new LevitatePanel("pop-up " + name, r);
            _lev = lev;
            _lift = new PopupLift();
            Root.Children.Add(lev.Picture); Root.Children.Add(lev.Dim); Root.Children.Add(lev.Film); Root.Children.Add(lev.Sheen);
            // clear but hit-testable, so the hover sheen follows the whole panel, not only its controls
            face = new Border { CornerRadius = r, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Root.PointerEntered += (_, _) => lev.Hover(true);
            Root.PointerExited += (_, _) => lev.Hover(false);
        } else if (DesktopGlass.ElectronLook) {
            // MANTLE_TEST_GLASS=electron (the Electron pairs): --lg-fill-pop with the outer hairline, radius --lg-r-pop
            face = (Border)XamlReader.Load(X + "Background='{ThemeResource LgFillPop}' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='1' CornerRadius='{ThemeResource LgRPopCorner}'/>");
        } else {
            // the see-through look (look.md SEE-009): the desktop still lined up with the screen and one tint, under the
            // hairline and the body; with no still, the thicker fill --lg-fill-pop inside the hairline (BAR-374) and the log says why
            face = (Border)XamlReader.Load(X + "BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='1' CornerRadius='{ThemeResource LgRPopCorner}'/>");
            var fill = (Border)XamlReader.Load(X + "Background='{ThemeResource LgFillPop}' BorderBrush='Transparent' BorderThickness='1' CornerRadius='{ThemeResource LgRPopCorner}' IsHitTestVisible='False'/>");
            _glassFill = fill;
            Root.Children.Add(fill);
        }
        face.Child = body;
        Root.Children.Add(face);
        Win = new Window { Title = "Mantle pop-up: " + name, Content = Root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        Win.AppWindow.SetPresenter(p);
        Win.AppWindow.IsShownInSwitchers = false;
        Win.SystemBackdrop = new TransparentBackdrop();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        WindowKinds.Frameless(Hwnd);
        WindowKinds.StripFrame(Hwnd);
        long ex = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW;
        if (!focusable || Host.Quiet) ex |= Win32.WS_EX_NOACTIVATE;
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;   // WIN-028: a quiet test window draws nothing
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) Win32.SetLayeredWindowAttributes(Hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(Hwnd);
        if (_glassFill is { } f) {
            // the layers sit at the window's top-left (the pop-up fills its window above the motion slack)
            Glass = new GlassLayer("pop-up " + name, Hwnd, new CornerRadius(16), _ => (Rect.Left, Rect.Top), plain => f.Visibility = plain ? Visibility.Visible : Visibility.Collapsed);
            Root.Children.Insert(0, Glass.Tint);
            Root.Children.Insert(0, Glass.Picture);
            Win.AppWindow.Changed += (_, e) => { if (e.DidPositionChange && Glass != null) { Rect = Now(); Glass.Moved(); } };
        }
    }

    readonly Border? _glassFill;
    /// The shared desktop-still backdrop (Shell/Glass), null in a MANTLE_TEST_GLASS=electron run and on a levitated pop-up.
    public GlassLayer? Glass { get; }

    /// The pop-up wears the levitated look (its kind asked for it, PopupKind.Levitate): <see cref="_lev"/> paints its
    /// backdrop and <see cref="_lift"/> runs its motion.
    public bool Levitate { get; }
    readonly LevitatePanel? _lev;
    readonly PopupLift? _lift;
    /// The levitated backdrop's state for the log ("frost soft", "fallback: why", ...), null on another pop-up.
    public string? LevitateState => _lev?.State;

    /// A levitated pop-up's backdrop lined up with where it was placed: its rect on <paramref name="monitor"/> (physical px,
    /// at <paramref name="scale"/> px per DIP), in this theme. Nothing on another pop-up.
    public void Lift(Win32.RECT monitor, double scale, bool dark)
    {
        if (_lev == null) return;
        double s = scale > 0 ? scale : 1;
        _lev.Place((monitor.Right - monitor.Left) / s, (monitor.Bottom - monitor.Top) / s,
            (Rect.Left - monitor.Left) / s, (Rect.Top - monitor.Top) / s, (Rect.Right - Rect.Left) / s, (Rect.Bottom - Rect.Top) / s, dark);
    }

    /// The window's rectangle now, keeping the pop-up's height (the window is taller by the motion slack).
    Win32.RECT Now()
    {
        if (!FrameNative.GetWindowRect(Hwnd, out var r)) return Rect;
        return new Win32.RECT { Left = r.Left, Top = r.Top, Right = r.Left + (Rect.Right - Rect.Left), Bottom = r.Top + (Rect.Bottom - Rect.Top) };
    }

    /// Physical px. The window is `Slack` taller than the pop-up (room below it for the entrance's rise and the
    /// exit's sink); the window region is the rounded shape + 1 px (re-applied on every size), and covers the
    /// slack only while a motion runs.
    public void Place(Win32.RECT r, double scale)
    {
        Rect = r;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        _slack = (int)Math.Ceiling((Levitate ? PopupLevitate.Slack() : RiseDip) * scale);
        _radius = (int)Math.Round((Levitate ? PopupLevitate.Radius : 16) * scale);
        Root.Width = w / scale; Root.Height = h / scale;
        Root.HorizontalAlignment = HorizontalAlignment.Left; Root.VerticalAlignment = VerticalAlignment.Top;
        Win32.SetWindowPos(Hwnd, IntPtr.Zero, r.Left, r.Top, w, h + _slack, Win32.SWP_NOACTIVATE | 0x0004 /* SWP_NOZORDER */);
        Clip(false);
    }

    void Clip(bool withSlack)
    {
        int w = Rect.Right - Rect.Left, h = Rect.Bottom - Rect.Top;
        WindowKinds.ClipToShape(Hwnd, 0, 0, w, h + (withSlack ? _slack : 0), _radius);
    }

    // ------------------------------------------------------------------ motion (BAR-387, BAR-388, BAR-390)
    // bar.css .pop: m-pop-rise 8 px on the shared spring over --m-dur-s (320 ms), fading in over its first 40 %;
    // bar.js#popLeave: an inert copy sinks 4 px and fades over 45 % of --m-dur-s (144 ms), removed 40 ms after;
    // reduced motion: a 120 ms fade each way. The native pop-up IS the copy on the way out: the host has already
    // closed it (state hidden, watches off) when Leave starts, and parks or closes the window when it ends.
    public const int DurSMs = 320, ReducedMs = 120, LeaveTailMs = 40;
    public const float RiseDip = 8, SinkDip = 4, EnterFadeK = 0.4f, LeaveK = 0.45f;
    int _slack, _radius, _motion;
    Visual? _vis;

    Visual Vis()
    {
        if (_vis != null) return _vis;
        _vis = ElementCompositionPreview.GetElementVisual(Root);
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        return _vis;
    }

    /// The entrance, from the first frame on. `spring` = the bar's spring samples (0..1). A levitated pop-up plays its own
    /// (PopupLevitate.Enter): it pops up, or lifts in when it opened in place of another (<paramref name="inPlace"/>).
    public void Enter(bool reduced, float[] spring, bool inPlace = false)
    {
        if (_lift != null) { EnterLevitated(reduced, inPlace); return; }
        int seq = ++_motion;
        var v = Vis(); var c = v.Compositor;
        v.StopAnimation("Translation"); v.StopAnimation("Opacity");
        var lin = c.CreateLinearEasingFunction();
        var op = c.CreateScalarKeyFrameAnimation();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (reduced) {
            v.Properties.InsertVector3("Translation", Vector3.Zero);
            op.Duration = TimeSpan.FromMilliseconds(ReducedMs);
            op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f, lin);
            v.StartAnimation("Opacity", op);
        } else {
            Clip(true);
            var tr = c.CreateVector3KeyFrameAnimation();
            tr.Duration = TimeSpan.FromMilliseconds(DurSMs);
            for (int i = 0; i < spring.Length; i++)
                tr.InsertKeyFrame(i / (float)(spring.Length - 1), new Vector3(0, RiseDip * (1 - spring[i]), 0), lin);
            op.Duration = TimeSpan.FromMilliseconds(DurSMs * EnterFadeK);
            op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
            v.StartAnimation("Translation", tr);
            v.StartAnimation("Opacity", op);
        }
        batch.End();
        var q = Win.DispatcherQueue;
        batch.Completed += (_, _) => q.TryEnqueue(() => { if (seq == _motion) Clip(false); });
    }

    /// The exit; `done` runs LeaveTailMs after it ends (on the UI thread), unless an Enter came first.
    public void Leave(bool reduced, Action done)
    {
        if (_lift != null) { LeaveLevitated(reduced, done); return; }
        int seq = ++_motion;
        var v = Vis(); var c = v.Compositor;
        var op = c.CreateScalarKeyFrameAnimation();
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        op.InsertExpressionKeyFrame(0f, "this.StartingValue");
        if (reduced) {
            op.Duration = TimeSpan.FromMilliseconds(ReducedMs);
            op.InsertKeyFrame(1f, 0f, c.CreateLinearEasingFunction());
            v.StartAnimation("Opacity", op);
        } else {
            Clip(true);
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
            var tr = c.CreateVector3KeyFrameAnimation();
            tr.Duration = op.Duration = TimeSpan.FromMilliseconds(DurSMs * LeaveK);
            tr.InsertExpressionKeyFrame(0f, "this.StartingValue");
            tr.InsertKeyFrame(1f, new Vector3(0, SinkDip, 0), ease);
            op.InsertKeyFrame(1f, 0f, ease);
            v.StartAnimation("Translation", tr);
            v.StartAnimation("Opacity", op);
        }
        batch.End();
        var q = Win.DispatcherQueue;
        batch.Completed += (_, _) => q.TryEnqueue(() => {
            var t = q.CreateTimer(); t.Interval = TimeSpan.FromMilliseconds(LeaveTailMs); t.IsRepeating = false;
            t.Tick += (_, _) => { t.Stop(); if (seq == _motion) done(); };
            t.Start();
        });
    }

    /// Ready to show with no motion (replaced in place, BAR-375/389): the resting pose at once.
    public void Rest()
    {
        ++_motion;
        _lift?.Gone();
        var v = Vis();
        v.StopAnimation("Translation"); v.StopAnimation("Opacity");
        if (_lift != null) {
            v.StopAnimation("Translation.Y"); v.StopAnimation("Scale.X"); v.StopAnimation("Scale.Y");
            v.Scale = Vector3.One;
        }
        v.Properties.InsertVector3("Translation", Vector3.Zero); v.Opacity = 1;
        Clip(false);
    }

    // ------------------------------------------------------------------ the levitated motion (lane r12-levitate-host)
    // Mantle.Core PopupLevitate / PopupLift: transform and opacity only, played by LevitateMotion on the root's visual.
    // The window keeps PopupLevitate.Slack() (28 DIP, the lift's travel) below the panel, covered by the region only while
    // a motion runs. Each end is checked against PopupLift's sequence, so a late end of an older motion does nothing.

    void EnterLevitated(bool reduced, bool inPlace)
    {
        var lift = _lift!;
        var tracks = lift.Open(inPlace, reduced);
        int seq = lift.Seq;
        Clip(true);
        PlayLevitated(tracks, PopupLevitate.Origin(inPlace), () => { if (lift.Ended(seq)) Clip(false); });
    }

    void LeaveLevitated(bool reduced, Action done)
    {
        var lift = _lift!;
        var tracks = lift.Close(reduced);
        int seq = lift.Seq;
        Clip(true);
        PlayLevitated(tracks, PopupLevitate.Origin(false), () => { if (lift.Ended(seq)) done(); });
    }

    void PlayLevitated(Mantle.Core.Look.Levitate.Track[] tracks, (double X, double Y) origin, Action ended)
    {
        var v = Vis();
        LevitateMotion.Play(Root, tracks, ended, origin.X, origin.Y);
        // a window shown for the first time may not be laid out yet (ActualSize 0): the origin from the size it was given
        if (Root.Width > 0 && Root.Height > 0) v.CenterPoint = new Vector3((float)(Root.Width * origin.X), (float)(Root.Height * origin.Y), 0);
    }

    public void Show()
    {
        if (Parked) { WindowKinds.Cloak(Hwnd, false); Parked = false; }
        else if (!_activated) {
            // WinUI starts drawing a window's content at its first Activate; a no-focus kind gives the foreground
            // straight back to the app that had it (the bar's own first show does the same)
            _activated = true;
            IntPtr prev = Win32.GetForegroundWindow();
            Win.Activate();
            if (!Focusable && prev != IntPtr.Zero && prev != Hwnd && Win32.GetForegroundWindow() == Hwnd) Win32.SetForegroundWindow(prev);
        }
        else Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.StripFrame(Hwnd);   // the presenter puts frame bits back at a show
        Glass?.Up(Root.RequestedTheme == ElementTheme.Dark);   // lined up where it was placed, the tint of its theme
    }

    /// An instant kind stays made, cloaked in place; the next open only uncloaks it.
    public void Park()
    {
        if (!Parked) Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.Cloak(Hwnd, true);
        Glass?.Down();
        _lev?.Down();
        Parked = true;
    }

    public void Close() { Glass?.Release(); _lev?.Release(); try { Win.Close(); } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { } }
}
