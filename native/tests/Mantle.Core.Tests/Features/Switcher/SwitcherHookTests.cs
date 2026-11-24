using System.Linq;
using Mantle.Core.Features.Switcher;
using Mantle.Core.State;
using Xunit;

namespace Mantle.Core.Tests.Features.Switcher;

// lib/switcher.test.js stateCases() and the rest checks, ported, plus applyHook's request and order of checks
// (SWI-128..SWI-130, SWI-132, SWI-133, SWI-134 (words), SWI-135, SWI-139 (words)).
public class SwitcherHookTests
{
    static readonly SwitcherFacts Base = new(
        SwitcherSettings.Default,
        new SwitcherPage(true, null),
        new HookAnswer(true, true, true, true, null));

    static readonly SwitcherPage Ready = new(true, null);

    // ---- the hand-back: when Alt+Tab is NOT Mantle's, the sidecar is asked for altTab false -------------

    [Fact]
    public void Alt_Tab_is_asked_for_only_when_on_up_drawable_and_not_resting()
    {
        Assert.Equal((true, true), SwitcherHook.Want(SwitcherSettings.Default, Ready, null, featureUp: true));
    }

    [Fact]
    public void Hand_back_Alt_Tab_goes_to_Windows_when_off_unhealthy_or_resting()
    {
        // off (the tray / Settings switch)
        Assert.False(SwitcherHook.Want(new SwitcherSettings(false, true), Ready, null, true).AltTab);
        // unhealthy: the feature is down, or the page crashed / never loaded (Mantle never switches blind)
        Assert.False(SwitcherHook.Want(SwitcherSettings.Default, Ready, null, featureUp: false).AltTab);
        Assert.False(SwitcherHook.Want(SwitcherSettings.Default, new SwitcherPage(false, "the switcher page crashed (killed)", true), null, true).AltTab);
        Assert.False(SwitcherHook.Want(SwitcherSettings.Default, new SwitcherPage(false, "the switcher page has not loaded yet"), null, true).AltTab);
        // resting while a game runs, even if a page still says ready
        Assert.False(SwitcherHook.Want(SwitcherSettings.Default, Ready, "VALORANT runs", true).AltTab);
        // Win+N / Win+A are their own switch and stay Mantle's while it rests
        Assert.True(SwitcherHook.Want(SwitcherSettings.Default, new SwitcherPage(false, "resting while a game runs"), "VALORANT runs", true).WinKeys);
        Assert.False(SwitcherHook.Want(new SwitcherSettings(true, false), Ready, null, true).WinKeys);
    }

    [Fact]
    public void The_ask_waits_for_an_open_Alt_Tab_and_test_runs_never_ask()
    {
        Assert.Equal(HookStep.Nothing, SwitcherHook.Next(false, false, true, false, null));
        Assert.Equal(HookStep.PublishOnly, SwitcherHook.Next(true, true, true, false, null));
        Assert.Equal(HookStep.PublishOnly, SwitcherHook.Next(true, false, false, false, null));
        Assert.Equal(HookStep.WaitForSession, SwitcherHook.Next(true, false, true, true, null));
        Assert.Equal(HookStep.Blocked, SwitcherHook.Next(true, false, true, false, "restarting"));
        Assert.Equal(HookStep.Ask, SwitcherHook.Next(true, false, true, false, null));
        Assert.Equal(HookStep.Ask, SwitcherHook.Next(true, false, true, false, ""));
        Assert.Equal(4000, SwitcherHook.AskTimeoutMs);
        Assert.Equal("the sidecar cannot be asked (restarting)", SwitcherHook.BlockedWords("restarting"));
    }

    [Fact]
    public void A_stale_exe_is_named_and_any_other_failure_is_said_as_it_came()
    {
        Assert.Equal("the sidecar exe predates Mantle's Alt+Tab (it said: hotkey needs winTap): run npm run build:sidecar",
            SwitcherHook.AskFailedWords("hotkey needs winTap"));
        Assert.Equal("hotkey needs winTap, altTab or winKeys: true or false", SwitcherHook.AskFailedWords("hotkey needs winTap, altTab or winKeys: true or false"));
        Assert.Equal("the sidecar is not running (it exited)", SwitcherHook.AskFailedWords("the sidecar is not running (it exited)"));
    }

    // ---- stateWords ---------------------------------------------------------------------------------------

