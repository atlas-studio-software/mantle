namespace Mantle.Core.Look.Levitate;

/// <summary>One layer of a CSS <c>box-shadow</c> / <c>text-shadow</c>: offset, blur and spread in DIP, its ink (straight alpha 0-1).</summary>
public readonly record struct ShadowLayer(double Dx, double Dy, double Blur, double Spread, byte R, byte G, byte B, double A);

/// <summary>A colour token: r, g, b and a straight alpha 0-1 (Levitate.xaml writes it #AARRGGBB, a = round(a * 255)).</summary>
public readonly record struct Ink(byte R, byte G, byte B, double A);

/// <summary>
/// The levitated look's numbers (owner's decision 2026-11-24, EM ticket t_c268b8: Quartz's look is Mantle's look), from the
/// design file LOOK.md (look-sync design commit 97aa001), which replaced the first spec (the prototype quartz-home/app.js +
/// style.css). The C# mirror of Mantle.App/Theme/Levitate.xaml for the pure code (LevitateTokensTests holds the two equal).
/// Everything floats above the wallpaper with gaps; nothing but the picture touches the screen edges. Island edge: none (no
/// hairline, ring or top highlight); no tint film, no frost colour and no white sheen at rest; one neutral family, one
/// accent; no gradients, no glow at rest. Tokens.xaml (the other surfaces' tokens) is not changed by any of this.
/// </summary>
public static class LevitateTokens
{
    // ---- gaps (DIP)
    /// The air between an island and the window / screen edge, and between big panels.
    public const double Gap = 12;
    /// The air between islands in a row.
    public const double GapRow = 6;
    /// The content edge lines up with the chrome edge: the same 12.
    public const double ContentInset = 12;

    // ---- radii (DIP)
    /// Controls: small, medium, large.
    public const double RadiusCtlS = 4, RadiusCtlM = 6, RadiusCtlL = 8;
    /// Toolbar and tab islands.
    public const double IslandRadius = 12;
    /// Cards.
    public const double CardRadius = 16;
    /// Pop-overs.
    public const double PopoverRadius = 14;
    /// A floating page.
    public const double PageRadius = 14;
    /// The big chart panel.
    public const double ChartRadius = 20;
    /// Pills and round buttons: fully round.
    public const double PillRadius = 999;

    /// A shape nested in another: the outer radius less the gap between them (never below 0).
    public static double Nested(double outer, double gap) => outer - gap > 0 ? outer - gap : 0;

    // ---- spacing and sizes (DIP)
    /// The spacing grid.
    public static readonly double[] Spacing = { 4, 8, 12, 16, 24, 32 };
    /// A card's padding.
    public const double CardPadding = 16;
    /// Row heights: lists, menus, the tray.
    public const double RowList = 32, RowMenu = 36, RowTray = 40;
    /// Toolbar buttons (and small).
    public const double ToolbarButton = 32, ToolbarButtonSmall = 28;
    /// Round controls (small, main).
    public const double RoundControl = 36, RoundControlSmall = 28, RoundControlMain = 44;
    /// Pill buttons are this tall.
    public const double PillHeight = 28;

    // ---- colours (dark; one neutral family, one accent)
    public static readonly Ink Text = new(0xEE, 0xF6, 0xF7, 1);
    public static readonly Ink Text2 = new(230, 242, 244, .76);
    public static readonly Ink Text3 = new(230, 242, 244, .54);
    /// Disabled only.
    public static readonly Ink Text4 = new(230, 242, 244, .34);
    /// The focus ring, the active underline, the selected state.
    public static readonly Ink Accent = new(0xA9, 0xE7, 0xED, 1);
    /// Ink on the accent.
    public static readonly Ink AccentInk = new(0x05, 0x2A, 0x2F, 1);
    public static readonly Ink Ok = new(0x8F, 0xE3, 0xB4, 1);
    public static readonly Ink Down = new(0xFF, 0x9D, 0x95, 1);
    /// Dividers INSIDE an island only (never an island's edge): white at this alpha.
    public const double Hair = 0.09;

    // ---- control fills inside an island: white at these alphas (rest none)
    public const double CtlFillRest = 0.00, CtlFillHover = 0.07, CtlFillOn = 0.12, CtlFillStrong = 0.18, CtlFillPressed = 0.05;

    /// The white sheen an island shows on HOVER only (none at rest): white at this alpha, a big panel at the second.
    public const double SheenHover = 0.07, SheenHoverBig = 0.05;

    // ---- focus: one ring everywhere (a focused input island gets it round the island)
    public const double FocusRingWidth = 2, FocusRingOffset = 2;

