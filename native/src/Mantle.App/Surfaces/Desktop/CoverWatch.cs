using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Mantle.Core.Features.Desktop;

namespace Mantle.App.Surfaces.Desktop;

/// <summary>
/// Is an app window over a desktop widget? (DESK-092/096..098; sidecar/WidgetCover.cs.) Electron's sidecar rode its window
/// list's WinEvent hooks through a 250 ms timer plus a 2 s safety poll. Here the hooks run on a thread of their own (so a
/// cursor or caret move, which Windows reports as a location change too, is dropped there and never wakes the UI thread),
/// and the only timers are the throttle's: one decision 100 ms after the first window event of a burst (every 100 ms while
/// a window is dragged), one settle check a second later, then none. No window event, no work: no poll at all.
/// </summary>
/// <remarks>
/// Read only: it never changes a window. The decision is WidgetCover.Decide (pure, unit-tested) over the live z-order, top
/// down to the desktop (Progman) or to the layer itself (a --test layer is an ordinary window: what is under it cannot
/// cover it). The answer goes to the layer only when a widget's covered / uncovered state CHANGES.
/// </remarks>
internal sealed partial class CoverWatch : IDisposable
{
    const uint WM_TIMER = 0x0113, WM_QUIT = 0x0012, WM_APP_AREAS = 0x8000 + 41, PM_NOREMOVE = 0;
    const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint EVENT_OBJECT_CREATE = 0x8000, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const long WS_EX_LAYERED = 0x80000;
    const uint LWA_ALPHA = 2;

    readonly Action<CoverAnswer, string> _answer;
    readonly Action<string> _error;
    readonly Action<int, string>? _decided;
    readonly int _ownPid;
    readonly IntPtr _layer;
    readonly WinEventProc _proc;
    readonly EnumProc _enum;
    readonly CoverThrottle _throttle = new();
    readonly List<IntPtr> _hooks = new();
    volatile IReadOnlyList<CoverArea> _areas;
    Thread? _thread;
    uint _tid;
    UIntPtr _throttleId, _settleId;
    string? _sig, _lastErr;
    volatile bool _closed;
    List<CoverWin>? _list;
    IntPtr _progman;
    int _decisions;

    /// <summary>Decisions made so far (for the log and the tests: nothing moves, nothing is decided).</summary>
    public int Decisions => Volatile.Read(ref _decisions);

    /// <param name="answer">On the hook thread: a changed answer and what prompted it.</param>
    /// <param name="decided">On the hook thread, every decision with its count and cause (the --test log only: a test reads
    /// that a resting desktop makes none).</param>
    public CoverWatch(IReadOnlyList<CoverArea> areas, IntPtr layer, Action<CoverAnswer, string> answer, Action<string> error, Action<int, string>? decided = null)
    {
        _areas = areas; _layer = layer; _answer = answer; _error = error; _decided = decided; _ownPid = Environment.ProcessId;
        _proc = OnEvent; _enum = OnWindow;
    }

