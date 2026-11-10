using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Surfaces.Osd;

/// <summary>
/// A CSS <c>box-shadow</c> list drawn exactly as Chromium means it, into a bitmap: each layer is the element's rounded
/// rectangle moved by (dx, dy), grown by spread (radius + spread, never below 0), blurred by a Gaussian of
/// sigma = blur / 2 (the CSS rule), all in the layer's colour; layers over each other (the first on top); and nothing
/// under the element itself (a box-shadow is clipped to outside its border box).
/// </summary>
/// <remarks>
/// Why not Composition's DropShadow (the bar's road): its BlurRadius is not CSS's blur. Measured on the OSD pairs
/// (run 42767286777, vol-62-light): the CSS 28 px layer showed as a hard, dark band under the pill, 0x13-0x18 darker
/// than the desktop all the way to the window's edge, where Chromium's is down to 0x02. A bitmap of the real formula
/// is a few ms once per size and theme, and matches. Shared by the OSD and the toast cards (lane native-osd-toasts).
/// </remarks>
internal static class CssShadow
{
    /// One layer: offset, blur, spread in DIP; its colour (straight alpha 0-1).
    public readonly record struct Layer(double Dx, double Dy, double Blur, double Spread, byte R, byte G, byte B, double A);

    /// One element (DIP, in the bitmap's space) with its corner radius.
    public readonly record struct Box(double X, double Y, double W, double H, double Radius);

    /// glass-light.css light --lg-shadow: 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22), 0 26px 56px -28px rgba(14,18,38,.30).
    public static readonly Layer[] LightLg =
    {
        new(0, 1, 1, 0, 14, 18, 38, .05),
        new(0, 10, 28, -12, 14, 18, 38, .22),
        new(0, 26, 56, -28, 14, 18, 38, .30),
    };

