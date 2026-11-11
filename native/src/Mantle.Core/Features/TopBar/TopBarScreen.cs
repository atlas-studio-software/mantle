using System;
using System.Runtime.InteropServices;

namespace Mantle.Core.Features.TopBar;

/// <summary>The primary display as the top bar needs it: bounds and work area in physical px, and its scale.</summary>
public readonly record struct TopBarDisplayInfo(int X, int Y, int W, int H, int WorkY, double Scale)
{
    /// The work area's top in DIP of this display (its own frame: bounds.y / scale + offset).
    public double WorkTopDip => Y / Scale + (WorkY - Y) / Scale;
    public double TopDip => Y / Scale;
}

/// <summary>What the feature reads from the screen itself: the primary display, the cursor, the mouse buttons. READ only.</summary>
public interface ITopBarScreen
{
    TopBarDisplayInfo? Primary(out string? why);
    /// The cursor in physical px, or null with why.
    (int X, int Y)? Cursor(out string? why);
    PeekButtons Buttons();
}

/// <summary>The real one: GetMonitorInfo / GetDpiForMonitor on the primary, GetCursorPos, GetAsyncKeyState (in this process, no sidecar).</summary>
public sealed class Win32TopBarScreen : ITopBarScreen
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll", SetLastError = true)] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    public TopBarDisplayInfo? Primary(out string? why)
    {
        why = null;
        try
        {
            IntPtr mon = MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, ref mi)) { why = "Windows did not describe the primary display (GetMonitorInfo failed)"; return null; }
            double scale = GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1;
            return new TopBarDisplayInfo(mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R - mi.rcMonitor.L, mi.rcMonitor.B - mi.rcMonitor.T, mi.rcWork.T, scale);
        }
        catch (Exception e) { why = "the primary display could not be read: " + e.Message; return null; }
    }

    public (int X, int Y)? Cursor(out string? why)
    {
        why = null;
        try
        {
            if (GetCursorPos(out var p)) return (p.X, p.Y);
            why = $"GetCursorPos failed (error {Marshal.GetLastWin32Error()})";
            return null;
        }
        catch (Exception e) { why = "GetCursorPos threw: " + e.Message; return null; }
    }

    public PeekButtons Buttons()
    {
        try
        {
            bool held = false, pressed = false;
            foreach (var vk in TopBarPeek.VkMouse) { short r = GetAsyncKeyState(vk); if ((r & 0x8000) != 0) held = true; if ((r & 1) != 0) pressed = true; }
            return new(true, held, pressed);
        }
        catch (Exception e) { return new(false, Reason: "GetAsyncKeyState threw: " + e.Message); }
    }
}
