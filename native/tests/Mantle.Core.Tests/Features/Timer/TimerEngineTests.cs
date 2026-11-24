using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Timers;
using Xunit;

namespace Mantle.Core.Tests.Features.Timers;

// lane r10-timer: the timer's state machine (lib/features/timer.js; cases from lib/features/timer.test.js) on a fake
// host: a hand-moved wall clock and monotonic count, an in-memory "store" (no file at all), a fake notices service.
public class TimerEngineTests
{
    const long S = 1000, M = 60 * S, H = 60 * M;
    const long T0 = 1_790_000_000_000;

    sealed class Host : ITimerHost
    {
        public long Now { get; set; } = T0;
        public long Mono { get; set; } = 5_000;
        public bool Enabled { get; set; } = true;
        public bool Test { get; set; }
        public (bool Known, string? Game) Resting { get; set; } = (true, null);
        public readonly List<string> Lines = new();
        public readonly List<(bool Ok, string? Reason)> Healths = new();
        public readonly List<TimerDoneNotice> Shown = new();
        public readonly List<string> Beeped = new();
        public JsonObject Doc = new();
        public int Saves, Changes, Closes;
        public long? Armed;
        public string? SaveFails;
        public TimerNoticeResult NoticeAnswer = TimerNoticeResult.Ok;
        public JsonObject? DocAtShow;

        public void Log(string text) => Lines.Add(text);
        public void Health(bool ok, string? reason) => Healths.Add((ok, reason));
        public ValueTask<StoreWrite> Save(Action<JsonObject> edit)
        {
            Saves++;
            if (SaveFails != null) return ValueTask.FromResult(StoreWrite.Failed(SaveFails));
            var copy = (JsonObject)Doc.DeepClone();
            edit(copy);
            Doc = copy;
            return ValueTask.FromResult(StoreWrite.Done);
        }
        public void Changed() => Changes++;
        public void Arm(long? delayMs) => Armed = delayMs;
        public TimerNoticeResult ShowDone(TimerDoneNotice notice)
        {
            Shown.Add(notice);
            DocAtShow = (JsonObject)Doc.DeepClone();
            return NoticeAnswer;
        }
        public void CloseDone() => Closes++;
        public void Beep(string why) => Beeped.Add(why);

        /// Move both clocks (a normal second passing).
        public void Pass(long ms) { Now += ms; Mono += ms; }
    }

    static (Host h, TimerEngine e) New(Action<Host>? set = null)
    {
        var h = new Host();
        set?.Invoke(h);
        var e = new TimerEngine(h);
        e.Restore(null, null, null);
        return (h, e);
    }

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    static string Clock(Host h) => h.Doc["clock"]!.ToJsonString();

    [Fact]
    public void Start_takes_text_or_ms_and_a_label_remembers_last_and_saves_the_three_keys()
    {
        var (h, e) = New();
        Assert.True(e.StartTimer("25", null).Ok);
        Assert.Equal(25 * M, e.LastMs);
        Assert.Contains("\"endAt\":" + (T0 + 25 * M), Clock(h));
        Assert.True(h.Doc.ContainsKey("last"));
        Assert.True(h.Doc.ContainsKey("held"));
        Assert.Equal((long?)(25 * M), h.Armed);
        Assert.True(e.StartTimer(null, 90_000, "Tea\nnow").Ok);
        Assert.Equal("Tea now", e.State.Label);
        Assert.Contains(h.Lines, l => l == "a 1 min 30 s timer replaces the 25 min timer (25:00 left)");
        Assert.DoesNotContain(h.Lines, l => l.Contains("Tea", StringComparison.Ordinal));   // the log names the length, never the label
        Assert.Equal(TimerCore.Junk, e.StartTimer(null, null).Reason);
        Assert.Equal(TimerCore.Junk, e.StartTimer("abc", null).Reason);
    }

