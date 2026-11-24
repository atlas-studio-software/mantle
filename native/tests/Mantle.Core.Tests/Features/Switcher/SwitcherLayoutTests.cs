using System;
using System.Collections.Generic;
using Mantle.Core.Features.Switcher;
using Xunit;

namespace Mantle.Core.Tests.Features.Switcher;

// lib/switcher.test.js layout() and liveLayout() (the reflow half), ported (SWI-058..SWI-065, SWI-067..SWI-069).
public class SwitcherLayoutTests
{
    public static IEnumerable<object[]> Screens() => new[]
    {
        new object[] { 1280, 720 }, new object[] { 1920, 1080 }, new object[] { 1536, 864 }, new object[] { 1024, 600 },
        new object[] { 800, 600 }, new object[] { 3840, 2160 }, new object[] { 2560, 1080 },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void Every_count_from_0_to_60_fits_the_screen_with_whole_rows(int ww, int wh)
    {
        for (int n = 0; n <= 60; n++)
        {
            var t = SwitcherLayout.Layout(n, ww, wh);
            string tag = $"{ww}x{wh} n={n}";
            if (n == 0)
            {
                Assert.True(t.Cols == 0 && t.PanelW == SwitcherLayout.EmptyW && t.W == SwitcherLayout.EmptyW + 2 * SwitcherLayout.Margin, tag + ": the empty panel");
                continue;
            }
            var bad = new List<string>();
            if (t.W > ww * SwitcherLayout.MaxW + 0.01) bad.Add($"wider than {SwitcherLayout.MaxW} of the screen ({t.W})");
            if (t.H > wh * SwitcherLayout.MaxH + 0.01) bad.Add($"taller than {SwitcherLayout.MaxH} of the screen ({t.H})");
            if (t.Cols * t.Rows < n) bad.Add("not enough tiles");
            if ((t.Rows - 1) * t.Cols >= n) bad.Add("an empty last row");
            if (t.Rows != (n + t.Cols - 1) / t.Cols) bad.Add("rows do not match");
            if (t.TileW > SwitcherLayout.TileMax || t.TileW < SwitcherLayout.TileMin) bad.Add($"tile {t.TileW} out of range");
            if (t.PreviewW != t.TileW - 2 * SwitcherLayout.Inset || t.PreviewH != (int)Math.Floor(t.PreviewW * SwitcherLayout.Aspect + 0.5)) bad.Add("preview box off");
            if (t.TileH != SwitcherLayout.Inset + t.PreviewH + SwitcherLayout.CapH) bad.Add("tile height off");
            if (t.Scroll != (t.VisibleRows < t.Rows) || t.VisibleRows < 1) bad.Add("scroll flag off");
            if (t.GridW != t.Cols * t.TileW + (t.Cols - 1) * SwitcherLayout.Gap) bad.Add("grid width off");
            if (t.PanelW != 2 * SwitcherLayout.Pad + t.GridW || t.W != t.PanelW + 2 * SwitcherLayout.Margin) bad.Add("panel width off");
            if (t.PanelH != 2 * SwitcherLayout.Pad + t.VisibleRows * t.TileH + (t.VisibleRows - 1) * SwitcherLayout.Gap) bad.Add("panel height off");
            Assert.True(bad.Count == 0, "layout " + tag + ": " + string.Join("; ", bad));
        }
    }

    [Fact]
    public void Seven_windows_are_balanced_into_4_plus_3()
    {
        var seven = SwitcherLayout.Layout(7, 1920, 1080);
        Assert.Equal(4, seven.Cols);
        Assert.Equal(2, seven.Rows);
    }

    [Fact]
    public void Biggest_tiles_first_a_few_windows_get_the_widest_tile()
    {
        var three = SwitcherLayout.Layout(3, 1920, 1080);
        Assert.Equal(SwitcherLayout.TileMax, three.TileW);
        Assert.Equal(1, three.Rows);
        Assert.False(three.Scroll);
    }

    [Fact]
    public void Forty_windows_on_1280x720_scroll_by_whole_rows()
    {
        var many = SwitcherLayout.Layout(40, 1280, 720);
        Assert.True(many.Scroll);
        Assert.Equal(7, many.Cols);
        Assert.Equal(4, many.VisibleRows);
        Assert.Equal(SwitcherLayout.TileMin, many.TileW);
    }

    [Fact]
    public void FirstRow_follows_the_selection_moving_as_little_as_possible()
    {
        var many = SwitcherLayout.Layout(40, 1280, 720);
        Assert.Equal(1, SwitcherLayout.FirstRow(many, 33, 0));   // below the window: pulled down just enough
        Assert.Equal(0, SwitcherLayout.FirstRow(many, 3, 1));    // above: pulled up
        Assert.Equal(1, SwitcherLayout.FirstRow(many, 20, 1));   // already visible: nothing moves
        Assert.Equal(2, SwitcherLayout.FirstRow(many, 39, 0));   // the very last tile shows the last rows
        Assert.Equal(0, SwitcherLayout.FirstRow(SwitcherLayout.Layout(14, 1920, 1080), 13, 0)); // a grid that fits never scrolls
        Assert.Equal(0, SwitcherLayout.FirstRow(null, 5, 3));
        Assert.Equal(0, SwitcherLayout.FirstRow(many, -1, 2));
    }

    [Fact]
    public void The_constants_are_Electrons()
    {
        Assert.Equal((18, 14, 8, 6, 30), (SwitcherLayout.Margin, SwitcherLayout.Pad, SwitcherLayout.Gap, SwitcherLayout.Inset, SwitcherLayout.CapH));
        Assert.Equal((240, 136, 4), (SwitcherLayout.TileMax, SwitcherLayout.TileMin, SwitcherLayout.Step));
        Assert.Equal(0.625, SwitcherLayout.Aspect);
        Assert.Equal((0.92, 0.84), (SwitcherLayout.MaxW, SwitcherLayout.MaxH));
        // 240 wide: a 228 x 142.5 preview rounds UP the way JS Math.round does (C#'s banker's rounding would give 142).
        var one = SwitcherLayout.Layout(1, 1920, 1080);
        Assert.Equal((228, 143, 6 + 143 + 30), (one.PreviewW, one.PreviewH, one.TileH));
        Assert.Equal((one.Inset, one.CapH, one.Pad, one.Gap, one.Margin), (6, 30, 14, 8, 18));
    }

    [Fact]
    public void The_empty_panel_and_the_notice_have_fixed_sizes()
    {
        var empty = SwitcherLayout.Layout(0, 1920, 1080);
        Assert.Equal(SwitcherGeometry.Empty, empty);
        Assert.Equal((380, 116, 416, 152), (empty.PanelW, empty.PanelH, empty.W, empty.H));
        var nt = SwitcherLayout.Notice();
        Assert.Equal((460, 104, 18, 496, 140), (nt.PanelW, nt.PanelH, nt.Margin, nt.W, nt.H));
    }

    [Fact]
    public void Nonsense_in_a_sane_answer_out()
    {
        Assert.Equal(0, SwitcherLayout.Layout(double.NaN, -5, double.NaN).Cols);
        Assert.Equal(3, SwitcherLayout.Layout(3, 0, 0).Cols);
        Assert.Equal(SwitcherLayout.Layout(3, 1280, 720), SwitcherLayout.Layout(3.9, double.NaN, -1));
        Assert.Equal(SwitcherLayout.Layout(3, 1280, 720), SwitcherLayout.Layout(3, double.PositiveInfinity, double.PositiveInfinity));
        Assert.True(SwitcherLayout.Layout(double.PositiveInfinity, 1920, 1080).Scroll);
        Assert.Equal(0, SwitcherLayout.Reflow(double.NaN, -5, double.NaN, null).Cols);
        Assert.Equal(3, SwitcherLayout.Reflow(3, 0, 0, SwitcherLayout.Layout(0, 1, 1)).Cols);
    }

    [Fact]
    public void Reflow_keeps_tiles_and_columns_while_the_list_changes()
    {
        const int W = 1920, H = 1040;
        var nine = SwitcherLayout.Layout(9, W, H);
        var ten = SwitcherLayout.Reflow(10, W, H, nine);
        Assert.True(ten.Cols == nine.Cols && ten.TileW == nine.TileW && ten.PanelW == nine.PanelW && ten.PanelH == nine.PanelH,
            "a 10th window fills the empty slot: " + ten.Key());
        var eleven = SwitcherLayout.Reflow(11, W, H, ten);
        Assert.True(eleven.Cols == nine.Cols && eleven.Rows == 3 && eleven.PanelW == nine.PanelW && eleven.PanelH > nine.PanelH,
            "an 11th adds a row, same width: " + eleven.Key());
        var four = SwitcherLayout.Reflow(4, W, H, nine);
        Assert.True(four.Cols == 4 && four.Rows == 1 && four.TileW == nine.TileW, "down to 4 is one row of 4 at the same tile size: " + four.Key());
        var none = SwitcherLayout.Reflow(0, W, H, nine);
        Assert.True(none.Cols == 0 && none.PanelW == SwitcherLayout.EmptyW, "to nothing is the empty panel");
        Assert.Equal(SwitcherLayout.Layout(3, W, H), SwitcherLayout.Reflow(3, W, H, SwitcherLayout.Layout(0, W, H)));
    }

    [Fact]
    public void Reflow_starts_over_when_the_old_grid_no_longer_fits_and_a_scrolling_grid_keeps_scrolling()
    {
        var small = SwitcherLayout.Layout(12, 1280, 720);
        Assert.Equal(SwitcherLayout.Layout(60, 1280, 720), SwitcherLayout.Reflow(60, 1280, 720, small));
        var many = SwitcherLayout.Layout(40, 1280, 720);
        var many2 = SwitcherLayout.Reflow(41, 1280, 720, many);
        Assert.True(many2.Scroll && many2.TileW == many.TileW && many2.Cols == many.Cols && many2.VisibleRows == many.VisibleRows,
            "a scrolling grid keeps scrolling by whole rows at its tile size: " + many2.Key());
        Assert.Equal(SwitcherLayout.Layout(5, 1280, 720), SwitcherLayout.Reflow(5, 1280, 720, null));
    }

    [Fact]
    public void Key_lists_the_fields_in_layout_js_order()
    {
        var t = SwitcherLayout.Layout(14, 1920, 1080);
        Assert.StartsWith($"14,{t.Cols},{t.Rows},{t.VisibleRows},0,{t.TileW},", t.Key());
        Assert.EndsWith($",{t.PanelW},{t.PanelH},{t.W},{t.H}", t.Key());
    }
}
