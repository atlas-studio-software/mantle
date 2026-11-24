using System;
using System.Collections.Generic;
using Mantle.Core.Features.Snap;
using Xunit;

namespace Mantle.Core.Tests.Features.Snap;

// lane r9-snap: the never-strand rule (SnapSafety). Every expected rect is worked out by hand in the comment
// beside it, never by asking the code twice. Made-up monitors only: no window is touched.
public class SnapSafetyTests
{
    static SnapBox B(int l, int t, int r, int b) => new(l, t, r, b);

    // A: the primary, 1920x1080 at 100 %, taskbar 40 px. P: a 2560x1440 monitor at 125 % to its right.
    // L: a 1920x1080 at 100 % LEFT of the primary (negative x).
    static readonly SnapScreen A = new("A", B(0, 0, 1920, 1080), B(0, 0, 1920, 1040), 96, true);
    static readonly SnapScreen P = new("P", B(1920, 0, 4480, 1440), B(1920, 0, 4480, 1392), 120);
    static readonly SnapScreen L = new("L", B(-1920, 0, 0, 1080), B(-1920, 0, 0, 1040), 96);

    static void OnLive(SnapLanding got, IReadOnlyList<SnapScreen> live) =>
        Assert.True(SnapSafety.TitleBarOnLive(got.Frame, live), "title bar off every live work area: " + got.Frame + " (" + got.Why + ")");

    // ------------------------------------------------------------ SAFETY: a monitor that disappears

    [Fact]
    public void A_frame_left_on_a_monitor_that_is_gone_comes_back_onto_the_live_one_with_its_title_bar_on_the_work_area()
    {
        // P was unplugged: [2200,200,3000,800] (800x600) is on no live monitor. Nearest live = A (distance).
        // Fit onto A's work 0..1920 x 0..1040: left = max(0, min(2200, 1920-800=1120)) = 1120, top 200.
        var live = new[] { A };
        SnapLanding got = SnapSafety.Clamp(B(2200, 200, 3000, 800), live);
        Assert.True(got.Changed);
        Assert.Equal("A", got.Screen!.Id);
        Assert.Equal(B(1120, 200, 1920, 800), got.Frame);
        OnLive(got, live);
    }

    [Fact]
    public void A_frame_far_left_of_every_monitor_goes_to_the_nearest_one_even_at_negative_coordinates()
    {
        // centre (-3600, 400): 1680 px from L, 3600 from A, so L. Fit onto L's work -1920..0:
        // left = max(-1920, min(-4000, 0-800)) = -1920 -> [-1920,100,-1120,700].
        var live = new[] { L, A };
        SnapLanding got = SnapSafety.Clamp(B(-4000, 100, -3200, 700), live);
        Assert.Equal("L", got.Screen!.Id);
        Assert.Equal(B(-1920, 100, -1120, 700), got.Frame);
        OnLive(got, live);
    }

    // ------------------------------------------------------------ SAFETY: a restore whose saved monitor is gone

    [Fact]
    public void A_restore_whose_saved_monitor_is_gone_lands_on_a_live_monitor_carried_and_scaled_for_its_DPI()
    {
        // saved on P (125 %): [2200,200,3000,800] = offset 280,200 into P's work, 800x600.
        // P is gone; nearest live is A (96): size 800*96/120 = 640, 600*96/120 = 480; offset 280 -> 224, 200 -> 160.
        // -> [224,160,864,640], already inside A's work.
        var saved = new SnapSaved(B(2200, 200, 3000, 800), "P", P.Work, 120);
        var live = new[] { A };
        SnapLanding got = SnapSafety.Restore(saved, live);
        Assert.True(got.Changed);
        Assert.Equal("A", got.Screen!.Id);
        Assert.Equal(B(224, 160, 864, 640), got.Frame);
        Assert.Contains("gone", got.Why);
        OnLive(got, live);
    }

    [Fact]
    public void A_restore_whose_saved_monitor_is_gone_and_whose_work_area_was_never_known_is_fitted_onto_the_nearest()
    {
        // no saved work area: plain fit of [2200,200,3000,800] onto A -> [1120,200,1920,800]
        var saved = new SnapSaved(B(2200, 200, 3000, 800), "P", default, 0);
        var live = new[] { A };
        SnapLanding got = SnapSafety.Restore(saved, live);
        Assert.Equal(B(1120, 200, 1920, 800), got.Frame);
        OnLive(got, live);
    }

    // ------------------------------------------------------------ SAFETY: a DPI change

