using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.TaskView;
using Xunit;

namespace Mantle.Core.Tests.Features.TaskView;

// lane r9-switcher: the [Feature("taskview")] service (lib/features/taskview.js open/end, the page's commands and the
// close re-check) on the feature host's test rig with a fake surface and a fake sidecar. TV-002, TV-004, TV-005,
// TV-015 (the service half), TV-024 (the reason), TV-029, TV-032, and the hand-back with no surface.
public class TaskViewFeatureTests
{
    sealed class Surface : ITaskViewSurface
    {
        public bool Ready { get; set; } = true;
        public readonly List<string> Calls = new();
        public readonly List<(string Name, string Json)> Pushed = new();
        public ValueTask<string?> OpenAsync(TaskViewSession s) { Calls.Add($"open #{s.Seq} {s.N}"); return ValueTask.FromResult<string?>(null); }
        public void End(TaskViewSession s, string why, bool giveBack) => Calls.Add($"end #{s.Seq} {why}{(giveBack ? " +keyboard" : "")}");
        public void Push(string name, object payload) => Pushed.Add((name, JsonSerializer.Serialize(payload)));
        public void Drop(string why) => Calls.Add("drop " + why);
    }

    const string Desk1 = "11111111-2222-3333-4444-555555555555";
    const string List = "{\"list\":[{\"hwnd\":\"101\",\"title\":\"Notes\"},{\"hwnd\":\"202\",\"title\":\"Mail\"}],\"desktops\":{\"list\":[{\"id\":\"" + Desk1 + "\",\"name\":\"Desktop 1\"}]}}";

    static void T(Func<Rig, Task> body, bool test = false) => HostLoop.Run(async (_, time) => await body(new Rig(time, test)));

    static FeatureDescriptor D(TaskViewFeature.Host h, List<TaskViewFeature> made) =>
        FeatureDescriptor.Of(typeof(TaskViewFeature).GetCustomAttribute<FeatureAttribute>()!, () => { var f = new TaskViewFeature(h); made.Add(f); return f; });

    static async Task Settle(Rig r, Func<bool> done, int rounds = 200)
    {
        for (int i = 0; i < rounds && !done(); i++) { await Task.Yield(); r.Time.AdvanceMs(5); }
    }

    // A test run is never live, so the host delivers no chord there: those tests open through the feature itself.
    static async Task<TaskViewFeature> Up(Rig r, Surface? s)
    {
        var made = new List<TaskViewFeature>();
        r.Platform.Start();
        r.Platform.Answer("hotkey", "{\"ok\":true}");
        r.Platform.Answer("taskview.list", List);
        r.Host.Load(new[] { D(new TaskViewFeature.Host(s, new HostClock(r.Time)), made) });
        await r.Host.Installed;
        await Settle(r, () => false, 20);
        return made[^1];
    }

    static async Task WinTab(Rig r, Func<bool> done)
    {
        r.Platform.Raise("chord", "{\"chord\":\"Win+Tab\"}");
        await Settle(r, done);
    }

    static Task<Mantle.Core.Commands.CommandResult> Call(Rig r, string verb, string args) =>
        r.Bus.CallAsync("taskview." + verb, JsonNode.Parse(args) as JsonObject).AsTask();

    [Fact]
    public void The_feature_row_is_taskview_on_by_default_resting_with_Win_Tab()
    {
        var a = typeof(TaskViewFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(TaskViewGuards.FeatureName, a.Name);
        Assert.Equal(TaskViewGuards.FeatureLabel, a.Label);
        Assert.Equal(TaskViewGuards.DefaultOn, a.DefaultOn);
        Assert.Equal(TaskViewGuards.Rests, a.Rest);
        Assert.Equal(TaskViewGuards.Chords, a.Chords);
    }

    [Fact]
    public void With_no_surface_the_feature_is_unhealthy_so_Win_Tab_stays_Windows_own() => T(async r =>
    {
        await Up(r, null);
        Assert.True(r.Hub.Current.Features.TryGetValue("taskview", out var p));
        Assert.False(p!.Ok);
        Assert.Contains("not drawn in native Mantle yet", p.Reason, StringComparison.Ordinal);
        await WinTab(r, () => false);
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "taskview.list");
    });

