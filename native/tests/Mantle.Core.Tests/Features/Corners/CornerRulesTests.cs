using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.Corners;
using Xunit;

namespace Mantle.Core.Tests.Features.Corners;

// lib/features/corners.test.js, case for case: the pure step driven with a FAKE clock, cursor and mouse buttons; the
// zones (the bottom edge clip at every scale); multi-monitor; the check at the moment of firing; the claim table.
public class CornerRulesTests
{
    // a 1536 x 864 DIP display at 0,0 (1920x1080 at 125%)
    static readonly DipRect B = new(0, 0, 1536, 864);
    static readonly Dictionary<string, DipPoint> PT = new() {
        ["tl"] = new(0, 0), ["tr"] = new(1535, 0), ["bl"] = new(0, 861), ["br"] = new(1535, 861) };   // 861 = the edge-clip row at 125%
    static readonly DipPoint MID = new(700, 400);
    static readonly CornerTable DEF = CornerRules.Defaults;

    sealed record Poll(long T, DipPoint? Pt, X? E = null);
    sealed record X(ButtonRead? Btn = null, bool BtnThrow = false, string? Guard = null, bool GuardSet = false, CornerTable? Actions = null, string? CursorWhy = null);
    sealed record Ran(List<(long T, CornerStep R)> Out, CornerState St, int Reads, List<(long T, CornerStep R)> Fires)
    {
        public long[] FiredAt => Fires.Select(f => f.T).ToArray();
    }

    static Ran Run(IEnumerable<Poll> script, CornerState? st = null, ButtonRead? btn = null, CornerTable? actions = null, string? guard = null)
    {
        var s = st ?? CornerRules.Initial();
        var outs = new List<(long, CornerStep)>();
        int reads = 0;
        foreach (var p in script) {
            var e = p.E ?? new X();
            var b = e.Btn ?? btn ?? new ButtonRead(true);
            var r = CornerRules.Step(s, new CornerInput {
                Now = p.T, Pt = p.Pt, CursorWhy = e.CursorWhy, Bounds = B, Actions = e.Actions ?? actions ?? DEF,
                Guard = e.GuardSet ? e.Guard : guard,
                Buttons = () => { reads++; if (e.BtnThrow) throw new InvalidOperationException("boom"); return b; },
            });
            s = r.St;
            outs.Add((p.T, r));
        }
        return new Ran(outs, s, reads, outs.Where(o => o.Item2.Act == "fire").ToList());
    }

    static IEnumerable<Poll> Every(long from, long to, DipPoint? pt, X? x = null)
    {
        for (long t = from; t <= to; t += CornerRules.CornerPollMs) yield return new Poll(t, pt, x);
    }
    static IEnumerable<Poll> Cat(params IEnumerable<Poll>[] parts) => parts.SelectMany(p => p);
    static IEnumerable<Poll> One(long t, DipPoint? pt, X? x = null) { yield return new Poll(t, pt, x); }

    [Fact]
    public void Constants_defaults_and_choices()
    {
        Assert.InRange(CornerRules.CornerDwellMs, 200, 300);
        Assert.Equal(100, CornerRules.CornerPollMs);
        Assert.InRange(CornerRules.CornerZoneDip, 3, 8);
        Assert.Equal(new CornerTable("taskview", "off", "off", "desktop"), CornerRules.Defaults);
        Assert.Equal(new[] { "off", "taskview", "desktop", "launcher", "qs", "lock" }, CornerRules.Actions);
        Assert.All(CornerRules.Actions, a => Assert.True(CornerRules.ActionNames.ContainsKey(a)));
        Assert.Equal("Show desktop", CornerRules.ActionName("desktop"));
        Assert.Equal("bottom-right", CornerRules.NameOf("br"));
    }

