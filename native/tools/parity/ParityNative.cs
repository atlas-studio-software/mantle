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
    // ---- the display scale (bar pairs at 100 % and 150 %): DisplayConfigGet/SetDeviceInfo with the undocumented
    // types -3 (get) / -4 (set), as SetDpi-style tools do (spikes/windowing/bar/probe/BarProbe.cs DpiSetPercent).
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

    public static int PrimaryDpi() { uint x, y; return GetDpiForMonitor(MonitorFromPoint(0, 1), 0, out x, out y) == 0 ? (int)x : 96; }

    /// The first active display source (the runner has one) to `percent`; "ok: ..." or why not.
    public static string SetScale(int percent)
    {
        uint np, nm;
        if (GetDisplayConfigBufferSizes(2, out np, out nm) != 0 || np == 0) return "no active display path";
        IntPtr paths = Marshal.AllocHGlobal((int)np * 72), modes = Marshal.AllocHGlobal((int)nm * 64);
        try {
            if (QueryDisplayConfig(2, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return "QueryDisplayConfig failed";
            var h = new DcHdr { lo = (uint)Marshal.ReadInt32(paths, 0), hi = Marshal.ReadInt32(paths, 4), id = (uint)Marshal.ReadInt32(paths, 8) };
            var g = new DcGet { h = h }; g.h.type = -3; g.h.size = Marshal.SizeOf(typeof(DcGet));
            int hr = DcGetDpi(ref g);
            if (hr != 0) return "get failed: " + hr;
            int idx = Array.IndexOf(DpiSteps, percent), rel = idx + g.min;
            if (idx < 0) return percent + " % is not a Windows scale step";
            if (rel < g.min || rel > g.max) return percent + " % is outside what Windows allows here (rel " + rel + ", allowed " + g.min + ".." + g.max + ")";
            var st = new DcSet { h = h, rel = rel }; st.h.type = -4; st.h.size = Marshal.SizeOf(typeof(DcSet));
            int hs = DcSetDpi(ref st);
            return hs == 0 ? "ok (rel " + rel + ")" : "set failed: " + hs;
        } finally { Marshal.FreeHGlobal(paths); Marshal.FreeHGlobal(modes); }
    }

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

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    /// <summary>The runner's own taskbar rect if it is visible and reaches into [top, bottom), else null.</summary>
    public static int[] TaskbarOver(int top, int bottom)
    {
        IntPtr t = FindWindowW("Shell_TrayWnd", null);
        if (t == IntPtr.Zero || !IsWindowVisible(t) || !GetWindowRect(t, out RECT r)) return null;
        return (r.Top < bottom && r.Bottom > top && r.Bottom - r.Top > 4) ? new[] { r.Left, r.Top, r.Right, r.Bottom } : null;
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    public static void ParkCursor(int x, int y) => SetCursorPos(x, y);

    // ------------------------------------------------------------------ windows of a process
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
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

    // ------------------------------------------------------------------ a clean shot: nothing excluded, nothing on top
    // Both bars' light glass takes a picture of what is behind the bar 4 times a second, and for each picture the bar
    // switches WDA_EXCLUDEFROMCAPTURE on (Electron main.js#captureBackdrop: 25 ms settle + the sidecar's shot, which
    // took over a second while the sidecar was busy starting; native LiveGlass the same way). A screen grab in that
    // moment has NO bar in it: run 42369550297 scored Electron light 0.7251 on a crop of bare wallpaper while the
    // whole-screen shot taken right after it showed the bar, and run 42369209048 (native/r2-core-2) had the opposite pair.
    [DllImport("user32.dll")] static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, char[] b, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);

    static bool Reaches(RECT r, int x, int y, int w, int h) => r.Right > r.Left && r.Bottom > r.Top && r.Left < x + w && r.Right > x && r.Top < y + h && r.Bottom > y;
    static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int c, 4) == 0 && c != 0;
    static string ClassOf(IntPtr h) { var b = new char[128]; int n = GetClassNameW(h, b, b.Length); return new string(b, 0, n); }
    static string TitleOf(IntPtr h) { var b = new char[256]; int n = GetWindowTextW(h, b, b.Length); return new string(b, 0, n); }
    // the desktop and the runner's own (auto-hidden) taskbar are part of the scene, never "in the way"
    static readonly HashSet<string> ShellClasses = new HashSet<string> { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>Visible, uncloaked, not minimised top-level windows that reach into the rect; with own = true the
    /// ones of the given pids, else the ones of every OTHER process (shell windows left out).</summary>
    static List<IntPtr> WindowsOver(int x, int y, int w, int h, int[] pids, bool own)
    {
        var keep = new HashSet<uint>(); foreach (int p in pids ?? new int[0]) keep.Add((uint)p);
        var list = new List<IntPtr>();
        EnumWindows((hw, l) =>
        {
            if (!IsWindowVisible(hw) || IsIconic(hw) || Cloaked(hw) || !GetWindowRect(hw, out RECT r) || !Reaches(r, x, y, w, h)) return true;
            GetWindowThreadProcessId(hw, out uint pid);
            if (keep.Contains(pid) != own) return true;
            if (!own && ShellClasses.Contains(ClassOf(hw))) return true;
            list.Add(hw);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    static string Describe(IntPtr hw)
    {
        GetWindowRect(hw, out RECT r); GetWindowThreadProcessId(hw, out uint pid);
        return $"{hw.ToInt64():x} pid {pid} '{TitleOf(hw)}' [{ClassOf(hw)}] {r.Left},{r.Top},{r.Right},{r.Bottom}";
    }

    /// <summary>Windows of other processes than keepPids over the rect ("hwnd pid 'title' [class] l,t,r,b"): the
    /// runner's own consoles grow with the display scale and covered the whole bar at 150 % (run 42369550297).</summary>
    public static string[] Covering(int x, int y, int w, int h, int[] keepPids)
    {
        var o = new List<string>(); foreach (var hw in WindowsOver(x, y, w, h, keepPids, false)) o.Add(Describe(hw)); return o.ToArray();
    }

    /// <summary>Minimises (without activating anything) every window Covering() lists; one that cannot minimise is
    /// moved to the top left corner instead. Returns what it did, one line per window.</summary>
    public static string[] ClearRect(int x, int y, int w, int h, int[] keepPids)
    {
        var o = new List<string>();
        foreach (var hw in WindowsOver(x, y, w, h, keepPids, false))
        {
            string d = Describe(hw);
            ShowWindowAsync(hw, 7 /* SW_SHOWMINNOACTIVE */);
            System.Threading.Thread.Sleep(150);
            if (!IsIconic(hw) && IsWindowVisible(hw))
            {
                SetWindowPos(hw, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0004 | 0x0010 /* NOSIZE | NOZORDER | NOACTIVATE */);
                o.Add("moved " + d);
            }
            else o.Add("minimised " + d);
        }
        return o.ToArray();
    }

    /// <summary>The window in FRONT over the rect: the first visible, uncloaked, not minimised top-level window in
    /// the z-order that reaches into it (shell windows left out), as "pid|hwnd pid 'title' [class] l,t,r,b"; "" if
    /// none. Covering() lists every window of another process over the rect whatever its z-order; this says which
    /// one is actually drawn (the notifs backdrop is under the bar or over it).</summary>
    public static string TopOver(int x, int y, int w, int h)
    {
        string top = "";
        EnumWindows((hw, l) =>
        {
            if (!IsWindowVisible(hw) || IsIconic(hw) || Cloaked(hw) || !GetWindowRect(hw, out RECT r) || !Reaches(r, x, y, w, h)) return true;
            if (ShellClasses.Contains(ClassOf(hw))) return true;
            GetWindowThreadProcessId(hw, out uint pid);
            top = pid + "|" + Describe(hw);
            return false;
        }, IntPtr.Zero);
        return top;
    }

    /// <summary>Puts every visible top-level window of the pid at the bottom of the z-order without activating
    /// anything (a backdrop that must stay behind the app under test). Returns what it moved, one line each.</summary>
    public static string[] ToBottom(int pid)
    {
        var o = new List<string>(); var wins = new List<IntPtr>();
        EnumWindows((hw, l) =>
        {
            GetWindowThreadProcessId(hw, out uint p);
            if (p == (uint)pid && IsWindowVisible(hw)) wins.Add(hw);
            return true;
        }, IntPtr.Zero);
        // moved after the walk: moving windows while EnumWindows walks the z-order could visit one twice
        foreach (var hw in wins)
        {
            o.Add(Describe(hw));
            SetWindowPos(hw, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE | NOMOVE | NOACTIVATE */);
        }
        return o.ToArray();
    }

    static bool AnyExcluded(List<IntPtr> wins)
    {
        foreach (var hw in wins) if (GetWindowDisplayAffinity(hw, out uint a) && a != 0) return true;
        return false;
    }

    public sealed class Shot { public byte[] Px = Array.Empty<byte>(); public bool Clean; public int WaitedMs, Tries, Windows; }

    /// <summary>A Grab taken while none of the pids' windows over the rect is excluded from capture: the display
    /// affinity of each must read 0 for quietMs before the grab and still read 0 right after it (an exclusion
    /// takes 25 ms + a picture, so it cannot come and go unseen inside one grab). Waits and tries again up to
    /// timeoutMs; Clean = false if it never got one (the last grab is returned so the caller can still save it).</summary>
    public static Shot CleanGrab(int[] pids, int x, int y, int w, int h, int quietMs, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var wins = WindowsOver(x, y, w, h, pids, true);
        var shot = new Shot { Windows = wins.Count };
        timeBeginPeriod(1);
        try
        {
            while (true)
            {
                long quietSince = -1;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    if (AnyExcluded(wins)) quietSince = -1;
                    else if (quietSince < 0) quietSince = sw.ElapsedMilliseconds;
                    else if (sw.ElapsedMilliseconds - quietSince >= quietMs) break;
                    System.Threading.Thread.Sleep(2);
                }
                shot.Tries++;
                shot.Px = Grab(x, y, w, h);
                shot.Clean = quietSince >= 0 && sw.ElapsedMilliseconds - quietSince >= quietMs && !AnyExcluded(wins);
                if (shot.Clean || sw.ElapsedMilliseconds >= timeoutMs) { shot.WaitedMs = (int)sw.ElapsedMilliseconds; return shot; }
            }
        }
        finally { timeEndPeriod(1); }
    }

    /// <summary>Mean absolute difference (0-255) of a crop against the same rect inside a whole-screen grab of
    /// width fw: did the screen change between the two shots of Shoot?</summary>
    public static double CropVsFull(byte[] crop, int cw, int ch, byte[] full, int fw, int cx, int cy)
    {
        double s = 0;
        for (int row = 0; row < ch; row++)
            for (int col = 0; col < cw; col++)
            {
                int a = (row * cw + col) * 4, b = ((cy + row) * fw + cx + col) * 4;
                s += Math.Abs(crop[a] - full[b]) + Math.Abs(crop[a + 1] - full[b + 1]) + Math.Abs(crop[a + 2] - full[b + 2]);
            }
        return s / (cw * ch * 3.0);
    }

    /// <summary>Motion over clean grabs only (see CleanGrab): a grab without the bar in it is not motion.</summary>
    public static double MotionClean(int[] pids, int x, int y, int w, int h, int gapMs)
    {
        var pa = CleanGrab(pids, x, y, w, h, 60, 3000).Px;
        System.Threading.Thread.Sleep(gapMs);
        var pb = CleanGrab(pids, x, y, w, h, 60, 3000).Px;
        double s = 0; for (int i = 0; i < pa.Length; i++) if (i % 4 != 3) s += Math.Abs(pa[i] - pb[i]);
        return s / (pa.Length / 4 * 3);
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

    /// <summary>The share (%) of pixels inside one rectangle (image pixels) whose largest channel delta is over
    /// threshold: how much of a known gap is still there. [diffPxPct, meanAbs, pixels].</summary>
    public static double[] RegionDiff(byte[] pa, byte[] pb, int w, int h, int rx, int ry, int rw, int rh, int threshold)
    {
        int over = 0, n = 0; double sum = 0;
        for (int y = Math.Max(0, ry); y < Math.Min(h, ry + rh); y++)
            for (int x = Math.Max(0, rx); x < Math.Min(w, rx + rw); x++)
            {
                int o = (y * w + x) * 4;
                int db = Math.Abs(pa[o] - pb[o]), dg = Math.Abs(pa[o + 1] - pb[o + 1]), dr = Math.Abs(pa[o + 2] - pb[o + 2]);
                sum += (db + dg + dr) / 3.0; n++;
                if (Math.Max(dr, Math.Max(dg, db)) > threshold) over++;
            }
        return new[] { n == 0 ? 0 : 100.0 * over / n, n == 0 ? 0 : sum / n, n };
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
