using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mantle.Core.Features.TaskView;
using Xunit;

namespace Mantle.Core.Tests.Features.TaskView;

// ui/taskview/layout.test.js, ported (TV-046 (first index), TV-047, TV-048, TV-051, TV-053, and the filter's matching rule).
public class TaskViewLayoutTests
{
    public static IEnumerable<object[]> Screens() => new[]
    {
        new object[] { 1536, 864 }, new object[] { 1920, 1080 }, new object[] { 1280, 720 },
        new object[] { 2560, 1440 }, new object[] { 1024, 768 }, new object[] { 1366, 768 },
    };

    [Fact]
    public void No_windows_no_rows_and_move_answers_minus_one()
    {
        var lay = TaskViewLayout.Layout(0, 1920, 1080);
        Assert.Equal(0, lay.Rows);
        Assert.Empty(TaskViewLayout.Place(lay, 0));
        Assert.Equal(-1, TaskViewLayout.Move(lay, 0, "ArrowRight"));
    }

    [Fact]
    public void One_window_is_the_biggest_tile_centred_both_ways()
    {
        var one = TaskViewLayout.Layout(1, 1920, 1080);
        var p = TaskViewLayout.Place(one, 0)[0];
        Assert.Equal(TaskViewLayout.TileMax, one.TileW);
        Assert.True(Math.Abs(p.X + one.TileW / 2.0 - 960) <= 1, "centred horizontally: " + p.X);
        Assert.True(Math.Abs(p.Y + one.TileH / 2.0 - (one.Area.Y + one.Area.H / 2)) <= 1, "centred vertically: " + p.Y);
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Every_count_from_1_to_60_keeps_the_invariants(int w, int h)
    {
        for (int n = 1; n <= 60; n++)
        {
            var lay = TaskViewLayout.Layout(n, w, h);
            string tag = $"{w}x{h} n={n}";
            var pos = TaskViewLayout.Place(lay, 0);
            var vis = pos.Where(q => q.Visible).ToList();
            string bad = "";
            if (lay.TileW > TaskViewLayout.TileMax || lay.TileW < TaskViewLayout.TileMin) bad = "tile width " + lay.TileW;
            if (bad == "" && lay.Cols * lay.Rows < n) bad = "grid too small";
            // every row full except the last, which has at least one; the fewest columns that give these rows
            for (int r = 0; r < lay.Rows && bad == ""; r++)
            {
                int k = TaskViewLayout.RowLen(lay, r);
                if (r < lay.Rows - 1 ? k != lay.Cols : k < 1) bad = $"row {r} has {k} of {lay.Cols}";
            }
            if (bad == "" && lay.Rows > 0 && lay.Cols > 1 && (int)Math.Ceiling(n / (double)(lay.Cols - 1)) == lay.Rows)
                bad = $"{lay.Cols} columns where {lay.Cols - 1} give the same rows";
            // whole visible rows inside the area, none off the sides
            foreach (var q in vis)
            {
                if (q.Y < lay.Area.Y - 0.5 || q.Y + lay.TileH > lay.Area.Y + lay.Area.H + 0.5) { bad = $"tile {q.I} outside the area vertically"; break; }
                if (q.X < lay.Area.X - 0.5 || q.X + lay.TileW > lay.Area.X + lay.Area.W + 0.5) { bad = $"tile {q.I} outside the area horizontally"; break; }
            }
            // no two visible tiles overlap
            for (int i = 0; i < vis.Count && bad == ""; i++)
                for (int j = i + 1; j < vis.Count; j++)
                {
                    var a = vis[i]; var b = vis[j];
                    if (a.X < b.X + lay.TileW && b.X < a.X + lay.TileW && a.Y < b.Y + lay.TileH && b.Y < a.Y + lay.TileH) { bad = $"tiles {a.I} and {b.I} overlap"; break; }
                }
            if (!lay.Scrolls && vis.Count != n && bad == "") bad = "not every tile visible though it fits";
            if (lay.Scrolls && vis.Count != lay.VisibleRows * lay.Cols && bad == "") bad = "scrolled view is not whole rows";
            Assert.True(bad == "", "invariants " + tag + ": " + bad);
        }
    }

    [Fact]
    public void Seven_windows_at_1920x1080_are_rows_of_4_and_3_at_the_biggest_tile()
    {
        var lay = TaskViewLayout.Layout(7, 1920, 1080);
        Assert.Equal((4, 2, 3), (lay.Cols, lay.Rows, TaskViewLayout.RowLen(lay, 1)));
        Assert.Equal(TaskViewLayout.TileMax, lay.TileW);
    }

    [Fact]
    public void Forty_windows_at_1280x720_scroll_and_the_selection_stays_shown()
    {
        var lay = TaskViewLayout.Layout(40, 1280, 720);
        Assert.True(lay.Scrolls && lay.VisibleRows >= 1);
        int first = TaskViewLayout.FirstRowFor(lay, 39, 0);
        Assert.Equal(lay.Rows - lay.VisibleRows, first);
        var pos = TaskViewLayout.Place(lay, first);
        Assert.True(pos[39].Visible);
        Assert.False(pos[0].Visible);
        Assert.Equal(-10000, pos[0].Y);
        Assert.Equal(0, TaskViewLayout.FirstRowFor(lay, 0, first));
        Assert.Equal(first, TaskViewLayout.FirstRowFor(lay, (first + 1) * lay.Cols, first));
        Assert.Equal(0, TaskViewLayout.FirstRowFor(TaskViewLayout.Layout(3, 1920, 1080), 2, 5));
    }

    [Fact]
    public void Arrow_keys_step_stop_at_the_edges_and_go_to_the_nearest_tile_in_x()
    {
        var lay = TaskViewLayout.Layout(7, 1920, 1080);   // rows of 4 and 3, centred
        Assert.Equal(1, TaskViewLayout.Move(lay, 0, "ArrowRight"));
        Assert.Equal(6, TaskViewLayout.Move(lay, 6, "ArrowRight"));
        Assert.Equal(0, TaskViewLayout.Move(lay, 0, "ArrowLeft"));
        Assert.Equal(4, TaskViewLayout.Move(lay, 3, "ArrowRight"));   // crosses rows like reading
        Assert.Equal(0, TaskViewLayout.Move(lay, 3, "Home"));
        Assert.Equal(6, TaskViewLayout.Move(lay, 2, "End"));
        Assert.Equal(4, TaskViewLayout.Move(lay, 0, "ArrowDown"));
        Assert.Equal(6, TaskViewLayout.Move(lay, 3, "ArrowDown"));
        var pos = TaskViewLayout.Place(lay, 0);
        double Cx(int i) => pos[i].X + lay.TileW / 2.0;
        int nearest = new[] { 0, 1, 2, 3 }.Aggregate(0, (b, i) => Math.Abs(Cx(i) - Cx(5)) < Math.Abs(Cx(b) - Cx(5)) - 0.5 ? i : b);
        Assert.Equal(nearest, TaskViewLayout.Move(lay, 5, "ArrowUp"));
        Assert.Equal(2, TaskViewLayout.Move(lay, 2, "ArrowUp"));
        Assert.Equal(5, TaskViewLayout.Move(lay, 5, "ArrowDown"));
        Assert.Equal(2, TaskViewLayout.Move(lay, 2, "x"));
        Assert.Equal(5, TaskViewLayout.Move(lay, 99, "ArrowLeft"));
    }

    [Fact]
    public void Type_to_filter_matches_every_word_any_case_in_list_order()
    {
        var items = new (string?, string?)[]
        {
            ("Inbox - Outlook", "olk"), ("mantle - Visual Studio Code", "Code"),
            ("Spotify Premium", "Spotify"), ("README.md - mantle", "notepad"),
        };
        Assert.Equal(4, TaskViewLayout.Filter(items, "").Count);
        Assert.Equal(new[] { 2 }, TaskViewLayout.Filter(items, "SPOTIFY"));
        Assert.Equal(new[] { 1 }, TaskViewLayout.Filter(items, "mantle code"));
        Assert.Equal(new[] { 3 }, TaskViewLayout.Filter(items, "notepad"));
        Assert.Empty(TaskViewLayout.Filter(items, "zzz"));
        Assert.Equal(new[] { 1, 3 }, TaskViewLayout.Filter(items, "mantle"));
        Assert.Equal(4, TaskViewLayout.Filter(items, null).Count);
    }

    [Fact]
    public void Layout_and_place_of_60_windows_is_well_under_a_millisecond()
    {
        for (int k = 0; k < 20; k++) TaskViewLayout.Place(TaskViewLayout.Layout(60, 1536, 864), 0);   // warm the JIT
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 200; k++) TaskViewLayout.Place(TaskViewLayout.Layout(60, 1536, 864), 0);
        double per = sw.Elapsed.TotalMilliseconds / 200;
        Assert.True(per < 1, $"layout + place, 60 windows: {per:0.000} ms");
    }
}
