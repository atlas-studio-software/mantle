using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Theme/Levitate.xaml (the levitated look's tokens, lane r10-newlook, LOOK.md numbers) against its C# mirror, Mantle.Core
/// LevitateTokens, so the XAML a surface binds to and the numbers the pure code computes with never drift; the LOOK.md
/// values themselves; and Theme.xaml merges it.
/// </summary>
public class LevitateTokensTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string ThemeDir => Path.Combine(NativeDir(), "src", "Mantle.App", "Theme");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static readonly Lazy<XElement> Root = new(() => XDocument.Load(Path.Combine(ThemeDir, "Levitate.xaml")).Root!);
    static XElement Key(string k) => Root.Value.Elements().Single(e => e.Attribute(X + "Key")?.Value == k);
    static bool Has(string k) => Root.Value.Elements().Any(e => e.Attribute(X + "Key")?.Value == k);
    static double D(string k) => double.Parse(Key(k).Value.Trim(), CultureInfo.InvariantCulture);
    static string Color(string k) => Key(k).Attribute("Color")!.Value;
    static string Argb(double a, byte r, byte g, byte b) => "#" + ((byte)Math.Round(a * 255)).ToString("X2") + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    static string Argb(Ink i) => Argb(i.A, i.R, i.G, i.B);

    /// A number in the XAML: the x:Double equals the mirror and, when there is one, its Thickness / CornerRadius twin too.
    static void Num(string key, double mirror, string? twin = null)
    {
        Assert.Equal(mirror, D(key));
        if (twin != null) Assert.Equal(mirror.ToString(CultureInfo.InvariantCulture), Key(twin).Value.Trim());
    }

    [Fact]
    public void Gaps_are_12_to_the_edge_and_between_big_panels_6_in_a_row()
    {
        Assert.Equal((12.0, 6.0, 12.0), (LevitateTokens.Gap, LevitateTokens.GapRow, LevitateTokens.ContentInset));
        Num("LvGap", LevitateTokens.Gap, "LvGapThickness");
        Num("LvGapRow", LevitateTokens.GapRow);
        Num("LvContentInset", LevitateTokens.ContentInset, "LvContentInsetThickness");
    }

    [Fact]
    public void Radii_are_LOOK_mds_and_r_m_10_is_gone()
    {
        Assert.Equal((4.0, 6.0, 8.0), (LevitateTokens.RadiusCtlS, LevitateTokens.RadiusCtlM, LevitateTokens.RadiusCtlL));
        Assert.Equal((12.0, 16.0, 14.0, 14.0, 20.0, 999.0), (LevitateTokens.IslandRadius, LevitateTokens.CardRadius, LevitateTokens.PopoverRadius, LevitateTokens.PageRadius, LevitateTokens.ChartRadius, LevitateTokens.PillRadius));
        Num("LvRadiusCtlS", LevitateTokens.RadiusCtlS, "LvRadiusCtlSCorner");
        Num("LvRadiusCtlM", LevitateTokens.RadiusCtlM, "LvRadiusCtlMCorner");
        Num("LvRadiusCtlL", LevitateTokens.RadiusCtlL, "LvRadiusCtlLCorner");
        Num("LvIslandR", LevitateTokens.IslandRadius, "LvIslandRCorner");
        Num("LvCardR", LevitateTokens.CardRadius, "LvCardRCorner");
        Num("LvPopoverR", LevitateTokens.PopoverRadius, "LvPopoverRCorner");
        Num("LvPageR", LevitateTokens.PageRadius, "LvPageRCorner");
        Num("LvChartR", LevitateTokens.ChartRadius, "LvChartRCorner");
        Num("LvPillR", LevitateTokens.PillRadius, "LvPillRCorner");
        Assert.False(Has("LvRM"));
        Assert.False(Has("LvRMCorner"));
    }

    [Fact]
    public void A_nested_shape_is_the_outer_radius_less_the_gap()
    {
        Assert.Equal(10, LevitateTokens.Nested(16, 6));
        Assert.Equal(4, LevitateTokens.Nested(LevitateTokens.CardRadius, LevitateTokens.Gap));
        Assert.Equal(0, LevitateTokens.Nested(4, 6));   // never below 0
    }

    [Fact]
    public void Spacing_rows_and_control_sizes()
    {
        Assert.Equal(new double[] { 4, 8, 12, 16, 24, 32 }, LevitateTokens.Spacing);
        foreach (var s in LevitateTokens.Spacing) Num("LvSpace" + s.ToString(CultureInfo.InvariantCulture), s);
        Num("LvCardPadding", LevitateTokens.CardPadding, "LvCardPaddingThickness");
        Assert.Equal(16, LevitateTokens.CardPadding);
        Assert.Equal((32.0, 36.0, 40.0), (LevitateTokens.RowList, LevitateTokens.RowMenu, LevitateTokens.RowTray));
        Num("LvRowList", LevitateTokens.RowList);
        Num("LvRowMenu", LevitateTokens.RowMenu);
        Num("LvRowTray", LevitateTokens.RowTray);
        Assert.Equal((32.0, 28.0), (LevitateTokens.ToolbarButton, LevitateTokens.ToolbarButtonSmall));
        Assert.Equal((36.0, 28.0, 44.0), (LevitateTokens.RoundControl, LevitateTokens.RoundControlSmall, LevitateTokens.RoundControlMain));
        Assert.Equal(28, LevitateTokens.PillHeight);
        Num("LvToolbarButton", LevitateTokens.ToolbarButton);
        Num("LvToolbarButtonSmall", LevitateTokens.ToolbarButtonSmall);
        Num("LvRoundControl", LevitateTokens.RoundControl);
        Num("LvRoundControlSmall", LevitateTokens.RoundControlSmall);
        Num("LvRoundControlMain", LevitateTokens.RoundControlMain);
        Num("LvPillHeight", LevitateTokens.PillHeight);
    }

    [Fact]
    public void Colours_are_one_neutral_family_and_one_accent()
    {
        Assert.Equal("#FFEEF6F7", Argb(LevitateTokens.Text));
        Assert.Equal(new Ink(230, 242, 244, .76), LevitateTokens.Text2);
        Assert.Equal(new Ink(230, 242, 244, .54), LevitateTokens.Text3);
        Assert.Equal(new Ink(230, 242, 244, .34), LevitateTokens.Text4);
        Assert.Equal("#FFA9E7ED", Argb(LevitateTokens.Accent));
        Assert.Equal("#FF052A2F", Argb(LevitateTokens.AccentInk));
        Assert.Equal("#FF8FE3B4", Argb(LevitateTokens.Ok));
        Assert.Equal("#FFFF9D95", Argb(LevitateTokens.Down));
        Assert.Equal(0.09, LevitateTokens.Hair);
        Assert.Equal(Argb(LevitateTokens.Text), Color("LvText"));
        Assert.Equal(Argb(LevitateTokens.Text2), Color("LvText2"));
        Assert.Equal(Argb(LevitateTokens.Text3), Color("LvText3"));
        Assert.Equal(Argb(LevitateTokens.Text4), Color("LvText4"));
        Assert.Equal(Argb(LevitateTokens.Accent), Color("LvAccent"));
        Assert.Equal(Argb(LevitateTokens.AccentInk), Color("LvAccentInk"));
        Assert.Equal(Argb(LevitateTokens.Ok), Color("LvOk"));
        Assert.Equal(Argb(LevitateTokens.Down), Color("LvDown"));
        Assert.Equal(Argb(LevitateTokens.Hair, 255, 255, 255), Color("LvHair"));
        Assert.Equal("#C2E6F2F4", Color("LvText2"));   // .76 * 255 = 193.8 -> 194
    }

    [Fact]
    public void Control_fills_are_white_at_000_007_012_018_pressed_005()
    {
        Assert.Equal((0.00, 0.07, 0.12, 0.18, 0.05), (LevitateTokens.CtlFillRest, LevitateTokens.CtlFillHover, LevitateTokens.CtlFillOn, LevitateTokens.CtlFillStrong, LevitateTokens.CtlFillPressed));
        Assert.Equal(Argb(LevitateTokens.CtlFillRest, 255, 255, 255), Color("LvCtlFillRest"));
        Assert.Equal(Argb(LevitateTokens.CtlFillHover, 255, 255, 255), Color("LvCtlFillHover"));
        Assert.Equal(Argb(LevitateTokens.CtlFillOn, 255, 255, 255), Color("LvCtlFillOn"));
        Assert.Equal(Argb(LevitateTokens.CtlFillStrong, 255, 255, 255), Color("LvCtlFillStrong"));
        Assert.Equal(Argb(LevitateTokens.CtlFillPressed, 255, 255, 255), Color("LvCtlFillPressed"));
    }

    [Fact]
    public void The_white_sheen_is_007_on_hover_005_on_big_panels_and_none_at_rest()
    {
        Assert.Equal((0.07, 0.05), (LevitateTokens.SheenHover, LevitateTokens.SheenHoverBig));
        Assert.Equal(Argb(LevitateTokens.SheenHover, 255, 255, 255), Color("LvSheenHover"));
        Assert.Equal(Argb(LevitateTokens.SheenHoverBig, 255, 255, 255), Color("LvSheenHoverBig"));
        Assert.False(Has("LvSheen"));        // no resting sheen token
        Assert.False(Has("LvIslandEdge"));   // no island edge (hairline, ring or top highlight)
    }

    [Fact]
    public void Focus_is_one_2px_accent_ring_offset_2()
    {
        Assert.Equal((2.0, 2.0), (LevitateTokens.FocusRingWidth, LevitateTokens.FocusRingOffset));
        Num("LvFocusRingWidth", LevitateTokens.FocusRingWidth, "LvFocusRingThickness");
        Num("LvFocusRingOffset", LevitateTokens.FocusRingOffset);
    }

    [Fact]
    public void Type_is_Segoe_UI_Variable_Text_and_Display()
    {
        Assert.Equal("Segoe UI Variable Text", LevitateTokens.FontText);
        Assert.Equal("Segoe UI Variable Display", LevitateTokens.FontDisplay);
        Assert.StartsWith(LevitateTokens.FontText + ",", Key("LvFontText").Value.Trim());
        Assert.StartsWith(LevitateTokens.FontDisplay + ",", Key("LvFontDisplay").Value.Trim());
        Assert.Equal((400, 500, 600, 300), (LevitateTokens.WeightRegular, LevitateTokens.WeightMedium, LevitateTokens.WeightSemibold, LevitateTokens.WeightHeroClock));
    }

    [Fact]
    public void The_fallback_is_2c5258()
    {
        Assert.Equal("#FF2C5258", Argb(1, LevitateTokens.FallbackR, LevitateTokens.FallbackG, LevitateTokens.FallbackB));
        Assert.Equal("#FF2C5258", Color("LvFallback"));
    }

    [Fact]
    public void Motion_tokens_match_the_mirror()
    {
        Assert.Equal(LevitateTokens.Ease.ToString(), Key("LvEaseBezier").Value.Trim());
        Assert.Equal(LevitateTokens.EaseLift.ToString(), Key("LvEaseLiftBezier").Value.Trim());
        Assert.Equal(LevitateTokens.EaseMorph.ToString(), Key("LvEaseMorphBezier").Value.Trim());
        Assert.Equal("0.22,1,0.36,1", LevitateTokens.EaseMorph.ToString());
        Num("LvDurMs", LevitateTokens.DurMs);
        Num("LvDurSlowMs", LevitateTokens.DurSlowMs);
        Num("LvSlideMs", Motion.SlideMs);
        Assert.Equal((0.94, 0.97, 0.985), (LevitateTokens.PressScaleSmall, LevitateTokens.PressScalePill, LevitateTokens.PressScaleTile));
        Num("LvPressScaleSmall", LevitateTokens.PressScaleSmall);
        Num("LvPressScalePill", LevitateTokens.PressScalePill);
        Num("LvPressScaleTile", LevitateTokens.PressScaleTile);
    }

    [Fact]
    public void The_shadows_are_LOOK_mds_tinted_0_12_16()
    {
        Assert.Equal(new ShadowLayer[] { new(0, 1, 1, 0, 0, 12, 16, .14), new(0, 8, 18, -6, 0, 12, 16, .32), new(0, 30, 56, -26, 0, 12, 16, .52) }, LevitateTokens.Float);
        Assert.Equal(new ShadowLayer[] { new(0, 2, 2, 0, 0, 12, 16, .12), new(0, 14, 26, -8, 0, 12, 16, .38), new(0, 44, 72, -28, 0, 12, 16, .60) }, LevitateTokens.FloatHi);
        Assert.Equal(new ShadowLayer[] { new(0, 2, 4, 0, 0, 12, 16, .18), new(0, 24, 48, -12, 0, 12, 16, .45), new(0, 60, 100, -40, 0, 12, 16, .60) }, LevitateTokens.Sheet);
        Assert.Equal(new ShadowLayer[] { new(0, 1, 2, 0, 0, 0, 0, .22) }, LevitateTokens.IslandText);
        Assert.Equal(new ShadowLayer[] { new(0, 1, 2, 0, 0, 14, 18, .35), new(0, 2, 18, 0, 0, 14, 18, .28) }, LevitateTokens.BareText);
    }

    [Fact]
    public void The_film_the_frost_and_the_dim()
    {
        Assert.Equal(((byte)10, (byte)12, (byte)14), (LevitateTokens.FilmR, LevitateTokens.FilmG, LevitateTokens.FilmB));
        Assert.Equal((0.56, 0.04, 0.163), (LevitateTokens.FilmCap, LevitateTokens.FilmStep, LevitateTokens.FilmTarget));
        Assert.Equal((512, 6.0, 15.0), (LevitateTokens.FrostWidth, LevitateTokens.LightBlurPx, LevitateTokens.SoftBlurPx));
        Assert.Equal((48.0, 20000.0), (LevitateTokens.BigMinSide, LevitateTokens.BigMinArea));
        Assert.Equal((0.20, 0.20, 0.90), (LevitateTokens.DimDefault, LevitateTokens.DimMin, LevitateTokens.DimMax));
    }

    [Fact]
    public void Theme_xaml_merges_Levitate_xaml_after_Tokens_xaml()
    {
        var doc = XDocument.Load(Path.Combine(ThemeDir, "Theme.xaml"));
        var srcs = doc.Root!.Element(P + "ResourceDictionary.MergedDictionaries")!.Elements(P + "ResourceDictionary")
            .Select(e => e.Attribute("Source")?.Value).ToList();
        Assert.Contains("ms-appx:///Theme/Levitate.xaml", srcs);
        Assert.True(srcs.IndexOf("ms-appx:///Theme/Tokens.xaml") < srcs.IndexOf("ms-appx:///Theme/Levitate.xaml"));
    }
}
