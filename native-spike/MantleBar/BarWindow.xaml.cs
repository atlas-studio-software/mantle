using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MantleBar.Native;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace MantleBar;

/// One dock cell (ui/bar/bar.js .cell): an app's icon or letter tile, and its running line.
public sealed class CellVm : INotifyPropertyChanged
{
    internal AppEntry App;
    internal CellVm(AppEntry app, ImageSource? icon)
    {
        App = app; Name = app.Name; Icon = icon;
        Letter = app.Name.Length > 0 ? app.Name.Substring(0, 1).ToUpperInvariant() : "?";
        _active = app.Active;
    }
    public string Name { get; }
    public string Letter { get; }
    public ImageSource? Icon { get; }
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TileVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    bool _active;
    public bool Active {
        get => _active;
        set { if (_active == value) return; _active = value; Changed(nameof(RunVisibility)); Changed(nameof(ActVisibility)); }
    }
    public Visibility RunVisibility => _active ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ActVisibility => _active ? Visibility.Visible : Visibility.Collapsed;
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed partial class BarWindow : Window
{
    // bar.css / main.js geometry, DIP
    const double BarH = 48, Lift = 14, SideGapTotal = 28, MaxW = 1120;
    const double ShadowTop = 56, ShadowSide = 28;   // room in the window for the light drop shadow

    readonly BarOptions _o;
    readonly IntPtr _hwnd;
    readonly double _scale;
    readonly Win32.RECT _monitor;
    readonly double _pillW;
    readonly RectInt32 _winPx;
    readonly Win32.RECT _pillPx;
    readonly bool _dark;
    readonly ObservableCollection<CellVm> _cells = new();
    readonly WindowList _list;
    readonly DispatcherQueueTimer _clock;
    long _firstFrameMs;
    string _backdropState = "not captured";

    internal BarWindow(BarOptions o)
    {
        _o = o;
        InitializeComponent();
        _dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // the primary monitor and its scale, before anything is placed
        IntPtr mon = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, 1 /* PRIMARY */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        _monitor = mi.rcMonitor;
        _scale = Win32.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        double monWdip = (_monitor.Right - _monitor.Left) / _scale;
        _pillW = Math.Min(MaxW, Math.Max(120, monWdip - SideGapTotal));

        int wPx = (int)Math.Round((_pillW + 2 * ShadowSide) * _scale), hPx = (int)Math.Round((ShadowTop + BarH + Lift) * _scale);
        int xPx = _monitor.Left + ((_monitor.Right - _monitor.Left) - wPx) / 2, yPx = _monitor.Bottom - hPx;
        _winPx = new RectInt32(xPx, yPx, wPx, hPx);
        _pillPx = new Win32.RECT {
            Left = xPx + (int)Math.Round(ShadowSide * _scale), Top = yPx + (int)Math.Round(ShadowTop * _scale),
        };
        _pillPx.Right = _pillPx.Left + (int)Math.Round(_pillW * _scale);
        _pillPx.Bottom = _pillPx.Top + (int)Math.Round(BarH * _scale);

        Root.Width = _pillW + 2 * ShadowSide;
        Root.Height = ShadowTop + BarH + Lift;
        Pill.Width = _pillW;
        Pill.Margin = new Thickness(ShadowSide, ShadowTop, 0, 0);

        SystemBackdrop = new TransparentBackdrop();
        ConfigureWindow();
        PaintBackdrop();
        if (!_dark) AddShadow();   // dark: the lip only, no drop shadow (bar.css, dark .bar --pane-drop)
        BuildGlyphs();

        Dock.ItemsSource = _cells;
        _list = new WindowList(DispatcherQueue);
        _list.Changed += SyncCells;

        _clock = DispatcherQueue.CreateTimer();
        _clock.IsRepeating = false;
        _clock.Tick += (_, _) => Tick();

        CompositionTarget.Rendering += OnFirstRender;
        Closed += (_, _) => _list.Dispose();
    }

    void ConfigureWindow()
    {
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        AppWindow.MoveAndResize(_winPx);
    }

    internal void ShowBar()
    {
        Activate();
        // after the first show: a click on the bar must never take the focus from the app in front
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        _list.Start();
        Tick();
    }

    // ------------------------------------------------------------------ the glass
    void PaintBackdrop()
    {
        WriteableBitmap? pic = null;
        if (!_o.NoBackdrop) {
            try {
                pic = Backdrop.Capture(_pillPx, _monitor, (_dark ? 5 : 12) * _scale, _dark ? 1.0 : 1.5);
                _backdropState = pic != null ? "captured" : "the screen could not be read";
            } catch (Exception e) { _backdropState = "capture failed: " + e.Message; }
        } else _backdropState = "off (--no-backdrop)";
        if (pic != null) GlassImage.ImageSource = pic;
        else {
            Glass.Background = null;
            Tint.Background = (Brush)Application.Current.Resources["LgPaneFillFallback"];
        }
        _o.Log("backdrop: " + _backdropState);
    }

    // The light island's drop shadow, from glass-light.css -lg-drop-up:
    //   0 -1px 1px rgba(14,18,38,.05), 0 -10px 28px -12px rgba(14,18,38,.22), 0 -26px 56px -28px rgba(14,18,38,.30)
    // A negative spread shrinks the shape before the blur; the third layer's -28 leaves a 48 tall
    // pill no shape at all, so it draws nothing, in CSS as here.
    void AddShadow()
    {
        var c = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
        var host = c.CreateContainerVisual();
        void Layer(double dy, double blur, double spread, byte alpha)
        {
            float w = (float)(_pillW + 2 * spread), h = (float)(BarH + 2 * spread);
            if (w <= 0 || h <= 0) return;
            float r = (float)Math.Max(0, 24 + spread);
            var geo = c.CreateRoundedRectangleGeometry(); geo.Size = new Vector2(w, h); geo.CornerRadius = new Vector2(r, r);
            var shape = c.CreateSpriteShape(geo); shape.FillBrush = c.CreateColorBrush(Colors.Black);
            var sv = c.CreateShapeVisual(); sv.Size = new Vector2(w, h); sv.Shapes.Add(shape);
            var surf = c.CreateVisualSurface(); surf.SourceVisual = sv; surf.SourceSize = new Vector2(w, h);
            var sprite = c.CreateSpriteVisual();
            sprite.Size = new Vector2(w, h);
            sprite.Offset = new Vector3((float)(ShadowSide - spread), (float)(ShadowTop - spread), 0);
            var ds = c.CreateDropShadow();
            ds.Mask = c.CreateSurfaceBrush(surf);
            ds.BlurRadius = (float)blur;
            ds.Offset = new Vector3(0, (float)dy, 0);
            ds.Color = Windows.UI.Color.FromArgb(alpha, 14, 18, 38);
            sprite.Shadow = ds;
            host.Children.InsertAtTop(sprite);
        }
        Layer(-1, 1, 0, 13);
        Layer(-10, 28, -12, 56);
        Layer(-26, 56, -28, 77);
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, host);
    }

