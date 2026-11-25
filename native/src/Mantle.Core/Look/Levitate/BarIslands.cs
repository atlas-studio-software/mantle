using System;

namespace Mantle.Core.Look.Levitate;

/// <summary>A box in DIP: in band space for an island (x from the band's left, y from its top), or relative to the
/// monitor's top-left for the band itself.</summary>
public readonly record struct IslandRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>Where the three islands sit in the band, and whether the dock dropped to the left (shell.js fitDock).</summary>
public readonly record struct BarIslandsLayout(IslandRect Left, IslandRect Dock, IslandRect Right, bool DockLeft);

/// <summary>What the network button shows: offline or not, and, while offline, its accessible name and its tooltip.
/// While connected both words are empty (the button keeps its own).</summary>
public readonly record struct NetOffline(bool Offline, string Label, string Tip);

/// <summary>
/// The bar as three floating islands (lane r13-bar-islands), pure so it is tested here. The numbers come from the bar
/// prototype apps-r3/mantle-bar (look.css "the bar: three islands", shell.js fitDock and ?net=off) and
/// <see cref="LevitateTokens"/>.
/// <list type="bullet">
/// <item>Three islands along the bottom, in this order: left (the M), dock, right (the status cluster). Each is
/// <see cref="BarH"/> (44) tall, radius <see cref="Radius"/> (12), the band <see cref="Gap"/> (12) from the screen's left,
/// right and bottom edges (bar zone 68).</item>
/// <item>Left: 44 x 44 around a 36 start button with a 22 mark. Dock: padding 0 4, gap 2, cells 40 x 36 at radius 8, 20
/// icons, run dot 4 x 3 two up, focus underline 16 x 3 (320 ms ease-lift), separator 1 x 20 margin 0 4. Right: padding 0 4,
/// gap 2, glyphs 32 x 36 at radius 8, 16 icons, separator 1 x 20 margin 0 4, clock padding 0 8 (time 12 / 500, date 11).</item>
/// <item>The dock is centred on the screen; when its right edge + 12 would pass the right island's left edge, it sits 12
/// after the M instead (x = 44 + 12 in band space).</item>
/// <item>Offline (no network the system knows of): a globe with a slash at .55, "Not connected" and the Wi-Fi detail
/// (Electron ui/bar/net.js).</item>
/// </list>
/// </summary>
public static class BarIslands
{
    /// The islands, in order left to right.
    public static readonly string[] Order = { "left", "dock", "right" };

    /// Each island's height (--bar-h), its radius, and the air to the screen edges (--gap).
    public const double BarH = 44, Radius = LevitateTokens.IslandRadius, Gap = LevitateTokens.Gap;
    /// The strip the bar keeps clear at the bottom: 44 + 2 x 12.
    public const double Zone = BarH + 2 * Gap;

    // ---- the left island
    public const double LeftW = BarH, StartSize = 36, StartMark = 22;

    // ---- the dock island
    public const double DockPadX = 4, DockGap = 2;
    public const double CellW = 40, CellH = 36, CellRadius = LevitateTokens.RadiusCtlL, CellIcon = 20;
    public const double RunW = 4, RunH = 3, RunBottom = 2;
    public const double FocusW = 16, FocusH = 3, FocusMs = 320;
    public const double SepW = 1, SepH = 20, SepMarginX = 4;

    // ---- the right island
    public const double RightPadX = 4, RightGap = 2;
    public const double GlyphW = 32, GlyphH = 36, GlyphRadius = LevitateTokens.RadiusCtlL, GlyphIcon = 16;
    public const double ClockPadX = 8, ClockTime = 12, ClockDate = 11;
    public const int ClockTimeWeight = LevitateTokens.WeightMedium;

    /// The control fill on hover and pressed; nothing at rest.
    public const double HoverFill = LevitateTokens.CtlFillHover, PressedFill = LevitateTokens.CtlFillPressed;

    /// The offline glyph's opacity.
    public const double OfflineOpacity = 0.55;

    /// The band for a monitor this size (DIP), relative to its top-left: the screen less 12 each side, 44 tall, 12 above
    /// the bottom. Never narrower than 0.
    public static IslandRect Band(double screenWdip, double screenHdip) =>
        new(Gap, screenHdip - Gap - BarH, Math.Max(0, screenWdip - 2 * Gap), BarH);

    /// The band in physical px for a monitor rect (px) at this scale (DPI / 96): left / right 12 DIP in, bottom 12 DIP up,
    /// 44 DIP tall, each edge rounded on its own.
    public static (int Left, int Top, int Right, int Bottom) BandPx(int monLeft, int monTop, int monRight, int monBottom, double scale)
    {
        int g = (int)Math.Round(Gap * scale), h = (int)Math.Round(BarH * scale);
        int left = monLeft + g, right = Math.Max(left, monRight - g), bottom = monBottom - g;
        return (left, bottom - h, right, bottom);
    }

    /// The widest the dock island may be with the right island this wide (DIP, its content): from 12 after the M to 12
    /// before the right island.
    public static double DockRoom(double screenWdip, double rightContentW)
    {
        double bandW = Math.Max(0, screenWdip - 2 * Gap), rightW = rightContentW + 2 * RightPadX;
        return Math.Max(0, bandW - rightW - Gap - (LeftW + Gap));
    }

    /// The three islands in band space for a screen this wide (DIP) and dock / right content this wide (DIP, without the
    /// islands' own 4 padding each side).
    public static BarIslandsLayout Layout(double screenWdip, double dockContentW, double rightContentW)
    {
        double bandW = Math.Max(0, screenWdip - 2 * Gap);
        double dockW = Math.Max(0, dockContentW) + 2 * DockPadX, rightW = Math.Max(0, rightContentW) + 2 * RightPadX;
        var left = new IslandRect(0, 0, LeftW, BarH);
        var right = new IslandRect(bandW - rightW, 0, rightW, BarH);
        double x = screenWdip / 2 - Gap - dockW / 2;   // centred on the screen, in band space
        bool dropped = x + dockW + Gap > right.X;
        if (dropped) x = LeftW + Gap;
        return new BarIslandsLayout(left, new IslandRect(x, 0, dockW, BarH), right, dropped);
    }

    /// The network button's state. Offline when the system knows no network and the Wi-Fi part does not say it is
    /// connected. The detail: Wi-Fi off -&gt; "Wi-Fi is turned off"; Wi-Fi on -&gt; "Wi-Fi is on but not connected to a
    /// network"; no Wi-Fi known -&gt; "No connections are available" (net.js 'wifi-off' / 'disconnected').
    public static NetOffline Offline(bool anyNetwork, bool? wifiOn, bool? wifiConnected)
    {
        if (anyNetwork || wifiConnected == true) return new NetOffline(false, "", "");
        string detail = wifiOn == false ? "Wi-Fi is turned off"
            : wifiOn == true ? "Wi-Fi is on but not connected to a network"
            : "No connections are available";
        return new NetOffline(true, "Not connected, " + detail, "Not connected\n" + detail + "\nClick: quick settings");
    }
}
