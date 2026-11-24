using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Features.Switcher;
using Xunit;

namespace Mantle.Core.Tests.Features.Switcher;

// lib/switcher.js's session half (onOpen, sizeParked, presize, painted, maybeReveal, the stuck-overlay guard,
// endVisual, commit/cancel/activated, pick, onReflow) against a fake overlay. Electron has no unit test of any of
// these (the rows cite "none", the live rehearsal or the page test): PEND-012, native tests added.
// SWI-044, 045, 046, 047, 050, 051, 052, 057, 066, 077 (index half), 086 (gate half), 089, 092, 106, 109, 111, 137, 140.
public class SwitcherSessionsTests
{
    sealed class FakeOverlay : ISwitcherOverlay
    {
        public bool Alive { get; set; } = true;
        public double Scale = 1;
        public string? PlaceFails;
        public readonly List<string> Calls = new();
        public readonly List<(string Name, string Json)> Sent = new();
        public int SyncCount;
        public string? Place(int x, int y, int w, int h, bool show)
        {
            Calls.Add($"place {x},{y} {w}x{h}{(show ? " show" : "")}");
            return PlaceFails;
        }
        public void Send(string name, object payload) { Calls.Add("send " + name); Sent.Add((name, JsonSerializer.Serialize(payload))); }
        public ThumbResult SyncThumbs(IReadOnlyList<PreviewBox> boxes, double dpr, int originX, int originY)
        {
            SyncCount++;
            Calls.Add($"thumbs {boxes.Count} @{originX},{originY} x{dpr}");
            return new ThumbResult(true, null, boxes.Count);
        }
        public void ClearThumbs(string why) => Calls.Add("clear " + why);
        public double ScaleFor(PhysRect mon) => Scale;
        public IReadOnlyList<PhysRect> Displays { get; set; } = new[] { new PhysRect(0, 0, 1920, 1080) };
        public PhysRect PrimaryMon => new(0, 0, 1920, 1080);
        public PhysRect PrimaryWork => new(0, 0, 1920, 1040);
    }

    static (FakeOverlay W, SwitcherSessions S, List<string> Log, List<(long Seq, int Cols)> Grid) Make(bool test = false)
    {
        var w = new FakeOverlay();
        var log = new List<string>();
        var grid = new List<(long, int)>();
        return (w, new SwitcherSessions(w, test, (seq, cols) => grid.Add((seq, cols)), log.Add), log, grid);
    }

    static string Hw(int i) => (1000 + i).ToString();

