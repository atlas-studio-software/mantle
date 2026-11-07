using System.Linq;
using Mantle.Core.Features.Toasts;
using Xunit;

namespace Mantle.Core.Tests.Features.Toasts;

// lib/features/_toasts-core.test.js, ported (TST-035..TST-039, TST-044, TST-053..TST-055, TST-065, TST-067..TST-069).
public class ToastsCoreTests
{
    const double Now = 1760500000000;
    static IncomingToast T(long id, double? time = Now - 1000) => new(id, "com.owensilva.helm", "Helm", $"T{id}", "b", time);

    [Fact]
    public void The_picker_banners_only_new_fresh_ids_after_a_baseline()
    {
        var p = new BannerPicker();
        Assert.Empty(p.Next(true, new[] { T(1), T(2) }, Now));                     // the first list is only the baseline
        var n = p.Next(true, new[] { T(3), T(1), T(2) }, Now);
        Assert.Single(n); Assert.Equal(3, n[0].Id);
        Assert.Empty(p.Next(true, new[] { T(3), T(1), T(2) }, Now));               // the same list again: nothing
        Assert.Empty(p.Next(false, new IncomingToast[0], Now));                    // a failed read changes nothing
        Assert.Empty(p.Next(true, new[] { T(3), T(1), T(2) }, Now));               // no burst after a failed read
        Assert.Empty(p.Next(true, new[] { T(3) }, Now));
        Assert.Single(p.Next(true, new[] { T(3), T(1) }, Now));                    // gone from a good read, then back = new
        Assert.Empty(p.Next(true, new[] { T(3), T(1), T(9, Now - ToastsCore.FreshMs - 5000) }, Now));   // older than FRESH_MS
        var noTime = p.Next(true, new[] { T(3), T(1), T(10, 0) }, Now);
        Assert.Single(noTime); Assert.Equal(10, noTime[0].Id);                     // a toast with no time is bannered
        p.Reset();
        Assert.Empty(p.Next(true, new[] { T(3), T(1), T(11) }, Now));              // after reset: a baseline again
    }

    [Fact]
    public void A_banner_carries_only_clean_known_fields()
    {
        var b = ToastsCore.BannerOf(new IncomingToast(5, "x", "  Helm  ", "Done   in 14s", "line one\n line two", Now, Icon: "data:image/png;base64,AA",
            Logo: "javascript:alert(1)", Hero: "data:image/png;base64,BB", Scenario: "reminder", Opens: "protocol"), Now);
        Assert.Equal("Helm", b.App);
        Assert.Equal("Done in 14s", b.Title); Assert.Equal("line one line two", b.Body);
        Assert.Null(b.Logo); Assert.Equal("data:image/png;base64,AA", b.Icon);
        Assert.Equal("data:image/png;base64,BB", b.Image);                          // hero stands in for image
        Assert.True(b.Sticky); Assert.Equal("protocol", b.Opens);
        Assert.Equal("An app", ToastsCore.BannerOf(new IncomingToast(1), Now).App);
        Assert.True(ToastsCore.BannerOf(new IncomingToast(1, Scenario: "IncomingCall"), Now).Sticky);   // case-blind
        Assert.False(ToastsCore.BannerOf(new IncomingToast(1), Now).Sticky);
        Assert.Equal("app", ToastsCore.BannerOf(new IncomingToast(1, Opens: "odd"), Now).Opens);
        Assert.Equal(300, ToastsCore.BannerOf(new IncomingToast(1, Title: new string('a', 400)), Now).Title!.Length);
        Assert.Equal("<img src=x>", ToastsCore.BannerOf(new IncomingToast(1, Title: "<img src=x>"), Now).Title);   // plain text, kept as text
    }

