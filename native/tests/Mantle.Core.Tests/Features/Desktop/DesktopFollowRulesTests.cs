using System.Threading.Tasks;
using Mantle.Core.Features.Corners;
using Mantle.Core.Features.Desktop;
using Xunit;

namespace Mantle.Core.Tests.Features.Desktop;

// Round 3 of the desktop lane: the menu keys (DESK-118..120), the 1 s fallback words (DESK-124), the display-change fit
// (DESK-016), the remake words (DESK-011..013), the panes' line heights, and the corner's quick-settings answer (CORN-054).
public class DesktopFollowRulesTests
{
    const int Up = DeskMenu.VkUp, Down = DeskMenu.VkDown, Enter = DeskMenu.VkReturn;
    const int S = DeskMenu.NavSwallow, R = DeskMenu.NavReport, H = DeskMenu.NavHeld;

    [Fact]
    public void Menu_keys_are_the_menus_only_bare_only_while_open_and_their_ups_go_with_their_downs()
    {
        Assert.Equal("up", DeskMenu.NavKey(Up)); Assert.Equal("end", DeskMenu.NavKey(DeskMenu.VkEnd)); Assert.Null(DeskMenu.NavKey(0x41));
        Assert.Equal(0, DeskMenu.NavVerdict(0x41, true, false, false, false, true, false));          // not a menu key
        Assert.Equal(S | R | H, DeskMenu.NavVerdict(Down, true, false, false, false, true, false));   // a bare Down: the menu's
        Assert.Equal(S | R | H, DeskMenu.NavVerdict(Down, true, false, false, false, true, true));    // autorepeat walks the menu
        Assert.Equal(S | H, DeskMenu.NavVerdict(Enter, true, false, false, false, true, true));       // Enter: its first down only
        Assert.Equal(0, DeskMenu.NavVerdict(Down, true, true, false, false, true, false));            // Shift+Down is the app's
        Assert.Equal(0, DeskMenu.NavVerdict(Down, true, false, true, false, true, false));            // a Mantle window in front
        Assert.Equal(0, DeskMenu.NavVerdict(Down, true, false, false, false, false, false));          // no menu open
        Assert.Equal(R | H, DeskMenu.NavVerdict(Down, true, false, false, true, true, false));         // passive: seen, not eaten
        Assert.Equal(S, DeskMenu.NavVerdict(Down, false, false, false, false, false, true));          // an eaten down's up, even after close
        Assert.Equal(0, DeskMenu.NavVerdict(Down, false, false, false, false, true, false));          // an up whose down was not ours
    }

    [Fact]
    public void Menu_keys_move_one_highlight_and_Enter_with_none_closes()
    {
        Assert.Equal(0, DeskMenu.Step(-1, 5, "down")); Assert.Equal(4, DeskMenu.Step(-1, 5, "up"));
        Assert.Equal(0, DeskMenu.Step(4, 5, "down")); Assert.Equal(4, DeskMenu.Step(0, 5, "up"));
        Assert.Equal(0, DeskMenu.Step(3, 5, "home")); Assert.Equal(4, DeskMenu.Step(1, 5, "end"));
        Assert.Equal("close", DeskMenu.EnterVerdict(-1, 5)); Assert.Equal("run", DeskMenu.EnterVerdict(2, 5)); Assert.Equal("close", DeskMenu.EnterVerdict(5, 5));
    }

    [Fact]
    public void The_fallback_is_said_and_a_stand_down_is_a_health_row()
    {
        var a = DeskMenu.Fallback("Mantle did not answer within 1000 ms", true, false, null);
        Assert.Equal("Mantle did not answer within 1000 ms; Windows' own menu opened instead", a.Line);
        Assert.Null(a.Health);
        var b = DeskMenu.Fallback("", false, true, null);
        Assert.Equal("the helper gave a right-click to Windows; the click was not given back to Windows", b.Line);
        Assert.Equal("right-clicks on the desktop go to Windows for now", b.Health);
        Assert.Equal("two misses in a row", DeskMenu.Fallback("x", true, true, "two misses in a row").Health);
        Assert.Equal(1000, DeskMenu.AckMs); Assert.Equal(30000, DeskMenu.StandDownRetryMs);
    }

