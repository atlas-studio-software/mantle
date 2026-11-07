using System;
using System.Runtime.InteropServices;
using Mantle.App.Native;

namespace Mantle.App.Surfaces.Bar.Frame;

/// main.js barGeometry for the pill, in physical px, always from the CURRENT primary monitor (BAR-084, BAR-087,
/// BAR-088): the pill is min(1120, screen - 28) DIP wide (at least 120), 48 DIP tall, resting 14 DIP above the
/// bottom edge. The window holds the pill plus room for the light drop shadow above and beside it; its bottom
/// is the screen's physical bottom, never a rounded height. The reveal band is the bottom 4 DIP (BAR-026).
internal readonly record struct BarGeometry(uint Dpi, double Scale, Win32.RECT Monitor, double PillWdip, Win32.RECT Window, Win32.RECT Pill, int BandTop)
{
    public const double BarH = 48, Lift = 14, SideGapTotal = 28, MaxW = 1120, BandDip = 4;
    public const double ShadowTop = 56, ShadowSide = 28;   // room in the window for the light drop shadow

    public static BarGeometry Compute()
    {
        IntPtr mon = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, 1 /* PRIMARY */);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        uint dpi = Win32.GetDpiForMonitor(mon, 0, out uint d, out _) == 0 ? d : 96;
        return For(mi.rcMonitor, dpi);
    }

    public static BarGeometry For(Win32.RECT m, uint dpi)
    {
        double s = dpi / 96.0;
        double wDip = Math.Min(MaxW, Math.Max(120, (m.Right - m.Left) / s - SideGapTotal));
        int wPx = (int)Math.Round((wDip + 2 * ShadowSide) * s), hPx = (int)Math.Round((ShadowTop + BarH + Lift) * s);
        int x = m.Left + ((m.Right - m.Left) - wPx) / 2, y = m.Bottom - hPx;
        var pill = new Win32.RECT { Left = x + (int)Math.Round(ShadowSide * s), Top = y + (int)Math.Round(ShadowTop * s) };
        pill.Right = pill.Left + (int)Math.Round(wDip * s);
        pill.Bottom = pill.Top + (int)Math.Round(BarH * s);
        var win = new Win32.RECT { Left = x, Top = y, Right = x + wPx, Bottom = m.Bottom };
        return new BarGeometry(dpi, s, m, wDip, win, pill, m.Bottom - (int)Math.Round(BandDip * s));
    }

    /// The same bar moved so its pill fills a dock slot Explorer gave (BAR-106): centred in the slot's height.
    public BarGeometry InSlot(Win32.RECT slot)
    {
        int ph = Pill.Bottom - Pill.Top, top = slot.Top + ((slot.Bottom - slot.Top) - ph) / 2, dy = top - Pill.Top;
        var p = Pill; p.Top += dy; p.Bottom += dy;
        var w = Window; w.Top += dy; w.Bottom += dy;
        return this with { Pill = p, Window = w };
    }

    public bool SameAs(BarGeometry o) => Dpi == o.Dpi && Pill.Equals(o.Pill) && Window.Equals(o.Window);

    public string Describe() =>
        $"primary {FrameNative.Rect(Monitor)} at {Math.Round(Scale * 100)} % (dpi {Dpi}); pill {FrameNative.Rect(Pill)} ({PillWdip:0.#} DIP); window {FrameNative.Rect(Window)}; band from y {BandTop}";
}

/// lib/bar-intent.js, physical px. "in" = the reveal band, the column under the pill down to the screen edge,
/// the window, or the pill's rest rect grown 8 DIP up and sideways (BAR-095, BAR-098); "overshoot" = grown 28 DIP
/// up and 12 DIP sideways (BAR-096); anything else is "out" (BAR-097).
internal static class BarIntent
{
    public const double KeepAbove = 8, KeepSide = 8, OverAbove = 28, OverSide = 12;
    public const int PollHiddenMs = 80, PollShownMs = 60, HideGraceMs = 180, OvershootMs = 500;

    public static bool InBand(BarGeometry g, int x, int y) => x >= g.Monitor.Left && x < g.Monitor.Right && y >= g.BandTop && y < g.Monitor.Bottom;

    public static string Zone(BarGeometry g, int x, int y)
    {
        if (InBand(g, x, y)) return "in";
        var p = g.Pill; double s = g.Scale;
        var column = new Win32.RECT { Left = p.Left, Top = p.Top, Right = p.Right, Bottom = g.Monitor.Bottom };
        if (FrameNative.Contains(column, x, y)) return "in";
        Win32.RECT Grow(double above, double side) => new() {
            Left = (int)Math.Round(p.Left - side * s), Top = (int)Math.Round(p.Top - above * s),
            Right = (int)Math.Round(p.Right + side * s), Bottom = g.Monitor.Bottom };
        if (FrameNative.Contains(Grow(KeepAbove, KeepSide), x, y)) return "in";
        if (FrameNative.Contains(Grow(OverAbove, OverSide), x, y)) return "overshoot";
        return "out";
    }

    /// lib/bar-intent.js shownStep: hide after the grace once "out", or after 500 ms away from the rest band.
    /// leftAt/outAt are -1 while unset. Returns the reason to hide, or null to stay up.
    public static string? ShownStep(string zone, long now, ref long leftAt, ref long outAt)
    {
        if (zone == "in") { leftAt = outAt = -1; return null; }
        if (leftAt < 0) leftAt = now;
        if (zone == "out") { if (outAt < 0) outAt = now; } else outAt = -1;
        if (outAt >= 0 && now - outAt >= HideGraceMs) return $"the cursor has been away from the bar for {HideGraceMs} ms";
        if (now - leftAt >= Math.Max(HideGraceMs, OvershootMs)) return $"the cursor rested in the overshoot band for {OvershootMs} ms";
        return null;
    }

    /// BAR-103: the keep zone in plain words for the log.
    public static string Describe(BarGeometry g) =>
        $"kept while the cursor is below y {(int)Math.Round(g.Pill.Top - KeepAbove * g.Scale)}, for {OvershootMs} ms below y {(int)Math.Round(g.Pill.Top - OverAbove * g.Scale)}; revealed from y {g.BandTop}";
}

/// main.js foregroundBlocksReveal (BAR-091): a fullscreen window in front keeps the bar down, cached 600 ms.
internal static class FullscreenBlock
{
    static long s_at = long.MinValue / 2;
    static string? s_reason;

    public static string? Blocks(long nowMs)
    {
        if (nowMs - s_at < 600) return s_reason;
        s_at = nowMs;
        s_reason = Check();
        return s_reason;
    }

    static string? Check()
    {
        IntPtr h = Win32.GetForegroundWindow();
        if (h == IntPtr.Zero || FrameNative.IsZoomed(h)) return null;   // maximised overhangs its monitor; it is not fullscreen
        string cls = Win32.ClassName(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        Win32.GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return null;
        IntPtr mon = FrameNative.MonitorFromWindow(h, 2 /* NEAREST */);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (mon == IntPtr.Zero || !Win32.GetMonitorInfoW(mon, ref mi) || !FrameNative.GetWindowRect(h, out var r)) return null;
        var m = mi.rcMonitor;
        if (r.Left > m.Left || r.Top > m.Top || r.Right < m.Right || r.Bottom < m.Bottom) return null;
        string exe = System.IO.Path.GetFileName(Win32.ExeOf(pid));
        return $"{(exe.Length > 0 ? exe : "a window")} is fullscreen in front, so the bar stays hidden (it must not paint over a game)";
    }
}
