using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.GameMode;
using Xunit;

namespace Mantle.Core.Tests.GameMode;

/// <summary>ui/bar/gamemode.js view(), pure: the Details rows off and on (BAR-812/813) and the boost's waiting line (BAR-786).</summary>
public class GameModeViewTests
{
    static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void Details_while_off_are_the_six_plan_rows()
    {
        var v = GameModeView.Of(J("{\"on\":false,\"enabled\":true,\"watching\":true}"));
        Assert.Equal(new[] { "Power mode", "Background apps", "Mantle itself", "Boost", "Wallpaper", "Mantle resting" }, v.Plan!.Select(r => r.Label));
        Assert.All(v.Plan!, r => Assert.Equal(("plan", "plan", ""), (r.Key, r.Status, r.Badge)));
        Assert.Empty(v.Rows);
        Assert.Null(GameModeView.Of(J("{\"on\":false,\"busy\":\"on\"}")).Plan);   // not while it is turning on
    }

    [Fact]
    public void Details_while_on_are_one_row_per_action_with_its_badge_and_why_first_and_resting_on_top()
    {
        var st = J("{\"on\":true,\"how\":\"auto\",\"game\":{\"file\":\"VALORANT-Win64-Shipping.exe\",\"why\":\"a known game is in front\"}," +
            "\"actions\":[{\"key\":\"power\",\"label\":\"Power mode\",\"status\":\"done\",\"text\":\"best performance\"},{\"key\":\"background\",\"label\":\"Background apps\",\"status\":\"same\",\"text\":\"nothing to ease\"}," +
            "{\"key\":\"mantle\",\"label\":\"Mantle itself\",\"status\":\"dry\",\"text\":\"would\"},{\"key\":\"wallpaper\",\"label\":\"Wallpaper\",\"status\":\"blocked\",\"text\":\"cannot\"}," +
            "{\"key\":\"x\",\"label\":\"X\",\"status\":\"failed\",\"text\":\"no\"},{\"key\":\"y\",\"label\":\"Y\",\"status\":\"skipped\",\"text\":\"off\"}]}");
        var v = GameModeView.Of(st);
        Assert.Null(v.Plan);
        Assert.Equal(new[] { "why", "power", "background", "mantle", "wallpaper", "x", "y" }, v.Rows.Select(r => r.Key));
        Assert.Equal(("Why it is on", "info", "", "A known game is in front"), (v.Rows[0].Label, v.Rows[0].Status, v.Rows[0].Badge, v.Rows[0].Text));
        Assert.Equal(new[] { "Done", "No change", "Test run", "Not done", "Failed", "Skipped" }, v.Rows.Skip(1).Select(r => r.Badge));
        Assert.Equal("On for VALORANT", v.Sub);
        // resting: "Mantle resting" goes on top
        st["rest"] = new JsonObject { ["on"] = true, ["why"] = "VALORANT runs" };
        v = GameModeView.Of(st);
        Assert.Equal(("rest", "Mantle resting", "Done", "Mantle is resting while VALORANT runs"), (v.Rows[0].Key, v.Rows[0].Label, v.Rows[0].Badge, v.Rows[0].Text));
        Assert.Equal("why", v.Rows[1].Key);
        // by hand: no "Why it is on" row
        Assert.DoesNotContain(GameModeView.Of(J("{\"on\":true,\"how\":\"manual\",\"game\":{\"why\":\"x\"},\"actions\":[]}")).Rows, r => r.Key == "why");
    }

    [Fact]
    public void A_double_click_on_the_switch_is_one_decision()
    {
        Assert.True(GameModeView.SwitchTakes(false, false, false, long.MinValue / 2, 0));
        Assert.False(GameModeView.SwitchTakes(false, false, false, 1000, 1499));   // the second click of a double click, even after a fast answer
        Assert.True(GameModeView.SwitchTakes(false, false, false, 1000, 1500));
        Assert.False(GameModeView.SwitchTakes(false, false, true, 0, 9999));       // a call on its way
        Assert.False(GameModeView.SwitchTakes(false, true, false, 0, 9999));       // turning on / off
        Assert.False(GameModeView.SwitchTakes(true, false, false, 0, 9999));       // unavailable
    }

    [Fact]
    public void While_on_the_boosts_waiting_line_is_the_line_under_the_switch()
    {
        var v = GameModeView.Of(J("{\"on\":true,\"how\":\"manual\",\"boostNow\":{\"state\":\"waiting\",\"text\":\"Boost: eased 4 background apps; idle memory is handed back a minute in\",\"warn\":false}}"));
        Assert.Equal(("Boost: eased 4 background apps; idle memory is handed back a minute in", false), (v.Why, v.Warn));
        v = GameModeView.Of(J("{\"on\":true,\"boostNow\":{\"state\":\"done\",\"text\":\"Boost: freed 1.2 GB and eased 4 background apps; 3.1 GB of memory free now\"}}"));
        Assert.Equal("Boost: freed 1.2 GB and eased 4 background apps; 3.1 GB of memory free now", v.Why);
        // off: the boost line is not shown
        Assert.Equal("", GameModeView.Of(J("{\"on\":false,\"boostNow\":{\"text\":\"Boost: x\"}}")).Why);
    }
}