    /// The bitmap (w x h physical px) of the layers round every box, at <paramref name="scale"/> px per DIP.
    /// <remarks>
    /// No garbage: the working planes are unmanaged memory, freed before return, and the BGRA bytes go through one
    /// reused buffer. The first version allocated ~90 bytes per pixel on the managed heap (about 9 MB per OSD size, more
    /// per toast card), which brought the first full garbage collection to the first seconds of a run; after a full
    /// collection a theme change stopped Mantle (see SeeThroughBackdrop: the windows' backdrop handler is then called
    /// with no target; CI run 42777700651 forced one collection with no shadow code and crashed 4 of 4).
    /// </remarks>
    public static WriteableBitmap Bitmap(int w, int h, double scale, IReadOnlyList<Box> boxes, IReadOnlyList<Layer> layers)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        var bmp = new WriteableBitmap(w, h);
        lock (Gate)
        {
            int bytes = w * h * 4;
            if (s_px.Length < bytes) s_px = new byte[bytes];
            Pixels(w, h, scale, boxes, layers, s_px.AsSpan(0, bytes));
            s_px.CopyTo(0, bmp.PixelBuffer, 0, bytes);
        }
        bmp.Invalidate();
        return bmp;
    }

    static readonly object Gate = new();
    static byte[] s_px = Array.Empty<byte>();   // the BGRA bytes, reused (grown to the largest size asked for)

    /// The premultiplied BGRA pixels (w x h) into px: the planes live in one unmanaged block.
    static unsafe void Pixels(int w, int h, double scale, IReadOnlyList<Box> boxes, IReadOnlyList<Layer> layers, Span<byte> px)
    {
        int n = w * h;
        const int Planes = 7;   // a r g b (the stack), m (a layer), t (the blur's pass), inside
        double* block = (double*)NativeMemory.AllocZeroed((nuint)n * Planes, (nuint)sizeof(double));
        try
        {
            var a = new Span<double>(block, n); var r = new Span<double>(block + n, n); var g = new Span<double>(block + 2 * n, n);
            var b = new Span<double>(block + 3 * n, n); var m = new Span<double>(block + 4 * n, n); var t = new Span<double>(block + 5 * n, n);
            var inside = new Span<double>(block + 6 * n, n);
            // the stack: alpha and premultiplied colour, drawn bottom layer first (CSS paints the first layer on top)
            for (int li = layers.Count - 1; li >= 0; li--)
            {
                var L = layers[li];
                m.Clear();
                bool any = false;
                foreach (var bx in boxes)
                {
                    double sw = bx.W + 2 * L.Spread, sh = bx.H + 2 * L.Spread;
                    if (sw <= 0 || sh <= 0) continue;   // a spread that eats the shape leaves no shadow (CSS)
                    double rad = Math.Max(0, Math.Min(Math.Min(bx.Radius, Math.Min(bx.W, bx.H) / 2) + L.Spread, Math.Min(sw, sh) / 2));
                    Fill(m, w, h, (bx.X - L.Spread + L.Dx) * scale, (bx.Y - L.Spread + L.Dy) * scale, sw * scale, sh * scale, rad * scale);
                    any = true;
                }
                if (!any) continue;
                Blur(m, t, w, h, L.Blur / 2 * scale);
                for (int i = 0; i < n; i++)
                {
                    double s = m[i] * L.A;
                    if (s <= 0) continue;
                    a[i] = s + a[i] * (1 - s);
                    r[i] = L.R / 255.0 * s + r[i] * (1 - s); g[i] = L.G / 255.0 * s + g[i] * (1 - s); b[i] = L.B / 255.0 * s + b[i] * (1 - s);
                }
            }
            // nothing under the elements themselves
            foreach (var bx in boxes) Fill(inside, w, h, bx.X * scale, bx.Y * scale, bx.W * scale, bx.H * scale, Math.Min(bx.Radius, Math.Min(bx.W, bx.H) / 2) * scale);
            for (int i = 0; i < n; i++)
            {
                double k = 1 - Math.Min(1, inside[i]);
                px[i * 4 + 0] = B255(b[i] * k); px[i * 4 + 1] = B255(g[i] * k); px[i * 4 + 2] = B255(r[i] * k); px[i * 4 + 3] = B255(a[i] * k);
            }
        }
        finally { NativeMemory.Free(block); }
    }

    static byte B255(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255)));

    /// Coverage of a rounded rectangle (anti-aliased by its signed distance), max-ed into m.
    static void Fill(Span<double> m, int w, int h, double x, double y, double rw, double rh, double rad)
    {
        double cx = x + rw / 2, cy = y + rh / 2, hx = rw / 2 - rad, hy = rh / 2 - rad;
        int x0 = Math.Max(0, (int)Math.Floor(x - 1)), x1 = Math.Min(w - 1, (int)Math.Ceiling(x + rw + 1));
        int y0 = Math.Max(0, (int)Math.Floor(y - 1)), y1 = Math.Min(h - 1, (int)Math.Ceiling(y + rh + 1));
        for (int j = y0; j <= y1; j++)
            for (int i = x0; i <= x1; i++)
            {
                double qx = Math.Abs(i + .5 - cx) - hx, qy = Math.Abs(j + .5 - cy) - hy;
                double outside = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2)) + Math.Min(Math.Max(qx, qy), 0) - rad;
                double c = Math.Max(0, Math.Min(1, .5 - outside));
                if (c > m[j * w + i]) m[j * w + i] = c;
            }
    }

    /// A separable Gaussian (sigma in px) of m, with t as the between-passes plane; sigma under .2 px is no blur.
    static void Blur(Span<double> m, Span<double> t, int w, int h, double sigma)
    {
        if (sigma < .2) return;
        int rad = (int)Math.Ceiling(sigma * 3);
        Span<double> k = 2 * rad + 1 <= 512 ? stackalloc double[2 * rad + 1] : new double[2 * rad + 1];
        double sum = 0;
        for (int i = -rad; i <= rad; i++) { k[i + rad] = Math.Exp(-(i * i) / (2 * sigma * sigma)); sum += k[i + rad]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                double s = 0;
                for (int q = -rad; q <= rad; q++) { int x = i + q; if (x >= 0 && x < w) s += m[j * w + x] * k[q + rad]; }
                t[j * w + i] = s;
            }
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                double s = 0;
                for (int q = -rad; q <= rad; q++) { int y = j + q; if (y >= 0 && y < h) s += t[y * w + i] * k[q + rad]; }
                m[j * w + i] = s;
            }
    }
}