    static JsonElement OpenMsg(long seq, int n, double keyAt = 0, double ms = 0, string? mon = null, string? work = null, int sel = 1)
    {
        var items = string.Join(",", Enumerable.Range(0, n).Select(i => $"{{\"hwnd\":{Hw(i)},\"title\":\"w{i}\",\"pid\":{i + 1},\"minimized\":false,\"hung\":false}}"));
        string json = $"{{\"op\":\"open\",\"seq\":{seq},\"keyAt\":{keyAt},\"ms\":{ms},\"sel\":{sel},\"current\":0,\"list\":[{items}]" +
            (mon is null ? "" : $",\"mon\":{mon}") + (work is null ? "" : $",\"work\":{work}") + "}";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    static PreviewBox?[] Boxes(int n) => Enumerable.Range(0, n).Select(i => (PreviewBox?)new PreviewBox(Hw(i), 10 + i * 100, 10, 90, 60)).ToArray();

    [Fact]
    public void Open_lays_out_in_DIP_sizes_the_parked_window_in_px_and_hands_page_and_grid_the_session()
    {
        var (w, s, _, grid) = Make();
        w.Scale = 1.5;
        var sess = s.Open(OpenMsg(7, 5, mon: "{\"x\":0,\"y\":0,\"w\":2880,\"h\":1620}", work: "{\"x\":0,\"y\":0,\"w\":2880,\"h\":1560}"), 1000, true, null);
        var lay = SwitcherLayout.Layout(5, 2880 / 1.5, 1560 / 1.5);
        Assert.Equal(lay.Key(), sess.Lay.Key());
        int pw = (int)Math.Round(lay.W * 1.5), ph = (int)Math.Round(lay.H * 1.5);
        // Centred in the work area (SWI-066), parked beside the same monitor (SWI-044).
        Assert.Equal(new PhysRect((int)Math.Round((2880 - pw) / 2.0), (int)Math.Round((1560 - ph) / 2.0), pw, ph), sess.Rect);
        Assert.Equal(SwitcherWords.ParkSpot(sess.Mon, pw, ph, w.Displays), sess.Park);
        Assert.Contains($"place {sess.Park.X},{sess.Park.Y} {pw}x{ph}", w.Calls);
        Assert.Equal("send switcher.session", w.Calls.Last());
        Assert.Equal(new[] { (7L, lay.Cols) }, grid);
        Assert.Null(sess.Undrawn);
        Assert.Null(sess.Shown);
    }

    [Fact]
    public void A_new_monitor_is_sized_twice_the_same_one_once()
    {
        var (w, s, _, _) = Make();
        s.Open(OpenMsg(1, 3), 0, true, null);
        Assert.Equal(2, w.Calls.Count(c => c.StartsWith("place ") && !c.EndsWith("show")));
        s.Cancel(1, "Esc", 10);
        w.Calls.Clear();
        s.Open(OpenMsg(2, 3), 20, true, null);
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("place ")));
    }

    [Fact]
    public void Undrawn_without_a_window_a_ready_page_or_in_a_test_run_and_the_sidecar_still_owns_the_switch()
    {
        var (w, s, _, grid) = Make();
        w.Alive = false;
        Assert.Equal("there is no switcher window", s.Open(OpenMsg(1, 2), 0, true, null).Undrawn);
        w.Alive = true;
        Assert.Equal("the switcher page has not loaded yet", s.Open(OpenMsg(2, 2), 0, false, "the switcher page has not loaded yet").Undrawn);
        var (w2, t, _, grid2) = Make(test: true);
        Assert.Equal("test mode", t.Open(OpenMsg(3, 2), 0, true, null).Undrawn);
        Assert.Empty(w2.Calls.Where(c => c.StartsWith("place ")));
        Assert.Empty(grid2);
        Assert.Empty(grid);
    }

    [Fact]
    public void Shows_100_ms_after_the_key_or_at_paint_if_later_in_one_place_and_a_tap_never_shows()
    {
        var (w, s, _, _) = Make();
        var sess = s.Open(OpenMsg(1, 3, keyAt: 990), 1000, true, null);
        Assert.Equal(1090.0, sess.DueAt);
        Assert.Null(s.Painted(1, sess.WinW, sess.WinH, Boxes(3), 1, 1010));
        Assert.Null(sess.Shown);                                 // painted, not due
        s.Tick(1089, () => true);
        Assert.Null(sess.Shown);
        s.Tick(1090, () => true);
        Assert.Equal((double?)1090, sess.Shown);
        Assert.Single(w.Calls, c => c.EndsWith(" show"));
        Assert.Equal($"place {sess.Rect.X},{sess.Rect.Y} 0x0 show", w.Calls.Single(c => c.EndsWith(" show")));

        // Due first, painted later: shown at the paint.
        var b = s.Open(OpenMsg(2, 3, keyAt: 2000), 2000, true, null);
        s.Tick(2150, () => true);
        Assert.Null(b.Shown);
        s.Painted(2, b.WinW, b.WinH, Boxes(3), 1, 2200);
        Assert.Equal((double?)2200, b.Shown);

        // A tap: let go before 100 ms, never on screen.
        var tap = s.Open(OpenMsg(3, 3, keyAt: 3000), 3000, true, null);
        s.Painted(3, tap.WinW, tap.WinH, Boxes(3), 1, 3010);
        Assert.True(s.Commit(3, "alt-up", 3050));
        s.Tick(3200, () => false);
        Assert.Null(tap.Shown);
    }

    [Fact]
    public void Previews_go_in_while_parked_before_the_reveal_and_a_wrong_size_report_waits()
    {
        var (w, s, _, _) = Make();
        var sess = s.Open(OpenMsg(1, 3, keyAt: 0, ms: 200), 0, true, null);
        Assert.Equal("stale", s.Painted(9, sess.WinW, sess.WinH, Boxes(3), 1, 1));
        Assert.StartsWith("waiting: the page is ", s.Painted(1, sess.WinW + 40, sess.WinH, Boxes(3), 1, 1));
        Assert.StartsWith("waiting: ", s.Painted(1, double.NaN, sess.WinH, Boxes(3), 1, 1));
        Assert.Equal(0, w.SyncCount);
        Assert.Null(s.Painted(1, sess.WinW + 1, sess.WinH, Boxes(3), 1, 2));
        int thumbs = w.Calls.FindIndex(c => c.StartsWith("thumbs 3 @" + sess.Park.X + "," + sess.Park.Y));
        int shown = w.Calls.FindIndex(c => c.EndsWith(" show"));
        Assert.True(thumbs >= 0 && shown > thumbs, string.Join(" | ", w.Calls));
        Assert.True(s.PageDrew);
    }

    [Fact]
    public void Boxes_for_windows_outside_this_Alt_Tab_never_reach_the_previews()
    {
        var (w, s, _, _) = Make();
        var sess = s.Open(OpenMsg(1, 2, ms: 200), 0, true, null);
        var boxes = new PreviewBox?[] { new(Hw(0), 0, 0, 10, 10), new("424242", 0, 0, 10, 10), new(Hw(1), 0, 0, 10, 10) };
        s.Painted(1, sess.WinW, sess.WinH, boxes, 1, 1);
        Assert.Contains(w.Calls, c => c.StartsWith("thumbs 2 "));
    }

    [Fact]
    public void A_rehearsal_or_a_test_run_stops_one_step_short_of_the_screen()
    {
        var (w, s, _, grid) = Make();
        var r = s.Open(OpenMsg(800000, 3, ms: 200), 0, true, null, rehearsal: "warm-up");
        s.Painted(800000, r.WinW, r.WinH, Boxes(3), 1, 1);
        s.Tick(500, () => true);
        Assert.Null(r.Shown);
        Assert.Empty(grid);                                      // a rehearsal never touches the keys
        Assert.DoesNotContain(w.Calls, c => c.EndsWith(" show"));
    }

    [Fact]
    public void End_clears_previews_first_then_parks_only_if_it_moved_then_empties_the_page()
    {
        var (w, s, _, _) = Make();
        var sess = s.Open(OpenMsg(1, 3, ms: 200), 0, true, null);
        s.Painted(1, sess.WinW, sess.WinH, Boxes(3), 1, 1);
        w.Calls.Clear();
        Assert.True(s.Commit(1, "alt-up", 300));
        Assert.Equal(new[] { "clear the Alt+Tab ended (switching (alt-up))", $"place {sess.Park.X},{sess.Park.Y} 0x0", "send switcher.end" }, w.Calls);
        Assert.Same(sess, s.Acting);
        Assert.Null(s.Current);

        // A quick tap never left its parking spot: no park call.
        var t = s.Open(OpenMsg(2, 3, ms: 0), 400, true, null);
        w.Calls.Clear();
        s.Cancel(2, "Esc", 410);
        Assert.Equal(new[] { "clear the Alt+Tab ended (Esc)", "send switcher.end" }, w.Calls);
        Assert.NotNull(t.Ended);
    }

    [Fact]
    public void A_new_Alt_Tab_pushes_the_old_aside_and_a_late_call_for_it_takes_nothing()
    {
        var (w, s, log, _) = Make();
        var a = s.Open(OpenMsg(1, 3), 0, true, null);
        var b = s.Open(OpenMsg(2, 4), 10, true, null);
        Assert.NotNull(a.Ended);
        Assert.Contains("clear the Alt+Tab ended (a new Alt+Tab started before the last one ended)", w.Calls);
        w.Calls.Clear();
        Assert.False(s.End(a, "late", 20));
        Assert.False(s.Commit(1, "alt-up", 20));
        Assert.Equal("stale", s.Painted(1, a.WinW, a.WinH, Boxes(3), 1, 20));
        Assert.False(s.Move(1, 2, "Tab"));
        Assert.Empty(w.Calls);
        Assert.Same(b, s.Current);
        s.Cancel(1, "Esc", 30);
        Assert.Equal("#1: closed without a switch (Esc)", log.Last());
        Assert.Same(b, s.Current);
    }

    [Fact]
    public void The_page_that_has_not_drawn_after_1500_ms_is_logged_once()
    {
        var (_, s, log, _) = Make();
        s.Open(OpenMsg(5, 2, ms: 200), 0, true, null);
        s.Tick(1499, () => true);
        Assert.DoesNotContain(log, l => l.Contains("has not drawn"));
        s.Tick(1500, () => true);
        s.Tick(1600, () => true);
        Assert.Single(log, l => l == "#5: the page has not drawn this Alt+Tab after 1500 ms; letting go of Alt still switches (the sidecar does it)");
    }

    [Fact]
    public void Stuck_overlay_guard_takes_it_down_600_ms_after_Alt_went_up_with_no_word_and_switches_nothing()
    {
        var (w, s, log, grid) = Make();
        var sess = s.Open(OpenMsg(1, 3, ms: 200), 0, true, null);
        s.Painted(1, sess.WinW, sess.WinH, Boxes(3), 1, 0);
        Assert.Equal((double?)0, sess.Shown);
        bool alt = true;
        for (double t = 40; t <= 2000; t += 40) s.Tick(t, () => alt);
        Assert.Same(sess, s.Current);                            // Alt held: stays up
        alt = false;
        double end = -1;
        for (double t = 2040; t <= 4000 && end < 0; t += 40) { s.Tick(t, () => alt); if (s.Current is null) end = t; }
        Assert.InRange(end, 2040 + 600, 2040 + 600 + 2 * SwitcherSessions.AltPollMs);
        Assert.Contains(log, l => l.StartsWith("#1: Alt has been up for 600 ms and the sidecar never said this Alt+Tab ended"));
        Assert.Null(s.Acting);                                   // nothing was switched from here
        Assert.Single(grid);                                     // the open's grid only: no key was sent
        Assert.Contains("clear the Alt+Tab ended (the sidecar went quiet)", w.Calls);
    }

    [Fact]
    public void Stuck_overlay_guard_waits_when_Alt_cannot_be_read_and_resets_when_Alt_goes_down_again()
    {
        var (_, s, _, _) = Make();
        var sess = s.Open(OpenMsg(1, 3, ms: 200), 0, true, null);
        s.Painted(1, sess.WinW, sess.WinH, Boxes(3), 1, 0);
        for (double t = 40; t <= 3000; t += 40) s.Tick(t, () => null);
        Assert.Same(sess, s.Current);
        for (double t = 3040; t <= 3400; t += 40) s.Tick(t, () => false);
        for (double t = 3440; t <= 3600; t += 40) s.Tick(t, () => true);
        for (double t = 3640; t <= 4000; t += 40) s.Tick(t, () => false);
        Assert.Same(sess, s.Current);
        for (double t = 4040; t <= 4400; t += 40) s.Tick(t, () => throw new InvalidOperationException("no key state"));
        Assert.Same(sess, s.Current);
    }

    [Fact]
    public void An_undrawn_or_unshown_session_is_never_taken_down_by_the_guard()
    {
        var (_, s, _, _) = Make();
        var sess = s.Open(OpenMsg(1, 3, ms: 200), 0, true, null);
        for (double t = 40; t <= 3000; t += 40) s.Tick(t, () => false);
        Assert.Same(sess, s.Current);
    }

    [Fact]
    public void Activated_logs_without_title_or_handle_and_says_when_a_notice_is_due()
    {
        var (_, s, log, _) = Make();
        s.Open(OpenMsg(1, 3, keyAt: 0, ms: 5), 0, true, null);
        s.Commit(1, "alt-up", 50);
        Assert.False(s.Activated(new ActivatedEvent(1, true, "alt-up", "direct", 12)));
        Assert.StartsWith("#1: switched (alt-up, direct, 12 ms) | key->open 5 ms", log.Last());
        Assert.DoesNotContain(Hw(0), log.Last());
        s.Open(OpenMsg(2, 3), 100, true, null);
        s.Commit(2, "alt-up", 150);
        Assert.True(s.Activated(new ActivatedEvent(2, false, "alt-up", Cause: "refused", Reason: "window 1234 refused")));
        Assert.Contains("the window refused", log.Last());
        s.Open(OpenMsg(3, 0), 200, true, null);
        s.Commit(3, "alt-up", 250);
        Assert.False(s.Activated(new ActivatedEvent(3, false, "alt-up", Cause: "refused", Reason: "x")));   // empty list: no failure
    }

    [Fact]
    public void A_click_goes_to_where_its_window_is_now_and_a_closed_one_is_refused_in_words()
    {
        var (_, s, _, _) = Make();
        s.Open(OpenMsg(1, 3), 0, true, null);
        Assert.Equal((2, (string?)null), s.PickIndex(1, 0, Hw(2)));
        Assert.Equal((-1, "that window closed just before the click arrived"), s.PickIndex(1, 0, "999"));
        Assert.Equal((1, (string?)null), s.PickIndex(9, 1, Hw(0)));   // another session: the sidecar decides
        Assert.Equal(-1, s.PickIndex(1, -1, null).Index);
    }

    [Fact]
    public void Reflow_keeps_tiles_grows_never_shrinks_resends_changed_columns_and_logs_counts_only()
    {
        var (w, s, log, grid) = Make();
        var sess = s.Open(OpenMsg(1, 2, ms: 200), 0, true, null);
        double w0 = sess.WinW, h0 = sess.WinH;
        int cols0 = sess.Lay.Cols;
        grid.Clear();
        Assert.True(s.Reflow(OpenMsg(1, 9)));
        Assert.True(sess.WinW >= w0 && sess.WinH >= h0);
        Assert.Equal(9, sess.List.Count);
        Assert.Equal("#1: the list changed while it was up: 7 opened, 0 closed, 9 now" + (sess.WinW > w0 || sess.WinH > h0 ? "; the window grew to fit" : ""), log.Last());
        Assert.DoesNotContain("w3", log.Last());
        if (sess.Lay.Cols != cols0) Assert.Equal(sess.Lay.Cols, Assert.Single(grid).Cols);
        else Assert.Empty(grid);
        double w1 = sess.WinW, h1 = sess.WinH;
        Assert.True(s.Reflow(OpenMsg(1, 1)));
        Assert.Equal((w1, h1), (sess.WinW, sess.WinH));            // never shrinks mid-session
        Assert.Equal("switcher.windows", w.Sent.Last().Name);
        Assert.False(s.Reflow(OpenMsg(2, 3)));                       // another Alt+Tab's update is ignored
    }

    [Fact]
    public void Presize_keeps_the_parked_window_ready_only_while_nothing_is_up()
    {
        var (w, s, log, _) = Make();
        Assert.True(s.Presize(6, true).Done);
        var sz = s.Size;
        Assert.False(s.Presize(6, true).Done);                       // same size: nothing to do
        Assert.False(s.Presize(6, false).Done);
        s.NoticeUp = true;
        Assert.False(s.Presize(12, true).Done);
        s.NoticeUp = false;
        s.Open(OpenMsg(1, 2), 0, true, null);
        Assert.False(s.Presize(12, true).Done);
        s.Cancel(1, "Esc", 1);
        w.PlaceFails = "Windows refused";
        Assert.Equal((true, "Windows refused"), s.Presize(14, true));
        Assert.Equal((true, "Windows refused"), s.Presize(14, true));
        Assert.Single(log, l => l == "the switcher window could not be sized ahead of the next Alt+Tab: Windows refused");
        Assert.NotNull(sz);
        Assert.False(SwitcherSessions.PresizeWanted(false, true, true, false));
        Assert.False(SwitcherSessions.PresizeWanted(false, false, false, false));
        Assert.False(SwitcherSessions.PresizeWanted(false, false, true, true));
        Assert.False(SwitcherSessions.PresizeWanted(true, false, true, false));
        Assert.True(SwitcherSessions.PresizeWanted(false, false, true, false));
    }

    [Fact]
    public void EndAll_takes_down_what_is_up_and_forgets_the_acting_one()
    {
        var (w, s, _, _) = Make();
        s.Open(OpenMsg(1, 2), 0, true, null);
        s.Commit(1, "alt-up", 1);
        s.Open(OpenMsg(2, 2), 2, true, null);
        Assert.True(s.EndAll("Mantle is quitting", 3));
        Assert.Null(s.Current);
        Assert.Null(s.Acting);
        Assert.Contains("clear the Alt+Tab ended (Mantle is quitting)", w.Calls);
        Assert.False(s.EndAll("again", 4));
    }

    [Fact]
    public void A_failed_place_is_logged_and_never_blocks_the_session()
    {
        var (w, s, log, _) = Make();
        w.PlaceFails = "access denied";
        var sess = s.Open(OpenMsg(1, 2, ms: 200), 0, true, null);
        Assert.Equal("the switcher window could not be sized (access denied)", sess.Undrawn);
        Assert.True(s.Commit(1, "alt-up", 5));                       // the sidecar's switch goes on
        Assert.Same(sess, s.Acting);
    }

    [Fact]
    public void Items_are_read_as_normItem_does()
    {
        var m = JsonDocument.Parse("{\"list\":[{\"hwnd\":\"12\",\"title\":null,\"pid\":\"x\",\"hung\":true},{\"hwnd\":34,\"minimized\":true},7]}").RootElement;
        var l = SwitcherSessions.Items(m);
        Assert.Equal(new SwitcherItem("12", "", 0, false, true), l[0]);
        Assert.Equal(new SwitcherItem("34", "", 0, true, false), l[1]);
        Assert.Equal("undefined", l[2].Hwnd);
        Assert.Empty(SwitcherSessions.Items(JsonDocument.Parse("[]").RootElement));
        Assert.Null(SwitcherSessions.Rect(JsonDocument.Parse("{\"mon\":{\"x\":0,\"y\":0,\"w\":0,\"h\":5}}").RootElement, "mon"));
    }
}
