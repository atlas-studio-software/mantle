// Backdrop.cs - the island's frosted glass: a picture of what is behind the pill, blurred once.
//
// This is the Electron bar's own mechanism (main.js applyGlass: "main captures what is behind the
// bar and hands the page a picture of it, and the page blurs that with CSS"), ported as is:
// capture the screen behind the pill BEFORE the window shows, blur it (light: blur(12px)
// saturate(150%); dark: blur(5px), from ui/shared/glass-light.css -lg-blur), and paint it under
// the tint. It costs one capture and one blur per reveal, and nothing while the bar sits still.
//
// Not used: DWM's acrylic/Mica. main.js records why: a system backdrop only draws on the ACTIVE
// window and the bar is never active. WinUI 3's DesktopAcrylicController can be told the window is
// always active and takes a tint alpha, but it fills the whole window rectangle, and the bar is a
// rounded pill inside a larger transparent window. See native-spike/README.md.
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Native;

internal static class Backdrop
{
    /// What was behind the pill before the window showed: the raw pixels around the pill, with a
    /// margin for the widest blur, kept so a theme change can re-blur it without capturing the bar.
    internal sealed class Shot
    {
        public byte[] Px = Array.Empty<byte>();
        public int X0, Y0, W, H;
    }

    /// The pill's rectangle on screen, in physical pixels. Returns null when the screen cannot be read.
    public static Shot? Grab(Win32.RECT pill, Win32.RECT screen, double maxSigmaPx)
    {
        int margin = (int)Math.Ceiling(maxSigmaPx * 3);
        int x0 = Math.Max(screen.Left, pill.Left - margin), y0 = Math.Max(screen.Top, pill.Top - margin);
        int x1 = Math.Min(screen.Right, pill.Right + margin), y1 = Math.Min(screen.Bottom, pill.Bottom + margin);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return null;
        byte[]? raw = Grab(x0, y0, w, h);
        return raw == null ? null : new Shot { Px = raw, X0 = x0, Y0 = y0, W = w, H = h };
    }

    /// Blur (and saturate) a copy of the shot, cropped to the pill. `into` is reused when it has the pill's size
    /// (the light glass loop swaps ~4 pictures a second; a new bitmap each time would only churn memory).
    public static WriteableBitmap Render(Shot shot, Win32.RECT pill, double sigmaPx, double saturate, WriteableBitmap? into = null, int stripScale = 1)
        => ToBitmap(Blurred(shot, pill, sigmaPx, saturate, stripScale), pill.Right - pill.Left, pill.Bottom - pill.Top, into);

    /// THE BAR'S STRIP IS A QUARTER-RESOLUTION PICTURE in Electron: main.js asks the sidecar for the screen behind the
    /// bar at BACKDROP_SCALE = 4 (sidecar/Backdrop.cs: HighQualityBilinear down to 1/4 a side) and the page stretches it
    /// back over the pill (bar.css .bdrop img, object-fit: fill) before the CSS blur. So fine detail behind the bar (a
    /// line of desktop text, a window edge) is smeared even at blur 0. The bar passes this so its glass is that same
    /// picture (lane r12-ci-fixes: at 150 % the runner's "Windows Server" desktop text lay under the pill's right end,
    /// sharp in native and smeared in Electron: 560 of the 742 light-150 pixels over the parity threshold).
    public const int BarStripScale = 4;

    /// The UI-thread half of Render: the cropped BGRA pixels into a bitmap.
    public static WriteableBitmap ToBitmap(byte[] crop, int cw, int ch, WriteableBitmap? into = null)
    {
        var bmp = into != null && into.PixelWidth == cw && into.PixelHeight == ch ? into : new WriteableBitmap(cw, ch);
        crop.CopyTo(bmp.PixelBuffer);
        bmp.Invalidate();
        return bmp;
    }

