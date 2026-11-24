using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.Core.Features.AutoPause;
using Xunit;

namespace Mantle.Core.Tests.Features.AutoPause;

// lib/features/_autopause-core.js's rules (the cases lib/features/autopause.test.js drives through the engine), ported:
// AUTO-057..AUTO-077, AUTO-079, AUTO-105..AUTO-107. A hand-moved clock; Spotify is a counter. Written, not run.
public class AutoPauseEngineTests
{
    sealed class Rig
    {
        public long Now;
        public readonly FakeSpotify Sp = new();
        public readonly List<string> Logs = new();
        public readonly AutoPauseEngine E;
        public Rig() { E = new AutoPauseEngine(Sp.For("mantle", () => true), () => Now, Logs.Add); }

        /// Tick every 250 ms for <paramref name="ms"/>, each tick with these sources.
        public void Run(long ms, System.Func<long, AutoPauseSource[]> at)
        {
            var end = Now + ms;
            while (Now < end) { Now += 250; E.Tick(Now, at(Now)).GetAwaiter().GetResult(); }
        }
        public void Tick(params AutoPauseSource[] s) => E.Tick(Now, s).GetAwaiter().GetResult();
    }

    static AutoPauseSource Media(double peak, string status = "playing", double? pos = null) => new("App", "media", "Lesson", status, pos, peak);
    static AutoPauseSource Browser(double peak, string status = "playing") => new("Chrome", "browser", "Chrome", status, null, peak);
    static AutoPauseSource AnyApp(double peak) => new("game.exe", "app", "Game", null, null, peak);

    [Fact]
    public void Audio_above_the_threshold_pauses_within_one_tick_and_below_it_never()
    {
        var r = new Rig();
        r.Run(2000, _ => new[] { Media(0.0005) });
        Assert.Equal(0, r.Sp.TotalPauses);
        r.Run(250, _ => new[] { Media(0.000846) });
        Assert.Equal(("paused", 1), (r.Sp.State, r.Sp.TotalPauses));
    }

    [Fact]
    public void Position_only_evidence_must_run_8_s_and_a_short_burst_never_pauses()
    {
        var r = new Rig();
        r.Run(3600, t => new[] { Media(-1, "playing", t) });                  // a ~3.6 s navigation burst
        r.Run(8000, _ => new[] { Media(-1, "paused", 0) });
        Assert.Equal(0, r.Sp.TotalPauses);
        var start = r.Now;
        r.Run(7500, t => new[] { Media(-1, "playing", t) });
        Assert.Equal(0, r.Sp.TotalPauses);
        r.Run(1500, t => new[] { Media(-1, "playing", t) });                  // sustained: pauses at 8-9 s
        Assert.Equal(1, r.Sp.TotalPauses);
    }

    [Fact]
    public void Audio_peak_is_ground_truth_both_ways()
    {
        var r = new Rig();
        r.Run(5000, _ => new[] { Media(-1, "playing", 1000) });               // says Playing, silent, frozen position
        Assert.Equal(0, r.Sp.TotalPauses);
        r.Run(5000, _ => new[] { Media(0.2, "paused", 1000) });               // says Paused while audio plays
        Assert.Equal(("paused", 1), (r.Sp.State, r.Sp.TotalPauses));
        Assert.Equal(0, r.Sp.TotalPlays);
    }

    [Fact]
    public void A_muted_browser_video_never_pauses_and_an_unmeasurable_one_trusts_its_Playing()
    {
        var r = new Rig();
        r.Tick(Browser(0.3, "playing")); Assert.Equal(1, r.Sp.TotalPauses);
        var m = new Rig();
        m.Run(3000, _ => new[] { Browser(0, "playing") });                    // measured, silent: a muted video
        Assert.Equal(0, m.Sp.TotalPauses);
        var u = new Rig();
        u.Run(7500, _ => new[] { Browser(-1, "playing") });                   // no meter: its Playing is trusted, as position-only evidence (8 s)
        Assert.Equal(0, u.Sp.TotalPauses);
        u.Run(750, _ => new[] { Browser(-1, "playing") });
        Assert.Equal(1, u.Sp.TotalPauses);
        var p = new Rig();
        p.Run(3000, _ => new[] { Browser(0.3, "paused") });                   // paused tab + a ping from another tab
        Assert.Equal(0, p.Sp.TotalPauses);
    }

    [Fact]
    public void Any_mode_app_needs_2_s_of_audible_time_and_a_500_ms_ping_never_pauses()
    {
        var r = new Rig();
        r.Run(500, _ => new[] { AnyApp(0.4) });
        r.Run(8000, _ => new[] { AnyApp(0) });                                // long enough for its run to end (3 s liveness + 4 s)
        Assert.Equal(0, r.Sp.TotalPauses);
        r.Run(1750, _ => new[] { AnyApp(0.4) });
        Assert.Equal(0, r.Sp.TotalPauses);
        r.Run(500, _ => new[] { AnyApp(0.4) });
        Assert.Equal(1, r.Sp.TotalPauses);
    }

