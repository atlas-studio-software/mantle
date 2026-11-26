using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mantle.Core.Look.Levitate;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Lane r15-quick-launcher: Theme/LvLauncher.xaml (the islands launcher's numbers, read by Mantle.App
/// Surfaces/Launcher/LauncherSpec.cs) against the approved prototype, apps-r3/mantle-bar panels/launcher.css (light-mantle
/// copies it unchanged) and look.css. Plan v0.2: prototype-spec numeric checks in unit, no pixel compare. Line numbers in
/// the comments are launcher.css's (L3) and look.css's (look L36). Parsed the way BarLightTokensTests parses Levitate.xaml.
/// </summary>
public class LauncherSpecTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string ThemeDir => Path.Combine(NativeDir(), "src", "Mantle.App", "Theme");
    static string LauncherDir => Path.Combine(NativeDir(), "src", "Mantle.App", "Surfaces", "Launcher");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static readonly Lazy<string> Raw = new(() => File.ReadAllText(Path.Combine(ThemeDir, "LvLauncher.xaml")));
    static readonly Lazy<XElement> Root = new(() => XDocument.Parse(Raw.Value).Root!);
    static readonly Lazy<XElement> LevitateRoot = new(() => XDocument.Load(Path.Combine(ThemeDir, "Levitate.xaml")).Root!);

    static XElement El(string key) => Root.Value.Elements().Single(e => e.Attribute(X + "Key")?.Value == key);
    static double D(string key)
    {
        var e = El(key);
        Assert.Equal(X + "Double", e.Name);
        return double.Parse(e.Value.Trim(), CultureInfo.InvariantCulture);
    }
    /// A Thickness or CornerRadius as its four numbers (one number = all four, XAML's shorthand).
    static double[] Four(string key, XName type)
    {
        var e = El(key);
        Assert.Equal(type, e.Name);
        var n = e.Value.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 1 ? new[] { n[0], n[0], n[0], n[0] } : n;
    }
    static double[] T(string key) => Four(key, P + "Thickness");
    static double[] R(string key) => Four(key, P + "CornerRadius");

    static string[] XamlKeys() => Root.Value.Elements().Select(e => e.Attribute(X + "Key")!.Value).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    static string Source(string file) => File.ReadAllText(Path.Combine(LauncherDir, file));

    [Fact]
    public void The_reader_and_the_dictionary_have_the_same_keys()
    {
        var read = Regex.Matches(Source("LauncherSpec.cs"), "\"(LvLauncher\\w+)\"").Select(m => m.Groups[1].Value).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(XamlKeys(), read);
    }

    [Fact]
    public void No_xaml_comment_holds_a_double_hyphen()
    {
        foreach (Match m in Regex.Matches(Raw.Value, "<!--(.*?)-->", RegexOptions.Singleline))
            Assert.DoesNotContain("--", m.Groups[1].Value);
    }

    [Fact]
    public void The_island_is_600_by_520_capped_at_the_screen_less_24_and_80_radius_14()
    {
        // L3: width min(600px, calc(100vw - 24px)); height min(520px, calc(100vh - 80px))
        Assert.Equal(600, D("LvLauncherWidth"));
        Assert.Equal(520, D("LvLauncherHeight"));
        Assert.Equal(24, D("LvLauncherScreenSideMargin"));
        Assert.Equal(80, D("LvLauncherScreenCapMargin"));
        // look L36 / L175: a pop-over island is r-pop 14
        Assert.Equal(14, D("LvLauncherRadius"));
        Assert.Equal(LevitateTokens.PopoverRadius, D("LvLauncherRadius"));
    }

    [Fact]
    public void The_window_has_room_for_the_whole_float_hi_shadow()
    {
        // look L39 --float-hi, LevitateTokens.FloatHi: each layer reaches dy + blur + spread below, |dx| + blur + spread aside
        var room = T("LvLauncherShadowRoom");
        Assert.Equal(new double[] { 44, 12, 44, 88 }, room);
        foreach (var l in LevitateTokens.FloatHi) {
            Assert.True(room[3] >= l.Dy + l.Blur + l.Spread, "below");
            Assert.True(room[1] >= -l.Dy + l.Blur + l.Spread, "above");
            Assert.True(room[0] >= Math.Abs(l.Dx) + l.Blur + l.Spread && room[2] >= Math.Abs(l.Dx) + l.Blur + l.Spread, "aside");
        }
    }

    [Fact]
    public void The_field_is_56_tall_with_a_hair_and_an_inset_accent_line_and_no_ring()
    {
        // L7: height 56px; padding 0 16px; gap 12px; L11: the glyph 18; L12: the query fs-l 16
        Assert.Equal(56, D("LvLauncherFieldHeight"));
        Assert.Equal(new double[] { 16, 0, 16, 0 }, T("LvLauncherFieldPadding"));
        Assert.Equal(12, D("LvLauncherFieldGap"));
        Assert.Equal(18, D("LvLauncherFieldIcon"));
        Assert.Equal(16, D("LvLauncherFieldFont"));
        // L8: a 1 px hair under the whole width; L9: the accent line left 16 right 16, fading over --dur (look L45: 160ms)
        Assert.Equal(1, D("LvLauncherFieldLine"));
        Assert.Equal(new double[] { 16, 0, 16, 0 }, T("LvLauncherFieldLineInset"));
        Assert.Equal(160, D("LvLauncherFieldLineFadeMs"));
        // FIXES-2: no ring round the field (the reader has no key for one)
        Assert.DoesNotContain(XamlKeys(), k => k.Contains("Ring", StringComparison.Ordinal) || k.Contains("Edge", StringComparison.Ordinal));
    }

    [Fact]
    public void The_list_headings_and_the_no_match_line()
    {
        Assert.Equal(new double[] { 8, 4, 8, 8 }, T("LvLauncherBodyPadding"));          // L18: padding 4px 8px 8px
        Assert.Equal(new double[] { 8, 12, 8, 4 }, T("LvLauncherGroupPadding"));        // L23: padding 12px 8px 4px
        Assert.Equal(new double[] { 8, 4, 8, 4 }, T("LvLauncherGroupPaddingFirst"));    // L24: the first, padding-top 4px
        Assert.Equal(12, D("LvLauncherGroupFont"));                                     // L23: fs-xs
        Assert.Equal(new double[] { 8, 8, 8, 0 }, T("LvLauncherNonePadding"));          // L26: padding 8px 8px 0
        Assert.Equal(13, D("LvLauncherNoneFont"));                                      // L26: fs-s
    }

    [Fact]
    public void A_row_is_36_a_quick_links_row_32_radius_8()
    {
        // L30: height 36px; padding 0 12px 0 8px; gap 12px; border-radius var(--r-m) (look L36: 8)
        Assert.Equal(36, D("LvLauncherRowHeight"));
        Assert.Equal(new double[] { 8, 0, 12, 0 }, T("LvLauncherRowPadding"));
        Assert.Equal(12, D("LvLauncherRowGap"));
        Assert.Equal(8, D("LvLauncherRowRadius"));
        Assert.Equal(LevitateTokens.RadiusCtlL, D("LvLauncherRowRadius"));
        // L37: .lch-row.bare (Quick links): height 32px; padding-left 12px
        Assert.Equal(32, D("LvLauncherRowHeightBare"));
        Assert.Equal(new double[] { 12, 0, 12, 0 }, T("LvLauncherRowPaddingBare"));
        // look L119 .app-ic: 24, radius r-s 6
        Assert.Equal(24, D("LvLauncherIcon"));
        Assert.Equal(new double[] { 6, 6, 6, 6 }, R("LvLauncherIconCorner"));
        // L40, L42: the name fs-m 14, the kind fs-xs 12 at most 44 %
        Assert.Equal(14, D("LvLauncherNameFont"));
        Assert.Equal(12, D("LvLauncherSubFont"));
        Assert.Equal(0.44, D("LvLauncherSubShare"));
    }

    [Fact]
    public void The_selected_row_tick_is_3_wide_11_in_9_on_a_bare_row_a_pill()
    {
        // L34: left 0; top 11px; bottom 11px; width 3px; border-radius 999px; L38: .bare top 9px; bottom 9px
        Assert.Equal(3, D("LvLauncherTickWidth"));
        Assert.Equal(11, D("LvLauncherTickInset"));
        Assert.Equal(9, D("LvLauncherTickInsetBare"));
        Assert.Equal(999, D("LvLauncherTickRadius"));
        // both rows keep a 14 tall tick
        Assert.Equal(D("LvLauncherRowHeight") - 2 * D("LvLauncherTickInset"), D("LvLauncherRowHeightBare") - 2 * D("LvLauncherTickInsetBare"));
    }

    [Fact]
    public void The_foot_is_40_with_a_hair_and_the_key_hints()
    {
        // L59: height 40px; padding 0 16px; L60: a 1px hair on top; L61: gap 16px, fs-xs; L63: gap 8px
        Assert.Equal(40, D("LvLauncherFootHeight"));
        Assert.Equal(new double[] { 16, 0, 16, 0 }, T("LvLauncherFootPadding"));
        Assert.Equal(1, D("LvLauncherFootLine"));
        Assert.Equal(12, D("LvLauncherHintFont"));
        Assert.Equal(16, D("LvLauncherHintGap"));
        Assert.Equal(8, D("LvLauncherKeyGap"));
        // look L116 .kbd: fs-2xs 11; padding 1px 6px; border-radius r-xs 4
        Assert.Equal(11, D("LvLauncherKbdFont"));
        Assert.Equal(new double[] { 6, 1, 6, 1 }, T("LvLauncherKbdPadding"));
        Assert.Equal(new double[] { 4, 4, 4, 4 }, R("LvLauncherKbdCorner"));
    }

    [Fact]
    public void The_pop_over_motion_is_the_panels()
    {
        // look L169 to L173: in opacity 160ms, transform 260ms ease-lift from translateY 6 and scale 0.97; out 120 / 160 ease
        Assert.Equal(PopupLevitate.PopUpIn[0].DurMs, D("LvLauncherInFadeMs"));
        Assert.Equal(PopupLevitate.PopUpIn[1].DurMs, D("LvLauncherInMoveMs"));
        Assert.Equal(PopupLevitate.PopUpOut[0].DurMs, D("LvLauncherOutFadeMs"));
        Assert.Equal(PopupLevitate.PopUpOut[1].DurMs, D("LvLauncherOutMoveMs"));
        Assert.Equal(Math.Abs(PopupLevitate.PopUpIn[1].From), D("LvLauncherRise"));
        Assert.Equal(PopupLevitate.PopUpIn[2].From, D("LvLauncherScaleFrom"));
        Assert.Equal((160.0, 260.0, 120.0, 160.0, 6.0, 0.97),
            (D("LvLauncherInFadeMs"), D("LvLauncherInMoveMs"), D("LvLauncherOutFadeMs"), D("LvLauncherOutMoveMs"), D("LvLauncherRise"), D("LvLauncherScaleFrom")));
    }

    [Fact]
    public void Sizes_keep_the_looks_scales()
    {
        // type 11 / 12 / 13 / 14 / 16 / 20 / 28 (look L11); radii 4 / 6 / 8, pop-overs 14, cards 16, pills 999 (LOOK.md section 7)
        var type = new[] { 11.0, 12, 13, 14, 16, 20, 28 };
        foreach (var k in XamlKeys().Where(k => k.EndsWith("Font", StringComparison.Ordinal))) Assert.Contains(D(k), type);
        var radii = new[] { 4.0, 6, 8, 14, 16, 999 };
        foreach (var k in new[] { "LvLauncherRadius", "LvLauncherRowRadius", "LvLauncherTickRadius" }) Assert.Contains(D(k), radii);
        foreach (var k in XamlKeys().Where(k => k.EndsWith("Corner", StringComparison.Ordinal))) Assert.All(R(k), v => Assert.Contains(v, radii));
        // spacing 4 / 8 / 12 / 16 / 24 / 32 (0 for none); the keycap's 1 x 6 is look.css's .kbd and not a spacing step
        var space = new[] { 0.0, 4, 8, 12, 16, 24, 32 };
        foreach (var k in new[] { "LvLauncherFieldPadding", "LvLauncherFieldLineInset", "LvLauncherBodyPadding", "LvLauncherGroupPadding", "LvLauncherGroupPaddingFirst",
                     "LvLauncherNonePadding", "LvLauncherRowPadding", "LvLauncherRowPaddingBare", "LvLauncherFootPadding" })
            Assert.All(T(k), v => Assert.Contains(v, space));
        foreach (var k in new[] { "LvLauncherFieldGap", "LvLauncherRowGap", "LvLauncherHintGap", "LvLauncherKeyGap" }) Assert.Contains(D(k), space);
    }
}
