using System;
using System.Globalization;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// The calendar colours are DATA (Google's #rrggbb per calendar), not theme colours: parsed here, and the agenda title's
/// gcal.css <c>color-mix(in oklab, var(--gc) 60%, var(--lg-ink))</c> done in OKLab (Björn Ottosson's matrices), so a pale
/// Google colour still reads on light glass and a dark one on dark glass.
/// </summary>
public static class GcalColour
{
    public readonly record struct Rgba(byte R, byte G, byte B, double A);

    /// "#rrggbb" -> the colour; null for anything else.
    public static Rgba? Parse(string? hex)
    {
        if (hex == null || hex.Length != 7 || hex[0] != '#') return null;
        if (!int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return null;
        return new Rgba((byte)(v >> 16), (byte)((v >> 8) & 0xff), (byte)(v & 0xff), 1);
    }

    /// color-mix(in oklab, a p, b): p is a's share (0.6 for the agenda title). Alpha is mixed linearly and the colours
    /// premultiplied by it, as CSS Color 5 says.
    public static Rgba MixOklab(Rgba a, Rgba b, double p)
    {
        var (l1, a1, b1) = ToOklab(a); var (l2, a2, b2) = ToOklab(b);
        double al = a.A * p + b.A * (1 - p);
        if (al <= 0) return new Rgba(0, 0, 0, 0);
        double L = (l1 * a.A * p + l2 * b.A * (1 - p)) / al, A = (a1 * a.A * p + a2 * b.A * (1 - p)) / al, B = (b1 * a.A * p + b2 * b.A * (1 - p)) / al;
        return FromOklab(L, A, B, al);
    }

    static double Lin(byte c) { double x = c / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
    static byte Gam(double x)
    {
        x = Math.Clamp(x, 0, 1);
        double v = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }

    static (double L, double A, double B) ToOklab(Rgba c)
    {
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static Rgba FromOklab(double L, double A, double B, double alpha)
    {
        double l = L + 0.3963377774 * A + 0.2158037573 * B, m = L - 0.1055613458 * A - 0.0638541728 * B, s = L - 0.0894841775 * A - 1.2914855480 * B;
        l = l * l * l; m = m * m * m; s = s * s * s;
        return new Rgba(Gam(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s),
                        Gam(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s),
                        Gam(-0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s), alpha);
    }
}
