#nullable enable
// KeyHook.cs - Mantle's global shortcuts inside the WinUI process: a low-level keyboard hook on a
// thread of its own, handlers on the UI thread. A port of the sidecar's Hotkey.cs plumbing, its
// KeyMachine rules for the three gestures the shell owns (solo Win tap, Win+Tab, Alt+Space) and
// HookHealth's dead-hook re-arm. Plain Win32 + a DispatcherQueue: the file later lanes copy.
//
// WHY A THREAD OF ITS OWN: Windows calls a low-level hook in-line with every keystroke on the desktop.
// Past LowLevelHooksTimeout it skips the hook for that key and, on Windows 7 and later, "the hook is
// silently removed without being called". A WinUI UI thread that is busy for a moment (layout, a
// big list, a GC) would cost keys and then the hook. So the hook thread runs nothing but the hook,
// its message loop and the raw-input health count; every handler is posted to the UI DispatcherQueue.
//
// THE TRICK (as Hotkey.cs): on a Win-up that Windows would read as a solo tap (our tap, or a Win+Tab
// whose Tab we ate), and on an Alt-up after an Alt+Space whose Space we ate, a keypress of VK 0xFF (an
// unmapped key) is injected BEFORE the real keyup goes on. Windows then sees Win+0xFF / Alt+0xFF:
// Start stays shut, the focused app does not open its menu bar. A modifier keyup is NEVER swallowed
// (a Win-down with its up eaten leaves Windows thinking Win is held forever); the only keys ever
// swallowed are Tab and Space, each keyup with its keydown.
//
// INJECTED KEYS: like the sidecar, keys with LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED are ignored,
// unless AcceptInjected is set (the spike's check drives the keyboard with SendInput, so it sets it).
// Our own 0xFF partner key carries a marker in dwExtraInfo and is always ignored.
//
// HOOK HEALTH (HookHealth.cs): the hook thread also takes RAW keyboard input (WM_INPUT, input sink,
// on a message-only window). Windows makes raw input after the low-level hooks have passed a key, so
// a live hook's callback always comes first and zeroes the count. MissedKeys raw key events with no
// callback for QuietMs = Windows dropped the hook: install it again (at most once per BackoffMs).
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

public sealed class KeyHook
{
    public const int EvWinTap = 1, EvWinTab = 2, EvAltSpace = 3;
    public static string Name(int ev) => ev switch { 1 => "winTap", 2 => "winTab", 3 => "altSpace", _ => "none" };

    public bool AcceptInjected, WinTap = true, WinTab = true, AltSpace = true;
    /// Alt+Space through RegisterHotKey (WM_HOTKEY on the hook thread) instead of the hook.
    public bool AltSpaceByRegisterHotKey;
    public bool? RegisterHotKeyOk;
    /// ui thread: the handler; ev, the hook's QPC timestamp, the key's KBDLLHOOKSTRUCT.time, via.
    readonly Action<int, long, int, string> _onUi;
    readonly DispatcherQueue _dq;
    readonly Action<string> _log;

    IntPtr _hook, _sink;
    uint _tid;
    HookProc? _proc;
    public bool OnUiThread { get; private set; }

    // counters (read by Status from any thread)
    public int Callbacks, Raw, RawSinceHook, Heals, Swallowed, Dummies, Fired, SlowestMs;
    public string? LastHeal;
    /// At the last re-install: did unhooking the OLD handle succeed (true = it was still installed,
    /// the heal was a false alarm; false = Windows had removed it).
    public bool? LastOldUnhook;
    volatile int _lastCallback = Environment.TickCount;
    int _healedAt; bool _healed;
    volatile int _stallNextMs;

    // a ring of fired events: the proc writes, the loop posts (no allocation inside the hook proc)
    readonly int[] _ringEv = new int[256], _ringTime = new int[256];
    readonly long[] _ringQpc = new long[256];
    int _ringHead;

    public const int MissedKeys = 4, QuietMs = 1500, BackoffMs = 15000;
    const uint MSG_EVENT = 0x8000 + 21, MSG_FREEZE = 0x8000 + 22, MSG_QUIT = 0x8000 + 23, MSG_DROP = 0x8000 + 24;
    static readonly IntPtr MARK = new IntPtr(0x4D414E54);   // 'MANT' in dwExtraInfo: our own partner key
    const ushort VK_DUMMY = 0xFF;

    public KeyHook(DispatcherQueue dq, Action<int, long, int, string> onUi, Action<string> log) { _dq = dq; _onUi = onUi; _log = log; }

    /// onUiThread = the CONTRAST mode: the hook on the calling (UI) thread, as a naive port would do.
    public void Start(bool onUiThread)
    {
        OnUiThread = onUiThread;
        if (onUiThread) { _tid = GetCurrentThreadId(); Install("start (UI thread)"); return; }
        var ready = new ManualResetEventSlim();
        var th = new Thread(() => Run(ready)) { IsBackground = true, Name = "keyboard hook", Priority = ThreadPriority.Highest };
        th.Start();
        ready.Wait(3000);
    }

