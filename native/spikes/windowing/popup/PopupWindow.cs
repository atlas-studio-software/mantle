// PopupWindow.cs - one see-through popup: a rounded glass shape (Mantle's island fill + 1 px hairline,
// ui/shared/glass-light.css) centred on a fully transparent window, set up with one of the frame
// techniques (frame=) and one of the click-through techniques (hit=), shown, parked and re-shown in
// one of the park modes (park=).
//
//   frame=spike   the bar spike as is: OverlappedPresenter without border/title bar, DWM corners off,
//                 border colour none, tool window, transparent backdrop + DWM frame + black erase.
//                 Leaves WS_DLGFRAME|WS_SYSMENU: a 3 px frame ring (the smoke baseline).
//   frame=popup   + style stripped to plain WS_POPUP (SWP_FRAMECHANGED).
//   frame=nc      + WM_NCCALCSIZE returns 0 (the client area is the whole window, whatever the style).
//   frame=ncp     + DWMWA_NCRENDERING_POLICY = DISABLED.
//
//   hit=none      nothing: the whole window rect takes the mouse.
//   hit=nchittest WM_NCHITTEST -> HTTRANSPARENT outside the shape (top level and its same-thread children).
//   hit=layered   WS_EX_LAYERED (alpha 255) + WS_EX_TRANSPARENT toggled by a 16 ms cursor poll.
//   hit=region    SetWindowRgn to the shape (+1 px for the anti-aliased edge).
//   hit=ncregion  InputNonClientPointerSource.SetRegionRects(Passthrough) over the transparent margins.
//
//   park=apphide  AppWindow.Hide / AppWindow.Show(false)
//   park=swhide   ShowWindow SW_HIDE / SW_SHOWNOACTIVATE
//   park=offscreen SetWindowPos to -32000,-32000 and back
//   park=cloak    DWMWA_CLOAK on / off
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static PopupSpike.Native;

namespace PopupSpike;

internal sealed class PopupSpec
{
    public int X = 300, Y = 300, W = 400, H = 200, ShapeW = 300, ShapeH = 100, Radius = 24;
    public string Frame = "nc", Theme = "light", Fill = "glass", Hit = "none", Park = "apphide";

    public static PopupSpec From(IDictionary<string, string> a)
    {
        var s = new PopupSpec();
        int I(string k, int d) => a.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
        string S(string k, string d) => a.TryGetValue(k, out var v) && v.Length > 0 ? v : d;
        s.X = I("x", s.X); s.Y = I("y", s.Y); s.W = I("w", s.W); s.H = I("h", s.H);
        s.ShapeW = I("sw", s.ShapeW); s.ShapeH = I("sh", s.ShapeH); s.Radius = I("r", s.Radius);
        s.Frame = S("frame", s.Frame); s.Theme = S("theme", s.Theme); s.Fill = S("fill", s.Fill);
        s.Hit = S("hit", s.Hit); s.Park = S("park", s.Park);
        return s;
    }
}

internal sealed class PopupWindow
{
    public readonly int Id;
    public readonly PopupSpec Spec;
    public readonly Window Win;
    public readonly IntPtr Hwnd;
    public readonly Windows.UI.Color Fill, Edge;
    public int Moves;
    readonly Grid _root;
    readonly HookState _hook = new() { Erase = true };
    readonly List<string> _children = new();
    readonly List<string> _notes = new();
    string _park;
    bool _parked;
    double _callMs;
    DispatcherQueueTimer? _poll;
    bool? _inside;

