using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Mantle.Core.Features.Corners;

/// <summary>Where the cursor is and what the displays are, in DIP. The live one reads Windows; a --test run uses
/// <see cref="TestCornerPointer"/> only, so a test never reads (or moves) the real cursor.</summary>
public interface ICornerPointer
{
    /// The cursor in DIP, or null with why (GetCursorPos FALSE: the lock / sign-in / UAC desktop has the input).
    (DipPoint? Pt, string? Why) Read();
    /// The mouse buttons (read only: GetAsyncKeyState); called only while the cursor is in an active corner.
    ButtonRead Buttons();
    /// Every display's bounds in DIP; the primary first.
    IReadOnlyList<DipRect> Displays();
}

/// <summary>The test hook: a made-up cursor and buttons (the self-test and the unit tests drive it).</summary>
public sealed class TestCornerPointer : ICornerPointer
{
    public DipPoint? Pt { get; set; }
    public string? Why { get; set; } = "the test has not reported a cursor yet";
    public ButtonRead Btn { get; set; } = new(true);
    public List<DipRect> Screens { get; } = new() { new DipRect(0, 0, 1536, 864) };
    public int ButtonReads { get; private set; }
    public (DipPoint? Pt, string? Why) Read() => (Pt, Pt == null ? Why : null);
    public ButtonRead Buttons() { ButtonReads++; return Btn; }
    public IReadOnlyList<DipRect> Displays() => Screens.ToArray();
}

/// <summary>The live reader: user32 GetCursorPos (physical pixels, Mantle is per-monitor DPI aware), converted to DIP
/// on the monitor under it with that monitor's own scale; GetAsyncKeyState for the buttons. Never throws.</summary>
public sealed class Win32CornerPointer : ICornerPointer
{
    public (DipPoint? Pt, string? Why) Read()
    {
        try {
            if (!GetCursorPos(out var p)) return (null, "Windows would not say where it is (GetCursorPos failed: the sign-in or lock screen has the input)");
            IntPtr mon = MonitorFromPoint(p, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (!Monitor(mon, out var r, out double s)) return (null, "the display under the cursor could not be read");
            var o = DipOrigin(r, s);
            return (new DipPoint(o.X + (p.X - r.Left) / s, o.Y + (p.Y - r.Top) / s), null);
        } catch (Exception e) { return (null, "the cursor read threw: " + e.Message); }
    }

    public ButtonRead Buttons()
    {
        try {
            bool held = false, pressed = false;
            foreach (int vk in new[] { 0x01, 0x02, 0x04, 0x05, 0x06 }) {   // left, right, middle, X1, X2
                short k = GetAsyncKeyState(vk);
                if ((k & 0x8000) != 0) held = true;
                if ((k & 0x0001) != 0) pressed = true;
            }
            return new ButtonRead(true, held, pressed);
        } catch (Exception e) { return new ButtonRead(false, Reason: "GetAsyncKeyState threw: " + e.Message); }
    }

    public IReadOnlyList<DipRect> Displays()
    {
        var all = new List<(bool Primary, DipRect R)>();
        try {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => {
                if (Monitor(h, out var r, out double s, out bool primary)) {
                    var o = DipOrigin(r, s);
                    all.Add((primary, new DipRect(o.X, o.Y, Math.Round((r.Right - r.Left) / s), Math.Round((r.Bottom - r.Top) / s))));
                }
                return true;
            }, IntPtr.Zero);
        } catch { /* none: the caller falls back to the primary */ }
        all.Sort((a, b) => b.Primary.CompareTo(a.Primary));
        return all.ConvertAll(a => a.R);
    }

    /// A monitor's DIP origin: its physical origin divided by its own scale (Electron's rule for a display at 0,0 and
    /// for same-scale neighbours; mixed-scale neighbours may gain a gap, see docs/lanes/desktop.md).
    static DipPoint DipOrigin(RECT r, double s) => new(Math.Round(r.Left / s), Math.Round(r.Top / s));

    static bool Monitor(IntPtr mon, out RECT r, out double scale) => Monitor(mon, out r, out scale, out _);
    static bool Monitor(IntPtr mon, out RECT r, out double scale, out bool primary)
    {
        r = default; scale = 1; primary = false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
        r = mi.rcMonitor; primary = (mi.dwFlags & 1) != 0;
        try { if (GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0) scale = dx / 96.0; } catch { scale = 1; }
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnum(IntPtr h, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum fn, IntPtr data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    [DllImport("user32.dll", SetLastError = true)] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    /// user32 LockWorkStation, live only (a --test run never calls it).
    public static CornerFx Lock()
    {
        try { return LockWorkStation() ? new CornerFx(true, Called: "LockWorkStation") : new CornerFx(false, "LockWorkStation returned FALSE: Windows would not lock"); }
        catch (Exception e) { return new CornerFx(false, $"LockWorkStation could not be called ({e.Message})"); }
    }

    /// The foreground window now (the self-test's "it never changed" check).
    public static IntPtr Foreground() { try { return GetForegroundWindow(); } catch { return IntPtr.Zero; } }
}
