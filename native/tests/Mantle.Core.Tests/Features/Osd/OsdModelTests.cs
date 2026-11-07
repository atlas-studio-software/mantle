using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.Osd;
using Mantle.Core.Tests.Features.Toasts;
using Xunit;

namespace Mantle.Core.Tests.Features.Osd;

// ui/osd/osd-model.test.js, ported (OSD-031..OSD-034, OSD-042..OSD-049, OSD-055..OSD-057, OSD-083..OSD-085, OSD-097..OSD-100).
public class OsdModelTests
{
    static (FakeToastClock C, OsdFader F, int[] Faded) Fader()
    {
        var c = new FakeToastClock(); var n = new int[1];
        return (c, new OsdFader(c, () => n[0]++), n);
    }

    [Fact]
    public void The_fade_comes_1500_ms_after_the_last_poke_once()
    {
        var (c, f, n) = Fader();
        f.Poke(); c.Advance(1499); Assert.Equal(0, n[0]);
        c.Advance(1); Assert.Equal(1, n[0]);
        c.Advance(5000); Assert.Equal(1, n[0]);
    }

    [Fact]
    public void A_repeat_key_restarts_the_clock()
    {
        var (c, f, n) = Fader();
        f.Poke(); c.Advance(1000); f.Poke(); c.Advance(1000);
        Assert.Equal(0, n[0]);
        c.Advance(500); Assert.Equal(1, n[0]);
    }

    [Fact]
    public void Never_fades_while_hovered_or_held_and_fades_1500_after()
    {
        var (c, f, n) = Fader();
        f.Poke(); f.Hover(true); c.Advance(9000);
        Assert.Equal(0, n[0]); Assert.True(f.State.Up);
        f.Hover(false); c.Advance(1499); Assert.Equal(0, n[0]);
        c.Advance(1); Assert.Equal(1, n[0]);
        var (c2, f2, n2) = Fader();
        f2.Poke(); f2.Hold(true); c2.Advance(9000); Assert.Equal(0, n2[0]);
        f2.Hold(false); c2.Advance(1500); Assert.Equal(1, n2[0]);
    }

    [Fact]
    public void Reset_cancels_the_pending_fade()
    {
        var (c, f, n) = Fader();
        f.Poke(); f.Hover(true); f.Reset();
        Assert.Equal(0, n[0]); Assert.False(f.State.Armed); Assert.False(f.State.Up);
        f.Hover(false); c.Advance(5000); Assert.Equal(0, n[0]);
    }

    [Fact]
    public void Levels_read_as_the_pop_up_shows_them()
    {
        Assert.Equal(42, OsdModel.Pct(41.6)); Assert.Equal(0, OsdModel.Pct(-3)); Assert.Equal(100, OsdModel.Pct(140));
        Assert.Null(OsdModel.Pct(null)); Assert.Null(OsdModel.Pct(double.NaN));
        var d = OsdModel.Describe("volume", true, 62);
        Assert.Equal(("62", 62, "Volume", false, "Volume 62%"), (d.Value, d.Fill, d.Label, d.Dead, d.Aria));
        var m = OsdModel.Describe("volume", true, 62, muted: true);
        Assert.True(m.Muted); Assert.Equal(("Muted", "62", "Muted (volume 62%)"), (m.Label, m.Value, m.Aria));
        var x = OsdModel.Describe("volume", false, null, reason: "no playback device");
        Assert.True(x.Dead); Assert.Contains("no playback device", x.Aria); Assert.Equal("—", x.Value);
        var b = OsdModel.Describe("brightness", true, 80);
        Assert.Equal(("80", "Brightness"), (b.Value, b.Label));
        var bx = OsdModel.Describe("brightness", true, null);
        Assert.True(bx.Dead); Assert.Contains("no reason", bx.Aria);
        Assert.Equal(("0", "Muted"), (OsdModel.Describe("volume", true, 0, true).Value, OsdModel.Describe("volume", true, 0, true).Label));   // OSD-044
    }

    [Fact]
    public void The_glyphs_draw_waves_by_level_a_cross_when_muted_and_a_sun_filled_from_50()
    {
        static int Waves(IReadOnlyList<GlyphPart> g) => g.Count(p => !p.Fill && p.Stroke == 1.2);
        Assert.Equal(3, Waves(OsdModel.VolumeGlyph(80, false)));
        Assert.Equal(2, Waves(OsdModel.VolumeGlyph(66, false)));
        Assert.Equal(1, Waves(OsdModel.VolumeGlyph(20, false)));
        Assert.Equal(0, Waves(OsdModel.VolumeGlyph(0, false)));
        var muted = OsdModel.VolumeGlyph(80, true);
        Assert.Contains(muted, p => p.Data.StartsWith("M10.2 5.6l4.6 4.8")); Assert.Equal(0, Waves(muted));
        Assert.True(OsdModel.SunGlyph(60)[0].Fill); Assert.False(OsdModel.SunGlyph(30)[0].Fill);
        Assert.Equal(9, OsdModel.SunGlyph(30).Count);   // the disc + 8 rays
        Assert.NotEqual(OsdModel.SunGlyph(10)[1].Data, OsdModel.SunGlyph(90)[1].Data);   // rays grow with the level
    }