    /// The next hook callback sleeps ms on the hook thread (simulates a starved hook thread, so
    /// Windows times the hook out and drops it).
    public void StallNext(int ms) { _stallNextMs = ms; }
    /// The hook thread stops pumping for ms (a starved thread: every key waits LowLevelHooksTimeout).
    public void Freeze(int ms) { PostThreadMessageW(_tid, MSG_FREEZE, new IntPtr(ms), IntPtr.Zero); }
    /// TEST: unhook without telling anyone, exactly what Windows does when it drops a hook, so the
    /// raw-input health check has a dead hook to find.
    public void SimulateDrop() { PostThreadMessageW(_tid, MSG_DROP, IntPtr.Zero, IntPtr.Zero); }

    void Run(ManualResetEventSlim ready)
    {
        _tid = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);   // make the queue before anyone posts to it
        Install("start");
        // raw keyboard input to a message-only window: the health count
        _sink = CreateWindowExW(0, "Message", "KeyHook sink", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        var rid = new RAWINPUTDEVICE[] { new() { usUsagePage = 1, usUsage = 6, dwFlags = 0x100 /* INPUTSINK */, hwndTarget = _sink } };
        bool rawOk = RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _log($"hook thread {_tid}: sink {_sink.ToInt64()}, raw input {(rawOk ? "on" : "FAILED " + Marshal.GetLastWin32Error())}");
        if (AltSpaceByRegisterHotKey) {
            RegisterHotKeyOk = RegisterHotKey(IntPtr.Zero, 1, 0x1 /* ALT */ | 0x4000 /* NOREPEAT */, 0x20 /* SPACE */);
            _log("RegisterHotKey(Alt+Space): " + (RegisterHotKeyOk == true ? "ok" : "FAILED " + Marshal.GetLastWin32Error()));
        }
        ready.Set();
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) {
            if (m.message == 0x00FF /* WM_INPUT */ && m.hwnd == _sink) { OnRaw(); DispatchMessageW(ref m); continue; }
            if (m.hwnd == IntPtr.Zero) {
                if (m.message == MSG_EVENT) { PostRing((int)m.wParam); continue; }
                if (m.message == 0x0312 /* WM_HOTKEY */) {
                    long q = Stopwatch.GetTimestamp();
                    Fired++;
                    int t = (int)m.time;
                    _dq.TryEnqueue(() => _onUi(EvAltSpace, q, t, "registerhotkey"));
                    continue;
                }
                if (m.message == MSG_FREEZE) { _log($"hook thread frozen for {(int)m.wParam} ms"); Thread.Sleep((int)m.wParam); _log("hook thread pumping again"); continue; }
                if (m.message == MSG_DROP) { bool u = UnhookWindowsHookEx(_hook); _log($"TEST: hook removed behind our back (UnhookWindowsHookEx {u})"); continue; }
                if (m.message == MSG_QUIT) break;
            }
            TranslateMessage(ref m); DispatchMessageW(ref m);
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    void Install(string why)
    {
        // Fails for a hook Windows already removed: that answer tells a real drop from a false alarm.
        if (_hook != IntPtr.Zero) LastOldUnhook = UnhookWindowsHookEx(_hook);
        _proc ??= new HookProc(Proc);
        _hook = SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _proc, GetModuleHandleW(null), 0);
        _lastCallback = Environment.TickCount;
        RawSinceHook = 0;
        _log($"hook {(_hook != IntPtr.Zero ? "installed" : "FAILED " + Marshal.GetLastWin32Error())} ({why})");
    }

    /// Per raw key event, on the hook thread (HookHealth.KeyVerdict).
    void OnRaw()
    {
        Raw++;
        int c = ++RawSinceHook;
        int now = Environment.TickCount, quiet = unchecked(now - _lastCallback);
        if (c < MissedKeys || quiet < QuietMs) return;
        if (_healed && unchecked(now - _healedAt) < BackoffMs) return;
        Heals++; _healed = true; _healedAt = now;
        LastHeal = $"{c} raw key events and no hook callback for {quiet} ms: Windows dropped the hook, installed it again";
        _log(LastHeal);
        Install("heal");
    }

    void PostRing(int idx)
    {
        int i = idx & 255;
        int ev = _ringEv[i], t = _ringTime[i];
        long q = _ringQpc[i];
        _dq.TryEnqueue(() => _onUi(ev, q, t, OnUiThread ? "hook-on-ui" : "hook"));
    }

    // ------------------------------------------------------------------ the machine (KeyMachine's rules)
    bool _winDown, _otherPressed, _swallowedInWin, _altHeld, _swallowedInAlt;
    readonly bool[] _chordDown = new bool[256];
    const int VK_TAB = 0x09, VK_SPACE = 0x20, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    const int Pass = 0, Swallow = 1, DummyFirst = 2;
    int _event;

