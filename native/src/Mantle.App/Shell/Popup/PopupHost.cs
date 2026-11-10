using System;
using System.Collections.Generic;
using Mantle.App.Native;
using Mantle.App.Shell.Windowing;
using Mantle.App.Surfaces.Bar.Frame;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Shell.Popups;

/// What a pop-up kind is: its key (bar.js keys: qs, cal, media, more, dl, notifs, jl:&lt;app&gt;, ...), its size in
/// DIP, whether it takes the keyboard, whether it is instant (parked between opens), and whether a switch to
/// another app closes it (`away`).
internal sealed record PopupKind(string Key, string Name, double WidthDip, double HeightDip, bool Focusable, bool Instant, bool Away, Func<UIElement> Body);

/// The surface gate and the open/close state machine of the bar's pop-ups (WIN-014, BAR-375, BAR-376):
///  - ONE pop-up at a time; opening another replaces it in place (no exit, room handed on);
///  - per open: hidden -> opening -> shown. The LAST intent wins: a second open while opening joins the first,
///    a close while opening cancels the show, an open while shown only brings it forward;
///  - its own button again closes it (toggle); a toggle within 600 ms of an outside-click close of the same
///    pop-up is the same click and does not reopen it;
///  - while one is open: the bar stays up (WIN-019), the outside watch is armed (WIN-017), and a click outside,
///    Esc, or (for `away` kinds) another app coming to the front closes it (BAR-394, BAR-396, BAR-411);
///  - the bar hiding closes it (BAR-024); opening one on a hidden bar reveals the bar (BAR-406).
internal sealed class PopupHost
{
    enum St { Hidden, Opening, Shown }
    public const int SameClickMs = 600;
    public const double GapDip = 7, InsetDip = 8;

    readonly BarWindow _bar;
    readonly DispatcherQueue _q;
    readonly PopupWatch? _watch;
    readonly string _watchWhy;
    readonly bool _watchAway;
    readonly Dictionary<string, PopupWindow> _parked = new();
    PopupWindow? _win;
    PopupKind? _kind;
    St _st = St.Hidden;
    int _seq;
    string? _outsideKey; long _outsideAt; Win32.POINT _outsidePt;
    IntPtr _fgAtOpen, _fgHook;
    Win32.WinEventProc? _fgProc;

    public PopupHost(BarWindow bar, bool watch, string watchWhy, bool away)
    {
        _bar = bar; _q = bar.DispatcherQueue; _watchWhy = watchWhy; _watchAway = away;
        _watch = watch ? new PopupWatch(_q, OnClick, () => Close("Esc"), Host.Log.Line) : null;
        bar.HoldsBar = () => _st != St.Hidden;
        bar.Hiding += why => Close("the bar went down");
        Host.Log.Line("pop-ups: " + (watch ? "the outside watch arms while one is open" : "no outside watch: " + watchWhy));
    }

    public string? OpenKey => _st == St.Hidden ? null : _kind?.Key;
    public string State => _st.ToString().ToLowerInvariant();

    /// A pop-up closed, however it closed (its button, an outside click, Esc, another pop-up, the bar hiding):
    /// (key, why). Raised once per open, after the state is hidden (quick settings lets its watches go here).
    public event Action<string, string>? Closed;

    /// The open no-focus pop-up that holds the keyboard on its own ask lost it to another window: its key.
    public event Action<string>? KeyboardLost;

    FrameworkElement? _anchor;
    PopupWindow? _kbWin;
    IntPtr _kbPrev;

    /// Electron's `bar.popover {height}` re-ask: the open pop-up of <paramref name="key"/> takes a new height, its
    /// bottom kept 7 DIP above the pill (a view that changes, a note that arrives late). False when it is not open.
    public bool Resize(string key, double heightDip)
    {
        if (_st == St.Hidden || _win == null || _kind == null || _kind.Key != key || _anchor == null) return false;
        if (Math.Abs(_kind.HeightDip - heightDip) < 0.5) return true;
        _kind = _kind with { HeightDip = heightDip };
        _win.Place(Place(_kind, _anchor), _bar.Geometry.Scale);
        Host.Log.Line($"pop-up {_kind.Name}: resized to {Math.Round(heightDip)} DIP high");
        return true;
    }

