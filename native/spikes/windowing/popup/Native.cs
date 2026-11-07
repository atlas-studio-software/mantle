// Native.cs - the Win32 / DWM calls the popup spike needs, and ONE window subclass proc that handles
// every message the techniques touch:
//   WM_ERASEBKGND  black (alpha 0 inside the extended DWM frame = see-through), as the bar spike;
//   WM_NCCALCSIZE  return 0: the client area IS the window rect (no non-client frame at all);
//   WM_NCHITTEST   HTTRANSPARENT outside the shape (hit=nchittest).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace PopupSpike;

internal static class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L, WS_CLIPSIBLINGS = 0x04000000L, WS_CLIPCHILDREN = 0x02000000L;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }

    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int n);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetRect(IntPtr h, int a, out RECT v, int n);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] public static extern int DwmGetInt(IntPtr h, int a, out int v, int n);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    public static long GetStyle(IntPtr h) => GetWindowLongPtrW(h, GWL_STYLE).ToInt64();
    public static long GetExStyle(IntPtr h) => GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
    public static void SetExStyle(IntPtr h, long v) => SetWindowLongPtrW(h, GWL_EXSTYLE, new IntPtr(v));

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }

    /// Child windows of `parent` (all levels).
    public static List<IntPtr> Children(IntPtr parent)
    {
        var list = new List<IntPtr>();
        EnumProc cb = (h, l) => { list.Add(h); return true; };
        EnumChildWindows(parent, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    public static bool OnThisThread(IntPtr h) => GetWindowThreadProcessId(h, out _) == GetCurrentThreadId();

    /// The bar spike's see-through half: DWM frame over the whole client area (so black = alpha 0).
    public static int ExtendFrame(IntPtr hwnd)
    {
        var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
        return DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    // ------------------------------------------------------------------ the subclass
    public sealed class HookState
    {
        public bool Erase, NcCalc, HitTest;
        public Func<int, int, bool>? InShape;   // screen pixel -> inside the visible shape
        public int HitTransparent, HitPassed;   // counters, for the log
    }
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    static SubclassProc? s_proc;   // kept alive: Windows calls it
    static readonly Dictionary<IntPtr, HookState> s_state = new();

    /// Subclass `h` (must belong to this thread). Calling it again for the same window replaces its state.
    public static bool Hook(IntPtr h, HookState st)
    {
        s_state[h] = st;
        s_proc ??= Proc;
        return SetWindowSubclass(h, s_proc, new UIntPtr(0x5050), IntPtr.Zero);
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        if (s_state.TryGetValue(h, out var st)) {
            switch (msg) {
                case 0x0014 when st.Erase:   // WM_ERASEBKGND
                    if (GetClientRect(h, out var rc)) FillRect(wp, ref rc, GetStockObject(4 /* BLACK_BRUSH */));
                    return new IntPtr(1);
                case 0x0083 when st.NcCalc:  // WM_NCCALCSIZE: leave the proposed rect alone = client is the whole window
                    return IntPtr.Zero;
                case 0x0084 when st.HitTest && st.InShape != null: {   // WM_NCHITTEST
                    long v = lp.ToInt64();
                    int x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF);
                    if (!st.InShape(x, y)) { st.HitTransparent++; return new IntPtr(-1); /* HTTRANSPARENT */ }
                    st.HitPassed++;
                    break;
                }
                case 0x0082:                 // WM_NCDESTROY (comctl32 removes the subclass itself)
                    s_state.Remove(h);
                    break;
            }
        }
        return DefSubclassProc(h, msg, wp, lp);
    }

    /// The point is inside the rounded rect (x, y, w, h, radius r), pixel centres.
    public static bool InsideRound(int px, int py, int x, int y, int w, int h, int r)
    {
        if (px < x || py < y || px >= x + w || py >= y + h) return false;
        r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
        double cx = px + 0.5 < x + r ? x + r : (px + 0.5 > x + w - r ? x + w - r : px + 0.5);
        double cy = py + 0.5 < y + r ? y + r : (py + 0.5 > y + h - r ? y + h - r : py + 0.5);
        double dx = px + 0.5 - cx, dy = py + 0.5 - cy;
        return dx * dx + dy * dy <= (double)r * r;
    }
}
