using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The bar as three islands (lane r13-bar-islands, BarIslands): the prototype's numbers (apps-r3/mantle-bar look.css "the
/// bar: three islands"), the band on common screens at 100 % and 150 %, the dock's drop-left rule (shell.js fitDock) and the
/// offline network words (Electron ui/bar/net.js, prototype ?net=off).
/// </summary>
public class BarIslandsTests
{
    // ------------------------------------------------------------------ the prototype's numbers

    [Fact]
    public void Three_islands_left_dock_right()
    {
        Assert.Equal(new[] { "left", "dock", "right" }, BarIslands.Order);
    }

    [Fact]
    public void Each_island_is_44_tall_radius_12_and_12_from_the_edges()
    {
        Assert.Equal(44, BarIslands.BarH);
        Assert.Equal(12, BarIslands.Radius);
        Assert.Equal(12, BarIslands.Gap);
        Assert.Equal(68, BarIslands.Zone);
        var l = BarIslands.Layout(1920, 400, 300);
        foreach (var r in new[] { l.Left, l.Dock, l.Right })
        {
            Assert.Equal(0, r.Y);
            Assert.Equal(44, r.H);
        }
    }

    [Fact]
    public void The_left_island_is_44_square_around_a_36_start_button()
    {
        var l = BarIslands.Layout(1920, 400, 300);
        Assert.Equal(new IslandRect(0, 0, 44, 44), l.Left);
        Assert.Equal(36, BarIslands.StartSize);
        Assert.Equal(22, BarIslands.StartMark);
    }

    [Fact]
    public void Dock_cells_are_40_by_36_and_status_glyphs_32_by_36()
    {
        Assert.Equal(40, BarIslands.CellW);
        Assert.Equal(36, BarIslands.CellH);
        Assert.Equal(8, BarIslands.CellRadius);
        Assert.Equal(20, BarIslands.CellIcon);
        Assert.Equal(32, BarIslands.GlyphW);
        Assert.Equal(36, BarIslands.GlyphH);
        Assert.Equal(8, BarIslands.GlyphRadius);
        Assert.Equal(16, BarIslands.GlyphIcon);
        Assert.Equal(4, BarIslands.DockPadX);
        Assert.Equal(2, BarIslands.DockGap);
        Assert.Equal(4, BarIslands.RightPadX);
        Assert.Equal(2, BarIslands.RightGap);
        Assert.Equal(4, BarIslands.RunW);
        Assert.Equal(3, BarIslands.RunH);
        Assert.Equal(2, BarIslands.RunBottom);
        Assert.Equal(16, BarIslands.FocusW);
        Assert.Equal(3, BarIslands.FocusH);
        Assert.Equal(320, BarIslands.FocusMs);
        Assert.Equal(1, BarIslands.SepW);
        Assert.Equal(20, BarIslands.SepH);
        Assert.Equal(4, BarIslands.SepMarginX);
        Assert.Equal(8, BarIslands.ClockPadX);
        Assert.Equal(12, BarIslands.ClockTime);
        Assert.Equal(500, BarIslands.ClockTimeWeight);
        Assert.Equal(11, BarIslands.ClockDate);
        Assert.Equal(0.07, BarIslands.HoverFill);
        Assert.Equal(0.05, BarIslands.PressedFill);
        Assert.Equal(0.55, BarIslands.OfflineOpacity);
    }

    // ------------------------------------------------------------------ layout and fitDock

    [Fact]
    public void On_a_wide_screen_the_dock_is_centred_on_the_screen_and_the_right_island_ends_at_the_band()
    {
        var l = BarIslands.Layout(1920, 400, 300);
        Assert.False(l.DockLeft);
        Assert.Equal(408, l.Dock.W);                       // 400 + padding 4 each side
        Assert.Equal(960, l.Dock.X + BarIslands.Gap + l.Dock.W / 2);   // its centre on the screen's centre
        Assert.Equal(308, l.Right.W);
        Assert.Equal(1896, l.Right.Right);                 // the band is 1920 - 2 x 12
        Assert.True(l.Left.Right < l.Dock.X && l.Dock.Right < l.Right.X);   // left to right
    }