    [Fact]
    public void A_stopwatch_replaces_a_timer_and_a_second_one_restarts_from_zero()
    {
        var (h, e) = New();
        e.StartTimer("5", null);
        e.Stopwatch();
        Assert.Equal(TimerKind.Stopwatch, e.State.Kind);
        Assert.Null(h.Armed);   // nothing ticks for a stopwatch
        h.Pass(70 * S);
        e.Stopwatch();
        Assert.Equal(h.Now, e.State.StartAt);
        Assert.Contains(h.Lines, l => l == "a stopwatch restarts the stopwatch (1:10)");
    }

    [Fact]
    public void Pause_resume_and_cancel_refuse_in_words()
    {
        var (h, e) = New();
        Assert.Equal("No timer or stopwatch is running", e.Pause().Reason);
        Assert.Equal("No timer or stopwatch is running", e.Resume().Reason);
        Assert.Equal("No timer or stopwatch is running", e.Cancel().Reason);
        e.StartTimer("10", null);
        Assert.Equal("The timer is not paused", e.Resume().Reason);
        h.Pass(4 * M);
        Assert.True(e.Pause().Ok);
        Assert.Null(h.Armed);   // no timeout while paused
        Assert.Equal("The timer is already paused", e.Pause().Reason);
        h.Pass(3 * H);
        Assert.True(e.Resume().Ok);
        Assert.Equal(h.Now + 6 * M, e.State.EndAt);
        Assert.True(e.Cancel().Ok);
        Assert.Equal("{\"kind\":\"idle\"}", Clock(h));
        e.Stopwatch();
        e.Pause();
        Assert.Equal("The stopwatch is already paused", e.Pause().Reason);
    }

    [Fact]
    public void Add_only_works_on_a_timer_and_needs_an_amount()
    {
        var (h, e) = New();
        Assert.Equal("Say how much time to add (ms)", e.Add(null).Reason);
        Assert.Equal("Say how much time to add (ms)", e.Add(0).Reason);
        Assert.Equal("Say how much time to add (ms)", e.Add(double.NaN).Reason);
        Assert.Equal("Only a running or paused timer can take more time", e.Add(300_000).Reason);
        e.StartTimer("25", null);
        Assert.True(e.Add(300_000).Ok);
        Assert.Equal("30 min", e.State.Label);
        Assert.Contains(h.Lines, l => l.StartsWith("added 5 min: 30 min timer", StringComparison.Ordinal));
    }

    [Fact]
    public void Switched_off_in_the_tray_refuses_starting_commands_and_switching_off_cancels_and_saves_idle()
    {
        var (h, e) = New();
        e.StartTimer("5", null);
        h.Enabled = false;
        e.OnEnabled(false);
        Assert.Equal(TimerKind.Idle, e.State.Kind);
        Assert.Equal("{\"kind\":\"idle\"}", Clock(h));
        Assert.Contains(h.Lines, l => l.StartsWith("switched off in the tray: cancelled the 5 min timer", StringComparison.Ordinal));
        Assert.True(h.Closes > 0);
        foreach (TimerReply r in new[] { e.StartTimer("5", null), e.Stopwatch(), e.Resume(), e.Add(1000), e.Again(), e.Snooze(60000) })
            Assert.Equal("The timer is switched off in the tray", r.Reason);
    }

    [Fact]
    public void At_zero_the_state_is_saved_idle_before_the_notice_shows_and_the_log_names_the_length()
    {
        var (h, e) = New();
        e.StartTimer("25", null, "Tea");
        h.Pass(25 * M);
        e.Check();
        Assert.Equal(TimerKind.Idle, e.State.Kind);
        Assert.Single(h.Shown);
        Assert.Equal("{\"kind\":\"idle\"}", h.DocAtShow!["clock"]!.ToJsonString());
        Assert.Equal("Timer done: Tea", h.Shown[0].Title);
        Assert.StartsWith("25 min timer · ended at ", h.Shown[0].Sub, StringComparison.Ordinal);
        Assert.Equal(new[] { "+1 min", "+5 min", "Restart", "Dismiss" }, h.Shown[0].Buttons.Select(b => b.Label));
        Assert.Equal("Run the 25 min timer again", h.Shown[0].Buttons[2].Tooltip);
        Assert.Equal("shown", e.Done!.Popup);
        Assert.Contains("done (25 min)", h.Lines);
        Assert.Empty(h.Beeped);
    }

