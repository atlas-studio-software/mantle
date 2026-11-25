using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The levitated look's v2.1 brightness check (Legibility.LiftAlpha / Film / PanelLift; lane r11-lift21, a port of look-sync
/// v2.1's liftAlpha and panelLift, look-sync 5d9e4f6 on the Engineering Manager's branch, not merged). Dark: the film
/// rgb(10, 12, 14) until the brightest tenth reads (L &lt;= 0.066). Light: the lift #F4F7F8 until the darkest tenth reads
/// (L &gt;= 0.386). Both: steps round2(k * 0.04), exact, the .72 cap, Dim a black wash, a dim outside 0..1 checked as 0.20,
/// the index over the VALID samples, channels clamped, malformed samples skipped, manual = 0. And look-sync's shared
/// vectors: 27 dark (lift-v2.json) and 29 light (lift-light.json), rev 2.1, matched exactly.
/// Every expected number below was worked out by look-sync.js 5d9e4f6 itself (liftAlpha / panelLift / relLum), not by hand.
/// </summary>
public class LevitateFilmTests
{
    static Rgb[] Fill(int n, double r, double g, double b) => Enumerable.Repeat(new Rgb(r, g, b), n).ToArray();
    static double[][] Px(int n, double r, double g, double b) => Enumerable.Range(0, n).Select(_ => new[] { r, g, b }).ToArray();
    static readonly Rgb[] White = Fill(40, 255, 255, 255);
    static readonly Rgb[] Black = Fill(40, 0, 0, 0);
    static readonly Rgb[] Dark = Fill(40, 30, 40, 45);
    static readonly Rgb[] Grey = Fill(40, 128, 128, 128);
    const string L = "light";

    [Fact]
    public void Luminance_is_WCAG_relative_luminance()
    {
        Assert.Equal(0, Legibility.L(new Rgb(0, 0, 0)));
        Assert.Equal(1, Legibility.L(new Rgb(255, 255, 255)), 12);
        Assert.Equal(0.6038273388553377, Legibility.L(new Rgb(204, 204, 204)), 12);   // white at dim .20
        Assert.Equal(10 / 255.0 / 12.92, Legibility.Lin(10), 15);                      // the linear toe
        Assert.Equal(Legibility.L(new Rgb(10, 12, 14)), Legibility.LumOf(new Rgb(255, 255, 255), 1), 12);       // all film
        Assert.Equal(Legibility.L(new Rgb(244, 247, 248)), Legibility.LumOf(new Rgb(0, 0, 0), 1, L), 12);        // all lift
        Assert.Equal(0.925319361180321, Legibility.L(new Rgb(244, 247, 248)), 12);
    }

    [Fact]
    public void The_film_is_the_themes_own_neutral()
    {
        Assert.Equal(((byte)10, (byte)12, (byte)14), Legibility.FilmRgb("dark"));
        Assert.Equal(((byte)244, (byte)247, (byte)248), Legibility.FilmRgb(L));
        Assert.Equal(Legibility.FilmRgb("dark"), Legibility.FilmRgb(null));      // anything but "light" is dark
        Assert.Equal(Legibility.FilmRgb("dark"), Legibility.FilmRgb("Light"));
        Assert.Equal(Legibility.FilmRgb("dark"), Legibility.FilmRgb("system"));
    }

    [Fact]
    public void A_dark_picture_gets_no_film_and_a_pale_one_no_lift()
    {
        Assert.Equal(0, Legibility.Film(Dark, 0.20));
        Assert.Equal(0, Legibility.Film(White, 0.90));   // white dimmed by .90 is dark already
        Assert.Equal(0, Legibility.Film(White, 0.20, L));
        Assert.Equal(0, Legibility.Film(Fill(40, 230, 235, 240), 0.20, L));
    }

    [Fact]
    public void The_steps_are_rounded_to_two_decimals_not_added_up()
    {
        var want = new[] { 0, 0.04, 0.08, 0.12, 0.16, 0.2, 0.24, 0.28, 0.32, 0.36, 0.4, 0.44, 0.48, 0.52, 0.56, 0.6, 0.64, 0.68, 0.72 };
        Assert.Equal(want, Enumerable.Range(0, 19).Select(Legibility.Step).ToArray());
        double added = 0;
        for (int i = 0; i < 18; i++) added += LevitateTokens.FilmStep;
        Assert.NotEqual(0.72, added);                    // 0.7200000000000001: float accumulation; v2.1 rounds each step
        Assert.Equal(0.72, Legibility.Step(18));
    }

