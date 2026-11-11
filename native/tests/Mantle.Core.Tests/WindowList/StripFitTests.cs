using System;
using System.Linq;
using Mantle.Core.Features.WindowList;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

// Ported from the pure parts of ui/bar/bar.page.test.js "fit" (measure, keepFor, placeStrip, decide, applyOverflow):
// BAR-056, BAR-235..238 and the "+N" part of BAR-242.
public class StripFitTests
{
    static bool[] Pins(int n) => new bool[n];
    static bool[] PinsThenRunning(int pins, int running) => Enumerable.Range(0, pins + running).Select(i => i == pins && pins > 0).ToArray();

    [Fact]
    public void Widths_count_the_cell_the_gap_and_the_divider_once()
    {
        Assert.Equal(0, StripFit.Need(Pins(0)));
        Assert.Equal(36, StripFit.Need(Pins(1)));
        Assert.Equal(3 * 36 + 2 * 6, StripFit.Need(Pins(3)));
        // the hairline before the first running-only app: 13 DIP, once
        Assert.Equal(4 * 36 + 3 * 6 + 13, StripFit.Need(PinsThenRunning(2, 2)));
        // no hairline when nothing is pinned
        Assert.Equal(2 * 36 + 6, StripFit.Need(PinsThenRunning(0, 2)));
    }

    [Fact]
    public void Icons_that_fit_the_span_stay_centred_on_the_bar()
    {
        var room = StripFit.Room.Between(1000, 50, 700);   // span 58..692, centre 500
        var f = StripFit.Decide(Pins(5), room);
        Assert.Equal(5, f.Keep);
        Assert.Equal(0, f.Hidden);
        Assert.Equal(0, f.Shift);
    }

    [Fact]
    public void A_strip_that_fits_the_span_but_not_centred_slides_by_the_minimum()
    {
        // 10 icons = 414 DIP; centred on 500 it runs 293..707, past the wall's 692: slid 15 left, nothing hidden
        var room = StripFit.Room.Between(1000, 50, 700);
        var f = StripFit.Decide(Pins(10), room);
        Assert.Equal(10, f.Keep);
        Assert.Equal(0, f.Hidden);
        Assert.Equal(692 - 707, f.Shift, 6);
        // a wide left cluster pushes it right instead
        var left = StripFit.Room.Between(1000, 290, 990);
        var g = StripFit.Decide(Pins(10), left);
        Assert.Equal(298 - 293, g.Shift, 6);
    }

    [Fact]
    public void Apps_that_cannot_fit_the_whole_span_go_behind_plus_N_from_the_tail_and_none_is_cut_off()
    {
        var room = StripFit.Room.Between(1000, 50, 700);   // span 634
        var div = Pins(20);
        var f = StripFit.Decide(div, room);
        // room for the button: 634 - 42 = 592 -> 14 icons (14 * 36 + 13 * 6 = 582)
        Assert.Equal(14, f.Keep);
        Assert.Equal(6, f.Hidden);
        Assert.Equal(582 + 6 + 36, f.Width);
        Assert.True(f.Width <= room.Span, "the strip with its button is wider than the span");
        // filled from the span's left edge
        Assert.Equal(room.SpanL, room.Centre - f.Width / 2 + f.Shift, 6);
        Assert.Equal("+6", StripFit.Label(f.Hidden));
        Assert.Equal("6 more apps that do not fit", StripFit.Tip(f.Hidden));
        Assert.Equal("1 more app that do not fit", StripFit.Tip(1));
    }

    [Fact]
    public void The_divider_counts_toward_what_fits()
    {
        // 13 pins + 7 running: the hairline's 13 DIP leaves one fewer on the strip than 20 pins at the edge of the room
        var room = StripFit.Room.Between(1000, 50, 700);
        Assert.Equal(14, StripFit.Keep(Pins(20), room.Span));
        Assert.Equal(13, StripFit.Keep(PinsThenRunning(13, 7), room.Span - 4));
        Assert.Equal(14, StripFit.Keep(PinsThenRunning(14, 7), room.Span));
    }

    [Fact]
    public void A_room_too_small_for_one_icon_and_the_button_keeps_only_the_button()
    {
        var f = StripFit.Decide(Pins(3), StripFit.Room.Between(200, 60, 120));   // span 44
        Assert.Equal(0, f.Keep);
        Assert.Equal(3, f.Hidden);
        Assert.Equal(StripFit.Cell, f.Width);
        Assert.Equal(new StripFit.Fit(0, 0, 0, 0), StripFit.Decide(Pins(0), StripFit.Room.Between(200, 60, 120)));
    }

    [Fact]
    public void The_panel_is_rebuilt_only_when_what_it_lists_changes()
    {
        TaskItem P(string id, params TaskWindow[] ws) => new(id, id, "pinned", null, null, null, "none", ws);
        var a = new[] { P("a"), P("b"), P("c") };
        Assert.Equal("b|c|", StripFit.Sig(a, 1));
        // a title change behind +N is not a change of what it lists
        var b = new[] { P("a"), P("b", new TaskWindow("1", 1, "one", false, false)), P("c") };
        var c = new[] { P("a"), P("b", new TaskWindow("1", 1, "two", false, false)), P("c") };
        Assert.Equal(StripFit.Sig(b, 1), StripFit.Sig(c, 1));
        Assert.NotEqual(StripFit.Sig(a, 1), StripFit.Sig(b, 1));   // it started running: its line changes
        var d = new[] { P("a"), P("b", new TaskWindow("1", 1, "two", true, false)), P("c") };
        Assert.NotEqual(StripFit.Sig(c, 1), StripFit.Sig(d, 1));   // it became the active one
        Assert.Equal("", StripFit.Sig(a, 3));
        // an app on the strip changing is not the panel's business
        Assert.Equal(StripFit.Sig(a, 2), StripFit.Sig(new[] { P("a", new TaskWindow("1", 1, "x", true, false)), P("b"), P("c") }, 2));
    }

    [Fact]
    public void The_panel_has_up_to_4_columns_of_76_and_fewer_apps_make_it_narrower()
    {
        Assert.Equal(1, StripFit.Columns(1));
        Assert.Equal(3, StripFit.Columns(3));
        Assert.Equal(4, StripFit.Columns(9));
        Assert.Equal(76 + 24, StripFit.PanelSize(1).W);
        Assert.Equal(4 * 76 + 3 * 2 + 24, StripFit.PanelSize(9).W);
        Assert.Equal(3 * 64 + 2 * 2 + 24, StripFit.PanelSize(9).H);
    }

    [Fact]
    public void A_drag_moves_only_among_the_icons_on_the_strip_never_into_the_plus_N_tail()
    {
        // 5 pins and 4 running, 7 on the strip: the pins are all on it, two of the running ones are behind +N
        Assert.Equal(5, StripFit.OnStrip(0, 5, 7));
        Assert.Equal(2, StripFit.OnStrip(5, 4, 7));
        // 3 kept: two pins wait behind it, and no running app is on the strip at all
        Assert.Equal(3, StripFit.OnStrip(0, 5, 3));
        Assert.Equal(0, StripFit.OnStrip(5, 4, 3));
        // all of them fit
        Assert.Equal(4, StripFit.OnStrip(5, 4, 9));
    }
}
