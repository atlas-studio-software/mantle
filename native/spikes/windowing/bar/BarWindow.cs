// BarWindow.cs - the bar: geometry, placement, the hover reveal and its motion, staying on top, the
// fullscreen block, the dock app bar and the display/DPI re-place. Ported from main.js (barGeometry,
// hoverTick, revealBar/revealByPage, beginHide, raiseBar, placeBottomStrip + the --dock app bar,
// displayBurst), lib/bar-intent.js (the keep zones), ui/bar/bar.css (the page's reveal) and the
// sidecar (Morph.cs, TrayWatch.cs, Foreground.cs, Taskbar.cs).
//
// Copy for later lanes: Compute (geometry), Reveal/BeginHide/AnimateComp (motion), Tick/Zone (hover),
// Raise/CheckOnTop/ArmWatch (topmost), ReserveAndPlace (dock), SubProc + DisplayChanged (DPI/display).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace BarSpike;

sealed class BarWindow
{
    // ---- main.js / bar.css / lib/bar-intent.js, DIP and ms
    const double BarH = 48, Lift = 14, SideGapTotal = 28, MaxW = 1120, BandDip = 4;
    const double KeepAbove = 8, KeepSide = 8, OverAbove = 28, OverSide = 12;
    const int PollHiddenMs = 80, PollShownMs = 60, HideGraceMs = 180, OvershootMs = 500;
    const int RevealMs = 560, HideMs = 260;      // the page's motion: 560 in (spring), 260 out (accelerate)
    const int SwpHideMs = 320;                   // --os-morph: the window walked 560 in, 320 out
    static readonly double[] RevealEase = { 0.22, 0.68, 0.30, 1 };   // --os-morph's curve
    const double DownY = BarH * 0.75, DownS = 0.94, FadeK = 0.4;     // bar.css: the down pose; the fade is the first 40 %
    // ui/shared/glass-light.css --m-spring (CSS linear(), 41 evenly spaced points)
    static readonly float[] Spring = { 0f, 0.0208f, 0.0739f, 0.1478f, 0.2337f, 0.3245f, 0.4153f, 0.5025f, 0.5838f, 0.6577f, 0.7235f,
        0.7808f, 0.8301f, 0.8716f, 0.906f, 0.9341f, 0.9566f, 0.9742f, 0.9876f, 0.9977f, 1.0049f, 1.0098f, 1.0128f, 1.0145f,
        1.0151f, 1.015f, 1.0143f, 1.0132f, 1.012f, 1.0106f, 1.0092f, 1.0079f, 1.0066f, 1.0055f, 1.0044f, 1.0035f, 1.0028f,
        1.0021f, 1.0015f, 1.0011f, 1f };

    struct Geo
    {
        public uint Dpi; public double S, PillWdip; public int BandTop;
        public W.RECT Mon, Win, Pill, Start;
    }

    readonly Opts _o;
    readonly string _restored;
    readonly Window _win;
    readonly IntPtr _hwnd;
    readonly DispatcherQueue _dq;
    readonly Grid _root;
    readonly Border _pill;
    readonly Visual _vis;
    readonly Compositor _c;
    readonly bool _dock, _comp;
    readonly Windows.UI.Color _fill, _ink;
    readonly int _taskbarOriginal;
    readonly string _taskbarHow;
    readonly string _styleBefore, _styleAfter;
    readonly long _t0 = Log.Now();
    Geo _g;
    string _appbarHow = "";
    long _appbarAt;
    DispatcherQueueTimer? _poll, _burst;
    bool _burstPending; string _burstWhy = "";
    bool _revealed, _hiding, _gaveUp;
    long _leftAt = -1, _outAt = -1, _revealT0, _hideT0;
    int _animSeq, _morphSeq, _reveals;
    string? _lastBlock;
    (int hr, W.DWM_TIMING_INFO t) _dwm0, _dwmH0;
    List<double>? _xf;
    readonly List<long> _raiseTimes = new();
    W.WinEventProc? _hookProc;
    IntPtr _hookShow, _hookFg, _hookTray;
    SUBCLASSPROC? _sub;

