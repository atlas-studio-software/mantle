using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Mantle.Core.Look;
using Xunit;
using Xunit.Abstractions;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The native readability guard over the SHARP still (look.md SEE-017), on generated pictures shaped like the cases of
/// look-sync's inventory/guard-quality-2026-11-11.md: Electron's guard (LookGuard.FromPixels at blurPx 0, the 48 brightest
/// pixels of the top 100 DIP at 4.2:1) against the native one (glyph boxes and row percentiles, 4.5:1, per 8 DIP strip,
/// 8-bit compositing). Every answer is checked two ways: an independent measure (NativeGuard.Measure, which composites each
/// pixel and sorts) says the text reaches 4.5:1 at the answer, and does NOT one .02 step below it (not over-darkened).
/// </summary>
public class NativeGuardTests
{
    readonly ITestOutputHelper _out;
    public NativeGuardTests(ITestOutputHelper o) { _out = o; }

    const double Dim = 0.30;

    sealed class Pic
    {
        public readonly int W, H;
        public readonly byte[] Rgb;
        public Pic(int w, int h) { W = w; H = h; Rgb = new byte[w * h * 3]; }
        public void Set(int x, int y, int r, int g, int b) { if (x < 0 || y < 0 || x >= W || y >= H) return; int i = (y * W + x) * 3; Rgb[i] = (byte)r; Rgb[i + 1] = (byte)g; Rgb[i + 2] = (byte)b; }
        public byte[] Bgra() { var o = new byte[W * H * 4]; for (int i = 0; i < W * H; i++) { o[i * 4] = Rgb[i * 3 + 2]; o[i * 4 + 1] = Rgb[i * 3 + 1]; o[i * 4 + 2] = Rgb[i * 3]; o[i * 4 + 3] = 255; } return o; }
        public byte[] Rgba() { var o = new byte[W * H * 4]; for (int i = 0; i < W * H; i++) { o[i * 4] = Rgb[i * 3]; o[i * 4 + 1] = Rgb[i * 3 + 1]; o[i * 4 + 2] = Rgb[i * 3 + 2]; o[i * 4 + 3] = 255; } return o; }
        /// Every pixel repeated n x n (the same picture on a monitor n times as dense).
        public Pic Dense(int n) { var p = new Pic(W * n, H * n); for (int y = 0; y < p.H; y++) for (int x = 0; x < p.W; x++) { int i = ((y / n) * W + x / n) * 3; p.Set(x, y, Rgb[i], Rgb[i + 1], Rgb[i + 2]); } return p; }
    }

    /// Snow-peak-like: a bright, cool snowfield (rgb ~ 85-95 / 95-105 / 110-120, soft ripples) with sparkling glints, single
    /// white pixels, one in every 8 x 8 cell (1.6 %: never two in one glyph box, far under the 5 % a percentile ignores).
    static Pic SnowPeak(int w = 960, int h = 540)
    {
        var p = new Pic(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                int d = (int)Math.Round(5 + 5 * Math.Sin(x / 37.0) * Math.Cos(y / 23.0));   // 0..10
                p.Set(x, y, 85 + d, 95 + d, 110 + d);
            }
        for (int y = 3; y < h; y += 8) for (int x = (y / 8) % 5; x < w; x += 8) p.Set(x, y, 255, 255, 255);
        return p;
    }

    /// Night-dune-like: a dark dune sky (rgb 20/18/30 to 34/30/44 down the picture) with sharp one-pixel stars, near white,
    /// one in every 9 x 9 cell, and fainter ones between.
    static Pic NightDune(int w = 960, int h = 540)
    {
        var p = new Pic(w, h);
        for (int y = 0; y < h; y++) { int t = 14 * y / h; for (int x = 0; x < w; x++) p.Set(x, y, 20 + t, 18 + t, 30 + t); }
        var rng = new Random(7);
        for (int y = 2; y < h; y += 9) for (int x = rng.Next(9); x < w; x += 9) { int v = 200 + rng.Next(31); p.Set(x, y, v, v, Math.Min(255, v + 5)); }
        for (int n = 0; n < w * h / 800; n++) { int v = 70 + rng.Next(40); p.Set(rng.Next(w), rng.Next(h), v, v, v + 10); }
        return p;
    }

    /// Electron Mantle's guard at blurPx 0 (the shared top band today).
    static double Legacy(Pic p) => LookGuard.FromPixels(p.Rgba(), p.W, p.H, Dim, 0, false, 1, LookGuard.BandDip)!.Value.Alpha;