    [Fact]
    public void Win_Tab_opens_and_Win_Tab_again_closes_with_the_keyboard_given_back() => T(async r =>
    {
        var s = new Surface();
        await Up(r, s);
        await WinTab(r, () => s.Calls.Any(c => c.StartsWith("open ", StringComparison.Ordinal)));
        Assert.Equal("open #1 2", s.Calls.Single());
        await WinTab(r, () => s.Calls.Count > 1);
        Assert.Equal("end #1 Win+Tab again +keyboard", s.Calls[1]);
        Assert.True(r.Logged("#1 (Win+Tab): 2 windows; ended: Win+Tab again"));
        Assert.DoesNotContain(r.Lines, l => l.Contains("Notes", StringComparison.Ordinal));
    });

    [Fact]
    public void Win_Tab_before_the_page_is_ready_does_nothing_but_a_log_line() => T(async r =>
    {
        var s = new Surface { Ready = false };
        await Up(r, s);
        await WinTab(r, () => r.Logged(TaskViewFeature.NotReady));
        Assert.True(r.Logged(TaskViewFeature.NotReady));
        Assert.Empty(s.Calls);
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "taskview.list");
    });

    [Fact]
    public void A_switch_to_a_window_checks_the_session_and_keeps_the_keyboard_where_the_user_went() => T(async r =>
    {
        var s = new Surface();
        await Up(r, s);
        r.Platform.Answer("taskview.activate", "{\"how\":\"direct\"}");
        await WinTab(r, () => s.Calls.Count == 1);
        var stale = await Call(r, "activate", "{\"seq\":9,\"hwnd\":\"101\"}");
        Assert.True(stale.Result.GetProperty("stale").GetBoolean());
        var bad = await Call(r, "activate", "{\"seq\":1,\"hwnd\":\"999\"}");
        Assert.False(bad.Ok);
        Assert.Equal(TaskViewGuards.NoWindow, bad.Error);
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb == "taskview.activate");
        var ok = await Call(r, "activate", "{\"seq\":1,\"hwnd\":\"101\"}");
        Assert.True(ok.Ok);
        Assert.Contains(r.Platform.Calls, c => c.Verb == "taskview.activate" && c.ArgsJson.Contains("\"hwnd\":\"101\"", StringComparison.Ordinal) && c.ArgsJson.Contains("\"dry\":false", StringComparison.Ordinal));
        Assert.Equal("end #1 switched to a window (direct)", s.Calls.Last());   // no +keyboard
    });

    [Fact]
    public void Desktop_actions_are_checked_before_the_sidecar_and_are_dry_runs_in_a_test_run() => T(async r =>
    {
        var s = new Surface();
        var f = await Up(r, s);
        r.Platform.Answer("desktops.rename", "{\"ok\":true}");
        r.Platform.Answer("desktops.create", "{\"id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"}");
        r.Platform.Answer("desktops.switch", "{\"already\":false}");
        await f.Open("test");
        Assert.Equal(TaskViewGuards.NameNotText, (await Call(r, "desk", "{\"seq\":1,\"op\":\"rename\",\"desktop\":\"" + Desk1 + "\",\"name\":5}")).Error);
        Assert.Equal(TaskViewGuards.NoDesktop, (await Call(r, "desk", "{\"seq\":1,\"op\":\"remove\",\"desktop\":\"nope\"}")).Error);
        Assert.Equal(TaskViewGuards.UnknownAction, (await Call(r, "desk", "{\"seq\":1,\"op\":\"format\"}")).Error);
        Assert.DoesNotContain(r.Platform.Calls, c => c.Verb.StartsWith("desktops.", StringComparison.Ordinal));
        var made = await Call(r, "desk", "{\"seq\":1,\"op\":\"create\"}");
        Assert.True(made.Ok);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", made.Result.GetProperty("created").GetString());
        Assert.Contains(r.Platform.Calls, c => c.Verb == "desktops.create" && c.ArgsJson.Contains("\"dry\":true", StringComparison.Ordinal));
        var sw = await Call(r, "desk", "{\"seq\":1,\"op\":\"switch\",\"desktop\":\"" + Desk1 + "\"}");
        Assert.True(sw.Ok);                                     // a test run never switches, so the session stays
        Assert.Contains(r.Platform.Calls, c => c.Verb == "desktops.switch" && c.ArgsJson.Contains("\"dry\":true", StringComparison.Ordinal));
        Assert.Single(s.Calls);
    }, test: true);

    [Fact]
    public void Esc_and_a_click_away_close_with_their_reasons_and_a_stale_dismiss_does_nothing() => T(async r =>
    {
        var s = new Surface();
        await Up(r, s);
        await WinTab(r, () => s.Calls.Count == 1);
        await Call(r, "dismiss", "{\"seq\":7,\"why\":\"esc\"}");
        Assert.Single(s.Calls);
        await Call(r, "dismiss", "{\"seq\":1,\"why\":\"esc\"}");
        Assert.Equal("end #1 Esc +keyboard", s.Calls.Last());
        await WinTab(r, () => s.Calls.Count == 3);
        await Call(r, "dismiss", "{\"seq\":2,\"why\":\"click-away\"}");
        Assert.Equal("end #2 clicked away +keyboard", s.Calls.Last());
    });

    [Fact]
    public void A_window_still_there_after_a_close_is_said_on_screen_and_a_test_run_never_closes() => T(async r =>
    {
        var s = new Surface();
        await Up(r, s);
        r.Platform.Answer("close", "{\"ok\":true}");
        await WinTab(r, () => s.Calls.Count == 1);
        var c = await Call(r, "close", "{\"seq\":1,\"hwnd\":\"101\"}");
        Assert.True(c.Ok);
        await Settle(r, () => s.Pushed.Any(p => p.Name == "taskview.notice"), 600);
        Assert.Equal(2, s.Pushed.Count(p => p.Name == "taskview.update"));
        Assert.Contains("\\u0022Notes\\u0022 is still open", s.Pushed.Single(p => p.Name == "taskview.notice").Json);
    });

    [Fact]
    public void A_test_run_refuses_to_close_a_window() => T(async r =>
    {
        var s = new Surface();
        var f = await Up(r, s);
        await f.Open("test");
        Assert.Single(s.Calls);
        var c = await Call(r, "close", "{\"seq\":1,\"hwnd\":\"101\"}");
        Assert.False(c.Ok);
        Assert.Equal("this is a test run, and a test run never closes your windows", c.Error);
        Assert.DoesNotContain(r.Platform.Calls, x => x.Verb == "close");
    }, test: true);

    [Fact]
    public void Turning_it_off_ends_the_session_and_stopping_drops_the_window() => T(async r =>
    {
        var s = new Surface();
        await Up(r, s);
        await WinTab(r, () => s.Calls.Count == 1);
        Assert.True(r.Host.TrySetEnabled("taskview", false, "the tray menu").Ok);
        await Settle(r, () => s.Calls.Count == 2);
        Assert.Equal("end #1 Task View was turned off +keyboard", s.Calls.Last());
        await r.Host.StopAllAsync("Mantle is quitting");
        Assert.StartsWith("drop ", s.Calls.Last(), StringComparison.Ordinal);
    });

    [Fact]
    public void A_game_rest_closes_Task_View_and_drops_its_window() => T(async r =>
    {
        var s = new Surface();
        await Up(r, s);
        await WinTab(r, () => s.Calls.Count == 1);
        await r.Host.RestAllAsync("VALORANT runs");
        await Settle(r, () => s.Calls.Any(c => c.StartsWith("drop ", StringComparison.Ordinal)));
        Assert.StartsWith("end #1 ", s.Calls[1], StringComparison.Ordinal);
        Assert.StartsWith("drop ", s.Calls.Last(), StringComparison.Ordinal);
    });

    [Fact]
    public void Learn_takes_the_windows_and_desktops_a_session_may_name()
    {
        var t = new TaskViewSession(1, "Win+Tab");
        TaskViewFeature.Learn(t, JsonDocument.Parse("{\"list\":[{\"hwnd\":5},{\"hwnd\":\"6\"},{\"title\":\"x\"}],\"desktops\":{\"list\":[{\"id\":\"" + Desk1 + "\"}]}}").RootElement);
        Assert.Equal(new[] { "5", "6" }, t.Hwnds.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { Desk1 }, t.Desks.ToArray());
        TaskViewFeature.Learn(t, JsonDocument.Parse("[]").RootElement);
        Assert.Empty(t.Hwnds);
    }
}