    [Fact]
    public void Bright_white_gets_the_first_step_that_reads()
    {
        double a = Legibility.Film(White, 0.30);
        Assert.Equal(0.64, a);                           // exact: round2(16 * 0.04)
        Assert.Equal(0.64, Legibility.Show(a));
        var bright = new Rgb(255, 255, 255).Times(1 - 0.30);
        Assert.True(Legibility.LumOf(bright, a) <= LevitateTokens.FilmTarget);                    // 0.0644
        Assert.True(Legibility.LumOf(bright, Legibility.Step(15)) > LevitateTokens.FilmTarget);   // 0.0771: one step less does not read
        // at the seed Dim .20 white needs the whole .72, and .72 reads (0.0538; .68 gives 0.0671)
        Assert.Equal(0.72, Legibility.Film(White, 0.20));
        Assert.True(Legibility.LumOf(new Rgb(204, 204, 204), 0.72) <= LevitateTokens.FilmTarget);
        Assert.True(Legibility.LumOf(new Rgb(204, 204, 204), 0.68) > LevitateTokens.FilmTarget);
    }

    [Fact]
    public void Black_gets_the_first_lift_step_that_reads()
    {
        Assert.Equal(0.68, Legibility.Film(Black, 0.20, L));                                                    // under the .72 cap
        Assert.True(Legibility.LumOf(new Rgb(0, 0, 0), 0.68, L) >= LevitateTokens.LightFilmTarget);             // 0.3894
        Assert.True(Legibility.LumOf(new Rgb(0, 0, 0), Legibility.Step(16), L) < LevitateTokens.LightFilmTarget); // 0.3406
        double a = Legibility.Film(White, 0.50, L);
        Assert.Equal(0.36, a);
        var dimmed = new Rgb(255, 255, 255).Times(1 - 0.50);
        Assert.True(Legibility.LumOf(dimmed, a, L) >= LevitateTokens.LightFilmTarget);                          // 0.4036
        Assert.True(Legibility.LumOf(dimmed, Legibility.Step(8), L) < LevitateTokens.LightFilmTarget);          // 0.3791
    }

