using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App.Surfaces.Bar.Media;

/// <summary>
/// The now-playing pictures and glyphs (bar.js MEDIA_SVG, paintArt): artwork and app icons arrive as data URLs from the
/// sidecar; a broken one falls back to the note glyph, as an &lt;img&gt;'s onerror does. UI thread only.
/// </summary>
internal static class MediaPics
{
    /// bar.js MEDIA_SVG bodies (16x16 view box), filled with the button's ink.
    public static readonly Dictionary<string, string> Svg = new(StringComparer.Ordinal) {
        ["play"] = "M5 3.2v9.6a.6.6 0 0 0 .92.5l7.3-4.8a.6.6 0 0 0 0-1l-7.3-4.8A.6.6 0 0 0 5 3.2z",
        ["pause"] = "M4.7 3h1.4a.7.7 0 0 1 .7.7v8.6a.7.7 0 0 1-.7.7H4.7a.7.7 0 0 1-.7-.7V3.7a.7.7 0 0 1 .7-.7zM9.9 3h1.4a.7.7 0 0 1 .7.7v8.6a.7.7 0 0 1-.7.7H9.9a.7.7 0 0 1-.7-.7V3.7a.7.7 0 0 1 .7-.7z",
        ["next"] = "M3 3.6v8.8a.55.55 0 0 0 .85.46L10 8.46a.55.55 0 0 0 0-.92L3.85 3.14A.55.55 0 0 0 3 3.6zM11.8 3h.6a.6.6 0 0 1 .6.6v8.8a.6.6 0 0 1-.6.6h-.6a.6.6 0 0 1-.6-.6V3.6a.6.6 0 0 1 .6-.6z",
        ["previous"] = "M13 3.6v8.8a.55.55 0 0 1-.85.46L6 8.46a.55.55 0 0 1 0-.92l6.15-4.4A.55.55 0 0 1 13 3.6zM3.6 3h.6a.6.6 0 0 1 .6.6v8.8a.6.6 0 0 1-.6.6h-.6a.6.6 0 0 1-.6-.6V3.6a.6.6 0 0 1 .6-.6z",
    };

    /// SVG path data spelled for the XAML parser: "a.6.6 0 0 1-.7.7" becomes "a .6 .6 0 0 1 -.7 .7" (SVG lets a sign or
    /// a second dot start the next number; the XAML parser is not promised to).
    static string Norm(string d)
    {
        var o = new System.Text.StringBuilder(d.Length + 24);
        bool inNum = false, dot = false;
        foreach (char ch in d) {
            if (char.IsDigit(ch)) { o.Append(ch); inNum = true; continue; }
            if (ch == '.') { if (inNum && dot) { o.Append(' '); dot = false; } o.Append(ch); inNum = true; dot = true; continue; }
            if (ch == '-') { o.Append(' ').Append(ch); inNum = true; dot = false; continue; }
            o.Append(' ').Append(ch).Append(' ');
            inNum = false; dot = false;
        }
        return o.ToString();
    }
    static Path P(string d) => (Path)XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{Norm(d)}'/>");

    /// A glyph at <paramref name="size"/> DIP in <paramref name="ink"/>.
    public static FrameworkElement Glyph(string key, double size, Brush ink)
    {
        if (key == "note") return Note(size, ink);
        var p = P(Svg[key]); p.Fill = ink;
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 16, Height = 16, Children = { p } } };
    }

    /// The note glyph (a stroked stem and two filled heads).
    static FrameworkElement Note(double size, Brush ink)
    {
        var c = new Canvas { Width = 16, Height = 16 };
        var stem = P("M6 11.6V3.9l6.4-1.4v7.3"); stem.Stroke = ink; stem.StrokeThickness = 1.2; stem.StrokeLineJoin = PenLineJoin.Round; c.Children.Add(stem);
        var a = new Ellipse { Width = 3.2, Height = 3.2, Fill = ink }; Canvas.SetLeft(a, 3); Canvas.SetTop(a, 10.2); c.Children.Add(a);
        var b = new Ellipse { Width = 3.2, Height = 3.2, Fill = ink }; Canvas.SetLeft(b, 9.4); Canvas.SetTop(b, 8.6); c.Children.Add(b);
        return new Viewbox { Width = size, Height = size, Child = c };
    }

    /// A data: URL (or a file path / http URL) as an image source; null when it cannot be read.
    public static async Task<ImageSource?> Load(string? src)
    {
        if (string.IsNullOrEmpty(src)) return null;
        try {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
                int comma = src.IndexOf(',');
                if (comma < 0 || !src[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                byte[] bytes = Convert.FromBase64String(src[(comma + 1)..]);
                using var ms = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var w = new global::Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0))) {
                    w.WriteBytes(bytes);
                    await w.StoreAsync();
                    await w.FlushAsync();
                    w.DetachStream();
                }
                ms.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms);
                return bmp;
            }
            return new BitmapImage(new Uri(src));
        } catch (Exception) {
            return null;   // the caller draws the note glyph or the app's letter, as an <img> onerror does
        }
    }
}
