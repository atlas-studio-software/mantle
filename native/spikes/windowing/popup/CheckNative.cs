// CheckNative.cs - check-side helpers for the popup lane (Add-Type from popup/check.ps1, next to the
// shared harness/Native.cs). Standalone on purpose: an Add-Type assembly cannot see WinHarness, so
// the small grab is copied here. Runner only: it moves the cursor and clicks.
//
// What it adds to the harness (candidates for harness/Native.cs later):
//   * the command pipe to a spike app (Connect / Send),
//   * screen-change timing: SendAndWatch / StartAndWatch poll a small rect in a tight loop and return
//     the ms from the command (or Process.Start) to the first changed pixel,
//   * mouse moves through the real input queue (Move), clicks (Click, as WinHarness.Click),
//   * a theme-change broadcast (BroadcastTheme), and coverage maths for anti-aliased edges.
#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

public sealed class WatchResult
{
    public double Ms = -1;        // command written (or Process.Start called) -> first changed pixel; -1 = none in time
    public double ReplyMs = -1;   // command written -> reply line read
    public string Reply = "";
    public int Pid;
}

public static class PopupProbe
{
    // ---- grab (copy of WinHarness.Grab)
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

    public static int MaxDelta(byte[] a, byte[] b)
    {
        int m = 0;
        for (int i = 0; i + 2 < a.Length && i + 2 < b.Length; i += 4)
            m = Math.Max(m, Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2]))));
        return m;
    }

    /// Pixels (whole grab) whose max channel changed by more than tol.
    public static int CountChanged(byte[] a, byte[] b, int tol)
    {
        int n = 0;
        for (int i = 0; i + 2 < a.Length && i + 2 < b.Length; i += 4)
            if (Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2]))) > tol) n++;
        return n;
    }

    /// Polls the rect until it differs from `baseline` by more than tol; ms on `sw`, or -1.
    public static double WatchChange(byte[] baseline, int x, int y, int w, int h, int tol, Stopwatch sw, int timeoutMs)
    {
        while (sw.ElapsedMilliseconds < timeoutMs)
            if (MaxDelta(baseline, Grab(x, y, w, h)) > tol) return sw.Elapsed.TotalMilliseconds;
        return -1;
    }

    // ---- the command pipe
    static NamedPipeClientStream? s_pipe;
    static StreamReader? s_rd;
    static StreamWriter? s_wr;
    static Task<string?>? s_pending;

    public static bool Connect(string name, int timeoutMs)
    {
        Disconnect();
        var p = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        try { p.Connect(timeoutMs); } catch { p.Dispose(); return false; }
        s_pipe = p;
        s_rd = new StreamReader(p, new UTF8Encoding(false));
        s_wr = new StreamWriter(p, new UTF8Encoding(false)) { AutoFlush = true };
        return true;
    }

    public static void Disconnect()
    {
        try { s_pipe?.Dispose(); } catch { }
        s_pipe = null; s_rd = null; s_wr = null; s_pending = null;
    }

    static string ReadReply(int timeoutMs)
    {
        if (s_rd == null) return "{\"ok\":false,\"error\":\"not connected\"}";
        var t = s_pending ?? s_rd.ReadLineAsync();
        s_pending = null;
        if (!t.Wait(timeoutMs)) { s_pending = t; return "{\"ok\":false,\"error\":\"reply timeout\"}"; }
        return t.Result ?? "{\"ok\":false,\"error\":\"pipe closed\"}";
    }

    public static string Send(string cmd, int timeoutMs)
    {
        if (s_wr == null) return "{\"ok\":false,\"error\":\"not connected\"}";
        if (s_pending != null) ReadReply(timeoutMs);   // drop a late reply to an earlier command
        s_wr.WriteLine(cmd);
        return ReadReply(timeoutMs);
    }

    /// Writes the command, then polls the rect for the first change from what it showed before.
    public static WatchResult SendAndWatch(string cmd, int x, int y, int w, int h, int tol, int timeoutMs)
    {
        var res = new WatchResult();
        if (s_wr == null) { res.Reply = "{\"ok\":false,\"error\":\"not connected\"}"; return res; }
        if (s_pending != null) ReadReply(timeoutMs);
        var baseline = Grab(x, y, w, h);
        var sw = Stopwatch.StartNew();
        s_wr.WriteLine(cmd);
        res.Ms = WatchChange(baseline, x, y, w, h, tol, sw, timeoutMs);
        res.Reply = ReadReply(timeoutMs);
        res.ReplyMs = sw.Elapsed.TotalMilliseconds;
        return res;
    }

    /// Starts a process, then polls the rect for the first change (the cold-open measurement).
    public static WatchResult StartAndWatch(string exe, string args, int x, int y, int w, int h, int tol, int timeoutMs)
    {
        var res = new WatchResult();
        var baseline = Grab(x, y, w, h);
        var sw = Stopwatch.StartNew();
        var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
        res.Pid = p?.Id ?? 0;
        res.Ms = WatchChange(baseline, x, y, w, h, tol, sw, timeoutMs);
        return res;
    }

    // ---- input
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    const uint MOVE = 0x1, DOWN = 0x2, UP = 0x4, ABS = 0x8000;

    static MOUSEINPUT At(int x, int y, uint flags)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        return new MOUSEINPUT { dx = (int)Math.Round(x * 65535.0 / (sw - 1)), dy = (int)Math.Round(y * 65535.0 / (sh - 1)), flags = flags | ABS };
    }

    /// A real mouse move to a screen point, through the input queue.
    public static uint Move(int x, int y)
    {
        var inp = new INPUT[1];
        inp[0].mi = At(x, y, MOVE);
        return SendInput(1, inp, Marshal.SizeOf<INPUT>());
    }

    /// A real left click at a screen point (move, down, up in one SendInput, as WinHarness.Click).
    public static uint Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inp = new INPUT[3];
        inp[0].mi = At(x, y, MOVE); inp[1].mi = At(x, y, DOWN); inp[2].mi = At(x, y, UP);
        return SendInput(3, inp, Marshal.SizeOf<INPUT>());
    }

    // ---- desktop
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? cls, string? name);

    /// Minimises every visible, unowned top-level window except the shell's own (the runner's
    /// console window otherwise sits over the test area and comes forward when focus moves).
    public static string MinimizeOthers()
    {
        var skip = new System.Collections.Generic.HashSet<string> { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
        var done = new System.Collections.Generic.List<string>();
        EnumProc cb = (h, l) => {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindow(h, 4) != IntPtr.Zero) return true;
            var c = new StringBuilder(256); GetClassNameW(h, c, 256);
            if (skip.Contains(c.ToString())) return true;
            var t = new StringBuilder(256); GetWindowTextW(h, t, 256);
            GetWindowThreadProcessId(h, out uint pid);
            ShowWindow(h, 6 /* SW_MINIMIZE */);
            done.Add($"{c}:'{t}'({pid})");
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return string.Join("; ", done);
    }

    /// Top of the non-topmost band, without activating (the fixture, before each test).
    public static bool RaiseByTitle(string title)
    {
        IntPtr h = FindWindowW(null, title);
        return h != IntPtr.Zero && SetWindowPos(h, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
    }

    // ---- theme
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, UIntPtr wp, string lp, uint flags, uint timeout, out UIntPtr result);
    /// WM_SETTINGCHANGE "ImmersiveColorSet" to every top-level window (what Settings sends on a theme flip).
    public static bool BroadcastTheme() => SendMessageTimeoutW(new IntPtr(0xFFFF), 0x001A, UIntPtr.Zero, "ImmersiveColorSet", 0x2, 3000, out _) != IntPtr.Zero;

    // ---- coverage of an opaque colour (fr, fg, fb) over `before` at one pixel: 0 = untouched, 1 = full
    public static double Coverage(byte[] before, byte[] after, int w, int x, int y, int fr, int fg, int fb)
    {
        int i = (y * w + x) * 4;
        double db = fb - before[i], dg = fg - before[i + 1], dr = fr - before[i + 2];
        double ab = after[i] - before[i], ag = after[i + 1] - before[i + 1], ar = after[i + 2] - before[i + 2];
        double n = db * db + dg * dg + dr * dr;
        return n < 1 ? double.NaN : (ab * db + ag * dg + ar * dr) / n;
    }

    /// Over the shape's four corner arcs: [pixels in the 2 px band around the arc, of them partial
    /// (coverage 0.08..0.92), inside pixels (more than 2 px in) that are NOT full (< 0.92), min
    /// coverage inside x100, corner-box pixels outside the arc band that are not ~0 (> 0.08)].
    public static int[] CornerAA(byte[] before, byte[] after, int w, int sx, int sy, int sw, int sh, int r, int fr, int fg, int fb)
    {
        int band = 0, partial = 0, notFull = 0, outside = 0; double minIn = 1;
        for (int y = sy; y < sy + sh; y++)
            for (int x = sx; x < sx + sw; x++) {
                double px = x + 0.5, py = y + 0.5;
                double cx = px < sx + r ? sx + r : (px > sx + sw - r ? sx + sw - r : px);
                double cy = py < sy + r ? sy + r : (py > sy + sh - r ? sy + sh - r : py);
                bool corner = cx != px && cy != py;
                double d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                double edge = Math.Min(Math.Min(px - sx, sx + sw - px), Math.Min(py - sy, sy + sh - py));
                double c = Coverage(before, after, w, x, y, fr, fg, fb);
                if (double.IsNaN(c)) continue;
                if (corner && Math.Abs(d - r) <= 1.0) { band++; if (c > 0.08 && c < 0.92) partial++; }
                else if (corner && d > r + 1.0) { if (c > 0.08) outside++; }
                else if ((corner && d < r - 2.0) || (!corner && edge > 2.0)) { if (c < 0.92) notFull++; minIn = Math.Min(minIn, c); }
            }
        return new[] { band, partial, notFull, (int)Math.Round(minIn * 100), outside };
    }
}