    internal BarWindow(Opts o, string restored)
    {
        _o = o; _restored = restored;
        _dock = o.Mode == "dock";
        _comp = !_dock && o.Anim == "comp";
        bool dark = o.Theme != "light";
        _fill = dark ? Windows.UI.Color.FromArgb(255, 32, 35, 40) : Windows.UI.Color.FromArgb(255, 246, 247, 249);
        _ink = dark ? Windows.UI.Color.FromArgb(255, 236, 238, 241) : Windows.UI.Color.FromArgb(255, 24, 26, 30);

        // ---- the pill: 48 DIP, radius 18 (dark) / 24 (light), a 20 DIP marker square (the DPI check
        // measures it) and a line of text (crispness, by eye in the screenshots)
        var marker = new Border { Width = 20, Height = 20, Background = new SolidColorBrush(_ink), Margin = new Thickness(16, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = "Mantle   12:34 PM", FontSize = 13, Foreground = new SolidColorBrush(_ink), Margin = new Thickness(0, 0, 20, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var inner = new Grid();
        inner.Children.Add(marker); inner.Children.Add(text);
        _pill = new Border {
            Height = BarH, CornerRadius = new CornerRadius(dark ? 18 : 24), Background = new SolidColorBrush(_fill),
            BorderBrush = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0)),
            BorderThickness = new Thickness(1), Child = inner,
            HorizontalAlignment = _dock ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = _dock ? VerticalAlignment.Center : VerticalAlignment.Top,
        };
        _root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        _root.Children.Add(_pill);
        _root.Loaded += (_, _) => _root.XamlRoot.Changed += (s, _) =>
            Log.Ev("xamlRoot", new { scale = s.RasterizationScale, w = s.Size.Width, h = s.Size.Height });

        _win = new Window { Content = _root, SystemBackdrop = new TransparentBackdrop() };
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        _dq = _win.DispatcherQueue;

        // ---- the window (the smoke app's see-through setup) and a plain WS_POPUP frame
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        int pref = 1 /* DWMWCP_DONOTROUND */; W.DwmSetWindowAttribute(_hwnd, 33, ref pref, 4);
        int none = unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */; W.DwmSetWindowAttribute(_hwnd, 34, ref none, 4);
        int noTrans = 1; W.DwmSetWindowAttribute(_hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref noTrans, 4);   // no DWM show/hide animation of its own
        SetEx(0x80 /* WS_EX_TOOLWINDOW */);
        Log.Line("see-through: " + TransparentBackdrop.MakeSeeThrough(_hwnd));
        long st = W.GetWindowLongPtrW(_hwnd, -16).ToInt64();
        _styleBefore = Hex(st);
        // WS_CAPTION (BORDER|DLGFRAME), THICKFRAME, SYSMENU, MIN/MAXIMIZEBOX off; WS_POPUP on: the presenter
        // leaves WS_DLGFRAME|WS_SYSMENU, which drew a 3 px frame ring in the smoke run
        st &= ~(0x00C00000L | 0x00040000L | 0x00080000L | 0x00020000L | 0x00010000L);
        st |= 0x80000000L;
        W.SetWindowLongPtrW(_hwnd, -16, new IntPtr(st));
        W.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOZORDER | W.SWP_NOACTIVATE | W.SWP_FRAMECHANGED);
        _styleAfter = Hex(W.GetWindowLongPtrW(_hwnd, -16).ToInt64());
        _sub = SubProc;
        SetWindowSubclass(_hwnd, _sub, new UIntPtr(2), IntPtr.Zero);

        // ---- the compositor's handle on the pill: Translation + Scale (about the bottom centre) + Opacity
        _vis = ElementCompositionPreview.GetElementVisual(_pill);
        _c = _vis.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(_pill, true);
        _pill.SizeChanged += (_, e) => _vis.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height, 0);
        if (_comp) Pose(up: false);

        // ---- Windows' taskbar: the original goes on disk first, then OS auto-hide (never SW_HIDE)
        _taskbarOriginal = Taskbar.State();
        bool change = (_taskbarOriginal & Taskbar.ABS_AUTOHIDE) == 0;
        ShellState.RecordTaskbar(_taskbarOriginal, change);
        _taskbarHow = Taskbar.AutoHide(_taskbarOriginal);
        IntPtr tray = W.FindWindowW("Shell_TrayWnd", null);
        Log.Ev("taskbar", new { original = _taskbarOriginal, how = _taskbarHow, now = Taskbar.State(), trayVisible = W.IsWindowVisible(tray) });

