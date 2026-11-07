// Glass: the island's glass alone (no icons, no clock), in the bar's real place, three ways:
//   --mode=cpu      Electron's own method, ported (CpuGlass.cs): a picture of the screen behind the pill,
//                   blurred on the CPU. Light: refreshed 4x a second (the window excluded from capture for
//                   ~30 ms each time, as Electron does). Dark: one still per reveal, cut again when the
//                   wallpaper changes.
//   --mode=gpu      DWM's host backdrop (live), blurred and saturated by the system compositor, masked to
//                   the pill (Backdrops.cs HostBlurBackdrop). Nothing to capture, nothing on a timer.
//   --mode=acrylic  WinUI's DesktopAcrylicController told the window is always active, its own tint and
//                   luminosity at 0 (the island's tint is drawn on top), the window cut to the pill.
// On top, in every mode, the island's layers from ui/shared/glass-light.css + ui/bar/bar.css: the tint
// (border-box), the 1 px lip (inset 0 1px 0), the 1 px hairline, and in light the drop shadow (--lg-drop-up).
//
//   Glass.exe --out=<dir> --mode=cpu|gpu|acrylic --theme=light|dark [--sigma=N] [--fps=4] [--stretch]
// Writes <out>/ready.json once the first frame has been on screen for 700 ms, and <out>/log.txt.
// While <out>/pause exists the cpu mode takes no new picture (the check's screenshots then never
// land inside a capture-exclusion window).
using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;

namespace Glass;

public partial class App : Application
{
    string _out = ".", _mode = "cpu", _theme = "light";
    double _sigmaArg = -1, _fps = 4;
    bool _dark, _stretch;
    // the bar's geometry (main.js barGeometry): min(1120, screen - 28) wide, 48 tall, 14 above the bottom
    const double BarH = 48, Lift = 14, ShadowTop = 56, ShadowSide = 28;
    double _scale = 1, _pillW;
    int _scrW, _scrH;
    RECT _pillPx, _winPx;
    Window? _win;
    IntPtr _hwnd;
    bool _rendered;
    string _state = "";
    CpuGlass? _cpu;
    WriteableBitmap? _bmp;
    HostBlurBackdrop? _host;
    DesktopAcrylicController? _acrylic;
    SystemBackdropConfiguration? _acrylicCfg;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    int _frames, _skipped;
    double _captureMsSum, _renderMsSum;
    bool _busy;

    public App()
    {
        foreach (var a in Environment.GetCommandLineArgs()) {
            if (a.StartsWith("--out=")) _out = a[6..].Trim('"');
            else if (a.StartsWith("--mode=")) _mode = a[7..];
            else if (a.StartsWith("--theme=")) _theme = a[8..];
            else if (a.StartsWith("--sigma=")) _sigmaArg = double.Parse(a[8..], System.Globalization.CultureInfo.InvariantCulture);
            else if (a == "--stretch") _stretch = true;
            else if (a.StartsWith("--fps=")) _fps = double.Parse(a[6..], System.Globalization.CultureInfo.InvariantCulture);
        }
        _dark = _theme == "dark";
        Directory.CreateDirectory(_out);
        InitializeComponent();
        UnhandledException += (_, e) => Log("UNHANDLED: " + e.Exception);
    }