    [Fact]
    public void A_restore_after_the_saved_monitor_changed_scale_is_resized_for_it_and_kept_on_it()
    {
        // A went 100 % -> 150 % (same pixels). [100,100,900,700]: size 800x600 -> 1200x900, offset 100,100 -> 150,150
        // -> [150,150,1350,1050]; the bottom is past 1040, so top = max(0, min(150, 1040-900=140)) = 140.
        var saved = new SnapSaved(B(100, 100, 900, 700), "A", A.Work, 96);
        var live = new[] { A with { Dpi = 144 } };
        SnapLanding got = SnapSafety.Restore(saved, live);
        Assert.True(got.Changed);
        Assert.Equal(B(150, 140, 1350, 1040), got.Frame);
        Assert.Contains("scale", got.Why);
        OnLive(got, live);
    }

    [Fact]
    public void A_frame_bigger_than_the_screen_after_a_DPI_change_is_shrunk_onto_the_work_area()
    {
        // [-50,-30,2050,1100]: the title bar is above the work area (top -30 < 0 - 2). Fit: 1920x1040 at 0,0.
        var live = new[] { A };
        SnapLanding got = SnapSafety.Clamp(B(-50, -30, 2050, 1100), live);
        Assert.Equal(B(0, 0, 1920, 1040), got.Frame);
        OnLive(got, live);
    }

    // ------------------------------------------------------------ SAFETY: the same monitor, changed (the SnapGeom.Translate hole)

    [Fact]
    public void A_restore_onto_the_same_monitor_after_its_work_area_shrank_is_kept_on_it()
    {
        // saved [1200,600,1900,1030] on a 1920x1040 work area; the resolution is now 1280x720 (work 1280x680).
        // offset and size unchanged (same DPI) -> fit: left = max(0, min(1200, 1280-700=580)) = 580,
        // top = max(0, min(600, 680-430=250)) = 250 -> [580,250,1280,680].
        var saved = new SnapSaved(B(1200, 600, 1900, 1030), "A", A.Work, 96);
        var live = new[] { A with { Bounds = B(0, 0, 1280, 720), Work = B(0, 0, 1280, 680) } };
        SnapLanding got = SnapSafety.Restore(saved, live);
        Assert.Equal(B(580, 250, 1280, 680), got.Frame);
        Assert.Contains("work area", got.Why);
        OnLive(got, live);
    }

    [Fact]
    public void A_restore_onto_an_unchanged_monitor_is_exactly_where_it_was()
    {
        var saved = new SnapSaved(B(100, 100, 900, 700), "A", A.Work, 96);
        SnapLanding got = SnapSafety.Restore(saved, new[] { A, P });
        Assert.False(got.Changed);
        Assert.Equal(B(100, 100, 900, 700), got.Frame);
    }

    // ------------------------------------------------------------ SAFETY: the title bar, edge by edge

    [Fact]
    public void A_title_bar_under_a_top_app_bar_is_moved_down_below_it()
    {
        // a 40 px top bar: work starts at y 40; top 10 is under it -> top = max(40, min(10, 1040-600)) = 40
        var top = A with { Work = B(0, 40, 1920, 1040) };
        var live = new[] { top };
        SnapLanding got = SnapSafety.Clamp(B(300, 10, 1100, 610), live);
        Assert.Equal(B(300, 40, 1100, 640), got.Frame);
        OnLive(got, live);
    }

    [Fact]
    public void A_frame_whose_title_bar_is_on_a_live_work_area_is_never_moved_even_hanging_off_an_edge()
    {
        // 120 px of the title bar is on A (>= 64): the person can grab it, so the frame is left alone
        var live = new[] { A };
        foreach (SnapBox f in new[] { B(100, 100, 900, 700), B(1800, 100, 2600, 700), B(-700, 500, 100, 1300) }) {
            SnapLanding got = SnapSafety.Clamp(f, live);
            Assert.False(got.Changed, f + ": " + got.Why);
            Assert.Equal(f, got.Frame);
        }
    }

    [Fact]
    public void Less_than_64_px_of_title_bar_on_screen_is_brought_back()
    {
        // 40 px across (1880..1920) < 64: left = max(0, min(1880, 1120)) = 1120
        var live = new[] { A };
        SnapLanding got = SnapSafety.Clamp(B(1880, 100, 2680, 700), live);
        Assert.Equal(B(1120, 100, 1920, 700), got.Frame);
        OnLive(got, live);
    }

    [Fact]
    public void A_title_bar_at_the_very_bottom_of_the_work_area_is_brought_back_up()
    {
        // top 1030: the 32 px strip would end at 1062 > 1040 + 2. top = max(0, min(1030, 1040-600=440)) = 440
        var live = new[] { A };
        SnapLanding got = SnapSafety.Clamp(B(100, 1030, 900, 1630), live);
        Assert.Equal(B(100, 440, 900, 1040), got.Frame);
        OnLive(got, live);
    }

