using System.Collections.Generic;
using Mantle.Core.Features.Switcher;
using Mantle.Core.Features.TaskView;
using Mantle.Core.Keys;
using Xunit;

namespace Mantle.Core.Tests.Features.TaskView;

// lib/features/taskview.test.js "the page may only name this session's windows" block, the feature row and
// parkRect, ported (TV-001, TV-007, TV-101 (main's half), TV-119, TV-120, TV-121, TV-128 (dry writes)).
public class TaskViewGuardsTests
{
    const string D1 = "D1000000-0000-0000-0000-000000000000";
    const string D2 = "d2000000-0000-0000-0000-00000000abcd";
    static readonly HashSet<string> Hwnds = new() { "101", "202" };
    static readonly HashSet<string> Desks = new() { D1, D2 };

    [Fact]
    public void The_feature_owns_Win_Tab_only_on_by_default_and_rests_for_games()
    {
        Assert.Equal("taskview", TaskViewGuards.FeatureName);
        Assert.Equal("Task View (Win+Tab)", TaskViewGuards.FeatureLabel);
        Assert.True(TaskViewGuards.DefaultOn);
        Assert.True(TaskViewGuards.Rests);
        Assert.Equal(new[] { "Win+Tab" }, TaskViewGuards.Chords);
        Assert.True(ChordTable.IsChord("Win+Tab"));
    }

    [Fact]
    public void A_window_the_session_never_listed_or_a_malformed_id_is_refused_in_words()
    {
        Assert.Null(TaskViewGuards.CheckHwnd("101", Hwnds));
        Assert.Equal("that window is no longer in Task View", TaskViewGuards.CheckHwnd("999", Hwnds));
        Assert.NotNull(TaskViewGuards.CheckHwnd("101; calc", new HashSet<string> { "101; calc" }));
        Assert.NotNull(TaskViewGuards.CheckHwnd("0101", new HashSet<string> { "0101" }));
        Assert.NotNull(TaskViewGuards.CheckHwnd(null, Hwnds));
        Assert.NotNull(TaskViewGuards.CheckHwnd("101", null));
    }

    [Fact]
    public void A_desktop_the_session_never_listed_or_a_malformed_id_is_refused()
    {
        Assert.Null(TaskViewGuards.CheckDesk(D1, Desks));
        Assert.Equal("that desktop no longer exists", TaskViewGuards.CheckDesk("D9000000-0000-0000-0000-000000000000", Desks));
        Assert.Equal("that desktop no longer exists", TaskViewGuards.CheckDesk("not a guid", new HashSet<string> { "not a guid" }));
    }

    [Fact]
    public void A_stale_or_missing_session_number_is_ignored()
    {
        Assert.True(TaskViewGuards.IsCurrent(1, 1));
        Assert.False(TaskViewGuards.IsCurrent(8, 1));
        Assert.False(TaskViewGuards.IsCurrent(null, 1));
        Assert.False(TaskViewGuards.IsCurrent(1, null));
    }

    [Fact]
    public void Desk_refuses_in_taskviews_order_before_the_sidecar_is_asked()
    {
        Assert.Equal("Task View does not know that desktop action", TaskViewGuards.Desk("format", D1, null, null, Hwnds, Desks, false).Error);
        Assert.Contains("does not know", TaskViewGuards.Desk("__proto__", null, null, null, Hwnds, Desks, false).Error);
        Assert.Contains("no longer exists", TaskViewGuards.Desk("switch", "D9000000-0000-0000-0000-000000000000", null, null, Hwnds, Desks, false).Error);
        Assert.Contains("no longer exists", TaskViewGuards.Desk("switch", "not a guid", null, null, Hwnds, Desks, false).Error);
        Assert.Contains("has to be text", TaskViewGuards.Desk("rename", D2, null, new Dictionary<string, object> { ["evil"] = true }, Hwnds, Desks, false).Error);
        Assert.Contains("100 characters", TaskViewGuards.Desk("rename", D2, null, new string('x', 101), Hwnds, Desks, false).Error);
        Assert.Contains("no longer in Task View", TaskViewGuards.Desk("move", D2, "555", null, Hwnds, Desks, false).Error);
        var (none, _) = TaskViewGuards.Desk("move", D2, "555", null, Hwnds, Desks, false);
        Assert.Null(none);
    }

    [Fact]
    public void Desk_requests_carry_only_what_each_action_needs_and_test_runs_are_dry()
    {
        Assert.Equal(new DeskRequest("desktops.create", true), TaskViewGuards.Desk("create", "junk", "junk", null, Hwnds, Desks, true).Request);
        Assert.Equal(new DeskRequest("desktops.switch", false, D1), TaskViewGuards.Desk("switch", D1, null, null, Hwnds, Desks, false).Request);
        Assert.Equal(new DeskRequest("desktops.remove", false, D2), TaskViewGuards.Desk("remove", D2, null, null, Hwnds, Desks, false).Request);
        Assert.Equal(new DeskRequest("desktops.move", false, D2, "202"), TaskViewGuards.Desk("move", D2, "202", null, Hwnds, Desks, false).Request);
        Assert.Equal(new DeskRequest("desktops.rename", false, D1, null, "Work"), TaskViewGuards.Desk("rename", D1, null, "Work", Hwnds, Desks, false).Request);
        Assert.Equal(new DeskRequest("desktops.rename", false, D1, null, new string('x', 100)),
            TaskViewGuards.Desk("rename", D1, null, new string('x', 100), Hwnds, Desks, false).Request);
        Assert.Equal(4000, TaskViewGuards.DeskTimeoutMs);
    }

    [Fact]
    public void Sidecar_client_failures_are_said_in_plain_words()
    {
        Assert.Equal("Mantle's window helper is restarting; try again in a moment", TaskViewGuards.Plain("the sidecar is not running (it exited)"));
        Assert.Equal("Mantle's window helper is restarting; try again in a moment", TaskViewGuards.Plain("did not answer \"taskview.list\" in 1500 ms"));
        Assert.Equal("Mantle's window helper is restarting; try again in a moment", TaskViewGuards.Plain("no READY LINE"));
        Assert.Equal("Windows always keeps one desktop", TaskViewGuards.Plain("Windows always keeps one desktop"));
        Assert.Equal("no reason was given", TaskViewGuards.Plain(null));
        Assert.Equal("no reason was given", TaskViewGuards.Plain(""));
    }

    [Fact]
    public void The_window_parks_beside_its_monitor_at_the_monitors_size_off_every_display()
    {
        var a = new PhysRect(0, 0, 1920, 1080);
        Assert.Equal(new PhysRect(0, -1080 - 64, 1920, 1080), TaskViewGuards.ParkRect(a, new[] { a }));
        var above = new PhysRect(0, -1080, 1920, 1080);
        Assert.Equal(new PhysRect(0, 1080 + 64, 1920, 1080), TaskViewGuards.ParkRect(a, new[] { a, above }));
        var all = new[] { a, above, new PhysRect(0, 1080, 1920, 1080), new PhysRect(-1920, 0, 1920, 1080), new PhysRect(1920, 0, 1920, 1080) };
        Assert.Equal(new PhysRect(-32000, -32000, 1920, 1080), TaskViewGuards.ParkRect(a, all));
    }
}
