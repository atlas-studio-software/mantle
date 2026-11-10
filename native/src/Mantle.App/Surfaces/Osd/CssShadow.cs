using System;
using System.Collections.Generic;
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

    /// The premultiplied BGRA pixels (w x h physical px) of the layers round every box, at <paramref name="scale"/> px per DIP.
    public static byte[] Pixels(int w, int h, double scale, IReadOnlyList<Box> boxes, IReadOnlyList<Layer> layers)
    {
        var px = new byte[w * h * 4];
        if (w <= 0 || h <= 0) return px;
        // the stack: alpha and premultiplied colour, drawn bottom layer first (CSS paints the first layer on top)
        var a = new double[w * h]; var r = new double[w * h]; var g = new double[w * h]; var b = new double[w * h];
        for (int li = layers.Count - 1; li >= 0; li--)
        {
            var L = layers[li];
            var m = new double[w * h];
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
            Blur(m, w, h, L.Blur / 2 * scale);
            for (int i = 0; i < m.Length; i++)
            {
                double s = m[i] * L.A;
                if (s <= 0) continue;
                a[i] = s + a[i] * (1 - s);
                r[i] = L.R / 255.0 * s + r[i] * (1 - s); g[i] = L.G / 255.0 * s + g[i] * (1 - s); b[i] = L.B / 255.0 * s + b[i] * (1 - s);
            }
        }
        // nothing under the elements themselves
        var inside = new double[w * h];
        foreach (var bx in boxes) Fill(inside, w, h, bx.X * scale, bx.Y * scale, bx.W * scale, bx.H * scale, Math.Min(bx.Radius, Math.Min(bx.W, bx.H) / 2) * scale);
        for (int i = 0; i < a.Length; i++)
        {
            double k = 1 - Math.Min(1, inside[i]);
            px[i * 4 + 0] = B255(b[i] * k); px[i * 4 + 1] = B255(g[i] * k); px[i * 4 + 2] = B255(r[i] * k); px[i * 4 + 3] = B255(a[i] * k);
        }
        return px;
    }

    public static WriteableBitmap Bitmap(int w, int h, double scale, IReadOnlyList<Box> boxes, IReadOnlyList<Layer> layers)
    {
        var bmp = new WriteableBitmap(Math.Max(1, w), Math.Max(1, h));
        Pixels(Math.Max(1, w), Math.Max(1, h), scale, boxes, layers).CopyTo(bmp.PixelBuffer);
        bmp.Invalidate();
        return bmp;
    }

    static byte B255(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v * 255)));

    /// Coverage of a rounded rectangle (anti-aliased by its signed distance), max-ed into m.
    static void Fill(double[] m, int w, int h, double x, double y, double rw, double rh, double rad)
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

    /// A separable Gaussian (sigma in px); sigma under .2 px is no blur.
    static void Blur(double[] m, int w, int h, double sigma)
    {
        if (sigma < .2) return;
        int rad = (int)Math.Ceiling(sigma * 3);
        var k = new double[2 * rad + 1]; double sum = 0;
        for (int i = -rad; i <= rad; i++) { k[i + rad] = Math.Exp(-(i * i) / (2 * sigma * sigma)); sum += k[i + rad]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        var t = new double[w * h];
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
