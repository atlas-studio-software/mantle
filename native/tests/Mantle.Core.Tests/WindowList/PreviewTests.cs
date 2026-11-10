using System;
using System.Linq;
using Mantle.Core.Features.WindowList;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

// The pure half of the window previews: ui/bar/bar.js#flyBuild / #flyRefresh / #flyCard and lib/thumbs.js#toPhysical,
// #hrWords, CLOAK_WHO, #prune.
public class PreviewTests
{
    static TaskWindow W(string h, string title = "t", bool min = false) => new(h, 1, title, false, min);
    static TaskItem App(params TaskWindow[] ws) => new("running:a", "A", "running", @"c:\a.exe", null, null, "x", ws);

    [Fact]
    public void Cards_are_full_size_while_they_fit_then_shrink_in_8_px_steps_then_the_list_says_why()
    {
        // 1280 DIP bar window: avail 1246; two full cards need 2*212+6 = 430
        var f = Previews.FitCards(2, 1280);
        Assert.True(f.Cards); Assert.Equal(200, f.ShotW); Assert.Equal(112, f.ShotH);
        // six full cards need 6*212+30 = 1302 > 1246: shrink to the first width that fits (192: 6*204+30 = 1254 > 1246; 184: 1206)
        f = Previews.FitCards(6, 1280);
        Assert.True(f.Cards); Assert.Equal(184, f.ShotW); Assert.Equal(Math.Round(184 * 112 / 200.0), f.ShotH);
        // nine at the 128 floor need 9*140+48 = 1308 > 1246: the list, with the reason
        f = Previews.FitCards(9, 1280);
        Assert.False(f.Cards);
        Assert.Equal("9 windows are too many to preview side by side in this bar, so this is the list", f.Why);
        // eight at 128 need 8*140+42 = 1162: still cards
        Assert.True(Previews.FitCards(8, 1280).Cards);
    }

    [Fact]
    public void The_flyout_is_sized_from_the_cards_as_bar_css_lays_them_out()
    {
        Assert.Equal(160, Previews.CardHeight(112));
        Assert.Equal(212, Previews.CardWidth(200));
        var (w, h) = Previews.FlyoutSize(2, 200, 112);
        Assert.Equal(2 * 212 + 6 + 16, w); Assert.Equal(176, h);
    }

    [Fact]
    public void A_picture_is_letterboxed_in_its_box_and_never_squashed()
    {
        // a wide window in a 250x140 box (200x112 DIP at 1.25): full width, centred vertically
        var r = Previews.Letterbox(10, 20, 250, 140, 1000, 400);
        Assert.Equal((10, 40, 260, 140), r);
        // a tall window: full height, centred horizontally
        r = Previews.Letterbox(0, 0, 200, 112, 300, 600);
        Assert.Equal((72, 0, 128, 112), r);
        // no source size: the whole box
        Assert.Equal((5, 5, 105, 55), Previews.Letterbox(5, 5, 100, 50, 0, 0));
    }

    [Fact]
    public void Every_dwm_failure_and_cloak_is_said_in_words()
    {
        Assert.Contains("E_HANDLE", Previews.HrWords(unchecked((int)0x80070006)));
        Assert.Contains("E_INVALIDARG", Previews.HrWords(unchecked((int)0x80070057)));
        Assert.Contains("composition is off", Previews.HrWords(unchecked((int)0x80263001)));
        Assert.Contains("E_ACCESSDENIED", Previews.HrWords(unchecked((int)0x80070005)));
        Assert.Equal("DWM refused (HRESULT 0x80004005)", Previews.HrWords(unchecked((int)0x80004005)));
        Assert.Contains("another virtual desktop", Previews.CloakWho(2));
        Assert.Contains("the app itself", Previews.CloakWho(1));
        Assert.Contains("the window that owns it", Previews.CloakWho(4));
        Assert.Equal("Windows reports it cloaked (8)", Previews.CloakWho(8));
    }

    [Fact]
    public void A_push_updates_in_place_rebuilds_or_closes_and_closed_windows_are_named()
    {
        string sig = Previews.Sig(new[] { W("1"), W("2") });
        Assert.Equal("1,2", sig);
        Assert.Equal(Previews.Refresh.Update, Previews.OnPush(sig, App(W("1", "new title"), W("2", min: true))));
        Assert.Equal(Previews.Refresh.Rebuild, Previews.OnPush(sig, App(W("1"))));
        Assert.Equal(Previews.Refresh.Rebuild, Previews.OnPush(sig, App(W("2"), W("1"))));
        Assert.Equal(Previews.Refresh.Close, Previews.OnPush(sig, App()));
        Assert.Equal(Previews.Refresh.Close, Previews.OnPush(sig, null));
        Assert.Equal(new[] { "2" }, Previews.Gone(new[] { "1", "2" }, new[] { App(W("1")) }).ToArray());
    }

    [Fact]
    public void A_card_title_says_untitled_minimized_and_still_open()
    {
        Assert.Equal("(untitled window)", Previews.CardTitle(W("1", "")));
        Assert.Equal("Minimized \u00b7  ", Previews.CardPrefix(W("1", min: true), false));
        Assert.Equal("Still open \u00b7  ", Previews.CardPrefix(W("1", min: true), true));
        Assert.Equal("", Previews.CardPrefix(W("1"), false));
    }
}