    [Fact]
    public void Reasons_are_plain_words()
    {
        var nd = OsdModel.Describe("volume", false, null, reason: "the volume key did nothing: no default playback device (HRESULT 0x80070490) - nothing is plugged in, or audio is disabled");
        Assert.StartsWith("Volume unavailable: No speakers or headphones are connected. Plug some in", nd.Aria);
        Assert.DoesNotContain("0x", nd.Aria);
        Assert.Equal("the volume could not be read: Element not found.", OsdModel.Plain("the volume could not be read: COMException: Element not found. (0x80070490)"));
        Assert.DoesNotContain("sidecar", OsdModel.Plain("the sidecar did not answer"), System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no reason was given", OsdModel.Plain("")); Assert.Equal("no reason was given", OsdModel.Plain(null));
    }

    [Fact]
    public void The_wheel_steps_one_per_notch_and_a_touchpad_adds_up()
    {
        Assert.Equal(1, OsdModel.Wheel(0, -100, 0).Steps); Assert.Equal(-1, OsdModel.Wheel(0, 100, 0).Steps);
        Assert.Equal(1, OsdModel.Wheel(0, -300, 0).Steps);
        Assert.Equal(1, OsdModel.Wheel(0, -3, 1).Steps);
        double acc = 0; int steps = 0;
        for (int i = 0; i < 40; i++) { var w = OsdModel.Wheel(acc, -4, 0); acc = w.Acc; steps += w.Steps; }
        Assert.Equal(1, steps);
        var turn = OsdModel.Wheel(-80, 30, 0);
        Assert.Equal((0, 30.0), turn);
        Assert.Equal(0, OsdModel.Wheel(0, 0, 0).Steps); Assert.Equal(0, OsdModel.Wheel(0, double.NaN, 0).Steps);
    }

    [Fact]
    public void The_app_list_keeps_first_seen_order_and_a_grace()
    {
        static AudioSession S(string k, bool active) => new(k, k, active, 50);
        static string Keys(IReadOnlyList<AudioSession> r) => string.Join(",", r.Select(s => s.Key));
        var r = OsdModel.ListApps(new[] { S("a", true), S("b", false), S("c", true) }, null, 0);
        Assert.Equal("a,c", Keys(r.Rows));
        r = OsdModel.ListApps(new[] { S("a", true), S("b", true), S("c", true) }, r.Memo, 100);
        Assert.Equal("a,c,b", Keys(r.Rows));
        r = OsdModel.ListApps(new[] { S("a", false), S("b", true), S("c", true) }, r.Memo, 3000);
        Assert.Equal("a,c,b", Keys(r.Rows));
        r = OsdModel.ListApps(new[] { S("a", false), S("b", true), S("c", true) }, r.Memo, 100 + OsdModel.GraceMs + 1);
        Assert.Equal("c,b", Keys(r.Rows));
        r = OsdModel.ListApps(new[] { S("a", false), S("b", true), S("c", true) }, r.Memo, 20000, k => k == "a");
        Assert.Contains(Keys(r.Rows), new[] { "a,c,b", "c,b,a" });
        Assert.Empty(OsdModel.ListApps(new AudioSession[0], r.Memo, 30000).Rows);
        Assert.Empty(OsdModel.ListApps(null, null, 0).Rows);
        Assert.Equal("System sounds", OsdModel.AppName(new AudioSession("x", "x", System: true)));
        Assert.Equal("An app", OsdModel.AppName(new AudioSession("x")));
    }

    [Fact]
    public void Placement_follows_the_bar_the_display_and_the_work_area()
    {
        var work = new OsdRect(0, 0, 1536, 864);
        var bar = new OsdRect(400, 812, 736, 44);
        var p = OsdModel.Place(bar, work, 260, 52, 10);
        Assert.Equal((638, 812 - 10 - 52, false), (p.X, p.Y, p.Below));
        var t = OsdModel.Place(new OsdRect(400, 8, 736, 44), work, 260, 52, 10);
        Assert.Equal((62, true), (t.Y, t.Below));
        var n = OsdModel.Place(null, work, 260, 52, 10);
        Assert.True(n.Y > 700 && n.Y + 52 <= 864);
        Assert.True(OsdModel.Place(bar, work, 360, 900, 10).Y >= 0);
        var off = OsdModel.Place(bar, new OsdRect(1536, 0, 1920, 1040), 260, 52, 10);
        Assert.True(off.X >= 1536 && off.X + 260 <= 1536 + 1920);
        Assert.Equal(1040 - 10 - 52 - 40, off.Y); Assert.False(off.Below);           // the other display's bar is ignored
        var parked = OsdModel.Place(new OsdRect(400, 900, 736, 44), work, 260, 52, 10);
        Assert.Equal(n.Y, parked.Y);                                                  // a bar parked below the screen
        var dock = OsdModel.Place(new OsdRect(0, 820, 1536, 44), new OsdRect(0, 0, 1536, 820), 260, 52, 10, new OsdRect(0, 0, 1536, 864));
        Assert.Equal(820 - 10 - 52, dock.Y);                                          // --dock bar outside the work area
        var q = OsdModel.Place(new OsdRect(400, 812, 736, 44), new OsdRect(0, 28, 1536, 836), 324, 82, 0);
        Assert.Equal((812 - 82, 606), (q.Y, q.X));
    }

    [Fact]
    public void PlaceLow_puts_the_window_bottom_on_the_work_area_and_reaches_over_the_bar()
    {
        var work = new OsdRect(0, 0, 1536, 864);
        var pill = new OsdRect(208, 802, 1120, 62);
        var p = OsdModel.PlaceLow(pill, work, 324, 82, work, (low, bar, w) => low.Y + low.Height - bar.Y);
        Assert.Equal((606, 864 - 82, 82, false), (p.X, p.Y, p.Height, p.Below));
        Assert.Equal(62, p.Reach); Assert.Equal(82 + 62, p.Full);
        var top = OsdModel.PlaceLow(new OsdRect(208, 0, 1120, 62), work, 324, 82, work, (_, _, _) => 99);
        Assert.True(top.Below); Assert.Equal(0, top.Reach);
        var none = OsdModel.PlaceLow(null, work, 324, 82, work, (_, _, _) => 99);
        Assert.Equal(0, none.Reach);
    }
}