    [Fact]
    public void Normalize_keeps_good_choices_and_falls_back_per_corner()
    {
        Assert.Equal(DEF, CornerRules.Normalize(null));
        var got = CornerRules.Normalize(new Dictionary<string, string?> { ["tl"] = "lock", ["tr"] = "nope", ["br"] = "off", ["xx"] = "qs" });
        Assert.Equal(new CornerTable("lock", "off", "off", "off"), got);
        Assert.False(CornerRules.AnyOn(new CornerTable("off", "off", "off", "off")));
        Assert.True(CornerRules.AnyOn(DEF));
        Assert.False(CornerRules.AnyOn(null));
    }

    [Fact]
    public void Zones_the_four_corner_pixels_and_a_6_DIP_square()
    {
        Assert.Equal("tl", CornerRules.CornerAt(new DipPoint(0, 0), B));
        Assert.Equal("tr", CornerRules.CornerAt(new DipPoint(1535, 0), B));
        Assert.Equal("bl", CornerRules.CornerAt(new DipPoint(0, 863), B));
        Assert.Equal("br", CornerRules.CornerAt(new DipPoint(1535, 863), B));
        Assert.Equal("tl", CornerRules.CornerAt(new DipPoint(5, 5), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(6, 0), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(0, 6), B));
        Assert.Equal("br", CornerRules.CornerAt(new DipPoint(1530, 858), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(1529, 863), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(1535, 857), B));
    }

    [Fact]
    public void Zones_an_edge_middle_off_display_and_nonsense_are_no_corner()
    {
        Assert.Null(CornerRules.CornerAt(new DipPoint(700, 0), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(-1, 0), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(0, 865), B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(1537, 0), B));
        Assert.Null(CornerRules.CornerAt(null, B));
        Assert.Null(CornerRules.CornerAt(new DipPoint(double.NaN, 0), B));
    }

    [Fact]
    public void Zones_the_last_pixel_one_DIP_past_a_fractional_display_is_still_its_corner()
    {
        Assert.Equal("br", CornerRules.CornerAt(new DipPoint(1536, 864), B));
        Assert.Equal("tr", CornerRules.CornerAt(new DipPoint(1536, 0), B));
    }

    [Fact]
    public void Zones_a_display_that_does_not_start_at_0_0()
    {
        var d = new DipRect(-1920, -200, 1920, 1080);
        Assert.Equal("tl", CornerRules.CornerAt(new DipPoint(-1920, -200), d));
        Assert.Equal("br", CornerRules.CornerAt(new DipPoint(-1, 879), d));
    }