    [Fact]
    public void An_early_wake_re_arms_for_the_rest_and_a_long_timer_is_capped()
    {
        var (h, e) = New();
        e.StartTimer("10", null);
        h.Pass(4 * M);
        e.Check();
        Assert.Empty(h.Shown);
        Assert.Equal((long?)(6 * M), h.Armed);
        Assert.True(TimerEngine.MaxDelay > 24 * H);   // 24 h fits under the cap: the cap is for a clock far ahead
    }

    [Fact]
    public void A_clock_set_back_still_ends_on_time_by_the_monotonic_count()
    {
        var (h, e) = New();
        e.StartTimer("10", null);
        h.Mono += 10 * M;          // ten minutes pass...
        h.Now += 10 * M - 2 * H;   // ...while the wall clock is set back two hours
        e.Check();
        Assert.Single(h.Shown);
        Assert.Contains(h.Lines, l => l.StartsWith("the PC clock was set back while the timer ran", StringComparison.Ordinal));
        Assert.Equal(h.Now, e.Done!.Finish.EndAt);   // "ended at" = now
        Assert.False(e.Done.Finish.Asleep);
    }

    [Fact]
    public void A_clock_set_forward_past_the_end_ends_it_at_the_next_check()
    {
        var (h, e) = New();
        e.StartTimer("10", null);
        h.Now += 3 * H;
        e.Check();
        Assert.Single(h.Shown);
    }

    [Fact]
    public void A_timer_that_ended_while_the_PC_slept_fires_once_on_wake_and_says_so()
    {
        var (h, e) = New();
        e.StartTimer("10", null);
        h.Pass(2 * H);
        e.OnWake();
        e.OnWake();
        e.Check();
        Assert.Single(h.Shown);
        Assert.True(e.Done!.Finish.Asleep);
        Assert.EndsWith(", while the PC was asleep", h.Shown[0].Sub, StringComparison.Ordinal);
        Assert.Contains(h.Lines, l => l == "done (10 min), late: the PC was asleep");
    }

    [Fact]
    public void A_save_that_fails_keeps_the_timer_running_says_so_and_recovers()
    {
        var (h, e) = New(x => x.SaveFails = "the disk is full");
        e.StartTimer("10", null);
        Assert.Equal(TimerKind.Timer, e.State.Kind);
        Assert.False(e.PersistOk);
        Assert.Equal("the disk is full", e.PersistReason);
        Assert.Contains(h.Healths, x => !x.Ok && x.Reason == "the timer cannot be kept across a restart: the disk is full");
        Assert.Equal(1, h.Lines.Count(l => l.StartsWith("could not save the timer (the disk is full)", StringComparison.Ordinal)));
        Assert.False(e.View()["persist"]!["ok"]!.GetValue<bool>());
        h.SaveFails = null;
        e.Pause();
        Assert.True(e.PersistOk);
        Assert.Equal((true, (string?)TimerEngine.OkWords), h.Healths.Last());
        Assert.Contains("the timer can be kept across a restart again", h.Lines);
    }

    [Fact]
    public void A_write_the_gate_refuses_is_a_failed_save_in_the_gates_words_not_a_silent_one()
    {
        var (h, e) = New(x => x.SaveFails = "native Mantle only reads the Electron app's files in this run");
        e.Stopwatch();
        Assert.Equal("native Mantle only reads the Electron app's files in this run", e.PersistReason);
        Assert.Contains(h.Healths, x => !x.Ok);
    }