    // ---- type (Mantle's own type SCALE is not set by LOOK.md: the existing native scale in Tokens.xaml stays)
    public const string FontText = "Segoe UI Variable Text", FontDisplay = "Segoe UI Variable Display";
    /// The weights in use; 300 only on a hero clock. Every number that changes uses tabular figures.
    public const int WeightRegular = 400, WeightMedium = 500, WeightSemibold = 600, WeightHeroClock = 300;

    // ---- the frost: a two-level PRE-blur of the wallpaper still (once per picture or window / monitor size change, never live)
    /// The still is scaled to this width (height by aspect) before it is blurred.
    public const int FrostWidth = 512;
    /// Gaussian sigma in bitmap px: light (about 16 px on screen) for small chrome, soft (about 40 px) for big panels; the
    /// copy is padded by stretching its edge pixels 3 x the radius.
    public const double LightBlurPx = 6, SoftBlurPx = 15;
    /// A panel is BIG when min(w, h) &gt;= BigMinSide AND w * h &gt;= BigMinArea (its LAYOUT size in DIP, not the animated rect).
    public const double BigMinSide = 48, BigMinArea = 20000;

    // ---- the dim and the legibility film
    /// The uniform black wash over the blurred copy: the default, and the range a saved value is clamped to (nothing below
    /// 0.20 is written: the owner's decision).
    public const double DimDefault = 0.20, DimMin = 0.20, DimMax = 0.90;
    /// The neutral film the brightness check lays over a bright panel: rgb(10, 12, 14) at the check's alpha.
    public const byte FilmR = 10, FilmG = 12, FilmB = 14;
    /// The film never goes above this; it grows in these steps (rounded to 2 decimals) from 0 until the panel's bright
    /// sample is at or below the target luminance.
    public const double FilmCap = 0.56, FilmStep = 0.04, FilmTarget = 0.163;

    /// Until the blurred bitmap is ready (or when it fails): this flat colour, #2c5258 (still legible).
    public const byte FallbackR = 0x2c, FallbackG = 0x52, FallbackB = 0x58;

    // ---- soft shadows only, tinted rgba(0,12,16,a)
    /// float (every island) = 0 1px 1px .14, 0 8px 18px -6px .32, 0 30px 56px -26px .52
    public static readonly ShadowLayer[] Float =
    {
        new(0, 1, 1, 0, 0, 12, 16, .14),
        new(0, 8, 18, -6, 0, 12, 16, .32),
        new(0, 30, 56, -26, 0, 12, 16, .52),
    };

    /// float-hi (hover, pop-overs, lifted) = 0 2px 2px .12, 0 14px 26px -8px .38, 0 44px 72px -28px .60
    public static readonly ShadowLayer[] FloatHi =
    {
        new(0, 2, 2, 0, 0, 12, 16, .12),
        new(0, 14, 26, -8, 0, 12, 16, .38),
        new(0, 44, 72, -28, 0, 12, 16, .60),
    };

    /// sheet (a page / content sheet) = 0 2px 4px .18, 0 24px 48px -12px .45, 0 60px 100px -40px .60
    public static readonly ShadowLayer[] Sheet =
    {
        new(0, 2, 4, 0, 0, 12, 16, .18),
        new(0, 24, 48, -12, 0, 12, 16, .45),
        new(0, 60, 100, -40, 0, 12, 16, .60),
    };

    /// Text on an island (a text-shadow) = 0 1px 2px rgba(0,0,0,.22)
    public static readonly ShadowLayer[] IslandText =
    {
        new(0, 1, 2, 0, 0, 0, 0, .22),
    };

    /// Bare text on the picture (a text-shadow) = 0 1px 2px rgba(0,14,18,.35), 0 2px 18px rgba(0,14,18,.28)
    public static readonly ShadowLayer[] BareText =
    {
        new(0, 1, 2, 0, 0, 14, 18, .35),
        new(0, 2, 18, 0, 0, 14, 18, .28),
    };

    // ---- motion (Motion.cs uses these): transform and opacity only, never width or blur
    /// ease = cubic-bezier(0.2, 0.8, 0.2, 1); ease-lift = cubic-bezier(0.22, 1.12, 0.36, 1) (a hair of overshoot);
    /// the morph's cubic-bezier(0.22, 1, 0.36, 1).
    public static readonly Bezier Ease = new(0.2, 0.8, 0.2, 1), EaseLift = new(0.22, 1.12, 0.36, 1), EaseMorph = new(0.22, 1, 0.36, 1);
    /// Hover / press (--dur) and the first spec's --dur-slow (kept; LOOK.md's list does not name it).
    public const double DurMs = 160, DurSlowMs = 340;
    /// Press scales: small buttons, pills, big tiles.
    public const double PressScaleSmall = 0.94, PressScalePill = 0.97, PressScaleTile = 0.985;
}
