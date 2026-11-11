using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Mantle.Core.Features.Desktop;
using Mantle.Core.Platform;
using Mantle.Core.State;
using Xunit;

namespace Mantle.Core.Tests.Features.Desktop;

// The desktop layer's pure rules: the clock, the System tile, the Mission Control probe, the strip, the menu, the
// layer's place and remake rules, its geometry at 100 / 125 / 150 / 175 %, and the MANTLE_TEST_DESKTOP fixture.
public class DesktopRulesTests
{
    [Theory]
    [InlineData(16, 15, 32, "4:15:32 PM")]
    [InlineData(0, 5, 9, "12:05:09 AM")]
    [InlineData(12, 0, 0, "12:00:00 PM")]
    [InlineData(9, 59, 59, "9:59:59 AM")]
    [InlineData(22, 0, 0, "10:00:00 PM")]
    public void Clock_reads_12_hour_with_seconds_whatever_the_locale(int h, int m, int s, string want)
    {
        var was = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // a 24-hour locale cannot make it military time
            Assert.Equal(want, DeskClock.Of(new DateTime(2026, 11, 6, h, m, s)).Text);
        } finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void Clock_aims_at_the_next_second_with_a_60_ms_floor_and_dates_without_the_year()
    {
        Assert.Equal(1000, DeskClock.NextDelayMs(new DateTime(2026, 11, 6, 1, 2, 3, 0)));
        Assert.Equal(250, DeskClock.NextDelayMs(new DateTime(2026, 11, 6, 1, 2, 3, 750)));
        Assert.Equal(60, DeskClock.NextDelayMs(new DateTime(2026, 11, 6, 1, 2, 3, 990)));
        Assert.Equal("Friday, November 6", DeskClock.DateLine(new DateTime(2026, 11, 6), new CultureInfo("en-US")));
        string gb = DeskClock.DateLine(new DateTime(2026, 11, 6), new CultureInfo("en-GB"));
        Assert.Contains("Friday", gb); Assert.Contains("6 November", gb); Assert.DoesNotContain("2026", gb);
        Assert.Equal("--:--", DeskClock.BootHm);
    }

    [Fact]
    public void Stats_words_battery_and_failures()
    {
        var v = DeskStats.Of(7, 19.4 * 1024 * 1024 * 1024, 32.0 * 1024 * 1024 * 1024, 81, true, "ok");
        Assert.Equal(("7%", "19.4 GB / 32.0 GB", "81% · charging", (string?)null), (v.Cpu, v.Mem, v.Battery, v.Note));
        Assert.Null(DeskStats.Of(3, 1, 2, null, false, "this machine reports no battery").Note);
        Assert.Equal("Battery: the driver did not answer", DeskStats.Of(3, 1, 2, null, false, "the driver did not answer").Note);
        var f = DeskStats.Failed("boom");
        Assert.Equal(("—", "—", (string?)null, "System numbers unavailable: boom"), (f.Cpu, f.Mem, f.Battery, f.Note));
    }

    [Fact]
    public void Mission_control_probe_cadence_and_words()
    {
        Assert.Equal(new[] { 30000, 30000, 60000, 120000, 240000, 480000, 600000, 600000 }, new[] { 0, 1, 2, 3, 4, 5, 6, 30 }.Select(McProbe.NextDelayMs).ToArray());
        var now = new DateTime(2026, 11, 6, 16, 15, 0);
        Assert.Equal("Mission Control offline: connect ECONNREFUSED 127.0.0.1:7373. Next check in 30 s.", McProbe.Offline("connect ECONNREFUSED 127.0.0.1:7373", 30000, now));
        Assert.Equal("Mission Control offline: x. Next check in 4 min, at 4:19 PM. Checks slow down while it stays down, to one every 10 min at most.", McProbe.Offline("x", 240000, now));
    }

    static ShellState State(ShellMode mode = ShellMode.Live) => ShellState.Initial(mode, BarEdge.Bottom);

