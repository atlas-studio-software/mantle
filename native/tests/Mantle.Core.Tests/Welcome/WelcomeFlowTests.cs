using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mantle.Core.Data;
using Mantle.Core.Features.Welcome;
using Xunit;

namespace Mantle.Core.Tests.Welcome;

/// <summary>
/// The welcome's cards, page flow and host (WEL-010, 011, 014..019, 024, 026..046, 048, 053), from lib/welcome.test.js,
/// ui/welcome/welcome.page.test.js and lib/welcome.e2e.test.js. Every profile is a TestDir under %TEMP%; every write goes
/// through a scratch (or closed) gate. No surface is drawn.
/// </summary>
public sealed class WelcomeFlowTests
{
    const string Now = "2026-11-24T12:00:00.000Z";
    static string FixedNow() => Now;
    static SafeFile Scratch(TestDir d) => new(WriteGate.Scratch(d.Path));

    static (WelcomeHost Host, WelcomeRecorder Rec, List<string> Log) Make(TestDir d, bool test = false, bool optIn = false, SafeFile? files = null, bool packaged = false)
    {
        var log = new List<string>();
        var v = FirstRun.Judge(d.Path, test, optIn, FixedNow);
        var rec = new WelcomeRecorder(v, d.Path, test, files ?? Scratch(d), log.Add, FixedNow);
        return (new WelcomeHost(rec, packaged, log.Add), rec, log);
    }

    static WelcomeFlag? FlagOf(TestDir d) => FirstRun.ReadFlag(d.Path).Flag;

    // ---- WEL-010 / 011 ----

    [Fact]
    public void First_run_outside_a_test_run_is_an_argument_error()
    {
        Assert.Equal(WelcomeHost.ArgError, WelcomeHost.CheckArgs(test: false, firstRun: true));
        Assert.Null(WelcomeHost.CheckArgs(test: true, firstRun: true));
        Assert.Null(WelcomeHost.CheckArgs(test: false, firstRun: false));
        Assert.Null(WelcomeHost.CheckArgs(test: true, firstRun: false));
    }

    [Fact]
    public void Record_writes_only_once_this_process_holds_the_single_instance_lock()
    {
        using var d = new TestDir("welcome-lock");
        var (host, _, _) = Make(d);
        var no = host.Record(holdsLock: false);
        Assert.True(no.Ok);
        Assert.False(no.Wrote);
        Assert.False(File.Exists(d.File(FirstRun.FlagFile)));
        Assert.Empty(Directory.GetFileSystemEntries(d.Path));

        Assert.True(host.Record(holdsLock: true).Ok);
        Assert.Equal("pending", FlagOf(d)!.State);
    }

    // ---- WEL-014..017 ----

    [Fact]
    public void Start_logs_the_verdict_once_and_asks_for_the_show_delay_only_when_due()
    {
        using var fresh = new TestDir("welcome-start");
        var (host, _, log) = Make(fresh);
        Assert.Equal(1200, host.Start());
        Assert.Null(host.Start());
        Assert.Single(log, l => l.StartsWith("due: ", StringComparison.Ordinal));

        using var used = new TestDir("welcome-start-used");
        used.Write("theme.json", "{}");
        var (h2, _, log2) = Make(used);
        Assert.Null(h2.Start());
        Assert.StartsWith("not shown: ", log2.Single());
    }

    [Fact]
    public void The_window_is_centred_and_clamped_to_the_work_area_and_never_navigates()
    {
        var big = WelcomeHost.Place(new WelcomeRect(0, 0, 1920, 1040));
        Assert.Equal(new WelcomeRect((1920 - FirstRun.WinW) / 2, (1040 - FirstRun.WinH) / 2, FirstRun.WinW, FirstRun.WinH), big);
        var small = WelcomeHost.Place(new WelcomeRect(100, 50, 500, 400));
        Assert.Equal(new WelcomeRect(100, 50, 500, 400), small);
        Assert.Equal(1, WelcomeHost.Place(new WelcomeRect(0, 0, FirstRun.WinW + 1, FirstRun.WinH + 1)).X);   // 0.5 rounds up, as JS

        using var d = new TestDir("welcome-spec");
        var (host, _, _) = Make(d);
        var o = host.Open("first start", new WelcomeRect(0, 0, 1920, 1040));
        Assert.True(o.Opened);
        var s = o.Spec!;
        Assert.Equal(FirstRun.Title, s.Title);
        Assert.True(s.Frameless && s.Clear);
        Assert.False(s.Resizable || s.Minimizable || s.Maximizable || s.Fullscreenable || s.MayNavigate || s.MayOpenWindows);
    }