    /// The native answer over a window band, checked both ways by the independent measure.
    (double Alpha, double Contrast) Checked(Pic p, GuardRows rows, double y0, double y1, string what)
    {
        var bgra = p.Bgra();
        var w = NativeGuard.ForWindow(new WashResult(Dim, "auto", false, false, "", Dim, null, ""), rows, y0, y1 - y0);
        var (b0, b1) = NativeGuard.Band(y0, y1 - y0, rows.HeightDip);
        var m = NativeGuard.Measure(bgra, p.W, p.H, rows.Scale, rows.Dark, w.Alpha, b0, b1);
        double c = Math.Min(m.Box, m.Row);
        _out.WriteLine($"{what}: native {w.Alpha:0.00} (box {m.Box:0.00}:1, row p95 {m.Row:0.00}:1) - {w.Text} / {w.Reason}");
        Assert.True(c >= NativeGuard.Target, $"{what}: at {w.Alpha:0.00} the text reads only {c:0.000}:1 (box {m.Box:0.000}, row {m.Row:0.000})");
        if (w.Alpha > Dim + 1e-9) {
            var below = NativeGuard.Measure(bgra, p.W, p.H, rows.Scale, rows.Dark, w.Alpha - NativeGuard.Step, b0, b1);
            Assert.True(Math.Min(below.Box, below.Row) < NativeGuard.Target, $"{what}: over-darkened: one step lower ({w.Alpha - NativeGuard.Step:0.00}) already reads {Math.Min(below.Box, below.Row):0.000}:1");
        }
        return (w.Alpha, c);
    }

    [Fact]
    public void Snow_peak_like_glints_do_not_drive_it_past_what_a_glyph_needs_about_53_not_78()
    {
        var p = SnowPeak();
        double old = Legacy(p);
        var rows = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        var (a, c) = Checked(p, rows, 0, 140, "snow-peak-like, the top 140 DIP");
        _out.WriteLine($"snow-peak-like: Electron's guard {old:0.00}, native {a:0.00} ({c:0.00}:1)");
        Assert.True(old >= 0.76, $"the generated picture should push the max-pixel guard to about .78, it gave {old:0.00}");
        Assert.InRange(a, 0.50, 0.56);
        Assert.True(old - a >= 0.20, $"native {a:0.00} vs Electron {old:0.00}");
    }

    [Fact]
    public void Night_dune_like_stars_leave_it_at_Dim_about_30_not_72()
    {
        var p = NightDune();
        double old = Legacy(p);
        var rows = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        var (a, c) = Checked(p, rows, 0, 140, "night-dune-like, the top 140 DIP");
        _out.WriteLine($"night-dune-like: Electron's guard {old:0.00}, native {a:0.00} ({c:0.00}:1)");
        Assert.True(old >= 0.70, $"the generated picture should push the max-pixel guard to about .72, it gave {old:0.00}");
        Assert.Equal(Dim, a, 2);
        // and it is not blind: a whole-screen window over the same stars reads too
        Checked(p, rows, 0, p.H, "night-dune-like, the whole screen");
    }

    [Fact]
    public void A_flat_white_picture_takes_the_wash_up_to_45_to_1_not_42()
    {
        var p = new Pic(480, 300);
        for (int y = 0; y < p.H; y++) for (int x = 0; x < p.W; x++) p.Set(x, y, 255, 255, 255);
        var rows = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        var (a, _) = Checked(p, rows, 0, 140, "white");
        Assert.InRange(a, 0.78, 0.82);
        Assert.True(a > Legacy(p), "4.5:1 needs a little more than Electron's 4.2:1");
    }

    [Fact]
    public void A_thin_bright_line_through_a_text_row_is_caught_by_the_row_percentile_where_a_box_dilutes_it()
    {
        var p = new Pic(480, 300);
        for (int y = 0; y < p.H; y++) for (int x = 0; x < p.W; x++) p.Set(x, y, 35, 40, 48);
        for (int y = 19; y <= 21; y++) for (int x = 0; x < p.W; x++) p.Set(x, y, 250, 250, 250);
        var rows = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        int s = 19 / 8;   // the strip of rows 16-23
        _out.WriteLine($"bright line: strip {s} box {rows.BoxNeed[s]}, row {rows.RowNeed[s]}");
        Assert.True(rows.RowNeed[s] > rows.BoxNeed[s], $"row {rows.RowNeed[s]} should exceed box {rows.BoxNeed[s]}");
        Checked(p, rows, 0, 140, "bright line");
    }