    void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(_out, "log.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }

    // ------------------------------------------------------------------ the island's tokens
    // light = glass-light.css :root; dark = :root[data-theme="dark"] with bar.css's dark .bar (--lg-fill-bar)
    double Radius => _dark ? 18 : 24;
    double Sigma => _sigmaArg > 0 ? _sigmaArg : (_dark ? 5 : 12);     // --lg-blur
    double Sat => _dark ? 1.0 : 1.5;
    Windows.UI.Color Tint => _dark ? C(9, 10, 11, .50) : C(255, 255, 255, .45);      // --lg-fill-bar / --lg-fill
    Windows.UI.Color LipC => _dark ? C(255, 255, 255, .07) : C(255, 255, 255, .62);  // --lg-lip-up
    Windows.UI.Color Edge => _dark ? C(255, 255, 255, .09) : C(11, 14, 26, .10);     // --lg-edge-out
    static Windows.UI.Color C(byte r, byte g, byte b, double a) => Windows.UI.Color.FromArgb((byte)Math.Round(a * 255), r, g, b);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log($"launched: mode {_mode}, theme {_theme}, sigma {Sigma}, saturate {Sat}");
        _scrW = GetSystemMetrics(0); _scrH = GetSystemMetrics(1);

        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        var shadowHost = new Canvas { IsHitTestVisible = false };
        var pill = new Grid { Height = BarH, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        var glass = new Border { CornerRadius = new CornerRadius(Radius) };
        var tint = new Border { CornerRadius = new CornerRadius(Radius), Background = new SolidColorBrush(Tint) };
        // the lip and the hairline are background-less layers of their own (a Border with a Background and a
        // translucent BorderBrush drew the hairline nearly opaque on the runner: bar spike run 1)
        var lip = new Border { Margin = new Thickness(1), CornerRadius = new CornerRadius(Radius - 1), BorderBrush = new SolidColorBrush(LipC), BorderThickness = new Thickness(0, 1, 0, 0) };
        var edge = new Border { CornerRadius = new CornerRadius(Radius), BorderBrush = new SolidColorBrush(Edge), BorderThickness = new Thickness(1) };
        pill.Children.Add(glass); pill.Children.Add(tint); pill.Children.Add(lip); pill.Children.Add(edge);
        root.Children.Add(shadowHost); root.Children.Add(pill);

        _win = new Window { Content = root };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        _scale = GetDpiForWindow(_hwnd) / 96.0;
        _pillW = Math.Min(1120, _scrW / _scale - 28);
        int pwPx = (int)Math.Round(_pillW * _scale), phPx = (int)Math.Round(BarH * _scale);
        int pxPx = (_scrW - pwPx) / 2, pyPx = _scrH - (int)Math.Round((Lift + BarH) * _scale);
        _pillPx = new RECT { Left = pxPx, Top = pyPx, Right = pxPx + pwPx, Bottom = pyPx + phPx };
        int sideP = (int)Math.Round(ShadowSide * _scale), topP = (int)Math.Round(ShadowTop * _scale), liftP = (int)Math.Round(Lift * _scale);
        _winPx = new RECT { Left = pxPx - sideP, Top = pyPx - topP, Right = pxPx + pwPx + sideP, Bottom = _scrH };
        root.Width = (_winPx.Right - _winPx.Left) / _scale; root.Height = (_winPx.Bottom - _winPx.Top) / _scale;
        pill.Width = _pillW; pill.Margin = new Thickness(ShadowSide, ShadowTop, 0, 0);
        Log($"screen {_scrW}x{_scrH} at {_scale:0.##}x; pill {pwPx}x{phPx} at {pxPx},{pyPx}; window {_winPx.Right - _winPx.Left}x{_winPx.Bottom - _winPx.Top} at {_winPx.Left},{_winPx.Top} (lift {liftP})");

        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        int pref = 1 /* DWMWCP_DONOTROUND */; DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, 4);
        int none = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */; DwmSetWindowAttribute(_hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, 4);
        SetWindowLongPtrW(_hwnd, -20, new IntPtr(GetWindowLongPtrW(_hwnd, -20).ToInt64() | 0x80 /* WS_EX_TOOLWINDOW */));

        float wPx = _winPx.Right - _winPx.Left, hPx = _winPx.Bottom - _winPx.Top;
        if (_mode == "gpu") {
            int on = 1; int hr = DwmSetWindowAttribute(_hwnd, 17 /* DWMWA_USE_HOSTBACKDROPBRUSH */, ref on, 4);
            Log($"DWMWA_USE_HOSTBACKDROPBRUSH: hr 0x{hr:X8}");
            // --stretch (light): Electron's picture of the pill is shown over the pill + 14 px each side
            float kx = _stretch ? (pwPx + 28f * (float)_scale) / pwPx : 1f, ky = _stretch ? (phPx + 28f * (float)_scale) / phPx : 1f;
            _host = new HostBlurBackdrop((float)(Sigma * _scale), (float)Sat, wPx, hPx, sideP, topP, pwPx, phPx, (float)(Radius * _scale), kx, ky);
            _win.SystemBackdrop = _host;
            Log("see-through: " + TransparentBackdrop.MakeSeeThrough(_hwnd));
        } else if (_mode == "acrylic") {
            _state = StartAcrylic();
            // the controller fills the whole window: cut the window to the pill (no shadow, no anti-aliased ends)
            IntPtr rgn = CreateRoundRectRgn(sideP, topP, sideP + pwPx + 1, topP + phPx + 1, (int)(2 * Radius * _scale), (int)(2 * Radius * _scale));
            SetWindowRgn(_hwnd, rgn, true);
        } else {
            _win.SystemBackdrop = new TransparentBackdrop();
            Log("see-through: " + TransparentBackdrop.MakeSeeThrough(_hwnd));
            _cpu = _dark
                ? new CpuGlass(pxPx, pyPx, pwPx, phPx, (int)Math.Round(16 * _scale), (int)Math.Round(16 * _scale), 8, Sigma * _scale, Sat, _scrW, _scrH)
                : new CpuGlass(pxPx, pyPx, pwPx, phPx, 0, (int)Math.Round(14 * _scale), 4, Sigma * _scale, Sat, _scrW, _scrH);
            _bmp = new WriteableBitmap(pwPx, phPx);
            glass.Background = new ImageBrush { ImageSource = _bmp, Stretch = Stretch.Fill };
            // the first picture before the window shows (Electron's pre-reveal frame): nothing of ours is on screen yet
            Refresh(exclude: false, "first picture");
        }
        if (!_dark && _mode != "acrylic") AddShadow(shadowHost);

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        _win.Activate();
        SetWindowLongPtrW(_hwnd, -20, new IntPtr(GetWindowLongPtrW(_hwnd, -20).ToInt64() | 0x08000000 /* WS_EX_NOACTIVATE */));
        // the frame ring the spike's setup leaves (WS_DLGFRAME|WS_SYSMENU still on): a plain popup instead
        SetWindowLongPtrW(_hwnd, -16, new IntPtr(unchecked((int)0x80000000) /* WS_POPUP */ | 0x10000000 /* WS_VISIBLE */));
        SetWindowPos(_hwnd, new IntPtr(-1) /* TOPMOST */, _winPx.Left, _winPx.Top, (int)wPx, (int)hPx, 0x10 | 0x20 /* NOACTIVATE FRAMECHANGED */);
        if (_mode == "acrylic") _acrylicCfg!.IsInputActive = true;

        if (_cpu != null) {
            if (!_dark) {
                var t = _win.DispatcherQueue.CreateTimer();
                t.Interval = TimeSpan.FromMilliseconds(1000 / _fps); t.IsRepeating = true;
                t.Tick += (_, _) => Tick();
                t.Start();
            } else {
                WatchWallpaper();   // dark: a new cut only when the wallpaper changes (no loop)
            }
            var rep = _win.DispatcherQueue.CreateTimer();
            rep.Interval = TimeSpan.FromSeconds(10); rep.IsRepeating = true;
            rep.Tick += (_, _) => Log($"cpu glass: {_frames} pictures, {_skipped} skipped (paused), mean capture {(_frames > 0 ? _captureMsSum / _frames : 0):0.0} ms, mean render {(_frames > 0 ? _renderMsSum / _frames : 0):0.0} ms");
            rep.Start();
        }
    }