    [Fact]
    public void Restart_keeps_a_future_timer_and_fires_one_that_ended_while_Mantle_was_off_once()
    {
        var h = new Host();
        var e = new TimerEngine(h);
        e.Restore(J("{\"kind\":\"timer\",\"endAt\":" + (T0 - M) + ",\"totalMs\":600000,\"label\":\"10 min\",\"startedAt\":1}"), null, J("600000"));
        Assert.Single(h.Shown);
        Assert.EndsWith(", while Mantle was off", h.Shown[0].Sub, StringComparison.Ordinal);
        Assert.Equal("{\"kind\":\"idle\"}", h.DocAtShow!["clock"]!.ToJsonString());
        Assert.Equal(10 * M, e.LastMs);

        var e2 = new TimerEngine(h);   // the next start reads what the first one saved
        e2.Restore(J(h.Doc["clock"]!.ToJsonString()), J(h.Doc["held"]?.ToJsonString() ?? "null"), null);
        Assert.Single(h.Shown);

        var h3 = new Host();
        var e3 = new TimerEngine(h3);
        e3.Restore(J("{\"kind\":\"timer\",\"endAt\":" + (T0 + M) + ",\"totalMs\":600000,\"label\":\"x\",\"startedAt\":1}"), null, null);
        Assert.Equal((long?)M, h3.Armed);
        Assert.Contains(h3.Lines, l => l.StartsWith("picked up the 10 min timer", StringComparison.Ordinal));
    }

    [Fact]
    public void A_junk_or_unreadable_save_starts_idle_with_the_reason_shown_and_logged()
    {
        var h = new Host();
        var e = new TimerEngine(h);
        e.Restore(J("{\"kind\":\"alarm\"}"), null, null);
        Assert.Equal("the saved timer could not be read (unknown kind \"alarm\")", e.RestoreError);
        Assert.Equal(e.RestoreError, e.View()["restoreError"]!.GetValue<string>());
        Assert.Contains(h.Lines, l => l.EndsWith("; starting with no timer", StringComparison.Ordinal));

        var h2 = new Host();
        var e2 = new TimerEngine(h2);
        e2.Restore(null, null, null, readError: "features\\timer.json was set aside as features\\timer.json.unreadable");
        Assert.Equal("the saved timer could not be read (features\\timer.json was set aside as features\\timer.json.unreadable)", e2.RestoreError);
    }

    [Fact]
    public void Switched_off_at_start_cancels_a_saved_timer_and_shows_no_off_time_finish()
    {
        var h = new Host { Enabled = false };
        var e = new TimerEngine(h);
        e.Restore(J("{\"kind\":\"timer\",\"endAt\":" + (T0 + M) + ",\"totalMs\":600000,\"label\":\"x\",\"startedAt\":1}"), null, null);
        Assert.Equal(TimerKind.Idle, e.State.Kind);
        Assert.Contains(h.Lines, l => l.StartsWith("switched off in the tray: the saved 10 min timer", StringComparison.Ordinal));

        var h2 = new Host { Enabled = false };
        new TimerEngine(h2).Restore(J("{\"kind\":\"timer\",\"endAt\":" + (T0 - M) + ",\"totalMs\":600000,\"label\":\"x\",\"startedAt\":1}"), null, null);
        Assert.Empty(h2.Shown);
        Assert.Contains(h2.Lines, l => l.EndsWith("but the timer is switched off in the tray: not shown", StringComparison.Ordinal));
    }

    [Fact]
    public void During_a_game_the_timer_still_ends_beeps_once_and_holds_the_notice_until_the_game_ends()
    {
        var (h, e) = New(x => x.Resting = (true, "Elden Ring"));
        e.StartTimer("5", null);
        h.Pass(5 * M);
        e.Check();
        Assert.Equal(TimerKind.Idle, e.State.Kind);
        Assert.Empty(h.Shown);
        Assert.Single(h.Beeped);
        Assert.True(e.Held);
        Assert.Equal("held", e.Done!.Popup);
        Assert.Equal("the pop-up waits for the game to end (Elden Ring)", e.Done.Reason);
        Assert.Equal(T0 + 5 * M, h.Doc["held"]!["endAt"]!.GetValue<long>());

        h.Resting = (true, null);
        e.OnRestingChanged();
        Assert.Single(h.Shown);
        Assert.EndsWith(", while Elden Ring ran", h.Shown[0].Sub, StringComparison.Ordinal);
        Assert.Null(h.Doc["held"]);
        Assert.Single(h.Beeped);
    }