    /// <summary>
    /// Q-W6: a no-focus pop-up that needs typing (the Wi-Fi password) takes the keyboard ON ITS OWN ASK, only while
    /// the field is in use: WS_EX_NOACTIVATE comes off, the window in front is remembered and the pop-up is raised
    /// (WIN-015's ladder). <see cref="GiveBackKeyboard"/> (or the close) puts NOACTIVATE back and, if the pop-up still
    /// holds the foreground, hands it to the remembered window. Another window taking focus meanwhile raises
    /// <see cref="KeyboardLost"/> and focus is NOT dragged back (the user went there on purpose).
    /// </summary>
    public (bool Ok, string Words) TakeKeyboard(string key)
    {
        if (_st != St.Shown || _win == null || _kind?.Key != key) return (false, "the pop-up is not open");
        if (Host.Quiet) return (false, "a quiet test window never takes the keyboard from whoever is at the machine");
        var w = _win;
        if (_kbWin == w && Win32.GetForegroundWindow() == w.Hwnd) return (true, "it already has the keyboard");
        IntPtr fg = Win32.GetForegroundWindow();
        _kbPrev = fg != w.Hwnd ? fg : IntPtr.Zero;
        NoActivate(w, false);
        string r = Focus.Raise(w.Hwnd);
        if (Win32.GetForegroundWindow() != w.Hwnd) {
            NoActivate(w, true);
            Host.Log.Line($"pop-up {w.Name}: could not take the keyboard ({r})");
            return (false, "Windows did not let the pop-up take the keyboard (" + r + ")");
        }
        _kbWin = w;
        Host.Log.Line($"pop-up {w.Name}: took the keyboard on its own ask ({r}); the window in front is handed it back afterwards");
        return (true, r);
    }

    public void GiveBackKeyboard(string why)
    {
        var w = _kbWin;
        if (w == null) return;
        _kbWin = null;
        NoActivate(w, true);
        bool held = Win32.GetForegroundWindow() == w.Hwnd;
        bool back = held && _kbPrev != IntPtr.Zero && Win32.IsWindow(_kbPrev) && Win32.SetForegroundWindow(_kbPrev);
        Host.Log.Line($"pop-up {w.Name}: gave the keyboard back ({why}){(back ? ", to the window that had it" : held ? "; the window that had it could not be raised" : "; another window already had it")}");
        _kbPrev = IntPtr.Zero;
    }

