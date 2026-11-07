// DeskSpike: primitive 4, the desktop layer. One full-screen WinUI 3 window (or, --kind=gdi, a plain
// GDI window as the baseline) made a CHILD of Explorer's desktop with DesktopLayer.Attach, kept there
// by a recheck timer (DesktopLayer.Recheck; a window Explorer destroyed is made again), and an
// optional click route (--route=hook): a low-level mouse hook on its own thread that takes left
// clicks on the layer's own button, because Explorer's icon layer (SHELLDLL_DefView) sits OVER the
// layer and gets every click there (the sidecar's DeskClick.cs finding). Clicks on an icon stay
// Explorer's.
//
//   DeskSpike.exe --out=<dir> [--mode=auto|progman-front|workerw-sibling|workerw-child|progman]
//                 [--kind=winui|gdi] [--route=none|hook] [--order=attach-first|show-first] [--recheck-ms=1000]
//
// Content (physical px at 100 %): the whole monitor magenta (230,40,140), a cyan (20,200,220) button
// at 1300,300 300x200 that turns yellow (255,200,0) when it is clicked. Writes <out>/state.json
// (rewritten on every change), <out>/clicks.jsonl (one line per click that reached the layer) and
// <out>/log.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskSpike;

public partial class App : Application
{
    public const int BtnX = 1300, BtnY = 300, BtnW = 300, BtnH = 200;
    static readonly Windows.UI.Color Magenta = Windows.UI.Color.FromArgb(255, 230, 40, 140);
    static readonly Windows.UI.Color Cyan = Windows.UI.Color.FromArgb(255, 20, 200, 220);
    static readonly Windows.UI.Color Yellow = Windows.UI.Color.FromArgb(255, 255, 200, 0);

    string _out = ".", _mode = "auto", _kind = "winui", _route = "none", _order = "attach-first";
    int _recheckMs = 1000;
    readonly long _t0 = Environment.TickCount64;

    Window? _win;
    Border? _button;
    IntPtr _hwnd;
    DispatcherQueue? _dq;
    readonly Dictionary<string, object?> _state = new();
    readonly List<string> _events = new();
    int _gen, _remakes, _rechecks, _clicks;
    string? _attachedMode;
    bool _renderedAfterAttach;
    MouseRoute? _mouse;

    public App()
    {
        foreach (var a in Environment.GetCommandLineArgs()) {
            if (a.StartsWith("--out=")) _out = a[6..].Trim('"');
            else if (a.StartsWith("--mode=")) _mode = a[7..];
            else if (a.StartsWith("--kind=")) _kind = a[7..];
            else if (a.StartsWith("--route=")) _route = a[8..];
            else if (a.StartsWith("--order=")) _order = a[8..];
            else if (a.StartsWith("--recheck-ms=")) _recheckMs = int.Parse(a[13..]);
        }
        Directory.CreateDirectory(_out);
        InitializeComponent();
        // A layer Explorer destroys must not end the process: we make a new one.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => { Log("UNHANDLED: " + e.Exception); e.Handled = true; };
    }

