using System;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Corners;

/// <summary>
/// A corner set to Quick settings (CORN-053/054, lib/features/corners.js H.qs): the bar toggles its panel and answers;
/// no answer within <see cref="AckMs"/> is said in words, and a bar's own reason is clipped to one line of at most
/// <see cref="ReasonMax"/> characters.
/// </summary>
public static class CornerQs
{
    public const int AckMs = 1500, ReasonMax = 160;

    /// The bar's answer, or "the bar did not answer within 1500 ms" when it did not come in time.
    public static async Task<CornerFx> Within(Task<CornerFx> answer, int ms, bool dry)
    {
        var first = await Task.WhenAny(answer, Task.Delay(ms)).ConfigureAwait(false);
        if (first != answer) return new CornerFx(false, $"the bar did not answer within {ms} ms", dry);
        var r = await answer.ConfigureAwait(false);
        return r.Ok ? r : r with { Reason = Clip(r.Reason) };
    }

    /// One line, at most 160 characters (an ellipsis when cut).
    public static string Clip(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "no reason came back";
        var one = string.Join(" ", reason.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length <= ReasonMax ? one : one[..(ReasonMax - 1)] + "…";
    }
}