    static Windows.UI.Color C(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    public PopupWindow(int id, PopupSpec spec)
    {
        Id = id; Spec = spec; _park = spec.Park;
        bool light = spec.Theme != "dark";
        if (spec.Fill == "solid") { Fill = C(255, 255, 0, 255); Edge = Fill; }        // opaque magenta: coverage is unambiguous
        else if (light) { Fill = C(115, 255, 255, 255); Edge = C(26, 11, 14, 26); }    // --lg-fill .45 white, --lg-edge-out .10 ink
        else { Fill = C(212, 19, 20, 21); Edge = C(23, 255, 255, 255); }               // dark --lg-fill (19,20,21,.83), --lg-edge-out .09 white

        var shape = new Border {
            Width = spec.ShapeW, Height = spec.ShapeH, CornerRadius = new CornerRadius(spec.Radius),
            Background = new SolidColorBrush(Fill), BorderBrush = new SolidColorBrush(Edge), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        // Transparent (not null) so XAML hit-tests the empty margins: with hit=none a click there lands here.
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent), RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark };
        _root.Children.Add(shape);
        _root.PointerPressed += (_, e) => {
            var p = e.GetCurrentPoint(_root).Position;
            App.AddEvent(new { win = Id, kind = "pressed", x = Math.Round(p.X), y = Math.Round(p.Y), t = App.NowMs() });
        };
        _root.PointerMoved += (_, _) => Moves++;

        Win = new Window { Content = _root, SystemBackdrop = new TransparentBackdrop() };
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Win);
        Configure();
    }

    void Configure()
    {
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        Win.AppWindow.SetPresenter(p);
        Win.AppWindow.IsShownInSwitchers = false;
        int pref = 1 /* DWMWCP_DONOTROUND */; DwmSetWindowAttribute(Hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, 4);
        int none = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */; DwmSetWindowAttribute(Hwnd, 34 /* DWMWA_BORDER_COLOR */, ref none, 4);
        int noAnim = 1; DwmSetWindowAttribute(Hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref noAnim, 4);   // a popup appears at once: no DWM show/hide animation
        SetExStyle(Hwnd, GetExStyle(Hwnd) | WS_EX_TOOLWINDOW);
        _hook.NcCalc = Spec.Frame is "nc" or "ncp";
        _hook.InShape = InShape;
        if (!Hook(Hwnd, _hook)) _notes.Add("subclass FAILED");
        int hr = ExtendFrame(Hwnd);
        if (hr != 0) _notes.Add($"DwmExtendFrameIntoClientArea hr 0x{hr:X8}");
        if (Spec.Frame == "ncp") {
            int pol = 1 /* DWMNCRP_DISABLED */;
            int r2 = DwmSetWindowAttribute(Hwnd, 2 /* DWMWA_NCRENDERING_POLICY */, ref pol, 4);
            _notes.Add($"NCRENDERING_POLICY disabled hr 0x{r2:X8}");
        }
        Strip();
        Win.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(Spec.X, Spec.Y, Spec.W, Spec.H));
    }

    /// Plain WS_POPUP (keeps WS_VISIBLE) for every frame technique but the spike's own.
    void Strip()
    {
        if (Spec.Frame == "spike") return;
        long st = GetStyle(Hwnd);
        long want = (st & WS_VISIBLE) | WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        if (st == want) return;
        SetWindowLongPtrW(Hwnd, GWL_STYLE, new IntPtr(want));
        SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // ------------------------------------------------------------------ geometry
    public (int x, int y, int w, int h, int r) ShapeRect()
    {
        GetClientRect(Hwnd, out var cr);
        var o = new POINT(); ClientToScreen(Hwnd, ref o);
        double sc = GetDpiForWindow(Hwnd) / 96.0;
        int sw = (int)Math.Round(Spec.ShapeW * sc), sh = (int)Math.Round(Spec.ShapeH * sc), r = (int)Math.Round(Spec.Radius * sc);
        return (o.X + (cr.Right - sw) / 2, o.Y + (cr.Bottom - sh) / 2, sw, sh, r);
    }

    bool InShape(int x, int y) { var s = ShapeRect(); return InsideRound(x, y, s.x, s.y, s.w, s.h, s.r); }

    // ------------------------------------------------------------------ show / park / resize
    /// Calls done(renderMs since t0, or null) on the first CompositionTarget.Rendering after this call,
    /// or after timeoutMs without one.
    void AwaitFrame(double t0, int timeoutMs, Action<double?> done)
    {
        bool fired = false;
        var timer = Win.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(timeoutMs); timer.IsRepeating = false;
        EventHandler<object>? h = null;
        h = (_, _) => {
            if (fired) return; fired = true;
            CompositionTarget.Rendering -= h; timer.Stop();
            done(App.NowMs() - t0);
        };
        timer.Tick += (_, _) => {
            if (fired) return; fired = true;
            CompositionTarget.Rendering -= h;
            done(null);
        };
        CompositionTarget.Rendering += h;
        timer.Start();
    }

    public void Open(double t0, int timeoutMs, Action<Dictionary<string, object?>> done)
    {
        double t1 = App.NowMs();
        AwaitFrame(t0, timeoutMs, ms => { var d = Info(); d["renderMs"] = ms; d["callMs"] = _callMs; done(d); });
        Win.Activate();
        SetExStyle(Hwnd, GetExStyle(Hwnd) | WS_EX_NOACTIVATE);
        Strip();
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        ApplyHit();
        _callMs = App.NowMs() - t1;
    }

    public void Show(double t0, Action<Dictionary<string, object?>> done)
    {
        double t1 = App.NowMs();
        AwaitFrame(t0, 500, ms => { var d = Info(); d["renderMs"] = ms; d["callMs"] = _callMs; done(d); });
        switch (_park) {
            case "swhide": ShowWindow(Hwnd, 4 /* SW_SHOWNOACTIVATE */); break;
            case "offscreen": SetWindowPos(Hwnd, IntPtr.Zero, Spec.X, Spec.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); break;
            case "cloak": { int v = 0; DwmSetWindowAttribute(Hwnd, 13 /* DWMWA_CLOAK */, ref v, 4); break; }
            default: Win.AppWindow.Show(false); break;
        }
        SetWindowPos(Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Strip();
        _parked = false;
        _callMs = App.NowMs() - t1;
    }

    public Dictionary<string, object?> Park(string mode)
    {
        double t1 = App.NowMs();
        _park = mode;
        switch (mode) {
            case "swhide": ShowWindow(Hwnd, 0 /* SW_HIDE */); break;
            case "offscreen": SetWindowPos(Hwnd, IntPtr.Zero, -32000, -32000, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); break;
            case "cloak": { int v = 1; DwmSetWindowAttribute(Hwnd, 13 /* DWMWA_CLOAK */, ref v, 4); break; }
            default: Win.AppWindow.Hide(); break;
        }
        _parked = true;
        var d = Info(); d["callMs"] = App.NowMs() - t1;
        return d;
    }

    public void Resize(double t0, int w, int h, Action<Dictionary<string, object?>> done)
    {
        AwaitFrame(t0, 1000, ms => { var d = Info(); d["renderMs"] = ms; done(d); });
        Win.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(Spec.X, Spec.Y, w, h));
        Strip();
        if (Spec.Hit == "region") ApplyRegion();
        if (Spec.Hit == "ncregion") ApplyNcRegion();
    }

    public void Close()
    {
        _poll?.Stop();
        Win.Close();
    }

    // ------------------------------------------------------------------ click-through
    void ApplyHit()
    {
        _children.Clear();
        foreach (var c in Children(Hwnd)) _children.Add($"{ClassOf(c)}{(OnThisThread(c) ? "" : " (other thread)")}");
        switch (Spec.Hit) {
            case "nchittest":
                _hook.HitTest = true;
                foreach (var c in Children(Hwnd))
                    if (OnThisThread(c)) Hook(c, new HookState { HitTest = true, InShape = InShape });
                break;
            case "layered": {
                SetExStyle(Hwnd, GetExStyle(Hwnd) | WS_EX_LAYERED);
                bool ok = SetLayeredWindowAttributes(Hwnd, 0, 255, 2 /* LWA_ALPHA */);
                if (!ok) _notes.Add("SetLayeredWindowAttributes FAILED");
                _poll = Win.DispatcherQueue.CreateTimer();
                _poll.Interval = TimeSpan.FromMilliseconds(16); _poll.IsRepeating = true;
                _poll.Tick += (_, _) => Poll();
                Poll();
                _poll.Start();
                break;
            }
            case "region": ApplyRegion(); break;
            case "ncregion": ApplyNcRegion(); break;
        }
    }

    void Poll()
    {
        if (_parked || !GetCursorPos(out var pt)) return;
        bool inside = InShape(pt.X, pt.Y);
        if (inside == _inside) return;
        _inside = inside;
        long ex = GetExStyle(Hwnd);
        SetExStyle(Hwnd, inside ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT);
        App.AddEvent(new { win = Id, kind = "toggle", inside, t = App.NowMs() });
    }

    void ApplyRegion()
    {
        var s = ShapeRect();
        GetWindowRect(Hwnd, out var wr);
        int l = s.x - wr.Left - 1, t = s.y - wr.Top - 1;
        // CreateRoundRectRgn leaves out its right and bottom edge: +1 on both, +1 px all round for the AA edge
        IntPtr rgn = CreateRoundRectRgn(l, t, l + s.w + 3, t + s.h + 3, 2 * s.r + 2, 2 * s.r + 2);
        int ok = SetWindowRgn(Hwnd, rgn, true);
        if (ok == 0) _notes.Add("SetWindowRgn FAILED");
    }

    void ApplyNcRegion()
    {
        try {
            var s = ShapeRect();
            var o = new POINT(); ClientToScreen(Hwnd, ref o);
            GetClientRect(Hwnd, out var cr);
            int sx = s.x - o.X, sy = s.y - o.Y;
            var rects = new[] {
                new Windows.Graphics.RectInt32(0, 0, cr.Right, sy),                                   // top margin
                new Windows.Graphics.RectInt32(0, sy + s.h, cr.Right, cr.Bottom - sy - s.h),          // bottom
                new Windows.Graphics.RectInt32(0, sy, sx, s.h),                                       // left
                new Windows.Graphics.RectInt32(sx + s.w, sy, cr.Right - sx - s.w, s.h),               // right
            };
            InputNonClientPointerSource.GetForWindowId(Win.AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, rects);
            _notes.Add("ncregion: Passthrough set on 4 margin rects");
        } catch (Exception ex) { _notes.Add("ncregion threw: " + ex.Message); }
    }

    // ------------------------------------------------------------------ state
    public Dictionary<string, object?> Info()
    {
        GetWindowRect(Hwnd, out var wr);
        GetClientRect(Hwnd, out var cr);
        var o = new POINT(); ClientToScreen(Hwnd, ref o);
        DwmGetRect(Hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var fr, 16);
        DwmGetInt(Hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, 4);
        GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
        var s = ShapeRect();
        return new Dictionary<string, object?> {
            ["ok"] = true, ["id"] = Id, ["hwnd"] = Hwnd.ToInt64(), ["dpi"] = GetDpiForWindow(Hwnd),
            ["frame"] = Spec.Frame, ["hit"] = Spec.Hit, ["park"] = _park, ["parked"] = _parked,
            ["style"] = "0x" + GetStyle(Hwnd).ToString("X8"), ["exStyle"] = "0x" + GetExStyle(Hwnd).ToString("X8"),
            ["window"] = new { x = wr.Left, y = wr.Top, w = wr.Right - wr.Left, h = wr.Bottom - wr.Top },
            ["client"] = new { x = o.X, y = o.Y, w = cr.Right, h = cr.Bottom },
            ["dwmFrame"] = new { x = fr.Left, y = fr.Top, w = fr.Right - fr.Left, h = fr.Bottom - fr.Top },
            ["shape"] = new { x = s.x, y = s.y, w = s.w, h = s.h, r = s.r },
            ["visible"] = IsWindowVisible(Hwnd), ["cloaked"] = cloaked,
            ["fill"] = new int[] { Fill.A, Fill.R, Fill.G, Fill.B }, ["edge"] = new int[] { Edge.A, Edge.R, Edge.G, Edge.B },
            ["foregroundIsMine"] = fgPid == (uint)Environment.ProcessId,
            ["children"] = _children.ToArray(), ["notes"] = _notes.ToArray(),
            ["hitTransparent"] = _hook.HitTransparent, ["moves"] = Moves,
        };
    }
}
