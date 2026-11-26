using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// Lane r16-qs-accent: every resource key Mantle.App looks up BY NAME must be defined by the theme it is asked in.
/// The App reads many tokens in code (a string handed to a helper that walks Application.Current.Resources), and a
/// key that a token file no longer has is only found at run time: drawn clear at best, a XAML parse error (an
/// unhandled crash) when the name ends up in a {ThemeResource ...} string handed to XamlReader. Lane r16-bar-accent
/// moved the islands accent out of Theme/Levitate.xaml while Surfaces/Bar/QuickSettings/QsLook.cs still named
/// LvBarAccent / LvBarAccentInk, and quick settings stopped opening. This is the check for that whole class of bug.
///
/// Mantle.Core.Tests cannot reference Mantle.App, so it is a SOURCE SCAN (like TokensTests / BarLightTokensTests):
///  1. the keys DEFINED: Theme/Theme.xaml and every dictionary it merges (recursively), App.xaml's own resources, and
///     the element resources of the App's other .xaml files (BarWindow.xaml's LgBtn style); per theme: a top-level
///     key serves both themes, a ThemeDictionaries Dark / Light entry its own theme (Default serves both);
///  2. the keys LOOKED UP: (a) the explicit helper patterns below, each naming the file(s) that use it; a "theme"
///     helper only searches ThemeDictionaries, so its key must be in Dark AND Light; (b) a catch-all: every string
///     literal in App code shaped like a token name (Lg*, Lv*, M* motion keys) except a StartsWith prefix, which
///     must be defined top-level or in both themes (this is what catches QsLook's ["Lg..."] = "LvBar..." map, both
///     sides); (c) every {ThemeResource X} / {StaticResource X} in App .xaml files and in XAML strings in App code;
///  3. every looked-up key must be defined; the failure lists each one with the file:line of the lookup.
/// </summary>
public class ResourceKeysTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string AppDir => Path.Combine(NativeDir(), "src", "Mantle.App");
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // ------------------------------------------------------------------ the keys defined

    sealed class Defined
    {
        public readonly HashSet<string> Top = new(StringComparer.Ordinal), Dark = new(StringComparer.Ordinal), Light = new(StringComparer.Ordinal);
        public readonly List<string> Files = new();
        public bool InBoth(string k) => Top.Contains(k) || (Dark.Contains(k) && Light.Contains(k));
        public bool InThemes(string k) => Dark.Contains(k) && Light.Contains(k);
    }

    static readonly Lazy<Defined> Defs = new(LoadDefined);

    static Defined LoadDefined()
    {
        var d = new Defined();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // App.xaml: its own resources (none today) and the dictionaries it merges (Theme/Theme.xaml; XamlControlsResources
        // is WinUI's and is not a Source dictionary)
        var app = XDocument.Load(Path.Combine(AppDir, "App.xaml")).Root!;
        foreach (var rd in app.Descendants(P + "ResourceDictionary").Where(e => e.Attribute("Source") == null && e.Parent?.Name.LocalName == "Application.Resources"))
            Dictionary(rd, AppDir, d, seen);
        foreach (var src in app.Descendants(P + "ResourceDictionary").Select(e => e.Attribute("Source")?.Value).Where(s => s != null))
            Merge(src!, AppDir, d, seen);   // App.xaml spells it "Theme/Theme.xaml"
        Merge("ms-appx:///Theme/Theme.xaml", AppDir, d, seen);   // the one theme entry point, whatever App.xaml spells
        // element resources of the App's other .xaml files (BarWindow.xaml's <Grid.Resources>): visible to that
        // file's elements and code (BarWindow.Media.cs reads Root.Resources "LgBtn")
        foreach (var f in Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories).Where(Scanned)) {
            if (Rel(f).StartsWith("Theme/", StringComparison.Ordinal) || Rel(f) == "App.xaml") continue;
            foreach (var e in XDocument.Load(f).Root!.Descendants().Where(e => e.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal)))
                foreach (var r in e.Elements()) if (r.Attribute(X + "Key")?.Value is { } k) d.Top.Add(k);
        }
        return d;
    }

    /// A merged dictionary's Source: "ms-appx:///X" from the App's root, else relative to the file that merges it.
    static void Merge(string source, string baseDir, Defined d, HashSet<string> seen)
    {
        const string scheme = "ms-appx:///";
        string path = source.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppDir, source[scheme.Length..].Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(baseDir, source.Replace('/', Path.DirectorySeparatorChar));
        path = Path.GetFullPath(path);
        if (!seen.Add(path)) return;
        Assert.True(File.Exists(path), $"a merged dictionary is missing: {source}");
        d.Files.Add(Rel(path));
        Dictionary(XDocument.Load(path).Root!, Path.GetDirectoryName(path)!, d, seen);
    }

    static void Dictionary(XElement rd, string baseDir, Defined d, HashSet<string> seen)
    {
        foreach (var e in rd.Elements()) {
            if (e.Attribute(X + "Key")?.Value is { } k) d.Top.Add(k);
            if (e.Name == P + "ResourceDictionary.ThemeDictionaries")
                foreach (var t in e.Elements(P + "ResourceDictionary")) {
                    string theme = t.Attribute(X + "Key")?.Value ?? "";
                    foreach (var k2 in t.Elements().Select(x => x.Attribute(X + "Key")?.Value).Where(x => x != null)) {
                        if (theme is "Dark" or "Default") d.Dark.Add(k2!);
                        if (theme is "Light" or "Default") d.Light.Add(k2!);
                    }
                }
            if (e.Name == P + "ResourceDictionary.MergedDictionaries")
                foreach (var m in e.Elements(P + "ResourceDictionary")) {
                    if (m.Attribute("Source")?.Value is { } s) Merge(s, baseDir, d, seen);
                    else Dictionary(m, baseDir, d, seen);
                }
        }
    }

    // ------------------------------------------------------------------ the keys looked up

    enum Need { Either, Themes }

    sealed record Lookup(string Key, string File, int Line, Need Need, string How);

    /// A token-shaped name: the Electron look's Lg*, the levitated look's Lv* (LvBar*, LvQs*, LvLa*), the motion M* keys.
    const string KeyShape = @"(?:Lg|Lv)[A-Z][A-Za-z0-9]*|M[A-Z][a-z][A-Za-z0-9]*";
    static readonly Regex Literal = new("\"(" + KeyShape + ")\"", RegexOptions.Compiled);

    /// The lookup helpers of Mantle.App that take a key as a string. File: a regex on the file's path under Mantle.App
    /// (null = any file); Call: the call up to its "(", whose argument text (no nested parentheses) is searched for
    /// key literals, so a ternary's two literals both count. Themes: the helper searches ThemeDictionaries only.
    static readonly (string? File, string Call, Need Need, string How)[] Helpers = {
        // Shell/Levitate/IslandsInk.cs: Find / Token / Color(dark, "LvBar..."): ThemeDictionaries only
        (null, @"IslandsInk\.(?:Token|Color|Find)\(", Need.Themes, "IslandsInk"),
        // Surfaces/Bar/Notifications/BarLook.cs: BarLook.B(dark, "Lg..."): ThemeDictionaries only
        (null, @"BarLook\.B\(", Need.Themes, "BarLook.B"),
        // Surfaces/Bar/Notifications/NotifPanel.cs: T("Lg...") => B(_dark, key) through `using static BarLook`
        (@"Notifications/NotifPanel\.cs$", @"\bT\(", Need.Themes, "NotifPanel T"),
        // Surfaces/Bar/Chips/BarWindow.More.cs: T("Lg...") => BarLook.B, and Txt(text, "Lg...", ...)
        (@"Chips/BarWindow\.More\.cs$", @"\b(?:T|Txt)\(", Need.Themes, "More T / Txt"),
        // QuickSettings/QsPanel*.cs: B("Lg...") and Tok(theme, "Lg...") (QsLook.Ink's islands twin, else ThemeDictionaries)
        (@"QuickSettings/QsPanel[^/]*\.cs$", @"\b(?:B|Tok)\(", Need.Themes, "QsPanel B / Tok"),
        // QuickSettings/QsPanel*.cs: Fill(canvas, paths, "Lg...") puts the name (or its QsLook twin) in a {ThemeResource}
        (@"QuickSettings/QsPanel[^/]*\.cs$", @"\b(?:Fill|PaintChevron)\(", Need.Either, "QsPanel Fill"),
        // QuickSettings/QsLook.cs, QsPanel.Islands.cs: QsLook.N / QsLook.T / L / LT("LvQs...", fallback): top level
        (null, @"QsLook\.(?:N|T|Ink)\(", Need.Either, "QsLook"),
        (@"QuickSettings/QsPanel\.Islands\.cs$", @"\b(?:L|LT)\(", Need.Either, "QsPanel L / LT"),
        // Calendar/CalPanel.cs, Media/MediaCard.cs, Desktop/DesktopSurface.cs: B("Lg..."): ThemeDictionaries only
        (@"(?:Calendar/CalPanel|Media/MediaCard|Desktop/DesktopSurface)\.cs$", @"\bB\(", Need.Themes, "panel B"),
        // Downloads/DownloadsShelf.cs: B("Lg...") and Ink("Lv...", "Lg...") (the Lv name top level, its twin themed)
        (@"Downloads/DownloadsShelf\.cs$", @"\b(?:B|Ink)\(", Need.Either, "DownloadsShelf B / Ink"),
        // Launcher/LauncherSurface*.cs: Lv("LvBar...") => IslandsInk.Token
        (@"Launcher/LauncherSurface[^/]*\.cs$", @"\bLv\(", Need.Themes, "Launcher Lv"),
        // Launcher/LauncherTokens.cs, used as t.Of / t.Brush / _tok.Brush("Lg...", dark): ThemeDictionaries only
        (@"Launcher/", @"\.(?:Of|Brush)\(", Need.Themes, "LauncherTokens"),
        // Launcher/LauncherSpec.cs: D / T / R("LvLa...", default): top level
        (@"Launcher/LauncherSpec\.cs$", @"\b(?:D|T|R)\(", Need.Either, "LauncherSpec"),
        // BarWindow.xaml.cs, Tasks/BarWindow.Drag.cs, Tasks/BarWindow.Previews.cs: ThemeToken / TokenOr / TryToken / Res
        (null, @"\b(?:ThemeToken|TokenOr|TryToken|Res)\(", Need.Either, "BarWindow ThemeToken"),
        // Media/BarWindow.Media.cs: MTok("Lg...") (themed, then top level)
        (null, @"\bMTok\(", Need.Either, "MTok"),
        // Osd/OsdSurface.cs, Toasts/ToastsSurface.cs: Tok / B / D("Lg..."); TopBar/TopBarSurface.cs: Tok / Br / Dbl
        (@"(?:Osd/OsdSurface|Toasts/ToastsSurface)\.cs$", @"\b(?:Tok|B|D)\(", Need.Either, "surface Tok"),
        (@"TopBar/TopBarSurface[^/]*\.cs$", @"\b(?:Tok|Br|Dbl)\(", Need.Either, "TopBar Tok"),
        // GameMode/GameModeRow.cs, Desktop/DeskMenuWindow.cs: _b("Lg...")
        (null, @"\b_b\(", Need.Either, "_b"),
        // any file: Resources.TryGetValue("K", ...) (BarWindow.Media.cs reads LgBtn); a Resources["K"] read is a literal (b)
        (null, @"Resources\.TryGetValue\(", Need.Either, "Resources.TryGetValue"),
    };

    /// A computed name the scan cannot read, with the keys it can produce. QsPanel.Fill's {ThemeResource {inkKey}}
    /// needs no entry: its names are Fill's literal arguments and QsLook's map, both scanned.
    static readonly (string File, string Expr, string[] Keys)[] Computed = {
        // the hover / pressed card and row templates: '{ThemeResource HOVER}' / '{ThemeResource PRESS}' are
        // placeholders that .Replace("HOVER", "LgHover") and so on fill in (the literals are also seen by the catch-all)
        ("Surfaces/Bar/Tasks/BarWindow.Previews.cs", "CardTemplate HOVER / PRESS", new[] { "LgHover", "LgPress", "LgWarnSoft" }),
        ("Surfaces/Bar/Tasks/BarWindow.WindowList.cs", "RowTemplate HOVER / PRESS", new[] { "LgHover", "LgPress", "LgWarnSoft" }),
    };

    /// The template placeholders of Computed: not keys themselves.
    static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal) { "HOVER", "PRESS" };

    /// Keys looked up that no token file defines, on purpose (none needed today): WinUI's own keys
    /// (SystemAccentColor, XamlControlsResources) are not token-shaped and the catch-all never sees them; a key
    /// referenced as {ThemeResource X} in XAML is listed here only when WinUI provides it.
    static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) { };

    static bool Scanned(string f)
    {
        var parts = Rel(f).Split('/');
        return !parts.Contains("obj") && !parts.Contains("bin");
    }

    static string Rel(string f) => Path.GetRelativePath(AppDir, f).Replace('\\', '/');

    /// The file's lines with comments blanked (// to end of line, /* */ blocks), strings kept.
    static string[] Code(string text)
    {
        var o = new StringBuilder(text.Length);
        bool block = false, line = false, str = false, verbatim = false;
        for (int i = 0; i < text.Length; i++) {
            char c = text[i], n = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '\n') { line = false; if (str && !verbatim) str = false; o.Append(c); continue; }
            if (block) { if (c == '*' && n == '/') { block = false; i++; } o.Append(' '); continue; }
            if (line) { o.Append(' '); continue; }
            if (str) {
                if (!verbatim && c == '\\') { o.Append(c); if (n != '\n' && n != '\0') { o.Append(n); i++; } continue; }
                if (c == '"') { if (verbatim && n == '"') { o.Append("\"\""); i++; continue; } str = false; }
                o.Append(c); continue;
            }
            if (c == '/' && n == '/') { line = true; o.Append(' '); continue; }
            if (c == '/' && n == '*') { block = true; o.Append(' '); continue; }
            if (c == '\'' && i + 3 < text.Length) {   // a char literal: '"' must not open a string
                int end = text.IndexOf('\'', i + (n == '\\' ? 3 : 2));
                if (end > i && end - i <= 8) { o.Append(text, i, end - i + 1); i = end; continue; }
            }
            if (c == '"') { str = true; verbatim = i > 0 && (text[i - 1] == '@' || (text[i - 1] == '$' && i > 1 && text[i - 2] == '@')); }
            o.Append(c);
        }
        return o.ToString().Split('\n');
    }

    static readonly Regex XamlRef = new(@"\{(?:ThemeResource|StaticResource)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled);

    static readonly Lazy<List<Lookup>> Lookups = new(Scan);
    static readonly Dictionary<string, int> HelperHits = new();

    static List<Lookup> Scan()
    {
        var all = new List<Lookup>();
        var helpers = Helpers.Select(h => (File: h.File == null ? null : new Regex(h.File), Call: new Regex(h.Call + @"([^()]*)\)"), h.Need, h.How)).ToArray();
        foreach (var h in Helpers) HelperHits[h.How + " " + h.Call] = 0;
        foreach (var f in Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories).Where(Scanned).OrderBy(f => f, StringComparer.Ordinal)) {
            string rel = Rel(f);
            var lines = Code(File.ReadAllText(f));
            for (int i = 0; i < lines.Length; i++) {
                string l = lines[i];
                // (a) the helpers
                for (int h = 0; h < helpers.Length; h++) {
                    if (helpers[h].File is { } fr && !fr.IsMatch(rel)) continue;
                    foreach (Match m in helpers[h].Call.Matches(l))
                        foreach (Match k in Literal.Matches(m.Groups[1].Value)) {
                            all.Add(new Lookup(k.Groups[1].Value, rel, i + 1, helpers[h].Need, helpers[h].How));
                            HelperHits[Helpers[h].How + " " + Helpers[h].Call]++;
                        }
                }
                // (b) the catch-all: any token-shaped literal that is not a StartsWith prefix
                foreach (Match k in Literal.Matches(l)) {
                    string before = l[..k.Index].TrimEnd();
                    if (before.EndsWith("StartsWith(", StringComparison.Ordinal)) continue;
                    all.Add(new Lookup(k.Groups[1].Value, rel, i + 1, Need.Either, "literal"));
                }
                // (c) XAML strings built in code
                foreach (Match k in XamlRef.Matches(l))
                    if (!Placeholders.Contains(k.Groups[1].Value))
                        all.Add(new Lookup(k.Groups[1].Value, rel, i + 1, Need.Either, "{ThemeResource} in code"));
            }
        }
        foreach (var f in Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories).Where(Scanned).OrderBy(f => f, StringComparer.Ordinal)) {
            var lines = File.ReadAllText(f).Split('\n');
            for (int i = 0; i < lines.Length; i++)
                foreach (Match k in XamlRef.Matches(lines[i]))
                    all.Add(new Lookup(k.Groups[1].Value, Rel(f), i + 1, Need.Either, "{ThemeResource} in xaml"));
        }
        foreach (var c in Computed)
            foreach (var k in c.Keys) all.Add(new Lookup(k, c.File, 0, Need.Either, "computed " + c.Expr));
        return all;
    }

    static string Lacks(Defined d, Lookup l)
    {
        if (Allowed.Contains(l.Key)) return "";
        if (l.Need == Need.Themes) {
            if (d.InThemes(l.Key)) return "";
            if (!d.InBoth(l.Key)) return "not defined";
            return "only top level, but " + l.How + " searches the Dark / Light ThemeDictionaries";
        }
        if (d.InBoth(l.Key)) return "";
        if (d.Dark.Contains(l.Key)) return "Dark only, no Light";
        if (d.Light.Contains(l.Key)) return "Light only, no Dark";
        return "not defined";
    }

    // ------------------------------------------------------------------ the checks

    [Fact]
    public void Every_key_the_app_looks_up_is_defined_in_each_theme_it_is_asked_for()
    {
        var d = Defs.Value;
        var bad = Lookups.Value.Select(l => (l, why: Lacks(d, l))).Where(x => x.why != "")
            .GroupBy(x => (x.l.Key, x.why)).OrderBy(g => g.Key.Key, StringComparer.Ordinal)
            .Select(g => $"  {g.Key.Key} ({g.Key.why}): " + string.Join(", ", g.Select(x => $"{x.l.File}:{x.l.Line} [{x.l.How}]").Distinct()))
            .ToList();
        Assert.True(bad.Count == 0,
            $"Mantle.App looks up {bad.Count} resource key(s) that {string.Join(" + ", d.Files)} (and App.xaml) do not define:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void The_scan_is_not_vacuous()
    {
        var d = Defs.Value;
        Assert.Contains("Theme/Tokens.xaml", d.Files);
        Assert.Contains("Theme/Levitate.xaml", d.Files);
        Assert.Contains("Theme/LvQuick.xaml", d.Files);
        Assert.Contains("Theme/LvLauncher.xaml", d.Files);
        Assert.True(d.Top.Count > 100 && d.Dark.Count > 50 && d.Light.Count > 50, $"defined: {d.Top.Count} top level, {d.Dark.Count} dark, {d.Light.Count} light");

        var all = Lookups.Value;
        int keys = all.Select(l => l.Key).Distinct().Count();
        Assert.True(keys > 50, $"the scan found only {keys} distinct looked-up keys");
        Assert.True(all.Count(l => l.How != "literal") > 200, $"the helper patterns found only {all.Count(l => l.How != "literal")} lookups");
        // QsLook's Lg -> LvBar twin map: both sides of an entry are keys
        Assert.Contains(all, l => l.File.EndsWith("QuickSettings/QsLook.cs", StringComparison.Ordinal) && l.Key == "LvBarInk");
        Assert.Contains(all, l => l.File.EndsWith("QuickSettings/QsLook.cs", StringComparison.Ordinal) && l.Key == "LgInk");
        // the themed helpers and the XAML references are seen
        Assert.Contains(all, l => l.How == "IslandsInk" && l.Need == Need.Themes);
        Assert.Contains(all, l => l.How == "QsPanel B / Tok");
        Assert.Contains(all, l => l.How == "{ThemeResource} in xaml");
        Assert.Contains(all, l => l.How == "{ThemeResource} in code");
        Assert.Contains(all, l => l.How == "LauncherSpec");
        Assert.Contains(all, l => l.How == "QsPanel L / LT");
    }

    [Fact]
    public void Every_helper_pattern_still_finds_a_lookup()
    {
        _ = Lookups.Value;
        var dead = HelperHits.Where(h => h.Value == 0).Select(h => h.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(dead.Count == 0, "these lookup patterns match nothing in Mantle.App any more (renamed helper? update the list):\n  " + string.Join("\n  ", dead));
    }
}
