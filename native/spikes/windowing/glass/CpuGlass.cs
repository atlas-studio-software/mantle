// CpuGlass.cs - the cpu mode: Electron's island glass, ported step for step and done on the CPU.
//
// What Electron does (main.js makeBackdrop/captureBackdrop, sidecar/Backdrop.cs, ui/bar/bar.css .bdrop):
//   light: a GDI capture of the PILL's rectangle (the bar window excluded from capture for ~25 ms),
//          4x down (GDI+ HighQualityBilinear), sent as a PNG 4x a second; the page stretches it over
//          the pill plus 14 px on every side (object-fit: fill), applies blur(12px) saturate(150%),
//          and clips it to the pill. The blur pulls transparency in from the picture's edges (that is
//          what the 14 px overscan is for), so the real screen shows through a little at the rim.
//   dark:  a 1/8 still of the screen (Windows' wallpaper as the desktop layer photographed it), cut to
//          the pill grown by 16 DIP on every side (edge pixels repeat past the screen), shown over
//          exactly that rectangle with blur(5px) and nothing else. One cut per landing, no loop.
// Here: the same rectangles, the same downscale (a tent filter as wide as the step), a bilinear
// stretch back, a Gaussian of the CSS sigma (three box passes) with transparent edges, saturate as
// the Filter Effects matrix, and the result handed to XAML as a premultiplied BGRA picture exactly
// the pill's size (no XAML scaling).
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glass;

internal sealed class CpuGlass
{
    // geometry, physical px
    readonly int _px, _py, _pw, _ph, _grow, _over, _pitch, _scrW, _scrH;
    readonly double _sigma, _sat;
    // the work buffers, kept between frames
    float[] _down = Array.Empty<float>(), _tmp = Array.Empty<float>(), _buf = Array.Empty<float>();
    public readonly byte[] Output;
    public double LastCaptureMs, LastRenderMs;

    /// light: grow 0, over 14, pitch 4, sigma 12, sat 1.5. dark: grow 16, over 16, pitch 8, sigma 5, sat 1.
    public CpuGlass(int px, int py, int pw, int ph, int grow, int over, int pitch, double sigma, double sat, int scrW, int scrH)
    {
        _px = px; _py = py; _pw = pw; _ph = ph; _grow = grow; _over = over; _pitch = pitch; _sigma = sigma; _sat = sat;
        _scrW = scrW; _scrH = scrH;
        Output = new byte[pw * ph * 4];
    }

    public string Describe() =>
        $"capture {_pw + 2 * _grow}x{_ph + 2 * _grow} (pill grown {_grow}), 1/{_pitch} down, shown over pill +{_over}, blur {_sigma} px, saturate {_sat}";

    /// Capture (the caller has excluded the window from capture) and render into Output. False if the screen could not be read.
    public bool CaptureAndRender()
    {
        var sw = Stopwatch.StartNew();
        int gx = _px - _grow, gy = _py - _grow, gw = _pw + 2 * _grow, gh = _ph + 2 * _grow;
        // the part of the grown rectangle that is on the screen; the rest repeats its edge pixels
        int kx0 = Math.Max(0, gx), ky0 = Math.Max(0, gy), kx1 = Math.Min(_scrW, gx + gw), ky1 = Math.Min(_scrH, gy + gh);
        if (kx1 <= kx0 || ky1 <= ky0) return false;
        byte[]? shot = Grab(kx0, ky0, kx1 - kx0, ky1 - ky0);
        LastCaptureMs = sw.Elapsed.TotalMilliseconds;
        if (shot == null) return false;
        Render(shot, kx0 - gx, ky0 - gy, kx1 - kx0, ky1 - ky0, gw, gh);
        LastRenderMs = sw.Elapsed.TotalMilliseconds - LastCaptureMs;
        return true;
    }

