using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mantle.Core.Features.LauncherIndex;

namespace Mantle.Core.Features.Search;

/// <summary>A search row: the index item (never copied, never mutated) and its score; null score = the empty query's list.</summary>
public sealed record SearchHit(LauncherItem Item, int? Score);

/// <summary>
/// lib/launcher-index.js#search and its scorers, ported line for line. The spec is Electron's own output:
/// tests/Mantle.Core.Tests/Search/LauncherGolden.g.cs holds search() run on a fixture index for every query
/// the Electron tests use plus ~1,200 more, and the native result must be the same rows in the same order
/// with the same scores. Every string test is ordinal, as JavaScript's are.
/// </summary>
public static class LauncherSearch
{
    public const int Limit = 12;
    public const int SecondaryPenalty = 40;

    static readonly Dictionary<string, int> KindOrder = new(StringComparer.Ordinal) { ["app"] = 0, ["command"] = 1, ["setting"] = 2, ["power"] = 3, ["url"] = 4, ["file"] = 5 };
    static readonly Dictionary<string, int> GroupCap = new(StringComparer.Ordinal) { ["app"] = 5, ["command"] = 4, ["setting"] = 4, ["power"] = 6, ["url"] = 1 };

    static readonly Regex Http = new(@"^https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Domain = new(@"^[a-z0-9][a-z0-9.\-]*\.[a-z]{2,}(?:[/:?#].*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// http(s) always; otherwise one token with a dot and a 2+ letter suffix; a phrase is never a URL.
    public static bool LooksLikeUrl(string q)
    {
        if (Http.IsMatch(q)) return true;
        if (Js.HasSpace(q)) return false;
        return Domain.IsMatch(q);
    }

    public static string NormalizeUrl(string q) => Http.IsMatch(q) ? q : "https://" + q;

    /// Is q a subsequence of s? Close-together, early matches score higher; -1 = no match.
    public static int SubsequenceScore(string s, string q)
    {
        int si = 0, first = -1, last = -1;
        for (int qi = 0; qi < q.Length; qi++)
        {
            int at = s.IndexOf(q[qi], si);
            if (at == -1) return -1;
            if (first == -1) first = at;
            last = at;
            si = at + 1;
        }
        int spread = last - first + 1;
        return Math.Max(0, 200 - (spread - q.Length) * 6 - first * 2);
    }

    public static int ScorePower(LauncherItem item, string q)
    {
        var words = new List<string> { Js.Lower(item.Name) };
        if (item.PowerWords != null) words.AddRange(item.PowerWords);
        foreach (var w in words) if (w == q) return 1000;
        if (q.Length < 3) return -1;
        int best = -1;
        foreach (var w in words)
        {
            if (w.StartsWith(q, StringComparison.Ordinal)) best = Math.Max(best, 760 - Math.Min(60, w.Length - q.Length));
            else if (Js.SplitSpace(w).Any(part => part.StartsWith(q, StringComparison.Ordinal))) best = Math.Max(best, 690);
        }
        if (best >= 0) return best;
        string group = Regex.Split(Js.Lower(item.Subtitle ?? ""), "[^a-z]+")[0];
        if (PowerRows.GroupWords.Contains(group) && group.StartsWith(q, StringComparison.Ordinal)) return 650;
        return -1;
    }

    public static int ScoreSettingKeywords(LauncherItem item, string q)
    {
        string? text = item.Keywords;
        if (string.IsNullOrEmpty(text)) return -1;
        string hay = " " + text + " ";
        if (hay.Contains(" " + q + " ", StringComparison.Ordinal)) return 880;
        if (q.Length >= 2 && hay.Contains(" " + q, StringComparison.Ordinal)) return 760;
        if (q.Length >= 3)
        {
            var typed = Js.SplitSpace(q).Where(t => t.Length > 0).ToArray();
            var words = text.Split(' ');
            if (typed.Length > 1 && typed.All(t => words.Any(w => w.StartsWith(t, StringComparison.Ordinal)))) return 700;
        }
        return -1;
    }

    public static int ScoreItem(LauncherItem item, string q)
    {
        if (item.Kind == "power") return ScorePower(item, q);
        int s = (item.Kind == "setting" || !string.IsNullOrEmpty(item.MantleApp)) && !string.IsNullOrEmpty(item.Keywords)
            ? Math.Max(ScoreByName(item.Name, item.Subtitle, q), ScoreSettingKeywords(item, q))
            : ScoreByName(item.Name, item.Subtitle, q);
        // a Mantle stand-in named differently from the app it replaces also scores as that name
        if (!string.IsNullOrEmpty(item.StandsFor) && !string.IsNullOrEmpty(item.MantleApp)) s = Math.Max(s, ScoreByName(item.StandsFor, "", q));
        return item.Secondary && s >= 0 ? Math.Max(0, s - SecondaryPenalty) : s;
    }

    public static int ScoreByName(string itemName, string? subtitle, string q)
    {
        string name = Js.Lower(itemName);
        string sub = Js.Lower(subtitle ?? "");
        if (name == q) return 1000;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 900 - Math.Min(80, name.Length - q.Length);
        foreach (var w in Js.AlnumWords(name)) if (w.StartsWith(q, StringComparison.Ordinal)) return 800 - Math.Min(80, name.Length - q.Length);
        if (sub == q) return 520;
        if (sub.StartsWith(q, StringComparison.Ordinal)) return 500;
        foreach (var w in Js.AlnumWords(sub)) if (w.StartsWith(q, StringComparison.Ordinal)) return 460;
        int seq = SubsequenceScore(name, q);
        if (seq >= 0) return 200 + seq;
        if (sub.Contains(q, StringComparison.Ordinal)) return 150;
        return -1;
    }

    static int Kind(string k) => KindOrder.TryGetValue(k, out var o) ? o : 9;

    /// search(q, items): at most 12 rows, shared out by group; a typed URL leads with an "Open" row.
    /// <paramref name="extra"/> = rows fetched fresh per search (window layouts), appended as Electron does.
    public static List<SearchHit> Search(string? q, IReadOnlyList<LauncherItem> items, IEnumerable<LauncherItem>? extra = null)
    {
        var list = extra == null ? items : items.Concat(extra).ToList();
        string raw = (q ?? "").Trim();
        string query = Js.Lower(raw);
        if (query.Length == 0)
        {
            // the user's own commands first, then apps alphabetically to fill twelve; never a power row
            var cmds = list.Where(i => i.Kind == "command").ToList();
            var apps = list.Where(i => i.Kind == "app").ToList();
            return cmds.Take(Limit).Concat(apps.Take(Math.Max(0, Limit - Math.Min(cmds.Count, Limit)))).Select(i => new SearchHit(i, null)).ToList();
        }
        var scored = new List<(LauncherItem It, int S)>();
        foreach (var it in list)
        {
            int s = ScoreItem(it, query);
            if (s >= 0) scored.Add((it, s));
        }
        var sorted = Js.StableSort(scored, (a, b) =>
        {
            int c = b.S - a.S;
            if (c != 0) return c;
            c = Kind(a.It.Kind) - Kind(b.It.Kind);
            if (c != 0) return c;
            c = Js.LocaleCompare(a.It.Name, b.It.Name);
            if (c != 0) return c;
            return Js.LocaleCompare(a.It.Id, b.It.Id);
        });
        var outList = sorted.Select(x => new SearchHit(x.It, x.S)).ToList();
        if (LooksLikeUrl(query))
        {
            string url = NormalizeUrl(raw);
            outList.Insert(0, new SearchHit(new LauncherItem
            {
                Id = Js.StableId("url", url), Kind = "url", Name = "Open " + raw, Subtitle = "in your browser", Path = url,
            }, 1100));
        }
        return CapByGroup(outList);
    }

    public static List<SearchHit> CapByGroup(List<SearchHit> rows)
    {
        var used = new Dictionary<string, int>(StringComparer.Ordinal);
        var outList = new List<SearchHit>();
        foreach (var r in rows)
        {
            int cap = GroupCap.TryGetValue(r.Item.Kind, out var c) ? c : Limit;
            used.TryGetValue(r.Item.Kind, out int n);
            if (n >= cap) continue;
            used[r.Item.Kind] = n + 1;
            outList.Add(r);
            if (outList.Count >= Limit) break;
        }
        return outList;
    }
}
