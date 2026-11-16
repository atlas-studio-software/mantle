using System.Linq;
using Mantle.Core.Features.QuickLinks;
using Xunit;

namespace Mantle.Core.Tests.QuickLinks;

/// <summary>
/// The launcher's Quick links mode, pure (ui/launcher/launcher.js quickRows / quickKey / activateQuick / activatePower /
/// once): the groups, the letters, the submenu, ask-first arming and the once-per-decision guard. Time is passed in.
/// </summary>
public class QuickMenuTests
{
    [Fact]
    public void The_main_list_is_three_groups_in_Windows_order_with_ids_only()
    {
        var m = new QuickMenu();
        var v = m.Rows();
        Assert.Equal(new[] { "Tools", "Open", "Power and desktop" }, v.Groups.Select(g => g.Label));
        Assert.Equal(new[] { 11, 5, 2 }, v.Groups.Select(g => g.Rows.Count));
        Assert.Equal(WinXLinks.Links.Select(l => "q:" + l.Id), v.Flat.Select(r => r.Item.Id));
        Assert.Equal("Task Manager", v.Flat[11].Item.Name);
        Assert.Equal("Mantle", v.Flat[11].Item.Subtitle);
        Assert.Equal("Mantle Files", v.Flat[13].Item.Subtitle);
        Assert.All(v.Flat, r => Assert.Equal("quick", r.Item.Kind));
        Assert.Equal("Quick links: press a letter", QuickMenu.Placeholder);
        Assert.Equal(new[] { ("Letter", "Open"), ("Enter", "Open"), ("Esc", "Close") }, m.Hint());
    }

    [Fact]
    public void Each_rows_letter_is_drawn_underlined_from_its_label()
    {
        var v = new QuickMenu().Rows();
        var dm = QuickMenu.LabelOf(v.Flat.First(r => r.Item.Id == "q:devices").Item)!;
        Assert.Equal(("Device ", "M", "anager"), (dm.Before, dm.Letter, dm.After));
        var ia = QuickMenu.LabelOf(v.Flat[0].Item)!;
        Assert.Equal(("Installed a", "p", "ps"), (ia.Before, ia.Letter, ia.After));
        Assert.Null(QuickMenu.LabelOf(new Mantle.Core.Features.LauncherIndex.LauncherItem { Id = "x", Kind = "app", Name = "x" }));
    }

    [Fact]
    public void A_letter_in_any_case_selects_and_activates_its_row_and_a_held_letter_picks_once()
    {
        var m = new QuickMenu();
        Assert.Equal(new QuickStep("select", "q:devices", Activate: true), m.Key("m", false, "q:apps"));
        Assert.Equal(new QuickStep("select", "q:devices", Activate: true), m.Key("M", false, "q:apps"));
        Assert.Equal(new QuickStep("select", "q:terminaladmin", Activate: true), m.Key("a", false, null));
        Assert.Equal("none", m.Key("m", true, "q:devices").Kind);
        Assert.Equal(QuickStep.None, m.Key("q", false, null));        // no row has Q
        Assert.Equal(QuickStep.None, m.Key("Tab", false, null));
    }

    [Fact]
    public void Shut_down_or_sign_out_opens_the_submenu_and_Esc_or_Left_go_back()
    {
        var m = new QuickMenu();
        var v = m.Rows();
        var sd = v.Flat.First(r => r.Item.Id == "q:shutdown").Item;
        Assert.Equal("sub", m.Activate(sd, 1000).Kind);
        var p = m.Rows();
        Assert.Equal(new[] { "Shut down or sign out" }, p.Groups.Select(g => g.Label));
        Assert.Equal(new[] { "Sign out", "Sleep", "Hibernate", "Shut down", "Restart" }, p.Flat.Select(r => r.Item.Name));
        Assert.Equal(new[] { "signout", "sleep", "hibernate", "power", "restart" }, p.Flat.Select(r => r.Item.Glyph));
        Assert.Equal("Press U again to shut down", p.Flat[3].Item.Confirm);
        Assert.Null(p.Flat[1].Item.Confirm);
        Assert.Equal(("Esc", "Back"), m.Hint()[2]);
        Assert.Equal("back", m.Key("Escape", false, "qp:signout").Kind);
        Assert.Null(m.Sub);
        Assert.Equal("close", m.Key("Escape", false, "q:apps").Kind);  // Esc on the main list closes
        Assert.Equal("sub", m.Key("Right", false, "q:shutdown").Kind);
        Assert.Equal(QuickStep.None, new QuickMenu().Key("Right", false, "q:apps"));
        Assert.Equal("back", m.Key("Left", false, "qp:sleep").Kind);
        Assert.Equal(QuickStep.None, m.Key("Left", false, "q:apps"));
    }

