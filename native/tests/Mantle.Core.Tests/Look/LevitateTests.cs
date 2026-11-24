using System;
using System.Text.Json.Nodes;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The levitated look's pure layer (lane r10-newlook; prototype quartz-home/app.js + style.css, 2026-11-24): the BIG rule,
/// the cover geometry and a panel's offsets, the 8 x 5 sampling map and its clamps, the dim, the pad-stretch blur and the
/// motion curves. The brightness check itself is LevitateFilmTests. Reference values come from gen-levitate-vectors.js.
/// </summary>
public class LevitateTests
{
    /// The motion curves are solved numerically (Newton, then bisection); the references are bisection to 1e-12.
    const double CurveTolerance = 1e-4;
    const double Exact = 1e-12;

    static void Near(double want, double got, double tolerance) =>
        Assert.True(Math.Abs(want - got) <= tolerance, $"want {want:R}, got {got:R} (tolerance {tolerance:R})");

    // ------------------------------------------------------------------ BIG

    [Fact]
    public void IsBig_needs_both_a_48_DIP_side_and_20000_square_DIP()
    {
        Assert.True(Island.IsBig(48, 417));       // 20016
        Assert.False(Island.IsBig(47, 1000));     // the short side 47
        Assert.True(Island.IsBig(1000, 48));
        Assert.False(Island.IsBig(48, 416));      // 19968
        Assert.False(Island.IsBig(100, 199.99));  // 19999
        Assert.True(Island.IsBig(100, 200));      // 20000
        Assert.False(Island.IsBig(0, 0));
    }

    // ------------------------------------------------------------------ cover geometry

    [Fact]
    public void Cover_scales_to_fill_and_centres_the_overflow()
    {
        var a = Island.Cover(512, 288, 1536, 864);   // 16:9 on 16:9: s 3, no overflow
        Near(3, a.S, Exact); Near(1536, a.Cw, Exact); Near(864, a.Ch, Exact);
        Near(0, a.Ox, Exact); Near(0, a.Oy, Exact);

        var b = Island.Cover(512, 512, 1920, 1080);  // square on 16:9: width wins, the height overflows
        Near(3.75, b.S, Exact); Near(1920, b.Cw, Exact); Near(1920, b.Ch, Exact);
        Near(0, b.Ox, Exact); Near(-420, b.Oy, Exact);

        var c = Island.Cover(1000, 500, 1600, 1200); // 2:1 on 4:3: height wins, the width overflows
        Near(2.4, c.S, Exact); Near(2400, c.Cw, Exact); Near(1200, c.Ch, Exact);
        Near(-400, c.Ox, Exact); Near(0, c.Oy, Exact);

        Assert.Equal(new CoverFit(0, 0, 0, 0, 0), Island.Cover(0, 288, 1536, 864));
        Assert.Equal(new CoverFit(0, 0, 0, 0, 0), Island.Cover(512, 288, 0, 864));
    }

    [Fact]
    public void A_panel_draws_the_copy_at_ox_minus_left_oy_minus_top_at_the_cover_size()
    {
        var fit = Island.Cover(512, 512, 1920, 1080);
        var (x, y, w, h) = Island.Draw(fit, 100, 50);
        Near(-100, x, Exact); Near(-470, y, Exact);
        Near(1920, w, Exact); Near(1920, h, Exact);
        var top = Island.Draw(Island.Cover(512, 288, 1536, 864), 8, 8);   // an island one gap in from the corner
        Assert.Equal((-8.0, -8.0, 1536.0, 864.0), top);
    }

    // ------------------------------------------------------------------ the 8 x 5 sampling map

    [Fact]
    public void ToBitmap_maps_through_the_cover_and_clamps_to_the_bitmap()
    {
        var fit = Island.Cover(512, 288, 1536, 864);   // s 3 in DIP; the bitmap is 1/3 of the monitor
        Assert.Equal((0, 0), Island.ToBitmap(fit, 512, 288, 0, 0));
        Assert.Equal((256, 144), Island.ToBitmap(fit, 512, 288, 768, 432));
        Assert.Equal((0, 0), Island.ToBitmap(fit, 512, 288, -50, -1));        // left of / above the picture
        Assert.Equal((511, 287), Island.ToBitmap(fit, 512, 288, 2000, 900));  // right of / below it
        Assert.Equal((511, 287), Island.ToBitmap(fit, 512, 288, 1536, 864));  // the far edge itself
    }

