using System;
using System.Runtime.InteropServices;
using Mantle.App.Native;

namespace Mantle.App.Surfaces.Bar.Frame;

/// The Win32 calls the bar frame (hover, dock, display changes) and the pop-up watch need, beyond Native/Win32.cs.
internal static class FrameNative
{
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Win32.POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Win32.POINT p);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfoW(uint action, uint param, out Win32.RECT r, uint winIni);

    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const int SW_HIDE = 0;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);

    // ---- low-level input hooks for the pop-up watch (WIN-017), on their own thread with its own message loop
    public delegate IntPtr HookProc(int code, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wp, IntPtr lp);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public Win32.POINT pt; }
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public Win32.POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    public static Win32.RECT WorkArea() { SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, out var r, 0); return r; }
    public static string Rect(Win32.RECT r) => $"{r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}";
    public static bool Contains(Win32.RECT r, int x, int y) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
}
