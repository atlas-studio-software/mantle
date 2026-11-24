// LevitatePanel.cs - one levitated panel's layers (lane r10-newlook): its part of the frosted still, lined up with the
// screen, the uniform dim over it and the legibility film over that. Three Borders the surface puts under its own UI, all
// rounded with the panel's shape. No tint film, no white sheen, no hairline edge: the shadow is LevitateShadow's.
//
// Geometry (Mantle.Core Island): the blurred copy is laid out "cover" over the monitor (DIP) and the panel draws it at
// (ox - panel.left, oy - panel.top), size (cw, ch); a BIG panel (Island.IsBig on its layout size) shows the soft copy, a
// small one the light copy. The film is Legibility's v2 check over the copy this panel shows. Until the copies are ready
// (or when they fail) the panel is the flat #2c5258 with no dim and no film.
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
    bool _up, _dark, _released;
    int _seq;
    string? _said;
    double _vw, _vh, _x, _y, _w, _h;
    (double Dim, bool Auto)? _look;

    /// The panel's part of the frosted still (or the flat fallback), under everything the panel draws.
    public Border Picture { get; }
    /// The black wash at alpha dim over it.
    public Border Dim { get; }
    /// The neutral film rgba(10, 12, 14, a) over the dim (a = the v2 brightness check; 0 in manual).
    public Border Film { get; }
    /// "frost light" | "frost soft" while the copy is up, else "fallback: why" (tests and logs).
    public string State { get; private set; } = "not shown yet";
    /// The film alpha now (an exact 2-decimal v2 step: 0, 0.04 .. 0.56).
    public double FilmAlpha { get; private set; }

    public LevitatePanel(string name, CornerRadius radius)
    {
        _name = name;
        _brush.Transform = _tr;
        Picture = new Border { Background = _fallback, CornerRadius = radius, IsHitTestVisible = false };
        Dim = new Border { Background = _dim, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Film = new Border { Background = _film, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        LevitateStill.Changed += OnChanged;
    }

    static Windows.UI.Color Rgba(byte r, byte g, byte b, double a) =>
        new() { A = (byte)Math.Round(Math.Clamp(a, 0, 1) * 255), R = r, G = g, B = b };

    /// The panel's shape changed.
    public void Radius(CornerRadius r) { Picture.CornerRadius = r; Dim.CornerRadius = r; Film.CornerRadius = r; }

    /// Override the dim and the mode (default: the look's, LevitateDim over DesktopGlass.Look; see the lane note).
    public void Look(double dim, bool auto) { _look = (LevitateDim.Resolve(dim), auto); if (_up) Paint("the look was set"); }

    /// The panel is up at (x, y), w x h (its LAYOUT rect, DIP) on a vw x vh monitor (DIP), in this theme.
    public void Place(double vw, double vh, double x, double y, double w, double h, bool dark)
    {
        if (_released) return;
        bool moved = !_up || vw != _vw || vh != _vh || x != _x || y != _y || w != _w || h != _h || dark != _dark;
        (_vw, _vh, _x, _y, _w, _h, _dark) = (vw, vh, x, y, w, h, dark);
        _up = true;
        if (moved) Paint("placed");
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
    public void Down() => _up = false;

    /// The panel is closed for good.
    public void Release() { _released = true; _up = false; LevitateStill.Changed -= OnChanged; _brush.ImageSource = null; }

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
        double film = Legibility.ForPanel(p.LightPx, p.SoftPx, _vw, _vh, _x, _y, _w, _h, dim, auto);
        _dim.Color = Rgba(0, 0, 0, dim);
        _film.Color = Rgba(LevitateTokens.FilmR, LevitateTokens.FilmG, LevitateTokens.FilmB, film);
        Dim.Visibility = Visibility.Visible;
        Film.Visibility = film > 0 ? Visibility.Visible : Visibility.Collapsed;
        string state = "frost " + (big ? "soft" : "light");
        bool said = State == state && Math.Abs(FilmAlpha - film) < 1e-9;
        State = state;
        FilmAlpha = film;
        _said = null;
        if (!said) Host.Log.Line(FormattableString.Invariant(
            $"levitate {_name}: {state} ({why}): {_w:0}x{_h:0} DIP at ({_x:0}, {_y:0}) on {_vw:0}x{_vh:0}; copy at ({dx:0.#}, {dy:0.#}) size {cw:0.#}x{ch:0.#}; dim {dim:0.00}, {(auto ? "automatic" : "manual")}, film {Legibility.Show(film):0.00}"));
    }

    /// The dim and the mode from the look DesktopGlass follows: its Wash answer's mode (dimAuto) and Dim, clamped to the
    /// contract range. Wash.Alpha already put a missing or out-of-range Dim at its seed (.30), so the levitated default .20 is
    /// not reached through this road (lane note: the raw glass.&lt;theme&gt; is not exposed by the look host yet).
    static (double Dim, bool Auto) FromGlass(bool dark)
    {
        var w = DesktopGlass.Look?.For(dark);
        return w == null ? (LevitateTokens.DimDefault, true) : (LevitateDim.Resolve(w.Dim), w.Mode != "manual");
    }

    void Fallback(string why)
    {
        _brush.ImageSource = null;
        Picture.Background = _fallback;
        Dim.Visibility = Film.Visibility = Visibility.Collapsed;
        FilmAlpha = 0;
        State = "fallback: " + why;
        if (why != _said) { _said = why; Host.Log.Line($"levitate {_name}: the flat fallback colour: {why}"); }
    }
}
