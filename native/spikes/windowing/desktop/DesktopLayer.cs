#nullable enable
// DesktopLayer.cs - put a window INTO the desktop: under the icons, above the wallpaper, behind every
// app. A port of the sidecar's Desktop.cs (Attach / Recheck / Fit / Detach) for a WinUI 3 process: it
// works on any HWND, so the native shell calls it with its WinUI window's handle
// (WinRT.Interop.WindowNative.GetWindowHandle). Plain Win32, no WinUI types: this is the file later
// lanes copy.
//
// The undocumented part: SendMessageTimeout(Progman, 0x052C, 0xD, ...) asks Progman to make the
// wallpaper WorkerW. Where it then lives depends on the Windows build, so Attach probes the known
// shapes and says which one it used (the modes, as in Desktop.cs):
//   progman-front    24H2 and later: SHELLDLL_DefView (the icons), any wallpaper player and the
//                    WorkerW are all CHILDREN of Progman, top to bottom. We become a child of Progman
//                    straight under DefView: in front of a video wallpaper, under the icons.
//   workerw-sibling  classic Win10: a TOP-LEVEL WorkerW after the one hosting SHELLDLL_DefView.
//   workerw-child    the WorkerW is a CHILD of Progman (we go inside it, at its bottom).
//   progman          nothing else matched: a child of Progman at the bottom.
// `want` forces one mode (for the spike's per-mode runs); "auto" = the first that matches, in the
// order above, which is what Desktop.cs does.
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DesktopLayer
{
    const uint WM_SPAWN_WORKER = 0x052C;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_CHILD = 0x40000000, WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000,
               WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
               WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
               SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
    const uint GA_PARENT = 1, GW_HWNDPREV = 3;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    static IntPtr _origStyle, _origEx;
    static IntPtr _origFor;

    public static string? Attach(IntPtr hwnd, string want, out string? mode)
    {
        mode = null;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return "no window to attach";
        IntPtr progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return "no Progman window: the desktop is not running (explorer restarting?)";

        SendMessageTimeoutW(progman, WM_SPAWN_WORKER, new IntPtr(0xD), IntPtr.Zero, 0, 1000, out _);
        SendMessageTimeoutW(progman, WM_SPAWN_WORKER, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);

        IntPtr defView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr target = IntPtr.Zero, insertAfter = HWND_BOTTOM;
        bool any = want == "auto";
        if ((any || want == "progman-front") && defView != IntPtr.Zero) { target = progman; insertAfter = defView; mode = "progman-front"; }
        if (target == IntPtr.Zero && (any || want == "workerw-sibling")) {
            IntPtr w = FindSiblingWorkerW();
            if (w != IntPtr.Zero) { target = w; mode = "workerw-sibling"; }
        }
        if (target == IntPtr.Zero && (any || want == "workerw-child")) {
            IntPtr w = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
            if (w != IntPtr.Zero) { target = w; mode = "workerw-child"; }
        }
        if (target == IntPtr.Zero && (any || want == "progman")) { target = progman; mode = "progman"; }
        if (target == IntPtr.Zero) return "mode " + want + " does not exist on this desktop (defView under Progman: " + (defView != IntPtr.Zero) + ")";

        if (_origFor != hwnd) { _origFor = hwnd; _origStyle = GetWindowLongPtrW(hwnd, GWL_STYLE); _origEx = GetWindowLongPtrW(hwnd, GWL_EXSTYLE); }

        // WS_CHILD BEFORE SetParent: a window that is still WS_POPUP when it gets a parent keeps
        // top-level behaviour (activation, its own frame), and WinUI's frame bits must go anyway.
        long st = GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64();
        st &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        st |= WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtrW(hwnd, GWL_STYLE, new IntPtr(st));
        SetParent(hwnd, target);
        int err = Marshal.GetLastWin32Error();
        // SetParent's return is the OLD parent (zero for a top-level window), so ask what it is now.
        IntPtr ancestor = GetAncestor(hwnd, GA_PARENT);
        if (ancestor != target && !IsChild(target, hwnd))
            return "SetParent did not take: parent is " + ancestor.ToInt64() + ", wanted " + target.ToInt64() + " (" + mode + "), Win32 error " + err;
        SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        return null;
    }

    /// Still where Attach put it? Explorer restarted (our parent is not today's Progman / WorkerW):
    /// attach again. progman-front: something visible got between DefView and us (a wallpaper
    /// player): go back under DefView and say what it was. moved = something was done.
    public static string? Recheck(IntPtr hwnd, string want, string? mode, out bool moved, out string? why)
    {
        moved = false; why = null;
        if (!IsWindow(hwnd)) return "the window is gone";
        IntPtr progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return "no Progman (explorer is restarting)";
        IntPtr parent = GetAncestor(hwnd, GA_PARENT);
        bool inDesktop = parent == progman || GetAncestor(parent, GA_PARENT) == progman || IsInSiblingWorkerW(parent);
        if (!inDesktop) {
            string? e = Attach(hwnd, want, out string? m);
            if (e != null) return "rejoin after explorer restart failed: " + e;
            moved = true; why = "parent " + parent.ToInt64() + " is not in the desktop any more (explorer restarted): rejoined as " + m;
            return null;
        }
        if (mode != "progman-front") return null;
        IntPtr defView = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero) return "Progman has no SHELLDLL_DefView";
        for (IntPtr w = GetWindow(hwnd, GW_HWNDPREV); w != IntPtr.Zero && w != defView; w = GetWindow(w, GW_HWNDPREV)) {
            if (!IsWindowVisible(w)) continue;
            why = "covered by " + ClassOf(w) + " (" + w.ToInt64() + ")";
            SetWindowPos(hwnd, defView, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            moved = true;
            break;
        }
        return null;
    }

    /// Cover the main monitor (physical px; the process is per-monitor DPI aware). A child's
    /// SetWindowPos numbers are its parent's client coordinates, so the point is mapped first.
    public static string? Fit(IntPtr hwnd, out string rect)
    {
        rect = "";
        IntPtr mon = MonitorFromPoint(new PT(), 1 /* PRIMARY */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return "no main monitor";
        int x = mi.rcMonitor.L, y = mi.rcMonitor.T, w = mi.rcMonitor.R - x, h = mi.rcMonitor.B - y;
        var p = new PT { X = x, Y = y };
        if ((GetWindowLongPtrW(hwnd, GWL_STYLE).ToInt64() & WS_CHILD) != 0) ScreenToClient(GetAncestor(hwnd, GA_PARENT), ref p);
        if (!SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
            return "SetWindowPos failed: " + Marshal.GetLastWin32Error();
        GetWindowRect(hwnd, out var r);
        rect = r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T);
        if (r.L != x || r.T != y || r.R - r.L != w || r.B - r.T != h) return "asked for " + x + "," + y + " " + w + "x" + h + ", got " + rect;
        return null;
    }

    /// Back to the top-level window it was, with the exact styles it had.
    public static string? Detach(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return "the window is gone";
        SetParent(hwnd, IntPtr.Zero);
        if (_origFor == hwnd) {
            SetWindowLongPtrW(hwnd, GWL_STYLE, _origStyle);
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, _origEx);
        }
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        return null;
    }

    static IntPtr FindSiblingWorkerW()
    {
        IntPtr found = IntPtr.Zero; bool seen = false;
        EnumWindows((h, l) => {
            if (FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) { seen = true; return true; }
            if (seen && ClassOf(h) == "WorkerW") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
    static bool IsInSiblingWorkerW(IntPtr parent) => parent != IntPtr.Zero && parent == FindSiblingWorkerW();

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }

    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RC { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RC rcMonitor, rcWork; public uint dwFlags; }
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? c, string? t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr after, string? c, string? t);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr p, IntPtr c);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref PT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PT p, uint f);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
}
