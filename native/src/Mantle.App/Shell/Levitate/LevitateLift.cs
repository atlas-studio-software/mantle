// LevitateLift.cs - what one levitated island lays over its picture (lane r16-sep-wiring, look-sync v2.3 "separation",
// apps-r3/SEPARATION.md): Mantle.Core Legibility.PanelLift(look, theme, samples, separate: true), passed through as it is.
// The film (rgb(10, 12, 14) in dark, #F4F7F8 in light) at Alpha, then the separation layer (white in dark, #0A0C0E in
// light) at Sep over it. No maths here: every number is Core's. With the brightness check off (dimAuto false) Core answers
// film 0 and sep 0, so the one switch turns both off.
//
// Pure C# (no WinUI): Mantle.Core.Tests compiles this file in as a link (LevitateLiftTests), so the app side of the wiring
// is unit-tested without a window. LevitatePanel is its only caller in the app.
using System.Text.Json.Nodes;
using Mantle.Core.Look.Levitate;

namespace Mantle.App.Shell.Levitate;

internal static class LevitateLift
{
    /// An island's layers, bottom to top (SEPARATION.md: picture, film, sep, hover sheen; the dim is the picture's own wash,
    /// LOOK.md section 4). LevitatePanel.Layers is built in this order and every host adds it in this order.
    public static readonly string[] Order = { "picture", "dim", "film", "sep", "sheen" };

    /// Core's answer for one island: the film's alpha and colour, the separation layer's alpha and colour, Core's dim and
    /// its check words.
    public sealed record Answer(double Film, (byte R, byte G, byte B) FilmRgb, double Sep, (byte R, byte G, byte B) SepRgb, double Dim, string Check, string Text);

    /// The look Core reads, from the dim and the check switch the panel already resolved (LevitateDim): <c>{ glass: {
    /// &lt;theme&gt;: { dim, dimAuto } } }</c>.
    public static JsonObject LookOf(string theme, double dim, bool auto) =>
        new() { ["glass"] = new JsonObject { [theme] = new JsonObject { ["dim"] = dim, ["dimAuto"] = auto } } };

    /// The 40 samples as Core's rgb triples.
    public static double[][] Triples(Rgb[]? samples)
    {
        if (samples == null) return System.Array.Empty<double[]>();
        var t = new double[samples.Length][];
        for (int i = 0; i < samples.Length; i++) t[i] = new[] { samples[i].R, samples[i].G, samples[i].B };
        return t;
    }

    /// Core's PanelLift with separate: true for this island in this theme ("light", anything else is dark).
    public static Answer For(bool dark, double dim, bool auto, Rgb[]? samples)
    {
        string theme = dark ? "dark" : "light";
        var r = Legibility.PanelLift(LookOf(theme, dim, auto), theme, Triples(samples), separate: true);
        return new Answer(r.Alpha, r.Film, r.Sep ?? 0, r.SepRgb ?? Separate.RgbFor(theme), r.Dim, r.Check, r.Text);
    }
}
