using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.PowerMode;
using Xunit;

namespace Mantle.Core.Tests.PowerMode;

/// ui/bar/powermode.js view(), the modes half: the sub line, the dead switch, the line under it and the note.
public class PowerModeViewTests
{
    static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    // the mock's state (no `settable`: absent is settable) says which power source the choice is for
    [InlineData("{\"ok\":true,\"mode\":\"balanced\",\"available\":true,\"source\":\"ac\",\"saver\":false}", "When plugged in", false, "")]
    [InlineData("{\"ok\":true,\"mode\":\"efficiency\",\"available\":true,\"settable\":true,\"source\":\"battery\",\"saver\":true}", "When on battery · Energy saver on", false, "")]
    [InlineData("{\"ok\":true,\"mode\":\"balanced\",\"available\":true,\"source\":\"x\"}", "Power source unknown", false, "")]
    // locked: Energy saver, or Windows' own reason (a technical one in plain words)
    [InlineData("{\"ok\":true,\"mode\":\"efficiency\",\"available\":true,\"settable\":false,\"source\":\"ac\",\"saver\":true}", "When plugged in", true, "Locked while Energy saver is on.")]
    [InlineData("{\"ok\":true,\"mode\":\"efficiency\",\"available\":true,\"settable\":false,\"source\":\"battery\",\"saver\":true,\"saverAt\":100}", "When on battery", true, "Locked: Energy saver is on whenever this PC is unplugged.")]
    [InlineData("{\"ok\":true,\"mode\":\"balanced\",\"available\":true,\"settable\":false,\"source\":\"ac\",\"reason\":\"PowerSetActiveOverlayScheme failed (HRESULT 0x80070005)\"}", "When plugged in", true, "PowerSetActiveOverlayScheme failed.")]
    // off Balanced, unreadable
    [InlineData("{\"ok\":true,\"mode\":\"balanced\",\"available\":false,\"source\":\"ac\"}", "Needs the Balanced plan", true, "Power modes work only on the Balanced power plan.")]
    [InlineData("{\"ok\":false,\"reason\":\"refused: sidecar is passive\"}", "Unavailable", true, "this is a test run, so Windows was not changed.")]
    [InlineData(null, "Reading…", true, "")]
    public void The_row_says_what_powermode_js_says(string? state, string sub, bool dead, string why)
    {
        var v = PowerModeView.View(state == null ? null : O(state), null, null);
        Assert.Equal(sub, v.Sub);
        Assert.Equal(dead, v.Dead);
        Assert.Equal(why, v.Why);
    }

    [Fact]
    public void The_chosen_segment_its_titles_a_pending_click_and_a_refusal()
    {
        var s = O("{\"ok\":true,\"mode\":\"balanced\",\"inForce\":\"performance\",\"available\":true,\"source\":\"ac\"}");
        var v = PowerModeView.View(s, null, null);
        Assert.Equal(new[] { false, true, false }, v.Segs.Select(g => g.On).ToArray());
        Assert.Equal("Balanced (current when plugged in; Windows reports Best performance in effect right now)", v.Segs[1].Title);
        Assert.Equal("Best power efficiency — use when plugged in", v.Segs[0].Title);
        Assert.Equal("Power mode when plugged in: Balanced", v.Aria);

        var p = PowerModeView.View(s, "performance", null);
        Assert.True(p.Segs[2].On && p.Segs[2].Pending && !p.Segs[1].On);

        var e = PowerModeView.View(s, null, "this is a test run, so Windows was not changed");
        Assert.Equal("Power mode did not change: this is a test run, so Windows was not changed.", e.Note);
        Assert.Equal("Power mode did not change: Windows gave no reason.", PowerModeView.View(s, null, "").Note);

        var other = PowerModeView.View(O("{\"ok\":true,\"mode\":\"better\",\"available\":true,\"source\":\"ac\"}"), null, null);
        Assert.Equal("Windows reports Better performance (a Windows 10 setting); pick one to replace it.", other.Note);
        Assert.All(other.Segs, g => Assert.False(g.On));

        var locked = PowerModeView.View(O("{\"ok\":true,\"mode\":\"efficiency\",\"available\":true,\"settable\":false,\"saver\":true,\"source\":\"ac\"}"), null, null);
        Assert.All(locked.Segs, g => Assert.True(g.Disabled));
        Assert.Equal("Best performance — Locked while Energy saver is on.", locked.Segs[2].Title);
    }
}
