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
/// Theme/Levitate.xaml (the levitated look's tokens, lane r10-newlook) against its C# mirror, Mantle.Core LevitateTokens,
/// so the XAML a surface binds to and the numbers the pure code computes with never drift; and Theme.xaml merges it.
/// </summary>
public class LevitateTokensTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string ThemeDir => Path.Combine(NativeDir(), "src", "Mantle.App", "Theme");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static readonly Lazy<XElement> Root = new(() => XDocument.Load(Path.Combine(ThemeDir, "Levitate.xaml")).Root!);
    static XElement Key(string k) => Root.Value.Elements().Single(e => e.Attribute(X + "Key")?.Value == k);
    static double D(string k) => double.Parse(Key(k).Value.Trim(), CultureInfo.InvariantCulture);
    static string Color(string k) => Key(k).Attribute("Color")!.Value;
    static string Argb(double a, byte r, byte g, byte b) => "#" + ((byte)Math.Round(a * 255)).ToString("X2") + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");

    [Fact]
    public void Islands_and_gaps_match_the_mirror()
    {
        Assert.Equal(LevitateTokens.Gap, D("LvGap"));
        Assert.Equal("8", Key("LvGapThickness").Value.Trim());
        Assert.Equal(LevitateTokens.IslandRadius, D("LvIslandR"));
        Assert.Equal("12", Key("LvIslandRCorner").Value.Trim());
        Assert.Equal(LevitateTokens.CardRadius, D("LvCardR"));
        Assert.Equal("16", Key("LvCardRCorner").Value.Trim());
        Assert.Equal(LevitateTokens.RadiusM, D("LvRM"));
        Assert.Equal("10", Key("LvRMCorner").Value.Trim());
        Assert.Equal(LevitateTokens.CardPadding, D("LvCardPadding"));
        Assert.Equal("16", Key("LvCardPaddingThickness").Value.Trim());
        Assert.Equal((8.0, 12.0, 16.0, 10.0, 16.0), (LevitateTokens.Gap, LevitateTokens.IslandRadius, LevitateTokens.CardRadius, LevitateTokens.RadiusM, LevitateTokens.CardPadding));
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
        Assert.Equal(LevitateTokens.DurMs, D("LvDurMs"));
        Assert.Equal(LevitateTokens.DurSlowMs, D("LvDurSlowMs"));
        Assert.Equal(Motion.SlideMs, D("LvSlideMs"));
    }

    [Fact]
    public void The_shadows_and_the_film_are_the_prototypes()
    {
        Assert.Equal(new ShadowLayer(0, 30, 56, -26, 0, 12, 16, .52), LevitateTokens.Float[2]);
        Assert.Equal(new ShadowLayer(0, 44, 72, -28, 0, 12, 16, .60), LevitateTokens.FloatHi[2]);
        Assert.Equal(2, LevitateTokens.TextLift.Length);
        Assert.Equal(((byte)10, (byte)12, (byte)14), (LevitateTokens.FilmR, LevitateTokens.FilmG, LevitateTokens.FilmB));
        Assert.Equal((0.56, 0.04, 0.163), (LevitateTokens.FilmCap, LevitateTokens.FilmStep, LevitateTokens.FilmTarget));
        Assert.Equal((512, 6.0, 15.0), (LevitateTokens.FrostWidth, LevitateTokens.LightBlurPx, LevitateTokens.SoftBlurPx));
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
