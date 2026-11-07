// Focus.cs - a click on an app cell: bring it forward, or minimize it if it is already in front
// (the Windows taskbar's rule, and the sidecar's Windows.Toggle).
//
// Why this is simpler than the sidecar's Focus: there, the click lands in Electron and the
// SetForegroundWindow call is made by ANOTHER process (the sidecar), which Windows' foreground
// lock may refuse, hence its AttachThreadInput ladder on a helper thread with a time budget. Here
// the process that received the click is the one that asks, so Windows allows it outright. The
// attach fallback is kept for the case where it does not.
using System;

namespace MantleBar.Native;

internal static class Focus
{
    public static string Toggle(AppEntry app)
    {
        if (app.Windows.Count == 0) return "no window";
        IntPtr fg = Win32.GetForegroundWindow();
        IntPtr target = app.Windows.Contains(fg) ? fg : app.Windows[0];
        if (target == fg && !Win32.IsIconic(target)) {
            Win32.ShowWindow(target, Win32.SW_MINIMIZE);
            return "minimized";
        }
        return Raise(target);
    }

    public static string Raise(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd)) return "window gone";
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        if (Win32.SetForegroundWindow(hwnd)) return "focused";
        IntPtr fg = Win32.GetForegroundWindow();
        uint me = Win32.GetCurrentThreadId();
        uint to = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        bool attached = to != 0 && to != me && !Win32.IsHungAppWindow(fg) && Win32.AttachThreadInput(me, to, true);
        try {
            Win32.BringWindowToTop(hwnd);
            return Win32.SetForegroundWindow(hwnd) ? "focused (after attach)" : "Windows refused the foreground change";
        } finally {
            if (attached) Win32.AttachThreadInput(me, to, false);
        }
    }
}