    [Fact]
    public void Focus_help_comes_only_outside_a_test_when_the_window_is_not_focused_and_its_failure_is_logged()
    {
        using var d = new TestDir("welcome-focus");
        var (host, _, log) = Make(d);
        host.Open("first start");
        Assert.True(host.ReadyToShow());
        Assert.Contains("shown (first start)", log);
        Assert.False(host.NeedsFocusHelp(isFocused: true));
        Assert.True(host.NeedsFocusHelp(isFocused: false));
        host.FocusHelpResult(false, "foreground lock");
        Assert.Contains(log, l => l.EndsWith("a click in it gives it the keyboard", StringComparison.Ordinal) && l.Contains("foreground lock"));

        using var t = new TestDir("welcome-focus-test");
        var (th, _, _) = Make(t, test: true, optIn: true);
        th.Open("first start");
        Assert.False(th.ReadyToShow());
        Assert.False(th.NeedsFocusHelp(isFocused: false));
    }

    [Fact]
    public void A_renderer_crash_or_load_failure_closes_it_and_the_welcome_stays_pending()
    {
        using var d = new TestDir("welcome-crash");
        var (host, _, log) = Make(d);
        host.Record(true);
        host.Open("first start");
        host.RendererGone("crashed");
        Assert.False(host.IsOpen);
        Assert.False(host.Closed(quitting: false).Recorded);
        Assert.Equal("pending", FlagOf(d)!.State);
        Assert.Single(log, l => l.Contains("renderer died"));

        host.Open("first start");
        Assert.False(host.LoadFailed("ERR_FILE_NOT_FOUND", isMainFrame: false));   // a sub-frame does not count
        Assert.True(host.LoadFailed("ERR_FILE_NOT_FOUND"));
        Assert.False(host.Closed(quitting: false).Recorded);
        Assert.Equal("pending", FlagOf(d)!.State);
        Assert.True(FirstRun.Judge(d.Path, false, false, FixedNow).Show);
    }

    // ---- WEL-018 / 019 ----

    [Fact]
    public void Show_again_from_Settings_opens_it_and_a_second_ask_gives_the_same_window()
    {
        Assert.Equal("Show again", WelcomeHost.SettingsButton);
        Assert.Equal("The three cards from Mantle’s first start: what changed, the keys, and how to switch back to Windows’ taskbar", WelcomeHost.SettingsHelp);
        using var d = new TestDir("welcome-again");
        d.Write("theme.json", "{}");
        var (host, _, _) = Make(d);
        var first = host.Show(WelcomeHost.SettingsVia);
        Assert.True(first.Opened);
        Assert.Equal("asked from Settings", host.Via);
        var again = host.Show(WelcomeHost.SettingsVia);
        Assert.True(again.Ok && again.Already && !again.Opened);
        Assert.Equal("The welcome is already open", WelcomeHost.AlreadyOpenNote);
        Assert.Equal("a page", WelcomeHost.CleanVia("<>!"));
        Assert.Equal("a page", WelcomeHost.CleanVia(null));
        Assert.Equal(30, WelcomeHost.CleanVia(new string('x', 40)).Length);
    }

    // ---- WEL-024 / 026 ----

    [Fact]
    public void A_failed_record_keeps_the_window_and_the_card_shows_the_reason()
    {
        using var d = new TestDir("welcome-fail");
        var (host, _, _) = Make(d, files: new SafeFile(WriteGate.Never("this run is not --live")));
        host.Open("first start");
        var flow = new WelcomeFlow();
        var ask = flow.SkipAll();
        Assert.Equal(WelcomeAsk.Done, ask.Ask);
        var ans = host.Done(ask.How);
        Assert.False(ans.Result.Ok);
        Assert.False(ans.CloseWindow);
        Assert.True(host.IsOpen);
        var back = flow.DoneAnswer(ans.Result.Ok, ans.Result.Reason);
        Assert.False(flow.Leaving);
        Assert.Equal("next", back.Focus);
        Assert.StartsWith("That could not be saved: ", flow.Error);
        Assert.EndsWith(". The welcome may show again next time.", flow.Error);
        Assert.False(File.Exists(d.File(FirstRun.FlagFile)));
        Assert.Equal(WelcomeAsk.Done, flow.SkipAll().Ask);                     // can be tried again
    }

