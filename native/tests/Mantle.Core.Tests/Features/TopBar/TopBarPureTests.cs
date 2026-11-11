using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The pure parts of the top bar, ported from their Electron tests as the spec: ui/topbar/model.js (app name, titles, fit,
/// stat formats), lib/features/_topbar-display.js (verdict, burst) and lib/features/_topbar-peek.js
/// (lib/features/topbar-peek.test.js, driven with fake time, cursor and buttons). Plain logic; nothing on screen.
/// </summary>
public sealed class TopBarPureTests
{
    static JsonObject F(string exe, string? desc = null, string? product = null, bool shell = false) => new() { ["exe"] = exe, ["desc"] = desc, ["product"] = product, ["shell"] = shell };

    // ---------------------------------------------------------------- model.js
    [Fact]
    public void App_name_rule_TOP_020()
    {
        Assert.Equal("Desktop", TopBarModel.AppName(F(@"C:\Windows\explorer.exe", "Windows Explorer", shell: true)));
        Assert.Equal("File Explorer", TopBarModel.AppName(F(@"C:\Windows\explorer.exe", "Windows Explorer")));
        Assert.Equal("App", TopBarModel.AppName(F(@"C:\Windows\System32\ApplicationFrameHost.exe", "Application Frame Host")));
        Assert.Equal("Notepad++", TopBarModel.AppName(F(@"C:\npp\notepad++.exe", "Notepad++")));
        Assert.Equal("Slack", TopBarModel.AppName(F(@"C:\x\slack.exe", "Electron", "Slack")));
        Assert.Equal("Discord", TopBarModel.AppName(F(@"C:\x\discord.exe", "Electron", "Electron")));
        Assert.Equal("Javaw", TopBarModel.AppName(F(@"C:\x\javaw.exe", "Java(TM) Platform SE binary")));
        Assert.Equal("Window", TopBarModel.AppName(F("")));
        Assert.Equal("", TopBarModel.AppName(null));
    }

    [Fact]
    public void Titles_glyph_then_bold_app_then_its_menus_or_Window_and_never_empty_TOP_018_019_021_022_023()
    {
        var only = TopBarModel.Titles(null, null);
        Assert.Single(only);
        Assert.True(only[0].Glyph);
        Assert.Equal("Mantle", only[0].Label);

        var npp = TopBarFixtures.Focus("npp")!;
        var t = TopBarModel.Titles(npp, npp["menu"] as JsonObject);
        Assert.Equal(new[] { "Mantle", "Notepad++", "File", "Edit", "Search", "View", "Encoding", "Language", "Settings", "Tools", "Macro", "Run", "Plugins", "Window", "?" }, t.Select(x => x.Label));
        Assert.True(t[1].Bold);

        var chrome = TopBarFixtures.Focus("chrome")!;
        var c = TopBarModel.Titles(chrome, null);
        Assert.Equal(new[] { "Mantle", "Google Chrome", "Window" }, c.Select(x => x.Label));
        Assert.Equal(new[] { "Minimize", "Restore size", "Always on top" }, c[2].Items!.Select(i => i.Label));

        var desk = TopBarFixtures.Focus("desktop")!;
        Assert.Equal(new[] { "Mantle", "Desktop" }, TopBarModel.Titles(desk, null).Select(x => x.Label));

        // separators dropped, a blank label is "(untitled)", a disabled title stays (dimmed)
        var menu = new JsonObject { ["source"] = "win32", ["items"] = new JsonArray(
            new JsonObject { ["t"] = "sep" }, new JsonObject { ["t"] = "item", ["label"] = "", ["pos"] = 1 },
            new JsonObject { ["t"] = "item", ["label"] = "Edit", ["enabled"] = false, ["pos"] = 2 }, new JsonObject { ["t"] = "sep" }) };
        var m = TopBarModel.Titles(npp, menu);
        Assert.Equal(new[] { "Mantle", "Notepad++", "(untitled)", "Edit" }, m.Select(x => x.Label));
        Assert.False(m[3].Enabled);
    }

    [Fact]
    public void App_menu_words_follow_zoomed_and_topmost_and_name_the_app()
    {
        var f = F(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Google Chrome"); f["zoomed"] = false; f["topmost"] = true;
        var items = TopBarModel.AppMenu(f);
        Assert.Equal(new[] { "New window", null, "Minimize", "Zoom", "Always on top", null, "Close window", "Quit Google Chrome" }, items.Select(i => i.Label));
        Assert.True(items.Single(i => i.Act == "ontop").Checked);
        Assert.Equal("New File Explorer window", TopBarModel.AppMenu(F(@"C:\Windows\explorer.exe", shell: true)).Single().Label);
        Assert.Null(TopBarModel.NewWindowArgs(@"C:\x\WindowsTerminal.exe"));
        Assert.Equal(new[] { "-new-window" }, TopBarModel.NewWindowArgs(@"C:\x\firefox.exe"));
    }

    [Fact]
    public void Mantle_menu_offers_hide_or_show_while_peeking_TOP_303()
    {
        Assert.Contains(TopBarModel.MantleMenu(), i => i.Label == "Hide top bar" && i.Key == "Win+Alt+T");
        Assert.Contains(TopBarModel.MantleMenu(peeking: true), i => i.Label == "Show top bar");
        Assert.True(TopBarModel.MantleMenu(overGames: true).Single(i => i.Act == "overGames").Checked);
    }

    [Fact]
    public void Fit_app_name_first_then_menus_into_more_TOP_024_025()
    {
        var w = new double[] { 30, 200, 40, 40, 40, 40 };
        Assert.Equal(new TopBarFit(6, 6, null), TopBarModel.FitTitles(w, 24, 1000));                  // everything fits
        var a = TopBarModel.FitTitles(w, 24, 300);                                                     // the name gives way
        Assert.Equal(6, a.Shown); Assert.Equal(109.0, a.AppW);
        var b = TopBarModel.FitTitles(w, 24, 220);                                                     // then menus go into »
        Assert.True(b.Shown < 6); Assert.True(b.AppW >= 24);
        Assert.Equal(new TopBarFit(2, 2, null), TopBarModel.FitTitles(new double[] { 30, 60 }, 24, 1000));
        var c = TopBarModel.FitTitles(new double[] { 30, 60, 500 }, 24, 200);                          // a short name is never widened
        Assert.True(c.AppW == null || c.AppW <= 60);
    }

    [Fact]
    public void Stat_cells_fixed_decimals_and_reasons()
    {
        var cells = TopBarModel.StatCells(TopBarFixtures.Stats("ok"));
        Assert.Equal(new[] { "CPU", "RAM", "GPU", "NET", "PING" }, cells.Select(c => c.Label));
        Assert.Equal(new[] { "23%", "9.7/15.7 GB", "8%", "↓2.4 MB/s ↑88 KB/s", "23 ms" }, cells.Select(c => c.Value));
        Assert.Equal("Valorant US West (N. California), 23 ms", cells[4].Title);
        var nogpu = TopBarModel.StatCells(TopBarFixtures.Stats("nogpu"));
        Assert.Equal("--", nogpu[2].Value);
        Assert.StartsWith("Windows has no GPU performance counters", nogpu[2].Why);
        var none = TopBarModel.StatCells(null);
        Assert.All(none, c => Assert.Equal("--", c.Value));
        Assert.Equal("no CPU reading yet", none[0].Why);
        Assert.Equal("no ping reading yet", none[4].Why);
        Assert.Equal("100%", TopBarModel.Pct(140)); Assert.Equal("0%", TopBarModel.Pct(-3)); Assert.Equal("--", TopBarModel.Pct(null));
        Assert.Equal("999 KB/s", TopBarModel.Rate(999 * 1024)); Assert.Equal("1.0 MB/s", TopBarModel.Rate(1000 * 1024)); Assert.Equal("120 MB/s", TopBarModel.Rate(120 * 1048576));
        Assert.Equal("<1 ms", TopBarModel.PingMs(0.3));
        var hi = TopBarModel.PingCell(new JsonObject { ["state"] = "ok", ["ms"] = 140 });
        Assert.True(hi.Warn); Assert.Equal("Ping, 140 ms (high)", hi.Title);
        Assert.Equal("no reply yet", TopBarModel.PingCell(new JsonObject { ["state"] = "timeout" }).Why);
    }

    [Fact]
    public void Notes_button_words()
    {
        Assert.Equal("Quick notes (Win+Alt+N)", TopBarModel.NotesButton(null).Title);
        Assert.Equal("Quick notes (Win+Alt+N) · 2 notes", TopBarModel.NotesButton(new JsonObject { ["count"] = 2 }).Title);
        var bad = TopBarModel.NotesButton(new JsonObject { ["saved"] = new JsonObject { ["ok"] = false, ["reason"] = "disk full" } });
        Assert.True(bad.Bad); Assert.Equal("Quick notes: NOT saved: disk full", bad.Title);
    }

    // ---------------------------------------------------------------- rules (topbar.js pure functions)
    [Fact]
    public void Reserve_size_strip_gap_work_area_and_re_register_TOP_241_243_245_246_249()
    {
        Assert.Equal(42, TopBarRules.ReserveSize(1.5, true));
        Assert.Equal(35, TopBarRules.ReserveSize(1.25, true));
        Assert.Equal(28, TopBarRules.ReserveSize(1.5, false));
        var pend = new ReserveState(false, "not reserved yet", new ReservePending(1000, "starting", 6000));
        Assert.Null(TopBarRules.StripGap(true, pend, false, false, 6999));
        Assert.Equal("the strip was asked for 6 s ago (starting) and has not landed: not reserved yet", TopBarRules.StripGap(true, pend, false, false, 7000));
        Assert.Null(TopBarRules.StripGap(false, pend, false, false, 99999));      // --test never reserves: no failure
        Assert.Null(TopBarRules.StripGap(true, pend, false, true, 99999));       // hidden by hand
        Assert.Equal("let go: x", TopBarRules.StripGap(true, new ReserveState(false, "let go: x"), false, false, 0));
        Assert.True(TopBarRules.WorkAreaCheck(0, 28.6).Ok);
        Assert.False(TopBarRules.WorkAreaCheck(0, 0).Ok);
        Assert.True(TopBarRules.ReRegister(false, false, null, 0));
        Assert.False(TopBarRules.ReRegister(false, true, null, 0));
        Assert.False(TopBarRules.ReRegister(false, false, 1000, 20000));
        Assert.True(TopBarRules.ReRegister(false, false, 1000, 31001));
    }

    [Fact]
    public void Aside_priority_lock_screen_and_words_TOP_260_266()
    {
        Assert.Equal("hidden", TopBarRules.AsideKind(true, "LockApp.exe", true, true, true, false, true));
        Assert.Equal("lock", TopBarRules.AsideKind(false, "LockApp.exe", true, true, true, true, true));
        Assert.Equal("run", TopBarRules.AsideKind(false, null, true, true, true, false, true));
        Assert.Equal("video", TopBarRules.AsideKind(false, null, false, true, true, false, true));
        Assert.Equal("game", TopBarRules.AsideKind(false, null, false, false, true, false, true));
        Assert.Null(TopBarRules.AsideKind(false, null, false, false, true, true, true));   // overGames keeps it over a game, full or not
        Assert.Equal("full", TopBarRules.AsideKind(false, null, false, false, false, false, true));
        Assert.Null(TopBarRules.AsideKind(false, null, false, false, false, false, false));
        Assert.Equal("LockApp.exe", TopBarRules.LockScreen(@"C:\Windows\SystemApps\LockApp.exe", null, false));
        Assert.Equal("LogonUI.exe", TopBarRules.LockScreen(null, "LogonUI.exe", false));
        Assert.Null(TopBarRules.LockScreen(@"C:\Windows\SystemApps\LockApp.exe", null, true));
        Assert.Equal("LockApp.exe is in front (the lock screen): the bar never draws over it", TopBarRules.Signal("lock", "LockApp.exe"));
        Assert.Equal("hidden by hand: Win+Alt+T or \"Show top bar\" in quick settings brings it back", TopBarRules.Signal("hidden"));
        Assert.Equal("a full-screen window has the screen; the top bar stepped aside until it leaves full screen", TopBarRules.LogSignal("full", "Chrome is full screen"));
        Assert.DoesNotContain("Chrome", TopBarRules.LogSignal("full", "Chrome is full screen")!);
        Assert.Equal("shown by hand", TopBarRules.BackWords("hidden"));
        Assert.Equal("the screen is locked", TopBarRules.StatsOffWords("lock"));
        Assert.Equal("Mantle top bar", TopBarRules.Title);
        Assert.Equal(28, TopBarRules.BarDip);
    }

    // ---------------------------------------------------------------- _topbar-display.js
    static TopBarDisplayEvent E(string kind, string[]? ch = null, bool primary = true, bool workOk = false, long ownAt = 0, long now = 100000)
        => new(kind, ch, primary, workOk, ownAt, now);

    [Fact]
    public void Display_verdict_TOP_236_237_238()
    {
        Assert.Equal((true, "a display was connected"), TopBarDisplay.Verdict(E("added")));
        Assert.Equal((true, "a display was disconnected"), TopBarDisplay.Verdict(E("removed")));
        Assert.Equal((true, "the display changed"), TopBarDisplay.Verdict(E("metrics")));
        Assert.Equal((true, "the scale changed, the work area moved"), TopBarDisplay.Verdict(E("metrics", new[] { "scaleFactor", "workArea" }, ownAt: 99999)));
        Assert.Equal((true, "the resolution changed"), TopBarDisplay.Verdict(E("metrics", new[] { "bounds" }, ownAt: 99999)));
        Assert.False(TopBarDisplay.Verdict(E("metrics", new[] { "workArea" }, primary: false)).Run);
        Assert.Equal((false, "Mantle moved the work area itself (its own app bar)"), TopBarDisplay.Verdict(E("metrics", new[] { "workArea" }, ownAt: 99000)));
        Assert.Equal((false, "the work area moved, but still starts at the bar's bottom"), TopBarDisplay.Verdict(E("metrics", new[] { "workArea" }, workOk: true, ownAt: 1)));
        Assert.Equal((true, "the work area moved off the bar's bottom"), TopBarDisplay.Verdict(E("metrics", new[] { "workArea" }, ownAt: 1)));
    }

    sealed class FakeTimers
    {
        public long Now;
        readonly List<(long Due, Action Fn, Box B)> _t = new();
        public sealed class Box : IDisposable { public bool Off; public void Dispose() => Off = true; }
        public IDisposable Later(int ms, Action fn) { var b = new Box(); _t.Add((Now + ms, fn, b)); return b; }
        public void To(long t)
        {
            while (true)
            {
                var next = _t.Where(x => !x.B.Off && x.Due <= t).OrderBy(x => x.Due).FirstOrDefault();
                if (next.Fn == null) break;
                _t.Remove(next); Now = next.Due; next.Fn();
            }
            Now = t;
        }
    }

    [Fact]
    public void Burst_first_at_once_then_one_trailing_run_with_each_reason_once_TOP_234_235()
    {
        var ft = new FakeTimers();
        var runs = new List<(long, string)>();
        var b = new TopBarBurst(w => runs.Add((ft.Now, w)), ft.Later);
        Assert.Equal("now", b.Fire("the scale changed"));
        ft.To(100); Assert.Equal("later", b.Fire("the scale changed, the work area moved"));
        ft.To(200); b.Fire("the resolution changed");
        ft.To(2000);
        Assert.Equal(2, runs.Count);
        Assert.Equal((0L, "the scale changed"), runs[0]);
        Assert.Equal((600L, "2 changes: the scale changed, the work area moved, the resolution changed"), runs[1]);
        Assert.Equal("now", b.Fire("the display changed"));   // a full quiet gap later: a new burst
    }

    // ---------------------------------------------------------------- _topbar-peek.js (topbar-peek.test.js)
    static readonly PeekRect Bounds = new(0, 0, 1536, 864), Bar = new(0, 0, 1536, 28);
    static readonly PeekPoint Edge = new(700, 0), OnBar = new(700, 12), Away = new(700, 400);
    static PeekGuard Hidden(Func<PeekGuard, PeekGuard>? g = null) => (g ?? (x => x))(new PeekGuard(true));

    sealed record Poll(long T, PeekPoint? Pt, PeekButtons? Btn = null, PeekGuard? G = null, bool Menu = false, PeekRect? BarR = null, string? CursorWhy = null);
    static (List<(long T, PeekResult R)> Out, int Reads, long? ShownAt, long? HiddenAt) Run(IEnumerable<Poll> script, PeekState? st = null, PeekButtons? btn = null, PeekGuard? g = null, bool btnThrow = false)
    {
        var s = st ?? TopBarPeek.Initial(); var o = new List<(long, PeekResult)>(); int reads = 0;
        foreach (var p in script)
        {
            var b = p.Btn ?? btn ?? new PeekButtons(true);
            var r = TopBarPeek.Step(s, new PeekInput(p.T, p.Pt, p.CursorWhy, Bounds, p.BarR ?? Bar, p.Menu, TopBarPeek.Blocker(p.G ?? g ?? Hidden()), false,
                () => { reads++; if (btnThrow) throw new InvalidOperationException("boom"); return b; }));
            s = r.St; o.Add((p.T, r));
        }
        long? Find(string a) { var x = o.FirstOrDefault(z => z.Item2.Act == a); return x.Item2 == null ? null : x.Item1; }
        return (o, reads, Find("show"), Find("hide"));
    }
    static IEnumerable<Poll> Every(long from, long to, PeekPoint? pt, int step = 100, PeekButtons? btn = null, PeekGuard? g = null, bool menu = false, PeekRect? bar = null)
    { for (long t = from; t <= to; t += step) yield return new Poll(t, pt, btn, g, menu, bar); }

    [Fact]
    public void Peek_constants_and_edge_TOP_290_291()
    {
        Assert.InRange(TopBarPeek.DwellMs, 450, 550); Assert.InRange(TopBarPeek.GraceMs, 500, 700); Assert.InRange(TopBarPeek.PollMs, 50, 150);
        Assert.Equal(new[] { 1, 2, 4, 5, 6 }, TopBarPeek.VkMouse);
        Assert.True(TopBarPeek.InBand(new PeekPoint(0, 0), Bounds) && TopBarPeek.InBand(new PeekPoint(1535, 1), Bounds));
        Assert.False(TopBarPeek.InBand(new PeekPoint(700, 2), Bounds));
        Assert.False(TopBarPeek.InBand(new PeekPoint(1536, 0), Bounds) || TopBarPeek.InBand(new PeekPoint(-1, 0), Bounds));
        Assert.False(TopBarPeek.InBand(new PeekPoint(700, -1), Bounds));
        Assert.True(TopBarPeek.InBand(new PeekPoint(1600, 100), new PeekRect(1536, 100, 1920, 1080)));
        Assert.False(TopBarPeek.InBand(null, Bounds));
    }

    [Fact]
    public void Peek_dwell_flick_and_buttons_TOP_292_294_295()
    {
        var r = Run(Every(0, 1000, Edge));
        Assert.Equal(500, r.ShownAt);
        Assert.Single(r.Out, x => x.R.Act == "show");
        Assert.Contains("rested on the top edge for 500 ms", r.Out.First(x => x.R.Act == "show").R.Why);
        Assert.Equal(1000, Run(Every(0, 300, Edge).Append(new Poll(400, Away)).Concat(Every(500, 1200, Edge))).ShownAt);
        Assert.Null(Run(Every(0, 3000, new PeekPoint(700, 2))).ShownAt);
        Assert.Equal(0, Run(Every(0, 3000, Away)).Reads);
        var held = new PeekButtons(true, true, true); var up = new PeekButtons(true);
        var h = Run(Every(0, 1500, Edge, btn: held).Concat(Every(1600, 3000, Edge, btn: up)));
        Assert.Null(h.ShownAt);
        Assert.Contains("mouse button went down on the top edge", h.Out[0].R.Blocked);
        Assert.Equal(1200, Run(Every(0, 500, Edge, btn: held).Append(new Poll(600, Away, up)).Concat(Every(700, 1400, Edge, btn: up))).ShownAt);
        Assert.Null(Run(new[] { new Poll(0, Edge), new Poll(100, Edge), new Poll(200, Edge, new PeekButtons(true, false, true)) }.Concat(Every(300, 2000, Edge))).ShownAt);
        Assert.Equal(500, Run(new[] { new Poll(0, Edge, new PeekButtons(true, false, true)) }.Concat(Every(100, 800, Edge))).ShownAt);
        var bad = Run(Every(0, 2000, Edge), btn: new PeekButtons(false, Reason: "no reader"));
        Assert.Null(bad.ShownAt);
        Assert.Contains("mouse buttons cannot be read (no reader)", bad.Out[0].R.Blocked);
        var th = Run(Every(0, 2000, Edge), btnThrow: true);
        Assert.Null(th.ShownAt); Assert.Contains("threw: boom", th.Out[0].R.Blocked);
        var onBar = Run(Every(0, 600, Edge).Concat(Every(700, 3000, OnBar, btn: held)));
        Assert.Null(onBar.HiddenAt); Assert.Equal(6, onBar.Reads);
    }

    [Theory]
    [InlineData("run", "VALORANT.exe is running (a known game)")]
    [InlineData("full", "Google Chrome is full screen")]
    [InlineData("game", "full-screen game is in front")]
    [InlineData("lock", "LockApp.exe is in front")]
    [InlineData("stopped", "stopped (Mantle is resting")]
    [InlineData("off", "switched off in the tray")]
    [InlineData("shown", "not hidden by hand")]
    public void Peek_never_and_mid_peek_hides_TOP_296(string what, string words)
    {
        PeekGuard G(PeekGuard x) => what switch
        {
            "run" => x with { Run = "VALORANT.exe" }, "full" => x with { Fullscreen = "Google Chrome" }, "game" => x with { Game = "the sidecar said so" },
            "lock" => x with { Lock = "LockApp.exe" }, "stopped" => x with { Stopped = true }, "off" => x with { Enabled = false }, _ => x with { Hidden = false },
        };
        var r = Run(Every(0, 3000, Edge), g: Hidden(G));
        Assert.Null(r.ShownAt); Assert.Contains(words, r.Out[0].R.Blocked); Assert.Equal(0, r.Reads);
        var r2 = Run(Every(0, 600, Edge).Append(new Poll(700, OnBar, G: Hidden(G))));
        Assert.Equal(700, r2.HiddenAt); Assert.Contains(words, r2.Out[^1].R.Why);
    }

    [Fact]
    public void Peek_grace_menus_and_cursor_TOP_299_300_301()
    {
        var shown = Every(0, 500, Edge).ToList();
        Assert.Null(Run(shown.Concat(Every(600, 4000, OnBar))).HiddenAt);
        var r2 = Run(shown.Concat(new[] { 600, 700, 1100, 1150, 1200, 1300 }.Select(t => new Poll(t, Away))));
        Assert.Equal(1200, r2.HiddenAt);
        Assert.Contains("left the bar 600 ms ago", r2.Out.First(x => x.R.Act == "hide").R.Why);
        Assert.Equal(1600, Run(shown.Concat(new[] { new Poll(600, Away), new Poll(900, OnBar), new Poll(1000, Away), new Poll(1500, Away), new Poll(1600, Away), new Poll(1700, Away) })).HiddenAt);
        Assert.Equal(3700, Run(shown.Concat(Every(600, 3000, Away, menu: true)).Concat(Every(3100, 4000, Away))).HiddenAt);
        Assert.Null(Run(shown.Concat(Every(600, 3000, new PeekPoint(40, 250), bar: new PeekRect(0, 0, 1536, 300)))).HiddenAt);
        Assert.Null(Run(shown.Concat(Every(600, 3000, Edge))).HiddenAt);
        var r7 = Run(shown.Append(new Poll(600, null, CursorWhy: "GetCursorPos failed")));
        Assert.Equal(600, r7.HiddenAt); Assert.Contains("could not be read (GetCursorPos failed)", r7.Out[^1].R.Why);
        var r8 = Run(new[] { new Poll(0, null, CursorWhy: "denied") });
        Assert.Contains("could not be read (denied)", r8.Out[0].R.Blocked);
        var r9 = Run(shown.Concat(Every(600, 1200, Away)).Concat(Every(1300, 2200, Edge)));
        Assert.Equal(new long[] { 500, 1800 }, r9.Out.Where(x => x.R.Act == "show").Select(x => x.T));
    }

    [Fact]
    public void Peek_starts_disarmed_TOP_293()
    {
        var r = Run(Every(0, 2000, Edge).Append(new Poll(2100, Away)).Concat(Every(2200, 3000, Edge)), TopBarPeek.StartState());
        Assert.DoesNotContain(r.Out.Take(21), x => x.R.Act == "show");
        Assert.Equal(2700, r.ShownAt);
        Assert.Equal(600, Run(new[] { new Poll(0, Away) }.Concat(Every(100, 700, Edge)), TopBarPeek.StartState()).ShownAt);
    }
}
