using Mantle.Core.Features.WindowList;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

// BAR-158, 161, 163: the bar's part of main.js shellRestarted (one per 3 s, the words) and what it re-does.
public class BarShellBackTests
{
    [Fact]
    public void Explorer_coming_back_is_taken_once_per_3_s_and_counted()
    {
        var b = new BarShellBack();
        Assert.True(b.Take(10_000));
        Assert.Equal(1, b.Count);
        Assert.False(b.Take(10_001));          // every Mantle window hears the same broadcast
        Assert.False(b.Take(12_999));
        Assert.Equal(1, b.Count);
        Assert.True(b.Take(13_000));
        Assert.Equal(2, b.Count);
        Assert.True(new BarShellBack().Take(0));   // the first one is never a repeat, whatever the clock says
    }

    [Fact]
    public void Dock_asks_again_an_up_bar_rewatches_and_raises_a_hidden_bar_waits_for_its_reveal()
    {
        Assert.Equal(new[] { "dock", "raise" }, BarShellBack.Steps("dock", true, false));
        Assert.Equal(new[] { "dock", "rewatch", "raise" }, BarShellBack.Steps("dock", true, true));
        Assert.Equal(new[] { "rewatch", "raise" }, BarShellBack.Steps("hover", true, true));
        Assert.Equal(new[] { "raise" }, BarShellBack.Steps("up", true, false));
        Assert.Empty(BarShellBack.Steps("hover", false, false));
    }

    [Fact]
    public void The_line_says_how_how_many_and_what_the_bar_did()
    {
        Assert.Equal("bar: Explorer is back (TaskbarCreated, 1 this run): asking for the dock slot again (a test run's helper reserves nothing), raising the bar over the new taskbar",
            BarShellBack.Line("TaskbarCreated", 1, "dock", true, false, true));
        Assert.Equal("bar: Explorer is back (TaskbarCreated, 2 this run): asking Explorer for the dock slot again, watching the new Windows taskbar, raising the bar over the new taskbar",
            BarShellBack.Line("TaskbarCreated", 2, "dock", true, true, false));
        Assert.Equal("bar: Explorer is back (TaskbarCreated, 1 this run): the bar is hidden, so nothing to do now: its next reveal finds the new Windows taskbar",
            BarShellBack.Line("TaskbarCreated", 1, "hover", false, false, false));
    }
}