    /// Start the hook thread; null when it is up (its first answer follows at once), else why not in words.
    public string? Start()
    {
        string? err = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready, e => err = e)) { IsBackground = true, Name = "cover watch" };
        _thread.Start();
        if (!ready.Wait(3000)) return "the window watch did not start in 3 s";
        return err;
    }

    /// The widgets moved (or one was added or dropped): decide again at once with the new areas.
    public void SetAreas(IReadOnlyList<CoverArea> areas)
    {
        _areas = areas;
        if (_tid != 0) PostThreadMessageW(_tid, WM_APP_AREAS, IntPtr.Zero, IntPtr.Zero);
    }

    void Loop(ManualResetEventSlim ready, Action<string> fail)
    {
        try {
            _tid = GetCurrentThreadId();
            PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // the thread's message queue, before anyone posts to it
            uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            foreach (var (lo, hi) in new[] {
                (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND), (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
                (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE), (EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE),
                (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED) }) {
                var h = SetWinEventHook(lo, hi, IntPtr.Zero, _proc, 0, 0, flags);
                if (h == IntPtr.Zero) { fail($"Windows refused a window-event hook (0x{lo:X4}..0x{hi:X4})"); Unhook(); return; }
                _hooks.Add(h);
            }
        } catch (Exception e) { fail($"the window watch could not start ({e.Message})"); Unhook(); return; }
        finally { ready.Set(); }
        Decide("the watch started");
        while (!_closed && GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) {
            if (m.message == WM_TIMER) { OnTimer(m.wParam); continue; }
            if (m.message == WM_APP_AREAS) { _sig = null; Decide("the widgets moved"); continue; }
            TranslateMessage(ref m); DispatchMessageW(ref m);
        }
        Kill(ref _throttleId); Kill(ref _settleId);
        Unhook();
    }

    void Unhook() { foreach (var h in _hooks) UnhookWinEvent(h); _hooks.Clear(); }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // the window itself (OBJID_WINDOW): the cursor, the caret and every sub-object are dropped here, on this thread
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || _closed) return;
        if (ev != EVENT_SYSTEM_FOREGROUND && GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;   // top-level windows only
        if (_throttle.Event() == CoverThrottle.Next.Throttle) {
            Kill(ref _settleId);
            _throttleId = SetTimer(IntPtr.Zero, UIntPtr.Zero, WidgetCover.ThrottleMs, IntPtr.Zero);
        }
    }

    void OnTimer(UIntPtr id)
    {
        if (id == _throttleId && id != UIntPtr.Zero) {
            Kill(ref _throttleId);
            Decide("a window changed");
            if (_throttle.Fired() == CoverThrottle.Next.Throttle) _throttleId = SetTimer(IntPtr.Zero, UIntPtr.Zero, WidgetCover.ThrottleMs, IntPtr.Zero);
            else _settleId = SetTimer(IntPtr.Zero, UIntPtr.Zero, WidgetCover.SettleMs, IntPtr.Zero);
        } else if (id == _settleId && id != UIntPtr.Zero) {
            Kill(ref _settleId);
            Decide("a second after the last window change");
        } else KillTimer(IntPtr.Zero, id);
    }

    static void Kill(ref UIntPtr id) { if (id != UIntPtr.Zero) { KillTimer(IntPtr.Zero, id); id = UIntPtr.Zero; } }

    void Decide(string why)
    {
        try {
            var a = WidgetCover.Decide(_areas, Live(), _ownPid);
            int n = Interlocked.Increment(ref _decisions);
            _decided?.Invoke(n, why);
            _lastErr = null;
            string sig = a.Sig();
            if (sig == _sig) return;
            _sig = sig;
            _answer(a, why);
        } catch (Exception e) {
            if (e.Message != _lastErr) { _lastErr = e.Message; _error(e.Message); }
        }
    }

    /// Every top-level window, top of the z-order first, down to the desktop (or the layer itself) inclusive.
    List<CoverWin> Live()
    {
        _list = new List<CoverWin>();
        _progman = FindWindowW("Progman", null);
        EnumWindows(_enum, IntPtr.Zero);
        return _list;
    }

    bool OnWindow(IntPtr hwnd, IntPtr lp)
    {
        var list = _list!;
        if (hwnd == _progman || hwnd == _layer) { list.Add(new CoverWin { Hwnd = hwnd.ToInt64(), Cls = ClassOf(hwnd), Desktop = true }); return false; }
        if (!IsWindowVisible(hwnd)) return true;   // the bulk of the list: not worth reading further
        var w = new CoverWin { Hwnd = hwnd.ToInt64(), Cls = ClassOf(hwnd), Title = TitleOf(hwnd), Iconic = IsIconic(hwnd) };
        w.Cloaked = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;
        w.ExStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        GetWindowThreadProcessId(hwnd, out uint pid);
        w.Pid = (int)pid;
        if ((w.ExStyle & WS_EX_LAYERED) != 0 && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out uint lf) && (lf & LWA_ALPHA) != 0) w.Alpha = alpha;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(hwnd, out r);
        w.L = r.Left; w.T = r.Top; w.R = r.Right; w.B = r.Bottom;
        list.Add(w);
        return true;
    }

    static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, sb.Capacity); return sb.ToString(); }
    static string TitleOf(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_tid != 0) PostThreadMessageW(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lp);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, UIntPtr id);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