    [Fact]
    public void A_window_straddling_two_live_monitors_with_its_title_bar_on_one_is_left_alone()
    {
        SnapLanding got = SnapSafety.Clamp(B(1700, 100, 2500, 700), new[] { A, P });
        Assert.False(got.Changed);
    }

    [Fact]
    public void The_title_bar_scales_with_the_monitor_DPI()
    {
        Assert.Equal(32, SnapSafety.Px(SnapSafety.TitleBarDip, 96));
        Assert.Equal(40, SnapSafety.Px(SnapSafety.TitleBarDip, 120));
        Assert.Equal(48, SnapSafety.Px(SnapSafety.TitleBarDip, 144));
        Assert.Equal(80, SnapSafety.Px(SnapSafety.GrabDip, 120));
        Assert.Equal(32, SnapSafety.Px(SnapSafety.TitleBarDip, 0));   // unknown = 96
    }

    // ------------------------------------------------------------ SAFETY: nothing to put it on

    [Fact]
    public void With_no_live_monitor_known_nothing_is_moved()
    {
        SnapBox f = B(2200, 200, 3000, 800);
        foreach (IReadOnlyList<SnapScreen>? live in new IReadOnlyList<SnapScreen>?[] { null, Array.Empty<SnapScreen>() }) {
            SnapLanding c = SnapSafety.Clamp(f, live);
            Assert.False(c.Changed);
            Assert.Equal(f, c.Frame);
            Assert.Null(c.Screen);
            Assert.Equal(SnapSafety.NoMonitor, c.Why);
            SnapLanding r = SnapSafety.Restore(new SnapSaved(f, "P", P.Work, 120), live);
            Assert.False(r.Changed);
            Assert.Equal(f, r.Frame);
        }
    }

    [Fact]
    public void A_monitor_with_no_work_area_uses_its_bounds_and_one_with_neither_is_skipped()
    {
        var none = new SnapScreen("Z", default, default, 96);
        var bare = new SnapScreen("W", B(0, 0, 1920, 1080), default, 96, true);
        var live = new[] { none, bare };
        SnapLanding got = SnapSafety.Clamp(B(5000, 5000, 5800, 5600), live);
        Assert.Equal("W", got.Screen!.Id);
        Assert.Equal(B(1120, 480, 1920, 1080), got.Frame);   // fit onto the bounds: 1920-800, 1080-600
        OnLive(got, live);
    }

    [Fact]
    public void An_empty_frame_becomes_the_centred_two_thirds()
    {
        // 1920*2/3 = 1280, 1040*2/3 = 693; left (1920-1280)/2 = 320, top (1040-693)/2 = 173
        SnapLanding got = SnapSafety.Clamp(default, new[] { A });
        Assert.Equal(B(320, 173, 1600, 866), got.Frame);
    }

    // ------------------------------------------------------------ SAFETY: the sweep

    [Fact]
    public void Sweep_every_clamped_or_restored_frame_has_its_title_bar_on_a_live_monitor_and_a_safe_one_never_moves()
    {
        var top = A with { Work = B(0, 40, 1920, 1040) };
        var above = new SnapScreen("U", B(0, -1080, 1920, 0), B(0, -1080, 1920, -40), 96);
        var layouts = new[] {
            new[] { A }, new[] { top, P }, new[] { L, A, P }, new[] { above, A }, new[] { P },
            new[] { A with { Dpi = 144 }, L with { Dpi = 120 } },
        };
        var all = new[] { A, P, L, above };
        var rnd = new Random(20261124);
        var bad = new List<string>();
        for (int i = 0; i < 4000; i++) {
            SnapScreen[] live = layouts[i % layouts.Length];
            int w = rnd.Next(1, 4200), h = rnd.Next(1, 2400);
            int x = rnd.Next(-7000, 7000), y = rnd.Next(-3000, 3000);
            SnapBox f = SnapBox.Xywh(x, y, w, h);
            bool safe = SnapSafety.TitleBarOnLive(f, live);
            SnapLanding c = SnapSafety.Clamp(f, live);
            if (!SnapSafety.TitleBarOnLive(c.Frame, live)) bad.Add("clamp " + f + " -> " + c.Frame);
            if (safe && (c.Changed || c.Frame != f)) bad.Add("a safe frame moved: " + f + " -> " + c.Frame);
            SnapScreen from = all[rnd.Next(all.Length)];
            var saved = new SnapSaved(f, from.Id, from.Work, from.Dpi);
            SnapLanding r = SnapSafety.Restore(saved, live);
            if (!SnapSafety.TitleBarOnLive(r.Frame, live)) bad.Add("restore " + f + " saved on " + from.Id + " -> " + r.Frame);
        }
        Assert.True(bad.Count == 0, bad.Count + " bad, first: " + string.Join(" | ", bad.GetRange(0, Math.Min(5, bad.Count))));
    }
}
