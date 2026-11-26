using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mantle.Core.Look;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Lane r15-bar-light: Theme/Levitate.xaml's theme-dependent LvBar* block (the bar's islands and the levitated bar pop-ups)
/// against the prototype's numbers. Light: apps-r3/LIGHT.md section 4 (text #0F1E21, text-2 / -3 / -4 at .76 / .56 / .36,
/// ok #124028, down #6F1813, fills black .05 / .08 / .12 pressed .04, hair black .08, the sheen white .16 / .12) and the
/// islands accent #083F49 / hover #05313A / ink #F4F7F8 (4 and 4.2). Dark: exactly what the islands showed before (the
/// LOOK.md tokens of the same file, LevitateTokens, or the Lg token a part kept) and the accent equal to Mantle.Core
/// Accent's "islands" profile. Parsed the way LevitateTokensTests parses the file.
/// </summary>
public class BarLightTokensTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string ThemeDir => Path.Combine(NativeDir(), "src", "Mantle.App", "Theme");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static readonly Lazy<XElement> Root = new(() => XDocument.Load(Path.Combine(ThemeDir, "Levitate.xaml")).Root!);
    static readonly Lazy<XElement> TokensRoot = new(() => XDocument.Load(Path.Combine(ThemeDir, "Tokens.xaml")).Root!);

    /// One theme's dictionary (x:Key Dark or Light) of a token file's ThemeDictionaries.
    static XElement Theme(XElement root, string theme) => root.Element(P + "ResourceDictionary.ThemeDictionaries")!
        .Elements(P + "ResourceDictionary").Single(e => e.Attribute(X + "Key")?.Value == theme);
    static string Bar(string theme, string key) => Theme(Root.Value, theme).Elements().Single(e => e.Attribute(X + "Key")?.Value == key).Attribute("Color")!.Value;
    static string Lg(string theme, string key) => Theme(TokensRoot.Value, theme).Elements().Single(e => e.Attribute(X + "Key")?.Value == key).Attribute("Color")!.Value;
    /// A top-level (theme-free) LOOK.md token of Levitate.xaml.
    static string Top(string key) => Root.Value.Elements().Single(e => e.Attribute(X + "Key")?.Value == key).Attribute("Color")!.Value;
    static string Argb(double a, byte r, byte g, byte b) => "#" + ((byte)Math.Round(a * 255)).ToString("X2") + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    static string Ink(double a) => Argb(a, 0x0F, 0x1E, 0x21);   // the light ink #0F1E21 at a
    static string Black(double a) => Argb(a, 0, 0, 0);
    static string White(double a) => Argb(a, 255, 255, 255);
    /// Core's "#rrggbb" as an opaque #FFRRGGBB.
    static string Opaque(string hex) => "#FF" + hex.TrimStart('#').ToUpperInvariant();

    static readonly string[] Keys = {
        "LvBarInk", "LvBarInk2", "LvBarInk3", "LvBarInk4", "LvBarWarn", "LvBarOk", "LvBarDown",
        "LvBarFillHover", "LvBarFillOn", "LvBarFillStrong", "LvBarFillPressed", "LvBarHair", "LvBarSheen", "LvBarSheenBig",
        "LvBarRun", "LvBarTile", "LvBarTileInk", "LvBarSunk", "LvBarAccent", "LvBarAccentHover", "LvBarAccentInk",
    };

    [Fact]
    public void Both_themes_have_the_same_keys_and_nothing_else()
    {
        foreach (var theme in new[] { "Dark", "Light" }) {
            var keys = Theme(Root.Value, theme).Elements(P + "SolidColorBrush").Select(e => e.Attribute(X + "Key")!.Value).OrderBy(k => k, StringComparer.Ordinal).ToArray();
            Assert.Equal(Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(), keys);
        }
    }

    [Fact]
    public void Light_text_is_0F1E21_and_its_steps_are_076_056_036()
    {
        Assert.Equal("#FF0F1E21", Bar("Light", "LvBarInk"));
        Assert.Equal(Ink(.76), Bar("Light", "LvBarInk2"));
        Assert.Equal(Ink(.56), Bar("Light", "LvBarInk3"));
        Assert.Equal(Ink(.36), Bar("Light", "LvBarInk4"));
        Assert.Equal(("#C20F1E21", "#8F0F1E21", "#5C0F1E21"), (Bar("Light", "LvBarInk2"), Bar("Light", "LvBarInk3"), Bar("Light", "LvBarInk4")));
        // the dock's running dot is text-3, a letter tile's letter the text ink
        Assert.Equal(Ink(.56), Bar("Light", "LvBarRun"));
        Assert.Equal("#FF0F1E21", Bar("Light", "LvBarTileInk"));
    }

    [Fact]
    public void Light_ok_down_and_warn()
    {
        Assert.Equal("#FF124028", Bar("Light", "LvBarOk"));
        Assert.Equal("#FF6F1813", Bar("Light", "LvBarDown"));
        Assert.Equal("#FF4C3300", Bar("Light", "LvBarWarn"));   // LIGHT.md 4.1
    }

    [Fact]
    public void Light_fills_are_black_005_008_012_pressed_004_and_hair_black_008()
    {
        Assert.Equal(Black(.05), Bar("Light", "LvBarFillHover"));
        Assert.Equal(Black(.08), Bar("Light", "LvBarFillOn"));
        Assert.Equal(Black(.12), Bar("Light", "LvBarFillStrong"));
        Assert.Equal(Black(.04), Bar("Light", "LvBarFillPressed"));
        Assert.Equal(Black(.08), Bar("Light", "LvBarHair"));
        Assert.Equal(("#0D000000", "#14000000", "#1F000000", "#0A000000"),
            (Bar("Light", "LvBarFillHover"), Bar("Light", "LvBarFillOn"), Bar("Light", "LvBarFillStrong"), Bar("Light", "LvBarFillPressed")));
        // .app-ic is fill-2, the "+N" at rest fill-1
        Assert.Equal(Black(.08), Bar("Light", "LvBarTile"));
        Assert.Equal(Black(.05), Bar("Light", "LvBarSunk"));
    }

    [Fact]
    public void Light_sheen_is_white_016_and_012_big()
    {
        Assert.Equal(White(.16), Bar("Light", "LvBarSheen"));
        Assert.Equal(White(.12), Bar("Light", "LvBarSheenBig"));
        Assert.Equal(("#29FFFFFF", "#1FFFFFFF"), (Bar("Light", "LvBarSheen"), Bar("Light", "LvBarSheenBig")));
    }

    [Fact]
    public void Light_accent_is_083F49_hover_05313A_ink_F4F7F8()
    {
        Assert.Equal("#FF083F49", Bar("Light", "LvBarAccent"));
        Assert.Equal("#FF05313A", Bar("Light", "LvBarAccentHover"));
        Assert.Equal("#FFF4F7F8", Bar("Light", "LvBarAccentInk"));
        // the ink on the accent is the light neutral the lift goes toward (LevitateTokens.LightFilm*)
        Assert.Equal(Argb(1, LevitateTokens.LightFilmR, LevitateTokens.LightFilmG, LevitateTokens.LightFilmB), Bar("Light", "LvBarAccentInk"));
    }

    [Fact]
    public void Dark_accent_is_Cores_islands_profile()
    {
        var t = Accent.Tokens(Accent.BaseHue(Accent.Islands, "dark"), "dark", Accent.Islands)!;
        Assert.Equal(185, Accent.BaseHue(Accent.Islands, "dark"));
        Assert.Equal("#FFA9E7ED", Opaque(t.Acc));
        Assert.Equal(Opaque(t.Acc), Bar("Dark", "LvBarAccent"));
        Assert.Equal(Opaque(t.Strong), Bar("Dark", "LvBarAccentHover"));
        Assert.Equal(Opaque(t.On), Bar("Dark", "LvBarAccentInk"));
        Assert.Equal(("#FFA9E7ED", "#FFC4F0F4", "#FF052A2F"), (Bar("Dark", "LvBarAccent"), Bar("Dark", "LvBarAccentHover"), Bar("Dark", "LvBarAccentInk")));
        // and the LOOK.md accent of the same file
        Assert.Equal(Top("LvAccent"), Bar("Dark", "LvBarAccent"));
        Assert.Equal(Top("LvAccentInk"), Bar("Dark", "LvBarAccentInk"));
    }

    [Fact]
    public void Dark_is_unchanged_the_LOOK_md_tokens_and_the_Lg_ones_a_part_kept()
    {
        // the inks and fills the islands drew from LOOK.md's tokens before lane r15 (and r14's LvBar* dark values)
        var same = new Dictionary<string, string> {
            ["LvBarInk"] = "LvText", ["LvBarInk2"] = "LvText2", ["LvBarInk3"] = "LvText3", ["LvBarInk4"] = "LvText4",
            ["LvBarOk"] = "LvOk", ["LvBarDown"] = "LvDown", ["LvBarFillHover"] = "LvCtlFillHover", ["LvBarFillOn"] = "LvCtlFillOn",
            ["LvBarFillStrong"] = "LvCtlFillStrong", ["LvBarFillPressed"] = "LvCtlFillPressed", ["LvBarHair"] = "LvHair",
            ["LvBarSheen"] = "LvSheenHover", ["LvBarSheenBig"] = "LvSheenHoverBig",
        };
        foreach (var (bar, lv) in same) Assert.Equal(Top(lv), Bar("Dark", bar));
        Assert.Equal(("#FFEEF6F7", "#C2E6F2F4", "#FFE3C47E"), (Bar("Dark", "LvBarInk"), Bar("Dark", "LvBarInk2"), Bar("Dark", "LvBarWarn")));
        // the sheen in dark is LevitateTokens' (LevitatePanel draws dark's from the C# mirror)
        Assert.Equal(White(LevitateTokens.SheenHover), Bar("Dark", "LvBarSheen"));
        Assert.Equal(White(LevitateTokens.SheenHoverBig), Bar("Dark", "LvBarSheenBig"));
        // the parts that kept an Lg token in dark keep its colour: the run dot LgInk2, the letter tile LgTile and its
        // letter LgInk, "+N" LgSunk; the dock's separator was LgEdgeOut, which is the hair
        Assert.Equal(Lg("Dark", "LgInk2"), Bar("Dark", "LvBarRun"));
        Assert.Equal(Lg("Dark", "LgTile"), Bar("Dark", "LvBarTile"));
        Assert.Equal(Lg("Dark", "LgInk"), Bar("Dark", "LvBarTileInk"));
        Assert.Equal(Lg("Dark", "LgSunk"), Bar("Dark", "LvBarSunk"));
        Assert.Equal(Lg("Dark", "LgEdgeOut"), Bar("Dark", "LvBarHair"));
    }

    [Fact]
    public void The_top_level_LOOK_md_tokens_stay_dark()
    {
        // the LOOK.md block is theme-free and dark (LevitateTokensTests); light lives only in the LvBar* block
        Assert.Equal("#FFEEF6F7", Top("LvText"));
        Assert.Equal(White(.07), Top("LvCtlFillHover"));
        Assert.Equal(White(.07), Top("LvSheenHover"));
        Assert.DoesNotContain(Root.Value.Elements(), e => (e.Attribute(X + "Key")?.Value ?? "").StartsWith("LvBar", StringComparison.Ordinal));
    }
}