    // ------------------------------------------------------------------ glyphs (the SVGs in ui/bar)
    static readonly Regex Tok = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);
    static string Norm(string d)
    {
        var sb = new StringBuilder();
        foreach (Match m in Tok.Matches(d)) sb.Append(m.Value).Append(' ');
        return sb.ToString().Trim();
    }
    static string Circle(double cx, double cy, double r)
        => FormattableString.Invariant($"M{cx - r} {cy} a{r} {r} 0 1 0 {2 * r} 0 a{r} {r} 0 1 0 {-2 * r} 0 Z");
    static string RoundRect(double x, double y, double w, double h, double r)
        => FormattableString.Invariant($"M{x + r} {y} h{w - 2 * r} a{r} {r} 0 0 1 {r} {r} v{h - 2 * r} a{r} {r} 0 0 1 {-r} {r} h{-(w - 2 * r)} a{r} {r} 0 0 1 {-r} {-r} v{-(h - 2 * r)} a{r} {r} 0 0 1 {r} {-r} Z");

    static UIElement PathEl(string d, bool fill, double stroke = 1.25, string brush = "LgInk2")
    {
        string paint = fill
            ? $"Fill='{{ThemeResource {brush}}}'"
            : FormattableString.Invariant($"Stroke='{{ThemeResource {brush}}}' StrokeThickness='{stroke}' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'");
        string xaml = "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + Norm(d) + "' " + paint + "/>";
        return (UIElement)XamlReader.Load(xaml);
    }

    void BuildGlyphs()
    {
        // the Mantle mark (index.html #start): one even-odd path, the M cut out of a rounded square
        MarkHost.Children.Add(PathEl("M7.2 3h9.6A4.2 4.2 0 0 1 21 7.2v9.6a4.2 4.2 0 0 1-4.2 4.2H7.2A4.2 4.2 0 0 1 3 16.8V7.2A4.2 4.2 0 0 1 7.2 3Zm1.05 4.6a.6.6 0 0 0-.6.6v7.6a.6.6 0 0 0 .6.6h1.3a.6.6 0 0 0 .6-.6v-3.63l1.33 2.06a.6.6 0 0 0 1.01 0l1.33-2.06v3.63a.6.6 0 0 0 .6.6h1.3a.6.6 0 0 0 .6-.6V8.2a.6.6 0 0 0-.6-.6h-1.2a.6.6 0 0 0-.51.29L12 11.3 9.96 7.89a.6.6 0 0 0-.51-.29h-1.2Z", true));
        DownloadsHost.Children.Add(PathEl("M8 2.3v7.6M5 7.1l3 2.9 3-2.9", false));
        DownloadsHost.Children.Add(PathEl("M2.6 10.1v1.9c0 .8.6 1.4 1.4 1.4h8c.8 0 1.4-.6 1.4-1.4v-1.9", false));
        // volume (bar.js volumeSvg): the speaker, plus 0-3 waves by level. The level is not read in
        // the spike (CoreAudio is migration work): no audio device -> no waves, as the real bar
        // draws a null level; otherwise all three.
        VolumeHost.Children.Add(PathEl("M2.2 6.1h2.4L8.1 3.1v9.8L4.6 9.9H2.2z", true));
        if (Win32.waveOutGetNumDevs() > 0)
            foreach (var arc in new[] { "M10 6.2a2.6 2.6 0 0 1 0 3.6", "M11.8 4.9a5 5 0 0 1 0 6.2", "M13.6 3.6a7.4 7.4 0 0 1 0 8.8" })
                VolumeHost.Children.Add(PathEl(arc, false, 1.2));
        // network (net.js): the wired glyph. Wi-Fi bars are migration work.
        NetworkHost.Children.Add(PathEl(RoundRect(2, 2.6, 12, 8.4, 1.3), false, 1.2));
        NetworkHost.Children.Add(PathEl("M6 13.6h4M8 11v2.6", false, 1.2));
        NotifsHost.Children.Add(PathEl("M8 2.2a3.9 3.9 0 0 0-3.9 3.9v2.6L2.9 11h10.2l-1.2-2.3V6.1A3.9 3.9 0 0 0 8 2.2Z", false));
        NotifsHost.Children.Add(PathEl("M6.4 12.9a1.7 1.7 0 0 0 3.2 0", false));
        foreach (var cx in new[] { 3.5, 8, 12.5 }) MoreHost.Children.Add(PathEl(Circle(cx, 8, 1.3), true));
    }

    // ------------------------------------------------------------------ the clock (bar.js tick)
    void Tick()
    {
        var now = DateTime.Now;
        int h = now.Hour % 12; if (h == 0) h = 12;
        string t = h.ToString(CultureInfo.InvariantCulture) + ":" + now.ToString("mm:ss", CultureInfo.InvariantCulture) + (now.Hour < 12 ? " AM" : " PM");
        if (ClockT.Text != t) ClockT.Text = t;
        string d = now.ToString("d", CultureInfo.CurrentCulture);
        if (ClockD.Text != d) ClockD.Text = d;
        // one timer, re-aimed at the next second boundary (+20 ms: never paint the previous second)
        _clock.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 20);
        _clock.Start();
    }

    // ------------------------------------------------------------------ the dock
    void SyncCells()
    {
        var apps = _list.Apps;
        var want = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in apps) want[a.Key] = a;
        for (int i = _cells.Count - 1; i >= 0; i--) if (!want.ContainsKey(_cells[i].App.Key)) _cells.RemoveAt(i);
        var have = new Dictionary<string, CellVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _cells) have[c.App.Key] = c;
        int iconPx = (int)Math.Round(24 * _scale);
        foreach (var a in apps) {
            if (have.TryGetValue(a.Key, out var vm)) { vm.App = a; vm.Active = a.Active; }
            else _cells.Add(new CellVm(a, Icons.ForApp(a, iconPx)));
        }
        if (_o.BenchOut != null) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, WriteCells);
    }

    void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not CellVm vm) return;
        string r = Focus.Toggle(vm.App);
        _o.Log("click " + vm.Name + ": " + r);
        _list.Rebuild(false);
    }

    // ------------------------------------------------------------------ bench output
    void OnFirstRender(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstRender;
        _firstFrameMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _o.Log("first frame " + (_firstFrameMs - _o.LaunchedUnixMs) + " ms after Main");
        if (_o.BenchOut == null) return;
        var startedAt = new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        Write("ready.json", new {
            processStartUnixMs = startedAt, mainUnixMs = _o.LaunchedUnixMs, firstFrameUnixMs = _firstFrameMs,
            scale = _scale, theme = _dark ? "dark" : "light", backdrop = _backdropState,
            window = new { x = _winPx.X, y = _winPx.Y, w = _winPx.Width, h = _winPx.Height },
            pill = new { x = _pillPx.Left, y = _pillPx.Top, w = _pillPx.Right - _pillPx.Left, h = _pillPx.Bottom - _pillPx.Top },
        });
    }

    void WriteCells()
    {
        var list = new List<object>();
        for (int i = 0; i < _cells.Count; i++) {
            if (Dock.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            var p = fe.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            int x = _winPx.X + (int)Math.Round(p.X * _scale), y = _winPx.Y + (int)Math.Round(p.Y * _scale);
            list.Add(new { name = _cells[i].Name, exe = _cells[i].App.Exe, pid = _cells[i].App.Pid, active = _cells[i].Active,
                           hasIcon = _cells[i].Icon != null, x, y, w = (int)Math.Round(fe.ActualWidth * _scale), h = (int)Math.Round(fe.ActualHeight * _scale) });
        }
        Write("cells.json", new { at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rebuilds = _list.Rebuilds, cells = list });
    }

    void Write(string name, object o)
    {
        try { File.WriteAllText(Path.Combine(_o.BenchOut!, name), JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { _o.Log("write " + name + " failed: " + e.Message); }
    }
}
