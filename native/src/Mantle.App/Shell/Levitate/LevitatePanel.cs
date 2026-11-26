// LevitatePanel.cs - one levitated panel's layers (lane r10-newlook): its part of the frosted still, lined up with the
// screen, the uniform dim over it, the legibility film over that and the hover sheen on top. Four Borders the surface puts
// under its own UI, all rounded with the panel's shape. No tint film, no frost colour, no white sheen AT REST (the sheen
// shows on hover only), no hairline, ring or top highlight: the shadow is LevitateShadow's.
//
// Never at rest: a Place that moved or resized the panel queues ONE recompute for the next frame (CompositionTarget.
// Rendering, hooked only while one is pending), so a panel being dragged or animated is checked at most once per frame.
//
// Geometry (Mantle.Core Island): the blurred copy is laid out "cover" over the monitor (DIP) and the panel draws it at
// (ox - panel.left, oy - panel.top), size (cw, ch); a BIG panel (Island.IsBig on its layout size) shows the soft copy, a
// small one the light copy. The film is Legibility's v2.1 check over the copy this panel shows: the dark film in dark, the
// light lift toward #F4F7F8 in light (lane r14-bar-review, apps-r3/LIGHT.md section 4b). Until the copies are ready (or when
// they fail) the panel is the flat #2c5258: in dark with no dim and no film, in light with the dim and the lift over it.
using System;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.App.Shell.Glass;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Shell.Levitate;

