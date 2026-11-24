using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The levitated look's v2 brightness check (Legibility.LiftAlpha / Film; lane r10-newlook, a port of look-sync v2's
/// liftAlpha): dark is 0, bright white is the first 0.04 step that reads, rounded to 2 decimals (exact), the 0.56 cap, dim
/// lowering the film, a dim outside 0..1 checked as 0.20, the top 10 % of the VALID samples ignored, channels clamped,
/// malformed samples skipped, manual = 0. And look-sync's 27 shared vectors.
/// </summary>
public class LevitateFilmTests
{
    static Rgb[] Fill(int n, double r, double g, double b) => Enumerable.Repeat(new Rgb(r, g, b), n).ToArray();
    static double[][] Px(int n, double r, double g, double b) => Enumerable.Range(0, n).Select(_ => new[] { r, g, b }).ToArray();
    static readonly Rgb[] White = Fill(40, 255, 255, 255);
    static readonly Rgb[] Dark = Fill(40, 30, 40, 45);

    [Fact]
    public void Luminance_is_WCAG_relative_luminance()
    {
        Assert.Equal(0, Legibility.L(new Rgb(0, 0, 0)));
        Assert.Equal(1, Legibility.L(new Rgb(255, 255, 255)), 12);
        Assert.Equal(0.6038273388553377, Legibility.L(new Rgb(204, 204, 204)), 12);   // white at dim .20
        Assert.Equal(10 / 255.0 / 12.92, Legibility.Lin(10), 15);                      // the linear toe
        Assert.Equal(Legibility.L(new Rgb(10, 12, 14)), Legibility.LumOf(new Rgb(255, 255, 255), 1), 12);   // all film
    }

    [Fact]
    public void A_dark_picture_gets_no_film()
    {
        Assert.Equal(0, Legibility.Film(Dark, 0.20));
        Assert.Equal(0, Legibility.Film(White, 0.90));   // white dimmed by .90 is dark already
    }

    [Fact]
    public void The_steps_are_rounded_to_two_decimals_not_added_up()
    {
        var want = new[] { 0, 0.04, 0.08, 0.12, 0.16, 0.2, 0.24, 0.28, 0.32, 0.36, 0.4, 0.44, 0.48, 0.52, 0.56 };
        Assert.Equal(want, Enumerable.Range(0, 15).Select(Legibility.Step).ToArray());
        double added = 0;
        for (int i = 0; i < 12; i++) added += LevitateTokens.FilmStep;
        Assert.NotEqual(0.48, added);                    // 0.4799999999999999: the first lane's loop; v2 rounds each step
        Assert.Equal(0.48, Legibility.Step(12));
    }

    [Fact]
    public void Bright_white_gets_the_first_step_that_reads()
    {
        double a = Legibility.Film(White, 0.20);
        Assert.Equal(0.48, a);                           // exact: round2(12 * 0.04)
        Assert.Equal(0.48, Legibility.Show(a));
        var bright = new Rgb(204, 204, 204);
        Assert.True(Legibility.LumOf(bright, a) <= LevitateTokens.FilmTarget);
        Assert.True(Legibility.LumOf(bright, Legibility.Step(11)) > LevitateTokens.FilmTarget);   // one step less does not read
    }