    [Fact]
    public void State_answers_via_test_packaged_firstRun_and_reason_and_the_page_follows_it()
    {
        using var d = new TestDir("welcome-state");
        var (host, rec, _) = Make(d, test: true, optIn: true, packaged: true);
        var s = host.State();
        Assert.Equal("first start", s.Via);
        Assert.True(s.Ok && s.Test && s.Packaged && s.FirstRun);
        Assert.Equal(rec.Verdict.Reason, s.Reason);

        var flow = new WelcomeFlow();
        Assert.False(flow.ChipVisible);
        Assert.Equal("For good", flow.Ways[2].Name);
        Assert.Contains("npm run autostart:remove", flow.Ways[2].Code);
        flow.State(s.Test, s.Packaged);
        Assert.True(flow.ChipVisible);
        Assert.Same(WelcomeCards.Installed, flow.Ways[2]);
        Assert.Equal(3, flow.Ways.Count);
    }

    // ---- WEL-027..041: the words ----

    [Fact]
    public void The_cards_say_exactly_what_content_js_says()
    {
        Assert.Equal(new[] { "changed", "keys", "back" }, WelcomeCards.Cards.Select(c => c.Id));
        Assert.Equal(new[] { "What changed", "The keys", "Switching back" }, WelcomeCards.Cards.Select(c => c.Step));
        Assert.Equal("A new shell, with Windows underneath", WelcomeCards.Cards[0].Title);
        Assert.Equal("Mantle takes over the taskbar and Start. Your apps, files and Windows settings are exactly where you left them.", WelcomeCards.Cards[0].Lead);
        Assert.Equal(new[] { "The bar", "The top bar", "The launcher" }, WelcomeCards.Items.Select(i => i.Name));
        Assert.Contains("every 6 hours", WelcomeCards.NetworkFoot);
        Assert.Contains("Akamai/Linode servers every 2 seconds", WelcomeCards.NetworkFoot);
        Assert.EndsWith("Check for Mantle updates, Top menu bar.", WelcomeCards.NetworkFoot);
        Assert.Equal("The keys you know, answered by Mantle", WelcomeCards.Cards[1].Title);
        Assert.Equal(new[] { "Win", "Alt+Tab", "Win+Shift+S", "Win+Tab", "Win+I" }, WelcomeCards.Keys.Select(k => string.Join("+", k.Keys)));
        Assert.Equal(new[] { "Open the launcher", "Switch windows", "Screenshot an area", "Task view", "Mantle Settings" }, WelcomeCards.Keys.Select(k => k.What));
        Assert.Equal("Alt+Space works too", WelcomeCards.Keys[0].Note);
        Assert.Equal("Copied to the clipboard; Save keeps a file", WelcomeCards.Keys[2].Note);
        Assert.Equal("Every key Mantle answers is listed in Mantle Settings, under Keyboard shortcuts.", WelcomeCards.KeysFoot);
        Assert.Equal("Want Windows’ taskbar back?", WelcomeCards.Cards[2].Title);
        Assert.Equal(new[] { "For now", "Quit", "For good" }, WelcomeCards.Ways.Select(w => w.Name));
        Assert.Equal("restore", WelcomeCards.Ways[0].Action);
        Assert.Equal(new[] { "Ctrl", "Alt", "Shift", "Backspace" }, WelcomeCards.Ways[1].Keys!.ToArray());
        Assert.StartsWith("Uninstall Mantle in Windows Settings, under Apps > Installed apps.", WelcomeCards.Installed.Text);
        Assert.Equal("Skip the welcome (Esc)", WelcomeCards.SkipTitle);
        Assert.Equal("Test run", WelcomeCards.TestChip);
    }