    [Fact]
    public void On_a_narrow_screen_the_dock_drops_to_12_after_the_M()
    {
        var l = BarIslands.Layout(800, 400, 300);
        Assert.True(l.DockLeft);
        Assert.Equal(56, l.Dock.X);                        // 44 + 12
        Assert.Equal(468, l.Right.X);                      // 776 - 308
    }

    [Fact]
    public void The_dock_drops_only_when_its_right_edge_plus_12_passes_the_right_island()
    {
        // screen 1000, right island 300 at band x 676: a dock 352 wide ends at 664, exactly 12 before
        Assert.False(BarIslands.Layout(1000, 344, 292).DockLeft);
        Assert.True(BarIslands.Layout(1000, 346, 292).DockLeft);
        Assert.Equal(608, BarIslands.DockRoom(1000, 292));   // 976 - 300 - 12 - 56
    }

    // ------------------------------------------------------------------ the band

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1536, 864)]
    [InlineData(1920, 1080)]
    public void The_band_in_DIP_is_the_screen_less_12_each_side_44_tall_12_up(double w, double h)
    {
        var b = BarIslands.Band(w, h);
        Assert.Equal(12, b.X);
        Assert.Equal(w - 24, b.W);
        Assert.Equal(44, b.H);
        Assert.Equal(h - 12, b.Bottom);
    }

    [Theory]
    // 100 %
    [InlineData(1366, 768, 1.0, 12, 712, 1354, 756)]
    [InlineData(1536, 864, 1.0, 12, 808, 1524, 852)]
    [InlineData(1920, 1080, 1.0, 12, 1024, 1908, 1068)]
    // 150 %: 12 DIP = 18 px, 44 DIP = 66 px
    [InlineData(1366, 768, 1.5, 18, 684, 1348, 750)]
    [InlineData(1536, 864, 1.5, 18, 780, 1518, 846)]
    [InlineData(1920, 1080, 1.5, 18, 996, 1902, 1062)]
    public void The_band_in_px_at_100_and_150(int w, int h, double s, int l, int t, int r, int b)
    {
        Assert.Equal((l, t, r, b), BarIslands.BandPx(0, 0, w, h, s));
    }

    [Fact]
    public void The_band_follows_a_monitor_that_does_not_start_at_0()
    {
        Assert.Equal((1932, 1024, 3828, 1068), BarIslands.BandPx(1920, 0, 3840, 1080, 1.0));
    }

    // ------------------------------------------------------------------ the offline words

    [Fact]
    public void Connected_is_not_offline_and_keeps_the_buttons_own_words()
    {
        Assert.Equal(new NetOffline(false, "", ""), BarIslands.Offline(true, true, true));
        Assert.Equal(new NetOffline(false, "", ""), BarIslands.Offline(true, null, null));   // wired
        Assert.False(BarIslands.Offline(false, true, true).Offline);   // Wi-Fi says connected: not called offline
    }

    [Fact]
    public void Wi_Fi_on_but_not_connected()
    {
        var o = BarIslands.Offline(false, true, false);
        Assert.True(o.Offline);
        Assert.Equal("Not connected, Wi-Fi is on but not connected to a network", o.Label);
        Assert.Equal("Not connected\nWi-Fi is on but not connected to a network\nClick: quick settings", o.Tip);
        Assert.Equal(o, BarIslands.Offline(false, true, null));
    }

    [Fact]
    public void Wi_Fi_off()
    {
        var o = BarIslands.Offline(false, false, false);
        Assert.True(o.Offline);
        Assert.Equal("Not connected, Wi-Fi is turned off", o.Label);
        Assert.Equal("Not connected\nWi-Fi is turned off\nClick: quick settings", o.Tip);
    }

    [Fact]
    public void No_Wi_Fi_and_no_network()
    {
        var o = BarIslands.Offline(false, null, null);
        Assert.True(o.Offline);
        Assert.Equal("Not connected, No connections are available", o.Label);
        Assert.Equal("Not connected\nNo connections are available\nClick: quick settings", o.Tip);
    }
}