    string StartAcrylic()
    {
        try {
            TransparentBackdrop.EnsureSystemDispatcherQueue();
            _acrylicCfg = new SystemBackdropConfiguration { IsInputActive = true, Theme = _dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light };
            _acrylic = new DesktopAcrylicController {
                Kind = DesktopAcrylicKind.Base,
                TintColor = _dark ? Windows.UI.Color.FromArgb(255, 9, 10, 11) : Windows.UI.Color.FromArgb(255, 255, 255, 255),
                TintOpacity = 0f, LuminosityOpacity = 0f,
                FallbackColor = Tint,
            };
            bool ok = _acrylic.AddSystemBackdropTarget(_win.As<ICompositionSupportsSystemBackdrop>());
            _acrylic.SetSystemBackdropConfiguration(_acrylicCfg);
            return $"acrylic controller: target {(ok ? "added" : "REFUSED")}, always active, tint 0, luminosity 0";
        } catch (Exception e) { return "acrylic FAILED: " + e.GetType().Name + ": " + e.Message; }
    }

    // ------------------------------------------------------------------ the cpu mode's pictures
    void Tick()
    {
        if (_busy) return;
        if (File.Exists(Path.Combine(_out, "pause"))) { _skipped++; return; }
        _busy = true;
        // exclude the window from capture, give DWM a frame to drop it (Electron: BACKDROP_SETTLE_MS 25), shoot
        SetWindowDisplayAffinity(_hwnd, 0x11 /* WDA_EXCLUDEFROMCAPTURE */);
        var t = _win!.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(30); t.IsRepeating = false;
        t.Tick += (_, _) => { Refresh(exclude: true, null); _busy = false; };
        t.Start();
    }

    void Refresh(bool exclude, string? why)
    {
        bool ok;
        try { ok = _cpu!.CaptureAndRender(); }
        catch (Exception e) { ok = false; Log("cpu glass failed: " + e.Message); }
        finally { if (exclude) SetWindowDisplayAffinity(_hwnd, 0 /* WDA_NONE */); }
        if (!ok) { _state = "cpu: the screen could not be read"; return; }
        _cpu.Output.CopyTo(_bmp!.PixelBuffer);
        _bmp.Invalidate();
        _frames++; _captureMsSum += _cpu.LastCaptureMs; _renderMsSum += _cpu.LastRenderMs;
        _state = "cpu: " + _cpu.Describe();
        if (why != null) Log($"{why}: capture {_cpu.LastCaptureMs:0.0} ms, render {_cpu.LastRenderMs:0.0} ms ({_cpu.Describe()})");
    }

