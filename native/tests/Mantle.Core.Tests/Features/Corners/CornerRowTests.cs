using System.Linq;
using System.Text.Json;
using Mantle.Core.Features.Corners;
using Xunit;

namespace Mantle.Core.Tests.Features.Corners;

/// <summary>Quick settings' Hot corners row, its words and states (ui/bar/corners.js view() and plain(), CORN-061..078).</summary>
public class CornerRowTests
{
    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    const string Choices = "[{\"id\":\"off\",\"label\":\"Off\"},{\"id\":\"taskview\",\"label\":\"Task view\"},{\"id\":\"desktop\",\"label\":\"Show desktop\"},{\"id\":\"launcher\",\"label\":\"Launcher\"},{\"id\":\"qs\",\"label\":\"Quick settings\"},{\"id\":\"lock\",\"label\":\"Lock screen\"}]";
    static string St(string extra = "", string acts = "{\"tl\":\"taskview\",\"tr\":\"off\",\"bl\":\"off\",\"br\":\"desktop\"}") =>
        "{\"enabled\":true,\"test\":false,\"actions\":" + acts + ",\"choices\":" + Choices + ",\"watch\":{\"ok\":true,\"reason\":\"watching\"},\"fails\":{}" + extra + "}";

    [Fact]
    public void Before_the_first_answer_the_row_says_Loading_with_blank_disabled_tiles_and_no_warning()
    {
        var v = CornerRow.View(null);
        Assert.True(v.Dead && v.Loading);
        Assert.Equal("Loading…", v.Sub);
        Assert.Empty(v.Lines);
        Assert.False(v.Warn);
    }

    [Fact]
    public void Tiles_are_laid_out_like_the_screen_with_their_names_actions_and_tooltips()
    {
        var v = CornerRow.View(J(St()));
        Assert.Equal(new[] { "tl", "tr", "bl", "br" }, v.Cells.Select(c => c.Corner));
        Assert.Equal(new[] { "Top left", "Top right", "Bottom left", "Bottom right" }, v.Cells.Select(c => c.Label));
        Assert.Equal(new[] { "Task view", "Off", "Off", "Show desktop" }, v.Cells.Select(c => c.ActionLabel));
        Assert.True(v.Cells[1].Off);
        Assert.Equal("Top left corner: Task view. Click to change.", v.Cells[0].Tip);
        Assert.Equal("2 on · push the pointer in", v.Sub);
        Assert.Equal("Top left corner: Task view (now)", CornerRow.ChipTip(v.Cells[0], "Task view", true));
        Assert.Equal("Make the top left corner lock screen", CornerRow.ChipTip(v.Cells[0], "Lock screen", false));
        Assert.Equal(6, v.Choices.Count);
        Assert.Empty(v.Lines);
    }

    [Fact]
    public void All_off_tray_off_resting_and_no_answer_each_have_their_own_words()
    {
        Assert.Equal("All off", CornerRow.View(J(St(acts: "{\"tl\":\"off\",\"tr\":\"off\",\"bl\":\"off\",\"br\":\"off\"}"))).Sub);
        var off = CornerRow.View(J(St().Replace("\"enabled\":true", "\"enabled\":false")));
        Assert.Equal("Switched off in the tray", off.Sub);
        Assert.Equal(new CornerLine("Hot corners are switched off in Mantle's tray menu; choices are kept for when they are on.", true), off.Lines.Single());
        var rest = CornerRow.View(J("{\"error\":\"resting while VALORANT runs\"}"));
        Assert.True(rest.Dead);
        Assert.Equal("Resting · a game runs", rest.Sub);
        Assert.Equal(new CornerLine("Resting while VALORANT runs.", false), rest.Lines.Single());
        Assert.False(rest.Warn);
        var gone = CornerRow.View(J("{\"error\":\"the sidecar pipe closed\"}"));
        Assert.Equal("Unavailable", gone.Sub);
        Assert.Equal("Hot corners didn't answer: Close quick settings and open it again. If it keeps happening, restart Mantle.", gone.Lines.Single().Text);
        Assert.True(gone.Warn);
    }

    [Fact]
    public void A_paused_watch_a_failed_corner_a_refused_push_and_a_test_copy_add_their_lines()
    {
        var v = CornerRow.View(J(St(",\"test\":true", "{\"tl\":\"taskview\",\"tr\":\"lock\",\"bl\":\"off\",\"br\":\"desktop\"}")
            .Replace("\"watch\":{\"ok\":true,\"reason\":\"watching\"}", "\"watch\":{\"ok\":false,\"reason\":\"the cursor could not be read (user32 said no)\"}")
            .Replace("\"fails\":{}", "\"fails\":{\"tr\":{\"action\":\"lock\",\"said\":\"the screen did not lock: LockWorkStation returned FALSE\"},\"br\":{\"action\":\"desktop\",\"said\":\"windows did not answer\"}},\"last\":{\"corner\":\"tl\",\"action\":\"taskview\",\"blocked\":true,\"said\":\"Code.exe is full screen in front\"}")));
        var t = v.Lines.Select(l => l.Text).ToArray();
        Assert.Equal("The corners are paused: Mantle can't read the mouse right now. They come back on their own.", t[0]);
        Assert.Equal("Top right (Lock screen): Lock screen didn't work last time. Push into the corner again; if it keeps failing, restart Mantle.", t[1]);
        Assert.Equal("Bottom right (Show desktop): Windows did not answer.", t[2]);
        Assert.Equal("Last push, top left: did nothing, Mantle could not check for a game or a full-screen window in front", t[3]);   // ".exe" reads technical
        Assert.Equal("This is a test copy of Mantle: corners only pretend to act.", t[4]);
        Assert.True(v.Cells[1].Failed && v.Cells[3].Failed);
        Assert.Equal("Top right corner: Lock screen (its last push failed; the reason is below). Click to change.", v.Cells[1].Tip);
        Assert.True(v.Lines[0].Warn && v.Lines[1].Warn && !v.Lines[3].Warn && !v.Lines[4].Warn);
        Assert.DoesNotContain(t, s => s.Contains("dry run", System.StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("the disk is full", "The disk is full.")]
    [InlineData("  it   could not\n be saved!  ", "It could not be saved!")]
    [InlineData("sidecar timed out", "FB")]
    [InlineData("0x80070005 access denied", "FB")]
    [InlineData("ENOENT: no such file", "FB")]
    [InlineData("value was undefined", "FB")]
    [InlineData("took 1500 ms", "FB")]
    [InlineData("HRESULT E_FAIL", "FB")]
    [InlineData("main.js threw", "FB")]
    [InlineData("", "FB")]
    public void Plain_keeps_plain_reasons_and_swaps_technical_ones_for_the_fallback(string said, string want) =>
        Assert.Equal(want, CornerRow.Plain(said, "FB"));

    [Fact]
    public void Plain_drops_a_reason_over_200_characters_and_save_notes_say_which_corner()
    {
        Assert.Equal("FB", CornerRow.Plain(new string('a', 201), "FB"));
        Assert.Equal("Top right could not be set: It could not be saved: the disk is full.", CornerRow.SaveNote("tr", "it could not be saved: the disk is full", true));
        Assert.Equal("Bottom left could not be set: Mantle could not save it. Try again.", CornerRow.SaveNote("bl", null, true));
        Assert.Equal("Bottom left could not be set: Mantle did not answer. Try again.", CornerRow.SaveNote("bl", null, false));
    }
}
