// BenchNative.cs - Win32 helpers for run-bench.ps1 (compiled with Add-Type in PowerShell 7).
// Runner only: it moves the cursor, clicks, changes the wallpaper and the taskbar's auto-hide.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class BenchNative
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int x, y; public uint clrUsed, clrImportant; }

    /// BGRA bytes of a screen rectangle (what DWM shows, layered windows included).
    public static byte[] Grab(int x, int y, int w, int h)
    {
        IntPtr s = GetDC(IntPtr.Zero), m = CreateCompatibleDC(s), b = CreateCompatibleBitmap(s, w, h);
        IntPtr old = SelectObject(m, b);
        try {
            BitBlt(m, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000);
            SelectObject(m, old);
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            GetDIBits(m, b, 0, (uint)h, bits, ref bmi, 0);
            return bits;
        } finally { DeleteObject(b); DeleteDC(m); ReleaseDC(IntPtr.Zero, s); }
    }

    /// Mean absolute difference per channel (0-255) between two grabs, every 4th pixel.
    public static double Diff(byte[] a, byte[] b)
    {
        long sum = 0; long n = 0;
        for (int i = 0; i + 2 < a.Length && i + 2 < b.Length; i += 16) {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            n += 3;
        }
        return n == 0 ? 0 : (double)sum / n;
    }

    // ---- input
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }   // 40 bytes on x64: MOUSEINPUT is the largest union member

    /// A real left click at a screen point (physical pixels), through the input queue.
    public static uint Click(int x, int y)
    {
        SetCursorPos(x, y);
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int nx = (int)Math.Round(x * 65535.0 / (sw - 1)), ny = (int)Math.Round(y * 65535.0 / (sh - 1));
        const uint MOVE = 0x1, DOWN = 0x2, UP = 0x4, ABS = 0x8000;
        var inp = new INPUT[3];
        inp[0].type = 0; inp[0].mi = new MOUSEINPUT { dx = nx, dy = ny, flags = MOVE | ABS };
        inp[1].type = 0; inp[1].mi = new MOUSEINPUT { dx = nx, dy = ny, flags = DOWN | ABS };
        inp[2].type = 0; inp[2].mi = new MOUSEINPUT { dx = nx, dy = ny, flags = UP | ABS };
        return SendInput(3, inp, Marshal.SizeOf<INPUT>());
    }

    // ---- foreground
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static uint ForegroundPid() { GetWindowThreadProcessId(GetForegroundWindow(), out uint p); return p; }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    /// Visible, unowned, titled top-level windows of a process.
    public static IntPtr[] WindowsOf(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid && IsWindowVisible(h) && GetWindow(h, 4) == IntPtr.Zero) {
                var sb = new System.Text.StringBuilder(256); GetWindowTextW(h, sb, 256);
                if (sb.Length > 0) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    public static bool AnyIconic(uint pid) { foreach (var h in WindowsOf(pid)) if (IsIconic(h)) return true; return false; }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    /// left, top, right, bottom of a window (screen pixels).
    public static int[] RectOf(IntPtr h) { GetWindowRect(h, out RECT r); return new[] { r.l, r.t, r.r, r.b }; }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    /// The process whose top-level window is on screen at a point (what a click there would hit).
    public static uint PidAt(int x, int y)
    {
        IntPtr h = GetAncestor(WindowFromPoint(new POINT { x = x, y = y }), 2 /* GA_ROOT */);
        GetWindowThreadProcessId(h, out uint p); return p;
    }
    /// Top of the z-order without activating it (activation is left to a real click).
    public static bool Raise(IntPtr h) => SetWindowPos(h, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE | NOMOVE | NOACTIVATE */);

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);

    /// Tile every ordinary app window into the top part of the screen, so the bottom `clear` pixels
    /// show only the wallpaper (the same picture behind both bars). Returns how many it moved.
    public static int ClearBottom(int screenW, int screenH, int clear)
    {
        var skip = new HashSet<string> { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
            if ((GetWindowLongPtrW(h, -20).ToInt64() & 0x80) != 0) return true;   // tool windows
            var sb = new System.Text.StringBuilder(256); GetClassNameW(h, sb, 256);
            if (skip.Contains(sb.ToString())) return true;
            var t = new System.Text.StringBuilder(256); GetWindowTextW(h, t, 256);
            if (t.Length == 0 || t.ToString().StartsWith("Mantle", StringComparison.Ordinal)) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        int n = list.Count, cols = Math.Max(1, Math.Min(4, n)), w = screenW / cols, hgt = Math.Max(200, screenH - clear);
        for (int i = 0; i < n; i++) {
            if (IsZoomed(list[i]) || IsIconic(list[i])) ShowWindow(list[i], 9 /* SW_RESTORE */);
            SetWindowPos(list[i], IntPtr.Zero, (i % cols) * w, 0, w, hgt, 0x4 | 0x10 /* NOZORDER | NOACTIVATE */);
        }
        return n;
    }

    // ---- desktop setup
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfoW(uint a, uint b, string c, uint d);
    public static bool SetWallpaper(string path) => SystemParametersInfoW(0x0014, 0, path, 0x3);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string name);
    /// Windows' own taskbar on auto-hide (Mantle live does the same itself), so it is not under either bar.
    public static void TaskbarAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindowW("Shell_TrayWnd", null), lParam = new IntPtr(on ? 1 : 2) };
        SHAppBarMessage(0x0000000A /* ABM_SETSTATE */, ref d);
    }

    // ---- process tree (Toolhelp: one snapshot, parent ids)
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr s, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr s, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32 { public uint dwSize, cntUsage, th32ProcessID; public IntPtr th32DefaultHeapID; public uint th32ModuleID, cntThreads, th32ParentProcessID; public int pcPriClassBase; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile; }

    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    /// Creation time (FILETIME ticks) of a live process, or 0 when it cannot be opened.
    public static long Created(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return 0;
        try { return GetProcessTimes(h, out long c, out _, out _, out _) ? c : 0; } finally { CloseHandle(h); }
    }

    /// The root and every process below it. A child counts only when it was created after its parent:
    /// a parent id can be a dead process's, reused by the root (never walk into a stranger's tree).
    public static int[] Tree(int root)
    {
        var kids = new Dictionary<uint, List<uint>>();
        IntPtr s = CreateToolhelp32Snapshot(0x2, 0);
        try {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(s, ref e)) do {
                if (!kids.TryGetValue(e.th32ParentProcessID, out var l)) kids[e.th32ParentProcessID] = l = new List<uint>();
                if (e.th32ProcessID != e.th32ParentProcessID) l.Add(e.th32ProcessID);
            } while (Process32NextW(s, ref e));
        } finally { CloseHandle(s); }
        var all = new List<int>(); var q = new Queue<uint>(); q.Enqueue((uint)root);
        var seen = new HashSet<uint>();
        while (q.Count > 0) {
            uint p = q.Dequeue(); if (!seen.Add(p)) continue; all.Add((int)p);
            if (!kids.TryGetValue(p, out var l)) continue;
            long pc = Created(p);
            foreach (var k in l) { long kc = Created(k); if (pc == 0 || kc == 0 || kc >= pc) q.Enqueue(k); }
        }
        return all.ToArray();
    }

    // ---- screen size (the runner desktop starts at 1024x768)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency,
          dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsW(ref DEVMODE dm, int flags);
    /// The primary screen's size now (WinForms' Screen caches it across a mode change). GetSystemMetrics is declared above (Click).
    public static int[] ScreenSize() => new[] { GetSystemMetrics(0), GetSystemMetrics(1) };

    /// Set the primary screen's size for this session. 0 = done (DISP_CHANGE_SUCCESSFUL).
    public static int SetRes(int w, int h)
    {
        var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        if (!EnumDisplaySettingsW(null, -1, ref dm)) return -100;
        if (dm.dmPelsWidth == w && dm.dmPelsHeight == h) return 0;
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
        return ChangeDisplaySettingsW(ref dm, 0);
    }
}
