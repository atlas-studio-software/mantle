using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// ui/launcher/launcher.js GLYPH: the rows with no bitmap draw one of these 16x16 line glyphs (currentColor = --lg-ink-2),
/// chosen by item.glyph, else item.kind. The SVG path data is Electron's, byte for byte, parsed here into XAML geometry
/// (no XamlReader: see LauncherSurface). Stroke 1.5 (1.6 for command and setting; 1.6 for all in dark), round caps and joins.
/// </summary>
internal static class LauncherGlyphs
{
    /// One glyph: stroked shapes and filled shapes, each an SVG element: "M..." path data, "c cx cy r" a circle, "r x y w h rx" a rect.
    sealed record Def(double Stroke, bool RoundJoin, string[] Stroked, string[]? Filled = null);

    static readonly Dictionary<string, Def> Table = new(StringComparer.Ordinal)
    {
        ["command"] = new(1.6, true, ["M3 4l3.2 3.2L3 10.4", "M8.4 11.4H13"]),
        ["setting"] = new(1.6, false, ["M2 4.5h12M2 11.5h12"], ["c 6 4.5 1.9", "c 10.5 11.5 1.9"]),
        ["url"] = new(1.5, true, ["c 8 8 6", "M2 8h12", "M8 2c1.9 2 2.9 4 2.9 6S9.9 12 8 14C6.1 12 5.1 10 5.1 8S6.1 4 8 2z"]),
        ["lock"] = new(1.5, true, ["r 3.5 7 9 6.5 1.5", "M5.5 7V5a2.5 2.5 0 0 1 5 0v2"]),
        ["sleep"] = new(1.5, true, ["M13 9.6A5.5 5.5 0 1 1 6.4 3a4.4 4.4 0 0 0 6.6 6.6z"]),
        ["hibernate"] = new(1.5, true, ["M12.5 9.8A5 5 0 1 1 6.2 3.5a4 4 0 0 0 6.3 6.3z", "M3 14.5h10"]),
        ["restart"] = new(1.5, true, ["M13 8a5 5 0 1 1-1.6-3.7", "M11.8 1.8v2.8H9"]),
        ["power"] = new(1.5, true, ["M8 2v5.5", "M4.6 4.2a5 5 0 1 0 6.8 0"]),
        ["signout"] = new(1.5, true, ["M6.5 2.5H4a1.5 1.5 0 0 0-1.5 1.5v8A1.5 1.5 0 0 0 4 13.5h2.5", "M10 5l3 3-3 3", "M13 8H6.5"]),
        ["calc"] = new(1.5, true, ["r 3 1.8 10 12.4 1.6", "M5.5 4.8h5", "M5.6 8.2h.01M8 8.2h.01M10.4 8.2h.01M5.6 11h.01M8 11h.01M10.4 11h.01"]),
        ["web"] = new(1.5, true, ["c 7 7 4.6", "M10.4 10.4L14 14"]),
        ["file"] = new(1.5, true, ["M9 1.8H4.5A1.5 1.5 0 0 0 3 3.3v9.4a1.5 1.5 0 0 0 1.5 1.5h7a1.5 1.5 0 0 0 1.5-1.5V5.8z", "M9 1.8v4h4"]),
        ["folder"] = new(1.5, true, ["M1.8 4.3A1.3 1.3 0 0 1 3.1 3h3l1.5 1.7h5.3a1.3 1.3 0 0 1 1.3 1.3v5.7A1.3 1.3 0 0 1 12.9 13H3.1a1.3 1.3 0 0 1-1.3-1.3z"]),
    };

    /// launcher.js iconCell: GLYPH[item.glyph] || GLYPH[item.kind]; null = the letter tile.
    public static string? NameFor(string? glyph, string kind) =>
        glyph != null && Table.ContainsKey(glyph) ? glyph : Table.ContainsKey(kind) ? kind : null;