    [Fact]
    public void Grid_is_8_by_5_at_cell_centres_i_major()
    {
        var fit = Island.Cover(512, 512, 1920, 1080);   // oy -420, cw = ch = 1920
        var g = Legibility.Grid(fit, 512, 512, 0, 0, 1920, 1080);
        Assert.Equal(40, g.Length);
        Assert.Equal((32, 140), g[0]);      // (i 0, j 0): (120, 108) -> (32, 528/1920*512 = 140.8)
        Assert.Equal((96, 140), g[5]);      // (i 1, j 0): x 360 -> 96
        Assert.Equal((480, 371), g[39]);    // (i 7, j 4): (1800, 972) -> (480, 1392/1920*512 = 371.2)
    }

    [Fact]
    public void Grid_of_a_panel_off_the_picture_is_clamped_to_its_edge()
    {
        var fit = Island.Cover(512, 288, 1536, 864);
        var g = Legibility.Grid(fit, 512, 288, -1000, 900, 100, 50);
        foreach (var p in g) { Assert.Equal(0, p.X); Assert.Equal(287, p.Y); }
    }

    [Fact]
    public void Samples_read_rgb_out_of_bgra_at_the_grid()
    {
        // 2 x 1: left px BGRA (10, 20, 30, 255), right px (40, 50, 60, 255); a panel over the left half samples the left px
        var copy = new FrostBitmap(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, 2, 1);
        var fit = Island.Cover(2, 1, 200, 100);
        var s = Legibility.Samples(copy, fit, 0, 0, 100, 100);
        Assert.Equal(40, s.Length);
        Assert.All(s, c => Assert.Equal(new Rgb(30, 20, 10), c));
        Assert.All(Legibility.Samples(copy, fit, 100, 0, 100, 100), c => Assert.Equal(new Rgb(60, 50, 40), c));
    }

    // ------------------------------------------------------------------ the dim

    [Fact]
    public void Dim_defaults_to_020_a_saved_value_wins_clamped_to_020_090()
    {
        Assert.Equal(0.20, LevitateDim.Resolve(null));
        Assert.Equal(0.20, LevitateDim.Resolve(double.NaN));
        Assert.Equal(0.20, LevitateDim.Resolve(double.PositiveInfinity));
        Assert.Equal(0.45, LevitateDim.Resolve(0.45));
        Assert.Equal(0.20, LevitateDim.Resolve(0.20));
        Assert.Equal(0.90, LevitateDim.Resolve(0.90));
        Assert.Equal(0.20, LevitateDim.Resolve(0.05));   // the prototype's own slider goes down to 0; the contract does not
        Assert.Equal(0.90, LevitateDim.Resolve(0.95));
        Assert.Equal(0.20, LevitateDim.Resolve(-1));
    }

    [Fact]
    public void Dim_and_mode_are_read_from_glass_theme_dim_and_dimAuto()
    {
        var look = JsonNode.Parse("{\"glass\":{\"dark\":{\"dim\":0.45,\"dimAuto\":false},\"light\":{\"dim\":1.5}}}");
        Assert.Equal((0.45, false), LevitateDim.Read(look, "dark"));
        Assert.Equal((0.90, true), LevitateDim.Read(look, "light"));     // clamped; dimAuto absent = automatic
        Assert.Equal((0.45, false), LevitateDim.Read(look, "anything")); // anything but light is dark
        Assert.Equal((0.20, true), LevitateDim.Read(null, "dark"));
        Assert.Equal((0.20, true), LevitateDim.Read(JsonNode.Parse("{\"glass\":{\"dark\":{\"dim\":\"x\"}}}"), "dark"));
        Assert.Equal((0.30, true), LevitateDim.Read(JsonNode.Parse("{\"glass\":{\"v\":{\"dark\":{\"dim\":0.3,\"dimAuto\":true}}}}"), "dark"));   // shared() shape
    }

    // ------------------------------------------------------------------ the pad-stretch blur

