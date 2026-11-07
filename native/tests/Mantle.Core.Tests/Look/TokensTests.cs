using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Theme/Tokens.xaml against its spec, native/docs/theme-tokens.md 1.1 (every --lg-* custom property of
/// ui/shared/glass-light.css, one key each, light and dark), and the no-colour-literal lint: Tokens.xaml is the
/// only place in Mantle.App a colour may be written down, so a surface can never drift from the theme.
/// </summary>
public class TokensTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string AppDir => Path.Combine(NativeDir(), "src", "Mantle.App");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    static string KeyOf(string css) => string.Concat(css.TrimStart('-').Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));

    static (Dictionary<string, XElement> Light, Dictionary<string, XElement> Dark, Dictionary<string, XElement> Shared) Load()
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "Theme", "Tokens.xaml"));
        var root = doc.Root!;
        var themes = root.Element(P + "ResourceDictionary.ThemeDictionaries")!.Elements(P + "ResourceDictionary").ToList();
        Dictionary<string, XElement> Keys(IEnumerable<XElement> els) => els.Where(e => e.Attribute(X + "Key") != null).ToDictionary(e => e.Attribute(X + "Key")!.Value);
        return (Keys(themes.Single(t => t.Attribute(X + "Key")!.Value == "Light").Elements()),
                Keys(themes.Single(t => t.Attribute(X + "Key")!.Value == "Dark").Elements()),
                Keys(root.Elements()));
    }

    static List<string[]> TableRows()
    {
        string md = File.ReadAllText(Path.Combine(NativeDir(), "docs", "theme-tokens.md"));
        int s = md.IndexOf("### 1.1 Table", StringComparison.Ordinal), e = md.IndexOf("### 1.2", s, StringComparison.Ordinal);
        return md[s..e].Split('\n').Where(l => l.StartsWith("| `--", StringComparison.Ordinal))
            .Select(l => l.Split('|').Select(c => c.Trim().Trim('`')).ToArray()).ToList();
    }

    static readonly Regex Argb = new("^#[0-9A-F]{8}$");

    [Fact]
    public void Every_colour_token_of_the_table_is_in_both_themes_with_its_exact_value()
    {
        var (light, dark, _) = Load();
        var bad = new List<string>();
        int n = 0;
        foreach (var r in TableRows()) {
            string name = r[1], xl = r[4], xd = r[5];
            if (!Argb.IsMatch(xl) && !Argb.IsMatch(xd)) continue;
            n++;
            string key = KeyOf(name);
            string? Color(Dictionary<string, XElement> d) => d.TryGetValue(key, out var el) && el.Name == P + "SolidColorBrush" ? el.Attribute("Color")?.Value : null;
            string wantDark = Argb.IsMatch(xd) ? xd : xl;
            if (Color(dark) != wantDark) bad.Add($"{key} dark is {Color(dark) ?? "missing"}, the table says {wantDark}");
            if (Argb.IsMatch(xl)) { if (Color(light) != xl) bad.Add($"{key} light is {Color(light) ?? "missing"}, the table says {xl}"); }
            else if (Color(light) is not { } l || !Argb.IsMatch(l)) bad.Add($"{key} is dark-only in CSS but has no explicit light value");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
        Assert.True(n >= 50, "expected the table's colour tokens, found " + n);
    }

    [Theory]
    [InlineData("LgR", 8, 10)] [InlineData("LgRPill", 24, 18)] [InlineData("LgRCtl", 6, 6)] [InlineData("LgRPop", 16, 14)]
    [InlineData("LgRXl", 18, 18)] [InlineData("LgRChip", 999, 999)] [InlineData("LgBlurPx", 12, 5)] [InlineData("LgGuardBar", 4.7, 4.2)]
    public void Radii_blur_and_guard_follow_the_table(string key, double light, double dark)
    {
        var (l, d, _) = Load();
        Assert.Equal(light, double.Parse(l[key].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(dark, double.Parse(d[key].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (key.StartsWith("LgR", StringComparison.Ordinal)) Assert.Equal(light.ToString(System.Globalization.CultureInfo.InvariantCulture), l[key + "Corner"].Value);
    }

    [Theory]
    [InlineData("MDurXsMs", "240")] [InlineData("MDurSMs", "320")] [InlineData("MDurMMs", "440")] [InlineData("MDurLMs", "560")]
    [InlineData("LgDurMs", "560")] [InlineData("LgDurOutMs", "260")] [InlineData("LgDurUiMs", "120")] [InlineData("LgDurUiSlowMs", "200")]
    [InlineData("MFadeInBezier", "0.2,0,0,1")] [InlineData("MFadeOutBezier", "0.4,0,1,1")] [InlineData("MAccelBezier", "0.3,0,0.8,0.15")]
    [InlineData("LgEaseBezier", "0.22,0.68,0.3,1")] [InlineData("LgEaseUiBezier", "0.2,0,0,1")] [InlineData("LgDisabled", "0.45")]
    public void Motion_and_shared_tokens_are_the_same_in_both_themes(string key, string value)
    {
        var (_, _, shared) = Load();
        Assert.Equal(value, shared[key].Value);
    }

    [Fact]
    public void The_bars_own_names_are_still_there_for_BarWindow()
    {
        var (l, d, s) = Load();
        foreach (var k in new[] { "LgPaneFill", "LgPaneFillFallback", "LgEdgeOut", "LgLip", "LgInk", "LgInk2", "LgInk3", "LgAcc", "LgAccSoft", "LgHover", "LgPress", "LgTile", "LgPillCorner", "LgLipCorner", "LgBodySize", "LgDateSize" }) {
            Assert.True(l.ContainsKey(k), "light lacks " + k);
            Assert.True(d.ContainsKey(k), "dark lacks " + k);
        }
        Assert.True(s.ContainsKey("LgFont"));
        Assert.Equal(l.Keys.OrderBy(k => k), d.Keys.OrderBy(k => k));   // no key only one theme has
    }

    /// Colour literals that are not theme colours, each with its reason. Anything else fails.
    static readonly (string File, string Snippet, string Why)[] Allowed = {
        ("BarWindow.xaml.cs", "CreateColorBrush(Colors.Black)", "an opacity mask shape: only its alpha is used, never seen as a colour"),
        ("BarWindow.xaml.cs", "Color.FromArgb(alpha, 14, 18, 38)", "the -elev-* drop-shadow ink rgba(14,18,38,a) with a computed alpha (theme-tokens.md 3.4); a shadow token is native-windows' to add"),
        ("TransparentBackdrop.cs", "Color.FromArgb(0, 0, 0, 0)", "fully transparent: no colour"),
    };

    static readonly Regex Literal = new(@"#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\b|Color\.FromArgb\s*\(|Colors\.[A-Z]|ColorHelper\.|Color\s*=\s*""[A-Za-z]+""|Fill\s*=\s*""[A-Za-z#]|Background\s*=\s*""(?!\{)[A-Za-z#]|Foreground\s*=\s*""(?!\{)[A-Za-z#]");

    [Fact]
    public void No_colour_literal_in_Mantle_App_outside_Tokens_xaml()
    {
        var hits = new List<string>();
        foreach (var f in Directory.GetFiles(AppDir, "*.*", SearchOption.AllDirectories)) {
            string ext = Path.GetExtension(f);
            if (ext != ".cs" && ext != ".xaml") continue;
            string rel = Path.GetRelativePath(AppDir, f);
            if (rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || rel.StartsWith("obj", StringComparison.Ordinal) || rel.StartsWith("bin", StringComparison.Ordinal)) continue;
            if (rel == Path.Combine("Theme", "Tokens.xaml")) continue;
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++) {
                string code = lines[i];
                if (ext == ".cs") { int c = code.IndexOf("//", StringComparison.Ordinal); if (c >= 0) code = code[..c]; }
                if (ext == ".xaml" && code.TrimStart().StartsWith("<!--", StringComparison.Ordinal)) continue;
                if (!Literal.IsMatch(code)) continue;
                if (Allowed.Any(a => Path.GetFileName(f) == a.File && code.Contains(a.Snippet, StringComparison.Ordinal))) continue;
                if (Background_is_transparent(code)) continue;
                hits.Add($"{rel}:{i + 1}: {code.Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "colour literals outside Theme/Tokens.xaml (use a {ThemeResource Lg...} token):\n" + string.Join("\n", hits));
    }

    /// "Transparent" is the absence of colour, which a hit-testable surface needs; it is not a theme colour.
    static bool Background_is_transparent(string code) => Regex.Replace(code, @"(Background|Fill|Foreground|Color)\s*=\s*""Transparent""", "").Length != code.Length && !Literal.IsMatch(Regex.Replace(code, @"(Background|Fill|Foreground|Color)\s*=\s*""Transparent""", ""));
}