    [Fact]
    public void Working_Alt_Tab_and_Win_N_A_are_Mantles_nothing_broken()
    {
        var st = SwitcherHook.StateWords(Base);
        Assert.True(st.AltTab);
        Assert.True(st.WinKeys);
        Assert.Null(st.Broken);
        Assert.Equal("Alt+Tab opens Mantle's switcher", st.Reason);
        Assert.Equal("ready", st.Page);
        Assert.Equal(new SwitcherWant(true, true), st.Want);
    }

    [Fact]
    public void Stale_exe_is_broken_says_predates_and_the_fix_once_for_both()
    {
        var st = SwitcherHook.StateWords(Base with
        {
            HookErr = "the sidecar exe predates Mantle's Alt+Tab (it said: hotkey needs winTap): run npm run build:sidecar",
            Hook = null,
        });
        Assert.NotNull(st.Broken);
        Assert.Contains("predates", st.Broken);
        Assert.Contains("build:sidecar", st.Broken);
        Assert.Equal(2, st.Broken!.Split("predates").Length);
        Assert.Contains("Win+N and Win+A too", st.Broken);
    }

    [Fact]
    public void Refused_hook_is_broken_with_the_sidecars_reason()
    {
        var st = SwitcherHook.StateWords(Base with { Hook = new HookAnswer(false, false, false, null, "SetWindowsHookEx failed: access denied") });
        Assert.Contains("SetWindowsHookEx failed", st.Broken);
    }

    [Fact]
    public void Quitting_is_not_broken_and_says_on_purpose()
    {
        foreach (var q in new[]
        {
            Base with { Hook = new HookAnswer(false, false, false, null, null) },
            Base with { Hook = null, HookErr = "the sidecar is not running (sidecar stopped because Mantle is shutting down)" },
        })
        {
            var st = SwitcherHook.StateWords(q with { Quitting = "Mantle is quitting (a second instance asked Mantle to quit)" });
            Assert.Null(st.Broken);
            Assert.False(st.AltTab);
            Assert.False(st.WinKeys);
            Assert.Equal("Alt+Tab is Windows' own: Mantle is quitting (a second instance asked Mantle to quit); the helper and its keyboard hook were stopped on purpose", st.Reason);
            Assert.DoesNotContain("did not install", st.Reason + st.WinKeysReason);
            Assert.DoesNotContain("not running", st.Reason + st.WinKeysReason);
        }
    }

    [Fact]
    public void Resting_Alt_Tab_is_Windows_not_broken_Win_N_A_kept()
    {
        var st = SwitcherHook.StateWords(Base with
        {
            Resting = "VALORANT runs",
            Page = new SwitcherPage(false, "resting while VALORANT runs", false),
            Hook = new HookAnswer(true, false, true, true, null),
        });
        Assert.False(st.AltTab);
        Assert.Contains("Windows' own while Mantle rests (VALORANT runs)", st.Reason);
        Assert.Null(st.Broken);
        Assert.True(st.WinKeys);
    }

    [Fact]
    public void Crashed_page_breaks_Alt_Tab_only()
    {
        var st = SwitcherHook.StateWords(Base with { Page = new SwitcherPage(false, "the switcher page crashed (killed)", true) });
        Assert.Contains("crashed", st.Broken);
        Assert.True(st.WinKeys);
        Assert.DoesNotContain("Win+N", st.Broken);
        Assert.EndsWith(", and Mantle will not switch windows blind", st.Reason);
    }

    [Fact]
    public void First_load_still_under_way_is_not_broken()
    {
        var st = SwitcherHook.StateWords(Base with { Page = new SwitcherPage(false, "the switcher page has not loaded yet"), Hook = null });
        Assert.Null(st.Broken);
        Assert.False(st.AltTab);
        Assert.Equal("down", st.Page);
    }

    [Fact]
    public void Both_switched_off_in_the_tray_is_not_broken()
    {
        var st = SwitcherHook.StateWords(Base with { Settings = new SwitcherSettings(false, false), Hook = new HookAnswer(true, false, false) });
        Assert.Null(st.Broken);
        Assert.Contains("turned off", st.Reason);
        Assert.Equal("Win+N and Win+A open Windows' own panels: turned off in the tray menu", st.WinKeysReason);
    }

    [Fact]
    public void Alt_Tab_off_Win_N_A_on_sidecar_dead_is_broken_for_Win_N_A_only()
    {
        var st = SwitcherHook.StateWords(Base with { Settings = new SwitcherSettings(false, true), HookErr = "the sidecar is not running (it exited)", Hook = null });
        Assert.StartsWith("Win+N and Win+A open Windows' own panels: the sidecar is not running", st.Broken);
    }