    void Render(byte[] shot, int kx, int ky, int kw, int kh, int gw, int gh)
    {
        // 1. down: tent filter, separable (x then y); source reads clamp into the captured part
        int sw = Math.Max(1, (int)Math.Round((double)gw / _pitch)), sh = Math.Max(1, (int)Math.Round((double)gh / _pitch));
        var tx = Taps(sw, gw, tent: true); var ty = Taps(sh, gh, tent: true);
        Ensure(ref _tmp, sw * gh * 3);
        for (int y = 0; y < gh; y++) {
            int sy = Math.Clamp(y - ky, 0, kh - 1);
            for (int i = 0; i < sw; i++) {
                float b = 0, g = 0, r = 0;
                foreach (var (j, wgt) in tx[i]) {
                    int sx = Math.Clamp(j - kx, 0, kw - 1);
                    int s = (sy * kw + sx) * 4;
                    b += shot[s] * wgt; g += shot[s + 1] * wgt; r += shot[s + 2] * wgt;
                }
                int d = (y * sw + i) * 3; _tmp[d] = b; _tmp[d + 1] = g; _tmp[d + 2] = r;
            }
        }
        Ensure(ref _down, sw * sh * 3);
        for (int i = 0; i < sh; i++)
            for (int x = 0; x < sw; x++) {
                float b = 0, g = 0, r = 0;
                foreach (var (j, wgt) in ty[i]) { int s = (j * sw + x) * 3; b += _tmp[s] * wgt; g += _tmp[s + 1] * wgt; r += _tmp[s + 2] * wgt; }
                int d = (i * sw + x) * 3; _down[d] = b; _down[d + 1] = g; _down[d + 2] = r;
            }

        // 2. up: bilinear to the shown rectangle (pill + over), into a zero (transparent) padded
        //    premultiplied BGRA float buffer, alpha 1 inside the picture
        int ow = _pw + 2 * _over, oh = _ph + 2 * _over;
        int pad = (int)Math.Ceiling(3 * _sigma);
        int bw = ow + 2 * pad, bh = oh + 2 * pad;
        Ensure(ref _buf, bw * bh * 4); Array.Clear(_buf, 0, bw * bh * 4);
        var ux = Taps(ow, sw, tent: false); var uy = Taps(oh, sh, tent: false);
        for (int y = 0; y < oh; y++)
            for (int x = 0; x < ow; x++) {
                float b = 0, g = 0, r = 0;
                foreach (var (jy, wy) in uy[y])
                    foreach (var (jx, wx) in ux[x]) {
                        int s = (jy * sw + jx) * 3; float w = wy * wx;
                        b += _down[s] * w; g += _down[s + 1] * w; r += _down[s + 2] * w;
                    }
                int d = ((y + pad) * bw + x + pad) * 4; _buf[d] = b; _buf[d + 1] = g; _buf[d + 2] = r; _buf[d + 3] = 1f;
            }

        // 3. blur: three box passes each way = a Gaussian of sigma, transparent past the edges
        Ensure(ref _tmp, bw * bh * 4);
        foreach (int rad in BoxRadii(_sigma)) { BoxH(_buf, _tmp, bw, bh, rad); BoxV(_tmp, _buf, bw, bh, rad); }

        // 4. the pill's part, saturated (on straight colour), back to premultiplied bytes
        double s0 = _sat;
        double rr = 0.213 + 0.787 * s0, rg = 0.715 - 0.715 * s0, rb = 0.072 - 0.072 * s0;
        double gr = 0.213 - 0.213 * s0, gg = 0.715 + 0.285 * s0, gb = 0.072 - 0.072 * s0;
        double br = 0.213 - 0.213 * s0, bg = 0.715 - 0.715 * s0, bb = 0.072 + 0.928 * s0;
        bool sat = Math.Abs(s0 - 1) > 0.001;
        for (int y = 0; y < _ph; y++)
            for (int x = 0; x < _pw; x++) {
                int s = ((y + _over + pad) * bw + x + _over + pad) * 4, d = (y * _pw + x) * 4;
                double a = Math.Clamp(_buf[s + 3], 0f, 1f);
                if (a < 1e-4) { Output[d] = Output[d + 1] = Output[d + 2] = Output[d + 3] = 0; continue; }
                double B = _buf[s] / a, G = _buf[s + 1] / a, R = _buf[s + 2] / a;
                if (sat) {
                    double r2 = rr * R + rg * G + rb * B, g2 = gr * R + gg * G + gb * B, b2 = br * R + bg * G + bb * B;
                    R = r2; G = g2; B = b2;
                }
                Output[d] = (byte)Math.Clamp(Math.Clamp(B, 0, 255) * a + 0.5, 0, 255);
                Output[d + 1] = (byte)Math.Clamp(Math.Clamp(G, 0, 255) * a + 0.5, 0, 255);
                Output[d + 2] = (byte)Math.Clamp(Math.Clamp(R, 0, 255) * a + 0.5, 0, 255);
                Output[d + 3] = (byte)Math.Clamp(a * 255 + 0.5, 0, 255);
            }
    }