    [Fact]
    public void Hot_corners_list_only_the_corners_that_are_on_with_exactly_one_colon()
    {
        var def = WelcomeCards.Corners(null);
        Assert.False(def.Live);
        Assert.Equal(new[] { "Top-left: Task view", "Bottom-right: Show desktop" }, def.Lines.Select(l => l.Name + " " + l.Action));
        Assert.Equal(WelcomeCards.CornersHow, def.How);
        Assert.Equal("Top-left:", WelcomeCards.CornerName("Top-left: "));
        Assert.Equal("Top-left:", WelcomeCards.CornerName("Top-left："));

        var live = WelcomeCards.Corners(new CornersState(new Dictionary<string, string> { ["tl"] = "launcher", ["tr"] = "lock", ["bl"] = "qs", ["br"] = "off" }, true));
        Assert.True(live.Live);
        Assert.Equal(new[] { "Top-left: Launcher", "Top-right: Lock screen", "Bottom-left: Quick settings" }, live.Lines.Select(l => l.Name + " " + l.Action));
        Assert.All(live.Lines, l => Assert.Equal(1, l.Name.Count(ch => ch == ':')));

        var off = WelcomeCards.Corners(new CornersState(null, false));
        Assert.True(off.Off);
        Assert.Null(off.How);
        Assert.Equal("Hot corners are off. Turn them on in Mantle Settings, under Mantle.", off.Lines.Single().Action);
        var allOff = WelcomeCards.Corners(new CornersState(new Dictionary<string, string> { ["tl"] = "off", ["tr"] = "off", ["bl"] = "off", ["br"] = "off" }, true));
        Assert.Equal("off", allOff.Lines.Single().Key);

        var flow = new WelcomeFlow();
        flow.CornersAnswer(null);                                              // a failure keeps the defaults
        Assert.False(flow.Corners.Live);
        flow.CornersAnswer(new CornersState(new Dictionary<string, string> { ["br"] = "taskview" }, true));
        Assert.Equal("Bottom-right: Task view", flow.Corners.Lines.Single().Name + " " + flow.Corners.Lines.Single().Action);
    }

    // ---- WEL-038: restore runs only on the user's click ----

    [Fact]
    public void Restore_is_asked_only_by_its_click_once_per_click_and_can_be_retried_after_a_failure()
    {
        var flow = new WelcomeFlow();
        foreach (var k in new[] { "Enter", "ArrowRight", "ArrowLeft", "PageDown", "PageUp", "Escape", "r", " " })
            Assert.NotEqual(WelcomeAsk.Restore, new WelcomeFlow().Key(k).Ask);
        Assert.Equal(WelcomeCards.RestoreLabel, flow.RestoreText);
        Assert.Equal(WelcomeAsk.Restore, flow.RestoreClick().Ask);
        Assert.Equal(WelcomeCards.RestoreBusy, flow.RestoreText);
        Assert.False(flow.RestoreEnabled);
        Assert.Equal(WelcomeAsk.None, flow.RestoreClick().Ask);               // busy: one call per click
        Assert.Equal("restore", flow.RestoreAnswer(false, "the sidecar is not running").Focus);
        Assert.Equal("Could not restore: the sidecar is not running", flow.RestoreAlert);
        Assert.True(flow.RestoreEnabled);
        Assert.Equal(WelcomeAsk.Restore, flow.RestoreClick().Ask);
        Assert.Equal("next", flow.RestoreAnswer(true, null).Focus);
        Assert.Equal(WelcomeCards.RestoreDone, flow.RestoreText);
        Assert.Null(flow.RestoreAlert);
        Assert.Equal(WelcomeAsk.None, flow.RestoreClick().Ask);
    }

    // ---- WEL-042..046, 048 ----

    [Fact]
    public void Buttons_and_step_markers_follow_the_card()
    {
        var flow = new WelcomeFlow();
        Assert.Equal("Next", flow.Primary);
        Assert.Equal("Next (Right arrow or Enter)", flow.PrimaryTitle);
        Assert.False(flow.BackVisible);
        Assert.Equal("What changed", flow.Counter);
        Assert.Equal(new[] { "Step 1 of 3: What changed", "Step 2 of 3: The keys", "Step 3 of 3: Switching back" }, flow.Steps().Select(s => s.AriaLabel));
        Assert.True(flow.Steps()[0].Current);
        Assert.Equal(new[] { "step:0", "step:1", "step:2", "skip", "next" }, flow.TabOrder().ToArray());

        Assert.Equal("step:2", flow.Go(2, "step").Focus);
        Assert.True(flow.Steps()[2].Current);
        Assert.Equal("Start using Mantle", flow.Primary);
        Assert.Equal("Start using Mantle (Enter)", flow.PrimaryTitle);
        Assert.Equal(new[] { "step:0", "step:1", "step:2", "skip", "restore", "back", "next" }, flow.TabOrder().ToArray());
        Assert.Equal(WelcomeAsk.None, flow.Go(2).Ask);                        // the same card: nothing
        Assert.Equal(WelcomeAsk.None, flow.Go(5).Ask);
        Assert.Equal(140, WelcomeFlow.ExitDelay(false));
        Assert.Equal(0, WelcomeFlow.ExitDelay(true));
    }