    [Fact]
    public void A_restart_during_the_game_stays_held_without_a_second_beep_and_after_it_shows_at_once()
    {
        string held = "{\"label\":\"5 min\",\"totalMs\":300000,\"endAt\":" + (T0 - M) + ",\"whileOff\":false}";
        var h = new Host { Resting = (true, "a game") };
        var e = new TimerEngine(h);
        e.Restore(J("{\"kind\":\"idle\"}"), J(held), null);
        Assert.True(e.Held);
        Assert.Empty(h.Beeped);
        Assert.Empty(h.Shown);

        var h2 = new Host();
        var e2 = new TimerEngine(h2);
        e2.Restore(J("{\"kind\":\"idle\"}"), J(held), null);
        Assert.Single(h2.Shown);
        Assert.EndsWith(", while a game ran", h2.Shown[0].Sub, StringComparison.Ordinal);

        var h3 = new Host();
        new TimerEngine(h3).Restore(J("{\"kind\":\"idle\"}"), J("{\"totalMs\":300000,\"endAt\":0}"), null);
        Assert.Empty(h3.Shown);
        Assert.Contains("a done pop-up saved during a game could not be read or is over 7 days old: not shown", h3.Lines);
    }

    [Fact]
    public void Game_state_unknown_shows_the_notice_at_once_and_the_state_says_why()
    {
        var (h, e) = New(x => x.Resting = (false, null));
        Assert.Equal("unknown", e.View()["resting"]!.GetValue<string>());
        Assert.Equal(TimerEngine.RestingUnknown, e.View()["restingReason"]!.GetValue<string>());
        e.StartTimer("1", null);
        h.Pass(M);
        e.Check();
        Assert.Single(h.Shown);
    }

    [Fact]
    public void Snooze_restart_and_dismiss_follow_the_finish_and_a_stale_click_is_refused()
    {
        var (h, e) = New();
        Assert.Equal("There is no finished timer to snooze", e.Snooze(60000).Reason);
        e.StartTimer("25", null, "Tea");
        h.Pass(25 * M);
        e.Check();
        long end = e.Done!.Finish.EndAt;
        Assert.Equal("A snooze is 1 or 5 minutes", e.Snooze(120000, end).Reason);
        Assert.Equal(TimerEngine.Stale, e.Snooze(60000, end - 1).Reason);
        Assert.True(e.Snooze(300000, end).Ok);
        Assert.Equal("Tea", e.State.Label);
        Assert.Equal((long?)(25 * M), e.State.Of);
        Assert.Null(e.Done);
        Assert.Equal("There is no finished timer to snooze", e.Snooze(300000, end).Reason);   // a second click (Electron's order)

        h.Pass(5 * M);
        e.Check();
        Assert.StartsWith("Snoozed 5 min · ended at ", h.Shown.Last().Sub, StringComparison.Ordinal);
        long end2 = e.Done!.Finish.EndAt;
        Assert.True(e.Again(end2).Ok);   // Restart runs the ORIGINAL 25 min, not the snooze
        Assert.Equal(25 * M, e.State.TotalMs);
        Assert.Null(e.State.Of);
        Assert.Equal(TimerEngine.Stale, e.Again(end2).Reason);   // a second click
        Assert.Equal(TimerEngine.Stale, e.Again(double.NaN).Reason);

        h.Pass(25 * M);
        e.Check();
        Assert.True(e.Dismiss());
        Assert.Null(e.Done);
        Assert.False(e.Dismiss());
        Assert.Contains("the done pop-up was dismissed", h.Lines);
    }