    public void Log(string s)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff ") + s;
        try { File.AppendAllText(Path.Combine(_out, "log.txt"), line + Environment.NewLine); } catch { }
        lock (_events) { _events.Add(line); if (_events.Count > 30) _events.RemoveAt(0); }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        Log($"launched mode={_mode} kind={_kind} route={_route} order={_order}");
        _state["args"] = new { mode = _mode, kind = _kind, route = _route, order = _order, recheckMs = _recheckMs };
        Make("start");
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(_recheckMs);
        t.IsRepeating = true;
        t.Tick += (_, _) => Tick();
        t.Start();
        if (_route == "hook") {
            _mouse = new MouseRoute(this, _dq);
            _mouse.Start();
        }
    }

    // ------------------------------------------------------------------ the window

    void Make(string why)
    {
        _gen++;
        _renderedAfterAttach = false;
        _attachedMode = null;
        Log($"make #{_gen} ({why})");
        if (_kind == "gdi") _hwnd = GdiLayer.Create(this);
        else MakeWinUI();
        _state["gen"] = _gen;
        _state["hwnd"] = _hwnd.ToInt64();
        if (_order == "show-first" && _kind == "winui") { ShowWinUI(); return; }   // attach after the first frame
        TryAttach();
        if (_kind == "winui") ShowWinUI();
        Write();
    }

    void MakeWinUI()
    {
        var root = new Grid { Background = new SolidColorBrush(Magenta) };
        _button = new Border {
            Width = BtnW, Height = BtnH, Background = new SolidColorBrush(Cyan),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(BtnX, BtnY, 0, 0),
            Child = new TextBlock { Text = "layer button", FontSize = 28, Foreground = new SolidColorBrush(Colors.Black),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _button.PointerPressed += (_, e) => {
            var p = e.GetCurrentPoint(null).Position;
            OnClick("direct", (int)p.X, (int)p.Y);
            e.Handled = true;
        };
        root.PointerPressed += (_, e) => {
            var p = e.GetCurrentPoint(null).Position;
            Click("direct-background", (int)p.X, (int)p.Y);
        };
        root.Children.Add(_button);
        root.Children.Add(new TextBlock { Text = "Mantle desktop layer spike (WinUI 3)", FontSize = 40,
            Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(1300, 140, 0, 0), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top });

        var win = new Window { Content = root };
        _win = win;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(win);
        var pres = OverlappedPresenter.Create();
        pres.IsResizable = false; pres.IsMaximizable = false; pres.IsMinimizable = false;
        pres.SetBorderAndTitleBar(false, false);
        win.AppWindow.SetPresenter(pres);
        win.AppWindow.IsShownInSwitchers = false;
        var s = Native.Screen();
        win.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(s.X, s.Y, s.W, s.H));
        win.Closed += (_, _) => Log($"Window.Closed (gen {_gen})");
        if (!_renderHooked) { _renderHooked = true; CompositionTarget.Rendering += OnRendering; }
    }

    void ShowWinUI()
    {
        try { _win!.Activate(); Log("Activate() ok"); } catch (Exception e) { Log("Activate threw: " + e.Message); }
    }

    int _framesAfterAttach;
    bool _renderHooked;
    void OnRendering(object? s, object e)
    {
        if (_order == "show-first" && _attachedMode == null && _state.GetValueOrDefault("attachError") == null) {
            TryAttach(); Write(); return;
        }
        if (_attachedMode == null || _renderedAfterAttach) return;
        if (++_framesAfterAttach < 3) return;
        _renderedAfterAttach = true;
        _framesAfterAttach = 0;
        CompositionTarget.Rendering -= OnRendering; _renderHooked = false;   // no per-frame callback while idle
        Log("frames rendered after attach");
        var t = _dq!.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(700); t.IsRepeating = false;
        t.Tick += (_, _) => { _state["rendered"] = true; _state["renderedAtMs"] = Environment.TickCount64 - _t0; Write(); };
        t.Start();
    }

    void TryAttach()
    {
        long a0 = Environment.TickCount64;
        string? err = DesktopLayer.Attach(_hwnd, _mode, out string? mode);
        string? fitErr = null, rect = "";
        if (err == null) fitErr = DesktopLayer.Fit(_hwnd, out rect);
        _state["attachMs"] = Environment.TickCount64 - a0;
        _state["attachError"] = err;
        _state["fitError"] = fitErr;
        _state["fitRect"] = rect;
        _state["rendered"] = false;
        if (err == null) {
            _attachedMode = mode;
            _state["mode"] = mode;
            if (_kind == "gdi") { _state["rendered"] = true; GdiLayer.Repaint(_hwnd); }
        }
        Log($"attach gen {_gen}: mode={mode ?? "-"} err={err ?? "-"} fit={fitErr ?? rect}");
    }

    void Tick()
    {
        _rechecks++;
        if (!Native.IsWindow(_hwnd)) {
            // Explorer took the layer down with its desktop: a new window, attached when Progman is back.
            if (Native.FindWindow("Progman") == IntPtr.Zero) { _state["waiting"] = "no Progman yet"; Write(); return; }
            _remakes++;
            _state["remakes"] = _remakes;
            Log($"layer window {_hwnd.ToInt64()} is gone: making a new one");
            Make("window destroyed (explorer restart)");
            return;
        }
        if (_attachedMode == null) {
            if (_state.GetValueOrDefault("attachError") != null && _order != "show-first") { TryAttach(); Write(); }
            return;
        }
        string? e = DesktopLayer.Recheck(_hwnd, _mode, _attachedMode, out bool moved, out string? why);
        if (e != null) { _state["recheckError"] = e; Log("recheck: " + e); }
        else _state.Remove("recheckError");
        if (moved) {
            _state["lastMove"] = why; Log("recheck moved: " + why);
            _state["rejoins"] = (_state.GetValueOrDefault("rejoins") as int? ?? 0) + 1;
            DesktopLayer.Fit(_hwnd, out string rect); _state["fitRect"] = rect;
            if (_kind == "gdi") GdiLayer.Repaint(_hwnd);
        }
        if (_mouse != null) _mouse.RefreshIcons();
        Write();
    }

    // ------------------------------------------------------------------ clicks

    /// The button got a click (direct = XAML's own PointerPressed, hook = the mouse route).
    public void OnClick(string via, int x, int y)
    {
        Click(via, x, y);
        if (_button != null) _button.Background = new SolidColorBrush(Yellow);
        if (_kind == "gdi") GdiLayer.Pressed(_hwnd);
    }

    public void Click(string via, int x, int y)
    {
        _clicks++;
        _state["clicks"] = _clicks;
        try {
            File.AppendAllText(Path.Combine(_out, "clicks.jsonl"),
                JsonSerializer.Serialize(new { via, x, y, ms = Environment.TickCount64 - _t0 }) + Environment.NewLine);
        } catch { }
        Log($"click {via} at {x},{y}");
        Write();
    }

    // ------------------------------------------------------------------ state.json

    void Write()
    {
        try {
            _state["rechecks"] = _rechecks;
            _state["msSinceStart"] = Environment.TickCount64 - _t0;
            _state["window"] = Native.Describe(_hwnd);
            _state["button"] = new { x = BtnX, y = BtnY, w = BtnW, h = BtnH };
            if (_mouse != null) _state["route"] = _mouse.Status();
            lock (_events) _state["events"] = _events.ToArray();
            string tmp = Path.Combine(_out, "state.tmp"), dst = Path.Combine(_out, "state.json");
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, dst, true);
        } catch (Exception e) { Log("state write failed: " + e.Message); }
    }

    public IntPtr LayerHwnd => _hwnd;
}

