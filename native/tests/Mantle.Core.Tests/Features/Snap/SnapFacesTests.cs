using System.Linq;
using Mantle.Core.Features.Snap;
using Xunit;

namespace Mantle.Core.Tests.Features.Snap;

// lane r9-snap: the snap window's faces as numbers (SNAP-058, 059, 067, 070, 071), for the surface to come. DIP in, DIP
// out; every expected value is worked out by hand beside it.
public class SnapFacesTests
{
    static readonly SnapBox Work125 = SnapBox.Xywh(0, 0, 1536, 832);   // 1920x1040 physical at 125 %

    [Fact]
    public void The_notice_is_440_by_132_centred_on_the_work_area_72_above_its_bottom()
    {
        // x = (1536-440)/2 = 548; y = 832-132-72 = 628
        Assert.Equal(SnapBox.Xywh(548, 628, 440, 132), SnapFaces.NoticeRect(Work125));
        // an odd width rounds half up like Math.round: (1441-440)/2 = 500.5 -> 501; at -1441, -940.5 -> -940
        Assert.Equal(501, SnapFaces.NoticeRect(SnapBox.Xywh(0, 0, 1441, 800)).Left);
        Assert.Equal(-940, SnapFaces.NoticeRect(SnapBox.Xywh(-1441, 0, 1441, 800)).Left);
    }

    [Fact]
    public void A_long_notice_grows_upward_to_its_text_but_never_past_260_or_shorter()
    {
        SnapBox r = SnapBox.Xywh(548, 628, 440, 132);
        Assert.Equal(SnapBox.Xywh(548, 560, 440, 200), SnapFaces.NoticeGrow(r, 200));   // 628 - (200-132)
        Assert.Equal(SnapBox.Xywh(548, 500, 440, 260), SnapFaces.NoticeGrow(r, 400));   // capped: 628 - 128
        Assert.Equal(r, SnapFaces.NoticeGrow(r, 100));                                  // never shorter
        Assert.Equal(r, SnapFaces.NoticeGrow(r, 2500));                                 // a nonsense measure is ignored
        Assert.Equal(r.Bottom, SnapFaces.NoticeGrow(r, 230).Bottom);                    // the bottom stays put
    }

    [Fact]
    public void A_notice_stays_4_5_s_or_2_s_plus_55_ms_a_character_at_most_9_s()
    {
        Assert.Equal(4500, SnapFaces.NoticeMs("", ""));
        Assert.Equal(4500, SnapFaces.NoticeMs(null, null));
        Assert.Equal(4530, SnapFaces.NoticeMs(new string('a', 20), new string('b', 26)));   // 2000 + 46*55
        Assert.Equal(7500, SnapFaces.NoticeMs(new string('a', 50), new string('b', 50)));   // 2000 + 100*55
        Assert.Equal(9000, SnapFaces.NoticeMs(new string('a', 100), new string('b', 100)));
    }

    [Fact]
    public void The_picker_opens_under_the_maximize_button_inside_the_work_area()
    {
        // x = 200+1000-400-24 = 776, y = 120+28 = 148
        Assert.Equal(SnapBox.Xywh(776, 148, 400, 268), SnapFaces.PickerRect(SnapBox.Xywh(200, 120, 1000, 700), Work125));
        // off the top-left: clamped to 0,0
        Assert.Equal(SnapBox.Xywh(0, 0, 400, 268), SnapFaces.PickerRect(SnapBox.Xywh(-300, -50, 500, 400), Work125));
        // off the right and the bottom: x = 1536-400 = 1136, y = 832-268 = 564
        Assert.Equal(SnapBox.Xywh(1136, 564, 400, 268), SnapFaces.PickerRect(SnapBox.Xywh(1300, 700, 600, 400), Work125));
    }

    [Fact]
    public void Five_layouts_three_a_row_with_their_grids_cells_and_names()
    {
        Assert.Equal(3, SnapFaces.LayoutCols);
        Assert.Equal(new[] { "halves", "thirds", "wide", "quarters", "columns" }, SnapFaces.Layouts.Select(l => l.Id).ToArray());
        Assert.Equal(new[] { "Halves", "Thirds", "Two thirds and one third", "Quarters", "Four columns" }, SnapFaces.Layouts.Select(l => l.Label).ToArray());
        Assert.Equal(new[] { "2x1", "3x1", "3x1", "2x2", "4x1" }, SnapFaces.Layouts.Select(l => l.Cols + "x" + l.Rows).ToArray());
        SnapLayout wide = SnapFaces.ById("wide")!;
        Assert.Equal(new[] { 0, 0, 2, 1 }, wide.Zones[0]);
        Assert.Equal(new[] { 2, 0, 1, 1 }, wide.Zones[1]);
        Assert.Equal(new[] { "left two thirds", "right third" }, wide.Names.ToArray());
        Assert.Equal("bottom-right quarter", SnapFaces.ById("quarters")!.Names[3]);
        Assert.Equal("fourth column", SnapFaces.ById("columns")!.Names[3]);
        Assert.Null(SnapFaces.ById("nope"));
        // every layout's zones tile its grid exactly (each cell covered once)
        foreach (SnapLayout l in SnapFaces.Layouts) {
            var cover = new int[l.Cols, l.Rows];
            foreach (int[] z in l.Zones)
                for (int x = z[0]; x < z[0] + z[2]; x++)
                    for (int y = z[1]; y < z[1] + z[3]; y++) cover[x, y]++;
            Assert.True(cover.Cast<int>().All(c => c == 1), l.Id + " does not tile its grid");
            Assert.Equal(l.Zones.Count, l.Names.Count);
        }
    }

    [Fact]
    public void After_a_pick_the_rest_of_the_layout_is_what_Snap_Assist_fills_next_in_reading_order()
    {
        var rest = SnapFaces.Rest("thirds", 0);
        Assert.Equal(new[] { 1, 2 }, rest.Select(z => z.Index).ToArray());
        Assert.Equal(new[] { "middle third", "right third" }, rest.Select(z => z.Name).ToArray());
        Assert.Equal(new[] { 2, 0, 1, 1 }, rest[1].Cell);
        Assert.Equal(3, rest[1].Cols);
        Assert.Empty(SnapFaces.Rest("nope", 0));
    }

    [Fact]
    public void Picker_keys_walk_every_zone_in_reading_order_and_jump_a_row_of_three_layouts()
    {
        Assert.Equal((0, 1), SnapFaces.StepZone(0, 0, 1));
        Assert.Equal((1, 0), SnapFaces.StepZone(0, 1, 1));    // halves' last zone -> thirds' first
        Assert.Equal((0, 0), SnapFaces.StepZone(4, 3, 1));    // the last zone wraps to the first
        Assert.Equal((4, 3), SnapFaces.StepZone(0, 0, -1));   // and back
        Assert.Equal((3, 1), SnapFaces.StepRow(0, 1, 1));     // halves -> quarters, zone 1 kept
        Assert.Equal((4, 2), SnapFaces.StepRow(1, 2, 1));     // thirds -> columns, zone 2 kept
        Assert.Equal((0, 1), SnapFaces.StepRow(3, 3, -1));    // quarters' zone 3 -> halves' last zone (clamped)
        Assert.Null(SnapFaces.StepRow(2, 1, 1));              // no row below "wide"
        Assert.Null(SnapFaces.StepRow(0, 0, -1));             // no row above
    }
}