    /// The keyboard went to Mantle's own window while the pop-up held it: on the close it goes back to the window
    /// that had it, if a Mantle window still has the foreground.
    void HandBackFromOwn(string why)
    {
        IntPtr prev = _kbPrev, fg = Win32.GetForegroundWindow();
        _kbPrev = IntPtr.Zero;
        uint pid = 0;
        if (fg != IntPtr.Zero) Win32.GetWindowThreadProcessId(fg, out pid);
        bool back = pid == (uint)Environment.ProcessId && Win32.IsWindow(prev) && Win32.SetForegroundWindow(prev);
        Host.Log.Line($"pop-up: gave the keyboard back ({why}){(back ? ", from Mantle's own window to the window that had it" : "; another window already had it")}");
    }

    static void NoActivate(PopupWindow w, bool on)
    {
        long ex = Win32.GetWindowLongPtr(w.Hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex = on ? ex | Win32.WS_EX_NOACTIVATE : ex & ~(long)Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(w.Hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    void OnActivated(PopupWindow w, WindowActivatedEventArgs e)
    {
        if (_kbWin != w || e.WindowActivationState != WindowActivationState.Deactivated) return;
        _kbWin = null;
        NoActivate(w, true);
        // Mantle's own window (a press on the bar): the remembered window still gets the keyboard back on the close;
        // another app: the user went there on purpose, so nothing is dragged back
        IntPtr fg = Win32.GetForegroundWindow();
        uint pid = 0;
        if (fg != IntPtr.Zero) Win32.GetWindowThreadProcessId(fg, out pid);
        bool own = fg != IntPtr.Zero && pid == (uint)Environment.ProcessId;
        if (!own) _kbPrev = IntPtr.Zero;
        Host.Log.Line($"pop-up {w.Name}: " + (own ? "Mantle's own window took the keyboard; the window that had it gets it back on the close" : "another window took the keyboard; it is not dragged back"));
        KeyboardLost?.Invoke(w.Key);
    }

    public void Toggle(PopupKind kind, FrameworkElement anchor)
    {
        if (_st != St.Hidden && _kind?.Key == kind.Key) { Close("its button was clicked again"); return; }
        if (_st == St.Hidden && _outsideKey == kind.Key && Environment.TickCount64 - _outsideAt < SameClickMs && OnAnchor(anchor, _outsidePt)) {
            Host.Log.Line($"pop-up {kind.Name}: not reopened (the click that closed it from outside was this same click)");
            _outsideKey = null;
            return;
        }
        Open(kind, anchor);
    }

    public void Open(PopupKind kind, FrameworkElement anchor)
    {
        if (_st != St.Hidden && _kind?.Key == kind.Key) {
            if (_st == St.Shown && _win != null) WindowKinds.Raise(_win.Hwnd, _q, "pop-up " + kind.Name, _fgAtOpen);   // brought forward
            return;   // opening: this open joins the first
        }
        if (_st != St.Hidden) CloseNow($"replaced by {kind.Name}", motion: false);   // BAR-375/389: no exit, the room handed on
        if (!_bar.IsUp) _bar.Reveal("a pop-up opened");
        int seq = ++_seq;
        _kind = kind; _st = St.Opening;
        _fgAtOpen = Win32.GetForegroundWindow();
        bool reused = _parked.TryGetValue(kind.Key, out var w);
        if (w == null) {
            w = new PopupWindow(kind.Key, kind.Name, kind.Focusable, kind.Instant, _bar.Dark, kind.Body());
            if (kind.Focusable) w.Root.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Close("Esc"); } };   // BAR-393
            if (kind.Instant) _parked[kind.Key] = w;
            var made = w;
            made.Win.Activated += (_, e) => OnActivated(made, e);
        }
        _anchor = anchor;
        _win = w;
        w.Root.RequestedTheme = _bar.Dark ? ElementTheme.Dark : ElementTheme.Light;
        w.Place(Place(kind, anchor), _bar.Geometry.Scale);
        w.Show();
        w.Enter(_bar.ReducedMotion, BarWindow.SpringCurve);   // BAR-387/390
        Host.Log.Line($"pop-up {kind.Name}: opening ({(reused ? "parked, uncloaked" : "a new window")}{(kind.Focusable ? ", takes the keyboard" : ", no focus")})");
        if (reused && w.Painted) Shown(seq);
        else {
            void First(object? s, object e) { CompositionTarget.Rendering -= First; w.Painted = true; _q.TryEnqueue(() => Shown(seq)); }
            CompositionTarget.Rendering += First;
        }
    }

    void Shown(int seq)
    {
        if (seq != _seq || _st != St.Opening || _win == null || _kind == null) return;   // closed or replaced while opening
        _st = St.Shown;
        var w = _win; var k = _kind;
        WindowKinds.Raise(w.Hwnd, _q, "pop-up " + k.Name, _fgAtOpen);   // WIN-011: on every show
        if (k.Focusable) {
            string r = Focus.Raise(w.Hwnd);   // WIN-015: ask, retry once through the attach ladder, else say so
            Host.Log.Line(Win32.GetForegroundWindow() == w.Hwnd ? $"pop-up {k.Name}: has the keyboard ({r})"
                : $"pop-up {k.Name}: could not take the keyboard ({r}); Esc and outside clicks still close it");
        }
        _watch?.Arm();
        if (_watch != null && k.Away && _watchAway) ArmAway();
        Host.Log.Line($"pop-up {k.Name}: shown at {FrameNative.Rect(w.Rect)}");
    }

    /// The outside-click close and this toggle were one click only if that click landed on the anchor itself.
    bool OnAnchor(FrameworkElement anchor, Win32.POINT pt)
    {
        try {
            var g = _bar.Geometry; double s = g.Scale;
            var p = anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            int x = g.Window.Left + (int)Math.Round(p.X * s), y = g.Window.Top + (int)Math.Round(p.Y * s);
            return pt.X >= x && pt.X < x + anchor.ActualWidth * s && pt.Y >= y && pt.Y < y + anchor.ActualHeight * s;
        } catch (ArgumentException) { return false; }
    }

    /// Placement (BAR-379, BAR-380): centred on its anchor, clamped 8 DIP inside the bar window, 7 DIP above the pill.
    Win32.RECT Place(PopupKind k, FrameworkElement anchor)
    {
        var g = _bar.Geometry; double s = g.Scale;
        int w = (int)Math.Round(k.WidthDip * s), h = (int)Math.Round(k.HeightDip * s);
        double cx;
        try {
            var p = anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(anchor.ActualWidth / 2, 0));
            cx = g.Window.Left + p.X * s;
        } catch (ArgumentException) { cx = (g.Pill.Left + g.Pill.Right) / 2.0; }
        int x = (int)Math.Round(cx - w / 2.0);
        x = Math.Max(g.Window.Left + (int)Math.Round(InsetDip * s), Math.Min(x, g.Window.Right - (int)Math.Round(InsetDip * s) - w));
        int bottom = g.Pill.Top - (int)Math.Round(GapDip * s);
        return new Win32.RECT { Left = x, Top = bottom - h, Right = x + w, Bottom = bottom };
    }

    public void Close(string why) { if (_st != St.Hidden) CloseNow(why, motion: true); }

    /// Closed at once (state hidden, watches off); the window itself plays the exit (BAR-388) and is then parked
    /// or closed, unless there is nothing to see: replaced by another pop-up, or never painted (BAR-389).
    void CloseNow(string why, bool motion)
    {
        var w = _win; var k = _kind;
        bool painted = _st == St.Shown || (w?.Painted == true && _st == St.Opening);
        _st = St.Hidden; _seq++; _win = null;
        if (_kbWin != null) GiveBackKeyboard("the pop-up closed");
        else if (_kbPrev != IntPtr.Zero) HandBackFromOwn("the pop-up closed");
        _watch?.Disarm();
        DisarmAway();
        string fate = w?.Instant == true ? "parked" : "closed", end = fate;
        if (w != null) {
            if (motion && painted && Win32.IsWindowVisible(w.Hwnd)) {
                int ms = _bar.ReducedMotion ? PopupWindow.ReducedMs : (int)Math.Round(PopupWindow.DurSMs * PopupWindow.LeaveK);
                w.Leave(_bar.ReducedMotion, () => { if (w.Instant) w.Park(); else w.Close(); Host.Log.Line($"pop-up {k?.Name ?? "?"}: exit done, {fate}"); });
                end = $"exit {ms} ms + {PopupWindow.LeaveTailMs} ms, then {fate}";
            } else {
                w.Rest();
                if (w.Instant) w.Park(); else w.Close();
                end = (w.Instant ? "parked" : "closed") + " at once";
            }
        }
        Host.Log.Line($"pop-up {k?.Name ?? "?"}: closed ({why}), {end}");
        if (k != null) Closed?.Invoke(k.Key, why);
    }

    /// The bar's own presses (BAR-392): one inside the bar but outside the pop-up's anchor closes it.
    public void BarPressed(DependencyObject? source, FrameworkElement? anchorOfOpen)
    {
        if (_st == St.Hidden) return;
        // BAR-392: no anchor counts as outside, not only the open one's: each toggles itself, and another
        // pop-up's anchor opens its pop-up in place of this one (BAR-375: replaced, no exit)
        for (var d = source; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == anchorOfOpen || (d is FrameworkElement f && _anchors.Contains(f))) return;
        Close("a press on the bar outside the pop-up");
    }

    readonly HashSet<FrameworkElement> _anchors = new();
    /// A bar element that opens a pop-up (BAR-392's anchor list: volume, network, battery, clock, bell, ...).
    public void Anchor(FrameworkElement el) => _anchors.Add(el);

    void OnClick(string what, Win32.POINT pt)
    {
        if (_st == St.Hidden || _win == null) return;
        if (FrameNative.Contains(_win.Rect, pt.X, pt.Y)) return;
        if (FrameNative.Contains(_bar.Geometry.Pill, pt.X, pt.Y)) return;   // the bar handles its own presses
        _outsideKey = _kind?.Key; _outsideAt = Environment.TickCount64; _outsidePt = pt;
        Close($"a {what} click outside");
    }

    // BAR-396: a switch to another app closes it; Mantle's own windows and the window in front when it opened do not
    void ArmAway()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc ??= (hook, ev, hwnd, idObject, idChild, thread, time) => {
            if (hwnd == IntPtr.Zero || hwnd == _fgAtOpen) return;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            _q.TryEnqueue(() => Close("another app came to the front"));
        };
        _fgHook = Win32.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _fgProc, 0, 0, 0x2 /* OUTOFCONTEXT | SKIPOWNPROCESS */);
    }

    void DisarmAway()
    {
        if (_fgHook != IntPtr.Zero) Win32.UnhookWinEvent(_fgHook);
        _fgHook = IntPtr.Zero;
    }
}