        // ---- first place, then show once (WinUI needs Activate to start drawing), then give the focus back
        IntPtr prevFg = W.GetForegroundWindow();
        if (_dock) ReserveAndPlace("start");
        else PlaceHover("start");
        _win.Activate();
        SetEx(0x08000000 /* WS_EX_NOACTIVATE */);
        var r0 = _dock || _comp ? _g.Win : _g.Start;
        W.SetWindowPos(_hwnd, W.HWND_TOPMOST, r0.L, r0.T, r0.Wd, r0.Ht, W.SWP_NOACTIVATE);
        if (prevFg != IntPtr.Zero && prevFg != _hwnd) W.SetForegroundWindow(prevFg);
        CompositionTarget.Rendering += OnFirstFrame;
    }

    // ================================================================== geometry
    /// The pill on the PRIMARY monitor (Electron: screen.getPrimaryDisplay()), in physical px:
    /// min(1120, screen - 28) DIP wide, 48 tall, 14 above the bottom edge. The window is the pill plus
    /// the lift under it, its bottom computed from the screen edge (never a rounded height).
    Geo Compute()
    {
        var (mon, _, dpi) = W.Primary();
        double s = dpi / 96.0;
        double wDip = Math.Min(MaxW, Math.Max(120, mon.Wd / s - SideGapTotal));
        int pw = (int)Math.Round(wDip * s), ph = (int)Math.Round(BarH * s), lift = (int)Math.Round(Lift * s);
        int px = mon.L + (mon.Wd - pw) / 2;
        var g = new Geo { Dpi = dpi, S = s, Mon = mon, PillWdip = wDip, BandTop = mon.B - (int)Math.Round(BandDip * s) };
        g.Pill = new W.RECT(px, mon.B - lift - ph, px + pw, mon.B - lift);
        g.Win = new W.RECT(px, g.Pill.T, px + pw, mon.B);
        g.Start = new W.RECT(px, mon.B, px + pw, mon.B + g.Win.Ht);   // swp: parked just past the edge
        return g;
    }

    void PlaceHover(string why)
    {
        Interlocked.Increment(ref _morphSeq);   // a walk in flight would fight the new place
        _g = Compute();
        var r = _comp || (_revealed && !_hiding) ? _g.Win : _g.Start;
        W.SetWindowPos(_hwnd, W.HWND_TOPMOST, r.L, r.T, r.Wd, r.Ht, W.SWP_NOACTIVATE);
        EvPlace(why, r);
    }

    /// --dock: a real app bar, BarH DIP in physical px. The window goes where Explorer says.
    void ReserveAndPlace(string why)
    {
        _g = Compute();
        int size = (int)Math.Round(BarH * _g.S);
        if (!AppBar.Registered) ShellState.RecordAppBar(_hwnd.ToInt64());   // on disk before ABM_NEW
        var rc = AppBar.Reserve(_hwnd, _g.Mon, size, out _appbarHow);
        _appbarAt = Log.Now();
        if (rc is W.RECT r) {
            int ph = _g.Pill.Ht, top = r.T + (r.Ht - ph) / 2;
            _g.Win = r;
            _g.Pill = new W.RECT(_g.Pill.L, top, _g.Pill.R, top + ph);
            W.SetWindowPos(_hwnd, W.HWND_TOPMOST, r.L, r.T, r.Wd, r.Ht, W.SWP_NOACTIVATE);
        }
        _pill.Width = _g.PillWdip;
        EvPlace(why, _g.Win);
    }

    void EvPlace(string why, W.RECT r)
    {
        W.GetWindowRect(_hwnd, out var read);
        Log.Ev("place", new {
            why, dpi = _g.Dpi, scale = _g.S, mon = _g.Mon.J, pill = _g.Pill.J, win = r.J, read = read.J, pillWdip = _g.PillWdip,
            bandTop = _g.BandTop, xamlScale = _root.XamlRoot?.RasterizationScale ?? 0, appbar = _dock ? _appbarHow : null,
            workArea = W.WorkArea().J, style = Hex(W.GetWindowLongPtrW(_hwnd, -16).ToInt64()), visible = W.IsWindowVisible(_hwnd),
        });
    }

    W.RECT MarkerRect()
    {
        double s = _g.S;
        int x = _g.Pill.L + (int)Math.Round(17 * s), y = _g.Pill.T + (int)Math.Round(14 * s), n = (int)Math.Round(20 * s);
        return new W.RECT(x, y, x + n, y + n);
    }

    // ================================================================== start and stop
    void OnFirstFrame(object? s, object e)
    {
        CompositionTarget.Rendering -= OnFirstFrame;
        long firstFrameMs = Log.Now() - _t0;
        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(400); t.IsRepeating = false;
        t.Tick += (_, _) => AfterFirstFrame(firstFrameMs);
        t.Start();
    }

    void AfterFirstFrame(long firstFrameMs)
    {
        if (!_dock) {
            // Hidden at rest: a hidden window draws nothing, takes no clicks and costs nothing. (Our OWN
            // window. Windows' taskbar is never hidden, only put on auto-hide.)
            W.ShowWindow(_hwnd, 0 /* SW_HIDE */);
            _poll = _dq.CreateTimer();
            _poll.IsRepeating = false;
            _poll.Tick += (_, _) => Tick();
            Schedule();
        } else {
            // main.js: Explorer answers QUERYPOS against the bars it knows at that instant, so ask again
            // once things settle and re-place unconditionally.
            var t = _dq.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(300); t.IsRepeating = false;
            t.Tick += (_, _) => ReserveAndPlace("300 ms after start: re-asked, re-placed unconditionally");
            t.Start();
        }
        W.GetWindowRect(_hwnd, out var read);
        Log.Json("ready.json", new {
            tag = _o.Tag, mode = _o.Mode, anim = _o.Anim, theme = _o.Theme, pid = Environment.ProcessId, hwnd = _hwnd.ToInt64(),
            dpi = _g.Dpi, scale = _g.S, styleBefore = _styleBefore, styleAfter = _styleAfter,
            exStyle = Hex(W.GetWindowLongPtrW(_hwnd, -20).ToInt64()),
            mon = _g.Mon.J, win = _g.Win.J, pill = _g.Pill.J, start = _g.Start.J, bandTop = _g.BandTop, pillWdip = _g.PillWdip,
            probeX = _g.Pill.L + (int)(_g.Pill.Wd * 0.3), marker = MarkerRect().J,
            fill = new[] { _fill.R, _fill.G, _fill.B }, ink = new[] { _ink.R, _ink.G, _ink.B },
            visible = W.IsWindowVisible(_hwnd), read = read.J, firstFrameMs,
            taskbar = new { original = _taskbarOriginal, how = _taskbarHow, now = Taskbar.State() },
            appbar = _dock ? _appbarHow : null, workArea = W.WorkArea().J, restored = _restored,
            xamlScale = _root.XamlRoot?.RasterizationScale ?? 0,
        });
    }

    void Quit(string why)
    {
        _poll?.Stop();
        DisarmWatch();
        string ab = "no app bar was registered";
        if (AppBar.Registered) { var wa = W.WorkArea(); AppBar.Remove(_hwnd); ab = $"ABM_REMOVE: work area {wa} -> {W.WorkArea()}"; }
        string tb = $"taskbar left as it was (original state {_taskbarOriginal} already had auto-hide)";
        if ((_taskbarOriginal & Taskbar.ABS_AUTOHIDE) == 0) tb = $"taskbar state back to {Taskbar.Set(_taskbarOriginal)} (original {_taskbarOriginal})";
        ShellState.Clear();
        Log.Ev("exit", new { why, appbar = ab, taskbar = tb });
        Log.Json("exit.json", new { why, appbar = ab, taskbar = tb, taskbarState = Taskbar.State(), workArea = W.WorkArea().J });
        Environment.Exit(0);
    }

    // ================================================================== display and DPI changes
    IntPtr SubProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        switch (msg) {
            case 0x0010:   // WM_CLOSE: the clean quit
                _dq.TryEnqueue(() => Quit("WM_CLOSE"));
                return IntPtr.Zero;
            case 0x02E0: { // WM_DPICHANGED: WinUI's own handler first (its rasterization scale), then our geometry
                IntPtr res = DefSubclassProc(h, msg, wp, lp);
                long dpi = wp.ToInt64() & 0xFFFF;
                _dq.TryEnqueue(() => DisplayChanged($"WM_DPICHANGED {dpi}"));
                return res;
            }
            case 0x007E: { // WM_DISPLAYCHANGE (resolution, a monitor added or removed)
                long v = lp.ToInt64();
                _dq.TryEnqueue(() => DisplayChanged($"WM_DISPLAYCHANGE {v & 0xFFFF}x{(v >> 16) & 0xFFFF}"));
                break;
            }
            case AppBar.CallbackMsg:
                if (wp.ToInt64() == 1 /* ABN_POSCHANGED */ && Log.Now() - _appbarAt > 250) _dq.TryEnqueue(() => DisplayChanged("ABN_POSCHANGED"));
                else if (wp.ToInt64() == 2 /* ABN_FULLSCREENAPP */) Log.Ev("abnFullscreen", new { open = lp.ToInt64() != 0 });
                return IntPtr.Zero;
        }
        return DefSubclassProc(h, msg, wp, lp);
    }

    /// main.js displayBurst (makeBurst quietMs 400): the first change re-places at once, the rest of a
    /// burst become one more re-place when it is over.
    void DisplayChanged(string why)
    {
        if (_burst != null && _burst.IsRunning) {
            _burstPending = true; _burstWhy = why;
            _burst.Stop(); _burst.Start();
            return;
        }
        Replace(why);
        if (_burst == null) {
            _burst = _dq.CreateTimer();
            _burst.Interval = TimeSpan.FromMilliseconds(400); _burst.IsRepeating = false;
            _burst.Tick += (_, _) => { if (_burstPending) { _burstPending = false; Replace(_burstWhy + " (end of a burst)"); } };
        }
        _burst.Start();
    }

    void Replace(string why) { if (_dock) ReserveAndPlace(why); else PlaceHover(why); }

    // ================================================================== the hover poll
    void Schedule()
    {
        if (_poll == null) return;
        _poll.Interval = TimeSpan.FromMilliseconds(_revealed ? PollShownMs : PollHiddenMs);
        _poll.Start();
    }

    /// One cursor read per tick (80 ms hidden, 60 ms shown); nothing else while hidden unless the
    /// cursor is in the band.
    void Tick()
    {
        W.GetCursorPos(out var pt);
        var g = _g;
        bool inBand = pt.X >= g.Mon.L && pt.X < g.Mon.R && pt.Y >= g.BandTop && pt.Y < g.Mon.B;
        long now = Log.Now();
        if (!_revealed) {
            if (inBand) {
                string? block = Foreground.Blocks(now);
                if (block != null) { if (block != _lastBlock) Log.Ev("blocked", new { reason = block, cursor = new { x = pt.X, y = pt.Y } }); }
                else Reveal("the cursor reached the bottom edge", pt);
                _lastBlock = block;
            } else _lastBlock = null;
            Schedule();
            return;
        }
        string zone = Zone(pt, inBand);
        if (zone == "in") {
            _leftAt = _outAt = -1;
            if (_hiding) Reveal("the cursor came back before the bar finished sliding away", pt);
            else CheckOnTop("poll");
        } else if (!_hiding) {
            // lib/bar-intent.js shownStep: out of the overshoot band for the grace, or out of the rest
            // band (in the overshoot band) for OvershootMs
            if (_leftAt < 0) _leftAt = now;
            if (zone == "out") { if (_outAt < 0) _outAt = now; } else _outAt = -1;
            bool hide = (_outAt >= 0 && now - _outAt >= HideGraceMs) || now - _leftAt >= Math.Max(HideGraceMs, OvershootMs);
            if (hide) BeginHide(_outAt >= 0 ? $"the cursor has been away from the bar for {HideGraceMs} ms" : $"the cursor rested in the overshoot band for {OvershootMs} ms");
            else CheckOnTop("poll");
        }
        Schedule();
    }

    /// lib/bar-intent.js zone, physical px: 'in' = the band, the window, or the pill's rest rect grown
    /// 8 DIP up and sideways; 'overshoot' = grown 28 up / 12 sideways; else 'out'.
    string Zone(W.POINT pt, bool inBand)
    {
        if (inBand) return "in";
        var g = _g; double s = g.S; var rest = g.Win;
        if (rest.Contains(pt.X, pt.Y)) return "in";
        W.RECT Grow(double above, double side) => new((int)Math.Round(rest.L - side * s), (int)Math.Round(rest.T - above * s), (int)Math.Round(rest.R + side * s), rest.B);
        if (Grow(KeepAbove, KeepSide).Contains(pt.X, pt.Y)) return "in";
        if (Grow(OverAbove, OverSide).Contains(pt.X, pt.Y)) return "overshoot";
        return "out";
    }

    // ================================================================== reveal and hide
    void Reveal(string why, W.POINT pt)
    {
        bool wasHiding = _hiding;
        _hiding = false; _leftAt = _outAt = -1;
        _revealed = true; _reveals++;
        var old = _g;
        _g = Compute();   // every reveal re-reads the geometry (a change while hidden is not missed)
        if (old.Dpi != _g.Dpi || !old.Pill.Equals(_g.Pill)) EvPlace("reveal: the geometry changed while hidden", _g.Win);
        _revealT0 = Log.Now(); _dwm0 = W.Timing();
        bool trace = _reveals <= _o.TraceFrames;
        Log.Ev("reveal", new { why, anim = _o.Anim, from = wasHiding ? "mid-hide" : "hidden", cursor = new { x = pt.X, y = pt.Y }, win = _g.Win.J, pill = _g.Pill.J, dpi = _g.Dpi });
        if (_comp) {
            W.SetWindowPos(_hwnd, W.HWND_TOPMOST, _g.Win.L, _g.Win.T, _g.Win.Wd, _g.Win.Ht, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            AnimateComp(true, trace);
        } else {
            W.RECT from = _g.Start;
            if (wasHiding && W.GetWindowRect(_hwnd, out var cur)) from = cur;
            W.SetWindowPos(_hwnd, W.HWND_TOPMOST, from.L, from.T, from.Wd, from.Ht, W.SWP_NOACTIVATE | W.SWP_SHOWWINDOW);
            Morph(from, _g.Win, RevealMs, up: true);
        }
        Raise("reveal: " + why);
        ArmWatch();
    }

    void BeginHide(string why)
    {
        if (!_revealed || _hiding) return;
        _hiding = true; _hideT0 = Log.Now(); _dwmH0 = W.Timing();
        Log.Ev("hide", new { why, anim = _o.Anim });
        if (_comp) AnimateComp(false, _reveals <= _o.TraceFrames);
        else { W.GetWindowRect(_hwnd, out var cur); Morph(cur, _g.Start, SwpHideMs, up: false); }
    }

    void Pose(bool up)
    {
        _vis.Properties.InsertVector3("Translation", up ? Vector3.Zero : new Vector3(0, (float)DownY, 0));
        _vis.Scale = up ? Vector3.One : new Vector3((float)DownS, (float)DownS, 1);
        _vis.Opacity = up ? 1 : 0;
    }

    /// (b) THE COMPOSITOR MOVES IT: the window is already where the pill rests and never moves; the
    /// pill's visual gets bar.css's motion. In: Translation 36 -> 0 DIP and Scale .94 -> 1 on the
    /// spring over 560 ms, Opacity 0 -> 1 over the first 40 %. Out: back to the down pose over 260 ms,
    /// accelerating. Every key frame starts from this.StartingValue, so a reversal mid-way turns round
    /// from wherever the pill is (a CSS transition's retarget).
    void AnimateComp(bool up, bool trace)
    {
        int seq = ++_animSeq;
        var lin = _c.CreateLinearEasingFunction();
        var batch = _c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var tr = _c.CreateVector3KeyFrameAnimation();
        var sc = _c.CreateVector3KeyFrameAnimation();
        var op = _c.CreateScalarKeyFrameAnimation();
        if (up) {
            tr.Duration = sc.Duration = TimeSpan.FromMilliseconds(RevealMs);
            op.Duration = TimeSpan.FromMilliseconds(RevealMs * FadeK);
            for (int i = 0; i < Spring.Length; i++) {
                float t = i / (float)(Spring.Length - 1);
                string k = F(Spring[i]);
                tr.InsertExpressionKeyFrame(t, $"this.StartingValue + (Vector3(0, 0, 0) - this.StartingValue) * {k}", lin);
                sc.InsertExpressionKeyFrame(t, $"this.StartingValue + (Vector3(1, 1, 1) - this.StartingValue) * {k}", lin);
            }
            op.InsertExpressionKeyFrame(0f, "this.StartingValue");
            op.InsertKeyFrame(1f, 1f, _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
        } else {
            var accel = _c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.8f, 0.15f));
            tr.Duration = sc.Duration = op.Duration = TimeSpan.FromMilliseconds(HideMs);
            tr.InsertExpressionKeyFrame(0f, "this.StartingValue");
            tr.InsertKeyFrame(1f, new Vector3(0, (float)DownY, 0), accel);
            sc.InsertExpressionKeyFrame(0f, "this.StartingValue");
            sc.InsertKeyFrame(1f, new Vector3((float)DownS, (float)DownS, 1), accel);
            op.InsertExpressionKeyFrame(0f, "this.StartingValue");
            op.InsertKeyFrame(1f, 0f, _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
        }
        _vis.StartAnimation("Translation", tr);
        _vis.StartAnimation("Scale", sc);
        _vis.StartAnimation("Opacity", op);
        batch.End();
        if (trace) StartFrameTrace();
        batch.Completed += (_, _) => _dq.TryEnqueue(() => { if (seq == _animSeq) AnimDone(up, -1, null); });
    }

    /// (a) THE WINDOW MOVES: SetWindowPos per frame on its own thread (sidecar/Morph.cs): the curve is
    /// sampled from the clock, the last frame is the exact target, and the thread sleeps to the next
    /// 16.7 ms boundary (swp) or blocks in DwmFlush until the next composition (swpflush).
    void Morph(W.RECT from, W.RECT to, int ms, bool up)
    {
        int seq = Interlocked.Increment(ref _morphSeq);
        bool flush = _o.Anim == "swpflush";
        IntPtr h = _hwnd;
        var th = new Thread(() => {
            var sw = Stopwatch.StartNew();
            var times = new List<double>(64);
            int frames = 0;
            while (true) {
                if (Volatile.Read(ref _morphSeq) != seq) return;
                double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
                double e = t >= 1 ? 1 : Bezier(t, RevealEase);
                int x = Lerp(from.L, to.L, e), y = Lerp(from.T, to.T, e), w = Lerp(from.Wd, to.Wd, e), hh = Lerp(from.Ht, to.Ht, e);
                if (t >= 1) { x = to.L; y = to.T; w = to.Wd; hh = to.Ht; }
                W.SetWindowPos(h, IntPtr.Zero, x, y, w, hh, W.SWP_NOZORDER | W.SWP_NOACTIVATE);
                frames++; times.Add(sw.Elapsed.TotalMilliseconds);
                if (t >= 1) break;
                if (flush) W.DwmFlush();
                else {
                    double wait = frames * (1000.0 / 60) - sw.Elapsed.TotalMilliseconds;
                    if (wait > 0) Thread.Sleep((int)Math.Ceiling(wait));
                }
            }
            _dq.TryEnqueue(() => { if (Volatile.Read(ref _morphSeq) == seq) AnimDone(up, frames, times); });
        }) { IsBackground = true, Name = "morph" };
        th.Start();
    }

    void AnimDone(bool up, int frames, List<double>? swpTimes)
    {
        long ms = Log.Now() - (up ? _revealT0 : _hideT0);
        var d1 = W.Timing();
        var xf = StopFrameTrace();
        Log.Ev(up ? "revealDone" : "hideDone", new {
            anim = _o.Anim, ms, swp = Gaps(swpTimes), xaml = Gaps(xf), dwm = Dwm(up ? _dwm0 : _dwmH0, d1),
        });
        if (!up) {
            W.ShowWindow(_hwnd, 0 /* SW_HIDE */);
            _revealed = false; _hiding = false;
            DisarmWatch();
            Log.Ev("hidden", new { visible = W.IsWindowVisible(_hwnd) });
        }
    }

    void StartFrameTrace() { if (_xf != null) return; _xf = new List<double>(64); CompositionTarget.Rendering += OnTraceFrame; }
    void OnTraceFrame(object? s, object e) { if (e is RenderingEventArgs r) _xf?.Add(r.RenderingTime.TotalMilliseconds); }
    List<double>? StopFrameTrace()
    {
        if (_xf == null) return null;
        CompositionTarget.Rendering -= OnTraceFrame;
        var x = _xf; _xf = null; return x;
    }

    // ================================================================== staying on top
    IntPtr Over()
    {
        var c = new W.POINT { X = (_g.Pill.L + _g.Pill.R) / 2, Y = (_g.Pill.T + _g.Pill.B) / 2 };
        IntPtr w = W.WindowFromPoint(c);
        return w == IntPtr.Zero ? w : W.GetAncestor(w, 2 /* GA_ROOT */);
    }
    static string Describe(IntPtr h) => h == IntPtr.Zero ? "nothing" : $"{W.ClassOf(h)} (pid {W.PidOf(h)})";

    /// main.js raiseBar: HWND_TOPMOST only puts us IN the topmost band; prove we are first at the
    /// pill's centre (WindowFromPoint), once more if not.
    void Raise(string why)
    {
        W.SetWindowPos(_hwnd, W.HWND_TOPMOST, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE);
        IntPtr over = Over();
        if (over != _hwnd) { W.SetWindowPos(_hwnd, W.HWND_TOPMOST, 0, 0, 0, 0, W.SWP_NOMOVE | W.SWP_NOSIZE | W.SWP_NOACTIVATE); over = Over(); }
        _raiseTimes.Add(Log.Now());
        Log.Ev("raise", new { why, onTop = over == _hwnd, over = Describe(over) });
    }

    /// While revealed: something new over the pill (a later topmost window, the Windows taskbar
    /// popping) is answered with a raise, at most 20 in 5 s so two always-on-top apps cannot fight forever.
    void CheckOnTop(string why)
    {
        if (!_revealed || _hiding) return;
        IntPtr over = Over();
        if (over == _hwnd || over == IntPtr.Zero) return;
        long now = Log.Now();
        _raiseTimes.RemoveAll(t => now - t > 5000);
        if (_raiseTimes.Count >= 20) {
            if (!_gaveUp) { _gaveUp = true; Log.Ev("raiseGaveUp", new { over = Describe(over), raisesIn5s = _raiseTimes.Count }); }
            return;
        }
        _gaveUp = false;
        Raise($"{why}: covered by {Describe(over)}");
    }

    /// Armed only while revealed (hidden = no hooks): any window shown or brought to the foreground,
    /// and the Windows taskbar's own thread (TrayWatch.cs), trigger a CheckOnTop.
    void ArmWatch()
    {
        if (_hookShow != IntPtr.Zero) return;
        _hookProc ??= OnWinEvent;
        const uint OUT_OF_CONTEXT_SKIP_OWN = 0x0 | 0x2;
        _hookShow = W.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _hookProc, 0, 0, OUT_OF_CONTEXT_SKIP_OWN);   // EVENT_OBJECT_SHOW
        _hookFg = W.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _hookProc, 0, 0, OUT_OF_CONTEXT_SKIP_OWN);     // EVENT_SYSTEM_FOREGROUND
        IntPtr tray = W.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) {
            uint tid = W.GetWindowThreadProcessId(tray, out uint tpid);
            _hookTray = W.SetWinEventHook(0x8002, 0x800B, IntPtr.Zero, _hookProc, tpid, tid, OUT_OF_CONTEXT_SKIP_OWN);   // SHOW..LOCATIONCHANGE
        }
    }
    void DisarmWatch()
    {
        foreach (var h in new[] { _hookShow, _hookFg, _hookTray }) if (h != IntPtr.Zero) W.UnhookWinEvent(h);
        _hookShow = _hookFg = _hookTray = IntPtr.Zero;
    }
    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || !_revealed || _hiding) return;   // OBJID_WINDOW only
        bool trayHook = hook == _hookTray;
        if (!trayHook && W.GetAncestor(hwnd, 2) != hwnd) return;                      // top-level windows only
        string src = trayHook ? "the Windows taskbar showed or moved" : ev == 3 ? "a window came to the foreground" : "a window was shown";
        _dq.TryEnqueue(DispatcherQueuePriority.Low, () => CheckOnTop("watch: " + src));
    }

    // ================================================================== small things
    void SetEx(long bits) => W.SetWindowLongPtrW(_hwnd, -20, new IntPtr(W.GetWindowLongPtrW(_hwnd, -20).ToInt64() | bits));
    static string Hex(long v) => "0x" + v.ToString("X8");
    static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    static int Lerp(int a, int b, double e) => (int)Math.Round(a + (b - a) * e);

    /// CSS cubic-bezier(x1, y1, x2, y2) at time t: solve x(u) = t (Newton, then bisection), return y(u).
    static double Bezier(double t, double[] p)
    {
        double X(double u) => 3 * (1 - u) * (1 - u) * u * p[0] + 3 * (1 - u) * u * u * p[2] + u * u * u;
        double Y(double u) => 3 * (1 - u) * (1 - u) * u * p[1] + 3 * (1 - u) * u * u * p[3] + u * u * u;
        double DX(double u) => 3 * (1 - u) * (1 - u) * p[0] + 6 * (1 - u) * u * (p[2] - p[0]) + 3 * u * u * (1 - p[2]);
        double x = t;
        for (int i = 0; i < 8; i++) {
            double d = DX(x); if (Math.Abs(d) < 1e-6) break;
            double nx = x - (X(x) - t) / d; if (nx < 0 || nx > 1) break; x = nx;
        }
        if (Math.Abs(X(x) - t) > 1e-4) {
            double lo = 0, hi = 1; x = t;
            for (int i = 0; i < 40; i++) { if (X(x) < t) lo = x; else hi = x; x = (lo + hi) / 2; }
        }
        return Y(x);
    }

    static object? Gaps(List<double>? ts)
    {
        if (ts == null || ts.Count < 2) return ts == null ? null : new { frames = ts.Count };
        var g = new List<double>();
        for (int i = 1; i < ts.Count; i++) g.Add(ts[i] - ts[i - 1]);
        g.Sort();
        return new {
            frames = ts.Count, spanMs = Math.Round(ts[^1] - ts[0], 1), medianMs = Math.Round(g[g.Count / 2], 2),
            p95Ms = Math.Round(g[Math.Min(g.Count - 1, (int)(g.Count * 0.95))], 2), maxMs = Math.Round(g[^1], 2), over25ms = g.Count(x => x > 25),
        };
    }

    static object Dwm((int hr, W.DWM_TIMING_INFO t) a, (int hr, W.DWM_TIMING_INFO t) b) => new {
        hr = b.hr, hz = b.t.rateRefreshDen == 0 ? 0 : Math.Round((double)b.t.rateRefreshNum / b.t.rateRefreshDen, 2),
        refreshes = (long)(b.t.cRefresh - a.t.cRefresh), composed = (long)(b.t.cFrame - a.t.cFrame),
        dropped = (long)(b.t.cFramesDropped - a.t.cFramesDropped), missed = (long)(b.t.cFramesMissed - a.t.cFramesMissed),
    };

    delegate IntPtr SUBCLASSPROC(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr h, SUBCLASSPROC p, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
}