    static ShellState Healthy(ShellMode mode = ShellMode.Live)
    {
        var s = State(mode);
        return s with {
            Sidecar = new SidecarPiece(PlatformState.Ok, ""), Taskbar = s.Taskbar with { Autohide = true }, Workarea = new WorkareaPiece(true, ""),
            Desktop = new DesktopPiece(true, "progman-front", ""), Hotkey = new HotkeyPiece(true, ""), Tray = new TrayPiece(true, "", true, ""),
            EdgeClip = new EdgeClipPiece(false, false, ""), TbHide = new TbHidePiece(false, false, ""),
            Backdrop = new BackdropPiece(new BackdropUse(true, true, 0, 0, ""), new BackdropUse(true, true, 0, 0, ""), new BackdropUse(true, true, 0, 0, "")),
            Glass = new GlassPiece("acrylic-blur-behind", ""), Shortcuts = new ShortcutsPiece(true, true, ""),
            Features = ImmutableSortedDictionary<string, FeaturePiece>.Empty,
        };
    }

    [Fact]
    public void Strip_nothing_wrong_is_no_rows()
    {
        Assert.Empty(DeskStrip.Rows(Healthy()));
    }

    [Fact]
    public void Strip_rows_in_order_with_words_and_the_one_rule_extras()
    {
        var s = Healthy() with {
            Sidecar = new SidecarPiece(PlatformState.Down, "it exited (code 3)"),
            Desktop = new DesktopPiece(false, null, ""),
            Hotkey = new HotkeyPiece(false, "the hook is gone"),
            Tray = new TrayPiece(true, "", false, "the taskbar watch stopped"),
            Wallpaper = new WallpaperPiece(false, "w1", "x", false, "the file is missing", false, ""),
            Features = ImmutableSortedDictionary<string, FeaturePiece>.Empty
                .Add("clips", new FeaturePiece("Clips", true, false, "", true, "", null, false))
                .Add("emoji", new FeaturePiece("Emoji", true, true, "", true, "", "Win+. is Windows'", false))
                .Add("rested", new FeaturePiece("Rested", true, false, "x", true, "", null, false, Resting: true)),
        };
        var rows = DeskStrip.Rows(s);
        Assert.Equal(new[] { "Sidecar", "Desktop layer", "Win key", "Taskbar watch", "Wallpaper pause rules", "Wallpaper", "Clips", "Emoji" }, rows.Select(r => r.Title).ToArray());
        Assert.Equal(DeskStrip.NoReason, rows[1].Reason);
        Assert.Equal("not being checked, and no reason given", rows[4].Reason);
        Assert.Equal("not working, and no reason given", rows[6].Reason);
        // a resting Win key is not a fault (problems()' exemption; Electron's strip lacked it)
        Assert.DoesNotContain(DeskStrip.Rows(Healthy() with { Hotkey = new HotkeyPiece(false, "resting", Resting: true) }), r => r.Title == "Win key");
        // test mode: no Alt+Tab row and no broken-chords rows
        var t = Healthy(ShellMode.Test) with { Features = s.Features };
        Assert.DoesNotContain(DeskStrip.Rows(t), r => r.Title == "Emoji");
        Assert.Equal("· test mode · 1 thing", DeskStrip.Count(1, true));
        Assert.Equal("· 3 things", DeskStrip.Count(3, false));
    }

    [Fact]
    public void Strip_pane_backdrop_is_one_row_and_not_said_under_mantles_own_wallpaper()
    {
        var s = Healthy() with { Backdrop = Healthy().Backdrop with { Desktop = new BackdropUse(true, false, 0, 0, "no capture") } };
        Assert.Single(DeskStrip.Rows(s, localBackdrop: "the picture did not stick"), r => r.Title == "Pane backdrop");
        Assert.DoesNotContain(DeskStrip.Rows(s, hasWall: true), r => r.Title == "Pane backdrop");
        Assert.Equal("the picture did not stick", DeskStrip.Rows(Healthy(), localBackdrop: "the picture did not stick").Single().Reason);
    }

