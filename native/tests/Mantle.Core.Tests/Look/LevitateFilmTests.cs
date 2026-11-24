using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The levitated look's v2 brightness check (Legibility.Film; lane r10-newlook, prototype quartz-home/app.js 2026-11-24):
/// dark is 0, bright white is the first 0.04 step that reads, the 0.56 cap, dim lowering the film, the top 10 % ignored,
/// manual = 0. And every vector file in Fixtures/look/v2/ (today film.json, hand-computed by gen-levitate-vectors.js; the
/// Engineering Manager's look-sync v2 vectors replace it in the same shape).
/// </summary>
public class LevitateFilmTests
{
    /// The film is a sum of 0.04 steps from 0 in doubles, exactly as the JS loop accumulates, so the two agree to the last
    /// bit; this is the room left for a vector file written with fewer digits.
    const double FilmTolerance = 1e-9;

    static Rgb[] Fill(int n, double r, double g, double b) => Enumerable.Repeat(new Rgb(r, g, b), n).ToArray();
    static readonly Rgb[] White = Fill(40, 255, 255, 255);
    static readonly Rgb[] Dark = Fill(40, 30, 40, 45);

    /// a after n steps of the JS loop (a += 0.04 from 0).
    static double Steps(int n) { double a = 0; for (int i = 0; i < n; i++) a += LevitateTokens.FilmStep; return a; }

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
    public void Bright_white_gets_the_first_step_that_reads()
    {
        double a = Legibility.Film(White, 0.20);
        Assert.Equal(Steps(12), a);                         // 0.4799999999999999, bit for bit the JS accumulation
        Assert.Equal(0.48, a, FilmTolerance);
        Assert.Equal(0.48, Legibility.Show(a));
        var bright = new Rgb(204, 204, 204);
        Assert.True(Legibility.LumOf(bright, a) <= LevitateTokens.FilmTarget);
        Assert.True(Legibility.LumOf(bright, Steps(11)) > LevitateTokens.FilmTarget);   // one step less does not read
    }

    [Fact]
    public void The_film_stops_at_056()
    {
        Assert.Equal(0.56, Legibility.Film(White, 0), FilmTolerance);   // undimmed white never reads under the cap
        Assert.True(Legibility.Film(White, 0) <= LevitateTokens.FilmCap);
        Assert.Equal(LevitateTokens.FilmCap, Legibility.Film(White, -5));   // out-of-range dims do not break the cap
    }

    [Fact]
    public void More_dim_means_less_film()
    {
        double at20 = Legibility.Film(White, 0.20), at50 = Legibility.Film(White, 0.50);
        Assert.Equal(0.16, at50, FilmTolerance);
        Assert.True(at50 < at20);
        Assert.Equal(0.2, Legibility.Film(Fill(40, 170, 200, 225), 0.30), FilmTolerance);   // pale sky
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
    public void Manual_is_no_film_and_no_samples_is_no_film()
    {
        Assert.Equal(0, Legibility.Film(auto: false, White, 0.20));
        Assert.Equal(Legibility.Film(White, 0.20), Legibility.Film(auto: true, White, 0.20));
        Assert.Equal(0, Legibility.Film(Array.Empty<Rgb>(), 0.20));
    }

    [Fact]
    public void A_panel_samples_the_copy_it_shows_soft_if_big_else_light()
    {
        // light copy all white, soft copy all dark (2 x 2 each); one monitor 400 x 400 DIP
        FrostBitmap Solid(byte v) { var px = new byte[16]; for (int i = 0; i < 4; i++) { px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v; px[i * 4 + 3] = 255; } return new FrostBitmap(px, 2, 2); }
        var light = Solid(255);
        var soft = Solid(30);
        Assert.Equal(Steps(12), Legibility.ForPanel(light, soft, 400, 400, 8, 8, 120, 40, 0.20, auto: true));   // small: light
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 8, 8, 200, 100, 0.20, auto: true));          // big: soft
        Assert.Equal(0, Legibility.ForPanel(light, soft, 400, 400, 8, 8, 120, 40, 0.20, auto: false));         // manual
    }

    // ------------------------------------------------------------------ the vector files

    static string V2Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "look", "v2");

    static Rgb[] Samples(JsonArray a) => a.Select(c => {
        var t = (JsonArray)c!;
        return new Rgb(t[0]!.GetValue<double>(), t[1]!.GetValue<double>(), t[2]!.GetValue<double>());
    }).ToArray();

    /// Every case of every file: { cases: [ { name, dim, samples: [[r, g, b] x 40], film, auto? } ], tolerance? }.
    /// A case with "auto": false expects 0 (manual).
    [Fact]
    public void Every_v2_vector_file_matches()
    {
        var files = Directory.GetFiles(V2Dir, "*.json");
        Assert.NotEmpty(files);
        int n = 0;
        var bad = new List<string>();
        foreach (var f in files) {
            var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(f))!;
            double tol = doc["tolerance"] is JsonValue tv && tv.TryGetValue(out double td) ? Math.Max(td, FilmTolerance) : FilmTolerance;
            foreach (var c in (JsonArray)doc["cases"]!) {
                var o = (JsonObject)c!;
                string name = o["name"]?.GetValue<string>() ?? ("case " + n);
                bool auto = !(o["auto"] is JsonValue av && av.TryGetValue(out bool ab) && !ab);
                double dim = o["dim"]!.GetValue<double>();
                double want = o["film"]!.GetValue<double>();
                double got = Legibility.Film(auto, Samples((JsonArray)o["samples"]!), dim);
                if (Math.Abs(got - want) > tol) bad.Add($"{Path.GetFileName(f)}: {name}: dim {dim}: want {want:R}, got {got:R}");
                n++;
            }
        }
        Assert.True(n > 0, "no vectors in " + V2Dir);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void The_placeholder_vectors_say_they_are_placeholders()
    {
        var doc = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(V2Dir, "film.json")))!;
        // look-sync v2's own file replaces this one; until then it must say it was worked out by hand
        Assert.Equal("hand-computed, replace with look-sync v2 vectors", doc["source"]!.GetValue<string>());
        Assert.InRange(((JsonArray)doc["cases"]!).Count, 4, 6);
    }
}
