using System;

namespace Mantle.Core.Features.Downloads;

/// <summary>
/// The shelf's keyboard walk and its one-press-per-row guard (ui/bar/downloads.js onKey / act), pure.
/// </summary>
public static class DownloadsKeys
{
    /// <summary>A second press on the same row this soon after the first is the same decision (a double click).</summary>
    public const long GapMs = 500;

    /// <summary>
    /// Up / Down / Home / End over <paramref name="count"/> rows from row <paramref name="at"/> (-1: none has the focus):
    /// the first Down is the newest, the first Up the oldest; clamped, never wraps. Null for any other key or no rows.
    /// </summary>
    public static int? Walk(string key, int at, int count)
    {
        if (count <= 0) return null;
        return key switch
        {
            "Home" => 0,
            "End" => count - 1,
            "Down" => at < 0 ? 0 : Math.Min(count - 1, at + 1),
            "Up" => at < 0 ? count - 1 : Math.Max(0, at - 1),
            _ => null,
        };
    }

    /// <summary>act(): a press runs unless the row's last one is still on its way, or came within <see cref="GapMs"/>.</summary>
    public static bool Takes(bool inFlight, string? lastRow, long lastAt, string row, long now) =>
        !inFlight && !(lastRow == row && now - lastAt < GapMs);
}
