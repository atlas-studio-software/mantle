using System.Runtime.InteropServices;
using System.Text;

namespace Mantle.UiTests;

/// <summary>The few Win32 calls the tests need to judge real window state (foreground, minimized, monitor).</summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);

    public const uint WM_CLOSE = 0x0010;

    public static void DpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4));   // per-monitor v2: physical px

    public static string Title(IntPtr h)
    {
        var b = new StringBuilder(512);
        GetWindowTextW(h, b, b.Capacity);
        return b.ToString();
    }

    /// <summary>Visible top-level windows of a process, optionally only those with this exact title.</summary>
    public static List<IntPtr> WindowsOf(int pid, string? title = null)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p == (uint)pid && IsWindowVisible(h) && (title == null || Title(h) == title)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static RECT MonitorRect(IntPtr h)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(h, 2), ref mi);
        return mi.rcMonitor;
    }

    /// <summary>OS-managed auto-hide for the runner's own taskbar, so it never covers the bar the tests click.</summary>
    public static void TaskbarAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindowW("Shell_TrayWnd", null), lParam = new IntPtr(on ? 1 : 2) };
        SHAppBarMessage(0x0000000A, ref d);
    }
}
