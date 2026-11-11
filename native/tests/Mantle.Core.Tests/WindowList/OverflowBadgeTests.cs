using Mantle.Core.Features.WindowList;
using Xunit;
using H = Mantle.Core.Features.WindowList.OverflowBadge.Hidden;

namespace Mantle.Core.Tests.WindowList;

// ui/bar/helmbadge.js paintMore (lib/helm-badge.test.js "+N"): BAR-257, BAR-1030.
public class OverflowBadgeTests
{
    [Fact]
    public void Plus_N_carries_the_sum_of_the_hidden_apps_counts_and_names_them()
    {
        var (n, warn, line) = OverflowBadge.Sum(new[] { new H("Helm", 2, false), new H("Mail", 0, false), new H("Quartz", 3, false) }, true);
        Assert.Equal(5, n);
        Assert.False(warn);
        Assert.Equal("\nNeeds you: Helm 2, Quartz 3", line);
    }

    [Fact]
    public void Any_error_behind_plus_N_turns_its_disc_warn_and_a_nameless_app_is_an_app()
    {
        var (n, warn, line) = OverflowBadge.Sum(new[] { new H("", 1, false), new H("Helm", 12, true) }, true);
        Assert.Equal(13, n);
        Assert.True(warn);
        Assert.Equal("\nNeeds you: an app 1, Helm 12", line);
    }

    [Fact]
    public void No_count_or_a_hidden_plus_N_has_no_disc_and_no_line()
    {
        Assert.Equal((0, false, ""), OverflowBadge.Sum(new[] { new H("Helm", 0, true) }, true));
        Assert.Equal((0, false, ""), OverflowBadge.Sum(new[] { new H("Helm", 4, true) }, false));
        Assert.Equal((0, false, ""), OverflowBadge.Sum(System.Array.Empty<H>(), true));
    }

    [Fact]
    public void The_spoken_name_turns_line_breaks_into_sentences()
    {
        Assert.Equal("3 more apps that do not fit. Needs you: Helm 2", OverflowBadge.Spoken("3 more apps that do not fit\nNeeds you: Helm 2"));
    }
}