    [Fact]
    public void Menu_rows_keys_and_place()
    {
        Assert.Equal(new[] { "wallpaper", "display", "personalize", null, "widgets", null, "settings" }, DeskMenu.Rows(true).Select(r => r.Id).ToArray());
        Assert.Equal("Hide desktop widgets", DeskMenu.Rows(true)[4].Label);
        Assert.Equal("Show desktop widgets", DeskMenu.Rows(false)[4].Label);
        Assert.Equal(new[] { "", "", "", "", "" }, DeskMenu.Rows(true).Where(r => !r.Sep).Select(r => r.Glyph).ToArray());
        Assert.Equal(("settings.open", "display"), (DeskMenu.Rows(true)[1].Command, DeskMenu.Rows(true)[1].Section));
        Assert.Equal(0, DeskMenu.Step(-1, 5, "down")); Assert.Equal(4, DeskMenu.Step(-1, 5, "up"));
        Assert.Equal(0, DeskMenu.Step(4, 5, "down")); Assert.Equal(4, DeskMenu.Step(0, 5, "up"));
        Assert.Equal(0, DeskMenu.Step(3, 5, "home")); Assert.Equal(4, DeskMenu.Step(1, 5, "end"));
        Assert.Equal(2, DeskMenu.Step(2, 5, "left")); Assert.Equal(-1, DeskMenu.Step(0, 0, "down"));
        var area = new PxRect(0, 0, 1920, 1032);
        Assert.Equal(new PxRect(280, 180, 264, 228), DeskMenu.Place(300, 200, 264, 228, 20, area));          // right of and below
        Assert.Equal(new PxRect(1900 - 264 + 20, 180, 264, 228), DeskMenu.Place(1900, 200, 264, 228, 20, area)); // flipped left
        Assert.Equal(new PxRect(280, 900 - 228 + 20, 264, 228), DeskMenu.Place(300, 900, 264, 228, 20, area));   // flipped up
        Assert.Equal(new PxRect(280, 1032 - 228, 264, 228), DeskMenu.Place(300, 1020, 264, 228, 20, area));      // ...and never outside
        Assert.Equal(new PxRect(-1980, 180, 264, 228), DeskMenu.Place(-1960, 200, 264, 228, 20, null));         // no display: as asked
        var other = new PxRect(1920, 0, 2560, 1400);   // a second monitor: clamped inside ITS work area
        Assert.Equal(1920, DeskMenu.Place(1925, 10, 264, 228, 20, other).X);
        Assert.Equal("Change wallpaper did not work: nothing answers wallpaper.open", DeskMenu.Failed("Change wallpaper", "nothing answers wallpaper.open"));
    }

    [Fact]
    public void Layer_words_and_remake_rules()
    {
        Assert.Contains("(progman-front)", DesktopLayer.Attached("progman-front"));
        Assert.Equal("the desktop layer is under the wallpaper (workerw-sibling)", DesktopLayer.Attached("workerw-sibling"));
        Assert.StartsWith("the new desktop layer is waiting, hidden", DesktopLayer.AttachFailed("x", true));
        Assert.StartsWith("the desktop layer could not go under the wallpaper, so it is floating", DesktopLayer.AttachFailed("x", false));
        long now = 10_000_000;
        Assert.True(DesktopLayer.Verdict(false, false, null, now).Again);
        var q = DesktopLayer.Verdict(true, false, null, now); Assert.False(q.Again); Assert.Null(q.Reason);
        Assert.Contains("put back", DesktopLayer.Verdict(false, true, null, now).Reason);
        var at = Array.Empty<long>() as IReadOnlyList<long>; int made = 0;
        for (int i = 0; i < 8; i++) { var v = DesktopLayer.Verdict(false, false, at, now + i * 1000); at = v.At; if (v.Again) made++; }
        Assert.Equal(5, made);
        Assert.Contains("6 times in ten minutes", DesktopLayer.Verdict(false, false, at, now + 9000).Reason);
        Assert.True(DesktopLayer.Verdict(false, false, at, now + 11 * 60 * 1000).Again);   // ten minutes later: again
        Assert.Equal("none", DesktopLayer.ShellBack(true, false, false, true, true, false));
        Assert.Equal("try-now", DesktopLayer.ShellBack(false, false, false, true, true, false));
        Assert.Equal("make", DesktopLayer.ShellBack(false, false, false, false, false, false));
        Assert.Equal("wait", DesktopLayer.ShellBack(false, false, false, false, true, true));
        Assert.Equal("recheck", DesktopLayer.ShellBack(false, false, false, false, true, false));
        Assert.Equal(new[] { "done", "retry", "give-up" }, new[] { DesktopLayer.AttachNext(true, 0), DesktopLayer.AttachNext(false, 0), DesktopLayer.AttachNext(false, 5) });
    }

