using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Surfaces.Bar.Frame;

/// <summary>
/// TEST RUNS ONLY: --test with MANTLE_TEST_IDLE_PROBE=&lt;delay ms&gt;,&lt;window ms&gt; (the idle UI test sets it). That long
/// after the hover bar first hides at rest, it counts for &lt;window&gt; what wakes the app: each message the UI thread takes
/// from its queue (WH_GETMESSAGE) or is sent from another thread (WH_CALLWNDPROC), by message and window class (a
/// WM_TIMER by its id and its timer proc's module), plus the host side's <see cref="IdleTally"/> (timers fired, host-thread
/// posts, sidecar lines). Then one "idle probe:" line per entry and "idle probe: done". Other runs never read the variable.
/// </summary>
internal static unsafe class IdleProbe
{
    static readonly Dictionary<string, int> Msgs = new(StringComparer.Ordinal);
    static readonly Dictionary<IntPtr, string> Classes = new();
    static readonly Dictionary<string, (uint Last, List<uint> Gaps)> Times = new(StringComparer.Ordinal);
    static IntPtr _get, _call;
    static int _errors;

    public static void Arm(DispatcherQueue q)
    {
        if (!Host.Args.Test || Host.Env("MANTLE_TEST_IDLE_PROBE") is not { Length: > 0 } v) return;
        var p = v.Split(',');
        if (p.Length != 2 || !int.TryParse(p[0], out int delay) || !int.TryParse(p[1], out int window) || delay < 0 || window <= 0) {
            Host.Log.Line("idle probe: MANTLE_TEST_IDLE_PROBE is not <delay ms>,<window ms>: " + v);
            return;
        }
        Host.Log.Line($"idle probe: armed: counting starts in {delay} ms and lasts {window} ms");
        Once(q, delay, () => Begin(q, window));
    }

    static DispatcherQueueTimer? _timer;   // held: a timer nothing references can be collected before it fires

    static void Once(DispatcherQueue q, int ms, Action a)
    {
        var t = _timer = q.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.IsRepeating = false;
        t.Tick += (s, _) => { s.Stop(); a(); };
        t.Start();
    }

    static void Begin(DispatcherQueue q, int window)
    {
        Msgs.Clear();
        uint tid = GetCurrentThreadId();
        _get = SetWindowsHookExW(WH_GETMESSAGE, (IntPtr)(void*)(delegate* unmanaged<int, IntPtr, IntPtr, IntPtr>)&OnGet, IntPtr.Zero, tid);
        _call = SetWindowsHookExW(WH_CALLWNDPROC, (IntPtr)(void*)(delegate* unmanaged<int, IntPtr, IntPtr, IntPtr>)&OnCall, IntPtr.Zero, tid);
        IdleTally.Start();
        Times.Clear();
        Windows(tid);
        string res = NtQueryTimerResolution(out uint coarsest, out uint finest, out uint now) == 0 ? $"; the timer resolution is {now / 10000.0:0.###} ms now ({finest / 10000.0:0.###} to {coarsest / 10000.0:0.###})" : "";
        Host.Log.Line($"idle probe: counting for {window} ms (queue hook {(_get != IntPtr.Zero ? "on" : "FAILED")}, sent-message hook {(_call != IntPtr.Zero ? "on" : "FAILED")}){res}");
        Once(q, window, End);
    }

    static void End()
    {
        if (_get != IntPtr.Zero) UnhookWindowsHookEx(_get);
        if (_call != IntPtr.Zero) UnhookWindowsHookEx(_call);
        _get = _call = IntPtr.Zero;
        var host = IdleTally.Stop();
        foreach (var kv in Msgs.OrderByDescending(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal)) Host.Log.Line($"idle probe: ui {kv.Value} x {kv.Key}");
        foreach (var kv in host) Host.Log.Line($"idle probe: host {kv.Value} x {kv.Key}");
        foreach (var (key, t) in Times) if (t.Gaps.Count >= 10) { var g = t.Gaps.OrderBy(x => x).ToList(); Host.Log.Line($"idle probe: gaps {key}: median {g[g.Count / 2]} ms, 10th {g[g.Count / 10]} ms, 90th {g[g.Count * 9 / 10]} ms"); }
        Host.Log.Line($"idle probe: done ({Msgs.Values.Sum()} UI-thread messages, {host.Sum(k => k.Value)} host-side hits{(_errors > 0 ? $", {_errors} hook errors" : "")})");
    }