    int Step(int vk, bool down, bool altFlag)
    {
        _event = 0;
        if (vk == VK_LWIN || vk == VK_RWIN) {
            if (down) { if (!_winDown) { _winDown = true; _otherPressed = false; _swallowedInWin = false; } return Pass; }
            bool solo = _winDown && !_otherPressed;
            bool dummy = (solo && WinTap) || _swallowedInWin;
            if (solo && WinTap) _event = EvWinTap;
            _winDown = _otherPressed = _swallowedInWin = false;
            return dummy ? DummyFirst : Pass;
        }
        if (down && _winDown) _otherPressed = true;   // a chord, not a tap
        if (vk == VK_LMENU || vk == VK_RMENU || vk == VK_MENU) {
            if (down) { if (!_altHeld) { _altHeld = true; _swallowedInAlt = false; } return Pass; }
            if (KeyDown(vk == VK_LMENU ? VK_RMENU : VK_LMENU)) return Pass;   // the other Alt still holds it
            bool d = _swallowedInAlt;
            _altHeld = _swallowedInAlt = false;
            return d ? DummyFirst : Pass;
        }
        if ((uint)vk >= 256) return Pass;
        if (!down) { if (_chordDown[vk]) { _chordDown[vk] = false; return Swallow; } return Pass; }
        if (_chordDown[vk]) return Swallow;   // auto-repeat of a chord we took: no second event
        bool win = _winDown || KeyDown(VK_LWIN) || KeyDown(VK_RWIN);
        bool alt = altFlag || KeyDown(VK_MENU);
        bool ctrl = KeyDown(VK_CONTROL), shift = KeyDown(VK_SHIFT);
        if (vk == VK_TAB && WinTab && win && !alt && !ctrl && !shift) {
            _chordDown[vk] = true; _swallowedInWin = true; _event = EvWinTab; return Swallow;
        }
        if (vk == VK_SPACE && AltSpace && !AltSpaceByRegisterHotKey && alt && !win && !ctrl && !shift) {
            _chordDown[vk] = true; _altHeld = true; _swallowedInAlt = true; _event = EvAltSpace; return Swallow;
        }
        return Pass;
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        _lastCallback = Environment.TickCount;
        RawSinceHook = 0;
        Callbacks++;
        if (code == 0) {
            long qpc = Stopwatch.GetTimestamp();
            int st = _stallNextMs;
            if (st > 0) { _stallNextMs = 0; Thread.Sleep(st); }
            try {
                int vk = Marshal.ReadInt32(lParam, 0);
                uint flags = (uint)Marshal.ReadInt32(lParam, 8);
                int time = Marshal.ReadInt32(lParam, 12);
                IntPtr extra = Marshal.ReadIntPtr(lParam, 16);
                bool injected = (flags & 0x12 /* INJECTED | LOWER_IL_INJECTED */) != 0;
                if (extra != MARK && vk != VK_DUMMY && (!injected || AcceptInjected)) {
                    uint msg = (uint)wParam.ToInt64();
                    bool down = msg == 0x100 || msg == 0x104;
                    int r = Step(vk, down, (flags & 0x20) != 0);
                    if ((r & DummyFirst) != 0) { SendInput(2, _dummy, 40); Dummies++; }
                    if (_event != 0) {
                        Fired++;
                        if (OnUiThread) {
                            int ev = _event;
                            _dq.TryEnqueue(() => _onUi(ev, qpc, time, "hook-on-ui"));
                        } else {
                            int i = Interlocked.Increment(ref _ringHead) & 255;
                            _ringEv[i] = _event; _ringQpc[i] = qpc; _ringTime[i] = time;
                            PostThreadMessageW(_tid, MSG_EVENT, new IntPtr(i), IntPtr.Zero);
                        }
                    }
                    if ((r & Swallow) != 0) {
                        Swallowed++;
                        int waited = unchecked(Environment.TickCount - time);
                        if (waited > SlowestMs && waited < 60000) SlowestMs = waited;
                        return new IntPtr(1);
                    }
                }
            } catch (Exception) { /* never throw out of a hook proc */ }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    readonly KI[] _dummy = {
        new() { type = 1, vk = VK_DUMMY, extra = MARK },
        new() { type = 1, vk = VK_DUMMY, flags = 0x2, extra = MARK },
    };

    public object Status() => new {
        thread = OnUiThread ? "ui" : "own", hooked = _hook != IntPtr.Zero, callbacks = Callbacks, raw = Raw, rawSinceHook = RawSinceHook,
        msSinceCallback = unchecked(Environment.TickCount - _lastCallback), heals = Heals, lastHeal = LastHeal, lastOldUnhookOk = LastOldUnhook,
        swallowed = Swallowed, dummies = Dummies, fired = Fired, slowestSwallowedMs = SlowestMs, registerHotKey = RegisterHotKeyOk,
        acceptInjected = AcceptInjected,
    };

    // ------------------------------------------------------------------ Win32
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public PT pt; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct KI { [FieldOffset(0)] public uint type; [FieldOffset(8)] public ushort vk; [FieldOffset(10)] public ushort scan;
                [FieldOffset(12)] public uint flags; [FieldOffset(16)] public uint time; [FieldOffset(24)] public IntPtr extra; }
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookExW(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint a, uint b, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, KI[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? n);
}
