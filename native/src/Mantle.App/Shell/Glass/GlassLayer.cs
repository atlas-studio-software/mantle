// GlassLayer.cs - one window's half of the shared desktop-still backdrop (DesktopGlass.cs): two layers the window puts
// under its own UI, the picture and the tint over it, both rounded with the window's shape.
//
// The picture is an ImageBrush over the shared bitmap (the still cover-fit to the monitor, one bitmap px per physical
// px): scaled by 1/DPI-scale and translated by (monitor origin - the layer's screen origin), so each screen pixel shows
// the still's pixel at that screen point. A move changes only that translation.
using System;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core.Look;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Shell.Glass;

internal sealed class GlassLayer
{
    readonly string _name;
    readonly IntPtr _hwnd;
    readonly Func<double, (double X, double Y)> _origin;   // scale -> the layers' top-left on screen, physical px
    readonly Action<bool> _plain;                           // true: the window shows its own plain fill (no picture)
    readonly ImageBrush _brush = new() { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
    readonly CompositeTransform _tr = new();
    readonly SolidColorBrush _tint = new();
    bool _up, _dark, _released;
    int _seq;
    string? _said;
    WashResult? _wash;
    GlassPic? _pic;
    Win32.RECT _mon;
    double _scale = 1;

    /// The still, under everything the window draws.
    public Border Picture { get; }
    /// The wash over it (Wash.Alpha's tint for the theme in effect).
    public Border Tint { get; }
    /// "picture" while the still is up, else "plain: why" (tests and logs).
    public string State { get; private set; } = "not shown yet";

    public GlassLayer(string name, IntPtr hwnd, CornerRadius radius, Func<double, (double X, double Y)> origin, Action<bool> plain)
    {
        _name = name; _hwnd = hwnd; _origin = origin; _plain = plain;
        _brush.Transform = _tr;
        Picture = new Border { Background = _brush, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Tint = new Border { Background = _tint, CornerRadius = radius, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        DesktopGlass.Changed += OnChanged;
        // the band the guard reads is the window's own rows: a new height may change the tint (a max over a few numbers)
        Picture.SizeChanged += (_, e) => { if (_up && _pic != null) Moved("resized"); };
    }

    /// An origin for layers placed anywhere in a frameless window: the window's top-left on screen plus where the layers sit
    /// in it (their own offset, so a pane centred, inset or resized by layout is followed).
    public static Func<double, (double X, double Y)> At(IntPtr hwnd, Func<FrameworkElement> el) => s => {
        if (!Mantle.App.Surfaces.Bar.Frame.FrameNative.GetWindowRect(hwnd, out var r)) return (0.0, 0.0);
        // the layout position (each element's ActualOffset up the tree), not a render transform or a composition
        // translation: a pane sliding or rising in is lined up where it comes to rest
        double x = 0, y = 0;
        for (DependencyObject? d = el(); d is UIElement u; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d)) { var o = u.ActualOffset; x += o.X; y += o.Y; }
        return (r.Left + x * s, r.Top + y * s);
    };

    /// The window's shape changed (the OSD's pill opening into its panel): both layers follow it.
    public void Radius(CornerRadius r) { Picture.CornerRadius = r; Tint.CornerRadius = r; }

    /// The window is going up (placed already): the picture for its monitor, lined up, in this theme.
    public void Up(bool dark) { if (_released) return; _dark = dark; _up = true; Refresh("shown"); }

    /// The window went down (hidden, parked or closing): nothing more is done until the next Up.
    public void Down() => _up = false;

    /// The theme in effect changed (the tint follows; the picture is the same in both themes).
    public void Theme(bool dark) { _dark = dark; if (_up && _pic != null) Tinted(); }

    /// The window moved (or was placed again): only the picture's translation changes.
    public void Moved(string why = "moved")
    {
        if (!_up || _pic == null) return;
        if (!Monitor(out var mon, out double scale)) { Plain("the window's monitor could not be read"); return; }
        if (mon.Right - mon.Left != _pic.W || mon.Bottom - mon.Top != _pic.H || scale != _scale) { Refresh("it moved to another monitor size"); return; }
        _mon = mon;
        var (dx, dy) = Offset();
        var old = _wash;
        var w = Tinted();
        string tint = old == null || Math.Abs(old.Alpha - w.Alpha) > 1e-9 ? $"; tint {(_dark ? "dark" : "light")} {w.Alpha:0.00} ({w.Text})" : "";
        Host.Log.Line($"glass {_name}: {why}: the picture's offset is now ({dx:0.##}, {dy:0.##}) DIP (no decode, no pixel work){tint}");
    }


    /// The window is closed for good.
    public void Release() { _released = true; _up = false; DesktopGlass.Changed -= OnChanged; _brush.ImageSource = null; }

    void OnChanged(string why) { if (_up) Refresh(why); else _pic = null; }   // a hidden window takes the change at its next Up

    bool Monitor(out Win32.RECT mon, out double scale)
    {
        mon = default; scale = Math.Max(1, Win32.GetDpiForWindow(_hwnd)) / 96.0;
        IntPtr m = FrameNative.MonitorFromWindow(_hwnd, 2 /* NEAREST */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (m == IntPtr.Zero || !Win32.GetMonitorInfoW(m, ref mi)) return false;
        mon = mi.rcMonitor;
        return true;
    }

    void Refresh(string why)
    {
        if (!Monitor(out var mon, out double scale)) { Plain("the window's monitor could not be read"); return; }
        var t = DesktopGlass.Pic(mon.Right - mon.Left, mon.Bottom - mon.Top, scale, out var none);
        if (none != null) { Plain(none); return; }
        int seq = ++_seq;
        if (t.IsCompleted) Take(t.Result, mon, scale, seq, why);
        else _ = Later(t, mon, scale, seq, why);
    }

    async Task Later(Task<GlassPic?> t, Win32.RECT mon, double scale, int seq, string why)
    {
        GlassPic? p;
        try { p = await t; } catch (Exception e) { p = null; Host.Log.Line($"glass {_name}: the picture failed ({e.Message})"); }
        Take(p, mon, scale, seq, why);
    }

    void Take(GlassPic? p, Win32.RECT mon, double scale, int seq, string why)
    {
        if (seq != _seq || !_up) return;
        if (p == null) { Plain(DesktopGlass.FailureFor(mon.Right - mon.Left, mon.Bottom - mon.Top, scale) ?? "the desktop still could not be decoded"); return; }
        _pic = p; _mon = mon; _scale = scale;
        if (!ReferenceEquals(_brush.ImageSource, p.Bitmap)) _brush.ImageSource = p.Bitmap;
        Picture.Visibility = Tint.Visibility = Visibility.Visible;
        _plain(false);
        var (dx, dy) = Offset();
        var w = Tinted();
        State = "picture";
        _said = null;
        Host.Log.Line($"glass {_name}: painted the desktop still ({why}): {p.Words}; monitor {FrameNative.Rect(mon)} at scale {scale:0.##}; offset ({dx:0.##}, {dy:0.##}) DIP; " +
            $"tint {(_dark ? "dark" : "light")} {w.Alpha:0.00} ({w.Text})");
    }

    (double, double) Offset()
    {
        var (ox, oy) = _origin(_scale);
        _tr.ScaleX = _tr.ScaleY = 1 / _scale;
        _tr.TranslateX = (_mon.Left - ox) / _scale;
        _tr.TranslateY = (_mon.Top - oy) / _scale;
        return (_tr.TranslateX, _tr.TranslateY);
    }

    /// The tint for the theme in effect: look-sync washAlpha's mode and Dim, and in Automatic the native guard's worst strip
    /// over this window's rows (its top on its monitor, its height, at least 140 DIP; NativeGuard.ForWindow).
    WashResult Tinted()
    {
        var look = DesktopGlass.Look;
        var shared = look?.For(_dark) ?? Wash.Alpha(null, null, _dark ? "dark" : "light", "the look has not been read yet");
        var w = shared;
        if (_pic != null) {
            var (_, oy) = _origin(_scale);
            double h = Picture.ActualHeight;
            if (!(h > 0) && Mantle.App.Surfaces.Bar.Frame.FrameNative.GetWindowRect(_hwnd, out var wr)) h = (wr.Bottom - wr.Top) / _scale;
            w = NativeGuard.ForWindow(shared, _pic.Guard(_dark), (oy - _mon.Top) / _scale, h);
        }
        _wash = w;
        int[] rgb = _dark ? LookHost.DarkBar : LookHost.LightFill;   // the wash colours (CONTRACT desktop.guard: rgb(9 10 11); light: white)
        _tint.Color = new Windows.UI.Color { A = (byte)Math.Round(Math.Clamp(w.Alpha, 0, 1) * 255), R = (byte)rgb[0], G = (byte)rgb[1], B = (byte)rgb[2] };
        return w;
    }

    void Plain(string why)
    {
        _pic = null;
        Picture.Visibility = Tint.Visibility = Visibility.Collapsed;
        _plain(true);
        State = "plain: " + why;
        if (why != _said) { _said = why; Host.Log.Line($"glass {_name}: no picture, a plain fill: {why}"); }
    }
}