    [Fact]
    public void An_ask_first_row_arms_on_the_first_press_and_runs_on_the_second_within_5_s()
    {
        var m = new QuickMenu();
        m.Activate(m.Rows().Flat.First(r => r.Item.Id == "q:shutdown").Item, 0);
        var sdRow = m.Rows().Flat.First(r => r.Item.Id == "qp:shutdown").Item;
        Assert.Equal(new QuickStep("armed", "qp:shutdown", "shutdown"), m.Activate(sdRow, 1000));
        Assert.Equal("Press U again to shut down", m.Rows().Flat.First(r => r.Item.Id == "qp:shutdown").Item.Subtitle);
        // a double click: nothing confirms within 250 ms of arming
        Assert.Equal(new QuickStep("none", "qp:shutdown", "too soon"), m.Activate(sdRow, 1100));
        Assert.Equal(new QuickStep("power", "qp:shutdown", "shutdown", Confirmed: true), m.Activate(sdRow, 1400));
        Assert.Null(m.ArmedId);
        // the same key: a letter selects and activates, so it is the second press
        Assert.Equal(new QuickStep("select", "qp:restart", Activate: true), m.Key("r", false, "qp:shutdown"));
    }

    [Fact]
    public void The_ask_runs_out_after_5_s_and_moving_off_or_Esc_disarms()
    {
        var m = new QuickMenu();
        m.Activate(m.Rows().Flat.First(r => r.Item.Id == "q:shutdown").Item, 0);
        var so = m.Rows().Flat[0].Item;
        Assert.Equal("armed", m.Activate(so, 0).Kind);
        Assert.False(m.ArmExpired(4999));
        Assert.True(m.ArmExpired(5000));
        Assert.Null(m.ArmedId);
        Assert.Equal("armed", m.Activate(so, 6000).Kind);
        m.Moved("qp:sleep");
        Assert.Null(m.ArmedId);
        Assert.Equal("armed", m.Activate(so, 7000).Kind);
        Assert.Equal(new QuickStep("back", Arg: "disarmed"), m.Key("Escape", false, "qp:signout"));
        Assert.Equal(QuickMenu.PowerSub, m.Sub);                        // Esc while armed only disarms
        // a late second press after the ask ran out arms again
        Assert.Equal("armed", m.Activate(so, 8000).Kind);
        Assert.Equal("armed", m.Activate(so, 13001).Kind);
    }

    [Fact]
    public void Sleep_and_Hibernate_go_straight_through_unconfirmed()
    {
        var m = new QuickMenu();
        m.Activate(m.Rows().Flat.First(r => r.Item.Id == "q:shutdown").Item, 0);
        Assert.Equal(new QuickStep("power", "qp:sleep", "sleep"), m.Activate(m.Rows().Flat[1].Item, 10));
    }

    [Fact]
    public void A_row_is_sent_once_per_decision_a_double_click_sends_one_call()
    {
        var m = new QuickMenu();
        var ev = m.Rows().Flat.First(r => r.Item.Id == "q:events").Item;
        Assert.Equal(new QuickStep("open", "q:events", "events"), m.Activate(ev, 1000));
        Assert.Equal(new QuickStep("none", "q:events", "guard"), m.Activate(ev, 1100));   // still on its way
        m.Done("q:events", "open");
        Assert.Equal("none", m.Activate(ev, 1200).Kind);                                  // within 500 ms of sending it
        Assert.Equal("open", m.Activate(ev, 1500).Kind);
    }

    [Fact]
    public void Search_and_Run_are_the_launchers_pages_and_a_note_shows_under_its_row()
    {
        var m = new QuickMenu();
        var v = m.Rows();
        Assert.Equal(new QuickStep("page", "q:search", "search"), m.Activate(v.Flat.First(r => r.Item.Id == "q:search").Item, 0));
        Assert.Equal(new QuickStep("page", "q:run", "run"), m.Activate(v.Flat.First(r => r.Item.Id == "q:run").Item, 0));
        m.SetNote("q:events", "dry run: would run ShellExecute(\"eventvwr.msc\")", true);
        Assert.Equal("dry run: would run ShellExecute(\"eventvwr.msc\")", m.Rows().Flat.First(r => r.Item.Id == "q:events").Item.Subtitle);
        m.Reset();
        Assert.Equal("", m.Rows().Flat.First(r => r.Item.Id == "q:events").Item.Subtitle);
    }
}
