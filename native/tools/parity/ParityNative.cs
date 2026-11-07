// ParityNative.cs - helpers for run-parity.ps1 (loaded with Add-Type). Runner only: it changes the
// desktop (wallpaper, theme, taskbar auto-hide, cursor) of the machine it runs on.
//
// Capture: the real composited screen pixels (GDI BitBlt with CAPTUREBLT), so glass, shadows and transparency are
// what a person would see, not what a window thinks it drew.
// Diff: per-channel mean absolute difference, the share of pixels whose largest channel delta is over a
// threshold, and SSIM on luminance (8x8 windows, step 4), each over the UNMASKED pixels only. The heatmap
// shows the first image dimmed to grey, the difference in red, and masked areas tinted blue.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class ParityNative
{
    // ------------------------------------------------------------------ desktop setup
    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr v);
    public static bool DpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4)) != IntPtr.Zero;

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    public static int[] Screen() => new[] { GetSystemMetrics(0), GetSystemMetrics(1) };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SystemParametersInfoW(uint a, uint b, string c, uint d);
    public static bool SetWallpaper(string path) => SystemParametersInfoW(0x0014, 0, path, 0x3);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint ms, out IntPtr res);
    /// <summary>Tells running apps the colour set changed (after the theme registry values are written).</summary>
    public static void BroadcastTheme()
    {
        SendMessageTimeoutW(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x2, 2000, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string title);
    /// <summary>OS-managed auto-hide for the runner's own taskbar, so it never sits over the bar being measured.</summary>
    public static void TaskbarAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindowW("Shell_TrayWnd", null), lParam = new IntPtr(on ? 1 : 2) };
        SHAppBarMessage(0x0000000A, ref d);
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    public static void ParkCursor(int x, int y) => SetCursorPos(x, y);

    // ------------------------------------------------------------------ windows of a process
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, char[] b, int n);
    /// <summary>"hwnd|title|l,t,r,b" for every visible top-level window, for the log.</summary>
    public static string[] VisibleWindows()
    {
        var list = new List<string>();
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h)) return true;
            var b = new char[256]; int n = GetWindowTextW(h, b, b.Length);
            GetWindowRect(h, out RECT r); GetWindowThreadProcessId(h, out uint pid);
            list.Add($"{h.ToInt64():x}|pid {pid}|{new string(b, 0, n)}|{r.Left},{r.Top},{r.Right},{r.Bottom}");
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    // ------------------------------------------------------------------ capture (GDI, no System.Drawing: Add-Type keeps its default references)
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

    /// <summary>BGRA bytes (alpha forced to 255) of a screen rectangle as DWM shows it. SRCCOPY | CAPTUREBLT,
    /// so layered (transparent) windows are included, as the spike bench did.</summary>
    public static byte[] Grab(int x, int y, int w, int h)
    {
        IntPtr s = GetDC(IntPtr.Zero), m = CreateCompatibleDC(s), b = CreateCompatibleBitmap(s, w, h);
        IntPtr old = SelectObject(m, b);
        try
        {
            BitBlt(m, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000);
            SelectObject(m, old);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            GetDIBits(m, b, 0, (uint)h, bits, ref bi, 0);
            for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
            return bits;
        }
        finally { DeleteObject(b); DeleteDC(m); ReleaseDC(IntPtr.Zero, s); }
    }

    /// <summary>Mean absolute difference (0-255) between two grabs of the same rect a moment apart: is it still moving?</summary>
    public static double Motion(int x, int y, int w, int h, int gapMs)
    {
        var pa = Grab(x, y, w, h);
        System.Threading.Thread.Sleep(gapMs);
        var pb = Grab(x, y, w, h);
        double s = 0; for (int i = 0; i < pa.Length; i++) if (i % 4 != 3) s += Math.Abs(pa[i] - pb[i]);
        return s / (pa.Length / 4 * 3);
    }

    // ------------------------------------------------------------------ diff
    public sealed class Score
    {
        public int Width, Height, Compared, Masked;
        public double MeanAbsR, MeanAbsG, MeanAbsB, MeanAbs, DiffPxPct, Ssim;
        public int Threshold;
        public byte[] Heat = Array.Empty<byte>();
    }

    /// <summary>
    /// Compares two same-size BGRA grabs. masks = flat [x, y, w, h, x, y, w, h, ...] in image pixels; those pixels
    /// are left out of every number. threshold = largest-channel delta (0-255) that counts a pixel as different.
    /// The heatmap (BGRA, same size) is left in Score.Heat for the caller to save.
    /// </summary>
    public static Score Diff(byte[] pa, byte[] pb, int w, int h, int[] masks, int threshold)
    {
        if (pa.Length != w * h * 4 || pb.Length != w * h * 4)
            throw new InvalidOperationException($"sizes differ: {pa.Length} / {pb.Length} bytes for {w}x{h}");
        var mask = new bool[w * h];
        for (int m = 0; m + 3 < (masks ?? Array.Empty<int>()).Length; m += 4)
            for (int y = Math.Max(0, masks[m + 1]); y < Math.Min(h, masks[m + 1] + masks[m + 3]); y++)
                for (int x = Math.Max(0, masks[m]); x < Math.Min(w, masks[m] + masks[m + 2]); x++) mask[y * w + x] = true;

        var s = new Score { Width = w, Height = h, Threshold = threshold };
        double sr = 0, sg = 0, sb = 0; int over = 0;
        var heat = new byte[w * h * 4];
        var la = new double[w * h]; var lb = new double[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            la[i] = 0.114 * pa[o] + 0.587 * pa[o + 1] + 0.299 * pa[o + 2];
            lb[i] = 0.114 * pb[o] + 0.587 * pb[o + 1] + 0.299 * pb[o + 2];
            byte grey = (byte)(la[i] * 0.35);
            heat[o + 3] = 255;
            if (mask[i]) { s.Masked++; heat[o] = (byte)Math.Min(255, grey + 90); heat[o + 1] = grey; heat[o + 2] = grey; continue; }
            int db = Math.Abs(pa[o] - pb[o]), dg = Math.Abs(pa[o + 1] - pb[o + 1]), dr = Math.Abs(pa[o + 2] - pb[o + 2]);
            sb += db; sg += dg; sr += dr; s.Compared++;
            int mx = Math.Max(dr, Math.Max(dg, db));
            if (mx > threshold) over++;
            heat[o] = grey; heat[o + 1] = grey; heat[o + 2] = (byte)Math.Min(255, grey + mx * 3);
        }
        int n = Math.Max(1, s.Compared);
        s.MeanAbsR = sr / n; s.MeanAbsG = sg / n; s.MeanAbsB = sb / n; s.MeanAbs = (sr + sg + sb) / (3.0 * n);
        s.DiffPxPct = 100.0 * over / n;
        s.Ssim = Ssim(la, lb, mask, w, h);

        s.Heat = heat;
        return s;
    }

    static double Ssim(double[] a, double[] b, bool[] mask, int w, int h)
    {
        const int win = 8, step = 4;
        const double c1 = 6.5025, c2 = 58.5225;   // (0.01*255)^2, (0.03*255)^2
        double total = 0; int count = 0;
        for (int y0 = 0; y0 + win <= h; y0 += step)
            for (int x0 = 0; x0 + win <= w; x0 += step)
            {
                double ma = 0, mb = 0; int k = 0;
                for (int y = y0; y < y0 + win; y++) for (int x = x0; x < x0 + win; x++)
                    { int i = y * w + x; if (mask[i]) continue; ma += a[i]; mb += b[i]; k++; }
                if (k < win * win / 2) continue;   // mostly masked: not judged
                ma /= k; mb /= k;
                double va = 0, vb = 0, cov = 0;
                for (int y = y0; y < y0 + win; y++) for (int x = x0; x < x0 + win; x++)
                    { int i = y * w + x; if (mask[i]) continue; double da = a[i] - ma, db = b[i] - mb; va += da * da; vb += db * db; cov += da * db; }
                va /= k - 1; vb /= k - 1; cov /= k - 1;
                total += ((2 * ma * mb + c1) * (2 * cov + c2)) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                count++;
            }
        return count == 0 ? double.NaN : total / count;
    }
}
