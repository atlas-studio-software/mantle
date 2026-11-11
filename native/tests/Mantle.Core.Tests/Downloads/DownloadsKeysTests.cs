using Mantle.Core.Features.Downloads;
using Xunit;

namespace Mantle.Core.Tests.Downloads;

/// <summary>ui/bar/downloads.js onKey (BAR-888) and act's one press per row (BAR-882), pure.</summary>
public class DownloadsKeysTests
{
    [Fact]
    public void The_first_Down_is_the_newest_the_first_Up_the_oldest_and_the_walk_is_clamped()
    {
        Assert.Equal(0, DownloadsKeys.Walk("Down", -1, 3));
        Assert.Equal(2, DownloadsKeys.Walk("Up", -1, 3));
        Assert.Equal(1, DownloadsKeys.Walk("Down", 0, 3));
        Assert.Equal(2, DownloadsKeys.Walk("Down", 2, 3));   // never wraps
        Assert.Equal(0, DownloadsKeys.Walk("Up", 0, 3));
        Assert.Equal(0, DownloadsKeys.Walk("Home", 2, 3));
        Assert.Equal(2, DownloadsKeys.Walk("End", 0, 3));
        Assert.Null(DownloadsKeys.Walk("Down", -1, 0));
        Assert.Null(DownloadsKeys.Walk("Tab", 0, 3));
    }

    [Fact]
    public void A_double_click_or_a_press_while_one_is_in_flight_opens_once()
    {
        Assert.True(DownloadsKeys.Takes(false, null, 0, "a|open", 1000));
        Assert.False(DownloadsKeys.Takes(true, null, 0, "a|open", 1000));
        Assert.False(DownloadsKeys.Takes(false, "a|open", 1000, "a|open", 1499));
        Assert.True(DownloadsKeys.Takes(false, "a|open", 1000, "a|open", 1500));
        Assert.True(DownloadsKeys.Takes(false, "a|open", 1000, "b|open", 1001));   // another row is another decision
        Assert.True(DownloadsKeys.Takes(false, "a|open", 1000, "a|reveal", 1001));
    }
}
