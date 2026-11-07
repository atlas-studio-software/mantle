// Shell.cs - the Win32 the bar needs, ported from the Electron sidecar:
//   Taskbar     Windows' own taskbar on OS auto-hide ONLY (ABM_SETSTATE, sidecar/Taskbar.cs). Never
//               SW_HIDE, never its z-order: hiding it once left Explorer stuck and needed a reboot.
//   AppBar      the real app bar for --mode=dock (ABM_NEW / ABM_QUERYPOS / ABM_SETPOS / ABM_REMOVE,
//               sidecar/Taskbar.cs AppBars), physical pixels.
//   ShellState  the ORIGINAL state written before anything is touched, removed on a clean exit, and
//               put back by the next start (or --mode=restore) when the process was killed.
//   Foreground  "is a fullscreen window in front" (sidecar/Foreground.cs IsFullScreen): a maximised
//               window is NOT fullscreen, shell windows and our own never are.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BarSpike;

static class W
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int L, T, R, B;
        public RECT(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; }
        public readonly int Wd => R - L;
        public readonly int Ht => B - T;
        public readonly bool Contains(int x, int y) => x >= L && x < R && y >= T && y < B;
        public readonly object J => new { x = L, y = T, w = R - L, h = B - T };
        public override readonly string ToString() => $"{L},{T} {R - L}x{B - T}";
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
        SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string? cls, string? name);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfoW(uint a, uint b, ref RECT r, uint f);
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int n);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    public static uint PidOf(IntPtr h) { GetWindowThreadProcessId(h, out uint p); return p; }
    public static RECT WorkArea() { var r = new RECT(); SystemParametersInfoW(0x30 /* SPI_GETWORKAREA */, 0, ref r, 0); return r; }

    /// The primary monitor (the bar's monitor, as in Electron: screen.getPrimaryDisplay()) and its DPI.
    public static (RECT mon, RECT work, uint dpi) Primary()
    {
        IntPtr m = MonitorFromPoint(new POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(m, ref mi);
        uint dpi = GetDpiForMonitor(m, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0 ? dx : 96;
        return (mi.rcMonitor, mi.rcWork, dpi);
    }

    // DWM_TIMING_INFO is #pragma pack(1) in dwmapi.h. Only the global counters are read (hwnd NULL).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DWM_TIMING_INFO
    {
        public uint cbSize; public uint rateRefreshNum, rateRefreshDen; public ulong qpcRefreshPeriod;
        public uint rateComposeNum, rateComposeDen; public ulong qpcVBlank, cRefresh; public uint cDXRefresh;
        public ulong qpcCompose, cFrame; public uint cDXPresent; public ulong cRefreshFrame, cFrameSubmitted;
        public uint cDXPresentSubmitted; public ulong cFrameConfirmed; public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed; public uint cDXRefreshConfirmed; public ulong cFramesLate;
        public uint cFramesOutstanding; public ulong cFrameDisplayed, qpcFrameDisplayed, cRefreshFrameDisplayed,
            cFrameComplete, qpcFrameComplete, cFramePending, qpcFramePending, cFramesDisplayed, cFramesComplete,
            cFramesPending, cFramesAvailable, cFramesDropped, cFramesMissed, cRefreshNextDisplayed,
            cRefreshNextPresented, cRefreshesDisplayed, cRefreshesPresented, cRefreshStarted, cPixelsReceived,
            cPixelsDrawn, cBuffersEmpty;
    }
    [DllImport("dwmapi.dll")] static extern int DwmGetCompositionTimingInfo(IntPtr h, ref DWM_TIMING_INFO t);
    public static (int hr, DWM_TIMING_INFO t) Timing()
    {
        var t = new DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>() };
        int hr = DwmGetCompositionTimingInfo(IntPtr.Zero, ref t);
        return (hr, t);
    }
}

static class Taskbar
{
    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    public const int ABS_AUTOHIDE = 1;
    static W.APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<W.APPBARDATA>(), hWnd = W.FindWindowW("Shell_TrayWnd", null) };
    public static int State() { var d = Data(); return (int)W.SHAppBarMessage(ABM_GETSTATE, ref d).ToInt64(); }
    public static int Set(int state) { var d = Data(); d.lParam = new IntPtr(state); W.SHAppBarMessage(ABM_SETSTATE, ref d); return State(); }

    /// OR the auto-hide bit in (ABM_SETSTATE replaces the whole state). The caller has already
    /// written the original to ShellState.
    public static string AutoHide(int original)
    {
        if ((original & ABS_AUTOHIDE) != 0) return $"already on auto-hide (state {original}), left alone";
        int after = Set(original | ABS_AUTOHIDE);
        return (after & ABS_AUTOHIDE) != 0
            ? $"auto-hide on through ABM_SETSTATE (state {original} -> {after})"
            : $"Windows did not take auto-hide (ABM_SETSTATE ran, ABM_GETSTATE still {after}); the taskbar is untouched";
    }
}

static class AppBar
{
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_BOTTOM = 3;
    public const uint CallbackMsg = 0x8000 + 0x51;   // WM_APP + 0x51
    public static bool Registered { get; private set; }