    [Fact]
    public void Bottom_corners_hold_the_edge_clip_row_and_the_bottom_row_at_every_scale()
    {
        var bad = new List<string>();
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (3840, 2160), (1366, 768) })
            foreach (var s in new[] { 1, 1.25, 1.5, 1.75, 2, 2.25, 2.5, 3 }) {
                var b = new DipRect(0, 0, Math.Round(w / s, MidpointRounding.AwayFromZero), Math.Round(h / s, MidpointRounding.AwayFromZero));
                foreach (Func<double, double> round in new Func<double, double>[] { Math.Floor, v => Math.Round(v, MidpointRounding.AwayFromZero) }) {
                    double y = round((h - 3) / s), x = round((w - 1) / s);
                    if (CornerRules.CornerAt(new DipPoint(x, y), b) != "br" || CornerRules.CornerAt(new DipPoint(0, y), b) != "bl") bad.Add($"{w}x{h}@{s} y {y}/{b.Height}");
                    if (CornerRules.CornerAt(new DipPoint(x, round((h - 1) / s)), b) != "br") bad.Add($"{w}x{h}@{s} clip off");
                }
            }
        Assert.True(bad.Count == 0, string.Join("; ", bad.Take(4)));
    }

    [Fact]
    public void Screens_every_display_has_corners_but_only_the_outer_ones()
    {
        var P1 = new DipRect(0, 0, 1536, 864); var P2 = new DipRect(1536, 0, 1920, 1080); var P3 = new DipRect(0, -864, 1536, 864);
        var two = new[] { P1, P2 };
        var pts = new[] { new DipPoint(0, 0), new DipPoint(1535, 0), new DipPoint(0, 863), new DipPoint(1535, 863) };
        Assert.Equal(new[] { "tl", "tr", "bl", "br" }, pts.Select(p => CornerRules.CornerOnScreens(p, new[] { P1 })).ToArray());
        Assert.Null(CornerRules.CornerOnScreens(new DipPoint(1535, 0), two));
        Assert.Null(CornerRules.CornerOnScreens(new DipPoint(1535, 863), two));
        Assert.Null(CornerRules.CornerOnScreens(new DipPoint(1536, 0), two));
        Assert.Equal("tr", CornerRules.CornerOnScreens(new DipPoint(3455, 0), two));
        Assert.Equal("br", CornerRules.CornerOnScreens(new DipPoint(3455, 1079), two));
        Assert.Equal("bl", CornerRules.CornerOnScreens(new DipPoint(1536, 1079), two));
        Assert.Equal("tl", CornerRules.CornerOnScreens(new DipPoint(0, 0), two));
        Assert.Equal("bl", CornerRules.CornerOnScreens(new DipPoint(0, 863), two));
        var three = new[] { P1, P2, P3 };
        Assert.Null(CornerRules.CornerOnScreens(new DipPoint(0, 0), three));
        Assert.Equal("tl", CornerRules.CornerOnScreens(new DipPoint(0, -864), three));
        Assert.Null(CornerRules.CornerOnScreens(null, two));
        Assert.Null(CornerRules.CornerOnScreens(new DipPoint(5, 5), Array.Empty<DipRect>()));
        Assert.Null(CornerRules.CornerOnScreens(new DipPoint(5, 5), null));
        // step with displays fires on the second screen, never at the inner corner
        var act = new CornerTable("taskview", "qs", "off", "desktop");
        string? Fire(DipPoint pt)
        {
            var st = CornerRules.Initial(); string? fired = null;
            for (long t = 0; t <= 600; t += 100) {
                var r = CornerRules.Step(st, new CornerInput { Now = t, Pt = pt, Bounds = P1, Displays = two, Actions = act, Buttons = () => new ButtonRead(true) });
                st = r.St; if (r.Act == "fire") fired = r.Corner;
            }
            return fired;
        }
        Assert.Equal("tr", Fire(new DipPoint(3455, 0)));
        Assert.Null(Fire(new DipPoint(1535, 0)));
        var claims = new CornerClaims();
        claims.Set(act, two);
        Assert.Null(claims.Claims(new DipPoint(1535, 863), P1));
        Assert.Equal("tl", claims.Claims(new DipPoint(0, 0), P1));
    }

    [Fact]
    public void Step_resting_fires_once_after_the_dwell_and_leaving_rearms()
    {
        var r = Run(Every(0, 1000, PT["br"]));
        Assert.Single(r.Fires);
        Assert.Equal(300, r.FiredAt[0]);
        Assert.Equal("br", r.Fires[0].R.Corner);
        Assert.Equal("desktop", r.Fires[0].R.Action);
        Assert.Contains("bottom-right corner for 300 ms", r.Fires[0].R.Why);
        var early = Run(Every(0, 200, PT["br"]));
        Assert.Empty(early.Fires); Assert.Equal("dwell", early.St.Phase);
        var flick = Run(new[] { new Poll(0, MID), new Poll(100, PT["tl"]), new Poll(200, PT["tl"]), new Poll(300, MID), new Poll(400, PT["tl"]), new Poll(500, MID) });
        Assert.Empty(flick.Fires);
        var back = Run(Cat(Every(0, 500, PT["br"]), One(600, MID), Every(700, 1200, PT["br"])));
        Assert.Equal(new long[] { 300, 1000 }, back.FiredAt);
        var nudge = Run(Cat(Every(0, 400, PT["br"]), Every(500, 1500, new DipPoint(1532, 860))));
        Assert.Single(nudge.Fires);
    }

    [Fact]
    public void Step_a_new_watch_starts_disarmed_and_leaving_once_arms_it()
    {
        var start = Run(Every(0, 1000, PT["tl"]), CornerRules.StartState());
        Assert.Empty(start.Fires); Assert.Equal("disarmed", start.St.Phase);
        Assert.Contains("leave first", start.Out[0].R.Blocked);
        var start2 = Run(Cat(One(0, PT["tl"]), One(100, MID), Every(200, 600, PT["tl"])), CornerRules.StartState());
        Assert.Equal(new long[] { 500 }, start2.FiredAt);
    }

    [Fact]
    public void Step_off_corners_never_fire_or_read_buttons_and_each_corner_fires_its_own()
    {
        var off = Run(Every(0, 1000, PT["tr"]));
        Assert.Empty(off.Fires); Assert.Equal(0, off.Reads); Assert.Equal("idle", off.St.Phase);
        Assert.Equal(0, Run(Every(0, 1000, MID)).Reads);
        var two = Run(Cat(Every(0, 400, PT["tl"]), One(500, MID), Every(600, 1000, PT["br"])));
        Assert.Equal(new[] { "taskview", "desktop" }, two.Fires.Select(f => f.R.Action).ToArray());
        var turnedOff = Run(Cat(Every(0, 400, PT["br"]), Every(500, 800, PT["br"], new X(Actions: DEF with { Br = "off" })), Every(900, 1300, PT["br"])));
        Assert.Equal(new long[] { 300, 1200 }, turnedOff.FiredAt);
    }

    [Fact]
    public void Step_guards_held_buttons_clicks_and_unreadable_reads()
    {
        var g = Run(Every(0, 1000, PT["br"]), guard: "the last corner action is still running");
        Assert.Empty(g.Fires); Assert.Equal("disarmed", g.St.Phase); Assert.Contains("still running", g.Out[^1].R.Blocked);
        Assert.Empty(Run(Cat(Every(0, 300, PT["br"], new X(Guard: "busy", GuardSet: true)), Every(400, 1000, PT["br"], new X(GuardSet: true)))).Fires);
        var after = Run(Cat(Every(0, 300, PT["br"], new X(Guard: "busy", GuardSet: true)), One(400, MID), Every(500, 900, PT["br"], new X(GuardSet: true))));
        Assert.Equal(new long[] { 800 }, after.FiredAt);
        var held = Run(Every(0, 1000, PT["br"], new X(Btn: new ButtonRead(true, true, true))));
        Assert.Empty(held.Fires); Assert.Equal("disarmed", held.St.Phase); Assert.Contains("mouse button went down", held.St.Why);
        Assert.Empty(Run(Cat(Every(0, 200, PT["br"], new X(Btn: new ButtonRead(true, true))), Every(300, 1000, PT["br"]))).Fires);
        var clickDwell = Run(Cat(One(0, PT["tl"]), One(100, PT["tl"], new X(Btn: new ButtonRead(true, false, true))), Every(200, 800, PT["tl"])));
        Assert.Empty(clickDwell.Fires); Assert.Equal("disarmed", clickDwell.St.Phase);
        var stale = Run(Cat(One(0, PT["tl"], new X(Btn: new ButtonRead(true, false, true))), Every(100, 500, PT["tl"])));
        Assert.Single(stale.Fires);
        var unread = Run(Every(0, 1000, PT["br"], new X(Btn: new ButtonRead(false, Reason: "no koffi"))));
        Assert.Empty(unread.Fires); Assert.Contains("cannot be read (no koffi)", unread.Out[0].R.Blocked);
        var thrown = Run(Every(0, 1000, PT["br"], new X(BtnThrow: true)));
        Assert.Empty(thrown.Fires); Assert.Contains("threw: boom", thrown.Out[0].R.Blocked);
        var recover = Run(Cat(Every(0, 300, PT["br"], new X(Btn: new ButtonRead(false, Reason: "x"))), Every(400, 800, PT["br"])));
        Assert.Equal(new long[] { 700 }, recover.FiredAt);
        var blind = Run(Cat(One(0, PT["br"]), One(100, PT["br"]), One(200, null, new X(CursorWhy: "GetCursorPos failed")), Every(300, 700, PT["br"])));
        Assert.Equal("the cursor could not be read (GetCursorPos failed)", blind.Out[2].R.Blocked);
        Assert.Equal(new long[] { 600 }, blind.FiredAt);
        Assert.Single(Run(Cat(Every(0, 400, PT["br"]), One(500, null), Every(600, 1000, PT["br"]))).Fires);
        // no reader at all: nothing fires, with the reason
        var none = CornerRules.Step(CornerRules.Initial(), new CornerInput { Now = 0, Pt = PT["br"], Bounds = B, Actions = DEF });
        Assert.Contains("there is no mouse button reader", none.Blocked);
    }

    [Fact]
    public void Fire_blocker_lock_screen_full_screen_games_and_no_answer()
    {
        CornerScan Scan(string exe = @"C:\Windows\explorer.exe", bool full = false, params (string, int)[] procs) => new(true, null, exe, "x", full, 1, procs);
        Assert.Null(CornerRules.FireBlocker(Scan()));
        Assert.Matches(@"could not be checked .*\(timed out\)", CornerRules.FireBlocker(new CornerScan(false, "timed out", null, null, false)));
        Assert.Contains("no answer", CornerRules.FireBlocker(null));
        Assert.Contains("chrome.exe is full screen in front", CornerRules.FireBlocker(Scan(@"C:\x\chrome.exe", true)));
        Assert.Contains("LockApp.exe is in front", CornerRules.FireBlocker(Scan(@"C:\Windows\SystemApps\Microsoft.LockApp_cw5n1h2txyewy\LockApp.exe")));
        // the game plan's check (the game-mode lane's) is a hook: its words win, after the lock screen
        string? Plan(CornerScan s) => s.Procs!.Any(p => p.File == "VALORANT.exe" && p.Session == s.OwnSession) ? "VALORANT is running (a known game)" : null;
        Assert.Contains("VALORANT is running (a known game)", CornerRules.FireBlocker(Scan(procs: ("VALORANT.exe", 1)), Plan));
        Assert.Null(CornerRules.FireBlocker(Scan(procs: ("VALORANT.exe", 2)), Plan));
    }

    [Fact]
    public void Claims_follow_the_table_and_hand_inner_corners_back()
    {
        var c = new CornerClaims();
        int changed = 0; c.Changed += () => changed++;
        c.Set(null);
        Assert.Null(c.Claims(PT["br"], B)); Assert.False(c.Active);
        c.Set(new CornerTable("off", "off", "off", "off"));
        Assert.Null(c.Claims(PT["br"], B)); Assert.Null(c.Claims(PT["tl"], B)); Assert.False(c.Active);
        c.Set(DEF);
        bool Band(DipPoint p) => p.Y >= B.Height - 4 && c.Claims(p, B) == null;   // main.js hoverTick: the bottom 4 DIP
        Assert.Equal("br", c.Claims(PT["br"], B));
        Assert.False(Band(PT["br"])); Assert.False(Band(new DipPoint(1535, 863)));
        Assert.Null(c.Claims(PT["bl"], B)); Assert.True(Band(PT["bl"]));
        Assert.True(Band(new DipPoint(700, 861))); Assert.True(Band(new DipPoint(1529, 861)));
        // the top bar peek (TOP-297): the active top-left square is the corner's, just beside it is the peek's
        Assert.Equal("tl", c.Claims(PT["tl"], B));
        Assert.Null(c.Claims(new DipPoint(6, 0), B));
        Assert.Null(c.Claims(PT["tr"], B));
        Assert.Equal(3, changed);
        c.Set(null);
        Assert.Null(c.Claims(PT["tl"], B));
    }
}