    [Fact]
    public void Quiet_reasons_follow_windows()
    {
        Assert.Equal("Do not disturb is on", ToastsCore.QuietWhy(true, 1, 5));
        Assert.Contains("alarms", ToastsCore.QuietWhy(true, 2, 5));
        Assert.Contains("game", ToastsCore.QuietWhy(true, 0, 3));
        Assert.Contains("locked", ToastsCore.QuietWhy(true, 0, 1));
        Assert.Null(ToastsCore.QuietWhy(true, 0, 5));
        Assert.Null(ToastsCore.QuietWhy(true, null, null));
        Assert.Null(ToastsCore.QuietWhy(false, 1, 3));                             // an unreadable state is NOT quiet
    }

    [Fact]
    public void A_click_opens_the_link_the_window_or_the_list()
    {
        var link = ToastsCore.BannerOf(new IncomingToast(1, Opens: "protocol"), Now);
        var app = ToastsCore.BannerOf(new IncomingToast(1), Now);
        Assert.Equal("link", ToastsCore.ClickPlan(link, "9").Act);
        Assert.Equal(("window", "123"), ToastsCore.ClickPlan(app, "123"));
        Assert.Equal("list", ToastsCore.ClickPlan(app, null).Act);
        Assert.Equal("list", ToastsCore.ClickPlan(ToastsCore.BannerOf(new IncomingToast(1, Opens: "none"), Now), null).Act);
    }

    static readonly SpotRect Work = new(0, 32, 1536, 832);   // 1920x1080 at 1.25x under a 32 DIP top bar
    static readonly SpotRect Pill = new(208, 802, 1120, 62);

    [Fact]
    public void The_column_sits_bottom_right_and_rises_above_whatever_reaches_the_corner()
    {
        var s0 = ToastsCore.Spot(Work, 392, 8, null, null);
        Assert.Equal((1536 - 392, 40, 864 - 40, 392), (s0.X, s0.Y, s0.Height, s0.Width)); Assert.False(s0.Hide); Assert.Empty(s0.Under);
        var s1 = ToastsCore.Spot(Work, 392, 8, Pill, null);
        Assert.Equal(802, s1.Y + s1.Height); Assert.Equal("the bar", string.Join(",", s1.Under));
        var s2 = ToastsCore.Spot(new SpotRect(0, 32, 2048, 1120), 392, 8, new SpotRect(464, 1090, 1120, 62), null);
        Assert.Equal(1152, s2.Y + s2.Height); Assert.Empty(s2.Under);                // a pill that does not reach the corner
        var s3 = ToastsCore.Spot(Work, 392, 8, new SpotRect(208, 802 - 420, 1120, 62 + 420), null);
        Assert.Equal(382, s3.Y + s3.Height); Assert.False(s3.Hide);                  // an open bar pop-up there
        var card = new SpotRect(1536 - 312, 802 - 122, 312, 122, "the screenshot card");
        var s4 = ToastsCore.Spot(Work, 392, 8, Pill, new[] { card });
        Assert.Equal(680, s4.Y + s4.Height); Assert.Equal("the bar,the screenshot card", string.Join(",", s4.Under));
        var s5 = ToastsCore.Spot(Work, 392, 8, null, new[] { card with { Y = 864 - 98, Height = 98 } });
        Assert.Equal(766, s5.Y + s5.Height);
        var s6 = ToastsCore.Spot(Work, 392, 8, new SpotRect(208, 802 - 640, 1120, 702), null);
        Assert.True(s6.Hide); Assert.Matches("DIP left above the bar", s6.Why);       // less than MIN_ROOM: held back
        var s7 = ToastsCore.Spot(Work, 392, 8, new SpotRect(208, 32, 1120, 62), null);
        Assert.Equal(864, s7.Y + s7.Height); Assert.False(s7.Hide);                  // a top-edge bar is not under the corner
        var s8 = ToastsCore.Spot(Work, 392, 8, null, new[] { new SpotRect(0, 700, 300, 164, "left") });
        Assert.Equal(864, s8.Y + s8.Height);                                         // another column does not move them
    }
}
