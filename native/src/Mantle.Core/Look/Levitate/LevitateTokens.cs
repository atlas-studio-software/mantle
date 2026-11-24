namespace Mantle.Core.Look.Levitate;

/// <summary>One layer of a CSS <c>box-shadow</c> / <c>text-shadow</c>: offset, blur and spread in DIP, its ink (straight alpha 0-1).</summary>
public readonly record struct ShadowLayer(double Dx, double Dy, double Blur, double Spread, byte R, byte G, byte B, double A);

/// <summary>
/// The levitated look's numbers (owner's decision 2026-11-24, EM ticket t_c268b8: Quartz's look is Mantle's look), taken from
/// the design prototype quartz-home/app.js + style.css (2026-11-24). The C# mirror of Mantle.App/Theme/Levitate.xaml for the
/// pure code (LevitateTokensTests holds the two equal). Everything floats above the wallpaper with gaps; nothing but the
/// picture touches the screen edges.
/// </summary>
public static class LevitateTokens
{
    // ---- islands and gaps (DIP)
    /// The air between an island and the window / screen edge.
    public const double Gap = 8;
    /// An island's corner radius.
    public const double IslandRadius = 12;
    /// A card's corner radius (the prototype's --r-l).
    public const double CardRadius = 16;
    /// The prototype's --r-m.
    public const double RadiusM = 10;
    /// A card's padding.
    public const double CardPadding = 16;

    // ---- control fills inside a frosted panel: white at these alphas
    public const double CtlFillRest = 0.00, CtlFillHover = 0.07, CtlFillOn = 0.12, CtlFillStrong = 0.18, CtlFillPressed = 0.05;

    // ---- the frost: a two-level PRE-blur of the wallpaper still (once per wallpaper change, never live)
    /// The still is scaled to this width (height by aspect) before it is blurred.
    public const int FrostWidth = 512;
    /// Gaussian sigma in bitmap px: light (about 16 px on screen) for small chrome, soft (about 40 px) for big panels.
    public const double LightBlurPx = 6, SoftBlurPx = 15;
    /// A panel is BIG when min(w, h) &gt;= BigMinSide AND w * h &gt;= BigMinArea (layout size in DIP).
    public const double BigMinSide = 48, BigMinArea = 20000;

    // ---- the dim and the legibility film
    /// The uniform black wash over the blurred copy: the default, and the contract range a saved value is clamped to.
    public const double DimDefault = 0.20, DimMin = 0.20, DimMax = 0.90;
    /// The neutral film the brightness check lays over a bright panel: rgb(10, 12, 14) at the check's alpha.
    public const byte FilmR = 10, FilmG = 12, FilmB = 14;
    /// The film never goes above this; it grows in these steps from 0 until the panel's bright sample is dark enough.
    public const double FilmCap = 0.56, FilmStep = 0.04, FilmTarget = 0.163;

    /// Until the blurred bitmap is ready (or when it fails): this flat colour, #2c5258 (still legible).
    public const byte FallbackR = 0x2c, FallbackG = 0x52, FallbackB = 0x58;

    // ---- soft shadows only (no tint film, no white sheen, no hairline edge)
    /// float = 0 1px 1px rgba(0,12,16,.14), 0 8px 18px -6px rgba(0,12,16,.32), 0 30px 56px -26px rgba(0,12,16,.52)
    public static readonly ShadowLayer[] Float =
    {
        new(0, 1, 1, 0, 0, 12, 16, .14),
        new(0, 8, 18, -6, 0, 12, 16, .32),
        new(0, 30, 56, -26, 0, 12, 16, .52),
    };

    /// float-hi = 0 2px 2px rgba(0,12,16,.12), 0 14px 26px -8px rgba(0,12,16,.38), 0 44px 72px -28px rgba(0,12,16,.6)
    public static readonly ShadowLayer[] FloatHi =
    {
        new(0, 2, 2, 0, 0, 12, 16, .12),
        new(0, 14, 26, -8, 0, 12, 16, .38),
        new(0, 44, 72, -28, 0, 12, 16, .60),
    };

    /// text-lift (a text-shadow) = 0 1px 2px rgba(0,14,18,.35), 0 2px 18px rgba(0,14,18,.28)
    public static readonly ShadowLayer[] TextLift =
    {
        new(0, 1, 2, 0, 0, 14, 18, .35),
        new(0, 2, 18, 0, 0, 14, 18, .28),
    };

    // ---- motion (Motion.cs uses these)
    /// ease = cubic-bezier(0.2, 0.8, 0.2, 1); ease-lift = cubic-bezier(0.22, 1.12, 0.36, 1) (a hair of overshoot).
    public static readonly Bezier Ease = new(0.2, 0.8, 0.2, 1), EaseLift = new(0.22, 1.12, 0.36, 1);
    /// --dur and --dur-slow.
    public const double DurMs = 160, DurSlowMs = 340;
}
