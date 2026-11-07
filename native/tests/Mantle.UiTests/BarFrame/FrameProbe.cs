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
    static double SafeCpu(Process d) { try { d.Refresh(); return d.TotalProcessorTime.TotalMilliseconds; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; } }
}
