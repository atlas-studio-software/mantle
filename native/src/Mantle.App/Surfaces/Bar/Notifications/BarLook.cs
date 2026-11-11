// BarLook.cs - the small kit the bar's right-cluster pieces (the notification centre, the tray strip, the More menu;
// lane native-r7-notif) draw with: theme tokens read in code (so a repaint follows the bar's theme), the flat button
// face of .qslink / .x / .ti, a TextBlock in the bar's font, the UIA id and name, and a data: picture decoded once.
// The quick-settings panel has its own copy of the token reader (Surfaces/Bar/QuickSettings); this one is the lane's.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Surfaces.Bar.Notifications;

internal static class BarLook
{
    public const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
    static readonly HashSet<string> Missing = new();

    /// The --lg-* token for a theme (Theme/Tokens.xaml); a missing one is said once and drawn clear.
    public static Brush B(bool dark, string key, Action<string>? log = null)
    {
        string theme = dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        if (Missing.Add(key)) log?.Invoke($"bar look: the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }

    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }

    /// A token at a fraction of its own opacity.
    public static Brush Alpha(Brush b, double a) => b is SolidColorBrush s ? new SolidColorBrush(s.Color) { Opacity = s.Opacity * a } : b;

    static FontFamily? _font;
    public static FontFamily Font => _font ??= new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

    public static TextBlock Text(string s, double size, Brush ink, bool semibold = false) => new() {
        Text = s, FontSize = size, Foreground = ink, FontFamily = Font, FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// A CSS line box: the line exactly fs * lh tall.
    public static void Line(TextBlock t, double fs, double lh)
    {
        t.FontSize = fs;
        t.LineHeight = Math.Round(fs * lh, MidpointRounding.AwayFromZero);
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    public static void Uia(FrameworkElement e, string id, string name)
    {
        AutomationProperties.SetAutomationId(e, id);
        AutomationProperties.SetName(e, name);
    }

    static readonly Dictionary<string, Style> Styles = new();
    /// A flat face in the element's own Background with a hover and a press layer over it (.qslink, .x, .ti, .item).
    /// Hover / press are theme tokens unless given (the .x hover is --lg-warn-soft).
    public static Style Flat(string hover = "LgHover", string press = "LgPress")
    {
        string k = hover + "|" + press;
        if (Styles.TryGetValue(k, out var s)) return s;
        return Styles[k] = (Style)XamlReader.Load(
            $"<Style {Ns} TargetType='Button'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/>" +
            "<Setter Property='MinWidth' Value='0'/><Setter Property='MinHeight' Value='0'/><Setter Property='CornerRadius' Value='{ThemeResource LgRCtlCorner}'/>" +
            "<Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='{ThemeResource LgInk2}'/>" +
            "<Setter Property='IsTabStop' Value='False'/><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
            "<Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='VerticalContentAlignment' Value='Center'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
            "<Grid x:Name='Face' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='{TemplateBinding CornerRadius}'>" +
            "<Border x:Name='Over' CornerRadius='{TemplateBinding CornerRadius}'/>" +
            "<ContentPresenter Padding='{TemplateBinding Padding}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}' Foreground='{TemplateBinding Foreground}'/>" +
            "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'><VisualState x:Name='Normal'/>" +
            $"<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Over.Background' Value='{{ThemeResource {hover}}}'/></VisualState.Setters></VisualState>" +
            $"<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Over.Background' Value='{{ThemeResource {press}}}'/></VisualState.Setters></VisualState>" +
            "<VisualState x:Name='Disabled'/></VisualStateGroup></VisualStateManager.VisualStateGroups>" +
            "</Grid></ControlTemplate></Setter.Value></Setter></Style>");
    }

    /// SVG path data in the XAML path syntax's spelling ("2-.3.5" is "2 -.3 .5").
    public static string Norm(string d)
    {
        var o = new System.Text.StringBuilder(d.Length + 16);
        bool inNum = false, dot = false;
        foreach (char ch in d) {
            if (char.IsDigit(ch)) { inNum = true; o.Append(ch); continue; }
            if (ch == '.') { if (inNum && dot) o.Append(' '); inNum = true; dot = true; o.Append(ch); continue; }
            if (ch == '-') { if (inNum) o.Append(' '); inNum = true; dot = false; o.Append(ch); continue; }
            if (char.IsLetter(ch)) o.Append(' ').Append(ch).Append(' ');
            else o.Append(' ');
            inNum = false; dot = false;
        }
        return System.Text.RegularExpressions.Regex.Replace(o.ToString(), " +", " ").Trim();
    }

    /// A 16-unit line glyph (stroke 1.3, round caps) in one brush, as a UIElement (the AOT build cannot cast
    /// XamlReader's result to Shapes.Path, so the stroke is set through the markup's own resource).
    public static Canvas Glyph(string[] paths, double size, double stroke, string inkKey)
    {
        var c = new Canvas { Width = 16, Height = 16 };
        foreach (var d in paths)
            c.Children.Add((UIElement)XamlReader.Load(FormattableString.Invariant(
                $"<Path {Ns} Data='{Norm(d)}' Stroke='{{ThemeResource {inkKey}}}' StrokeThickness='{stroke}' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>")));
        return size == 16 ? c : new Canvas { Width = size, Height = size, Children = { new Viewbox { Width = size, Height = size, Child = c } } };
    }

    static readonly Dictionary<string, ImageSource?> Pics = new(StringComparer.Ordinal);
    /// A data: picture (the sidecar's and the mock's shape), decoded once and kept (64 at most); null when it is not a
    /// data: URL or does not decode (the caller draws its letter instead and the log says why, once).
    public static async Task<ImageSource?> Pic(string? url, Action<string> log)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("data:image/", StringComparison.Ordinal)) return null;
        if (Pics.TryGetValue(url, out var known)) return known;
        ImageSource? img = null;
        try {
            int comma = url.IndexOf(',');
            byte[] bytes = Convert.FromBase64String(comma >= 0 ? url[(comma + 1)..] : url);
            using var ms = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = new global::Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(bytes); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream(); }
            ms.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms);
            img = bmp;
        } catch (Exception e) {
            log($"bar look: a picture did not decode ({e.GetType().Name}: {e.Message}); its letter shows instead");
        }
        if (Pics.Count > 64) Pics.Clear();
        Pics[url] = img;
        return img;
    }

    /// Straight-alpha BGRA pixels of a data: picture (for the tray's tone), or the reason they could not be read.
    public static async Task<(byte[]? Px, string? Why)> Pixels(string url)
    {
        try {
            int comma = url.IndexOf(',');
            byte[] bytes = Convert.FromBase64String(comma >= 0 ? url[(comma + 1)..] : url);
            using var ms = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = new global::Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(bytes); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream(); }
            ms.Seek(0);
            var dec = await global::Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms);
            var data = await dec.GetPixelDataAsync(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
                new global::Windows.Graphics.Imaging.BitmapTransform(), global::Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                global::Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            return (data.DetachPixelData(), null);
        } catch (Exception e) {
            return (null, e.GetType().Name + ": " + e.Message);
        }
    }
}