    /// The thread-free half of Render (no XAML object): blur, saturate, crop to the pill. Safe off the UI thread.
    public static byte[] Blurred(Shot shot, Win32.RECT pill, double sigmaPx, double saturate, int stripScale = 1)
    {
        byte[] px = (byte[])shot.Px.Clone();
        int w = shot.W, h = shot.H;
        if (stripScale > 1) Strip(px, w, h, stripScale);
        if (sigmaPx > 0.5) Blur(px, w, h, sigmaPx);
        if (Math.Abs(saturate - 1) > 0.001) Saturate(px, saturate);

        int cw = pill.Right - pill.Left, ch = pill.Bottom - pill.Top;
        int ox = pill.Left - shot.X0, oy = pill.Top - shot.Y0;
        var crop = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) {
            int sy = Math.Clamp(oy + y, 0, h - 1);
            for (int x = 0; x < cw; x++) {
                int sx = Math.Clamp(ox + x, 0, w - 1);
                int s = (sy * w + sx) * 4, d = (y * cw + x) * 4;
                crop[d] = px[s]; crop[d + 1] = px[s + 1]; crop[d + 2] = px[s + 2]; crop[d + 3] = 255;
            }
        }
        return crop;
    }

    static byte[]? Grab(int x, int y, int w, int h)
    {
        IntPtr screen = Win32.GetDC(IntPtr.Zero);
        IntPtr mem = Win32.CreateCompatibleDC(screen);
        IntPtr bmp = Win32.CreateCompatibleBitmap(screen, w, h);
        IntPtr old = Win32.SelectObject(mem, bmp);
        try {
            if (!Win32.BitBlt(mem, 0, 0, w, h, screen, x, y, Win32.SRCCOPY | Win32.CAPTUREBLT)) return null;
            Win32.SelectObject(mem, old);
            var bmi = new Win32.BITMAPINFO { bmiColors = new uint[256] };
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w; bmi.bmiHeader.biHeight = -h; bmi.bmiHeader.biPlanes = 1; bmi.bmiHeader.biBitCount = 32;
            var bits = new byte[w * h * 4];
            return Win32.GetDIBits(mem, bmp, 0, (uint)h, bits, ref bmi, 0) == 0 ? null : bits;
        } finally {
            Win32.DeleteObject(bmp); Win32.DeleteDC(mem); Win32.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// Electron's strip, in place: the picture averaged down to 1/k a side (round(w/k) x round(h/k), each small pixel
    /// the mean of its box, as a high-quality bilinear downscale does) and stretched back to w x h bilinearly with pixel
    /// centres aligned (a browser's object-fit: fill). See <see cref="BarStripScale"/>.
    internal static void Strip(byte[] px, int w, int h, int k)
    {
        int sw = Math.Max(1, (int)Math.Round((double)w / k)), sh = Math.Max(1, (int)Math.Round((double)h / k));
        if (sw == w && sh == h) return;
        var small = new double[sw * sh * 3];
        for (int sy = 0; sy < sh; sy++) {
            int y0 = sy * h / sh, y1 = Math.Max(y0 + 1, (sy + 1) * h / sh);
            for (int sx = 0; sx < sw; sx++) {
                int x0 = sx * w / sw, x1 = Math.Max(x0 + 1, (sx + 1) * w / sw);
                double b = 0, g = 0, r = 0; int n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) { int o = (y * w + x) * 4; b += px[o]; g += px[o + 1]; r += px[o + 2]; n++; }
                int d = (sy * sw + sx) * 3;
                small[d] = b / n; small[d + 1] = g / n; small[d + 2] = r / n;
            }
        }
        for (int y = 0; y < h; y++) {
            double fy = (y + 0.5) * sh / h - 0.5; int ya = (int)Math.Floor(fy); double ty = fy - ya;
            int ra = Math.Clamp(ya, 0, sh - 1) * sw, rb = Math.Clamp(ya + 1, 0, sh - 1) * sw;
            for (int x = 0; x < w; x++) {
                double fx = (x + 0.5) * sw / w - 0.5; int xa = (int)Math.Floor(fx); double tx = fx - xa;
                int ca = Math.Clamp(xa, 0, sw - 1), cb = Math.Clamp(xa + 1, 0, sw - 1);
                int o = (y * w + x) * 4;
                for (int c = 0; c < 3; c++) {
                    double v = (small[(ra + ca) * 3 + c] * (1 - tx) + small[(ra + cb) * 3 + c] * tx) * (1 - ty)
                             + (small[(rb + ca) * 3 + c] * (1 - tx) + small[(rb + cb) * 3 + c] * tx) * ty;
                    px[o + c] = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
                }
            }
        }
    }

    // Three box blurs = a Gaussian of the given sigma (the usual boxes-for-gauss construction).
    static void Blur(byte[] px, int w, int h, double sigma)
    {
        const int n = 3;
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal); if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int m = (int)Math.Round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4));
        var tmp = new byte[px.Length];
        for (int i = 0; i < n; i++) {
            int r = ((i < m ? wl : wu) - 1) / 2;
            BoxH(px, tmp, w, h, r);
            BoxV(tmp, px, w, h, r);
        }
    }

    static void BoxH(byte[] src, byte[] dst, int w, int h, int r)
    {
        double k = 1.0 / (2 * r + 1);
        for (int y = 0; y < h; y++) {
            int row = y * w;
            for (int c = 0; c < 3; c++) {
                int acc = 0;
                for (int i = -r; i <= r; i++) acc += src[(row + Math.Clamp(i, 0, w - 1)) * 4 + c];
                for (int x = 0; x < w; x++) {
                    dst[(row + x) * 4 + c] = (byte)(acc * k + 0.5);
                    int add = Math.Min(x + r + 1, w - 1), sub = Math.Max(x - r, 0);
                    acc += src[(row + add) * 4 + c] - src[(row + sub) * 4 + c];
                }
            }
        }
    }

    static void BoxV(byte[] src, byte[] dst, int w, int h, int r)
    {
        double k = 1.0 / (2 * r + 1);
        for (int x = 0; x < w; x++) {
            for (int c = 0; c < 3; c++) {
                int acc = 0;
                for (int i = -r; i <= r; i++) acc += src[(Math.Clamp(i, 0, h - 1) * w + x) * 4 + c];
                for (int y = 0; y < h; y++) {
                    dst[(y * w + x) * 4 + c] = (byte)(acc * k + 0.5);
                    int add = Math.Min(y + r + 1, h - 1), sub = Math.Max(y - r, 0);
                    acc += src[(add * w + x) * 4 + c] - src[(sub * w + x) * 4 + c];
                }
            }
        }
    }

    // CSS saturate(s), the W3C filter matrix. Pixels are BGRA.
    static void Saturate(byte[] px, double s)
    {
        double rr = 0.213 + 0.787 * s, rg = 0.715 - 0.715 * s, rb = 0.072 - 0.072 * s;
        double gr = 0.213 - 0.213 * s, gg = 0.715 + 0.285 * s, gb = 0.072 - 0.072 * s;
        double br = 0.213 - 0.213 * s, bg = 0.715 - 0.715 * s, bb = 0.072 + 0.928 * s;
        for (int i = 0; i < px.Length; i += 4) {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            px[i + 2] = (byte)Math.Clamp(rr * r + rg * g + rb * b + 0.5, 0, 255);
            px[i + 1] = (byte)Math.Clamp(gr * r + gg * g + gb * b + 0.5, 0, 255);
            px[i] = (byte)Math.Clamp(br * r + bg * g + bb * b + 0.5, 0, 255);
        }
    }
}