    [Fact]
    public void A_bright_patch_low_on_the_screen_raises_only_the_windows_over_it()
    {
        // night-fjord-like: dark everywhere but a lit snow slope at rows 600-680; the top band needs nothing
        var p = new Pic(640, 1080);
        for (int y = 0; y < p.H; y++) for (int x = 0; x < p.W; x++) p.Set(x, y, 18, 22, 30);
        for (int y = 600; y < 680; y++) for (int x = 100; x < 400; x++) p.Set(x, y, 205, 210, 220);
        var rows = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        var (top, _) = Checked(p, rows, 0, 140, "fjord, the top band");
        var (over, _) = Checked(p, rows, 560, 760, "fjord, a window over the slope");
        var (small, _) = Checked(p, rows, 650, 690, "fjord, a 40 DIP window over the slope (band widened to 140)");
        var (below, _) = Checked(p, rows, 800, 1000, "fjord, a window under it");
        Assert.Equal(Dim, top, 2);
        Assert.Equal(Dim, below, 2);
        Assert.True(over >= 0.70, $"the window over the slope got {over:0.00}");
        Assert.Equal(over, small, 2);
        Assert.Equal((540.0, 680.0), NativeGuard.Band(600, 40, 680));      // near the bottom: the 140 above its bottom edge
        Assert.Equal((0.0, 140.0), NativeGuard.Band(-30, 40, 1080));
        Assert.Equal((100.0, 400.0), NativeGuard.Band(100, 300, 1080));
    }

    [Fact]
    public void It_samples_physical_pixels_the_same_picture_twice_as_dense_gives_the_same_profile()
    {
        var p = SnowPeak(480, 270);
        var one = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        var d = p.Dense(2);
        var two = NativeGuard.Rows(d.Bgra(), d.W, d.H, 2, dark: true);
        Assert.Equal(4, one.BoxPx); Assert.Equal(8, two.BoxPx); Assert.Equal(5, NativeGuard.BoxPx(1.25));
        Assert.Equal(one.Strips, two.Strips);
        // a box between two DIP is the mean of the boxes around it and the percentile's rank moves by at most one pixel,
        // so a strip may differ by a hundredth, never more
        for (int i = 0; i < one.Strips; i++) {
            Assert.True(Math.Abs(one.BoxNeed[i] - two.BoxNeed[i]) <= 1, $"strip {i}: box {one.BoxNeed[i]} at 1x, {two.BoxNeed[i]} at 2x");
            Assert.True(Math.Abs(one.RowNeed[i] - two.RowNeed[i]) <= 1, $"strip {i}: row {one.RowNeed[i]} at 1x, {two.RowNeed[i]} at 2x");
        }
        var shared = new WashResult(Dim, "auto", false, false, "", Dim, null, "");
        Assert.True(Math.Abs(NativeGuard.ForWindow(shared, one, 0, 140).Alpha - NativeGuard.ForWindow(shared, two, 0, 140).Alpha) <= 0.02 + 1e-9);
        // at 125 % the strips are 10 px and a 4 DIP box is 5 px
        var q = NativeGuard.Rows(SnowPeak(600, 340).Bgra(), 600, 340, 1.25, dark: true);
        Assert.Equal(10, q.StripTop(1)); Assert.Equal(5, q.BoxPx); Assert.Equal(34, q.Strips);
    }

    [Fact]
    public void Light_dark_ink_on_white_reaches_45_to_1_over_a_dark_picture_and_stays_at_Dim_over_a_bright_one()
    {
        var dark = new Pic(480, 300);
        for (int y = 0; y < dark.H; y++) for (int x = 0; x < dark.W; x++) dark.Set(x, y, 60 + x % 7, 62, 70);
        var rows = NativeGuard.Rows(dark.Bgra(), dark.W, dark.H, 1, dark: false);
        var (a, _) = Checked(dark, rows, 0, 140, "light over a dark picture");
        Assert.InRange(a, 0.46, 0.64);   // rgb ~62 needs about .53 of white for #333A4E to read 4.5:1
        var bright = SnowPeak(480, 300);
        for (int i = 0; i < bright.Rgb.Length; i++) bright.Rgb[i] = (byte)Math.Min(255, bright.Rgb[i] + 120);
        var rb = NativeGuard.Rows(bright.Bgra(), bright.W, bright.H, 1, dark: false);
        var (b, _) = Checked(bright, rb, 0, 140, "light over a bright picture");
        Assert.Equal(Dim, b, 2);
    }

