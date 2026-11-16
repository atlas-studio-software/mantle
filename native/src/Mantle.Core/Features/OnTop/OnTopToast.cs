using System.Text.Json;

namespace Mantle.Core.Features.OnTop;

/// <summary>
/// The keep-on-top result pop-up's rules (lib/features/ontop.js toastWin / toast / hideToast, ui/ontop/ontop.js show;
/// BAR-341): a 396 x 80 DIP window (the 360 x 44 pill and 18 DIP of margin each side) at the volume pop-up's place;
/// the pane fades after 1.8 s (3.4 s for a refusal with its reason, so it can be read), the window is hidden by a
/// backstop at 4.3 s whatever the pane did, and it is destroyed after 30 s unused. The surface is Mantle.App's
/// Surfaces/OnTop/OnTopToastSurface.cs.
/// </summary>
public static class OnTopToast
{
    public const double Margin = 18, PaneW = 360, PaneH = 44, WindowW = PaneW + 2 * Margin, WindowH = PaneH + 2 * Margin;
    public const int HoldMs = 1800, WarnHoldMs = HoldMs + 1600, FadeMs = 260, BackstopMs = HoldMs + 2500, IdleMs = 30000;

    /// One result as the pop-up draws it.
    public sealed record Shown(string Text, string Line, bool LineIsHint, string Tone, string Did, int HoldMs);

    /// ui/ontop/ontop.js show(): the second line is the reason, else the hint (in the quieter ink); a warning with a
    /// reason is held longer. Null when the payload has no text (nothing to say).
    public static Shown? Read(JsonElement p)
    {
        static string S(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        string text = S(p, "text");
        if (text.Length == 0) return null;
        string reason = S(p, "reason"), hint = S(p, "hint"), tone = S(p, "tone");
        return new Shown(text, reason.Length > 0 ? reason : hint, reason.Length == 0 && hint.Length > 0, tone, S(p, "did"),
            reason.Length > 0 && tone == "warn" ? WarnHoldMs : HoldMs);
    }
}
