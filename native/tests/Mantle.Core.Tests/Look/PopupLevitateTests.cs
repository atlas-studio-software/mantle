using System;
using System.Linq;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// What the bar's pop-up host does for every levitated pop-up (lane r12-levitate-host, PopupLevitate / PopupLift): the frost
/// by size, the dim, the film (dark and automatic only), the hover sheen, the placement 12 above the bar and its cap, the
/// list-panel numbers (bar prototype apps-r3/mantle-bar panels/downloads.css) and the open / close motion and its states.
/// </summary>
public class PopupLevitateTests
{
    static Rgb[] Fill(int n, double r, double g, double b) => Enumerable.Repeat(new Rgb(r, g, b), n).ToArray();
    static readonly Rgb[] White = Fill(40, 255, 255, 255);
    static readonly Rgb[] Dark = Fill(40, 30, 40, 45);

    // ------------------------------------------------------------------ frost, dim, film, sheen

    [Fact]
    public void A_big_pop_up_shows_the_soft_frost_a_small_one_the_light()
    {
        Assert.Equal(FrostKind.Soft, PopupLevitate.Frost(360, 420));       // Downloads
        Assert.Equal(FrostKind.Soft, PopupLevitate.Frost(100, 200));       // 20000 exactly
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(100, 199.99));   // 19999
        Assert.Equal(FrostKind.Soft, PopupLevitate.Frost(48, 417));        // 20016
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(48, 416));       // 19968
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(47, 1000));      // the short side 47
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(0, 0));
        // look-sync frostKind: anything that is not a finite size >= 0 is light
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(double.NaN, 500));
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(double.PositiveInfinity, 500));
        Assert.Equal(FrostKind.Light, PopupLevitate.Frost(-500, -500));
    }

    [Fact]
    public void The_dim_is_020_by_default_and_clamped_to_020_090()
    {
        Assert.Equal(0.20, PopupLevitate.Dim(null));
        Assert.Equal(0.20, PopupLevitate.Dim(double.NaN));
        Assert.Equal(0.20, PopupLevitate.Dim(0.05));
        Assert.Equal(0.20, PopupLevitate.Dim(0));
        Assert.Equal(0.35, PopupLevitate.Dim(0.35));
        Assert.Equal(0.90, PopupLevitate.Dim(0.90));
        Assert.Equal(0.20, PopupLevitate.Dim(5));   // out of range reads as the seed (look-sync v2.1), not clamped
    }

    [Fact]
    public void The_film_is_the_dark_v21_check_and_0_in_light_or_manual()
    {
        Assert.Equal(0.72, PopupLevitate.Film(dark: true, auto: true, White, 0.20));    // white at the lowest Dim needs the cap
        Assert.Equal(0.64, PopupLevitate.Film(dark: true, auto: true, White, 0.30));
        Assert.Equal(Legibility.Film(White, 0.50), PopupLevitate.Film(true, true, White, 0.50));
        Assert.Equal(0, PopupLevitate.Film(dark: true, auto: true, Dark, 0.20));        // a dark picture gets none
        Assert.Equal(0, PopupLevitate.Film(dark: true, auto: false, White, 0.20));      // manual
        Assert.Equal(0, PopupLevitate.Film(dark: false, auto: true, White, 0.20));      // light: no dark film, ever
        Assert.Equal(0, PopupLevitate.Film(dark: false, auto: true, Fill(40, 0, 0, 0), 0.20));   // nor the light lift (not built)
        Assert.Equal(0, PopupLevitate.Film(true, true, null, 0.20));
        Assert.Equal(0, PopupLevitate.Film(true, true, Array.Empty<Rgb>(), 0.20));
    }

    [Fact]
    public void No_sheen_at_rest_007_on_hover_005_on_a_big_panel()
    {
        Assert.Equal(0, PopupLevitate.Sheen(false, 360, 420));
        Assert.Equal(0, PopupLevitate.Sheen(false, 40, 40));
        Assert.Equal(0.05, PopupLevitate.Sheen(true, 360, 420));
        Assert.Equal(0.07, PopupLevitate.Sheen(true, 200, 40));
    }

    // ------------------------------------------------------------------ placement

    [Fact]
    public void A_pop_up_sits_12_above_the_bar_centred_on_its_anchor()
    {
        // a 1920 x 1080 monitor, the bar's top at 1018 DIP, the anchor's centre at 1500
        var b = PopupLevitate.Place(1500, 360, 300, 1018, 1920, 1080);
        Assert.Equal(1320, b.X);
        Assert.Equal(360, b.W);
        Assert.Equal(300, b.H);
        Assert.Equal(1006, b.Bottom);                   // 1018 - 12
        Assert.Equal(706, b.Y);
        Assert.Equal(12, PopupLevitate.AboveBar);
    }

    [Fact]
    public void A_pop_up_is_kept_12_inside_the_monitor_left_and_right()
    {
        Assert.Equal(12, PopupLevitate.Place(30, 360, 300, 1018, 1920, 1080).X);             // would start at -150
        Assert.Equal(1920 - 12 - 360, PopupLevitate.Place(1900, 360, 300, 1018, 1920, 1080).X);
        Assert.Equal(12, PopupLevitate.Place(200, 400, 300, 1018, 380, 1080).X);             // wider than the room: the left gap
    }

    [Fact]
    public void A_pop_up_is_at_most_the_monitor_height_less_80()
    {
        Assert.Equal(1000, PopupLevitate.Cap(1080));
        Assert.Equal(0, PopupLevitate.Cap(60));
        Assert.Equal(0, PopupLevitate.Cap(double.NaN));
        var b = PopupLevitate.Place(500, 360, 5000, 1018, 1920, 1080);
        Assert.Equal(1000, b.H);
        Assert.Equal(1006, b.Bottom);                   // still 12 above the bar
        Assert.Equal(6, b.Y);
        Assert.Equal(0, PopupLevitate.Place(500, 360, -5, 1018, 1920, 1080).H);
    }

    [Fact]
    public void A_list_panel_is_360_wide_padding_8_with_a_40_head_and_a_40_foot()
    {
        Assert.Equal(360, PopupLevitate.ListWidth);
        Assert.Equal(8, PopupLevitate.ListPadding);
        Assert.Equal(40, PopupLevitate.HeadHeight);
        Assert.Equal((16.0, 8.0), (PopupLevitate.HeadPadLeft, PopupLevitate.HeadPadRight));
        Assert.Equal((14.0, 600), (PopupLevitate.TitleSize, PopupLevitate.TitleWeight));
        Assert.Equal(40, PopupLevitate.FootHeight);
        Assert.Equal((16.0, 8.0, 1.0), (PopupLevitate.FootPadLeft, PopupLevitate.FootPadRight, PopupLevitate.FootLine));
        Assert.Equal((40.0, 12.0, 4.0, 12.0), (PopupLevitate.RowHeight, PopupLevitate.RowPadLeft, PopupLevitate.RowPadRight, PopupLevitate.RowGap));
        Assert.Equal((8.0, 28.0, 8.0), (PopupLevitate.RowRadius, PopupLevitate.TileSize, PopupLevitate.TileRadius));
        Assert.Equal(14, PopupLevitate.Radius);
        // the radii in use are LOOK.md's set
        var set = new double[] { 4, 6, 8, 12, 14, 16, 20, 999 };
        Assert.Contains(PopupLevitate.Radius, set);
        Assert.Contains(PopupLevitate.RowRadius, set);
    }

    // ------------------------------------------------------------------ motion

    [Fact]
    public void A_pop_up_pops_up_from_6_below_and_097_about_its_bottom_centre()
    {
        var t = PopupLevitate.Enter(inPlace: false, reduced: false);
        Assert.Same(PopupLevitate.PopUpIn, t);
        Assert.Equal(new[] { "opacity", "translateY", "scale" }, t.Select(x => x.Property));
        Assert.Equal((0.0, 1.0, 160.0), (t[0].From, t[0].To, t[0].DurMs));
        Assert.Equal((6.0, 0.0, 260.0), (t[1].From, t[1].To, t[1].DurMs));
        Assert.Equal((0.97, 1.0, 260.0), (t[2].From, t[2].To, t[2].DurMs));
        Assert.All(t, x => Assert.Equal(LevitateTokens.EaseLift, x.Curve));
        Assert.Equal((0.5, 1.0), PopupLevitate.Origin(false));
        var o = PopupLevitate.Exit(false);
        Assert.Equal((1.0, 0.0, 120.0), (o[0].From, o[0].To, o[0].DurMs));
        Assert.Equal((0.0, 6.0, 160.0), (o[1].From, o[1].To, o[1].DurMs));
        Assert.Equal((1.0, 0.97, 160.0), (o[2].From, o[2].To, o[2].DurMs));
        Assert.All(o, x => Assert.Equal(LevitateTokens.Ease, x.Curve));
    }

    [Fact]
    public void A_pop_up_opening_in_place_of_another_lifts_in_from_28_and_0965()
    {
        var t = PopupLevitate.Enter(inPlace: true, reduced: false);
        Assert.Same(Motion.LiftIn, t);
        Assert.Equal(28, t.Single(x => x.Property == "translateY").From);
        Assert.Equal(0.965, t.Single(x => x.Property == "scale").From);
        Assert.Equal((Motion.SinkOriginX, Motion.SinkOriginY), PopupLevitate.Origin(true));
        Assert.Equal(28, PopupLevitate.Slack());   // the room the window keeps below the panel while it moves
    }

    [Fact]
    public void Motion_is_transform_and_opacity_only_and_reduced_is_no_travel_and_quick_fades()
    {
        foreach (var set in new[] { PopupLevitate.PopUpIn, PopupLevitate.PopUpOut, Motion.LiftIn })
            Assert.All(set, t => Assert.Contains(t.Property, new[] { "opacity", "translateY", "scale" }));
        foreach (var r in new[] { PopupLevitate.Enter(false, true), PopupLevitate.Enter(true, true), PopupLevitate.Exit(true) })
            foreach (var t in r) {
                Assert.Equal(0, t.DelayMs);
                if (t.Property == "opacity") Assert.True(t.DurMs > 0 && t.DurMs <= Motion.ReducedFadeMs);
                else Assert.Equal(0, t.DurMs);
            }
        Assert.Equal(160, Motion.TotalMs(PopupLevitate.Enter(true, true), false));
        Assert.Equal(260, Motion.TotalMs(PopupLevitate.Enter(false, false), false));
        Assert.Equal(480, Motion.TotalMs(PopupLevitate.Enter(true, false), false));
        Assert.Equal(160, Motion.TotalMs(PopupLevitate.Exit(false), false));
    }

    [Fact]
    public void The_motion_states_open_show_close_and_hide()
    {
        var m = new PopupLift();
        Assert.Equal(PopupPhase.Hidden, m.Phase);
        Assert.Empty(m.Close(false));                   // nothing to close
        var t = m.Open(false, false);
        Assert.Same(PopupLevitate.PopUpIn, t);
        int open = m.Seq;
        Assert.Equal(PopupPhase.Opening, m.Phase);
        Assert.True(m.Ended(open));
        Assert.Equal(PopupPhase.Shown, m.Phase);
        Assert.False(m.Ended(open));                    // once
        Assert.Same(PopupLevitate.PopUpOut, m.Close(false));
        int close = m.Seq;
        Assert.Equal(PopupPhase.Closing, m.Phase);
        Assert.True(m.Ended(close));
        Assert.Equal(PopupPhase.Hidden, m.Phase);
    }

    [Fact]
    public void An_open_while_closing_drops_the_exit_and_a_late_end_is_ignored()
    {
        var m = new PopupLift();
        m.Open(false, false);
        m.Ended(m.Seq);
        m.Close(false);
        int exit = m.Seq;
        m.Open(false, false);                          // reopened before the exit ended
        Assert.False(m.Ended(exit));                   // the exit's end does not hide it
        Assert.Equal(PopupPhase.Opening, m.Phase);
        Assert.True(m.Ended(m.Seq));
        Assert.Equal(PopupPhase.Shown, m.Phase);
    }

    [Fact]
    public void A_replaced_or_unpainted_pop_up_is_gone_at_once_and_its_entrance_end_is_ignored()
    {
        var m = new PopupLift();
        m.Open(false, false);
        int entrance = m.Seq;
        m.Gone();                                      // replaced in place by another (BAR-375) or closed unpainted
        Assert.Equal(PopupPhase.Hidden, m.Phase);
        Assert.False(m.Ended(entrance));
        Assert.Equal(PopupPhase.Hidden, m.Phase);
        Assert.Same(Motion.LiftIn, m.Open(inPlace: true, reduced: false));
    }
}
