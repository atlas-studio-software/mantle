using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.Desktop;
using Xunit;

namespace Mantle.Core.Tests.Features.Desktop;

/// <summary>DESK-091..103: which windows cover a widget (sidecar/test-widgethide.js), the answer, the throttle.</summary>
public class WidgetCoverTests
{
    const int Own = 4242;
    static readonly CoverArea Clock = new("clock", 40, 40, 460, 134), Stats = new("stats", 40, 145, 460, 270);
    static readonly IReadOnlyList<CoverArea> Areas = new[] { Clock, Stats };

    static CoverWin W(string title, int l, int t, int r, int b, string cls = "Notepad", int pid = 100) =>
        new() { Hwnd = l * 7 + t, Cls = cls, Title = title, Pid = pid, L = l, T = t, R = r, B = b };

    [Fact]
    public void What_covers_and_what_never_does_in_words()
    {
        Assert.Null(WidgetCover.Ignored(W("Notepad", 0, 0, 800, 600), Own));
        Assert.Equal("not visible", WidgetCover.Ignored(new CoverWin { Visible = false, R = 9, B = 9 }, Own));
        Assert.Equal("minimised", WidgetCover.Ignored(new CoverWin { Iconic = true, R = 9, B = 9 }, Own));
        Assert.StartsWith("cloaked", WidgetCover.Ignored(new CoverWin { Cloaked = true, R = 9, B = 9 }, Own));
        Assert.StartsWith("a tool window", WidgetCover.Ignored(new CoverWin { ExStyle = 0x80, R = 9, B = 9 }, Own));
        Assert.Equal("click-through overlay", WidgetCover.Ignored(new CoverWin { ExStyle = 0x20, R = 9, B = 9 }, Own));
        Assert.Equal("fully transparent", WidgetCover.Ignored(new CoverWin { Alpha = 0, R = 9, B = 9 }, Own));
        Assert.Null(WidgetCover.Ignored(new CoverWin { Alpha = 30, R = 9, B = 9 }, Own));   // a see-through app still covers
        foreach (var cls in new[] { "Progman", "WorkerW", "Shell_TrayWnd", "#32768", "tooltips_class32", "XamlExplorerHostIslandWindow" })
            Assert.StartsWith("Windows shell furniture", WidgetCover.Ignored(W("x", 0, 0, 99, 99, cls), Own));
        Assert.Equal("Mantle's own shell UI", WidgetCover.Ignored(W("Mantle top bar", 0, 0, 99, 99, pid: Own), Own));
        Assert.Null(WidgetCover.Ignored(W("Mantle Settings", 0, 0, 99, 99, pid: Own), Own));   // Mantle's APP windows cover
        Assert.Equal("zero size", WidgetCover.Ignored(W("x", 5, 5, 6, 99), Own));
    }

    [Fact]
    public void Each_widget_on_its_own_with_two_px_overlap_and_the_first_window_down_names_it()
    {
        var a = WidgetCover.Decide(Areas, new[] { W("Top", 300, 100, 900, 140), W("Under", 0, 0, 2000, 2000) }, Own);
        Assert.True(a.Is("clock")); Assert.True(a.Is("stats"));
        Assert.Equal("Top", a.By["clock"]); Assert.Equal("Under", a.By["stats"]);
        // one px of overlap is not a cover; two is
        Assert.False(WidgetCover.Decide(Areas, new[] { W("Edge", 459, 0, 900, 300) }, Own).Is("clock"));
        Assert.True(WidgetCover.Decide(Areas, new[] { W("Edge", 458, 0, 900, 300) }, Own).Is("clock"));
        // only the clock
        var c = WidgetCover.Decide(Areas, new[] { W("Small", 100, 50, 200, 100) }, Own);
        Assert.True(c.Is("clock")); Assert.False(c.Is("stats"));
        Assert.Equal("clock (by Small)", c.Words());
        Assert.Equal("clock=1,stats=0", c.Sig());
        // an untitled window is named by its class
        Assert.Equal("Chrome_WidgetWin_1", WidgetCover.Decide(Areas, new[] { W("", 0, 0, 999, 999, "Chrome_WidgetWin_1") }, Own).By["clock"]);
    }

