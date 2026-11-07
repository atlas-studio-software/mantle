using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mantle.UiTests;

/// What the frame and pop-up tests read from OUTSIDE the app: every top-level window of a process (hidden and
/// cloaked ones too), the cursor, real keys, the native log (mirrored to <bench-out>/native-log.txt), CPU time.
internal static class FrameProbe
{
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint a, uint p, out Win32.RECT r, uint w);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, KIN[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
    [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; public uint pad1, pad2; }
    [StructLayout(LayoutKind.Sequential)] struct KIN { public uint type; public KI ki; }

    public static int ScreenW => GetSystemMetrics(0);
    public static int ScreenH => GetSystemMetrics(1);
    public static Win32.RECT WorkArea() { SystemParametersInfoW(0x30, 0, out var r, 0); return r; }

    public static List<(IntPtr Hwnd, string Title)> All(int pid)
    {
        var list = new List<(IntPtr, string)>();
        EnumWindows((h, _) => {
            GetWindowThreadProcessId(h, out uint p);
            if (p == pid) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); list.Add((h, sb.ToString())); }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static IntPtr Find(int pid, string title) => All(pid).FirstOrDefault(w => w.Title == title).Hwnd;

    public static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int c, 4) == 0 && c != 0;
    public static bool Showing(IntPtr h) => h != IntPtr.Zero && Win32.IsWindowVisible(h) && !Cloaked(h);

    public static void Tap(ushort vk)
    {
        var a = new KIN[2];
        a[0].type = 1; a[0].ki.vk = vk;
        a[1].type = 1; a[1].ki.vk = vk; a[1].ki.flags = 2;
        SendInput(2, a, Marshal.SizeOf<KIN>());
    }

    /// The cursor somewhere neutral: the middle of the screen, away from the bar and the edge band.
    public static void CursorAway() => SetCursorPos(ScreenW / 2, ScreenH / 3);
    public static void CursorToEdge() => SetCursorPos(ScreenW / 2, ScreenH - 1);

    /// A fixture made borderless and fullscreen on the primary display (a game's "fullscreen window").
    public static void MakeFullscreen(IntPtr h)
    {
        SetWindowLongPtr(h, -16, new IntPtr(unchecked((int)0x90000000)));   // WS_POPUP | WS_VISIBLE
        SetWindowPos(h, IntPtr.Zero, 0, 0, ScreenW, ScreenH, 0x0020 | 0x0040 /* FRAMECHANGED | SHOWWINDOW */);
    }

    public static string Log(string dir) { try { using var f = new FileStream(Path.Combine(dir, "native-log.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return new StreamReader(f).ReadToEnd(); } catch (IOException) { return ""; } }
    public static bool LogHas(string dir, string text, int ms) => Host.Until(() => Log(dir).Contains(text, StringComparison.Ordinal), ms);
    public static int Count(string dir, string text) { string l = Log(dir); int n = 0; for (int i = l.IndexOf(text, StringComparison.Ordinal); i >= 0; i = l.IndexOf(text, i + 1, StringComparison.Ordinal)) n++; return n; }

    public static string NewDir(string tag) { var d = Path.Combine(Path.GetTempPath(), "mantle-frame-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    /// CPU time of the process (and of dwm.exe, which composes what it draws) over `ms`, as % of ONE core.
    public static (double App, double Dwm) Cpu(Process p, int ms)
    {
        var dwm = Process.GetProcessesByName("dwm");
        p.Refresh();
        double a0 = p.TotalProcessorTime.TotalMilliseconds, d0 = dwm.Sum(SafeCpu);
        var sw = Stopwatch.StartNew();
        Thread.Sleep(ms);
        p.Refresh();
        double wall = sw.Elapsed.TotalMilliseconds;
        double a1 = p.TotalProcessorTime.TotalMilliseconds, d1 = dwm.Sum(SafeCpu);
        return (Math.Round(100 * (a1 - a0) / wall, 3), Math.Round(100 * (d1 - d0) / wall, 3));
    }
    // ---- the runner's display scale (DisplayConfigGet/SetDeviceInfo, undocumented types -3 get / -4 set, as
    // tools/parity/ParityNative.cs SetScale does). Runner only: it changes the display of the machine it runs on.
    [StructLayout(LayoutKind.Sequential)] struct DcHdr { public int type; public int size; public uint lo; public int hi; public uint id; }
    [StructLayout(LayoutKind.Sequential)] struct DcGet { public DcHdr h; public int min, cur, max; }
    [StructLayout(LayoutKind.Sequential)] struct DcSet { public DcHdr h; public int rel; }
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint f, out uint np, out uint nm);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint f, ref uint np, IntPtr paths, ref uint nm, IntPtr modes, IntPtr topo);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int DcGetDpi(ref DcGet p);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] static extern int DcSetDpi(ref DcSet p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(long pt, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    static readonly int[] DpiSteps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    /// The primary display's scale in % (96 dpi = 100).
    public static int PrimaryScale() => GetDpiForMonitor(MonitorFromPoint(0, 1), 0, out uint x, out _) == 0 ? (int)Math.Round(x / 96.0 * 100) : 100;

    /// Sets the first active display (the runner has one) to `percent`: "ok ..." or why not.
    public static string SetScale(int percent)
    {
        if (GetDisplayConfigBufferSizes(2, out uint np, out uint nm) != 0 || np == 0) return "no active display path";
        IntPtr paths = Marshal.AllocHGlobal((int)np * 72), modes = Marshal.AllocHGlobal((int)nm * 64);
        try {
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return "QueryDisplayConfig failed";
            var h = new DcHdr { lo = (uint)Marshal.ReadInt32(paths, 0), hi = Marshal.ReadInt32(paths, 4), id = (uint)Marshal.ReadInt32(paths, 8) };
            var g = new DcGet { h = h }; g.h.type = -3; g.h.size = Marshal.SizeOf<DcGet>();
            int hr = DcGetDpi(ref g);
            if (hr != 0) return "get failed: " + hr;
            int idx = Array.IndexOf(DpiSteps, percent), rel = idx + g.min;
            if (idx < 0) return percent + " % is not a Windows scale step";
            if (rel < g.min || rel > g.max) return $"{percent} % is outside what Windows allows here (rel {rel}, allowed {g.min}..{g.max})";
            var st = new DcSet { h = h, rel = rel }; st.h.type = -4; st.h.size = Marshal.SizeOf<DcSet>();
            int hs = DcSetDpi(ref st);
            return hs == 0 ? $"ok (rel {rel})" : "set failed: " + hs;
        } finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
    }

    static double SafeCpu(Process d) { try { d.Refresh(); return d.TotalProcessorTime.TotalMilliseconds; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; } }
}
