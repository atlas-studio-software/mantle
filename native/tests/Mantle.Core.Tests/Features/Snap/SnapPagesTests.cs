using Mantle.Core.Features.Snap;
using Xunit;

namespace Mantle.Core.Tests.Features.Snap;

// lane r9-snap: the snap page's model (ui/snap/snap.js keydown, #stepZone, #stepRow, #layoutAssist, #placeTiles,
// #selectTile, #buildPicker, #buildAssist, #buildNotice, snap.thumbs, snap.nokeys; ui/snap/index.html), pure:
// SNAP-060, 061, 068, 069, 071, 077, 082..085, 088, 089. Hand arithmetic; nothing is drawn.
public class SnapPagesTests
{
    const string Dot = " · ";

    // ------------------------------------------------------------ the picker's keys (SNAP-071)

    [Fact]
    public void Picker_digits_1_to_5_select_that_layouts_first_zone_and_other_digits_are_not_the_pickers()
    {
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 0, 0), SnapPages.PickerKey("1", false, 3, 2));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 2, 0), SnapPages.PickerKey("3", false, 0, 1));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 4, 0), SnapPages.PickerKey("5", false, 0, 0));
        Assert.Equal(SnapKeyAct.None, SnapPages.PickerKey("6", false, 0, 0).Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.PickerKey("9", false, 0, 0).Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.PickerKey("0", false, 0, 0).Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.PickerKey("12", false, 0, 0).Act);
    }

    [Fact]
    public void Picker_Enter_or_Space_snaps_the_selected_zone_Esc_closes_and_other_keys_go_on()
    {
        Assert.Equal(new SnapKeyed(SnapKeyAct.Pick, 1, 2), SnapPages.PickerKey("Enter", false, 1, 2));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Pick, 4, 3), SnapPages.PickerKey(" ", false, 4, 3));
        Assert.Equal(SnapKeyAct.Dismiss, SnapPages.PickerKey("Escape", false, 1, 2).Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.PickerKey("a", false, 1, 2).Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.PickerKey("Home", false, 1, 2).Act);
    }

    [Fact]
    public void Picker_arrows_and_Tab_walk_the_zones_wrapping_and_Down_Up_jump_a_row_of_three_layouts()
    {
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 1, 0), SnapPages.PickerKey("ArrowRight", false, 0, 1));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 1, 0), SnapPages.PickerKey("Tab", false, 0, 1));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 4, 3), SnapPages.PickerKey("Tab", true, 0, 0));      // 15 zones: wraps back to the last
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 0, 0), SnapPages.PickerKey("ArrowLeft", false, 0, 1));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 3, 1), SnapPages.PickerKey("ArrowDown", false, 0, 1)); // halves -> quarters
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, 1, 2), SnapPages.PickerKey("ArrowUp", false, 4, 3));   // columns -> thirds, zone clamped
        Assert.Equal(new SnapKeyed(SnapKeyAct.Stay, 3, 0), SnapPages.PickerKey("ArrowDown", false, 3, 0));   // no row below
        Assert.Equal(new SnapKeyed(SnapKeyAct.Stay, 1, 0), SnapPages.PickerKey("ArrowUp", false, 1, 0));     // no row above
    }

    // ------------------------------------------------------------ the notice's keys and words (SNAP-060, 061)

    [Fact]
    public void A_notice_closes_on_Esc_Enter_or_Space_and_its_title_falls_back_to_could_not_snap()
    {
        Assert.Equal(SnapKeyAct.Dismiss, SnapPages.NoticeKey("Escape").Act);
        Assert.Equal(SnapKeyAct.Dismiss, SnapPages.NoticeKey("Enter").Act);
        Assert.Equal(SnapKeyAct.Dismiss, SnapPages.NoticeKey(" ").Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.NoticeKey("ArrowLeft").Act);
        Assert.Equal("Could not snap", SnapPages.NoticeTitle(null));
        Assert.Equal("Could not snap", SnapPages.NoticeTitle(""));
        Assert.Equal("Could not snap Notes", SnapPages.NoticeTitle("Could not snap Notes"));
    }

    [Fact]
    public void A_page_that_cannot_start_says_which_half_is_missing()
    {
        Assert.Equal(("The snap page cannot work", "window.mantle is missing (preload.js did not run)."), SnapPages.Broken(bridge: false));
        Assert.Equal(("The snap page cannot work", "layouts.js did not load."), SnapPages.Broken(bridge: true));
    }

    // ------------------------------------------------------------ the picker's words (SNAP-068, 069)

    [Fact]
    public void The_picker_says_who_it_is_for_names_each_group_and_zone_and_the_footer_follows_the_selection()
    {
        Assert.Equal("Snap layouts", SnapPages.PickerTitle);
        Assert.Equal("for code.exe", SnapPages.PickerFor("code.exe", "plan.txt - Code"));
        Assert.Equal("for Notes", SnapPages.PickerFor(null, "Notes"));
        Assert.Equal("", SnapPages.PickerFor("", null));
        Assert.Equal("1: Halves", SnapPages.GroupLabel(0));
        Assert.Equal("4: Quarters", SnapPages.GroupLabel(3));
        Assert.Equal("Two thirds and one third, left two thirds", SnapPages.ZoneLabel(2, 0));
        Assert.Equal("Thirds: right third" + Dot + "Enter snaps" + Dot + "Esc closes", SnapPages.PickerFoot(1, 2));
        Assert.Equal("Arrows choose a zone" + Dot + "Enter snaps" + Dot + "Esc closes", SnapPages.PickerHint);
        Assert.Equal((0, 0), SnapPages.PickerOpensOn);                    // the page ignores main's start (snap.js)
    }

    // ------------------------------------------------------------ Snap Assist's words (SNAP-077, 082, 083, 088, 089)

    [Fact]
    public void Snap_Assist_names_the_zone_and_how_many_follow_and_its_footer_says_only_Esc_with_nothing_to_offer()
    {
        Assert.Equal("Snap Assist", SnapPages.AssistTitle);
        Assert.Equal("choose a window for the middle third (1 more after this)", SnapPages.AssistZoneLine("middle third", 1));
        Assert.Equal("choose a window for the right half", SnapPages.AssistZoneLine("right half", 0));
        Assert.Equal("choose a window for the this space", SnapPages.AssistZoneLine(null, 0));
        Assert.Equal("Arrows choose a window" + Dot + "Enter puts it here" + Dot + "Esc closes", SnapPages.AssistFootFor(2));
        Assert.Equal("Esc closes", SnapPages.AssistFootFor(0));
        Assert.Equal("No other windows to put here", SnapPages.NoOthers);
    }

    [Fact]
    public void A_tile_caption_falls_back_to_the_exe_then_untitled_and_the_badge_is_the_first_letter()
    {
        Assert.Equal("Inbox", SnapPages.Caption("Inbox", "mail.exe"));
        Assert.Equal("mail.exe", SnapPages.Caption("", "mail.exe"));
        Assert.Equal("Untitled window", SnapPages.Caption(null, null));
        Assert.Equal("· minimized", SnapPages.MinimizedTag);
        Assert.Equal("M", SnapPages.Letter("mail.exe", "Inbox"));
        Assert.Equal("I", SnapPages.Letter(null, "inbox"));
        Assert.Equal("?", SnapPages.Letter(null, null));
        Assert.Equal("?", SnapPages.Letter(".EXE", null));
        Assert.True(SnapPages.IsIcon("data:image/png;base64,AA"));
        Assert.False(SnapPages.IsIcon("https://example.invalid/icon.png"));
        Assert.False(SnapPages.IsIcon(null));
        Assert.Equal(2, SnapPages.StartSel(5, 3));
        Assert.Equal(0, SnapPages.StartSel(0, 0));
        Assert.Equal(0, SnapPages.StartSel(-1, 4));
    }

    [Fact]
    public void A_picture_that_could_not_be_drawn_says_why_and_the_keyboard_warning_has_a_fallback()
    {
        Assert.Equal("", SnapPages.TileWhy(true, "ignored"));
        Assert.Equal("no picture", SnapPages.TileWhy(false, null));
        Assert.Equal("the window is cloaked", SnapPages.TileWhy(false, "the window is cloaked"));
        Assert.Equal("No live pictures: DWM is off", SnapPages.NoPictures("DWM is off"));
        Assert.Equal("No live pictures: unknown reason", SnapPages.NoPictures(""));
        Assert.Equal(SnapPanel.NoKeys, SnapPages.NoKeysFoot(SnapPanel.NoKeys));
        Assert.Equal("The keyboard is on another window: click instead.", SnapPages.NoKeysFoot(null));
    }

    // ------------------------------------------------------------ Snap Assist's keys (SNAP-085)

    [Fact]
    public void Snap_Assist_keys_move_through_the_tiles_without_wrapping_and_Up_stops_at_the_top()
    {
        // 7 windows, 3 a row.
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 3), SnapPages.AssistKey("ArrowRight", false, 2, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 3), SnapPages.AssistKey("Tab", false, 2, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 6), SnapPages.AssistKey("ArrowRight", false, 6, 7, 3));  // the last stays
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 3), SnapPages.AssistKey("ArrowLeft", false, 4, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 0), SnapPages.AssistKey("Tab", true, 0, 7, 3));          // the first stays
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 6), SnapPages.AssistKey("ArrowDown", false, 5, 7, 3));   // clamped to the last
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 1), SnapPages.AssistKey("ArrowUp", false, 4, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Stay, Sel: 2), SnapPages.AssistKey("ArrowUp", false, 2, 7, 3));       // the top row: stays
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 0), SnapPages.AssistKey("Home", false, 5, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Select, Sel: 6), SnapPages.AssistKey("End", false, 1, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Pick, Sel: 4), SnapPages.AssistKey("Enter", false, 4, 7, 3));
        Assert.Equal(new SnapKeyed(SnapKeyAct.Pick, Sel: 4), SnapPages.AssistKey(" ", false, 4, 7, 3));
        Assert.Equal(SnapKeyAct.Dismiss, SnapPages.AssistKey("Escape", false, 4, 7, 3).Act);
        Assert.Equal(SnapKeyAct.None, SnapPages.AssistKey("x", false, 4, 7, 3).Act);
        // nothing to offer: the keys are taken and do nothing, Esc still closes
        Assert.Equal(SnapKeyAct.Stay, SnapPages.AssistKey("ArrowRight", false, 0, 0, 3).Act);
        Assert.Equal(SnapKeyAct.Stay, SnapPages.AssistKey("Enter", false, 0, 0, 3).Act);
        Assert.Equal(SnapKeyAct.Dismiss, SnapPages.AssistKey("Escape", false, 0, 0, 3).Act);
    }

    // ------------------------------------------------------------ Snap Assist's tiles (SNAP-083, 084)

    [Fact]
    public void Tiles_are_300_wide_when_they_fit_and_shrink_by_10_until_whole_rows_fit()
    {
        Assert.Equal(180, SnapPages.PreviewH(300));
        Assert.Equal(143, SnapPages.PreviewH(240));                         // 142.5 rounds up, as JavaScript does
        // 4 windows in 1000x600: 300 wide, 3 a row, 2 rows of 180 + 12 + 28, centred.
        Assert.Equal(new SnapTiles(300, 220, 3, 2, 2, 42, 76), SnapPages.Layout(4, 1000, 600));
        // 12 windows: 4 rows at 250 do not fit 600; at 240 four fit a row and 3 rows do.
        Assert.Equal(new SnapTiles(240, 183, 4, 3, 3, 8, 17), SnapPages.Layout(12, 1000, 600));
    }

    [Fact]
    public void Too_many_windows_stop_at_150_wide_and_page_by_whole_rows_with_a_more_hint()
    {
        SnapTiles t = SnapPages.Layout(40, 1000, 400);
        Assert.Equal(new SnapTiles(150, 126, 6, 7, 3, 30, 3), t);
        Assert.Equal(SnapBox.Xywh(30, 3, 150, 126), SnapPages.Tile(0, 0, t));
        Assert.Equal(SnapBox.Xywh(188, 137, 150, 126), SnapPages.Tile(7, 0, t));
        Assert.Null(SnapPages.Tile(18, 0, t));                              // row 3 is off view
        Assert.Equal(SnapBox.Xywh(30, 271, 150, 126), SnapPages.Tile(18, 1, t));

        Assert.Equal((18, 1, true), SnapPages.SelectTile(18, 0, 40, 0, t, scroll: true));
        Assert.Equal((0, 0, true), SnapPages.SelectTile(0, 18, 40, 1, t, scroll: true));
        Assert.Equal((5, 0, false), SnapPages.SelectTile(5, 0, 40, 0, t, scroll: true));
        Assert.Equal((39, 0, false), SnapPages.SelectTile(99, 0, 40, 0, t, scroll: false));  // hover: no paging
        Assert.Equal((3, 2, false), SnapPages.SelectTile(0, 3, 0, 2, t, scroll: true));      // nothing to select

        Assert.Equal("22 more below", SnapPages.MoreHint(40, 0, t));
        Assert.Equal("more above" + Dot + "10 more below", SnapPages.MoreHint(40, 2, t));
        Assert.Equal("more above" + Dot, SnapPages.MoreHint(40, 5, t));    // snap.js keeps the separator
        Assert.Null(SnapPages.MoreHint(4, 0, SnapPages.Layout(4, 1000, 600)));
    }
}
