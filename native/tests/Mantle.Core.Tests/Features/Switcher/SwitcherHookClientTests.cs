using System;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Features.Switcher;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features.Switcher;

// lib/switcher.js applyHook against a fake platform (SWI-132, SWI-133, and the hand-back request). Electron has no
// unit test of applyHook itself (PEND-012 gap): these check the request, the wait for an open Alt+Tab and the words.
public class SwitcherHookClientTests
{
    const string Working = "{\"installed\":true,\"winTap\":false,\"altTab\":true,\"winKeys\":true,\"tracking\":true,\"reason\":null}";
    static readonly SwitcherPage Ready = new(true, null);

    static (FakePlatform P, SwitcherHookClient C) Make(string answer = Working)
    {
        var p = new FakePlatform();
        p.SetStatus(PlatformState.Ok, "sidecar running");
        p.Answer("hotkey", answer);
        return (p, new SwitcherHookClient(p));
    }

    static ValueTask<HookStep> Apply(SwitcherHookClient c, SwitcherSettings? s = null, SwitcherPage? page = null, string? resting = null,
        bool featureUp = true, bool test = false, bool live = true, bool sessionOpen = false) =>
        c.ApplyAsync("test", s ?? SwitcherSettings.Default, page ?? Ready, resting, featureUp, true, test, live, sessionOpen);

    [Fact]
    public async Task Asks_for_altTab_and_winKeys_only_with_a_4_s_timeout_and_keeps_the_answer()
    {
        var (p, c) = Make();
        Assert.Equal(HookStep.Ask, await Apply(c));
        var call = Assert.Single(p.Calls);
        Assert.Equal("hotkey", call.Verb);
        Assert.Equal("{\"altTab\":true,\"winKeys\":true}", call.ArgsJson);
        Assert.Equal(new TimeSpan(0, 0, 4), call.Timeout);
        Assert.Equal(new HookAnswer(true, true, true, true, null), c.Hook);
        Assert.Null(c.HookErr);
        var st = SwitcherHook.StateWords(c.Facts(SwitcherSettings.Default, Ready, false, true));
        Assert.True(st.AltTab);
        Assert.Null(st.Broken);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("feature down")]
    [InlineData("page crashed")]
    [InlineData("resting")]
    public async Task Hand_back_the_sidecar_is_asked_for_altTab_false(string why)
    {
        var (p, c) = Make("{\"installed\":true,\"altTab\":false,\"winKeys\":true}");
        var settings = why == "off" ? new SwitcherSettings(false, true) : SwitcherSettings.Default;
        var page = why == "page crashed" ? new SwitcherPage(false, "the switcher page crashed (killed)", true) : Ready;
        await Apply(c, settings, page, why == "resting" ? "a game runs" : null, featureUp: why != "feature down");
        Assert.Equal("{\"altTab\":false,\"winKeys\":true}", Assert.Single(p.Calls).ArgsJson);
    }

    [Fact]
    public async Task An_open_Alt_Tab_defers_the_change_and_the_next_apply_sends_it()
    {
        var (p, c) = Make();
        Assert.Equal(HookStep.WaitForSession, await Apply(c, new SwitcherSettings(false, true), sessionOpen: true));
        Assert.Empty(p.Calls);
        Assert.Equal("test", c.PendingApply);
        var st = SwitcherHook.StateWords(c.Facts(SwitcherSettings.Default, Ready, false, true));
        Assert.Contains("until the Alt+Tab on screen ends", st.Reason);
        Assert.Null(st.Broken);
        Assert.Equal(HookStep.Ask, await Apply(c, new SwitcherSettings(false, true)));
        Assert.Null(c.PendingApply);
        Assert.Equal("{\"altTab\":false,\"winKeys\":true}", Assert.Single(p.Calls).ArgsJson);
    }

    [Fact]
    public async Task A_test_run_or_a_shell_not_taken_never_asks()
    {
        var (p, c) = Make();
        Assert.Equal(HookStep.PublishOnly, await Apply(c, test: true));
        Assert.Equal(HookStep.PublishOnly, await Apply(c, live: false));
        Assert.Equal(HookStep.Nothing, await c.ApplyAsync("x", SwitcherSettings.Default, Ready, null, true, false, false, true, false));
        Assert.Empty(p.Calls);
    }

    [Fact]
    public async Task A_sidecar_that_is_down_is_not_asked_and_the_state_says_why()
    {
        var (p, c) = Make();
        p.SetStatus(PlatformState.Down, "sidecar exited; restarting in 1 s");
        Assert.Equal(HookStep.Blocked, await Apply(c));
        Assert.Empty(p.Calls);
        Assert.Equal("the sidecar cannot be asked (sidecar exited; restarting in 1 s)", c.HookErr);
        var st = SwitcherHook.StateWords(c.Facts(SwitcherSettings.Default, Ready, false, true));
        Assert.False(st.AltTab);
        Assert.Contains("cannot be asked", st.Broken);
    }

    [Fact]
    public async Task A_stale_exe_is_named_once_and_an_empty_answer_is_said()
    {
        var p = new FakePlatform();
        p.SetStatus(PlatformState.Ok, "sidecar running");
        p.Handle("hotkey", _ => PlatformReply.Fail(PlatformOutcome.Failed, "hotkey needs winTap: true or false"));
        var c = new SwitcherHookClient(p);
        await Apply(c);
        Assert.Equal("the sidecar exe predates Mantle's Alt+Tab (it said: hotkey needs winTap: true or false): run npm run build:sidecar", c.HookErr);
        var st = SwitcherHook.StateWords(c.Facts(SwitcherSettings.Default, Ready, false, true));
        Assert.Equal(2, st.Broken!.Split("predates").Length);

        var (_, empty) = Make("null");
        await Apply(empty);
        Assert.Null(empty.Hook);
        Assert.Equal("the sidecar answered \"hotkey\" with nothing", empty.HookErr);
    }

    [Fact]
    public void Parse_reads_the_sidecar_status_fields_and_nothing_else()
    {
        using var doc = JsonDocument.Parse("{\"installed\":false,\"altTab\":\"yes\",\"reason\":\"SetWindowsHookEx failed: access denied\"}");
        Assert.Equal(new HookAnswer(false, null, null, null, "SetWindowsHookEx failed: access denied"), SwitcherHookClient.Parse(doc.RootElement));
        using var arr = JsonDocument.Parse("[1]");
        Assert.Null(SwitcherHookClient.Parse(arr.RootElement));
        Assert.Null(SwitcherHookClient.Parse(default));
    }

    [Fact]
    public void The_log_line_is_why_then_both_reasons()
    {
        string? line = null;
        var c = new SwitcherHookClient(new FakePlatform(), l => line = l);
        c.LogState("the tray menu turned altTab off", SwitcherHook.StateWords(new SwitcherFacts(new SwitcherSettings(false, true), Ready)));
        Assert.Equal("the tray menu turned altTab off: Alt+Tab is Windows' own: Mantle's switcher is turned off in the tray menu; Win+N and Win+A open Windows' own panels: the sidecar has not confirmed the hook yet", line);
    }
}
