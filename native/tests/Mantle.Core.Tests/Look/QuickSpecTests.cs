using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Lane r15-quick-settings: Theme/LvQuick.xaml's quick-settings numbers and inks against the approved prototype
/// (apps-r3/mantle-bar panels/quick.css and quick.js, look.css; apps-r3/light-mantle/light.css; LIGHT.md). Plan v0.2's
/// "prototype-spec numeric checks in unit": the numbers, never a pixel compare. Parsed the way BarLightTokensTests parses
/// Levitate.xaml (Mantle.Core.Tests cannot reference Mantle.App).
/// </summary>
public class QuickSpecTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string ThemeDir => Path.Combine(NativeDir(), "src", "Mantle.App", "Theme");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static readonly Lazy<XElement> Root = new(() => XDocument.Load(Path.Combine(ThemeDir, "LvQuick.xaml")).Root!);
    static readonly Lazy<string> Raw = new(() => File.ReadAllText(Path.Combine(ThemeDir, "LvQuick.xaml")));

    static XElement Theme(string theme) => Root.Value.Element(P + "ResourceDictionary.ThemeDictionaries")!
        .Elements(P + "ResourceDictionary").Single(e => e.Attribute(X + "Key")?.Value == theme);
    static string Brush(string theme, string key) => Theme(theme).Elements().Single(e => e.Attribute(X + "Key")?.Value == key).Attribute("Color")!.Value;
    static XElement Top(string key) => Root.Value.Elements().Single(e => e.Attribute(X + "Key")?.Value == key);
    static double Num(string key) { var e = Top(key); Assert.Equal(X + "Double", e.Name); return double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture); }
    static string Thick(string key) { var e = Top(key); Assert.Equal(P + "Thickness", e.Name); return e.Value.Trim(); }
    static string Argb(double a, byte r, byte g, byte b) => "#" + ((byte)Math.Round(a * 255)).ToString("X2") + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");

    [Fact]
    public void The_file_has_no_double_hyphen_in_a_comment()
    {
        // an XML comment may not hold two hyphens in a row (it broke the build once)
        foreach (var c in Root.Value.Document!.DescendantNodes().OfType<XComment>()) Assert.DoesNotContain("--", c.Value);
        Assert.Equal(2, Raw.Value.Split("--").Length - 1);   // only the open and close marks of the one head comment
    }

    [Fact]
    public void Panel_and_grid_numbers_are_quick_css()
    {
        Assert.Equal(360, Num("LvQsWidth"));        // quick.css 5: .qs width 360
        Assert.Equal(8, Num("LvQsPad"));            // quick.css 12: .qs-in padding 8
        Assert.Equal(12, Num("LvQsGap"));           // quick.css 12: .qs-in gap 12
        Assert.Equal(8, Num("LvQsTileGap"));        // quick.css 15: .qs-grid gap 8
        Assert.Equal(48, Num("LvQsTileH"));         // quick.css 17: .qs-tile height 48
        Assert.Equal("12,0,8,0", Thick("LvQsTilePad"));   // quick.css 17: padding 0 8 0 12
        Assert.Equal(20, Num("LvQsTileIco"));       // quick.css 16: grid-template-columns 20px
        Assert.Equal(8, Num("LvQsTileIcoGap"));     // quick.css 16: column-gap 8
        Assert.Equal(13, Num("LvQsLblFs"));         // quick.css 23: .qs-lbl --fs-s (look.css 12: 13)
        Assert.Equal(18, Num("LvQsLblLh"));         // quick.css 23: line-height 18
        Assert.Equal(11, Num("LvQsSubFs"));         // quick.css 24: .qs-sub --fs-2xs (11)
        Assert.Equal(15, Num("LvQsSubLh"));         // quick.css 24: line-height 15
        Assert.Equal(24, Num("LvQsChevW"));         // quick.css 29: .qs-split columns 1fr 24px
        Assert.Equal(2, Num("LvQsSplitGap"));       // quick.css 29: gap 2
    }

    [Fact]
    public void Row_slider_and_seg_numbers_are_quick_css()
    {
        Assert.Equal(4, Num("LvQsRowGap"));         // quick.css 39: .qs-row gap 4
        Assert.Equal("4,0,4,0", Thick("LvQsHeadPad"));   // quick.css 40: .qs-head padding 0 4
        Assert.Equal(13, Num("LvQsHeadFs"));        // quick.css 40: --fs-s
        Assert.Equal(12, Num("LvQsValFs"));         // quick.css 41: .qs-val --fs-xs
        Assert.Equal(28, Num("LvQsLineH"));         // quick.css 42: .qs-line height 28
        Assert.Equal(4, Num("LvQsLineGap"));        // quick.css 42: gap 4
        Assert.Equal(28, Num("LvQsIBtn"));          // quick.css 44: .qs-ibtn 28 x 28
        Assert.Equal(4, Num("LvQsTrackH"));         // quick.css 51: the track 4 tall
        Assert.Equal(16, Num("LvQsThumb"));         // quick.css 53: the thumb 16 x 16
        Assert.Equal(32, Num("LvQsSegH"));          // quick.css 64: .qs-seg height 32
        Assert.Equal(2, Num("LvQsSegPad"));         // quick.css 64: padding 2
        Assert.Equal(28, Num("LvQsSegBtnH"));       // quick.css 65: .qs-segb height 28
    }

    [Fact]
    public void More_row_header_and_footer_numbers_are_quick_css()
    {
        Assert.Equal(40, Num("LvQsGoRowH"));        // quick.css 119: .qs-gorow height 40
        Assert.Equal("12,0,8,0", Thick("LvQsGoRowPad"));  // quick.css 119: padding 0 8 0 12
        Assert.Equal(12, Num("LvQsGoRowGap"));      // quick.css 119: gap 12
        Assert.Equal(40, Num("LvQsHeadH"));         // quick.css 127: .qs-shead height 40
        Assert.Equal(8, Num("LvQsHeadPadX"));       // quick.css 127: padding 0 8
        Assert.Equal(32, Num("LvQsBackSize"));      // quick.css 128: .qs-shead .qs-ibtn 32 x 32
        Assert.Equal(14, Num("LvQsTitleFs"));       // quick.css 129: .qs-stitle --fs-m (14)
        Assert.Equal(40, Num("LvQsFootH"));         // quick.css 111: .qs-foot height 40
        Assert.Equal("16,0,8,0", Thick("LvQsFootPad"));   // quick.css 111: padding 0 8 0 16
        Assert.Equal(4, Num("LvQsFootGap"));        // quick.css 111: gap 4
        Assert.Equal(12, Num("LvQsFootFs"));        // quick.css 112: .qs-power --fs-xs
    }

    [Fact]
    public void Size_rule_faint_arc_and_radii_are_the_prototype()
    {
        Assert.Equal(240, Num("LvQsMinH"));         // quick.js fit(): Math.max(240, ...)
        Assert.Equal(80, Num("LvQsScreenGap"));     // quick.js fit(): innerHeight - 80
        Assert.Equal(0.32, Num("LvQsFaint"));       // quick.js wifiG: unlit arcs at opacity .32
        Assert.Equal(8, Num("LvQsRCtl"));           // look.css 36: --r-m 8 (tiles, rows, the seg)
        Assert.Equal(4, Num("LvQsRSmall"));         // look.css 36: --r-xs 4 (quick.css 30: the split's inner corners)
        Assert.Equal(6, Num("LvQsRSeg"));           // look.css 36: --r-s 6 (quick.css 65: a seg button)
        Assert.Equal(999, Num("LvQsRPill"));        // look.css 106: pills 999
        // LOOK.md: radii for controls are 4 / 6 / 8 only
        foreach (var k in new[] { "LvQsRCtl", "LvQsRSmall", "LvQsRSeg" }) Assert.Contains(Num(k), new[] { 4.0, 6.0, 8.0 });
        // LOOK.md: spacing 4 / 8 / 12 / 16 / 24 / 32 (a split's 2 px gap is quick.css 31's own)
        foreach (var k in new[] { "LvQsPad", "LvQsGap", "LvQsTileGap", "LvQsRowGap", "LvQsLineGap", "LvQsGoRowGap", "LvQsHeadPadX", "LvQsFootGap" })
            Assert.Contains(Num(k), new[] { 4.0, 8.0, 12.0, 16.0, 24.0, 32.0 });
        // LOOK.md: type 11 / 12 / 13 / 14 / 16 / 20 / 28
        foreach (var k in new[] { "LvQsLblFs", "LvQsSubFs", "LvQsHeadFs", "LvQsValFs", "LvQsTitleFs", "LvQsFootFs" })
            Assert.Contains(Num(k), new[] { 11.0, 12.0, 13.0, 14.0, 16.0, 20.0, 28.0 });
    }

    [Fact]
    public void Theme_inks_are_look_css_and_light_css()
    {
        foreach (var theme in new[] { "Dark", "Light" }) {
            var keys = Theme(theme).Elements(P + "SolidColorBrush").Select(e => e.Attribute(X + "Key")!.Value).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "LvQsHoverOver", "LvQsOnSubInk" }, keys);
        }
        Assert.Equal(Argb(0.72, 5, 42, 47), Brush("Dark", "LvQsOnSubInk"));       // look.css 21: rgba(5, 42, 47, 0.72)
        Assert.Equal(Argb(0.78, 244, 247, 248), Brush("Light", "LvQsOnSubInk"));  // light.css 16: rgba(244, 247, 248, 0.78)
        // the hover overlay turns fill-1 into fill-2: dark white .07 to .12 (look.css 28, 29), light black .05 to .08
        // (light.css 24, 25), within one step of the alpha byte
        Assert.InRange(Over(0.07, Brush("Dark", "LvQsHoverOver")), 0.12 - 1.0 / 255, 0.12 + 1.0 / 255);
        Assert.InRange(Over(0.05, Brush("Light", "LvQsHoverOver")), 0.08 - 1.0 / 255, 0.08 + 1.0 / 255);
        Assert.Equal("FFFFFF", Brush("Dark", "LvQsHoverOver")[3..]);
        Assert.Equal("000000", Brush("Light", "LvQsHoverOver")[3..]);
    }

    /// The alpha of one same-colour layer over another (Porter-Duff over).
    static double Over(double under, string argb) { double a = int.Parse(argb.AsSpan(1, 2), NumberStyles.HexNumber) / 255.0; return a + under * (1 - a); }
}