    [Fact]
    public void Restart_with_nothing_finished_runs_the_last_length_or_says_there_is_none()
    {
        var (h, e) = New();
        Assert.Equal("There is no finished timer to run again", e.Again().Reason);
        e.StartTimer("15", null);
        e.Cancel();
        Assert.True(e.Again().Ok);
        Assert.Equal(15 * M, e.State.TotalMs);
    }

    [Fact]
    public void A_notice_that_cannot_show_beeps_once_and_the_state_says_why()
    {
        var (h, e) = New(x => x.NoticeAnswer = new TimerNoticeResult(false, "the notifications service is not running"));
        e.StartTimer("1", null);
        h.Pass(M);
        e.Check();
        Assert.Equal("failed", e.Done!.Popup);
        Assert.Equal("the notifications service is not running", e.Done.Reason);
        Assert.Single(h.Beeped);
        Assert.Contains(h.Lines, l => l.StartsWith("the done pop-up failed (the notifications service is not running)", StringComparison.Ordinal));

        var (h2, e2) = New();
        e2.StartTimer("1", null);
        h2.Pass(M);
        e2.Check();
        e2.NoticeFailed(e2.Done!.Finish.EndAt, "the surface closed");
        e2.NoticeFailed(e2.Done.Finish.EndAt, "the surface closed");
        Assert.Single(h2.Beeped);
    }

    [Fact]
    public void A_chime_that_cannot_play_gets_one_beep_instead()
    {
        var (h, e) = New();
        Assert.Equal("There is no done pop-up", e.Chime(true, false, null).Reason);
        e.StartTimer("1", null);
        h.Pass(M);
        e.Check();
        e.Chime(false, false, "no audio output");
        Assert.Equal("failed", e.Done!.Sound);
        Assert.Single(h.Beeped);
        Assert.Contains("the chime did not play (no audio output)", h.Lines);
    }

    [Fact]
    public void In_test_mode_beeps_are_only_counted_and_logged()
    {
        var (h, e) = New(x => { x.Test = true; x.Resting = (true, "a game"); });
        e.StartTimer("1", null);
        h.Pass(M);
        e.Check();
        Assert.Empty(h.Beeped);
        Assert.Equal(1, e.Beeps);
        Assert.Contains(h.Lines, l => l.StartsWith("would beep (muted in --test)", StringComparison.Ordinal));
        Assert.True(e.View()["test"]!.GetValue<bool>());
    }

    [Fact]
    public void The_state_view_has_Electrons_fields()
    {
        var (h, e) = New();
        e.StartTimer("10", null);
        JsonObject v = e.View();
        foreach (string k in new[] { "kind", "paused", "endAt", "leftMs", "totalMs", "label", "snoozeOf", "startAt", "elapsedMs", "lastMs",
                     "done", "held", "resting", "restingReason", "persist", "restoreError", "on", "test", "now" })
            Assert.True(v.ContainsKey(k), k);
        Assert.Equal("timer", v["kind"]!.GetValue<string>());
        Assert.Equal(10 * M, v["leftMs"]!.GetValue<long>());
        Assert.False(v["resting"]!.GetValue<bool>());
        Assert.True(h.Changes > 0);
    }

    [Fact]
    public void The_done_notice_as_one_of_Mantles_own_banners_keeps_three_buttons_and_dismiss_is_its_X()
    {
        var d = new TimerDoneInfo(new TimerFinish("25 min", 25 * M, T0, false));
        TimerDoneNotice n = TimerEngine.NoticeFor(d, TimeZoneInfo.Utc);
        Assert.Equal("Timer done: 25 min", n.Title);
        Assert.StartsWith("Ended at ", n.Sub, StringComparison.Ordinal);
        var post = n.ToBannerPost();
        Assert.Equal(new[] { "+1 min", "+5 min", "Restart" }, post.Actions!.Select(a => a.Label));
        Assert.Equal(n.Title, post.Title);
    }
}
