using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mantle.Core.Features.LauncherIndex;

/// <summary>
/// One row the launcher can open: the native form of an item in lib/launcher-index.js's <c>build().items</c>.
/// Field for field the same (id, kind, name, subtitle, path, keywords, secondary, mantleApp, standsFor,
/// windowsPath, aumid, glyph, confirm); the icon is <see cref="IconKey"/> (a Mantle icon by name) or
/// <see cref="Icon"/> (a picture the platform resolved), never both. Immutable: a search returns hits that
/// point at these objects and carry their own score, so the cached index is never mutated by a keystroke.
/// </summary>
public sealed record LauncherItem
{
    public required string Id { get; init; }
    /// app | command | setting | power | url (file and calc rows are the launcher page's, not the index's).
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public string Subtitle { get; init; } = "";
    /// The .lnk path, the ms-settings: / mantle: uri, the command name, the power action, the URL.
    public string Path { get; init; } = "";
    /// Settings pages and Mantle stand-ins: one lowercased string of extra words (Electron's <c>keywords</c> string).
    public string? Keywords { get; init; }
    /// Power rows: the words that reach them (Electron's <c>keywords</c> array).
    public IReadOnlyList<string>? PowerWords { get; init; }
    /// Windows' own Settings / Task Manager / File Explorer / Media Player: always just under Mantle's.
    public bool Secondary { get; init; }
    /// settings | taskmgr | files | player: Mantle's app in Windows' place.
    public string? MantleApp { get; init; }
    /// The Windows name a Mantle stand-in also scores as (Mantle Player stands for Media Player).
    public string? StandsFor { get; init; }
    /// What the stand-in replaced (its .lnk or shell:AppsFolder uri): opened when Mantle's is not running.
    public string? WindowsPath { get; init; }
    /// A Store app's AUMID.
    public string? Aumid { get; init; }
    public string? Glyph { get; init; }
    /// Power rows that ask first: the armed line ("Press Enter again to restart").
    public string? Confirm { get; init; }
    /// A Mantle icon by name (<c>mantle:settings</c>, <c>mantle:taskmgr</c>, <c>mantle:files</c>, <c>mantle:player</c>).
    public string? IconKey { get; init; }
    /// A picture the platform resolved for this path (filled in later; null = letter tile or glyph).
    public string? Icon { get; init; }
}

/// <summary>One index source's report (lib/launcher-index.js <c>sources[]</c>): an empty list is never silent.</summary>
public sealed record IndexSource(string Name, int Count, string Status, string Reason);

/// <summary>
/// The JavaScript behaviours the ranking depends on, in one place, so "the same order as Electron" has one
/// definition: ICU collation for <c>localeCompare</c>, a stable sort (V8's), JS lowercasing and the ids.
/// Every string test elsewhere is ordinal (JS <c>startsWith</c>/<c>includes</c>/<c>indexOf</c> are).
/// </summary>
public static class Js
{
    // Node's String.prototype.localeCompare with no locale is ICU's default collation at tertiary strength;
    // .NET on Windows 10 1903+ collates through the same ICU. en-US = root for everything the index holds.
    static readonly CompareInfo Collator = CultureInfo.GetCultureInfo("en-US").CompareInfo;

    /// <c>a.localeCompare(b)</c>.
    public static int LocaleCompare(string a, string b) => Math.Sign(Collator.Compare(a, b, CompareOptions.None));

    /// <c>s.toLowerCase()</c>.
    public static string Lower(string s) => s.ToLowerInvariant();

    /// Array.prototype.sort with a comparator: stable (equal elements keep their order), as V8's TimSort is.
    public static List<T> StableSort<T>(IEnumerable<T> items, Comparison<T> cmp)
    {
        var list = new List<(T It, int At)>();
        int i = 0;
        foreach (var it in items) list.Add((it, i++));
        list.Sort((x, y) => { int c = cmp(x.It, y.It); return c != 0 ? c : x.At.CompareTo(y.At); });
        var outList = new List<T>(list.Count);
        foreach (var x in list) outList.Add(x.It);
        return outList;
    }

    /// lib/launcher-index.js#stableId: kind + ':' + the first 12 hex of sha1(key lowercased).
    public static string StableId(string kind, string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(Lower(key)));
        return kind + ":" + Convert.ToHexStringLower(hash).Substring(0, 12);
    }

    /// <c>s.split(/[^a-z0-9]+/).filter(Boolean)</c>.
    public static List<string> AlnumWords(string s)
    {
        var words = new List<string>();
        int start = -1;
        for (int i = 0; i <= s.Length; i++)
        {
            bool word = i < s.Length && ((s[i] >= 'a' && s[i] <= 'z') || (s[i] >= '0' && s[i] <= '9'));
            if (word) { if (start < 0) start = i; }
            else if (start >= 0) { words.Add(s.Substring(start, i - start)); start = -1; }
        }
        return words;
    }

    /// <c>s.split(/\s+/)</c> (keeps empty ends, as JS does).
    public static string[] SplitSpace(string s) => System.Text.RegularExpressions.Regex.Split(s, @"\s+");

    /// <c>/\s/.test(s)</c>.
    public static bool HasSpace(string s) { foreach (var c in s) if (char.IsWhiteSpace(c)) return true; return false; }
}
