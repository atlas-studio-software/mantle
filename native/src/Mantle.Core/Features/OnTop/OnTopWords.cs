namespace Mantle.Core.Features.OnTop;

/// <summary>
/// The bar's keep-on-top words (ui/bar/ontop.js paint, CANT; BAR-258, BAR-333, BAR-334): the line an app's tooltip gets,
/// and what the tooltip and the menu say when the bar cannot know which windows are kept on top.
/// </summary>
public static class OnTopWords
{
    /// ui/bar/ontop.js CANT: no pin state can reach the bar (no event channel, or the state never answered).
    public const string Cant = "the bar can't show which windows are kept on top right now (restart Mantle to fix it)";
    public const string KeptLine = "Kept on top · right-click or Win+Ctrl+T to stop";

    /// The app tooltip's keep-on-top line: kept on top says how to stop; with no pin state, an app with windows says
    /// why the bar cannot tell (never silent); otherwise nothing.
    public static string TipLine(bool keptOnTop, bool hasWindows, string? cant) =>
        keptOnTop ? "\n" + KeptLine : cant != null && hasWindows ? "\nKeep on top: " + cant : "";

    /// The menu's "Keep on top" row tooltip when the bar cannot know the state.
    public static string MenuCant(string cant) => "Keep on top: " + cant;

    /// ui/bar/helmbadge.css <c>.cell.hb-on .ontop-badge { right: auto; left: 3px; }</c>: an app's count disc owns the
    /// icon's top-right corner, so while a count shows the keep-on-top pin moves to the top-left (both stay readable).
    public static bool PinOnLeft(int count) => count > 0;

    /// The pin's offset from the icon's corner (DIP, left, top, right, bottom): mirrored when it moves left.
    public static (double L, double T, double R, double B) PinMargin(int count) => PinOnLeft(count) ? (-9, -3, 0, 0) : (0, -3, -9, 0);
}