    [Fact]
    public void Test_mode_is_never_broken()
    {
        var st = SwitcherHook.StateWords(Base with { Test = true, Hook = null, HookErr = "anything" });
        Assert.Null(st.Broken);
        Assert.Contains("test mode", st.Reason);
    }

    [Fact]
    public void Windows_put_back_is_not_broken()
    {
        var st = SwitcherHook.StateWords(Base with { Live = false, ReleasedWhy = "the tray menu", Hook = null });
        Assert.Null(st.Broken);
        Assert.Contains("put Windows back", st.Reason);
        Assert.Equal("Alt+Tab is Windows' own: Mantle has not taken over the shell yet",
            SwitcherHook.StateWords(Base with { Live = false, Hook = null }).Reason);
    }

    [Fact]
    public void Asked_answer_not_in_yet_is_not_broken()
    {
        var st = SwitcherHook.StateWords(Base with { Hook = null, Applying = true });
        Assert.Null(st.Broken);
        Assert.Contains("not confirmed", st.Reason);
    }

    [Fact]
    public void Answered_without_turning_Alt_Tab_on_is_broken_but_not_while_a_newer_ask_is_in_flight()
    {
        var h = new HookAnswer(true, false, true, null, null);
        var st = SwitcherHook.StateWords(Base with { Hook = h });
        Assert.Contains("answered without", st.Broken);
        Assert.Null(SwitcherHook.StateWords(Base with { Hook = h, Applying = true }).Broken);
        Assert.EndsWith("turning Mantle's Alt+Tab on (low on hooks)",
            SwitcherHook.StateWords(Base with { Hook = h with { Reason = "low on hooks" } }).Reason);
    }

    [Fact]
    public void A_change_waiting_for_the_Alt_Tab_on_screen_is_not_broken()
    {
        var st = SwitcherHook.StateWords(Base with { Hook = new HookAnswer(true, false, false), PendingApply = true });
        Assert.Null(st.Broken);
        Assert.Contains("until the Alt+Tab on screen ends", st.Reason);
    }

    [Fact]
    public void Stacking_order_instead_of_MRU_is_working_and_says_so()
    {
        var st = SwitcherHook.StateWords(Base with { Hook = new HookAnswer(true, true, true, false) });
        Assert.True(st.AltTab);
        Assert.Contains("stacking order", st.Reason);
        Assert.Null(st.Broken);
    }

    [Fact]
    public void A_settings_file_note_is_appended_to_the_reason()
    {
        var st = SwitcherHook.StateWords(Base with { SettingsNote = "switcher.json could not be read; both switches are at their defaults (on)" });
        Assert.Equal("Alt+Tab opens Mantle's switcher; switcher.json could not be read; both switches are at their defaults (on)", st.Reason);
    }

    // ---- rest words and the tray lines --------------------------------------------------------------------

    [Fact]
    public void Rest_words_say_the_kind_never_the_game()
    {
        Assert.Equal("a game runs", SwitcherHook.RestWords("VALORANT runs"));
        Assert.Equal("a game runs", SwitcherHook.RestWords("javaw.exe runs"));
        Assert.DoesNotContain("VALORANT", SwitcherHook.RestWords("VALORANT runs"));
        Assert.Equal("game mode is on", SwitcherHook.RestWords("game mode is on"));
        Assert.Equal("game mode is on", SwitcherHook.RestWords("Game Mode is on"));
        Assert.Equal("a game runs", SwitcherHook.RestWords(null));
    }

    [Fact]
    public void Tray_lines_are_indented_and_cut_at_110_characters()
    {
        Assert.Equal("Alt+Tab opens Mantle's switcher", SwitcherHook.TrayAltTabLabel);
        Assert.Equal("Win+N / Win+A open Mantle's panels", SwitcherHook.TrayWinKeysLabel);
        Assert.Equal("      short", SwitcherHook.TrayLine("short"));
        Assert.Equal("      ", SwitcherHook.TrayLine(null));
        string exact = new string('a', 110), longer = new string('b', 111);
        Assert.Equal("      " + exact, SwitcherHook.TrayLine(exact));
        Assert.Equal("      " + new string('b', 107) + "...", SwitcherHook.TrayLine(longer));
        Assert.Equal(6 + 110, SwitcherHook.TrayLine(longer).Length);
        Assert.True(SwitcherHook.TrayLine(longer).Skip(6).All(c => c == 'b' || c == '.'));
    }
}