    [Fact]
    public void Minimum_hold_and_resume_delay_one_blip_costs_one_gap_and_blips_3_s_apart_are_one_episode()
    {
        var r = new Rig();
        r.Run(250, _ => new[] { Media(0.3) });                                // one blip
        r.Run(15000, _ => new[] { Media(0) });
        Assert.Equal((1, 1), (r.Sp.TotalPauses, r.Sp.TotalPlays));            // never on-off-on-off
        var b = new Rig();
        for (int i = 0; i < 4; i++) { b.Run(250, _ => new[] { Media(0.3) }); b.Run(3000, _ => new[] { Media(0) }); }
        b.Run(15000, _ => new[] { Media(0) });
        Assert.Equal((1, 1), (b.Sp.TotalPauses, b.Sp.TotalPlays));
    }

    [Fact]
    public void A_lesson_ending_resumes_about_7_s_after_its_sound_stops()
    {
        var r = new Rig();
        r.Run(20000, _ => new[] { Media(0.3) });
        long stop = r.Now; long resumedAt = -1;
        while (r.Now - stop < 12000) { r.Run(250, _ => new[] { Media(0) }); if (resumedAt < 0 && r.Sp.TotalPlays == 1) resumedAt = r.Now; }
        Assert.InRange(resumedAt - stop, 6500, 7750);
    }

    [Fact]
    public void A_source_closing_releases_its_hold_at_once()
    {
        var r = new Rig();
        r.Run(1000, _ => new[] { Media(0.3) });
        r.Run(250, _ => System.Array.Empty<AutoPauseSource>());
        Assert.Equal(("playing", 1), (r.Sp.State, r.Sp.TotalPlays));
    }

    [Fact]
    public void Only_resume_what_we_paused_and_skip_when_the_user_resumed()
    {
        var r = new Rig(); r.Sp.State = "paused";
        var a = r.E.AddHold("http:x", false, r.Now).GetAwaiter().GetResult();
        var b = r.E.RemoveHold("http:x", "done").GetAwaiter().GetResult();
        Assert.Equal(("already-paused", "skipped", "not-held", 0, 0), (a.Action, b.Action, b.Reason, r.Sp.TotalPauses, r.Sp.TotalPlays));
        var u = new Rig();
        u.E.AddHold("http:x", false, u.Now).GetAwaiter().GetResult();
        u.Sp.State = "playing";                                               // the user pressed play during the hold
        var c = u.E.RemoveHold("http:x", "done").GetAwaiter().GetResult();
        Assert.Equal(("skipped", "user-resumed", 0), (c.Action, c.Reason, u.Sp.TotalPlays));
    }

    [Fact]
    public void First_hold_pauses_the_last_resumes_and_a_refresh_sends_nothing()
    {
        var r = new Rig();
        Assert.Equal("paused", r.E.AddHold("http:a", false, 0).GetAwaiter().GetResult().Action);
        Assert.Equal("refreshed", r.E.AddHold("http:a", false, 10).GetAwaiter().GetResult().Action);
        Assert.Equal("already-paused", r.E.AddHold("http:b", false, 20).GetAwaiter().GetResult().Action);
        Assert.Equal("other-holds", r.E.RemoveHold("http:a", "x").GetAwaiter().GetResult().Reason);
        Assert.Equal("resumed", r.E.RemoveHold("http:b", "x").GetAwaiter().GetResult().Action);
        Assert.Equal((1, 1), (r.Sp.TotalPauses, r.Sp.TotalPlays));
    }

    sealed class SlowSpotify : ISpotifyControl
    {
        public string S = "playing"; public readonly List<string> Order = new();
        public TaskCompletionSource<SpotifyResult> PauseGate = new();
        public string State() => S;
        public async Task<SpotifyResult> Pause() { Order.Add("pause>"); var r = await PauseGate.Task; S = "paused"; Order.Add("pause<"); return r; }
        public Task<SpotifyResult> Play() { Order.Add("play"); S = "playing"; return Task.FromResult(SpotifyResult.Done); }
    }