    static FrostBitmap Fill(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; }
        return new FrostBitmap(px, w, h);
    }

    [Fact]
    public void The_still_is_scaled_to_512_wide_height_by_aspect()
    {
        Assert.Equal((512, 288), Frost.BaseSize(1920, 1080));
        Assert.Equal((512, 288), Frost.BaseSize(3840, 2160));
        Assert.Equal((512, 1536), Frost.BaseSize(1000, 3000));
        Assert.Equal((512, 1), Frost.BaseSize(4000, 1));     // at least one row
        Assert.Equal((0, 0), Frost.BaseSize(0, 5));
    }

    [Fact]
    public void The_padding_is_ceil_3_sigma_and_the_kernel_is_a_normalised_gaussian()
    {
        Assert.Equal(18, Frost.Pad(LevitateTokens.LightBlurPx));
        Assert.Equal(45, Frost.Pad(LevitateTokens.SoftBlurPx));
        Assert.Equal(3, Frost.Pad(1));
        Assert.Equal(2, Frost.Pad(0.5));
        Assert.Equal(0, Frost.Pad(0));
        var k = Frost.Kernel(1);
        Assert.Equal(7, k.Length);
        double sum = 0; foreach (var v in k) sum += v;
        Assert.Equal(1.0, sum, 5);
        Assert.Equal(0.39905028, k[3], 6);   // gen-levitate-vectors.js
        Assert.Equal(0.24203623, k[2], 6);
        Assert.Equal(0.00443305, k[0], 6);
        Assert.Equal(k[1], k[5]);
    }

    [Fact]
    public void Padding_stretches_the_edge_rows_and_columns()
    {
        // 2 x 2: A B / C D (only the blue byte differs)
        var src = new FrostBitmap(new byte[] { 1, 0, 0, 255, 2, 0, 0, 255, 3, 0, 0, 255, 4, 0, 0, 255 }, 2, 2);
        var p = Frost.Padded(src, 1);
        Assert.Equal(4, p.W); Assert.Equal(4, p.H);
        byte At(int x, int y) => p.Bgra[(y * p.W + x) * 4];
        Assert.Equal(new byte[] { 1, 1, 2, 2 }, new[] { At(0, 0), At(1, 0), At(2, 0), At(3, 0) });   // the top row stretched up
        Assert.Equal(new byte[] { 1, 1, 3, 3 }, new[] { At(0, 0), At(0, 1), At(0, 2), At(0, 3) });   // the left column stretched out
        Assert.Equal(4, At(3, 3));
        Assert.Equal(255, p.Bgra[(3 * p.W + 3) * 4 + 3]);
    }

    [Fact]
    public void A_uniform_picture_stays_uniform_to_its_borders()
    {
        // a zero-padded blur would fade every border; the stretched padding keeps each pixel exactly the picture's colour
        var src = Fill(5, 4, 120, 80, 40);
        foreach (double sigma in new[] { 1.0, LevitateTokens.LightBlurPx, LevitateTokens.SoftBlurPx }) {
            var b = Frost.Blur(src, sigma);
            Assert.Equal(5, b.W); Assert.Equal(4, b.H);
            Assert.Equal(src.Bgra, b.Bgra);
        }
    }

    [Fact]
    public void One_bright_pixel_spreads_by_the_kernel_and_the_padding_is_cropped()
    {
        // one row of 7, the middle one white: x 0..6 = 255 * k[6], k[5], k[4], k[3], ... (sigma 1, radius 3)
        var src = Fill(7, 1, 0, 0, 0);
        for (int c = 0; c < 3; c++) src.Bgra[3 * 4 + c] = 255;
        var b = Frost.Blur(src, 1);
        Assert.Equal(7, b.W); Assert.Equal(1, b.H);
        var red = new byte[7];
        for (int x = 0; x < 7; x++) { red[x] = b.Bgra[x * 4 + 2]; Assert.Equal(255, b.Bgra[x * 4 + 3]); }
        Assert.Equal(new byte[] { 1, 14, 62, 102, 62, 14, 1 }, red);   // 1.1304, 13.7714, 61.7192, 101.7578 rounded
        Assert.Equal(b.Bgra[3 * 4], b.Bgra[3 * 4 + 2]);                // every channel alike
    }

    [Fact]
    public void No_blur_is_a_copy_and_a_bad_bitmap_says_so()
    {
        var src = Fill(3, 2, 9, 8, 7);
        var b = Frost.Blur(src, 0);
        Assert.Equal(src.Bgra, b.Bgra);
        Assert.NotSame(src.Bgra, b.Bgra);
        Assert.Throws<ArgumentException>(() => Frost.Blur(new FrostBitmap(new byte[4], 2, 2), 1));
    }

    // ------------------------------------------------------------------ motion

    [Fact]
    public void Ease_is_cubic_bezier_02_08_02_1_and_never_overshoots()
    {
        var e = LevitateTokens.Ease;
        Assert.Equal(new Bezier(0.2, 0.8, 0.2, 1), e);
        Assert.Equal(0, e.Y(0)); Assert.Equal(1, e.Y(1));
        Near(0.396694, e.Y(0.1), CurveTolerance);
        Near(0.767284, e.Y(0.25), CurveTolerance);
        Near(0.946079, e.Y(0.5), CurveTolerance);
        Near(0.991108, e.Y(0.75), CurveTolerance);
        for (int k = 0; k <= 100; k++) Assert.True(e.Y(k / 100.0) <= 1 + 1e-9);
    }

    [Fact]
    public void Ease_lift_is_cubic_bezier_022_112_036_1_with_a_hair_of_overshoot()
    {
        var e = LevitateTokens.EaseLift;
        Assert.Equal(new Bezier(0.22, 1.12, 0.36, 1), e);
        Assert.Equal(0, e.Y(0)); Assert.Equal(1, e.Y(1));
        Near(0.441276, e.Y(0.1), CurveTolerance);
        Near(0.817361, e.Y(0.25), CurveTolerance);
        Near(0.988610, e.Y(0.5), CurveTolerance);
        Near(1.003439, e.Y(0.75), CurveTolerance);   // above 1: the lift
        Near(1.003737, e.Y(0.7047), CurveTolerance); // its peak
        Assert.Equal("0.22,1.12,0.36,1", e.ToString());
    }

    [Fact]
    public void Durations_and_the_sink_and_lift_tracks()
    {
        Assert.Equal(160, LevitateTokens.DurMs);
        Assert.Equal(340, LevitateTokens.DurSlowMs);
        Assert.Equal(new Track("opacity", 0, 1, 280, 60, LevitateTokens.Ease), Motion.LiftIn[0]);
        Assert.Equal(new Track("translateY", 10, 0, 420, 40, LevitateTokens.EaseLift), Motion.LiftIn[1]);
        Assert.Equal(new Track("opacity", 1, 0, 200, 0, LevitateTokens.Ease), Motion.SinkOut[0]);
        Assert.Equal(new Track("translateY", 0, 12, 300, 0, LevitateTokens.Ease), Motion.SinkOut[1]);
        Assert.Equal(new Track("scale", 1, 0.955, 300, 0, LevitateTokens.Ease), Motion.SinkOut[2]);
        Assert.Equal((0.5, 0.35), (Motion.SinkOriginX, Motion.SinkOriginY));
        Assert.Equal(460, Motion.TotalMs(Motion.LiftIn, reduceMotion: false));   // 40 + 420
        Assert.Equal(300, Motion.TotalMs(Motion.SinkOut, reduceMotion: false));
        Assert.Equal(0, Motion.TotalMs(Motion.LiftIn, reduceMotion: true));
    }

    [Fact]
    public void A_track_holds_its_start_through_the_delay_then_follows_its_curve()
    {
        var fade = Motion.LiftIn[0];
        var rise = Motion.LiftIn[1];
        Assert.Equal(0, Motion.At(fade, 0, false));
        Assert.Equal(0, Motion.At(fade, 60, false));
        Assert.Equal(1, Motion.At(fade, 340, false));
        Assert.Equal(10, Motion.At(rise, 40, false));
        Near(10 - 10 * 0.988610, Motion.At(rise, 40 + 210, false), CurveTolerance);
        Assert.True(Motion.At(rise, 40 + 315, false) < 0);   // ease-lift passes the rest point a hair, then settles
        Assert.Equal(0, Motion.At(rise, 1000, false));
    }

    [Fact]
    public void Reduce_motion_is_the_end_state_at_once()
    {
        Assert.Equal(1, Motion.At(Motion.LiftIn[0], 0, reduceMotion: true));
        Assert.Equal(0, Motion.At(Motion.LiftIn[1], 0, reduceMotion: true));
        Assert.Equal(0.955, Motion.At(Motion.SinkOut[2], 0, reduceMotion: true));
        Assert.Equal(0, Motion.SlideDuration(instant: false, reduceMotion: true));
    }

    [Fact]
    public void The_sliding_highlight_moves_x_and_width_over_320ms_on_ease_lift_or_instantly_at_first()
    {
        var s = Motion.Slide(10, 40, 110, 60);
        Assert.Equal(new Track("translateX", 10, 110, 320, 0, LevitateTokens.EaseLift), s[0]);
        Assert.Equal(new Track("width", 40, 60, 320, 0, LevitateTokens.EaseLift), s[1]);
        Assert.Equal(320, Motion.SlideDuration(instant: false, reduceMotion: false));
        Assert.Equal(0, Motion.SlideDuration(instant: true, reduceMotion: false));
    }
}
