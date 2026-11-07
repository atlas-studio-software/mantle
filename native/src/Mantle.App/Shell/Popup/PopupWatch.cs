using System;
using System.Runtime.InteropServices;
using System.Threading;
using Mantle.App.Native;
using Mantle.App.Surfaces.Bar.Frame;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Shell.Popups;

/// The outside watch of an open pop-up (WIN-017, WIN-018): mouse buttons and Esc anywhere, whichever app has the
/// keyboard. Low-level hooks on their own thread with its own message loop, armed ONLY while a pop-up is open (no
/// hook, no thread, no CPU otherwise). The hooks only report, they never swallow a click or a key. If Windows
/// refuses the hooks, a 25 ms GetAsyncKeyState poll takes over (down edges, plus the "pressed since the last read"
/// bit for a tap shorter than the poll). The mode is said once. Reports arrive on the UI thread.
internal sealed class PopupWatch
{
    readonly DispatcherQueue _q;
    readonly Action<string, Win32.POINT> _click;   // "left" | "right" | "middle", screen px
    readonly Action _esc;
    readonly Action<string> _log;
    readonly FrameNative.HookProc _mouseProc, _keyProc;   // kept alive: Windows calls them
    Thread? _thread;
    volatile bool _armed;
    uint _tid;
    string _mode = "", _said = "";

    public PopupWatch(DispatcherQueue q, Action<string, Win32.POINT> click, Action esc, Action<string> log)
    {
        _q = q; _click = click; _esc = esc; _log = log;
        _mouseProc = MouseProc; _keyProc = KeyProc;
    }

    public string Mode => _mode;

    public void Arm()
    {
        if (_armed) return;
        _armed = true;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "pop-up watch" };
        _thread.Start();
        ready.Wait(1000);
        if (_mode != _said) { _said = _mode; _log("pop-up watch: " + _mode); }
    }

    public void Disarm()
    {
        if (!_armed) return;
        _armed = false;
        if (_tid != 0) FrameNative.PostThreadMessageW(_tid, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }

    void Run(ManualResetEventSlim ready)
    {
        _tid = FrameNative.GetCurrentThreadId();
        IntPtr mod = FrameNative.GetModuleHandleW(IntPtr.Zero);
        IntPtr m = FrameNative.SetWindowsHookExW(14 /* WH_MOUSE_LL */, _mouseProc, mod, 0);
        int mErr = Marshal.GetLastWin32Error();
        IntPtr k = FrameNative.SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _keyProc, mod, 0);
        int kErr = Marshal.GetLastWin32Error();
        if (m != IntPtr.Zero && k != IntPtr.Zero) {
            _mode = "hook (low-level mouse and keyboard hooks, armed only while a pop-up is open; they report, never swallow)";
            ready.Set();
            while (FrameNative.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        } else {
            if (m != IntPtr.Zero) FrameNative.UnhookWindowsHookEx(m);
            if (k != IntPtr.Zero) FrameNative.UnhookWindowsHookEx(k);
            m = k = IntPtr.Zero;
            _mode = $"poll (Windows refused the low-level hooks: mouse error {mErr}, keyboard error {kErr}; GetAsyncKeyState every 25 ms)";
            ready.Set();
            Poll();
        }
        if (m != IntPtr.Zero) FrameNative.UnhookWindowsHookEx(m);
        if (k != IntPtr.Zero) FrameNative.UnhookWindowsHookEx(k);
        _tid = 0;
    }

    void Poll()
    {
        var keys = new[] { (0x01, "left"), (0x02, "right"), (0x04, "middle"), (0x1B, "esc") };
        var down = new bool[keys.Length];
        for (int i = 0; i < keys.Length; i++) FrameNative.GetAsyncKeyState(keys[i].Item1);   // clear the "since last read" bits
        while (_armed) {
            Thread.Sleep(25);
            for (int i = 0; i < keys.Length; i++) {
                short s = FrameNative.GetAsyncKeyState(keys[i].Item1);
                bool now = (s & 0x8000) != 0, tapped = (s & 0x1) != 0;
                if ((now && !down[i]) || (!now && tapped)) Report(keys[i].Item2);
                down[i] = now;
            }
        }
    }

    void Report(string what)
    {
        if (what == "esc") { _q.TryEnqueue(() => { if (_armed) _esc(); }); return; }
        FrameNative.GetCursorPos(out var pt);
        _q.TryEnqueue(() => { if (_armed) _click(what, pt); });
    }

    IntPtr MouseProc(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && _armed) {
            int msg = (int)wp.ToInt64();
            string? what = msg == 0x0201 ? "left" : msg == 0x0204 ? "right" : msg == 0x0207 ? "middle" : null;
            if (what != null) {
                var pt = new Win32.POINT { X = Marshal.ReadInt32(lp, 0), Y = Marshal.ReadInt32(lp, 4) };   // MSLLHOOKSTRUCT.pt
                _q.TryEnqueue(() => { if (_armed) _click(what, pt); });
            }
        }
        return FrameNative.CallNextHookEx(IntPtr.Zero, code, wp, lp);
    }

    IntPtr KeyProc(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0 && _armed) {
            int msg = (int)wp.ToInt64();
            if ((msg == 0x0100 || msg == 0x0104) && Marshal.ReadInt32(lp, 0) == 0x1B /* KBDLLHOOKSTRUCT.vkCode: Esc */)
                _q.TryEnqueue(() => { if (_armed) _esc(); });
        }
        return FrameNative.CallNextHookEx(IntPtr.Zero, code, wp, lp);
    }
}
