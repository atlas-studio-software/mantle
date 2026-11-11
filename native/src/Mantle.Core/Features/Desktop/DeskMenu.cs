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
    // ---- MENU KEYS (DESK-118..120): the menu never takes the keyboard, so a low-level hook armed only while it is open
    // hands it the bare Up / Down / Home / End / Enter (sidecar/Hotkey.cs PopWatch NavVerdict, nav:true) ----

    public const int VkReturn = 0x0D, VkEnd = 0x23, VkHome = 0x24, VkUp = 0x26, VkDown = 0x28;
    public const int NavSwallow = 1, NavReport = 2, NavHeld = 4;

    /// The menu key a virtual-key code is ("up", "down", "home", "end", "enter"), or null.
    public static string? NavKey(int vk) => vk switch {
        VkUp => "up", VkDown => "down", VkHome => "home", VkEnd => "end", VkReturn => "enter", _ => null,
    };

    /// What the keyboard hook does with one key while the menu is open: NavSwallow | NavReport | NavHeld bits.
    /// Not a menu key: 0. A key up: swallowed when its down was (held, not passive). A down with Ctrl/Alt/Shift/Win, or
    /// with the bar in front, or no menu open: passed, nothing reported. A down: reported (Enter on its first down only,
    /// the arrows on every autorepeat so a held arrow walks the menu), swallowed unless passive, held set.
    public static int NavVerdict(int vk, bool down, bool anyMod, bool fgIsBar, bool passive, bool nav, bool held)
    {
        if (NavKey(vk) == null) return 0;
        if (!down) return held && !passive ? NavSwallow : 0;
        if (!nav || anyMod || fgIsBar) return 0;
        int r = NavHeld;
        if (vk != VkReturn || !held) r |= NavReport;
        if (!passive) r |= NavSwallow;
        return r;
    }

    /// Enter: the highlighted row runs; with nothing highlighted the menu closes and runs nothing (DESK-119).
    public static string EnterVerdict(int sel, int n) => sel >= 0 && sel < n ? "run" : "close";

    // ---- NEVER SILENT (DESK-124): the helper gives the click back to Windows when Mantle does not ack in 1 s ----

    public const int AckMs = 1000, StandDownRetryMs = 30000;

    /// The helper's fallback event in words: the log line, and the health row when it stood down (null otherwise).
    public static (string Line, string? Health) Fallback(string? why, bool replayed, bool standDown, string? standWhy)
    {
        string line = $"{(string.IsNullOrWhiteSpace(why) ? "the helper gave a right-click to Windows" : why)}; {(replayed ? "Windows' own menu opened instead" : "the click was not given back to Windows")}";
        return (line, standDown ? (string.IsNullOrWhiteSpace(standWhy) ? "right-clicks on the desktop go to Windows for now" : standWhy) : null);
    }

    public static string Failed(string label, string? reason) => $"{label} did not work: {(string.IsNullOrEmpty(reason) ? "no reason was given" : reason)}";
}