    [Fact]
    public void Behind_the_desktop_another_monitor_minimised_and_Mantle_shell_never_cover()
    {
        var wins = new[] {
            W("Mantle top bar", 0, 0, 2000, 40, pid: Own),
            W("Minimised", 0, 0, 2000, 2000) , new CoverWin { Desktop = true, Cls = "Progman" },
            W("Behind", 0, 0, 2000, 2000),
        };
        wins[1].Iconic = true;
        var why = new List<string>();
        var a = WidgetCover.Decide(Areas, wins, Own, why);
        Assert.False(a.Is("clock")); Assert.False(a.Is("stats"));
        Assert.Equal("nothing", a.Words());
        Assert.Contains(why, l => l.EndsWith(": behind the desktop"));
        Assert.Contains(why, l => l.EndsWith(": Mantle's own shell UI"));
        Assert.Contains(why, l => l.EndsWith(": minimised"));
        // a window on a monitor to the right (virtual-screen px) cannot overlap the primary's widgets
        Assert.False(WidgetCover.Decide(Areas, new[] { W("Right", 1920, 0, 3840, 1080) }, Own).Is("clock"));
        // a monitor to the left has negative x
        Assert.False(WidgetCover.Decide(Areas, new[] { W("Left", -1920, 0, 0, 1080) }, Own).Is("clock"));
    }

    [Fact]
    public void Fails_open_unknown_is_nothing_covered_and_the_idle_reasons_in_order()
    {
        var open = CoverAnswer.Open(true, WidgetCover.NoAreas);
        Assert.False(open.Is("clock")); Assert.Equal("nothing", open.Words()); Assert.Equal("", open.Sig());
        // off: even a covered answer shows everything
        var off = new CoverAnswer(false, new Dictionary<string, bool> { ["clock"] = true }, new Dictionary<string, string?> { ["clock"] = "x" }, WidgetCover.Off);
        Assert.False(off.Is("clock"));
        Assert.Equal(WidgetCover.Off, WidgetCover.Idle(false, false, false, false, 0));
        Assert.Equal(WidgetCover.NoLayer, WidgetCover.Idle(true, false, true, true, 4));
        Assert.Equal(WidgetCover.NotShown, WidgetCover.Idle(true, true, false, true, 4));
        Assert.Equal(WidgetCover.WidgetsOff, WidgetCover.Idle(true, true, true, false, 4));
        Assert.Equal(WidgetCover.NoAreas, WidgetCover.Idle(true, true, true, true, 0));
        Assert.Null(WidgetCover.Idle(true, true, true, true, 4));
        Assert.Equal("switched off, so the widgets always show", WidgetCover.Off);
        Assert.Equal("the desktop layer is not open, so there are no widgets to hide", WidgetCover.NoLayer);
        Assert.StartsWith("the desktop widgets stay up under windows because Mantle could not watch the windows: ", WidgetCover.WatchFailed("x"));
    }

    [Fact]
    public void Areas_leave_out_panes_with_no_size()
    {
        var a = WidgetCover.Areas(new[] { ("clock", 40, 40, 420, 94), ("strip", 0, 0, 0, 0), ("mc", 40, 300, 420, 0) });
        Assert.Equal(new CoverArea("clock", 40, 40, 460, 134), a.Single());
    }

    [Fact]
    public void Throttle_decides_after_the_first_event_rides_a_burst_then_settles_once_then_nothing()
    {
        var t = new CoverThrottle();
        Assert.Equal(CoverThrottle.Next.Throttle, t.Event());   // arm one decision
        Assert.Equal(CoverThrottle.Next.None, t.Event());       // rides along
        Assert.Equal(CoverThrottle.Next.None, t.Event());
        Assert.Equal(CoverThrottle.Next.Throttle, t.Fired());   // events came while it waited: another decision
        Assert.Equal(CoverThrottle.Next.Settle, t.Fired());     // a quiet decision: one settle check
        Assert.Equal(CoverThrottle.Next.None, t.Settled());     // then nothing at all
        Assert.False(t.Waiting);
        Assert.Equal(CoverThrottle.Next.Throttle, t.Event());   // the next event starts it again
        Assert.True(WidgetCover.ThrottleMs <= 250 && WidgetCover.SettleMs >= 500);
    }

    [Fact]
    public void A_covered_clock_parks_only_once_its_fade_has_finished()
    {
        Assert.False(WidgetCover.Parked(true, false));   // still fading: the digits are visible, keep ticking
        Assert.True(WidgetCover.Parked(true, true));
        Assert.False(WidgetCover.Parked(false, true));
        Assert.Equal((260, 560), (WidgetCover.FadeOutMs, WidgetCover.FadeInMs));
    }
}
