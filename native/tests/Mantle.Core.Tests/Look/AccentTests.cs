using System;
using Mantle.Core.Look;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// lib/accent.js ported: the look-sync project's own accent contract tests (test/accent-mode.test.js at
/// 0b55b9c, the accent part), plus a golden table produced by running the vendored accent.js in Node, so the
/// port is exact for every hue family, both themes.
/// </summary>
public class AccentTests
{
    [Fact]
    public void Contract_hue_212_dark_reproduces_the_steel_blue_and_every_dark_token()
    {
        var t = Accent.Tokens(212, "dark")!;
        Assert.Equal("#9fb4cc", t.Acc);
        Assert.Equal("#b7c8db", t.Strong);
        Assert.Equal("#11161c", t.On);
        Assert.Equal("rgba(159, 180, 204, 0.14)", t.Soft);
        Assert.Equal("rgba(159, 180, 204, 0.42)", t.Line);
        Assert.Equal("rgba(159, 180, 204, 0.32)", t.Select);
        Assert.Equal("rgba(159, 180, 204, 0.22)", t.Glow);
    }

    [Fact]
    public void Contract_hue_212_light_reproduces_the_light_tokens()
    {
        var t = Accent.Tokens(212, "light")!;
        Assert.Equal("#4d6784", t.Acc);
        Assert.Equal("#3c536d", t.Strong);
        Assert.Equal("#ffffff", t.On);
        Assert.Equal("rgba(77, 103, 132, 0.11)", t.Soft);
        Assert.Equal("rgba(77, 103, 132, 0.4)", t.Line);
        Assert.Equal("rgba(77, 103, 132, 0.2)", t.Select);
    }

    [Fact]
    public void Contract_hue_null_keeps_the_own_tokens_a_bad_hue_throws_and_0_and_360_agree()
    {
        Assert.Null(Accent.Tokens(null, "dark"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Accent.Tokens(400, "dark"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Accent.Tokens(-1, "dark"));
        Assert.False(Accent.ValidHue(1.5));
        Assert.True(Accent.ValidHue(0) && Accent.ValidHue(360));
        Assert.Equal(Accent.Tokens(0, "dark")!.Acc, Accent.Tokens(360, "dark")!.Acc);
        Assert.Equal("accent hue must be null or an integer 0..360", Accent.BadHue);
    }

    [Fact]
    public void The_derived_alphas_per_theme()
    {
        Assert.Equal((0.14, 0.42, 0.32, 0.22), Accent.Alpha(dark: true));
        Assert.Equal((0.11, 0.40, 0.20, 0.22), Accent.Alpha(dark: false));
    }

    // node -e "const A=require('./lib/accent.js'); ... A.tokens(h,t)" (lib/accent.js at 4f82c4b)
    [Theory]
    [InlineData(0, "dark", "#cc9f9f", "#dbb7b7", "#1c1111", "rgba(204, 159, 159, 0.14)")]
    [InlineData(0, "light", "#844d4d", "#6d3c3c", "#ffffff", "rgba(132, 77, 77, 0.11)")]
    [InlineData(37, "dark", "#ccbb9f", "#dbcdb7", "#1c1811", "rgba(204, 187, 159, 0.14)")]
    [InlineData(37, "light", "#846f4d", "#6d5a3c", "#ffffff", "rgba(132, 111, 77, 0.11)")]
    [InlineData(120, "dark", "#9fcc9f", "#b7dbb7", "#111c11", "rgba(159, 204, 159, 0.14)")]
    [InlineData(120, "light", "#4d844d", "#3c6d3c", "#ffffff", "rgba(77, 132, 77, 0.11)")]
    [InlineData(200, "dark", "#9fbdcc", "#b7cfdb", "#11181c", "rgba(159, 189, 204, 0.14)")]
    [InlineData(200, "light", "#4d7284", "#3c5d6d", "#ffffff", "rgba(77, 114, 132, 0.11)")]
    [InlineData(300, "dark", "#cc9fcc", "#dbb7db", "#1c111c", "rgba(204, 159, 204, 0.14)")]
    [InlineData(300, "light", "#844d84", "#6d3c6d", "#ffffff", "rgba(132, 77, 132, 0.11)")]
    [InlineData(359, "dark", "#cc9fa0", "#dbb7b8", "#1c1111", "rgba(204, 159, 160, 0.14)")]
    [InlineData(359, "light", "#844d4e", "#6d3c3d", "#ffffff", "rgba(132, 77, 78, 0.11)")]
    [InlineData(360, "dark", "#cc9f9f", "#dbb7b7", "#1c1111", "rgba(204, 159, 159, 0.14)")]
    public void Every_hue_matches_the_JavaScript_original(int hue, string theme, string acc, string strong, string on, string soft)
    {
        var t = Accent.Tokens(hue, theme)!;
        Assert.Equal((acc, strong, on, soft), (t.Acc, t.Strong, t.On, t.Soft));
    }
}