/// The click route (the DeskClick.cs approach, for the left button and only the layer's own button):
/// a low-level mouse hook on a thread of its own (a busy UI thread must never hold up the mouse). On a
/// left-button DOWN inside the button's screen rect, over the desktop (the window under the cursor
/// has Progman / the layer as its root), and NOT on an icon: the down and its up are kept from
/// Explorer and the click is handed to the UI thread. Everything else passes untouched.
sealed class MouseRoute
{
    readonly App _app;
    readonly DispatcherQueue _dq;
    IntPtr _hook;
    uint _tid;
    Native.HookProc? _proc;
    volatile int[] _iconRects = Array.Empty<int>();
    volatile bool _upPending;
    int _taken, _passedOnIcon, _passedOther, _maxUs;
    string? _iconError;

    public MouseRoute(App app, DispatcherQueue dq) { _app = app; _dq = dq; }

    public void Start()
    {
        RefreshIcons();
        var th = new Thread(Run) { IsBackground = true, Name = "mouse route", Priority = ThreadPriority.Highest };
        th.Start();
    }

    public void RefreshIcons()
    {
        var icons = DeskIcons.List(out _iconError);
        var r = new int[icons.Count * 4];
        for (int i = 0; i < icons.Count; i++) { r[i * 4] = icons[i].X; r[i * 4 + 1] = icons[i].Y; r[i * 4 + 2] = icons[i].W; r[i * 4 + 3] = icons[i].H; }
        _iconRects = r;
    }

    public object Status() => new { taken = _taken, passedOnIcon = _passedOnIcon, passedOther = _passedOther,
        icons = _iconRects.Length / 4, iconError = _iconError, maxDecisionUs = _maxUs, hooked = _hook != IntPtr.Zero };

    void Run()
    {
        _tid = Native.GetCurrentThreadId();
        _proc = Proc;
        _hook = Native.SetWindowsHookExW(14 /* WH_MOUSE_LL */, _proc, Native.GetModuleHandleW(null), 0);
        _app.Log("mouse route hook " + (_hook != IntPtr.Zero ? "installed" : "FAILED " + Marshal.GetLastWin32Error()));
        while (Native.GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { Native.TranslateMessage(ref m); Native.DispatchMessageW(ref m); }
    }

    IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code == 0) {
            uint msg = (uint)w.ToInt64();
            if (msg == 0x0201 /* LBUTTONDOWN */) {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int x = Marshal.ReadInt32(l, 0), y = Marshal.ReadInt32(l, 4);
                bool take = false;
                if (x >= App.BtnX && x < App.BtnX + App.BtnW && y >= App.BtnY && y < App.BtnY + App.BtnH) {
                    if (OnIcon(x, y)) _passedOnIcon++;
                    else if (OverDesktop(x, y)) take = true;
                    else _passedOther++;
                }
                int us = (int)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
                if (us > _maxUs) _maxUs = us;
                if (take) {
                    _taken++;
                    _upPending = true;
                    _dq.TryEnqueue(() => _app.OnClick("hook", x, y));
                    return new IntPtr(1);
                }
            } else if (msg == 0x0202 /* LBUTTONUP */ && _upPending) {
                _upPending = false;
                return new IntPtr(1);
            }
        }
        return Native.CallNextHookEx(_hook, code, w, l);
    }

    bool OnIcon(int x, int y)
    {
        var r = _iconRects;
        for (int i = 0; i + 3 < r.Length; i += 4)
            if (x >= r[i] - 2 && x < r[i] + r[i + 2] + 2 && y >= r[i + 1] - 2 && y < r[i + 1] + r[i + 3] + 2) return true;
        return false;
    }

    bool OverDesktop(int x, int y)
    {
        IntPtr h = Native.WindowFromPoint(new Native.PT { X = x, Y = y });
        IntPtr root = Native.GetAncestor(h, 2 /* GA_ROOT */);
        return root == Native.FindWindow("Progman") || root == _app.LayerHwnd || h == _app.LayerHwnd ||
               DesktopLayer.ClassOf(root) == "WorkerW";
    }
}