    [Fact]
    public void The_film_stops_at_056()
    {
        Assert.Equal(0.56, Legibility.Film(White, 0));   // undimmed white never reads under the cap
        Assert.Equal(LevitateTokens.FilmCap, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0));
    }

    [Fact]
    public void A_dim_outside_0_to_1_is_checked_as_020_and_0_to_1_is_taken_as_given()
    {
        double at20 = Legibility.Film(White, 0.20);
        Assert.Equal(at20, Legibility.Film(White, -5));
        Assert.Equal(at20, Legibility.Film(White, 1.5));
        Assert.Equal(at20, Legibility.Film(White, double.NaN));
        Assert.Equal(at20, Legibility.Film(White, double.PositiveInfinity));
        Assert.Equal(0.20, Legibility.CheckDim(-0.01));
        Assert.Equal(0, Legibility.CheckDim(0));         // not LevitateDim's 0.20..0.90 slider clamp
        Assert.Equal(1, Legibility.CheckDim(1));
        Assert.Equal(0, Legibility.Film(White, 1));      // all black
    }

    [Fact]
    public void More_dim_means_less_film()
    {
        double at20 = Legibility.Film(White, 0.20), at50 = Legibility.Film(White, 0.50);
        Assert.Equal(0.16, at50);
        Assert.True(at50 < at20);
        Assert.Equal(0.2, Legibility.Film(Fill(40, 170, 200, 225), 0.30));   // pale sky
    }

    [Fact]
    public void The_brightest_10_percent_is_ignored_index_4_of_40_decides()
    {
        var four = Fill(4, 255, 255, 255).Concat(Fill(36, 30, 40, 45)).ToArray();
        var five = Fill(35, 30, 40, 45).Concat(Fill(5, 255, 255, 255)).ToArray();
        Assert.Equal(0, Legibility.Film(four, 0.20));
        Assert.Equal(Legibility.Film(White, 0.20), Legibility.Film(five, 0.20));
    }

    [Fact]
    public void The_index_counts_valid_samples_only()
    {
        // 10 valid samples (index floor(10 * 0.1) = 1) and junk around them
        var junk = new double[]?[] { null, new double[] { 1 }, new double[] { 1, 2 } };
        var one = Px(1, 255, 255, 255).Concat(Px(9, 30, 40, 45)).Concat(junk).ToArray();
        var two = Px(2, 255, 255, 255).Concat(Px(8, 30, 40, 45)).Concat(junk).ToArray();
        Assert.Equal(0, Legibility.LiftAlpha(one, 0.20));      // index 1 is dark
        Assert.Equal(0.48, Legibility.LiftAlpha(two, 0.20));   // index 1 is white
    }

    [Fact]
    public void Channels_are_clamped_to_0_255()
    {
        Assert.Equal(0.48, Legibility.LiftAlpha(Px(40, 999, 300, 256), 0.20));   // = white
        Assert.Equal(0, Legibility.LiftAlpha(Px(40, -50, -1, -900), 0.20));      // = black
        Assert.Equal(0.48, Legibility.Film(Fill(40, 1e9, 1e9, 1e9), 0.20));
    }

    [Fact]
    public void Malformed_samples_are_skipped_and_none_left_is_0()
    {
        var bad = new double[]?[] { null, new double[] { 1, 2 }, new[] { double.NaN, 0, 0 }, new[] { double.PositiveInfinity, 0, 0 }, new[] { 0, 0, double.NegativeInfinity } };
        Assert.Equal(0, Legibility.LiftAlpha(bad, 0.20));
        Assert.Equal(0.48, Legibility.LiftAlpha(bad.Append(new double[] { 255, 255, 255 }).ToArray(), 0.20));
        Assert.Equal(0.48, Legibility.LiftAlpha(new[] { new double[] { 255, 255, 255, 0.5 } }, 0.20));   // extra entries are fine
        Assert.Equal(0, Legibility.LiftAlpha(null, 0.20));
        Assert.Equal(0, Legibility.LiftAlpha(Array.Empty<double[]>(), 0.20));
        Assert.Equal(0, Legibility.Film(Array.Empty<Rgb>(), 0.20));
        Assert.Equal(0, Legibility.Film(null, 0.20));
        Assert.Equal(0, Legibility.Film(new[] { new Rgb(double.NaN, 255, 255) }, 0.20));
    }

    [Fact]
    public void Manual_is_no_film()
    {
        Assert.Equal(0, Legibility.Film(auto: false, White, 0.20));
        Assert.Equal(Legibility.Film(White, 0.20), Legibility.Film(auto: true, White, 0.20));
        Assert.Equal(0, Legibility.LiftAlpha(dimAuto: false, Px(40, 255, 255, 255), 0.20));
        Assert.Equal(0.48, Legibility.LiftAlpha(dimAuto: true, Px(40, 255, 255, 255), 0.20));
    }

    [Fact]
    public void A_panel_samples_the_copy_it_shows_soft_if_big_else_light()
    {
        // light copy all white, soft copy all dark (2 x 2 each); one monitor 400 x 400 DIP
        FrostBitmap Solid(byte v) { var px = new byte[16]; for (int i = 0; i < 4; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v; px[i * 4 + 3] = 255; } return new FrostBitmap(px, 2, 2); }
        var light = Solid(255);
        var soft = Solid(30);
        Assert.Equal(0.48, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 120, 40, 0.20, auto: true));   // small: light
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 200, 100, 0.20, auto: true));     // big: soft
        Assert.Equal(0.48, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 48, 416, 0.20, auto: true));   // 19968: light
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 48, 417, 0.20, auto: true));      // 20016: soft
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 120, 40, 0.20, auto: false));    // manual
    }

    // ------------------------------------------------------------------ the shared vectors
    // Fixtures/look/v2/lift-v2.json: source look-sync 9a53e89 test/lift-v2.json (not merged yet), a byte-for-byte copy;
    // made by look-sync's lift-v2.gen.js from the prototype's own code (quartz-home app.js filmFor, 9383686). Do not edit
    // it here; when look-sync changes it, copy it again. Shape:
    // { about, lift: { cols, rows, quantile, rgb: [r, g, b], target, step, max },
    //   vectors: [ { name, dim, samples: [[r, g, b] x 40] (BEFORE dim), alpha } x 27 ] }

    static string V2Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "look", "v2");
    static readonly Lazy<JsonObject> LiftV2 = new(() => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(V2Dir, "lift-v2.json")))!);

    static double[][] Samples(JsonArray a) => a.Select(c => ((JsonArray)c!).Select(v => v!.GetValue<double>()).ToArray()).ToArray();

    [Fact]
    public void Every_lift_v2_vector_matches_exactly()
    {
        var vectors = (JsonArray)LiftV2.Value["vectors"]!;
        Assert.Equal(27, vectors.Count);
        var bad = new List<string>();
        foreach (var c in vectors) {
            var o = (JsonObject)c!;
            string name = o["name"]!.GetValue<string>();
            double dim = o["dim"]!.GetValue<double>();
            double want = o["alpha"]!.GetValue<double>();
            var samples = Samples((JsonArray)o["samples"]!);
            Assert.Equal(40, samples.Length);
            double got = Legibility.LiftAlpha(samples, dim);
            if (got != want) bad.Add($"{name}: dim {dim:R}: want {want:R}, got {got:R}");
            // the Rgb path (what a panel reads) gives the same
            if (Legibility.Film(samples.Select(s => new Rgb(s[0], s[1], s[2])).ToArray(), dim) != got) bad.Add($"{name}: Film differs from LiftAlpha");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void The_lift_v2_constants_are_ours()
    {
        var lift = (JsonObject)LiftV2.Value["lift"]!;
        Assert.Equal(Legibility.Cols, lift["cols"]!.GetValue<int>());
        Assert.Equal(Legibility.Rows, lift["rows"]!.GetValue<int>());
        Assert.Equal(Legibility.BrightRank, lift["quantile"]!.GetValue<double>());
        var rgb = ((JsonArray)lift["rgb"]!).Select(v => v!.GetValue<int>()).ToArray();
        Assert.Equal(new[] { (int)LevitateTokens.FilmR, LevitateTokens.FilmG, LevitateTokens.FilmB }, rgb);
        Assert.Equal(LevitateTokens.FilmTarget, lift["target"]!.GetValue<double>());
        Assert.Equal(LevitateTokens.FilmStep, lift["step"]!.GetValue<double>());
        Assert.Equal(LevitateTokens.FilmCap, lift["max"]!.GetValue<double>());
        Assert.Contains("lift-v2.gen.js", LiftV2.Value["about"]!.GetValue<string>());
    }

    [Fact]
    public void Only_look_syncs_file_is_in_the_v2_folder()
    {
        // the first lane's hand-computed placeholder (film.json) is gone
        Assert.Equal(new[] { "lift-v2.json" }, Directory.GetFiles(V2Dir).Select(f => Path.GetFileName(f)).ToArray());
    }
}
