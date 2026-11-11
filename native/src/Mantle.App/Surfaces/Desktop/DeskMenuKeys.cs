using System;
using System.Runtime.InteropServices;
using System.Threading;
using Mantle.App.Native;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core.Features.Desktop;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Surfaces.Desktop;

/// <summary>
/// MENU KEYS (DESK-118..120; sidecar/Hotkey.cs PopWatch nav:true, lib/popwatch.js): the desktop menu never takes the
/// keyboard, so while it is open a low-level keyboard hook on its own thread hands it the bare Up, Down, Home, End and
/// Enter, the way a Windows context menu takes them, and swallows them (with their key-ups) so the app in front does
/// not see them too. Shift+Down, Ctrl+End and the like stay the app's, and so does every key while a Mantle window (the
/// bar, Settings, the launcher) is in front. No hook, no thread, no CPU while the menu is closed.
/// </summary>
/// <remarks>
/// A --test run arms it only with MANTLE_TEST_DESKMENU_KEYS=1 (the desktop UI test, on a CI runner): there it swallows
/// too, so a test's Enter never reaches the runner's desktop, and it takes the keys whatever is in front (the test's bar
/// may be the foreground window there). Without it a test menu takes no keys at all.
/// </remarks>
internal sealed class DeskMenuKeys
{
    public const string TestEnv = "MANTLE_TEST_DESKMENU_KEYS";

    readonly DispatcherQueue _q;
    readonly Action<string> _key, _log;
    readonly FrameNative.HookProc _proc;   // kept alive: Windows calls it
    readonly uint _pid = (uint)Environment.ProcessId;
    readonly bool _test;
    Thread? _thread;
    volatile bool _armed;
    uint _tid;
    int _held;   // the menu keys whose down was swallowed and whose up is not seen yet (hook thread only)

    public DeskMenuKeys(DispatcherQueue q, Action<string> key, Action<string> log, bool test)
    {
        _q = q; _key = key; _log = log; _test = test; _proc = Proc;
    }

    /// Arm for one open menu; false (and why, in the log) when Windows refused the hook.
    public bool Arm()
    {
        if (_armed) return true;
        _armed = true;
        bool ok = false;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, r => ok = r)) { IsBackground = true, Name = "desktop menu keys" };
        _thread.Start();
        ready.Wait(1000);
        if (!ok) { _armed = false; _log("menu keys: Windows refused the keyboard hook, so the menu takes the mouse only"); }
        return ok;
    }

    public void Disarm()
    {
        if (!_armed) return;
        _armed = false;
        if (_tid != 0) FrameNative.PostThreadMessageW(_tid, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread = null;
    }

    void Run(ManualResetEventSlim ready, Action<bool> said)
    {
        _tid = FrameNative.GetCurrentThreadId();
        _held = 0;
        IntPtr h = FrameNative.SetWindowsHookExW(13 /* WH_KEYBOARD_LL */, _proc, FrameNative.GetModuleHandleW(IntPtr.Zero), 0);
        said(h != IntPtr.Zero);
        ready.Set();
        if (h == IntPtr.Zero) { _tid = 0; return; }
        while (FrameNative.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
        FrameNative.UnhookWindowsHookEx(h);
        _tid = 0;
    }

    static int Bit(int vk) => vk switch { DeskMenu.VkUp => 1, DeskMenu.VkDown => 2, DeskMenu.VkHome => 4, DeskMenu.VkEnd => 8, DeskMenu.VkReturn => 16, _ => 0 };

    IntPtr Proc(int code, IntPtr wp, IntPtr lp)
    {
        if (code >= 0) {
            int msg = (int)wp.ToInt64();
            bool down = msg == 0x0100 || msg == 0x0104, up = msg == 0x0101 || msg == 0x0105;
            int vk = Marshal.ReadInt32(lp, 0);   // KBDLLHOOKSTRUCT.vkCode
            int bit = Bit(vk);
            if (bit != 0 && (down || up)) {
                bool anyMod = down && (Pressed(0x10) || Pressed(0x11) || Pressed(0x12) || Pressed(0x5B) || Pressed(0x5C));
                bool mantleInFront = down && !_test && MantleInFront();
                int v = DeskMenu.NavVerdict(vk, down, anyMod, mantleInFront, false, _armed, (_held & bit) != 0);
                if ((v & DeskMenu.NavHeld) != 0) _held |= bit; else _held &= ~bit;
                if ((v & DeskMenu.NavReport) != 0 && DeskMenu.NavKey(vk) is { } name) _q.TryEnqueue(() => { if (_armed) _key(name); });
                if ((v & DeskMenu.NavSwallow) != 0) return new IntPtr(1);
            }
        }
        return FrameNative.CallNextHookEx(IntPtr.Zero, code, wp, lp);
    }

    static bool Pressed(int vk) => (FrameNative.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// A Mantle window (the bar, Settings, the launcher) in front: its own keys. The menu never takes the foreground.
    bool MantleInFront()
    {
        IntPtr fg = Win32.GetForegroundWindow();
        return fg != IntPtr.Zero && Win32.GetWindowThreadProcessId(fg, out uint pid) != 0 && pid == _pid;
    }
}
