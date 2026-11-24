using System;

namespace Mantle.Core.Look.Levitate;

/// <summary>A BGRA bitmap (4 bytes per px, rows top to bottom, no stride padding): the scaled still or one blurred copy.</summary>
public sealed record FrostBitmap(byte[] Bgra, int W, int H);

/// <summary>
/// The levitated look's frost: a two-level, size-scaled PRE-blur of the wallpaper still (prototype quartz-home/app.js,
/// 2026-11-24). Done ONCE per wallpaper change on a pool thread, never live: the still scaled to 512 px wide
/// (<see cref="BaseSize"/>, the decoder's job), then two Gaussian copies of it, light (sigma 6 px, about 16 px on screen) for
/// small chrome and soft (sigma 15 px, about 40 px on screen) for big panels. Before blurring the bitmap is padded by
/// ceil(3 x sigma) px on each side, the padding filled by stretching the edge rows and columns, so the blur does not fade at
/// the borders; the padding is cropped off afterwards. Just blur: the picture's own colours, nothing added.
/// </summary>
public static class Frost
{
    /// The size the still is scaled to before the blur: 512 px wide, the height by aspect (rounded, at least 1).
    public static (int W, int H) BaseSize(int iw, int ih)
    {
        if (iw <= 0 || ih <= 0) return (0, 0);
        int w = LevitateTokens.FrostWidth;
        return (w, Math.Max(1, (int)Math.Round(ih * (double)w / iw, MidpointRounding.AwayFromZero)));
    }

    /// The padding (and the kernel's radius) for a blur of sigma px: ceil(3 x sigma).
    public static int Pad(double sigma) => sigma > 0 ? (int)Math.Ceiling(3 * sigma) : 0;

    /// The normalised 1-D Gaussian of sigma px over -Pad..+Pad: exp(-k^2 / (2 sigma^2)) / sum.
    public static float[] Kernel(double sigma)
    {
        int r = Pad(sigma);
        var k = new double[2 * r + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++) { k[i + r] = r == 0 ? 1 : Math.Exp(-(i * (double)i) / (2 * sigma * sigma)); sum += k[i + r]; }
        var f = new float[k.Length];
        for (int i = 0; i < k.Length; i++) f[i] = (float)(k[i] / sum);
        return f;
    }

    /// The bitmap with <paramref name="pad"/> px more on each side, the padding filled by stretching the edge rows and columns
    /// (each padding pixel is the nearest edge pixel; the corners the corner pixel).
    public static FrostBitmap Padded(FrostBitmap src, int pad)
    {
        int w = src.W, h = src.H, pw = w + 2 * pad, ph = h + 2 * pad;
        var o = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++) {
            int sy = Math.Clamp(y - pad, 0, h - 1);
            for (int x = 0; x < pw; x++) {
                int sx = Math.Clamp(x - pad, 0, w - 1);
                int s = (sy * w + sx) * 4, d = (y * pw + x) * 4;
                o[d] = src.Bgra[s]; o[d + 1] = src.Bgra[s + 1]; o[d + 2] = src.Bgra[s + 2]; o[d + 3] = src.Bgra[s + 3];
            }
        }
        return new FrostBitmap(o, pw, ph);
    }

    /// One Gaussian copy of <paramref name="src"/> at sigma px: pad (stretched edges), blur across then down (separable),
    /// crop the padding off. Every channel alike (the still is opaque); each result rounded half up to a byte.
    public static FrostBitmap Blur(FrostBitmap src, double sigma)
    {
        if (src.W <= 0 || src.H <= 0 || src.Bgra.Length < src.W * src.H * 4) throw new ArgumentException($"not a {src.W}x{src.H} BGRA bitmap", nameof(src));
        if (!(sigma > 0)) return new FrostBitmap((byte[])src.Bgra.Clone(), src.W, src.H);
        int p = Pad(sigma), w = src.W, h = src.H;
        var k = Kernel(sigma);
        var pad = Padded(src, p);
        int pw = pad.W, ph = pad.H;
        // across: every padded row, only the columns kept after the crop
        var tmp = new float[ph * w * 4];
        for (int y = 0; y < ph; y++) {
            int row = y * pw;
            for (int x = 0; x < w; x++) {
                float b = 0, g = 0, r = 0, a = 0;
                for (int i = 0; i < k.Length; i++) {
                    int s = (row + x + i) * 4;   // padded column (x + p) + (i - p)
                    float kw = k[i];
                    b += kw * pad.Bgra[s]; g += kw * pad.Bgra[s + 1]; r += kw * pad.Bgra[s + 2]; a += kw * pad.Bgra[s + 3];
                }
                int d = (y * w + x) * 4;
                tmp[d] = b; tmp[d + 1] = g; tmp[d + 2] = r; tmp[d + 3] = a;
            }
        }
        // down: only the rows kept after the crop
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
                float b = 0, g = 0, r = 0, a = 0;
                for (int i = 0; i < k.Length; i++) {
                    int s = ((y + i) * w + x) * 4;   // padded row (y + p) + (i - p)
                    float kw = k[i];
                    b += kw * tmp[s]; g += kw * tmp[s + 1]; r += kw * tmp[s + 2]; a += kw * tmp[s + 3];
                }
                int d = (y * w + x) * 4;
                o[d] = Byte(b); o[d + 1] = Byte(g); o[d + 2] = Byte(r); o[d + 3] = Byte(a);
            }
        }
        return new FrostBitmap(o, w, h);
    }

    static byte Byte(float v) => (byte)Math.Clamp((int)(v + 0.5f), 0, 255);

    /// Both copies of the scaled still: light (sigma <see cref="LevitateTokens.LightBlurPx"/>) and soft
    /// (sigma <see cref="LevitateTokens.SoftBlurPx"/>).
    public static (FrostBitmap Light, FrostBitmap Soft) Both(FrostBitmap scaled) =>
        (Blur(scaled, LevitateTokens.LightBlurPx), Blur(scaled, LevitateTokens.SoftBlurPx));
}