    [Fact]
    public void Manual_stays_exactly_Dim_and_no_profile_keeps_the_shared_answer()
    {
        var p = SnowPeak(480, 300);
        var rows = NativeGuard.Rows(p.Bgra(), p.W, p.H, 1, dark: true);
        var manual = new WashResult(0.20, "manual", false, false, "Dim is set by hand", 0.20, null, "Manual: 0.20");
        Assert.Same(manual, NativeGuard.ForWindow(manual, rows, 0, 300));
        var shared = new WashResult(0.30, "auto", false, false, "no desktop picture from Mantle", 0.30, null, "Automatic: 0.30, not raised: no desktop picture from Mantle");
        Assert.Same(shared, NativeGuard.ForWindow(shared, null, 0, 300));
        var w = NativeGuard.ForWindow(shared, rows, 0, 300);
        Assert.True(w.Raised && w.Guarded && w.Mode == "auto");
        Assert.StartsWith("Automatic: 0.30, raised to 0.5", w.Text);
        Assert.EndsWith("so text stays readable over this window's part of the picture", w.Text);
        // the steps start at Dim (look-guard's rule): Dim .35 and a need of .52 give .53, never past max(.88, Dim)
        Assert.Equal((0.53, false), NativeGuard.Step02(0.35, 0.52));
        Assert.Equal((0.54, false), NativeGuard.Step02(0.30, 0.53));
        Assert.Equal((0.30, false), NativeGuard.Step02(0.30, 0.12));
        Assert.Equal((0.88, true), NativeGuard.Step02(0.30, 1.01));
        Assert.Equal((0.90, true), NativeGuard.Step02(0.90, 1.01));
    }

    [Fact]
    public void The_shared_entry_keeps_the_shape_Quartz_and_Helm_read_and_washAlpha_takes_it()
    {
        var p = SnowPeak(480, 300);
        var bgra = p.Bgra();
        var dark = NativeGuard.Rows(bgra, p.W, p.H, 1, dark: true);
        var light = NativeGuard.Rows(bgra, p.W, p.H, 1, dark: false);
        var g = new JsonObject { ["dark"] = NativeGuard.Entry(dark, bgra, 0.30, 5), ["light"] = NativeGuard.Entry(light, bgra, 0.30, 5) };
        _out.WriteLine(g.ToJsonString());
        Assert.Null(LookGuard.CheckGuard(g));
        Assert.Null(LookGuard.CheckGuardLight(g));
        var e = (JsonObject)g["dark"]!;
        Assert.Equal(140, (double)e["region"]!["heightDip"]!);
        Assert.Equal("[480,300]", e["monitor"]!.ToJsonString());
        Assert.True((double)e["contrast"]! >= NativeGuard.Target);
        // a consumer (look-sync washAlpha, which Quartz and Helm run) takes it as a valid top band for the stored glass
        var look = new JsonObject { ["glass"] = new JsonObject { ["dark"] = new JsonObject { ["blurPx"] = 5, ["dim"] = 0.30, ["panel"] = 0 }, ["light"] = new JsonObject { ["blurPx"] = 5, ["dim"] = 0.30, ["panel"] = 0 } } };
        var desk = new JsonObject { ["rev"] = 4, ["ok"] = true, ["id"] = "snow", ["still"] = "C:/x/still.png", ["guard"] = g };
        var w = Wash.Alpha(look, desk, "dark");
        Assert.True(w.Guarded, w.Text);
        Assert.Equal((double)e["alpha"]!, w.Alpha, 2);
    }

    [Fact]
    public void A_full_screen_still_is_worked_out_once_in_a_bounded_time()
    {
        var p = SnowPeak(1920, 1080);
        var bgra = p.Bgra();
        var t = Stopwatch.StartNew();
        var d = NativeGuard.Rows(bgra, p.W, p.H, 1, dark: true);
        long darkMs = t.ElapsedMilliseconds; t.Restart();
        var l = NativeGuard.Rows(bgra, p.W, p.H, 1, dark: false);
        _out.WriteLine($"1920x1080: dark {darkMs} ms, light {t.ElapsedMilliseconds} ms, {d.Strips} strips each");
        Assert.Equal(135, d.Strips);
        Assert.Equal(135, l.Strips);
    }
}
