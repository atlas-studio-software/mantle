// BarProbe.cs - the bar checks' own Win32 helper (Add-Type from check*.ps1; RUNNER ONLY). It is not
// in harness/ on purpose (that folder belongs to the windowing lane); the parts worth sharing are
// named in bar/README.md. Everything here runs per-monitor-DPI aware, so every number is physical px.
//   fixtures   plain Win32 windows on their own threads: normal, maximised, fullscreen, topmost
//   trace      sample a 1 px screen column as fast as BitBlt allows and report every change (frames)
//   z-order    WindowFromPoint, rects, visibility, the work area, the taskbar state
//   displays   monitors, per-monitor DPI scale (DisplayConfig, undocumented type -3/-4), primary switch
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class BarProbe
{
    // ------------------------------------------------------------------ DPI awareness
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] static extern int GetAwarenessFromDpiAwarenessContext(IntPtr c);
    static readonly IntPtr PMv2 = new IntPtr(-4);
    static void T() { SetThreadDpiAwarenessContext(PMv2); }
    public static string Aware()
    {
        bool p = SetProcessDpiAwarenessContext(PMv2);
        T();
        return "process PMv2 " + (p ? "set" : "not set (already decided: " + Marshal.GetLastWin32Error() + ")") +
               ", thread awareness " + GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) + " (2 = per monitor)";
    }

    // ------------------------------------------------------------------ Win32
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint a, uint b, ref RECT r, uint f);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);

    static string J(RECT r) { return "{\"x\":" + r.L + ",\"y\":" + r.T + ",\"w\":" + (r.R - r.L) + ",\"h\":" + (r.B - r.T) + "}"; }
    public static string ClassOf(long h) { var sb = new StringBuilder(256); GetClassNameW(new IntPtr(h), sb, 256); return sb.ToString(); }
    public static int PidOf(long h) { uint p; GetWindowThreadProcessId(new IntPtr(h), out p); return (int)p; }
    public static long RootAt(int x, int y) { T(); IntPtr w = WindowFromPoint(new POINT { X = x, Y = y }); return w == IntPtr.Zero ? 0 : GetAncestor(w, 2).ToInt64(); }
    public static string Rect(long h) { T(); RECT r; GetWindowRect(new IntPtr(h), out r); return J(r); }
    /// What DWM draws (no invisible resize borders): the honest rect of a maximised window.
    public static string FrameRect(long h) { T(); RECT r; DwmGetWindowAttribute(new IntPtr(h), 9, out r, Marshal.SizeOf(typeof(RECT))); return J(r); }
    public static bool Visible(long h) { return IsWindowVisible(new IntPtr(h)); }
    public static bool Alive(long h) { return IsWindow(new IntPtr(h)); }
    public static bool Close(long h) { return PostMessageW(new IntPtr(h), 0x0010, IntPtr.Zero, IntPtr.Zero); }
    public static string WorkArea() { T(); var r = new RECT(); SystemParametersInfoW(0x30, 0, ref r, 0); return J(r); }
    public static long Tray() { return FindWindowW("Shell_TrayWnd", null).ToInt64(); }
    public static int TaskbarState() { var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = new IntPtr(Tray()) }; return (int)SHAppBarMessage(4, ref d).ToInt64(); }
    public static void SetTaskbarState(int s) { var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = new IntPtr(Tray()), lParam = new IntPtr(s) }; SHAppBarMessage(10, ref d); }
    public static bool Cursor(int x, int y) { T(); return SetCursorPos(x, y); }
    public static long Foreground() { return GetForegroundWindow().ToInt64(); }
    /// The ALT tap releases the foreground lock for this process, then SetForegroundWindow.
    public static bool ForceForeground(long h)
    {
        IntPtr w = new IntPtr(h);
        for (int i = 0; i < 3 && GetForegroundWindow() != w; i++) {
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetForegroundWindow(w);
            Thread.Sleep(80);
        }
        return GetForegroundWindow() == w;
    }
    public static void Maximize(long h) { ShowWindow(new IntPtr(h), 3); }
    public static void Restore(long h) { ShowWindow(new IntPtr(h), 9); }

    // ------------------------------------------------------------------ fixture windows
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int c);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref RECT r, IntPtr br);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint rgb);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string n);
    static WndProc s_proc;
    static readonly Dictionary<long, IntPtr> s_brush = new Dictionary<long, IntPtr>();
    static bool s_registered;

    static IntPtr FixtureProc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == 0x0014) {   // WM_ERASEBKGND: the fixture's colour
            IntPtr br; RECT rc;
            lock (s_brush) s_brush.TryGetValue(h.ToInt64(), out br);
            if (br != IntPtr.Zero && GetClientRect(h, out rc)) { FillRect(w, ref rc, br); return new IntPtr(1); }
        }
        if (m == 0x0002) PostQuitMessage(0);   // WM_DESTROY ends that window's thread
        return DefWindowProcW(h, m, w, l);
    }

    /// kind: "popup" (borderless, e.g. a fullscreen game), "normal" (overlapped) or "max" (overlapped,
    /// maximised). Each window gets its own thread and message loop; Close() ends both.
    public static long Fixture(string title, int r, int g, int b, int x, int y, int w, int h, bool topmost, string kind)
    {
        long made = 0; var ready = new ManualResetEvent(false);
        var th = new Thread(() => {
            T();
            if (!s_registered) {
                s_proc = FixtureProc;
                var c = new WNDCLASSEX { cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)), lpfnWndProc = s_proc, hInstance = GetModuleHandleW(null), lpszClassName = "BarProbeFixture" };
                RegisterClassExW(ref c); s_registered = true;
            }
            uint style = kind == "popup" ? 0x80000000u : 0x00CF0000u;   // WS_POPUP : WS_OVERLAPPEDWINDOW
            uint ex = topmost ? 0x8u : 0u;
            IntPtr hw = CreateWindowExW(ex, "BarProbeFixture", title, style, x, y, w, h, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
            lock (s_brush) s_brush[hw.ToInt64()] = CreateSolidBrush((uint)(r | (g << 8) | (b << 16)));
            ShowWindow(hw, kind == "max" ? 3 : 5);
            made = hw.ToInt64(); ready.Set();
            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        });
        th.IsBackground = true; th.SetApartmentState(ApartmentState.STA); th.Start();
        ready.WaitOne(5000);
        return made;
    }
    /// Re-assert HWND_TOPMOST (as an always-on-top app that comes back to the front would).
    public static void ToTop(long h) { SetWindowPos(new IntPtr(h), new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); }

    // ------------------------------------------------------------------ screen column trace
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bm, uint start, uint lines, byte[] bits, ref BMI bmi, uint usage);
    [StructLayout(LayoutKind.Sequential)] struct BMI { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int x, y; public uint clrUsed, clrImportant; }

    /// Grab the column (colX, y0 .. y0+h) as fast as BitBlt allows for `ms` ms. At t = 0 (after the
    /// reference grab) the cursor moves to (moveX, moveY) unless moveX < 0. Every sample that differs
    /// from the one before is a frame: [t ms, first row that differs from the reference by > tol, rows that do].
    /// The summary judges the motion between the first and last change: `periodMs` is one refresh.
    public static string Trace(int colX, int y0, int h, int moveX, int moveY, int ms, int tol, double periodMs)
    {
        T();
        IntPtr s = GetDC(IntPtr.Zero), m = CreateCompatibleDC(s), b = CreateCompatibleBitmap(s, 1, h), old = SelectObject(m, b);
        var bmi = new BMI { biSize = 40, biWidth = 1, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        byte[] refb = new byte[h * 4], cur = new byte[h * 4];
        BitBlt(m, 0, 0, 1, h, s, colX, y0, 0x00CC0020 | 0x40000000); GetDIBits(m, b, 0, (uint)h, refb, ref bmi, 0);
        ulong last = Hash(refb);
        var ch = new List<double[]>();
        int samples = 0; double prev = 0, maxSampleGap = 0;
        var sw = Stopwatch.StartNew();
        if (moveX >= 0) SetCursorPos(moveX, moveY);
        while (sw.Elapsed.TotalMilliseconds < ms) {
            BitBlt(m, 0, 0, 1, h, s, colX, y0, 0x00CC0020 | 0x40000000); GetDIBits(m, b, 0, (uint)h, cur, ref bmi, 0);
            double t = sw.Elapsed.TotalMilliseconds; samples++;
            if (t - prev > maxSampleGap) maxSampleGap = t - prev; prev = t;
            ulong hh = Hash(cur);
            if (hh == last) continue;
            last = hh;
            int top = -1, rows = 0;
            for (int r = 0; r < h; r++) {
                int i = r * 4, d = Math.Max(Math.Abs(cur[i] - refb[i]), Math.Max(Math.Abs(cur[i + 1] - refb[i + 1]), Math.Abs(cur[i + 2] - refb[i + 2])));
                if (d > tol) { rows++; if (top < 0) top = r; }
            }
            ch.Add(new double[] { Math.Round(t, 2), top, rows });
        }
        SelectObject(m, old); DeleteObject(b); DeleteDC(m); ReleaseDC(IntPtr.Zero, s);

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"samples\":").Append(samples).Append(",\"sampleMeanMs\":").Append((ms / (double)Math.Max(1, samples)).ToString("0.###", inv))
          .Append(",\"sampleMaxGapMs\":").Append(maxSampleGap.ToString("0.##", inv)).Append(",\"frames\":").Append(ch.Count);
        if (ch.Count > 0) {
            double first = ch[0][0], lastT = ch[ch.Count - 1][0];
            var gaps = new List<double>();
            for (int i = 1; i < ch.Count; i++) gaps.Add(ch[i][0] - ch[i - 1][0]);
            gaps.Sort();
            int dropped = 0; foreach (var g in gaps) if (g > 1.5 * periodMs) dropped += (int)Math.Round(g / periodMs) - 1;
            sb.Append(",\"firstMs\":").Append(first.ToString("0.#", inv)).Append(",\"lastMs\":").Append(lastT.ToString("0.#", inv))
              .Append(",\"motionMs\":").Append((lastT - first).ToString("0.#", inv))
              .Append(",\"medianGapMs\":").Append(gaps.Count > 0 ? gaps[gaps.Count / 2].ToString("0.##", inv) : "null")
              .Append(",\"p95GapMs\":").Append(gaps.Count > 0 ? gaps[Math.Min(gaps.Count - 1, (int)(gaps.Count * 0.95))].ToString("0.##", inv) : "null")
              .Append(",\"maxGapMs\":").Append(gaps.Count > 0 ? gaps[gaps.Count - 1].ToString("0.##", inv) : "null")
              .Append(",\"expectedFrames\":").Append(Math.Round((lastT - first) / periodMs) + 1)
              .Append(",\"droppedFrames\":").Append(dropped)
              .Append(",\"endTop\":").Append(ch[ch.Count - 1][1]).Append(",\"endRows\":").Append(ch[ch.Count - 1][2]);
        }
        sb.Append(",\"changes\":[");
        for (int i = 0; i < ch.Count; i++) sb.Append(i == 0 ? "" : ",").Append("[").Append(ch[i][0].ToString("0.##", inv)).Append(",").Append(ch[i][1]).Append(",").Append(ch[i][2]).Append("]");
        return sb.Append("]}").ToString();
    }
    static ulong Hash(byte[] a) { ulong x = 1469598103934665603UL; for (int i = 0; i < a.Length; i++) { x ^= a[i]; x *= 1099511628211UL; } return x; }

    /// BGRA bytes of a screen rect (BitBlt SRCCOPY|CAPTUREBLT of the composed screen).
    public static byte[] Grab(int x, int y, int w, int h)
    {
        T();
        IntPtr s = GetDC(IntPtr.Zero), m = CreateCompatibleDC(s), b = CreateCompatibleBitmap(s, w, h), old = SelectObject(m, b);
        var bmi = new BMI { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var bits = new byte[w * h * 4];
        BitBlt(m, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000); GetDIBits(m, b, 0, (uint)h, bits, ref bmi, 0);
        SelectObject(m, old); DeleteObject(b); DeleteDC(m); ReleaseDC(IntPtr.Zero, s);
        return bits;
    }

    /// The marker square: the bounding box of pixels within `tol` of (r,g,b) inside the rect, and how
    /// many pixels on its middle row/column are "between" fill and marker (soft edges): 2 per axis
    /// = one anti-aliased pixel each side at most.
    public static string Marker(int x, int y, int w, int h, int r, int g, int b, int fr, int fg, int fb, int tol)
    {
        T();
        byte[] px = Grab(x, y, w, h);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        Func<int, int> near = i => Math.Max(Math.Abs(px[i + 2] - r), Math.Max(Math.Abs(px[i + 1] - g), Math.Abs(px[i] - b)));
        Func<int, int> nearFill = i => Math.Max(Math.Abs(px[i + 2] - fr), Math.Max(Math.Abs(px[i + 1] - fg), Math.Abs(px[i] - fb)));
        for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) {
            int i = (yy * w + xx) * 4;
            if (near(i) <= tol) { if (xx < minX) minX = xx; if (xx > maxX) maxX = xx; if (yy < minY) minY = yy; if (yy > maxY) maxY = yy; }
        }
        if (maxX < 0) return "{\"found\":false}";
        int midY = (minY + maxY) / 2, midX = (minX + maxX) / 2, softRow = 0, softCol = 0;
        for (int xx = 0; xx < w; xx++) { int i = (midY * w + xx) * 4; if (near(i) > tol && nearFill(i) > tol) softRow++; }
        for (int yy = 0; yy < h; yy++) { int i = (yy * w + midX) * 4; if (near(i) > tol && nearFill(i) > tol) softCol++; }
        return "{\"found\":true,\"x\":" + (x + minX) + ",\"y\":" + (y + minY) + ",\"w\":" + (maxX - minX + 1) + ",\"h\":" + (maxY - minY + 1) +
               ",\"softRow\":" + softRow + ",\"softCol\":" + softCol + "}";
    }

    /// Rows/columns of a rect whose pixels are mostly the pill fill: the pill's real extent on screen.
    public static string FillBox(int x, int y, int w, int h, int fr, int fg, int fb, int tol)
    {
        T();
        byte[] px = Grab(x, y, w, h);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) {
            int i = (yy * w + xx) * 4;
            if (Math.Abs(px[i + 2] - fr) <= tol && Math.Abs(px[i + 1] - fg) <= tol && Math.Abs(px[i] - fb) <= tol) {
                if (xx < minX) minX = xx; if (xx > maxX) maxX = xx; if (yy < minY) minY = yy; if (yy > maxY) maxY = yy;
            }
        }
        if (maxX < 0) return "{\"found\":false}";
        return "{\"found\":true,\"x\":" + (x + minX) + ",\"y\":" + (y + minY) + ",\"w\":" + (maxX - minX + 1) + ",\"h\":" + (maxY - minY + 1) + "}";
    }

    // ------------------------------------------------------------------ monitors
    delegate bool MonEnum(IntPtr mon, IntPtr dc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonEnum cb, IntPtr data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFOEX { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFOEX mi);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
    public static string Monitors()
    {
        T();
        var parts = new List<string>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr mon, IntPtr dc, ref RECT r, IntPtr d) => {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
            GetMonitorInfoW(mon, ref mi);
            uint dx, dy; GetDpiForMonitor(mon, 0, out dx, out dy);
            parts.Add("{\"device\":\"" + mi.szDevice.Replace("\\", "\\\\") + "\",\"primary\":" + ((mi.dwFlags & 1) != 0 ? "true" : "false") +
                      ",\"rect\":" + J(mi.rcMonitor) + ",\"work\":" + J(mi.rcWork) + ",\"dpi\":" + dx + "}");
            return true;
        }, IntPtr.Zero);
        return "[" + string.Join(",", parts) + "]";
    }

    // ------------------------------------------------------------------ per-monitor DPI scale
    // DisplayConfigGet/SetDeviceInfo with the undocumented types -3 (get) / -4 (set), as SetDpi and
    // similar tools do. Values are steps relative to the monitor's recommended scale.
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct HDR { public int type; public int size; public LUID adapter; public uint id; }
    [StructLayout(LayoutKind.Sequential)] struct DPIGET { public HDR h; public int min, cur, max; }
    [StructLayout(LayoutKind.Sequential)] struct DPISET { public HDR h; public int rel; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SRCNAME { public HDR h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name; }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint f, out uint np, out uint nm);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint f, ref uint np, IntPtr paths, ref uint nm, IntPtr modes, IntPtr topo);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetDpi(ref DPIGET p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetName(ref SRCNAME p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] static extern int SetDpi(ref DPISET p);
    static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    static List<HDR> Sources()
    {
        var list = new List<HDR>();
        uint np, nm;
        if (GetDisplayConfigBufferSizes(2 /* QDC_ONLY_ACTIVE_PATHS */, out np, out nm) != 0) return list;
        IntPtr paths = Marshal.AllocHGlobal((int)np * 72), modes = Marshal.AllocHGlobal((int)nm * 64);
        try {
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;
            for (int i = 0; i < np; i++) {
                IntPtr p = paths + i * 72;   // DISPLAYCONFIG_PATH_INFO: sourceInfo { LUID adapterId; UINT32 id; ... } first
                list.Add(new HDR { adapter = new LUID { Low = (uint)Marshal.ReadInt32(p, 0), High = Marshal.ReadInt32(p, 4) }, id = (uint)Marshal.ReadInt32(p, 8) });
            }
        } finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
        return list;
    }
    /// Every active display source: GDI name, scale now, the range Windows allows.
    public static string DpiInfo()
    {
        var parts = new List<string>();
        foreach (var s in Sources()) {
            var n = new SRCNAME { h = s }; n.h.type = 1; n.h.size = Marshal.SizeOf(typeof(SRCNAME));
            int hn = GetName(ref n);
            var g = new DPIGET { h = s }; g.h.type = -3; g.h.size = Marshal.SizeOf(typeof(DPIGET));
            int hr = GetDpi(ref g);
            int rec = -g.min, curIdx = rec + g.cur;
            string pct = hr == 0 && curIdx >= 0 && curIdx < Steps.Length ? Steps[curIdx].ToString() : "null";
            string max = hr == 0 && rec + g.max >= 0 && rec + g.max < Steps.Length ? Steps[rec + g.max].ToString() : "null";
            string recp = hr == 0 && rec >= 0 && rec < Steps.Length ? Steps[rec].ToString() : "null";
            parts.Add("{\"device\":\"" + (hn == 0 ? n.name.Replace("\\", "\\\\") : "?") + "\",\"hr\":" + hr + ",\"min\":" + g.min + ",\"cur\":" + g.cur +
                      ",\"max\":" + g.max + ",\"percent\":" + pct + ",\"recommended\":" + recp + ",\"maxPercent\":" + max + "}");
        }
        return "[" + string.Join(",", parts) + "]";
    }
    /// Set the scale of the source whose GDI name is `device` (null/"" = the first) to `percent`.
    public static string DpiSetPercent(string device, int percent)
    {
        foreach (var s in Sources()) {
            var n = new SRCNAME { h = s }; n.h.type = 1; n.h.size = Marshal.SizeOf(typeof(SRCNAME));
            GetName(ref n);
            if (!string.IsNullOrEmpty(device) && !string.Equals(n.name, device, StringComparison.OrdinalIgnoreCase)) continue;
            var g = new DPIGET { h = s }; g.h.type = -3; g.h.size = Marshal.SizeOf(typeof(DPIGET));
            int hr = GetDpi(ref g);
            if (hr != 0) return "get failed: " + hr;
            int idx = Array.IndexOf(Steps, percent), rel = idx - (-g.min);
            if (idx < 0) return percent + "% is not a Windows scale step";
            if (rel < g.min || rel > g.max) return percent + "% is outside the allowed range (rel " + rel + ", allowed " + g.min + ".." + g.max + ")";
            var st = new DPISET { h = s, rel = rel }; st.h.type = -4; st.h.size = Marshal.SizeOf(typeof(DPISET));
            int hs = SetDpi(ref st);
            return hs == 0 ? "ok: " + n.name + " -> " + percent + "% (rel " + rel + ")" : "set failed: " + hs;
        }
        return "no display source named " + device;
    }

    // ------------------------------------------------------------------ the primary monitor
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID, DeviceKey; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string dev, uint i, ref DISPLAY_DEVICE d, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, ref DEVMODE dm, IntPtr h, uint f, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, IntPtr dm, IntPtr h, uint f, IntPtr l);

    /// Attached display devices: name, adapter string, flags (1 attached, 4 primary), position and size.
    public static string Devices()
    {
        var parts = new List<string>();
        for (uint i = 0; i < 16; i++) {
            var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            if (!EnumDisplayDevicesW(null, i, ref d, 0)) break;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            bool cur = EnumDisplaySettingsW(d.DeviceName, -1, ref dm);
            parts.Add("{\"name\":\"" + d.DeviceName.Replace("\\", "\\\\") + "\",\"adapter\":\"" + d.DeviceString.Replace("\\", "\\\\").Replace("\"", "'") + "\",\"flags\":" + d.StateFlags +
                      (cur ? ",\"x\":" + dm.dmPositionX + ",\"y\":" + dm.dmPositionY + ",\"w\":" + dm.dmPelsWidth + ",\"h\":" + dm.dmPelsHeight : "") + "}");
        }
        return "[" + string.Join(",", parts) + "]";
    }

    /// Make `device` the primary display: it goes to (0,0), every other attached display keeps its place
    /// relative to it. Registry first (CDS_NORESET), then one apply. Returns the result codes.
    public static string SetPrimary(string device)
    {
        var all = new List<KeyValuePair<string, DEVMODE>>();
        DEVMODE target = new DEVMODE(); bool found = false;
        for (uint i = 0; i < 16; i++) {
            var d = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
            if (!EnumDisplayDevicesW(null, i, ref d, 0)) break;
            if ((d.StateFlags & 1) == 0) continue;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
            if (!EnumDisplaySettingsW(d.DeviceName, -1, ref dm)) continue;
            all.Add(new KeyValuePair<string, DEVMODE>(d.DeviceName, dm));
            if (string.Equals(d.DeviceName, device, StringComparison.OrdinalIgnoreCase)) { target = dm; found = true; }
        }
        if (!found) return "no attached display named " + device;
        int ox = target.dmPositionX, oy = target.dmPositionY;
        var sb = new StringBuilder();
        // run 1: the old primary first and position-only fields gave -1 for the new primary, and the apply
        // reset the old primary to its registry mode (1024x768). The new primary goes first now, and every
        // display's current size is written along with its position.
        all.Sort((a, b) => (string.Equals(b.Key, device, StringComparison.OrdinalIgnoreCase) ? 1 : 0) - (string.Equals(a.Key, device, StringComparison.OrdinalIgnoreCase) ? 1 : 0));
        foreach (var kv in all) {
            var dm = kv.Value;
            bool isNew = string.Equals(kv.Key, device, StringComparison.OrdinalIgnoreCase);
            dm.dmPositionX -= ox; dm.dmPositionY -= oy; dm.dmFields = 0x20 /* DM_POSITION */ | 0x80000 /* DM_PELSWIDTH */ | 0x100000 /* DM_PELSHEIGHT */;
            uint f = 0x1 /* UPDATEREGISTRY */ | 0x10000000 /* NORESET */ | (isNew ? 0x10u /* SET_PRIMARY */ : 0u);
            sb.Append(kv.Key).Append(" -> ").Append(dm.dmPositionX).Append(",").Append(dm.dmPositionY).Append(": ").Append(ChangeDisplaySettingsExW(kv.Key, ref dm, IntPtr.Zero, f, IntPtr.Zero)).Append("; ");
        }
        sb.Append("apply: ").Append(ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero));
        return sb.ToString();
    }
}