    [Fact]
    public void A_display_change_fits_only_a_live_layer_that_is_in_place()
    {
        Assert.Equal("skip", DesktopLayer.FitVerdict(false, false, false, false).Act);
        var t = DesktopLayer.FitVerdict(true, true, false, false);
        Assert.Equal("skip", t.Act); Assert.Contains("a test run", t.Reason);
        Assert.Equal("skip", DesktopLayer.FitVerdict(true, false, true, false).Act);
        Assert.Equal("later", DesktopLayer.FitVerdict(true, false, false, true).Act);
        Assert.Equal("fit", DesktopLayer.FitVerdict(true, false, false, false).Act);
        Assert.Equal(400, DesktopLayer.DisplayQuietMs);
    }

    [Fact]
    public void The_layer_is_checked_against_the_display_at_its_scale()
    {
        var ok = DesktopLayer.PageFill(1536, 864, 1.25, 1920, 1080, 1.25);
        Assert.True(ok.Ok); Assert.Equal("the desktop layer fills the main display (1536x864 at scale 1.25)", ok.Reason);
        Assert.True(DesktopLayer.PageFill(1537, 863, 1.25, 1920, 1080, 1.25).Ok);   // one DIP of rounding is a fill
        var old = DesktopLayer.PageFill(1536, 864, 1.0, 1920, 1080, 1.25);
        Assert.False(old.Ok); Assert.Contains("(it fills the screen but draws at the old scale)", old.Reason);
        var small = DesktopLayer.PageFill(1280, 720, 1.5, 2560, 1440, 1.5);
        Assert.False(small.Ok); Assert.Equal("the desktop layer is 1280x720 at scale 1.5 but the main display is 1706.67x960 at scale 1.5", small.Reason);
        Assert.Null(DesktopLayer.PageFill(0, 0, 1, 1920, 1080, 1).Ok);
        Assert.Null(DesktopLayer.PageFill(10, 10, 1, 0, 0, 1).Ok);
    }

    [Fact]
    public void The_remake_and_Explorer_back_words()
    {
        Assert.Equal("Explorer is back (TaskbarCreated, 1 this run): a test run never touches the shell, so nothing is re-attached", DesktopLayer.ShellBackLine("TaskbarCreated", 1, true));
        Assert.Equal("Explorer is back (TaskbarCreated, 2 this run): putting the desktop layer back under the icons", DesktopLayer.ShellBackLine("TaskbarCreated", 2, false));
        Assert.Contains("after Explorer restarted (6 tries: no Progman)", DesktopLayer.GaveUp(6, "no Progman", null));
        Assert.StartsWith("the desktop layer could not go under the icons at start (6 tries: no reason given)", DesktopLayer.GaveUp(6, null, "at start"));
        // the 6 back-off steps add up to ~50 s, and a layer made again 5 times in ten minutes is not made a 6th time
        Assert.Equal(49500, System.Linq.Enumerable.Sum(DesktopLayer.AttachBackoffMs));
        var v = DesktopLayer.Verdict(false, false, new long[] { 1, 2, 3, 4, 5 }, 6);
        Assert.False(v.Again); Assert.Contains("closed 6 times in ten minutes", v.Reason);
        Assert.Equal("the desktop layer closed while Windows was put back; restart Mantle to bring it back", DesktopLayer.Verdict(false, true, null, 1).Reason);
    }

    [Theory]
    [InlineData(13, 1)]   // Chromium 14 + 3 = 17, WinUI 18: the System values and the short window's date
    [InlineData(14, 0)]   // 15 + 4 = 19 both: the tall window's date
    [InlineData(12, 0)]
    [InlineData(11, 0)]
    public void The_panes_take_Chromiums_line_height(double px, double trim) => Assert.Equal(trim, DesktopLayer.LineTrim(px));

    [Fact]
    public async Task A_corner_says_when_the_bar_does_not_answer_and_clips_its_reason()
    {
        var never = new TaskCompletionSource<CornerFx>().Task;
        var late = await CornerQs.Within(never, 50, false);
        Assert.False(late.Ok); Assert.Equal("the bar did not answer within 50 ms", late.Reason);
        var bad = await CornerQs.Within(Task.FromResult(new CornerFx(false, "line one\r\nline two " + new string('x', 300))), 1000, false);
        Assert.StartsWith("line one line two x", bad.Reason); Assert.Equal(CornerQs.ReasonMax, bad.Reason!.Length); Assert.EndsWith("…", bad.Reason);
        var ok = await CornerQs.Within(Task.FromResult(new CornerFx(true, Closed: true)), 1000, false);
        Assert.True(ok.Ok); Assert.True(ok.Closed);
        Assert.Equal(1500, CornerQs.AckMs);
    }
}