    [Fact]
    public void The_film_stops_at_072()
    {
        Assert.Equal(0.72, Legibility.Film(White, 0));   // undimmed white never reads under the cap
        Assert.True(Legibility.LumOf(new Rgb(255, 255, 255), 0.72) > LevitateTokens.FilmTarget);   // 0.0799
        Assert.Equal(LevitateTokens.FilmCap, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0));
        // light: black is the darkest there is and needs .68, so the cap never bites; Dim cannot make black darker
        Assert.Equal(0.68, Legibility.Film(Black, 1, L));
        Assert.Equal(0.68, Legibility.Film(White, 1, L));
    }

    [Fact]
    public void A_dim_outside_0_to_1_is_checked_as_020_and_0_to_1_is_taken_as_given()
    {
        foreach (var t in new[] { "dark", L }) {
            double at20 = Legibility.Film(Grey, 0.20, t);
            Assert.Equal(t == L ? 0.48 : 0.36, at20);
            foreach (var d in new[] { -5, -0.01, 1.5, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                Assert.Equal(at20, Legibility.Film(Grey, d, t));
        }
        Assert.Equal(0.48, Legibility.Film(Grey, 0));        // dim 0 is checked as 0, dark
        Assert.Equal(0.36, Legibility.Film(Grey, 0, L));     // and light
        Assert.Equal(0.20, Legibility.CheckDim(-0.01));
        Assert.Equal(0, Legibility.CheckDim(0));             // not LevitateDim's 0.20..0.90 slider clamp
        Assert.Equal(1, Legibility.CheckDim(1));
        Assert.Equal(0, Legibility.Film(White, 1));          // all black: no film
        Assert.Equal(0.68, Legibility.Film(Grey, 1, L));     // all black: the full lift black needs
    }

    [Fact]
    public void Dim_is_a_black_wash_in_both_themes()
    {
        double at20 = Legibility.Film(White, 0.20), at50 = Legibility.Film(White, 0.50);
        Assert.Equal(0.48, at50);                            // dark: more dim, less film
        Assert.True(at50 < at20);
        Assert.Equal(0.52, Legibility.Film(Fill(40, 170, 200, 225), 0.30));         // pale sky, dark film
        Assert.Equal(0.28, Legibility.Film(Fill(40, 170, 200, 225), 0.30, L));      // the same pale sky, light lift
        Assert.True(Legibility.Film(White, 0.90, L) > Legibility.Film(White, 0.50, L));   // light: more dim, more lift (.68 > .36)
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
    public void Light_ignores_the_darkest_10_percent_index_4_of_40_decides()
    {
        var four = Fill(4, 0, 0, 0).Concat(Fill(36, 230, 235, 240)).ToArray();
        var five = Fill(35, 230, 235, 240).Concat(Fill(5, 0, 0, 0)).ToArray();
        Assert.Equal(0, Legibility.Film(four, 0.20, L));
        Assert.Equal(0.68, Legibility.Film(five, 0.20, L));   // = all black
        // the dark rule over the same samples looks at the BRIGHT end: pale at .20 needs film
        Assert.Equal(Legibility.Film(Fill(40, 230, 235, 240), 0.20), Legibility.Film(five, 0.20));
    }

    [Fact]
    public void The_index_counts_valid_samples_only()
    {
        // 10 valid samples (index floor(10 * 0.1) = 1) and junk around them
        var junk = new double[]?[] { null, new double[] { 1 }, new double[] { 1, 2 } };
        var one = Px(1, 255, 255, 255).Concat(Px(9, 30, 40, 45)).Concat(junk).ToArray();
        var two = Px(2, 255, 255, 255).Concat(Px(8, 30, 40, 45)).Concat(junk).ToArray();
        Assert.Equal(0, Legibility.LiftAlpha(one, 0.20));      // index 1 is dark
        Assert.Equal(0.72, Legibility.LiftAlpha(two, 0.20));   // index 1 is white
        var lone = Px(1, 0, 0, 0).Concat(Px(9, 230, 235, 240)).Concat(junk).ToArray();
        var ltwo = Px(2, 0, 0, 0).Concat(Px(8, 230, 235, 240)).Concat(junk).ToArray();
        Assert.Equal(0, Legibility.LiftAlpha(lone, 0.20, L));      // index 1 is pale
        Assert.Equal(0.68, Legibility.LiftAlpha(ltwo, 0.20, L));   // index 1 is black
    }

    [Fact]
    public void Channels_are_clamped_to_0_255()
    {
        Assert.Equal(0.72, Legibility.LiftAlpha(Px(40, 999, 300, 256), 0.20));   // = white
        Assert.Equal(0, Legibility.LiftAlpha(Px(40, -50, -1, -900), 0.20));      // = black
        Assert.Equal(0.72, Legibility.Film(Fill(40, 1e9, 1e9, 1e9), 0.20));
        Assert.Equal(0, Legibility.LiftAlpha(Px(40, 999, 300, 256), 0.20, L));      // = white
        Assert.Equal(0.68, Legibility.LiftAlpha(Px(40, -50, -1, -900), 0.20, L));   // = black
        Assert.Equal(0, Legibility.Film(Fill(40, 1e9, 1e9, 1e9), 0.20, L));
    }

    [Fact]
    public void Malformed_samples_are_skipped_and_none_left_is_0()
    {
        var bad = new double[]?[] { null, new double[] { 1, 2 }, new[] { double.NaN, 0, 0 }, new[] { double.PositiveInfinity, 0, 0 }, new[] { 0, 0, double.NegativeInfinity } };
        Assert.Equal(0, Legibility.LiftAlpha(bad, 0.20));
        Assert.Equal(0, Legibility.LiftAlpha(bad, 0.20, L));
        Assert.Equal(0.72, Legibility.LiftAlpha(bad.Append(new double[] { 255, 255, 255 }).ToArray(), 0.20));
        Assert.Equal(0.68, Legibility.LiftAlpha(bad.Append(new double[] { 0, 0, 0 }).ToArray(), 0.20, L));
        Assert.Equal(0.72, Legibility.LiftAlpha(new[] { new double[] { 255, 255, 255, 0.5 } }, 0.20));   // extra entries are fine
        Assert.Equal(0.68, Legibility.LiftAlpha(new[] { new double[] { 0, 0, 0, 0.5 } }, 0.20, L));
        Assert.Equal(0, Legibility.LiftAlpha(null, 0.20));
        Assert.Equal(0, Legibility.LiftAlpha(null, 0.20, L));
        Assert.Equal(0, Legibility.LiftAlpha(Array.Empty<double[]>(), 0.20));
        Assert.Equal(0, Legibility.Film(Array.Empty<Rgb>(), 0.20));
        Assert.Equal(0, Legibility.Film(null, 0.20));
        Assert.Equal(0, Legibility.Film(null, 0.20, L));
        Assert.Equal(0, Legibility.Film(new[] { new Rgb(double.NaN, 255, 255) }, 0.20));
        Assert.Equal(0, Legibility.Film(new[] { new Rgb(double.NaN, 0, 0) }, 0.20, L));
    }

    [Fact]
    public void Only_light_is_the_light_rule()
    {
        Assert.Equal(0.72, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0.20));
        Assert.Equal(0.72, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0.20, "dark"));
        Assert.Equal(0.72, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0.20, "system"));
        Assert.Equal(0.72, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0.20, "Light"));
        Assert.Equal(0, Legibility.LiftAlpha(Px(40, 255, 255, 255), 0.20, L));
    }

    [Fact]
    public void Manual_is_no_film()
    {
        Assert.Equal(0, Legibility.Film(auto: false, White, 0.20));
        Assert.Equal(Legibility.Film(White, 0.20), Legibility.Film(auto: true, White, 0.20));
        Assert.Equal(0, Legibility.LiftAlpha(dimAuto: false, Px(40, 255, 255, 255), 0.20));
        Assert.Equal(0.72, Legibility.LiftAlpha(dimAuto: true, Px(40, 255, 255, 255), 0.20));
        Assert.Equal(0, Legibility.Film(auto: false, Black, 0.20, L));
        Assert.Equal(0, Legibility.LiftAlpha(dimAuto: false, Px(40, 0, 0, 0), 0.20, L));
        Assert.Equal(0.68, Legibility.LiftAlpha(dimAuto: true, Px(40, 0, 0, 0), 0.20, L));
    }

    [Fact]
    public void A_panel_samples_the_copy_it_shows_soft_if_big_else_light()
    {
        // light copy all white, soft copy all dark (2 x 2 each); one monitor 400 x 400 DIP
        FrostBitmap Solid(byte v) { var px = new byte[16]; for (int i = 0; i < 4; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v; px[i * 4 + 3] = 255; } return new FrostBitmap(px, 2, 2); }
        var light = Solid(255);
        var soft = Solid(30);
        Assert.Equal(0.72, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 120, 40, 0.20, auto: true));   // small: light
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 200, 100, 0.20, auto: true));     // big: soft
        Assert.Equal(0.72, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 48, 416, 0.20, auto: true));   // 19968: light
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 48, 417, 0.20, auto: true));      // 20016: soft
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 12, 12, 120, 40, 0.20, auto: false));    // manual
    }

    // ------------------------------------------------------------------ panelLift
    // Each expected record is look-sync.js 5d9e4f6 panelLift's own answer for the same input (printed by the lane's
    // scratch derive.js), field by field.

    static JsonNode J(string json) => JsonNode.Parse(json)!;
    static readonly (byte, byte, byte) DarkFilm = (10, 12, 14), LightFilm = (244, 247, 248);

    [Fact]
    public void PanelLift_dark_on_off_and_its_text()
    {
        Assert.Equal(new PanelLiftResult(0.72, "on", 0.2, DarkFilm, "Brightness check on: Dim 0.20, darkened 0.72 where the picture is bright"),
            Legibility.PanelLift(J("""{"glass":{"dark":{"dim":0.2}}}"""), "dark", Px(40, 255, 255, 255)));
        Assert.Equal(new PanelLiftResult(0, "off", 0.2, DarkFilm, "Brightness check off: Dim 0.20 only"),
            Legibility.PanelLift(J("""{"glass":{"dark":{"dim":0.2,"dimAuto":false}}}"""), "dark", Px(40, 255, 255, 255)));
        Assert.Equal(new PanelLiftResult(0, "on", 0.2, DarkFilm, "Brightness check on: Dim 0.20, nothing to darken here"),
            Legibility.PanelLift(null, "dark", Px(40, 30, 40, 45)));
        Assert.Equal(new PanelLiftResult(0.48, "on", 0.5, DarkFilm, "Brightness check on: Dim 0.50, darkened 0.48 where the picture is bright"),
            Legibility.PanelLift(J("""{"glass":{"dark":{"dim":0.5,"dimAuto":"no"}}}"""), "dark", Px(40, 255, 255, 255)));   // only false is off
    }

    [Fact]
    public void PanelLift_light_lays_F4F7F8_and_dimAuto_switches_it()
    {
        Assert.Equal(new PanelLiftResult(0.68, "on", 0.2, LightFilm, "Brightness check on: Dim 0.20, lightened 0.68 where the picture is dark"),
            Legibility.PanelLift(J("""{"glass":{"light":{"dim":0.2}}}"""), L, Px(40, 0, 0, 0)));
        Assert.Equal(new PanelLiftResult(0, "off", 0.2, LightFilm, "Brightness check off: Dim 0.20 only"),
            Legibility.PanelLift(J("""{"glass":{"light":{"dim":0.2,"dimAuto":false}}}"""), L, Px(40, 0, 0, 0)));
        Assert.Equal(new PanelLiftResult(0, "on", 0.2, LightFilm, "Brightness check on: Dim 0.20, nothing to lighten here"),
            Legibility.PanelLift(J("""{"glass":{"light":{"dim":0.2}}}"""), L, Px(40, 255, 255, 255)));
        // the shared() shape
        Assert.Equal(new PanelLiftResult(0.36, "on", 0.5, LightFilm, "Brightness check on: Dim 0.50, lightened 0.36 where the picture is dark"),
            Legibility.PanelLift(J("""{"glass":{"v":{"light":{"dim":0.5}}}}"""), L, Px(40, 255, 255, 255)));
        // each theme reads its own dimAuto: dark off does not switch the light lift off
        Assert.Equal("on", Legibility.PanelLift(J("""{"glass":{"dark":{"dimAuto":false},"light":{"dim":0.2}}}"""), L, Px(40, 0, 0, 0)).Check);
    }

    [Fact]
    public void PanelLift_takes_the_seed_for_a_dim_outside_its_range_not_a_clamp()
    {
        var white = Px(40, 255, 255, 255);
        Assert.Equal(0.2, Legibility.PanelLift(J("""{"glass":{"dark":{"dim":0.05}}}"""), "dark", white).Dim);
        Assert.Equal(0.2, Legibility.PanelLift(J("""{"glass":{"dark":{"dim":0.95}}}"""), "dark", white).Dim);   // LevitateDim would clamp to .90
        Assert.Equal(0.72, Legibility.PanelLift(J("""{"glass":{"dark":{"dim":0.95}}}"""), "dark", white).Alpha);
        var s = Legibility.PanelLift(J("""{"glass":{"dark":{"dim":"0.5"}}}"""), "system", white);   // a string is not a number; "system" is dark
        Assert.Equal(new PanelLiftResult(0.72, "on", 0.2, DarkFilm, "Brightness check on: Dim 0.20, darkened 0.72 where the picture is bright"), s);
    }

    // ------------------------------------------------------------------ the shared vectors
    // Fixtures/look/v2/lift-v2.json (dark, 27) and lift-light.json (light, 29): look-sync v2.1 @ 5d9e4f6 (the Engineering
    // Manager's branch, not merged) test/fixtures/lift-v2.json and lift-light.json, byte-for-byte copies (line ends aside),
    // made by look-sync's lift-v2.gen.js from the prototype's own code (quartz-home app.js filmFor, 9383686) and LIGHT.md
    // section 4b's liftFor, both with the v2.1 constants. Do not edit them here; when look-sync changes them, copy them
    // again. Shape: { about, rev: "2.1", theme, lift: { cols, rows, quantile, rgb, target, step, max, light: { rgb, target } },
    //   vectors: [ { name, dim, samples: [[r, g, b] x 40] (BEFORE dim), alpha } ] }. The old v2 (0.163 / .56) vectors are
    // retired.

    static string V2Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "look", "v2");
    static JsonObject Load(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(V2Dir, file)))!;
    static readonly Lazy<JsonObject> LiftDark = new(() => Load("lift-v2.json"));
    static readonly Lazy<JsonObject> LiftLight = new(() => Load("lift-light.json"));

    static double[][] Samples(JsonArray a) => a.Select(c => ((JsonArray)c!).Select(v => v!.GetValue<double>()).ToArray()).ToArray();

    /// Every vector of one file, compared EXACTLY (no tolerance), through both entry points.
    static void AllMatch(JsonObject file, string theme, int count)
    {
        Assert.Equal("2.1", file["rev"]!.GetValue<string>());
        Assert.Equal(theme, file["theme"]!.GetValue<string>());
        var vectors = (JsonArray)file["vectors"]!;
        Assert.Equal(count, vectors.Count);
        var bad = new List<string>();
        foreach (var c in vectors) {
            var o = (JsonObject)c!;
            string name = o["name"]!.GetValue<string>();
            double dim = o["dim"]!.GetValue<double>();
            double want = o["alpha"]!.GetValue<double>();
            var samples = Samples((JsonArray)o["samples"]!);
            Assert.Equal(40, samples.Length);
            double got = Legibility.LiftAlpha(samples, dim, theme);
            if (got != want) bad.Add($"{theme} {name}: dim {dim:R}: want {want:R}, got {got:R}");
            // the Rgb path (what a panel reads) gives the same
            if (Legibility.Film(samples.Select(s => new Rgb(s[0], s[1], s[2])).ToArray(), dim, theme) != got) bad.Add($"{theme} {name}: Film differs from LiftAlpha");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Every_dark_lift_vector_matches_exactly() => AllMatch(LiftDark.Value, "dark", 27);

    [Fact]
    public void Every_light_lift_vector_matches_exactly() => AllMatch(LiftLight.Value, L, 29);

    [Fact]
    public void The_lift_constants_in_both_files_are_ours()
    {
        foreach (var file in new[] { LiftDark.Value, LiftLight.Value }) {
            var lift = (JsonObject)file["lift"]!;
            Assert.Equal(Legibility.Cols, lift["cols"]!.GetValue<int>());
            Assert.Equal(Legibility.Rows, lift["rows"]!.GetValue<int>());
            Assert.Equal(Legibility.BrightRank, lift["quantile"]!.GetValue<double>());
            var rgb = ((JsonArray)lift["rgb"]!).Select(v => v!.GetValue<int>()).ToArray();
            Assert.Equal(new[] { (int)LevitateTokens.FilmR, LevitateTokens.FilmG, LevitateTokens.FilmB }, rgb);
            Assert.Equal(LevitateTokens.FilmTarget, lift["target"]!.GetValue<double>());
            Assert.Equal(LevitateTokens.FilmStep, lift["step"]!.GetValue<double>());
            Assert.Equal(LevitateTokens.FilmCap, lift["max"]!.GetValue<double>());
            var light = (JsonObject)lift["light"]!;
            var lrgb = ((JsonArray)light["rgb"]!).Select(v => v!.GetValue<int>()).ToArray();
            Assert.Equal(new[] { (int)LevitateTokens.LightFilmR, LevitateTokens.LightFilmG, LevitateTokens.LightFilmB }, lrgb);
            Assert.Equal(LevitateTokens.LightFilmTarget, light["target"]!.GetValue<double>());
            Assert.Contains("lift-v2.gen.js", file["about"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Only_look_syncs_files_are_in_the_v2_folder()
    {
        Assert.Equal(new[] { "lift-light.json", "lift-v2.json" },
            Directory.GetFiles(V2Dir).Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray());
    }
}
