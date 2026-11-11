namespace Mantle.Core.Features.JumpList;

/// <summary>
/// How the app menu's rows look in each theme (BAR-329; ui/bar/jumplist.css). Light is the bar's own menu
/// (.pop.jl .jl-i: min 28 high, padding 4 8, hover surface, a 1.5 accent ring on keyboard focus); dark is Helm's menu
/// (HELM-SPEC rows 3-24: padding 4, rows min 30 at 5 / 10 with a 10 gap in ink, icons in ink-2, hover and keyboard focus = the press
/// surface with no accent, keyboard focus adds a 1 px hairline; dividers in the divider colour, 6 in from the sides). Colours are theme
/// token names, never literals. Rows change state at once in both themes: there is no row transition, so reduced
/// motion has nothing to drop.
/// </summary>
public sealed record JumpLook(
    double PadX, double PadY, double MinH, double Gap, string Ink, string Hover, string FocusFill, string FocusRing, double RingW,
    double BodyPad, string Divider, double SepX, string IconInk)
{
    public static readonly JumpLook Light = new(8, 4, 28, 9, "LgInk2", "LgHover", "LgHover", "LgAcc", 1.5, 6, "LgEdgeOut", 8, "LgInk3");
    public static readonly JumpLook Dark = new(10, 5, 30, 10, "LgInk", "LgPress", "LgPress", "LgEdgeOut", 1, 4, "LgDivider", 6, "LgInk2");

    public static JumpLook For(bool dark) => dark ? Dark : Light;

    /// A row's ink: the plain rows take the theme's (ink-2 in light, ink in dark); a row with its own ink keeps it.
    public string RowInk(string ink) => ink == Light.Ink ? Ink : ink;

    /// What a --test run logs when a menu is built, so a test can tell which look it got.
    public string Describe(bool dark) => System.FormattableString.Invariant(
        $"{(dark ? "dark (Helm's menu)" : "light")}: rows min {MinH:0} at {PadY:0} / {PadX:0}, gap {Gap:0}, hover {Hover}, keyboard focus {FocusFill} with a {RingW:0.#} px {FocusRing} ring");
}