internal sealed class LevitatePanel
{
    readonly string _name;
    readonly ImageBrush _brush = new() { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
    readonly CompositeTransform _tr = new();
    readonly SolidColorBrush _fallback = new(Rgba(LevitateTokens.FallbackR, LevitateTokens.FallbackG, LevitateTokens.FallbackB, 1));
    readonly SolidColorBrush _dim = new();
    readonly SolidColorBrush _film = new();
    readonly SolidColorBrush _sheen = new();
    bool _up, _dark, _released, _queued;
    string _why = "placed";
    int _seq;
    string? _said;
    bool? _saidDark;
    double _vw, _vh, _x, _y, _w, _h;
    (double Dim, bool Auto)? _look;

    /// The panel's part of the frosted still (or the flat fallback), under everything the panel draws.
    public Border Picture { get; }
    /// The black wash at alpha dim over it.
    public Border Dim { get; }
    /// The neutral film over the dim: rgba(10, 12, 14, a) in dark, the light lift rgba(244, 247, 248, a) in light (a = the
    /// v2.1 brightness check of the theme in effect; 0 in manual).
    public Border Film { get; }
    /// The white sheen on HOVER only (none at rest): .07, .05 on a big panel (its layout size); over the film, under the UI.
    public Border Sheen { get; }
    /// "frost light" | "frost soft" while the copy is up, else "fallback: why" (tests and logs).
    public string State { get; private set; } = "not shown yet";
    /// The film (dark) or lift (light) alpha now (an exact 2-decimal v2.1 step: 0, 0.04 .. 0.72).
    public double FilmAlpha { get; private set; }

    public LevitatePanel(string name, CornerRadius radius)
    {
        _name = name;
        _brush.Transform = _tr;
        Picture = new Border { Background = _fallback, CornerRadius = radius, IsHitTestVisible = false };
        Dim = new Border { Background = _dim, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Film = new Border { Background = _film, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Sheen = new Border { Background = _sheen, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        LevitateStill.Changed += OnChanged;
    }

    static Windows.UI.Color Rgba(byte r, byte g, byte b, double a) =>
        new() { A = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255), R = r, G = g, B = b };

    /// The panel's shape changed.
    public void Radius(CornerRadius r) { Picture.CornerRadius = r; Dim.CornerRadius = r; Film.CornerRadius = r; Sheen.CornerRadius = r; }

    /// Override the dim and the mode (default: the look's, LevitateDim over DesktopGlass.Look; see the lane note).
    public void Look(double dim, bool auto) { _look = (LevitateDim.Resolve(dim), auto); if (_up) Paint("the look was set"); }

    /// The panel is up at (x, y), w x h (its LAYOUT rect, DIP) on a vw x vh monitor (DIP), in this theme.
    public void Place(double vw, double vh, double x, double y, double w, double h, bool dark)
    {
        if (_released) return;
        bool moved = !_up || vw != _vw || vh != _vh || x != _x || y != _y || w != _w || h != _h || dark != _dark;
        (_vw, _vh, _x, _y, _w, _h, _dark) = (vw, vh, x, y, w, h, dark);
        _up = true;
        if (moved) Queue("placed");
    }

    /// One recompute on the next frame, however many Place calls come before it.
    void Queue(string why)
    {
        _why = why;
        if (_queued) return;
        _queued = true;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFrame;
        _queued = false;
        if (_up && !_released) Paint(_why);
    }

    /// The pointer came over the island (or left it): the hover sheen on or off. No pixel work.
    public void Hover(bool on)
    {
        _sheen.Color = Rgba(255, 255, 255, Island.IsBig(_w, _h) ? LevitateTokens.SheenHoverBig : LevitateTokens.SheenHover);
        Sheen.Visibility = on && _up && !_released ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <see cref="Place(double, double, double, double, double, double, bool)"/> for an element of a window: the monitor the
    /// window is on, the element's layout position on it (GlassLayer.At: its ActualOffset up the tree, not a render transform)
    /// and its layout size.
    public bool Place(IntPtr hwnd, FrameworkElement el, bool dark)
    {
        double scale = Math.Max(1, Win32.GetDpiForWindow(hwnd)) / 96.0;
        IntPtr m = FrameNative.MonitorFromWindow(hwnd, 2 /* NEAREST */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (m == IntPtr.Zero || !Win32.GetMonitorInfoW(m, ref mi)) { Fallback("the window's monitor could not be read"); return false; }
        var mon = mi.rcMonitor;
        var (sx, sy) = GlassLayer.At(hwnd, () => el)(scale);
        Place((mon.Right - mon.Left) / scale, (mon.Bottom - mon.Top) / scale, (sx - mon.Left) / scale, (sy - mon.Top) / scale, el.ActualWidth, el.ActualHeight, dark);
        return true;
    }

    /// The theme in effect changed (the dim and the film follow it; the copy is the same in both themes).
    public void Theme(bool dark) { if (dark == _dark) return; _dark = dark; if (_up) Paint("the theme changed"); }

    /// The panel went down: nothing more is done until the next Place.
    public void Down() { _up = false; Sheen.Visibility = Visibility.Collapsed; }

    /// The panel is closed for good.
    public void Release()
    {
        _released = true;
        _up = false;
        LevitateStill.Changed -= OnChanged;
        if (_queued) { CompositionTarget.Rendering -= OnFrame; _queued = false; }
        _brush.ImageSource = null;
        Sheen.Visibility = Visibility.Collapsed;
    }

    void OnChanged(string why) { if (_up) Paint(why); }   // a hidden panel takes the change at its next Place

    void Paint(string why)
    {
        var t = LevitateStill.Get(out var none);
        if (none != null) { Fallback(none); return; }
        int seq = ++_seq;
        if (t.IsCompleted) Take(t.Result, seq, why);
        else _ = Later(t, seq, why);
    }

    async Task Later(Task<FrostPair?> t, int seq, string why)
    {
        FrostPair? p;
        try { p = await t; } catch (Exception e) { p = null; Host.Log.Line($"levitate {_name}: the frost failed ({e.Message})"); }
        Take(p, seq, why);
    }

    void Take(FrostPair? p, int seq, string why)
    {
        if (seq != _seq || !_up || _released) return;
        if (p == null) { Fallback(LevitateStill.Failure ?? "the frosted still could not be made"); return; }
        bool big = Island.IsBig(_w, _h);
        var px = p.PxFor(big);
        var fit = Island.Cover(px.W, px.H, _vw, _vh);
        var (dx, dy, cw, ch) = Island.Draw(fit, _x, _y);
        _tr.ScaleX = cw / px.W;
        _tr.ScaleY = ch / px.H;
        _tr.TranslateX = dx;
        _tr.TranslateY = dy;
        var bmp = p.For(big);
        if (!ReferenceEquals(_brush.ImageSource, bmp)) _brush.ImageSource = bmp;
        Picture.Background = _brush;
        var (dim, auto) = _look ?? FromGlass(_dark);
        // Dark: the v2.1 dark film rgb(10, 12, 14) (LOOK.md section 4). Light (lane r14-bar-review, apps-r3/LIGHT.md section
        // 4b): the light lift toward #F4F7F8 over the SAME copy this panel shows (its 8 x 5 samples after the dim, the darkest
        // tenth, the smallest step to L >= 0.386, cap .72). Only one of the two runs; the check off (dimAuto false) = none.
        double film = _dark
            ? Legibility.ForPanel(p.LightPx, p.SoftPx, _vw, _vh, _x, _y, _w, _h, dim, auto)
            : Legibility.Film(auto, Legibility.Samples(px, Island.Cover(px.W, px.H, _vw, _vh), _x, _y, _w, _h), dim, "light");
        Layers(dim, film);
        string state = "frost " + (big ? "soft" : "light");
        bool said = State == state && Math.Abs(FilmAlpha - film) < 1e-9 && _saidDark == _dark;
        State = state;
        FilmAlpha = film;
        _said = null;
        _saidDark = _dark;
        if (!said) Host.Log.Line(FormattableString.Invariant(
            $"levitate {_name}: {state} ({why}): {_w:0}x{_h:0} DIP at ({_x:0}, {_y:0}) on {_vw:0}x{_vh:0}; copy at ({dx:0.#}, {dy:0.#}) size {cw:0.#}x{ch:0.#}; dim {dim:0.00}, {(auto ? "automatic" : "manual")}, {FilmWords(film)}"));
    }

    /// The dim and this theme's film over the picture (or the flat fallback): the black wash at <paramref name="dim"/>, then
    /// rgb(10, 12, 14) in dark or #F4F7F8 in light at <paramref name="film"/> (Legibility.FilmRgb).
    void Layers(double dim, double film)
    {
        var rgb = Legibility.FilmRgb(_dark ? "dark" : "light");
        _dim.Color = Rgba(0, 0, 0, dim);
        _film.Color = Rgba(rgb.R, rgb.G, rgb.B, film);
        Dim.Visibility = Visibility.Visible;
        Film.Visibility = film > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    string FilmWords(double film) => FormattableString.Invariant($"{(_dark ? "film" : "light lift")} {Legibility.Show(film):0.00}");

    /// The dim and the mode from the look DesktopGlass follows: the look host's saved <c>glass.&lt;theme&gt;.dim</c> and
    /// <c>dimAuto</c>, read by <see cref="LevitateDim.Read"/> (look-sync v2.1: seed 0.20; a stored value in 0.20..0.90 wins,
    /// 0.30 included; missing, not a number or out of range = 0.20). No look read yet: the seed, automatic (lane r12-app-wiring;
    /// before it this went through Wash's answer, not the saved value).
    static (double Dim, bool Auto) FromGlass(bool dark) => LevitateDim.Read(DesktopGlass.Look?.Values, dark ? "dark" : "light");

    /// The flat #2c5258 until the copies are ready (or when they fail). Dark: no dim and no film, as before. Light (lane
    /// r14-bar-review, apps-r3/LIGHT.md 4.2 "the light check reads that colour too"): the dim and the light lift over the
    /// flat colour, so dark ink never sits on unlifted dark teal (at the seed dim 0.20 the v2.1 lift is 0.60; LIGHT.md's
    /// ".40" is the v2.0 target 0.226's answer).
    void Fallback(string why)
    {
        _brush.ImageSource = null;
        Picture.Background = _fallback;
        double film = 0;
        if (_dark) Dim.Visibility = Film.Visibility = Visibility.Collapsed;
        else {
            var (dim, auto) = _look ?? FromGlass(false);
            film = Legibility.Film(auto, FlatSamples, dim, "light");
            Layers(dim, film);
        }
        FilmAlpha = film;
        State = "fallback: " + why;
        string said = why + (_dark ? "" : FormattableString.Invariant($" (in light: the {FilmWords(film)} over it)"));
        if (said != _said) { _said = said; Host.Log.Line($"levitate {_name}: the flat fallback colour: {said}"); }
    }

    /// The flat fallback as the check's 40 samples (the island shows that colour everywhere).
    static readonly Rgb[] FlatSamples = Flat();
    static Rgb[] Flat()
    {
        var s = new Rgb[Legibility.Cols * Legibility.Rows];
        Array.Fill(s, new Rgb(LevitateTokens.FallbackR, LevitateTokens.FallbackG, LevitateTokens.FallbackB));
        return s;
    }
}
