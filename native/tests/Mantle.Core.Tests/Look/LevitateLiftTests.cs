using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mantle.App.Shell.Levitate;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Lane r16-sep-wiring: the app side of look-sync v2.3 separation. Mantle.App/Shell/Levitate/LevitateLift.cs is compiled
/// into this project as a link (it is pure C#), so these tests run the app's own code: every number it hands LevitatePanel
/// is Core's PanelLift(.., separate: true) unchanged, the check off gives film 0 and sep 0, and every Levitate host lays the
/// panel's layers in LevitateLift.Order (picture, dim, film, sep, sheen; SEPARATION.md).
/// </summary>
public class LevitateLiftTests
{
    static string Here([CallerFilePath] string here = "") => Path.GetDirectoryName(here)!;
    static string AppDir => Path.GetFullPath(Path.Combine(Here(), "..", "..", "..", "src", "Mantle.App"));
    static string Src(params string[] rel) => File.ReadAllText(Path.Combine(new[] { AppDir }.Concat(rel).ToArray()));

    static Rgb[] Uni(byte r, byte g, byte b) => Enumerable.Repeat(new Rgb(r, g, b), Legibility.Cols * Legibility.Rows).ToArray();
    static Rgb[] Ramp() => Enumerable.Range(0, 40).Select(i => new Rgb(i * 6, 40 + i * 4, 255 - i * 5)).ToArray();

    static readonly Rgb[][] Pictures = {
        Uni(255, 255, 255), Uni(0, 0, 0), Uni(17, 32, 35), Uni(44, 82, 88), Uni(200, 205, 210), Uni(120, 130, 90), Ramp(),
    };

    [Fact]
    public void The_layers_are_picture_dim_film_sep_sheen_bottom_to_top()
    {
        Assert.Equal(new[] { "picture", "dim", "film", "sep", "sheen" }, LevitateLift.Order);
        // LevitatePanel.Layers is built in that order
        string panel = Src("Shell", "Levitate", "LevitatePanel.cs");
        Assert.Contains("public Border[] Layers => new[] { Picture, Dim, Film, Sep, Sheen };", panel);
    }

    [Fact]
    public void Every_Levitate_host_adds_the_whole_layer_list_so_every_island_gets_the_separation_layer()
    {
        var hosts = Directory.GetFiles(AppDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("new LevitatePanel(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        // the bar's islands, the pop-up host (Downloads, quick settings) and the launcher; a new host must be added here
        Assert.Equal(new[] { "BarWindow.Islands.cs", "LauncherSurface.Islands.cs", "PopupWindow.cs" }, hosts);
        foreach (var f in Directory.GetFiles(AppDir, "*.cs", SearchOption.AllDirectories).Where(f => hosts.Contains(Path.GetFileName(f)))) {
            string code = File.ReadAllText(f);
            Assert.Contains(".Layers", code);
            // no host picks single layers (it would leave Sep out or put it in the wrong place)
            Assert.DoesNotMatch(new Regex(@"\blev\.(Picture|Dim|Film|Sep|Sheen)\b"), code);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Film_and_sep_are_Cores_PanelLift_with_separate_unchanged(bool dark)
    {
        string theme = dark ? "dark" : "light";
        foreach (var dim in new[] { 0.20, 0.35, 0.60, 0.90 })
            foreach (var pic in Pictures) {
                var a = LevitateLift.For(dark, dim, true, pic);
                var r = Legibility.PanelLift(JsonNode.Parse("{\"glass\":{\"" + theme + "\":{\"dim\":" + dim.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}"), theme,
                    pic.Select(s => new[] { s.R, s.G, s.B }).ToArray(), separate: true);
                Assert.Equal(r.Alpha, a.Film);
                Assert.Equal(r.Film, a.FilmRgb);
                Assert.Equal(r.Sep!.Value, a.Sep);
                Assert.Equal(r.SepRgb!.Value, a.SepRgb);
                Assert.Equal(r.Dim, a.Dim);
                Assert.Equal(dim, a.Dim);   // Core's dim is the one the panel washes with
                Assert.Equal("on", a.Check);
            }
    }

    [Fact]
    public void The_shared_v23_vectors_come_through_exactly()
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(Here(), "separation-v23.json")))!;
        int n = 0, withSep = 0;
        foreach (var v in doc["vectors"]!.AsArray()) {
            double dim = v!["dim"]!.GetValue<double>();
            if (dim < 0.20 || dim > 0.90) continue;   // PanelLift reads a saved dim only in 0.20..0.90 (the panel never has another)
            var pic = v["samples"]!.AsArray().Select(s => new Rgb(s![0]!.GetValue<double>(), s[1]!.GetValue<double>(), s[2]!.GetValue<double>())).ToArray();
            var a = LevitateLift.For(v["theme"]!.GetValue<string>() != "light", dim, true, pic);
            Assert.Equal(v["alpha"]!.GetValue<double>(), a.Film);
            Assert.Equal(v["sep"]!.GetValue<double>(), a.Sep);
            n++; if (a.Sep > 0) withSep++;
        }
        Assert.Equal(62, n);
        Assert.Equal(13, withSep);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_check_off_means_no_film_and_no_separation(bool dark)
    {
        foreach (var pic in Pictures) {
            var a = LevitateLift.For(dark, 0.30, false, pic);
            Assert.Equal(0.0, a.Film);
            Assert.Equal(0.0, a.Sep);
            Assert.Equal("off", a.Check);
        }
    }

    [Fact]
    public void The_layer_is_white_in_dark_and_0A0C0E_in_light_and_never_over_016()
    {
        foreach (var pic in Pictures) {
            var d = LevitateLift.For(true, 0.20, true, pic);
            var l = LevitateLift.For(false, 0.20, true, pic);
            Assert.Equal(((byte)255, (byte)255, (byte)255), d.SepRgb);
            Assert.Equal(((byte)10, (byte)12, (byte)14), l.SepRgb);
            foreach (var s in new[] { d.Sep, l.Sep }) {
                Assert.InRange(s, 0.0, Separate.Max);
                Assert.Equal(Math.Round(s / Separate.Step), s / Separate.Step, 9);   // a 0.02 step
            }
        }
    }

    [Fact]
    public void The_look_handed_to_Core_carries_the_panels_dim_and_switch()
    {
        var look = LevitateLift.LookOf("light", 0.45, false);
        Assert.Equal(0.45, look["glass"]!["light"]!["dim"]!.GetValue<double>());
        Assert.False(look["glass"]!["light"]!["dimAuto"]!.GetValue<bool>());
        Assert.Empty(LevitateLift.Triples(null));
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, LevitateLift.Triples(new[] { new Rgb(1, 2, 3) })[0]);
    }
}
