// Smoke: one see-through WinUI 3 window, set up exactly as native-spike/MantleBar/BarWindow does
// (OverlappedPresenter without border/title bar, DWM corners off, border colour none, tool window,
// TransparentBackdrop + MakeSeeThrough), with one opaque rounded rectangle in it.
//
//   Smoke.exe --out=<dir> [--x=300 --y=300 --w=400 --h=200]
// Writes <out>/ready.json (hwnd, styles, window/client/DWM rects, the shape's screen rect) once the
// first frame has been on screen for 700 ms, and <out>/log.txt.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Smoke;

public partial class App : Application
{
    string _out = ".";
    int _x = 300, _y = 300, _w = 400, _h = 200;
    const int ShapeW = 300, ShapeH = 100, Radius = 24;
    Window? _win;
    IntPtr _hwnd;
    bool _rendered;

    public App()
    {
        foreach (var a in Environment.GetCommandLineArgs()) {
            if (a.StartsWith("--out=")) _out = a[6..].Trim('"');
            else if (a.StartsWith("--x=")) _x = int.Parse(a[4..]);
            else if (a.StartsWith("--y=")) _y = int.Parse(a[4..]);
            else if (a.StartsWith("--w=")) _w = int.Parse(a[4..]);
            else if (a.StartsWith("--h=")) _h = int.Parse(a[4..]);
        }
        Directory.CreateDirectory(_out);
        InitializeComponent();
        UnhandledException += (_, e) => Log("UNHANDLED: " + e.Exception);
    }

    void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(_out, "log.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log("launched");
        var shape = new Border {
            Width = ShapeW, Height = ShapeH, CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 96, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(shape);

        _win = new Window { Content = root, SystemBackdrop = new TransparentBackdrop() };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);

        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        int pref = 1 /* DWMWCP_DONOTROUND */; DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, 4);
        int none = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */; DwmSetWindowAttribute(_hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, 4);
        SetWindowLongPtrW(_hwnd, -20, new IntPtr(GetWindowLongPtrW(_hwnd, -20).ToInt64() | 0x80 /* WS_EX_TOOLWINDOW */));
        Log("see-through: " + TransparentBackdrop.MakeSeeThrough(_hwnd));
        _win.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(_x, _y, _w, _h));

        CompositionTarget.Rendering += OnRendering;
        _win.Activate();
        SetWindowLongPtrW(_hwnd, -20, new IntPtr(GetWindowLongPtrW(_hwnd, -20).ToInt64() | 0x08000000 /* WS_EX_NOACTIVATE */));
        SetWindowPos(_hwnd, new IntPtr(-1) /* TOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x20 /* NOSIZE NOMOVE NOACTIVATE FRAMECHANGED */);
    }

    void OnRendering(object? s, object e)
    {
        if (_rendered) return;
        _rendered = true;
        CompositionTarget.Rendering -= OnRendering;
        Log("first frame");
        var t = _win!.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(700); t.IsRepeating = false;
        t.Tick += (_, _) => WriteReady();
        t.Start();
    }

    void WriteReady()
    {
        GetWindowRect(_hwnd, out var wr);
        GetClientRect(_hwnd, out var cr);
        var o = new POINT(); ClientToScreen(_hwnd, ref o);
        DwmGetWindowAttribute(_hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var fr, Marshal.SizeOf<RECT>());
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int sw = (int)Math.Round(ShapeW * scale), sh = (int)Math.Round(ShapeH * scale);
        var ready = new {
            hwnd = _hwnd.ToInt64(), dpi = GetDpiForWindow(_hwnd),
            style = "0x" + GetWindowLongPtrW(_hwnd, -16).ToInt64().ToString("X8"),
            exStyle = "0x" + GetWindowLongPtrW(_hwnd, -20).ToInt64().ToString("X8"),
            window = new { x = wr.Left, y = wr.Top, w = wr.Right - wr.Left, h = wr.Bottom - wr.Top },
            client = new { x = o.X, y = o.Y, w = cr.Right, h = cr.Bottom },
            dwmFrame = new { x = fr.Left, y = fr.Top, w = fr.Right - fr.Left, h = fr.Bottom - fr.Top },
            shape = new { x = o.X + (cr.Right - sw) / 2, y = o.Y + (cr.Bottom - sh) / 2, w = sw, h = sh, r = (int)Math.Round(Radius * scale) },
        };
        File.WriteAllText(Path.Combine(_out, "ready.json"), JsonSerializer.Serialize(ready));
        Log("ready.json written");
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT v, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
}
