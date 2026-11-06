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

namespace MantleBar.Native;

internal static class Backdrop
{
    /// The pill's rectangle on screen, in physical pixels. Returns null when the screen cannot be read.
    public static WriteableBitmap? Capture(Win32.RECT pill, Win32.RECT screen, double sigmaPx, double saturate)
    {
        int margin = (int)Math.Ceiling(sigmaPx * 3);
        int x0 = Math.Max(screen.Left, pill.Left - margin), y0 = Math.Max(screen.Top, pill.Top - margin);
        int x1 = Math.Min(screen.Right, pill.Right + margin), y1 = Math.Min(screen.Bottom, pill.Bottom + margin);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) return null;

        byte[]? px = Grab(x0, y0, w, h);
        if (px == null) return null;
        if (sigmaPx > 0.5) Blur(px, w, h, sigmaPx);
        if (Math.Abs(saturate - 1) > 0.001) Saturate(px, saturate);

        int cw = pill.Right - pill.Left, ch = pill.Bottom - pill.Top;
        int ox = pill.Left - x0, oy = pill.Top - y0;
        var crop = new byte[cw * ch * 4];
        for (int y = 0; y < ch; y++) {
            int sy = Math.Clamp(oy + y, 0, h - 1);
            for (int x = 0; x < cw; x++) {
                int sx = Math.Clamp(ox + x, 0, w - 1);
                int s = (sy * w + sx) * 4, d = (y * cw + x) * 4;
                crop[d] = px[s]; crop[d + 1] = px[s + 1]; crop[d + 2] = px[s + 2]; crop[d + 3] = 255;
            }
        }
        var bmp = new WriteableBitmap(cw, ch);
        crop.CopyTo(bmp.PixelBuffer);
        bmp.Invalidate();
        return bmp;
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