    /// The glyph's shapes into box (cleared first): a 16x16 drawing in the given brush.
    public static void Fill(Grid box, string name, Brush ink, bool dark)
    {
        box.Children.Clear();
        var d = Table[name];
        var stroked = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var s in d.Stroked) stroked.Children.Add(Shape(s));
        box.Children.Add(new Path
        {
            Data = stroked, Stroke = ink, StrokeThickness = dark ? 1.6 : d.Stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = d.RoundJoin ? PenLineJoin.Round : PenLineJoin.Miter, Width = 16, Height = 16,
        });
        if (d.Filled != null)
        {
            var filled = new GeometryGroup();
            foreach (var s in d.Filled) filled.Children.Add(Shape(s));
            box.Children.Add(new Path { Data = filled, Fill = ink, Width = 16, Height = 16 });
        }
    }

    /// The footer's warning triangle (launcher.css .src-error::before mask: 12 px, stroke 1.6), in box (cleared first).
    public static void Warn(Grid box, Brush ink)
    {
        box.Children.Clear();
        var g = new GeometryGroup();
        g.Children.Add(PathData("M8 2.2l6.2 10.9H1.8z"));
        g.Children.Add(PathData("M8 6.4v3M8 11.2v.01"));
        var art = new Grid { Width = 16, Height = 16 };
        art.Children.Add(new Path { Data = g, Stroke = ink, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
        box.Children.Add(new Viewbox { Width = 12, Height = 12, Child = art });
    }

    static Geometry Shape(string s)
    {
        if (s.StartsWith("c ", StringComparison.Ordinal))
        {
            var n = Nums(s[2..]);
            return new EllipseGeometry { Center = new Point(n[0], n[1]), RadiusX = n[2], RadiusY = n[2] };
        }
        if (s.StartsWith("r ", StringComparison.Ordinal))
        {
            var n = Nums(s[2..]);
            if (n.Count > 4 && n[4] > 0) return RoundedRect(n[0], n[1], n[2], n[3], n[4]);
            return new RectangleGeometry { Rect = new Rect(n[0], n[1], n[2], n[3]) };
        }
        return PathData(s);
    }

    static List<double> Nums(string s)
    {
        var o = new List<double>();
        foreach (var p in s.Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Add(double.Parse(p, CultureInfo.InvariantCulture));
        return o;
    }

    /// A rounded rectangle as a path (RectangleGeometry has no corner radius in WinUI).
    static Geometry RoundedRect(double x, double y, double w, double h, double r) =>
        PathData(FormattableString.Invariant($"M{x + r} {y}H{x + w - r}A{r} {r} 0 0 1 {x + w} {y + r}V{y + h - r}A{r} {r} 0 0 1 {x + w - r} {y + h}H{x + r}A{r} {r} 0 0 1 {x} {y + h - r}V{y + r}A{r} {r} 0 0 1 {x + r} {y}z"));

    /// SVG path data (M L H V C S A Z, absolute and relative; the commands Electron's glyphs use) to a PathGeometry.
    internal static PathGeometry PathData(string d)
    {
        var g = new PathGeometry();
        PathFigure? fig = null;
        int i = 0;
        char cmd = ' ';
        double cx = 0, cy = 0, sx = 0, sy = 0, lc2x = 0, lc2y = 0;
        bool lastCubic = false;
        void Skip() { while (i < d.Length && (d[i] == ' ' || d[i] == ',')) i++; }
        bool AtNum() { Skip(); return i < d.Length && (char.IsDigit(d[i]) || d[i] == '-' || d[i] == '.' || d[i] == '+'); }
        double Num()
        {
            Skip();
            int st = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            bool dot = false;
            while (i < d.Length && (char.IsDigit(d[i]) || (d[i] == '.' && !dot)))
            {
                if (d[i] == '.') dot = true;
                i++;
            }
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E')) { i++; if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++; while (i < d.Length && char.IsDigit(d[i])) i++; }
            return double.Parse(d.AsSpan(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        double Flag() { Skip(); return d[i++] == '1' ? 1 : 0; }
        void Start(double x, double y)
        {
            fig = new PathFigure { StartPoint = new Point(x, y), IsClosed = false, IsFilled = true };
            g.Figures.Add(fig);
            cx = sx = x; cy = sy = y;
        }
        void Add(PathSegment s) { if (fig == null) Start(cx, cy); fig!.Segments.Add(s); }
        while (true)
        {
            Skip();
            if (i >= d.Length) break;
            if (char.IsLetter(d[i])) cmd = d[i++];
            bool rel = char.IsLower(cmd);
            double ox = rel ? cx : 0, oy = rel ? cy : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    Start(ox + Num(), oy + Num());
                    cmd = rel ? 'l' : 'L';                                    // more pairs after an M are lines
                    lastCubic = false;
                    break;
                case 'L': { double x = ox + Num(), y = oy + Num(); Add(new LineSegment { Point = new Point(x, y) }); cx = x; cy = y; lastCubic = false; break; }
                case 'H': { double x = ox + Num(); Add(new LineSegment { Point = new Point(x, cy) }); cx = x; lastCubic = false; break; }
                case 'V': { double y = oy + Num(); Add(new LineSegment { Point = new Point(cx, y) }); cy = y; lastCubic = false; break; }
                case 'C':
                {
                    double x1 = ox + Num(), y1 = oy + Num(), x2 = ox + Num(), y2 = oy + Num(), x = ox + Num(), y = oy + Num();
                    Add(new BezierSegment { Point1 = new Point(x1, y1), Point2 = new Point(x2, y2), Point3 = new Point(x, y) });
                    lc2x = x2; lc2y = y2; cx = x; cy = y; lastCubic = true;
                    break;
                }
                case 'S':
                {
                    double x1 = lastCubic ? 2 * cx - lc2x : cx, y1 = lastCubic ? 2 * cy - lc2y : cy;
                    double x2 = ox + Num(), y2 = oy + Num(), x = ox + Num(), y = oy + Num();
                    Add(new BezierSegment { Point1 = new Point(x1, y1), Point2 = new Point(x2, y2), Point3 = new Point(x, y) });
                    lc2x = x2; lc2y = y2; cx = x; cy = y; lastCubic = true;
                    break;
                }
                case 'A':
                {
                    double rx = Num(), ry = Num(), rot = Num(), large = Flag(), sweep = Flag(), x = ox + Num(), y = oy + Num();
                    Add(new ArcSegment
                    {
                        Point = new Point(x, y), Size = new Size(rx, ry), RotationAngle = rot, IsLargeArc = large != 0,
                        SweepDirection = sweep != 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                    });
                    cx = x; cy = y; lastCubic = false;
                    break;
                }
                case 'Z':
                    if (fig != null) fig.IsClosed = true;
                    cx = sx; cy = sy; lastCubic = false;
                    fig = null;
                    break;
                default:
                    throw new FormatException("glyph path: unknown command '" + cmd + "' in " + d);
            }
            if (char.ToUpperInvariant(cmd) != 'Z' && !AtNum() && i < d.Length && !char.IsLetter(d[i])) i++;   // stray separator
        }
        return g;
    }
}