    [UnmanagedCallersOnly]
    static IntPtr OnGet(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)1) {   // PM_REMOVE: taken off the queue (a PM_NOREMOVE peek is not a wake)
            var m = (MSG*)lParam;
            string k = Count("queued", m->message, m->hwnd, m->wParam, m->lParam);
            if (k.Length > 0) Gap(k, m->time);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    [UnmanagedCallersOnly]
    static IntPtr OnCall(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == IntPtr.Zero) {   // sent by another thread or process (it woke this one)
            var c = (CWPSTRUCT*)lParam;
            Count("sent", c->message, c->hwnd, c->wParam, c->lParam);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    static string Count(string how, uint msg, IntPtr hwnd, IntPtr wp, IntPtr lp)
    {
        try {
            string key = $"{how} {MsgName(msg)} to {(hwnd == IntPtr.Zero ? "the thread" : ClassOf(hwnd))}";
            if (msg == 0x0113 || msg == 0x0118) key += $" (timer id 0x{wp.ToInt64():x}, proc {ModuleOf(lp)})";
            else if (msg == 0x0060) key += $" (wParam 0x{wp.ToInt64():x}, lParam 0x{lp.ToInt64():x})";
            Msgs[key] = Msgs.TryGetValue(key, out int n) ? n + 1 : 1;
            return key;
        } catch (Exception) { _errors++; return ""; }
    }

    static string MsgName(uint m) => m switch {
        0x000F => "WM_PAINT", 0x0113 => "WM_TIMER", 0x0118 => "WM_SYSTIMER", 0x001A => "WM_SETTINGCHANGE",
        0x007E => "WM_DISPLAYCHANGE", 0x0219 => "WM_DEVICECHANGE", 0x0218 => "WM_POWERBROADCAST", 0x031A => "WM_THEMECHANGED",
        0x0200 => "WM_MOUSEMOVE", 0x02A3 => "WM_MOUSELEAVE", 0x0084 => "WM_NCHITTEST", 0x0046 => "WM_WINDOWPOSCHANGING",
        0x0047 => "WM_WINDOWPOSCHANGED", 0x0006 => "WM_ACTIVATE", 0x001C => "WM_ACTIVATEAPP", 0x0086 => "WM_NCACTIVATE",
        0x02B1 => "WM_WTSSESSION_CHANGE", 0x0320 => "WM_DWMCOLORIZATIONCOLORCHANGED", 0x031E => "WM_DWMCOMPOSITIONCHANGED",
        0x0000 => "WM_NULL",
        >= 0xC000 and <= 0xFFFF => "registered \"" + Registered(m) + "\"",
        >= 0x8000 and < 0xC000 => $"WM_APP+0x{m - 0x8000:x}",
        >= 0x0400 and < 0x8000 => $"WM_USER+0x{m - 0x0400:x}",
        _ => $"0x{m:x4}",
    };

    static string Registered(uint m)
    {
        char* b = stackalloc char[128];
        int n = GetClipboardFormatNameW(m, b, 128);
        return n > 0 ? new string(b, 0, n) : $"0x{m:x4}";
    }

    static string ClassOf(IntPtr h)
    {
        if (Classes.TryGetValue(h, out var c)) return c;
        char* b = stackalloc char[128];
        int n = GetClassNameW(h, b, 128);
        return Classes[h] = n > 0 ? new string(b, 0, n) : "(gone)";
    }

    static string ModuleOf(IntPtr addr)
    {
        if (addr == IntPtr.Zero) return "none";
        if (!GetModuleHandleExW(6, addr, out IntPtr mod)) return $"0x{addr.ToInt64():x}";   // FROM_ADDRESS | UNCHANGED_REFCOUNT
        char* b = stackalloc char[260];
        int n = GetModuleFileNameW(mod, b, 260);
        string path = n > 0 ? new string(b, 0, n) : "?";
        return $"{System.IO.Path.GetFileName(path)}+0x{addr.ToInt64() - mod.ToInt64():x}";
    }

    static void Gap(string key, uint time)
    {
        if (!Times.TryGetValue(key, out var t)) { Times[key] = (time, new List<uint>()); return; }
        t.Gaps.Add(time - t.Last);
        Times[key] = (time, t.Gaps);
    }

    /// The UI thread's windows (class, visible, size) and its focus and caret, once at the start of the window.
    static void Windows(uint tid)
    {
        var list = new List<string>();
        EnumThreadWindows(tid, (h, _) => {
            GetWindowRect(h, out var r);
            list.Add($"{ClassOf(h)} {(IsWindowVisible(h) ? "visible" : "hidden")} {r.R - r.L}x{r.B - r.T}");
            return true;
        }, IntPtr.Zero);
        Host.Log.Line($"idle probe: {list.Count} UI-thread windows: {string.Join("; ", list)}");
        var gi = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (GetGUIThreadInfo(tid, ref gi))
            Host.Log.Line($"idle probe: focus {(gi.hwndFocus == IntPtr.Zero ? "none" : ClassOf(gi.hwndFocus))}, active {(gi.hwndActive == IntPtr.Zero ? "none" : ClassOf(gi.hwndActive))}, caret {(gi.hwndCaret == IntPtr.Zero ? "none" : ClassOf(gi.hwndCaret))}, flags 0x{gi.flags:x}");
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO { public uint cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public RECT rcCaret; }
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint tid, EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO gi);

    const int WH_GETMESSAGE = 3, WH_CALLWNDPROC = 4;
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct CWPSTRUCT { public IntPtr lParam; public IntPtr wParam; public uint message; public IntPtr hwnd; }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, IntPtr proc, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern int GetClassNameW(IntPtr h, char* buf, int len);
    [DllImport("user32.dll")] static extern int GetClipboardFormatNameW(uint f, char* buf, int len);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
    [DllImport("kernel32.dll")] static extern bool GetModuleHandleExW(uint flags, IntPtr addr, out IntPtr mod);
    [DllImport("kernel32.dll")] static extern int GetModuleFileNameW(IntPtr mod, char* buf, int len);
}