    [Theory]
    [InlineData(1.0, 44, 38, 420)]
    [InlineData(1.25, 55, 48, 525)]
    [InlineData(1.5, 66, 57, 630)]
    [InlineData(1.75, 77, 67, 735)]
    public void Geometry_the_cluster_on_screen_at_every_scale(double scale, int x, int y, int w)
    {
        var c = DesktopLayer.ClusterFor(1080);
        Assert.False(c.Short);
        Assert.Equal((38.0, 44.0, 420.0, 16.0, 1004.0), (c.Top, c.Left, c.Width, c.Gap, c.MaxHeight));
        var r = DesktopLayer.ToScreen(c.Left, c.Top, c.Width, 100, 0, 0, scale);
        Assert.Equal((x, y, w), (r.X, r.Y, r.Width));
        // a monitor to the right: its origin is added in physical px
        Assert.Equal(1920 + x, DesktopLayer.ToScreen(c.Left, c.Top, c.Width, 100, 1920, 0, scale).X);
    }

    [Fact]
    public void Geometry_a_short_window_packs_the_cluster_and_drops_nothing()
    {
        var c = DesktopLayer.ClusterFor(540);
        Assert.True(c.Short);
        Assert.Equal((22.0, 30.0, 11.0, 48.0, 13.0, 132.0), (c.Top, c.Left, c.Gap, c.ClockPx, c.DatePx, c.McHeight));
        Assert.Equal((11.0, 13.0, 12.0, 13.0), c.Pad);
        Assert.False(DesktopLayer.ClusterFor(761).Short);
    }

    [Fact]
    public void Geometry_page_rects_are_checked_in_words()
    {
        Assert.Null(DesktopLayer.CheckRects(new Dictionary<string, (double, double, double, double)> { ["clock"] = (44, 38, 420, 140) }));
        Assert.Contains("not a pane key", DesktopLayer.CheckRects(new Dictionary<string, (double, double, double, double)> { ["Clock!"] = (0, 0, 1, 1) }));
        Assert.Contains("not a number", DesktopLayer.CheckRects(new Dictionary<string, (double, double, double, double)> { ["mc"] = (double.NaN, 0, 1, 1) }));
        var many = Enumerable.Range(0, 17).ToDictionary(i => "p" + i, _ => (0.0, 0.0, 1.0, 1.0));
        Assert.Contains("more than the 16", DesktopLayer.CheckRects(many));
    }

    [Fact]
    public void Fixture_parses_every_key_and_says_what_is_wrong()
    {
        var f = DeskFixture.Parse("{\"now\":\"2026-11-06T16:15:32\",\"stats\":{\"cpu\":7,\"memUsed\":20830591385,\"memTotal\":34359738368,\"battery\":{\"percent\":81,\"charging\":true}},\"mc\":\"connect ECONNREFUSED 127.0.0.1:7373\",\"strip\":[[\"Sidecar\",\"it exited\"]],\"widgets\":\"off\",\"menuAt\":[300,200]}", out var err);
        Assert.Null(err);
        Assert.Equal("4:15:32 PM", DeskClock.Of(f!.Now!.Value).Text);
        Assert.Equal("7%", f.Stats!.Cpu); Assert.Equal("81% · charging", f.Stats.Battery);
        Assert.False(f.McOk); Assert.Equal("connect ECONNREFUSED 127.0.0.1:7373", f.McReason);
        Assert.Equal(new DeskRow("Sidecar", "it exited"), f.Strip!.Single());
        Assert.True(f.WidgetsOff); Assert.Equal((300, 200), f.MenuAt);
        Assert.True(DeskFixture.Parse("{\"mc\":\"ok\"}", out _)!.McOk);
        Assert.Null(DeskFixture.Parse(null, out err)); Assert.Null(err);
        Assert.Null(DeskFixture.Parse("[1]", out err)); Assert.Contains("not a JSON object", err);
        Assert.Null(DeskFixture.Parse("{", out err)); Assert.Contains("could not be read", err);
    }
}