    // dark: Windows says the wallpaper changed -> a new cut, 400 ms later (Explorer repaints first)
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    SubclassProc? _wallProc;
    void WatchWallpaper()
    {
        _wallProc = (h, msg, wp, lp, id, data) => {
            if (msg == 0x001A /* WM_SETTINGCHANGE */ && wp.ToInt64() == 0x0014 /* SPI_SETDESKWALLPAPER */) {
                var t = _win!.DispatcherQueue.CreateTimer();
                t.Interval = TimeSpan.FromMilliseconds(400); t.IsRepeating = false;
                t.Tick += (_, _) => {
                    if (File.Exists(Path.Combine(_out, "pause"))) { _skipped++; return; }
                    SetWindowDisplayAffinity(_hwnd, 0x11);
                    var t2 = _win!.DispatcherQueue.CreateTimer();
                    t2.Interval = TimeSpan.FromMilliseconds(30); t2.IsRepeating = false;
                    t2.Tick += (_, _) => Refresh(exclude: true, null);
                    t2.Start();
                };
                t.Start();
            }
            return DefSubclassProc(h, msg, wp, lp);
        };
        Log("dark: wallpaper watch " + (SetWindowSubclass(_hwnd, _wallProc, new UIntPtr(2), IntPtr.Zero) ? "on" : "FAILED"));
    }

    // ------------------------------------------------------------------ the light drop shadow
    // --lg-drop-up: 0 -1px 1px rgba(14,18,38,.05), 0 -10px 28px -12px rgba(14,18,38,.22), 0 -26px 56px -28px rgba(14,18,38,.30)
    // A negative spread shrinks the shape before the blur; the third layer's -28 leaves a 48 tall pill no
    // shape at all, so it draws nothing, in CSS as here. A CSS blur radius is twice the Gaussian's sigma.
    void AddShadow(Canvas host)
    {
        var c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var container = c.CreateContainerVisual();
        void Layer(double dy, double blur, double spread, double alpha)
        {
            float w = (float)(_pillW + 2 * spread), h = (float)(BarH + 2 * spread);
            if (w <= 0 || h <= 0) return;
            float r = (float)Math.Max(0, Radius + spread);
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
            ds.Color = Windows.UI.Color.FromArgb((byte)Math.Round(alpha * 255), 14, 18, 38);
            sprite.Shadow = ds;
            container.Children.InsertAtTop(sprite);
        }
        Layer(-1, 1, 0, .05);
        Layer(-10, 28, -12, .22);
        Layer(-26, 56, -28, .30);
        ElementCompositionPreview.SetElementChildVisual(host, container);
    }

    // ------------------------------------------------------------------ ready.json
    void OnRendering(object? s, object e)
    {
        if (_rendered) return;
        _rendered = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        Log("first frame");
        var t = _win!.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(700); t.IsRepeating = false;
        t.Tick += (_, _) => WriteReady();
        t.Start();
    }

    void WriteReady()
    {
        GetWindowRect(_hwnd, out var wr);
        if (_host != null) _state = _host.State;
        var ready = new {
            mode = _mode, theme = _theme, sigma = Sigma, saturate = Sat, radius = Radius, dpi = GetDpiForWindow(_hwnd),
            hwnd = _hwnd.ToInt64(),
            style = "0x" + GetWindowLongPtrW(_hwnd, -16).ToInt64().ToString("X8"),
            exStyle = "0x" + GetWindowLongPtrW(_hwnd, -20).ToInt64().ToString("X8"),
            window = new { x = wr.Left, y = wr.Top, w = wr.Right - wr.Left, h = wr.Bottom - wr.Top },
            pill = new { x = _pillPx.Left, y = _pillPx.Top, w = _pillPx.Right - _pillPx.Left, h = _pillPx.Bottom - _pillPx.Top, r = (int)Math.Round(Radius * _scale) },
            state = _state,
            pictures = _frames,
            captureMs = _cpu?.LastCaptureMs, renderMs = _cpu?.LastRenderMs,
            msSinceStart = _clock.ElapsedMilliseconds,
        };
        File.WriteAllText(Path.Combine(_out, "ready.json"), JsonSerializer.Serialize(ready));
        Log("ready.json written: " + _state);
    }

    // ------------------------------------------------------------------ Win32
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
}
