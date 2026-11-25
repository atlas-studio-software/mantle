using System;

namespace Mantle.Core.Look.Levitate;

/// <summary>Which pre-blurred copy a panel shows: light (sigma 6) for small chrome, soft (sigma 15) for a big panel.</summary>
public enum FrostKind { Light, Soft }

/// <summary>A pop-up's box in DIP, relative to its monitor's top-left.</summary>
public readonly record struct PopupBox(double X, double Y, double W, double H)
{
    public double Bottom => Y + H;
}

/// <summary>Where a levitated pop-up is in its motion (<see cref="PopupLift"/>).</summary>
public enum PopupPhase { Hidden, Opening, Shown, Closing }

/// <summary>
/// The decisions the bar's pop-up host (Mantle.App Shell/Popup/PopupHost + PopupWindow) takes for every pop-up that wears
/// the levitated look (lane r12-levitate-host), pure so they are tested here: which frost it shows, its dim and film, where
/// it sits over the bar, how tall it may grow, and its motion. The look comes from the bar prototype apps-r3/mantle-bar
/// (look.css, panels/downloads.css) and LOOK.md; the numbers from <see cref="LevitateTokens"/> and look-sync's FROST / LIFT
/// (look-sync dca42dc: unchanged from 5d9e4f6, which the v2.1 vectors pin).
/// <list type="bullet">
/// <item>Frost: soft when the panel is big (short side &gt;= 48 AND area &gt;= 20000 DIP, its layout size), else light;
/// a size that is not a finite number &gt;= 0 is light (look-sync frostKind).</item>
/// <item>Dim: the saved value clamped to 0.20..0.90, default 0.20 (<see cref="LevitateDim.Resolve"/>).</item>
/// <item>Film: the v2.1 dark brightness check (<see cref="Legibility.Film(Rgb[], double, string)"/>) in DARK with the check
/// on; 0 in light (native has no light Levitate look yet) and 0 in manual.</item>
/// <item>Placement: centred on its anchor, its bottom <see cref="AboveBar"/> (12) above the bar, kept <see cref="EdgeGap"/>
/// (12) inside the monitor, at most the monitor height less <see cref="CapMargin"/> (80) tall (calc(100vh - 80px)).</item>
/// <item>Motion, transform and opacity only: a pop-up opening on its own pops in from translateY +6 and scale 0.97 about
/// its bottom centre (the bar's panels open upward: look.css .panel / .panel.up); one opening IN PLACE of another lifts in
/// (<see cref="Motion.LiftIn"/>, from translateY 28 and scale 0.965: the sink-and-lift switch; the replaced one is gone at
/// once, BAR-375); closing pops out the same way back. Reduced motion: no travel, quick fades only.</item>
/// <item>No hairline edge, no tint, no sheen at rest: the hover sheen is .07 (.05 on a big panel).</item>
/// </list>
/// </summary>
public static class PopupLevitate
{
    /// The pop-over radius (LOOK.md: pop-overs 14).
    public const double Radius = LevitateTokens.PopoverRadius;
    /// A panel opens this far above the bar.
    public const double AboveBar = LevitateTokens.GapAboveBar;
    /// The air kept between a panel and the monitor's left / right edge.
    public const double EdgeGap = LevitateTokens.Gap;
    /// A panel is at most the monitor height less this (look.css: max-height calc(100vh - 80px)).
    public const double CapMargin = 80;

    // ---- a list panel (panels/downloads.css: "A list panel (FIXES-2 B)")
    /// 360 wide, padding 8.
    public const double ListWidth = 360, ListPadding = 8;
    /// The head: a 40 DIP row measured from the island's edge, padding 0 8 0 16, its title 14 / 600.
    public const double HeadHeight = 40, HeadPadLeft = 16, HeadPadRight = 8, TitleSize = 14;
    public const int TitleWeight = LevitateTokens.WeightSemibold;
    /// The foot: a 40 DIP row with a hair line on top, padding 0 8 0 16.
    public const double FootHeight = 40, FootPadLeft = 16, FootPadRight = 8, FootLine = 1;
    /// A row: 40 tall, padding 0 4 0 12, 12 between its parts, radius 8; its glyph tile 28 at radius 8.
    public const double RowHeight = 40, RowPadLeft = 12, RowPadRight = 4, RowGap = 12, RowRadius = LevitateTokens.RadiusCtlL, TileSize = 28, TileRadius = LevitateTokens.RadiusCtlL;

    /// The pop-up opening upward (from below): opacity 160 ms, translate + scale 260 ms on ease-lift, from translateY +6 and
    /// scale 0.97, about its bottom centre (<see cref="PopUpOriginX"/>, <see cref="PopUpOriginY"/>).
    public static readonly Track[] PopUpIn =
    {
        new("opacity", 0, 1, 160, 0, LevitateTokens.EaseLift),
        new("translateY", 6, 0, 260, 0, LevitateTokens.EaseLift),
        new("scale", 0.97, 1, 260, 0, LevitateTokens.EaseLift),
    };

    /// Closing, back down: opacity 120 ms, translate + scale 160 ms, ease.
    public static readonly Track[] PopUpOut =
    {
        new("opacity", 1, 0, 120, 0, LevitateTokens.Ease),
        new("translateY", 0, 6, 160, 0, LevitateTokens.Ease),
        new("scale", 1, 0.97, 160, 0, LevitateTokens.Ease),
    };

