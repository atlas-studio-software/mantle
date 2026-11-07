// GlassTools.cs - the glass check's own pixel maths (Add-Type from glass/check.ps1; not part of the app).
// Everything works on BGRA byte arrays of the same w x h rectangle (the bench's shot: the pill plus 40 px
// left and right, 60 above, down to the screen's bottom edge).
using System;

public static class GlassTools
{
    /// Masks in the shot's coordinates. kind: 0 = the whole pill (pixels fully inside its rounded rect),
    /// 1 = glass only (the pill minus the boxes where Electron's bar draws its mark, dock and right side),
    /// 2 = the band 2-30 px above the pill (the light drop shadow), 3 = the rim (the pill's outer 3 px:
    /// hairline, lip and the blur's edge).
    public static bool[] Mask(int w, int h, int px, int py, int pw, int ph, int r, int kind)
    {
        var m = new bool[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                bool inPill = Inside(x, y, px, py, pw, ph, r);
                bool v;
                if (kind == 0) v = inPill;
                else if (kind == 1) {
                    bool mark = x >= px + 10 && x < px + 42 && y >= py + 6 && y < py + 42;
                    bool dock = x >= px + 420 && x < px + pw - 8 && y >= py + 5 && y < py + 43;
                    v = inPill && !mark && !dock;
                } else if (kind == 2) v = y >= py - 30 && y < py - 2 && x >= px + 24 && x < px + pw - 24;
                else v = inPill && !Inside(x, y, px + 3, py + 3, pw - 6, ph - 6, Math.Max(0, r - 3));
                m[y * w + x] = v;
            }
        return m;
    }

    // a pixel counts when all four of its corners are inside the rounded rect
    static bool Inside(int x, int y, int rx, int ry, int rw, int rh, int r)
    {
        return In(x, y, rx, ry, rw, rh, r) && In(x + 1, y, rx, ry, rw, rh, r) && In(x, y + 1, rx, ry, rw, rh, r) && In(x + 1, y + 1, rx, ry, rw, rh, r);
    }
    static bool In(double x, double y, int rx, int ry, int rw, int rh, int r)
    {
        if (x < rx || y < ry || x > rx + rw || y > ry + rh) return false;
        double cx = Math.Min(Math.Max(x, rx + r), rx + rw - r), cy = Math.Min(Math.Max(y, ry + r), ry + rh - r);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= (double)r * r;
    }

    public static int Count(bool[] m) { int n = 0; foreach (var b in m) if (b) n++; return n; }

    /// |a - b| per colour channel over the mask: [mean, p95, max, pixels].
    public static double[] Stats(byte[] a, byte[] b, bool[] m)
    {
        var hist = new long[256]; long n = 0, sum = 0; int max = 0;
        for (int p = 0; p < m.Length; p++) {
            if (!m[p]) continue;
            int i = p * 4;
            for (int c = 0; c < 3; c++) {
                int d = Math.Abs(a[i + c] - b[i + c]);
                hist[d]++; sum += d; n++; if (d > max) max = d;
            }
        }
        if (n == 0) return new double[] { 0, 0, 0, 0 };
        long want = (long)Math.Ceiling(n * 0.95), acc = 0; int p95 = 0;
        for (int d = 0; d < 256; d++) { acc += hist[d]; if (acc >= want) { p95 = d; break; } }
        return new double[] { Math.Round((double)sum / n, 2), p95, max, n / 3 };
    }

    /// Mean R, G, B over the mask.
    public static double[] MeanRgb(byte[] a, bool[] m)
    {
        double r = 0, g = 0, b = 0; long n = 0;
        for (int p = 0; p < m.Length; p++) {
            if (!m[p]) continue;
            int i = p * 4; b += a[i]; g += a[i + 1]; r += a[i + 2]; n++;
        }
        return n == 0 ? new double[] { 0, 0, 0 } : new[] { Math.Round(r / n, 1), Math.Round(g / n, 1), Math.Round(b / n, 1) };
    }

    /// Mean of all three channels over a small grab (for the "did the glass follow" poll).
    public static double Mean(byte[] a)
    {
        long s = 0; int n = 0;
        for (int i = 0; i + 2 < a.Length; i += 4) { s += a[i] + a[i + 1] + a[i + 2]; n += 3; }
        return n == 0 ? 0 : (double)s / n;
    }

    /// Electron above, native in the middle, |difference| x4 (grey) below, `gap` px of white between. Opaque BGRA.
    public static byte[] SideBySide(byte[] e, byte[] n, int w, int h, int gap)
    {
        int H = 3 * h + 2 * gap;
        var o = new byte[w * H * 4];
        for (int i = 0; i < o.Length; i++) o[i] = 255;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int s = (y * w + x) * 4;
                int d0 = (y * w + x) * 4, d1 = ((y + h + gap) * w + x) * 4, d2 = ((y + 2 * h + 2 * gap) * w + x) * 4;
                int dm = 0;
                for (int c = 0; c < 3; c++) {
                    o[d0 + c] = e[s + c]; o[d1 + c] = n[s + c];
                    dm = Math.Max(dm, Math.Abs(e[s + c] - n[s + c]));
                }
                byte v = (byte)Math.Max(0, 255 - Math.Min(255, dm * 4));
                o[d2] = o[d2 + 1] = o[d2 + 2] = v;
                o[d0 + 3] = o[d1 + 3] = o[d2 + 3] = 255;
            }
        return o;
    }
}