    [Fact]
    public void A_release_never_overtakes_the_pause_before_it() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new SlowSpotify();
        var e = new AutoPauseEngine(sp, () => 0);
        var add = e.AddHold("http:a", false, 0);
        var rem = e.RemoveHold("http:a", "x");
        Assert.False(rem.IsCompleted);
        sp.PauseGate.SetResult(SpotifyResult.Done);
        await Task.WhenAll(add, rem);
        await e.Idle();
        Assert.Equal(new[] { "pause>", "pause<", "play" }, sp.Order);
        Assert.Equal("resumed", rem.Result.Action);
    });

    [Fact]
    public void Repause_at_most_once_a_second_not_within_1_5_s_of_our_resume_then_back_off_for_5_min()
    {
        var r = new Rig();
        r.E.AddHold("http:a", false, r.Now).GetAwaiter().GetResult();
        int pressed = 0;
        for (int i = 0; i < 4; i++)
        {
            r.Sp.State = "playing"; pressed++;                                // the user presses play
            r.Now += 1000; r.Tick();
        }
        Assert.Equal(3, r.Sp.TotalPauses);                                     // play pressed 4x = first pause + 2 re-pauses
        Assert.Contains("Spotify was started 2 times during a hold: leaving Spotify alone for 5 min", r.Logs);
        var s = r.E.Snapshot(r.Now);
        Assert.True(s.Suppressed);
        Assert.InRange(s.SuppressedForMs, 299000, 300000);
        r.E.RemoveHold("http:a", "x").GetAwaiter().GetResult();
        Assert.Equal(0, r.E.Snapshot(r.Now).EpisodeRepauses);                 // reset when holds go back to none
    }

    [Fact]
    public void Adopt_leaves_what_already_plays_alone_and_the_next_video_still_pauses()
    {
        var r = new Rig();
        r.E.Adopt();
        r.Run(10000, _ => new[] { Media(0.3) });
        Assert.Equal(0, r.Sp.TotalPauses);
        r.Run(8000, _ => new[] { Media(0) });                                  // it stops (its run ends 7 s after its sound)
        r.Run(250, _ => new[] { Media(0.3) });                                 // the next video
        Assert.Equal(1, r.Sp.TotalPauses);
        var on = new Rig();
        on.E.Adopt(); on.E.Adopt(false);                                       // turned on by hand: they mean now
        on.Tick(Media(0.3));
        Assert.Equal(1, on.Sp.TotalPauses);
        var back = new Rig();
        back.E.Adopt();
        back.Tick(Media(0.3));
        back.Now += 250; back.Tick();                                          // its session closes
        back.Now += 250; back.Tick(Media(0.3));                                // and comes back: news
        Assert.Equal(1, back.Sp.TotalPauses);
    }

    [Fact]
    public void Inherit_takes_a_paused_Spotify_only_and_hands_it_back_at_once_when_nothing_holds()
    {
        var r = new Rig(); r.Sp.State = "paused";
        var a = r.E.Inherit(true, 0).GetAwaiter().GetResult();
        Assert.Equal(("resumed", "playing"), (a.Action, r.Sp.State));         // the anti-strand rule
        var h = new Rig(); h.Sp.State = "paused";
        h.E.AddHold("native:x", true, 0).GetAwaiter().GetResult();
        Assert.Equal("inherited", h.E.Inherit(true, 0).GetAwaiter().GetResult().Action);
        Assert.True(h.E.Snapshot(0).WePaused);
        var u = new Rig(); u.Sp.State = "playing";                             // the user restarted it: theirs
        Assert.Equal("nothing", u.E.Inherit(true, 0).GetAwaiter().GetResult().Action);
        var b = new Rig(); b.Sp.State = "playing";
        b.E.Inherit(false, 120000).GetAwaiter().GetResult();                   // the old host's back-off comes along
        Assert.Equal((true, 120000L), (b.E.Snapshot(0).Suppressed, b.E.Snapshot(0).SuppressedForMs));
    }

    [Fact]
    public void Hand_off_stops_deciding_without_touching_Spotify_and_release_all_resumes()
    {
        var r = new Rig();
        r.E.AddHold("http:quartz", false, 0, successor: true).GetAwaiter().GetResult();
        var h = r.E.HandOff();
        Assert.Equal(("handed-off", true, "paused", 0), (h.Action, h.Paused, r.Sp.State, r.Sp.TotalPlays));
        Assert.Empty(r.E.Snapshot(0).Holds);
        var x = new Rig();
        x.E.AddHold("http:a", false, 0).GetAwaiter().GetResult();
        Assert.Equal("resumed", x.E.ReleaseAll("off").GetAwaiter().GetResult().Action);
        Assert.Equal("nothing", x.E.ReleaseAll("off").GetAwaiter().GetResult().Action);
    }

    [Fact]
    public void The_constants_are_the_bridges_measured_ones()
    {
        var c = new AutoPauseConfig();
        Assert.Equal((250, 3000L, 4000L, 0.0008, 0L, 8000L, 6000L), (c.PollMs, c.LivenessWindowMs, c.ResumeDelayMs, c.PeakThreshold, c.HoldMinPlayingMs, c.HoldMinPosOnlyMs, c.MinHoldMs));
        Assert.Equal((2, 300000L, 1000L, 1500L, 180000L, 2000L), (c.RepauseGiveUpAfter, c.RepauseGiveUpCooldownMs, c.RepauseCheckEveryMs, c.ResumeGraceMs, c.HttpHoldTtlMs, c.AnyMinRunMs));
    }
}