/// --kind=gdi: the same layer as a plain GDI window (no DirectComposition), the baseline that tells
/// a WinUI rendering problem from a reparenting one.
static class GdiLayer
{
    static Native.WndProc? s_proc;
    static App? s_app;
    static bool s_pressed;

    public static IntPtr Create(App app)
    {
        s_app = app;
        if (s_proc == null) {
            s_proc = Proc;
            var wc = new Native.WNDCLASSEXW { cbSize = Marshal.SizeOf<Native.WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = Native.GetModuleHandleW(null), lpszClassName = "DeskSpikeGdi", hCursor = Native.LoadCursorW(IntPtr.Zero, 32512) };
            Native.RegisterClassExW(ref wc);
        }
        var s = Native.Screen();
        return Native.CreateWindowExW(0x80 /* TOOLWINDOW */, "DeskSpikeGdi", "DeskSpike GDI layer", 0x80000000 /* POPUP */,
            s.X, s.Y, s.W, s.H, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
    }
    public static void Repaint(IntPtr h) => Native.InvalidateRect(h, IntPtr.Zero, true);
    public static void Pressed(IntPtr h) { s_pressed = true; Repaint(h); }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == 0x000F /* WM_PAINT */) {
            var ps = new byte[72];
            IntPtr dc = Native.BeginPaint(h, ps);
            Native.GetClientRect(h, out var rc);
            Fill(dc, rc, 0x008C28E6);                              // COLORREF 0x00BBGGRR: 230,40,140
            var b = new Native.RC { L = App.BtnX, T = App.BtnY, R = App.BtnX + App.BtnW, B = App.BtnY + App.BtnH };
            Fill(dc, b, s_pressed ? 0x0000C8FFu : 0x00DCC814u);   // yellow 255,200,0 / cyan 20,200,220
            Native.EndPaint(h, ps);
            return IntPtr.Zero;
        }
        if (msg == 0x0201 /* WM_LBUTTONDOWN */) {
            int x = (short)(l.ToInt64() & 0xFFFF), y = (short)((l.ToInt64() >> 16) & 0xFFFF);
            if (x >= App.BtnX && x < App.BtnX + App.BtnW && y >= App.BtnY && y < App.BtnY + App.BtnH) s_app?.OnClick("direct", x, y);
            else s_app?.Click("direct-background", x, y);
            return IntPtr.Zero;
        }
        return Native.DefWindowProcW(h, msg, w, l);
    }
    static void Fill(IntPtr dc, Native.RC r, uint color) { IntPtr br = Native.CreateSolidBrush(color); Native.FillRect(dc, ref r, br); Native.DeleteObject(br); }
}

static class Native
{
    public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    public delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RC { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public PT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW { public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookExW(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string? n);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? c, string? t);
    public static IntPtr FindWindow(string cls) => FindWindowW(cls, null);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, int id);
    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, byte[] ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, byte[] ps);
    [DllImport("user32.dll")] public static extern int FillRect(IntPtr dc, ref RC r, IntPtr br);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint c);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);

    public static (int X, int Y, int W, int H) Screen() => (0, 0, GetSystemMetrics(0), GetSystemMetrics(1));

    public static object Describe(IntPtr h)
    {
        if (!IsWindow(h)) return new { alive = false };
        GetWindowRect(h, out var r);
        IntPtr parent = GetAncestor(h, 1);
        return new {
            alive = true, visible = IsWindowVisible(h),
            style = "0x" + GetWindowLongPtrW(h, -16).ToInt64().ToString("X8"),
            exStyle = "0x" + GetWindowLongPtrW(h, -20).ToInt64().ToString("X8"),
            parent = parent.ToInt64(), parentClass = DesktopLayer.ClassOf(parent),
            progman = FindWindow("Progman").ToInt64(),
            rect = new { x = r.L, y = r.T, w = r.R - r.L, h = r.B - r.T },
        };
    }
}