    [Fact]
    public void Keys_move_between_cards_never_finish_by_arrow_and_skip_or_finish_ask_exactly_once()
    {
        var flow = new WelcomeFlow();
        flow.Key("ArrowLeft");
        Assert.Equal(0, flow.Index);                                           // stays on card 1
        flow.Key("ArrowRight"); flow.Key("PageDown");
        Assert.Equal(2, flow.Index);
        Assert.Equal(WelcomeAsk.None, flow.Key("ArrowRight").Ask);            // does not finish on the last card
        Assert.Equal(WelcomeAsk.None, flow.Key("Enter", onButton: true).Ask);
        Assert.Equal(WelcomeAsk.None, flow.Key("Escape", ctrl: true).Ask);
        Assert.Equal(WelcomeAsk.None, flow.Key("Enter", alt: true).Ask);
        flow.Key("PageUp");
        Assert.Equal(1, flow.Index);
        flow.Key("Enter");
        var fin = flow.Key("Enter");
        Assert.Equal(new WelcomeStep(WelcomeAsk.Done, "finished"), fin);
        Assert.Equal(WelcomeAsk.None, flow.Key("Escape").Ask);                // leaving: ignored
        Assert.Equal(WelcomeAsk.None, flow.NextOrFinish().Ask);
        Assert.Equal(WelcomeAsk.None, flow.SkipAll().Ask);
        Assert.Equal(1, flow.DoneAsks);

        var skip = new WelcomeFlow();
        Assert.Equal(new WelcomeStep(WelcomeAsk.Done, "skipped"), skip.Key("Escape"));
        Assert.Equal(WelcomeAsk.None, skip.SkipAll().Ask);
        Assert.Equal(1, skip.DoneAsks);
    }

    // ---- WEL-053: the whole flow on a temp profile ----

    [Fact]
    public void Live_flow_fresh_pending_quit_returns_esc_skips_next_start_quiet_show_again_finishes()
    {
        using var d = new TestDir("welcome-e2e");
        var (h1, _, _) = Make(d);
        Assert.Equal(FirstRun.ShowDelayMs, h1.Start());
        h1.Record(holdsLock: true);
        Assert.Equal("pending", FlagOf(d)!.State);
        h1.Open("first start");
        Assert.False(h1.Closed(quitting: true).Recorded);                     // quit halfway

        var (h2, _, _) = Make(d);
        Assert.NotNull(h2.Start());                                            // comes back
        h2.Record(true);
        h2.Open("first start");
        var flow = new WelcomeFlow();
        var esc = flow.Key("Escape");
        var ans = h2.Done(esc.How);
        Assert.True(ans.Result.Recorded && ans.CloseWindow);
        Assert.False(h2.Closed(quitting: false).Recorded);                    // the close after an answer adds nothing
        Assert.Equal(("done", "skipped"), (FlagOf(d)!.State, FlagOf(d)!.How));

        var (h3, _, log3) = Make(d);
        Assert.Null(h3.Start());                                               // next start never shows
        Assert.StartsWith("not shown: ", log3[0]);
        Assert.True(h3.Show("Settings").Opened);                               // Show again reopens
        var again = new WelcomeFlow();
        again.NextOrFinish(); again.NextOrFinish();
        var fin = again.NextOrFinish();
        Assert.Equal("finished", fin.How);
        Assert.True(h3.Done(fin.How).Result.Recorded);
        Assert.Equal(("done", "finished"), (FlagOf(d)!.State, FlagOf(d)!.How));
        Assert.Equal(new[] { FirstRun.FlagFile }, Directory.GetFileSystemEntries(d.Path).Select(f => Path.GetFileName(f)).ToArray());
    }

    // SAFETY: a plain --test run writes nothing through the whole flow, and the flow only ever writes the flag.
    [Fact]
    public void A_plain_test_run_records_nothing_through_the_whole_flow()
    {
        using var d = new TestDir("welcome-e2e-test");
        var (host, _, _) = Make(d, test: true);
        Assert.Null(host.Start());
        Assert.False(host.Record(true).Wrote);
        host.Show("Settings");
        var ans = host.Done("skipped");
        Assert.True(ans.Result.Ok);
        Assert.False(ans.Result.Recorded);
        Assert.True(ans.CloseWindow);
        Assert.Equal("a test run without --first-run: nothing is recorded", ans.Result.Reason);
        Assert.Empty(Directory.GetFileSystemEntries(d.Path));
    }
}