    /// Reserve `size` physical px along the bottom of `mon` for `h`. Returns the rect Explorer gave,
    /// which is where the window must go. QUERYPOS may move the edge (other bars) but not keep the
    /// thickness, so the thickness is re-applied before SETPOS (sidecar/Taskbar.cs).
    public static W.RECT? Reserve(IntPtr h, W.RECT mon, int size, out string how)
    {
        var d = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), hWnd = h, uCallbackMessage = CallbackMsg, uEdge = ABE_BOTTOM };
        bool isNew = false;
        if (!Registered) {
            if (W.SHAppBarMessage(ABM_NEW, ref d) == IntPtr.Zero) { how = "ABM_NEW refused: no screen space reserved"; return null; }
            Registered = true; isNew = true;
        }
        d.rc = mon; d.rc.T = mon.B - size;
        W.SHAppBarMessage(ABM_QUERYPOS, ref d);
        d.rc.T = d.rc.B - size;
        W.SHAppBarMessage(ABM_SETPOS, ref d);
        how = $"{(isNew ? "ABM_NEW + " : "")}ABM_QUERYPOS/ABM_SETPOS {size}px -> {d.rc}";
        return d.rc;
    }
    public static void Remove(IntPtr h)
    {
        var d = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), hWnd = h };
        W.SHAppBarMessage(ABM_REMOVE, ref d);
        Registered = false;
    }
    /// For a window this process never registered (a killed instance's): Explorer matches by hwnd.
    public static void RawRemove(long hwnd)
    {
        var d = new W.APPBARDATA { cbSize = Marshal.SizeOf<W.APPBARDATA>(), hWnd = new IntPtr(hwnd) };
        W.SHAppBarMessage(ABM_REMOVE, ref d);
    }
}

/// What has to be put back, on disk BEFORE it is changed. <out>/shell-state.json.
static class ShellState
{
    public sealed class Rec { public int pid { get; set; } public int taskbarOriginal { get; set; } = -1; public bool taskbarChanged { get; set; } public long appbarHwnd { get; set; } public string at { get; set; } = ""; }
    static string FilePath => Path.Combine(App.O.Out, "shell-state.json");
    static Rec _r = new();

    public static void RecordTaskbar(int original, bool willChange)
    {
        _r.pid = Environment.ProcessId; _r.taskbarOriginal = original; _r.taskbarChanged = willChange; Save();
    }
    public static void RecordAppBar(long hwnd) { _r.pid = Environment.ProcessId; _r.appbarHwnd = hwnd; Save(); }
    static void Save()
    {
        _r.at = DateTime.Now.ToString("HH:mm:ss.fff");
        File.WriteAllText(FilePath, JsonSerializer.Serialize(_r));
    }
    public static void Clear() { try { File.Delete(FilePath); } catch { } }

    /// The dirty-start guard: a shell-state.json whose process is gone means that instance was killed
    /// with the shell changed. Put back what it recorded. Returns what was done.
    public static string RestoreIfStale()
    {
        if (!File.Exists(FilePath)) return "clean start: no shell-state.json";
        Rec? r;
        try { r = JsonSerializer.Deserialize<Rec>(File.ReadAllText(FilePath)); } catch (Exception e) { return "shell-state.json unreadable, left: " + e.Message; }
        if (r == null) return "shell-state.json empty";
        bool alive = false;
        try { alive = r.pid != Environment.ProcessId && !Process.GetProcessById(r.pid).HasExited; } catch { }
        if (alive) return $"shell-state.json belongs to running pid {r.pid}: left alone";
        var did = new StringBuilder($"pid {r.pid} is gone (killed?): ");
        if (r.taskbarChanged && r.taskbarOriginal >= 0) {
            int before = Taskbar.State();
            int after = Taskbar.Set(r.taskbarOriginal);
            did.Append($"taskbar state {before} -> {after} (original {r.taskbarOriginal}); ");
        } else did.Append("taskbar was not changed; ");
        if (r.appbarHwnd != 0) {
            var wa = W.WorkArea();
            AppBar.RawRemove(r.appbarHwnd);
            did.Append($"raw ABM_REMOVE for app bar {r.appbarHwnd} (window {(W.IsWindow(new IntPtr(r.appbarHwnd)) ? "still exists" : "gone")}), work area {wa} -> {W.WorkArea()}; ");
        }
        Clear();
        return did.Append("shell-state.json removed").ToString();
    }
}

static class Foreground
{
    static (long at, string? reason) _cache = (0, null);

    /// null = nothing blocks the reveal; otherwise why the bar must stay down. Cached 600 ms, like
    /// main.js foregroundBlocksReveal.
    public static string? Blocks(long nowMs)
    {
        if (nowMs - _cache.at < 600) return _cache.reason;
        _cache = (nowMs, Check());
        return _cache.reason;
    }
    static string? Check()
    {
        IntPtr h = W.GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        if (W.IsZoomed(h)) return null;   // a maximised window overhangs its monitor; it is not fullscreen
        string cls = W.ClassOf(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        uint pid = W.PidOf(h);
        if (pid == Environment.ProcessId) return null;
        IntPtr mon = W.MonitorFromWindow(h, 2 /* NEAREST */);
        var mi = new W.MONITORINFO { cbSize = Marshal.SizeOf<W.MONITORINFO>() };
        if (mon == IntPtr.Zero || !W.GetMonitorInfoW(mon, ref mi) || !W.GetWindowRect(h, out var r)) return null;
        var m = mi.rcMonitor;
        bool covers = r.L <= m.L && r.T <= m.T && r.R >= m.R && r.B >= m.B;
        return covers ? $"a fullscreen window is in front (class {cls}, pid {pid}, rect {r} covers monitor {m}), so the bar stays hidden" : null;
    }
}
