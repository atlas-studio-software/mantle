using System;
using System.Collections.Generic;

namespace Mantle.Core.Features.Desktop;

/// <summary>One row of the desktop menu (null id = a separator).</summary>
public sealed record DeskMenuRow(string? Id, string Label, string Glyph, string? Command, string? Section)
{
    public bool Sep => Id == null;
}

/// <summary>A whole-pixel rect (physical or DIP, as the caller uses it).</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// The desktop right-click menu, pure (ui/desktop/deskmenu.js rows / step, lib/features/deskmenu.js place).
/// </summary>
public static class DeskMenu
{
    /// The panel's clear margin (its shadow) round the panel inside the menu window.
    public const int Inset = 20;
    public const int RowHeight = 32, Padding = 5, MinWidth = 224, MaxWidth = 320;
    public const string Via = "the desktop menu";

    /// The rows in order: Change wallpaper, Display settings, Personalize, |, Hide/Show desktop widgets, |, Mantle Settings.
    public static IReadOnlyList<DeskMenuRow> Rows(bool widgetsOn) => new DeskMenuRow[] {
        new("wallpaper", "Change wallpaper", "", "wallpaper.open", null),
        new("display", "Display settings", "", "settings.open", "display"),
        new("personalize", "Personalize", "", "settings.open", "personalization"),
        new(null, "", "", null, null),
        new("widgets", widgetsOn ? "Hide desktop widgets" : "Show desktop widgets", "", null, null),
        new(null, "", "", null, null),
        new("settings", "Mantle Settings", "", "settings.open", null),
    };

    /// Up / Down / Home / End over <paramref name="n"/> rows (separators are not counted): Down from none = first,
    /// Up from none = last, both wrap. Any other key keeps the highlight.
    public static int Step(int sel, int n, string key)
    {
        if (!(n > 0)) return -1;
        return key switch {
            "home" => 0,
            "end" => n - 1,
            "down" => sel < 0 ? 0 : (sel + 1) % n,
            "up" => sel < 0 ? n - 1 : (sel - 1 + n) % n,
            _ => sel,
        };
    }

    /// Windows' rule: the PANEL's corner on the cursor, right of and below it; flipped left / up when it would leave the
    /// work area of the display under the click, and never outside it. <paramref name="area"/> null = placed as asked.
    public static PxRect Place(int ptX, int ptY, double w, double h, int inset, PxRect? area)
    {
        int W = (int)Math.Ceiling(w), H = (int)Math.Ceiling(h);
        double x = ptX - inset, y = ptY - inset;
        if (area is { } a) {
            if (x + W > a.Right) x = ptX - W + inset;
            if (y + H > a.Bottom) y = ptY - H + inset;
            x = Math.Min(Math.Max(a.X, x), a.Right - W);
            y = Math.Min(Math.Max(a.Y, y), a.Bottom - H);
        }
        return new PxRect((int)Math.Round(x, MidpointRounding.AwayFromZero), (int)Math.Round(y, MidpointRounding.AwayFromZero), W, H);
    }

    /// The words a failed row keeps on the menu.
    public static string Failed(string label, string? reason) => $"{label} did not work: {(string.IsNullOrEmpty(reason) ? "no reason was given" : reason)}";
}
