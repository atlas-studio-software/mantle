using System;
using System.Text.Json;
using Mantle.Core.Features.Timers;
using Xunit;

namespace Mantle.Core.Tests.Features.Timers;

// lane r10-timer: the timer's pure rules (lib/features/_timer-core.js; cases from lib/features/timer.test.js).
// TIMER-003 (format part), TIMER-005, TIMER-006 (delay rule), TIMER-028..034, TIMER-040 (arithmetic), TIMER-050..052.
public class TimerCoreTests
{
    const long S = 1000, M = 60 * S, H = 60 * M;
    const long Now = 1_790_000_000_000;

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static long Ms(string text)
    {
        TimerLength r = TimerCore.Parse(text);
        Assert.True(r.Ok, $"{text}: {r.Reason}");
        return r.Ms;
    }

    [Fact]
    public void A_bare_number_is_minutes_with_spaces_trimmed()
    {
        Assert.Equal(12 * M, Ms("12"));
        Assert.Equal(90 * M, Ms("90"));
        Assert.Equal(2 * M + 30 * S, Ms("2.5"));
        Assert.Equal(5 * M, Ms("  5  "));
        Assert.Equal(30 * S, Ms(".5"));
    }

    [Fact]
    public void H_mm_is_hours_and_minutes_and_minutes_over_59_are_junk()
    {
        Assert.Equal(H + 30 * M, Ms("1:30"));
        Assert.Equal(45 * M, Ms("0:45"));
        Assert.Equal(24 * H, Ms("24:00"));
        Assert.Equal(TimerCore.Junk, TimerCore.Parse("1:60").Reason);
    }

