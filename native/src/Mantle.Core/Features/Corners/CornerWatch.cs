using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mantle.Core.Features.Corners;

/// <summary>
/// What a pointer watch has to know to decide, off the host thread, whether a cursor move matters: the corner table and
/// the displays (both swapped whole by the feature, read without a lock). A move matters when the cursor is near an
/// active corner (its square plus <see cref="SlackDip"/>), or when it was near one at the last move (it just left).
/// Everything else (the whole screen bar a few corner squares) is dropped on the watch's own thread: no host wake.
/// </summary>
public sealed class CornerFilter
{
    /// The near zone is the corner square plus this much (DIP), so rounding never hides the move into the square.
    public const double SlackDip = 2;
    volatile CornerTable _table = CornerRules.Defaults;
    volatile IReadOnlyList<DipRect> _displays = Array.Empty<DipRect>();
    bool _was;

    public CornerTable Table { get => _table; set => _table = value; }
    public IReadOnlyList<DipRect> Displays { get => _displays; set => _displays = value; }

    /// One move: true when the host should look (near an active corner now, or near one at the last move).
    public bool Wants(DipPoint? pt)
    {
        bool near = Near(pt);
        bool want = near || _was;
        _was = near;
        return want;
    }

    /// The point is in or next to an active outer corner square. An unreadable point counts as near (the host says why).
    public bool Near(DipPoint? pt)
    {
        if (pt is not { } p) return true;
        var t = _table;
        if (!CornerRules.AnyOn(t)) return false;
        var c = CornerRules.CornerOnScreens(p, _displays, CornerRules.CornerZoneDip + SlackDip);
        return c != null && t.Of(c) != "off";
    }
}

/// <summary>
/// The live pointer watch: an out-of-context WinEvent hook for the cursor's own location-change event (OBJID_CURSOR), on
/// a thread of its own. Windows queues the event to that thread when the cursor moves; nothing runs while it rests, and
/// unlike a low-level mouse hook it sits outside the input path (it cannot slow the pointer, and Windows never drops it
/// for answering late). Each move costs one GetCursorPos and a few comparisons on that thread; only a move near an active
/// corner (or the one leaving it) wakes the host. The sign-in, lock and UAC desktops send nothing, so nothing fires there.
/// </summary>
public sealed class Win32CornerWatch : ICornerWatch
{
    readonly ICornerPointer _pointer;
    readonly CornerFilter _filter;
    readonly Action _wake;
    readonly WinEventProc _proc;
    Thread? _thread;
    volatile uint _tid;
    volatile bool _closed;
    IntPtr _hook;
    long _displaysAt = long.MinValue;
    int _pending;

    /// Moves heard and host wakes asked for (the probe's numbers).
    public long Moves, Wakes;

    public Win32CornerWatch(ICornerPointer pointer, CornerFilter filter, Action wake)
    {
        _pointer = pointer; _filter = filter; _wake = wake; _proc = OnEvent;
    }

    /// Start the hook thread; null when it is up, else why not in words.
    public string? Start()
    {
        string? err = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready, e => err = e)) { IsBackground = true, Name = "corner watch" };
        _thread.Start();
        if (!ready.Wait(3000)) return "the pointer watch did not start in 3 s";
        return err;
    }

    /// The host looked: the next move near a corner may wake it again.
    public void Taken() => Interlocked.Exchange(ref _pending, 0);

    void Loop(ManualResetEventSlim ready, Action<string> fail)
    {
        try {
            _tid = GetCurrentThreadId();
            PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // the thread's queue, before anyone posts to it
            _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
            if (_hook == IntPtr.Zero) { fail("Windows refused the pointer's location-change hook"); return; }
        } catch (Exception e) { fail($"the pointer watch could not start ({e.Message})"); return; }
        finally { ready.Set(); }
        while (!_closed && GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_CURSOR || _closed) return;
        try {
            Moves++;
            long now = Environment.TickCount64;
            if (now - _displaysAt >= CornerRules.DisplaysMs) {   // a monitor plugged in or moved: seen at the next move
                _displaysAt = now;
                var d = _pointer.Displays();
                if (d.Count > 0) _filter.Displays = d;
            }
            if (!_filter.Wants(_pointer.Read().Pt)) return;
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;   // one wake out at a time
            Wakes++;
            _wake();
        } catch { /* a move is never worth a throw on the hook thread */ }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_tid != 0) PostThreadMessageW(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0, WM_QUIT = 0x0012;
    const int OBJID_CURSOR = -9;
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint lo, uint hi);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint lo, uint hi, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
