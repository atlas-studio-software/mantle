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
}