    [Fact]
    public void Unit_forms_go_biggest_first_each_once_and_a_trailing_bare_number_takes_the_next_unit()
    {
        Assert.Equal(45 * S, Ms("45s"));
        Assert.Equal(10 * M, Ms("10 min"));
        Assert.Equal(10 * M, Ms("10m"));
        Assert.Equal(H + 30 * M, Ms("1h 30m"));
        Assert.Equal(H + 30 * M, Ms("1 h 30 min"));
        Assert.Equal(H + 30 * M, Ms("1h30"));
        Assert.Equal(2 * M + 30 * S, Ms("2m30"));
        Assert.Equal(2 * H, Ms("2 HOURS"));
        Assert.Equal(H + 5 * S, Ms("1hr 5secs"));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-5")]
    [InlineData("10 20")]
    [InlineData("30m 1h")]
    [InlineData("5 parsecs")]
    [InlineData("1h 2h")]
    [InlineData("30s5")]
    [InlineData("١٢")]
    public void Junk_gets_the_one_plain_reason(string text)
    {
        TimerLength r = TimerCore.Parse(text);
        Assert.False(r.Ok);
        Assert.Equal("Type minutes (12) or hours:minutes (1:30)", r.Reason);
    }

    [Fact]
    public void Null_text_is_junk_too() => Assert.Equal(TimerCore.Junk, TimerCore.Parse(null).Reason);

    [Fact]
    public void Lengths_round_to_whole_seconds_and_keep_within_1_second_and_24_hours()
    {
        Assert.Equal(2 * S, TimerCore.Check(1500).Ms);
        Assert.Equal(S, TimerCore.Check(1499).Ms);
        Assert.Equal("A timer needs at least 1 second", TimerCore.Check(400).Reason);
        Assert.Equal("A timer needs at least 1 second", TimerCore.Parse("0").Reason);
        Assert.Equal("A timer can run up to 24 hours", TimerCore.Parse("24:01").Reason);
        Assert.Equal("A timer can run up to 24 hours", TimerCore.Parse("999999999999").Reason);
        Assert.Equal(TimerCore.Junk, TimerCore.Check(double.NaN).Reason);
        Assert.Equal(TimerCore.Junk, TimerCore.Check(-1).Reason);
        Assert.True(TimerCore.Check(24 * H).Ok);
    }

    [Fact]
    public void Automatic_labels_name_the_length()
    {
        Assert.Equal("25 min", TimerCore.LabelFor(25 * M));
        Assert.Equal("1 h 30 min", TimerCore.LabelFor(H + 30 * M));
        Assert.Equal("45 s", TimerCore.LabelFor(45 * S));
        Assert.Equal("2 min 30 s", TimerCore.LabelFor(2 * M + 30 * S));
        Assert.Equal("1 h", TimerCore.LabelFor(H));
        Assert.Equal("0 s", TimerCore.LabelFor(0));
        Assert.Equal("1 h 30 min", TimerCore.Parse("1:30").Label);
    }

    [Fact]
    public void A_custom_label_is_one_line_without_control_or_bidi_characters_and_at_most_60_long()
    {
        Assert.Equal("tea is ready", TimerCore.CleanLabel("tea\nis‮ready"));
        Assert.Equal("a b", TimerCore.CleanLabel("  a \u0007b  "));
        Assert.Equal(60, TimerCore.CleanLabel(new string('x', 500)).Length);
        Assert.Equal("", TimerCore.CleanLabel(null));
        Assert.Equal("", TimerCore.CleanLabel("‎‏"));
        string cut = TimerCore.CleanLabel(new string('x', 59) + "\U0001F600tail");
        Assert.Equal(59, cut.Length);   // never half a surrogate pair
    }

    [Fact]
    public void The_countdown_rounds_up_and_the_stopwatch_rounds_down()
    {
        Assert.Equal("25:00", TimerCore.FormatClock(25 * M));
        Assert.Equal("0:01", TimerCore.FormatClock(1));
        Assert.Equal("0:00", TimerCore.FormatClock(0));
        Assert.Equal("0:00", TimerCore.FormatClock(-5));
        Assert.Equal("1:00:00", TimerCore.FormatClock(H));
        Assert.Equal("1:00:01", TimerCore.FormatClock(H + 1));
        Assert.Equal("0:00", TimerCore.FormatClock(999, up: true));
        Assert.Equal("1:01:01", TimerCore.FormatClock(H + M + S + 999, up: true));
    }

    [Fact]
    public void The_time_of_day_is_the_bar_clocks_shape()
    {
        long at = new DateTimeOffset(2026, 11, 24, 21, 14, 30, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("9:14 PM", TimerCore.TimeOfDay(at, TimeZoneInfo.Utc));
        long midnight = new DateTimeOffset(2026, 11, 24, 0, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("12:05 AM", TimerCore.TimeOfDay(midnight, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Pause_keeps_what_is_left_and_resume_keeps_it_however_long_the_pause()
    {
        TimerState t = TimerCore.StartTimer(Now, 10 * M, null);
        Assert.Equal("10 min", t.Label);
        TimerState p = TimerCore.Pause(t, Now + 4 * M);
        Assert.True(p.Paused);
        Assert.Equal(6 * M, TimerCore.LeftMs(p, Now + 50 * H));
        TimerState r = TimerCore.Resume(p, Now + 50 * H);
        Assert.Equal(Now + 50 * H + 6 * M, r.EndAt);
        Assert.Same(p, TimerCore.Pause(p, Now));
        Assert.Same(TimerState.Idle, TimerCore.Resume(TimerState.Idle, Now));
    }

    [Fact]
    public void A_stopwatch_counts_up_pauses_and_carries_on()
    {
        TimerState sw = TimerCore.StartStopwatch(Now);
        Assert.Equal(90 * S, TimerCore.ElapsedMs(sw, Now + 90 * S));
        TimerState p = TimerCore.Pause(sw, Now + 90 * S);
        Assert.Equal(90 * S, TimerCore.ElapsedMs(p, Now + H));
        TimerState r = TimerCore.Resume(p, Now + H);
        Assert.Equal(Now + H - 90 * S, r.StartAt);
    }

    [Fact]
    public void Adding_time_keeps_within_0_to_24_hours_and_an_automatic_label_follows()
    {
        TimerState t = TimerCore.StartTimer(Now, 25 * M, null);
        TimerState a = TimerCore.AddTime(t, Now, 5 * M);
        Assert.Equal("30 min", a.Label);
        Assert.Equal(Now + 30 * M, a.EndAt);
        TimerState named = TimerCore.StartTimer(Now, 25 * M, "Tea");
        Assert.Equal("Tea", TimerCore.AddTime(named, Now, 5 * M).Label);
        Assert.Equal(0, TimerCore.LeftMs(TimerCore.AddTime(t, Now, -H), Now));
        Assert.Equal(24 * H, TimerCore.LeftMs(TimerCore.AddTime(t, Now, 30 * H), Now));
        TimerState paused = TimerCore.Pause(t, Now);
        Assert.Equal(30 * M, TimerCore.AddTime(paused, Now + H, 5 * M).LeftMs);
    }

    [Fact]
    public void The_bar_redraws_once_per_shown_second_and_not_at_all_while_idle_or_paused()
    {
        TimerState t = TimerCore.StartTimer(Now, 10 * S, null);
        Assert.Equal((long?)1000, TimerCore.NextTickMs(t, Now));
        Assert.Equal((long?)300, TimerCore.NextTickMs(t, Now + 700));
        Assert.Equal((long?)315, TimerCore.BarRedrawMs(t, Now + 700));
        Assert.Equal((long?)20, TimerCore.BarRedrawMs(t, Now + 999));
        Assert.Null(TimerCore.NextTickMs(t, Now + 10 * S));
        Assert.Null(TimerCore.NextTickMs(TimerCore.Pause(t, Now), Now));
        Assert.Null(TimerCore.NextTickMs(TimerState.Idle, Now));
        Assert.Equal((long?)750, TimerCore.NextTickMs(TimerCore.StartStopwatch(Now), Now + 2250));
    }

    [Fact]
    public void The_saved_shape_is_Electrons_and_comes_back_the_same()
    {
        TimerState t = TimerCore.StartTimer(Now, 25 * M, "Tea", of: 5 * M);
        string json = TimerCore.ToJson(t).ToJsonString();
        Assert.Contains("\"kind\":\"timer\"", json);
        Assert.Contains("\"endAt\":" + (Now + 25 * M), json);
        Assert.Contains("\"of\":300000", json);
        Assert.DoesNotContain("paused", json);
        TimerRestore r = TimerCore.Restore(J(json), Now + M);
        Assert.Equal(t, r.State);
        Assert.Null(r.Reason);
        Assert.Equal("{\"kind\":\"idle\"}", TimerCore.ToJson(TimerState.Idle).ToJsonString());
        Assert.Equal("{\"kind\":\"stopwatch\",\"paused\":true,\"elapsedMs\":5000}", TimerCore.ToJson(new TimerState(TimerKind.Stopwatch, Paused: true, ElapsedMs: 5000)).ToJsonString());
    }

    [Fact]
    public void Restore_keeps_a_future_timer_a_paused_one_and_a_stopwatch()
    {
        Assert.Equal(TimerKind.Timer, TimerCore.Restore(J("{\"kind\":\"timer\",\"endAt\":" + (Now + M) + ",\"totalMs\":600000,\"label\":\"x\",\"startedAt\":1}"), Now).State.Kind);
        TimerRestore p = TimerCore.Restore(J("{\"kind\":\"timer\",\"paused\":true,\"leftMs\":5000,\"totalMs\":600000,\"label\":\"\",\"startedAt\":null}"), Now);
        Assert.True(p.State.Paused);
        Assert.Equal("10 min", p.State.Label);
        Assert.Null(p.State.StartedAt);
        TimerRestore sw = TimerCore.Restore(J("{\"kind\":\"stopwatch\",\"startAt\":" + (Now + H) + "}"), Now);
        Assert.Equal(Now, sw.State.StartAt);   // a start in the future is brought back to now
        Assert.Null(TimerCore.Restore(null, Now).Reason);
        Assert.Equal(TimerKind.Idle, TimerCore.Restore(J("{\"kind\":\"idle\"}"), Now).State.Kind);
    }

    [Fact]
    public void A_timer_that_ended_while_Mantle_was_off_comes_back_idle_with_one_finish()
    {
        TimerRestore r = TimerCore.Restore(J("{\"kind\":\"timer\",\"endAt\":" + (Now - M) + ",\"totalMs\":600000,\"label\":\"Tea\",\"startedAt\":1,\"of\":60000}"), Now);
        Assert.Equal(TimerKind.Idle, r.State.Kind);
        Assert.NotNull(r.Fired);
        Assert.True(r.Fired!.WhileOff);
        Assert.Equal("Tea", r.Fired.Label);
        Assert.Equal((long?)60000, r.Fired.Of);
    }

    [Fact]
    public void Old_far_or_junk_saves_come_back_idle_with_the_reason_in_words()
    {
        Assert.Equal("the saved timer ended more than 7 days ago (or its end time is wrong), so it was not shown as done",
            TimerCore.Restore(J("{\"kind\":\"timer\",\"endAt\":0,\"totalMs\":600000}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (it ends more than 24 hours from now; the clock may have changed)",
            TimerCore.Restore(J("{\"kind\":\"timer\",\"endAt\":" + (Now + 26 * H) + ",\"totalMs\":600000}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (not a timer)", TimerCore.Restore(J("[1]"), Now).Reason);
        Assert.Equal("the saved timer could not be read (its length is missing)", TimerCore.Restore(J("{\"kind\":\"timer\",\"endAt\":1}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (its time left is missing)", TimerCore.Restore(J("{\"kind\":\"timer\",\"paused\":true,\"totalMs\":1000}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (its end time is missing)", TimerCore.Restore(J("{\"kind\":\"timer\",\"totalMs\":1000,\"endAt\":\"soon\"}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (its time is more than a year)", TimerCore.Restore(J("{\"kind\":\"stopwatch\",\"paused\":true,\"elapsedMs\":1e13}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (it started more than a year ago; the clock may have changed)", TimerCore.Restore(J("{\"kind\":\"stopwatch\",\"startAt\":0}"), Now).Reason);
        Assert.Equal("the saved timer could not be read (unknown kind \"alarm\")", TimerCore.Restore(J("{\"kind\":\"alarm\"}"), Now).Reason);
        Assert.Equal(TimerKind.Idle, TimerCore.Restore(J("{\"kind\":\"timer\",\"endAt\":1e300,\"totalMs\":1000}"), Now).State.Kind);
    }
}