    static void Ensure(ref float[] a, int n) { if (a.Length < n) a = new float[n]; }

    /// Resample taps for n_out samples over n_in, pixel centres lined up (Chromium / GDI+ convention).
    /// tent: a prefilter as wide as the step (downscale); otherwise plain bilinear (upscale). Indices clamp.
    static (int j, float w)[][] Taps(int nOut, int nIn, bool tent)
    {
        var all = new (int, float)[nOut][];
        double s = (double)nIn / nOut;
        for (int i = 0; i < nOut; i++) {
            double c = (i + 0.5) * s - 0.5;
            var list = new System.Collections.Generic.List<(int, float)>();
            if (tent) {
                double hw = Math.Max(1.0, s), sum = 0;
                for (int j = (int)Math.Floor(c - hw); j <= (int)Math.Ceiling(c + hw); j++) {
                    double w = Math.Max(0, 1 - Math.Abs(j - c) / hw);
                    if (w > 0) { list.Add((Math.Clamp(j, 0, nIn - 1), (float)w)); sum += w; }
                }
                for (int k = 0; k < list.Count; k++) list[k] = (list[k].Item1, (float)(list[k].Item2 / sum));
            } else {
                double cc = Math.Clamp(c, 0, nIn - 1);
                int j0 = (int)Math.Floor(cc), j1 = Math.Min(j0 + 1, nIn - 1); double t = cc - j0;
                list.Add((j0, (float)(1 - t))); list.Add((j1, (float)t));
            }
            all[i] = list.ToArray();
        }
        return all;
    }

    /// The usual boxes-for-gauss construction: three box widths whose convolution has variance sigma^2.
    static int[] BoxRadii(double sigma)
    {
        const int n = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal); if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int m = (int)Math.Round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4));
        var r = new int[n];
        for (int i = 0; i < n; i++) r[i] = ((i < m ? wl : wu) - 1) / 2;
        return r;
    }

    // Box passes on premultiplied RGBA floats; outside the buffer counts as transparent (zero).
    static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        float k = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++) {
            int row = y * w;
            for (int c = 0; c < 4; c++) {
                float acc = 0;
                for (int i = 0; i <= r && i < w; i++) acc += src[(row + i) * 4 + c];
                for (int x = 0; x < w; x++) {
                    dst[(row + x) * 4 + c] = acc * k;
                    int add = x + r + 1, sub = x - r;
                    if (add < w) acc += src[(row + add) * 4 + c];
                    if (sub >= 0) acc -= src[(row + sub) * 4 + c];
                }
            }
        }
    }
    static void BoxV(float[] src, float[] dst, int w, int h, int r)
    {
        float k = 1f / (2 * r + 1);
        for (int x = 0; x < w; x++)
            for (int c = 0; c < 4; c++) {
                float acc = 0;
                for (int i = 0; i <= r && i < h; i++) acc += src[(i * w + x) * 4 + c];
                for (int y = 0; y < h; y++) {
                    dst[(y * w + x) * 4 + c] = acc * k;
                    int add = y + r + 1, sub = y - r;
                    if (add < h) acc += src[(add * w + x) * 4 + c];
                    if (sub >= 0) acc -= src[(sub * w + x) * 4 + c];
                }
            }
    }

    // ---- GDI capture of the composed screen (what DWM shows; windows with WDA_EXCLUDEFROMCAPTURE are left out)
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

    static byte[]? Grab(int x, int y, int w, int h)
    {
        IntPtr s = GetDC(IntPtr.Zero), m = CreateCompatibleDC(s), b = CreateCompatibleBitmap(s, w, h);
        IntPtr old = SelectObject(m, b);
        try {
            if (!BitBlt(m, 0, 0, w, h, s, x, y, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */)) return null;
            SelectObject(m, old);
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            return GetDIBits(m, b, 0, (uint)h, bits, ref bmi, 0) == 0 ? null : bits;
        } finally { DeleteObject(b); DeleteDC(m); ReleaseDC(IntPtr.Zero, s); }
    }
}