    /// The pop-up's own origin: bottom centre (look.css .panel.up: transform-origin 50% 100%).
    public const double PopUpOriginX = 0.5, PopUpOriginY = 1.0;

    /// The frost a panel of this layout size shows.
    public static FrostKind Frost(double w, double h) =>
        double.IsFinite(w) && double.IsFinite(h) && w >= 0 && h >= 0 && Island.IsBig(w, h) ? FrostKind.Soft : FrostKind.Light;

    /// The dim to paint: the saved value clamped to 0.20..0.90, 0.20 when there is none.
    public static double Dim(double? saved) => LevitateDim.Resolve(saved);

    /// The film alpha over these samples (off the copy the panel shows, before the dim): the dark v2.1 check when dark and
    /// automatic, else 0.
    public static double Film(bool dark, bool auto, Rgb[]? samples, double dim) => dark && auto ? Legibility.Film(samples, dim) : 0;

    /// The white hover sheen's alpha (none at rest): .05 on a big panel, else .07.
    public static double Sheen(bool hover, double w, double h) =>
        !hover ? 0 : Frost(w, h) == FrostKind.Soft ? LevitateTokens.SheenHoverBig : LevitateTokens.SheenHover;

    /// The tallest a panel may be on a monitor this tall (DIP): its height less 80, never below 0.
    public static double Cap(double monitorH) => double.IsFinite(monitorH) && monitorH > CapMargin ? monitorH - CapMargin : 0;

    /// Where the pop-up goes, all in DIP relative to the monitor's top-left: centred on the anchor's centre x, its bottom 12
    /// above the bar's top, its height capped (<see cref="Cap"/>), kept 12 inside the monitor's left and right edges (a panel
    /// wider than the room left starts at the left gap).
    public static PopupBox Place(double anchorCx, double w, double h, double barTop, double monitorW, double monitorH)
    {
        double hh = Math.Min(Math.Max(0, h), Cap(monitorH));
        double ww = Math.Max(0, w);
        double x = anchorCx - ww / 2;
        double lo = EdgeGap, hi = monitorW - EdgeGap - ww;
        x = hi < lo ? lo : Math.Clamp(x, lo, hi);
        double bottom = barTop - AboveBar;
        return new PopupBox(x, bottom - hh, ww, hh);
    }

    /// The entrance: lift in when the pop-up opens in place of another, else pop up; reduced = no travel, quick fades.
    public static Track[] Enter(bool inPlace, bool reduced)
    {
        var t = inPlace ? Motion.LiftIn : PopUpIn;
        return reduced ? Motion.Reduce(t) : t;
    }

    /// The exit (reduced: a quick fade).
    public static Track[] Exit(bool reduced) => reduced ? Motion.Reduce(PopUpOut) : PopUpOut;

    /// The origin (fractions of the panel's size) a set of tracks plays about: the sink's 50 % 35 % for the lift, the
    /// bottom centre for the pop-up's own in / out.
    public static (double X, double Y) Origin(bool inPlace) => inPlace ? (Motion.SinkOriginX, Motion.SinkOriginY) : (PopUpOriginX, PopUpOriginY);

    /// The room (DIP) the window keeps below the panel while it moves: the furthest any entrance or exit travels down (28,
    /// the lift).
    public static double Slack()
    {
        double m = 0;
        foreach (var set in new[] { PopUpIn, PopUpOut, Motion.LiftIn })
            foreach (var t in set)
                if (t.Property == "translateY") m = Math.Max(m, Math.Max(t.From, t.To));
        return Math.Ceiling(m);
    }
}

/// <summary>
/// One levitated pop-up window's motion state (pure; PopupWindow holds one): hidden -&gt; opening (its entrance) -&gt;
/// shown (the entrance ended) -&gt; closing (its exit) -&gt; hidden (the exit ended). Every change bumps <see cref="Seq"/>, so a
/// motion that ends after a newer one started is ignored (<see cref="Ended"/> answers false). An open while closing drops
/// the exit and plays the entrance; a close of a pop-up that never painted, or one replaced by another (BAR-375), has no
/// exit at all (<see cref="Gone"/>).
/// </summary>
public sealed class PopupLift
{
    public PopupPhase Phase { get; private set; } = PopupPhase.Hidden;
    public int Seq { get; private set; }

    /// It opens: the entrance to play (see <see cref="PopupLevitate.Enter"/>).
    public Track[] Open(bool inPlace, bool reduced)
    {
        Seq++;
        Phase = PopupPhase.Opening;
        return PopupLevitate.Enter(inPlace, reduced);
    }

    /// It closes with its exit (none when it is already hidden).
    public Track[] Close(bool reduced)
    {
        if (Phase == PopupPhase.Hidden) return Array.Empty<Track>();
        Seq++;
        Phase = PopupPhase.Closing;
        return PopupLevitate.Exit(reduced);
    }

    /// It goes at once, no motion: replaced in place by another, or never painted.
    public void Gone()
    {
        Seq++;
        Phase = PopupPhase.Hidden;
    }

    /// The motion started at <paramref name="seq"/> ended: true when it is still the current one (opening -&gt; shown,
    /// closing -&gt; hidden), false when a newer change came first.
    public bool Ended(int seq)
    {
        if (seq != Seq) return false;
        if (Phase == PopupPhase.Opening) { Phase = PopupPhase.Shown; return true; }
        if (Phase == PopupPhase.Closing) { Phase = PopupPhase.Hidden; return true; }
        return false;
    }
}
