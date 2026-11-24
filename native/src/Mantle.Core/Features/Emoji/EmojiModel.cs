using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Emoji;

/// <summary>One cell of the picker (ui/emoji/model.js mkItem). <see cref="Words"/> are the name's words first, then the keywords'.</summary>
public sealed record EmojiItem(string Ch, string Name, IReadOnlyList<int>? Plan, string Group, string LName,
    IReadOnlyList<string> Kw, IReadOnlyList<string> Words, int NameWords, string? Tab = null, bool Fixed = false);

/// <summary>One section of a tab: a data group, a symbols or kaomoji group, or the recents.</summary>
public sealed record EmojiSection(string Id, string Name, string Glyph, IReadOnlyList<EmojiItem> Items);

/// <summary>Where the caret (bottom-left, H = its height) or the pointer is, in DIP.</summary>
public readonly record struct EmojiAnchor(double X, double Y, double H = 0);

public readonly record struct EmojiRect(double X, double Y, double Width, double Height);

/// <summary>A key as the sidecar's watch (or the page) hands it over: the key name, Shift, and the typed text.</summary>
public readonly record struct EmojiKey(string? Key, bool Shift = false, string? Text = null);

/// <summary>What a key does (model.js keyAction): Do = insert | close | move | type | back | clear | tab | none.</summary>
public readonly record struct EmojiKeyAction(string Do, bool Close = false, bool Back = false, string? Key = null, string? Text = null);

/// <summary>
/// The bundled catalogue as the page uses it (model.js catalogue): the Emoji tab's nine data groups from Electron's own
/// ui/emoji/emoji-data.js (embedded unchanged by Mantle.Core.csproj, Unicode License V3), and the Symbols and Kaomoji tabs
/// from model.js (EmojiTabs.g.cs, made by native/tools/parity/clipemoji/make-golden.cjs).
/// </summary>
public sealed class EmojiCatalog
{
    public const string ResourceName = "Mantle.Core.Emoji.emoji-data.js";

    public IReadOnlyList<EmojiSection> Emoji { get; }
    public IReadOnlyList<EmojiSection> Symbols { get; }
    public IReadOnlyList<EmojiSection> Kaomoji { get; }
    /// Emoji-tab items by their exact text (model.js byChar).
    public IReadOnlyDictionary<string, EmojiItem> ByChar { get; }

    EmojiCatalog(List<EmojiSection> emoji, List<EmojiSection> symbols, List<EmojiSection> kaomoji, Dictionary<string, EmojiItem> byChar)
    {
        Emoji = emoji; Symbols = symbols; Kaomoji = kaomoji; ByChar = byChar;
    }

    static readonly Lazy<EmojiCatalog> Bundled = new(() => FromJson(BundledDataJson(), EmojiTabs.Json));

    /// The catalogue built from the embedded data (built once).
    public static EmojiCatalog Load() => Bundled.Value;

    /// The DATA object literal of ui/emoji/emoji-data.js (one line: <c>  var DATA = {...};</c>).
    internal static string BundledDataJson()
    {
        using var s = typeof(EmojiCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("the emoji data is not embedded in Mantle.Core (" + ResourceName + ")");
        using var r = new StreamReader(s, Encoding.UTF8);
        return DataJsonOf(r.ReadToEnd());
    }

    internal static string DataJsonOf(string js)
    {
        const string Mark = "var DATA = ";
        int at = js.IndexOf(Mark, StringComparison.Ordinal);
        if (at < 0) throw new InvalidDataException("ui/emoji/emoji-data.js has no 'var DATA = ' line");
        int start = at + Mark.Length;
        int end = js.IndexOf('\n', start);
        string line = (end < 0 ? js.Substring(start) : js.Substring(start, end - start)).TrimEnd();
        return line.EndsWith(';') ? line.Substring(0, line.Length - 1) : line;
    }

    /// <summary>model.js catalogue(data) plus the two fixed tabs. <paramref name="tabsJson"/> = <c>{symbols, kaomoji}</c>
    /// as EmojiTabs.g.cs holds them (items <c>[ch, name, keywords joined by |]</c>).</summary>
    public static EmojiCatalog FromJson(string dataJson, string tabsJson)
    {
        var emoji = new List<EmojiSection>();
        var byChar = new Dictionary<string, EmojiItem>(StringComparer.Ordinal);
        using (var doc = JsonDocument.Parse(dataJson))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in groups.EnumerateArray())
                {
                    string id = Str(g, "id") ?? "";
                    var items = new List<EmojiItem>();
                    if (g.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array)
                        foreach (var r in rows.EnumerateArray())
                        {
                            if (r.ValueKind != JsonValueKind.Array) continue;
                            var it = EmojiModel.MkItem(At(r, 0) ?? "", At(r, 1) ?? "", At(r, 2), r.GetArrayLength() > 3 ? EmojiModel.PlanOf(r[3]) : null, id);
                            items.Add(it);
                            byChar[it.Ch] = it;
                        }
                    string name = EmojiModel.GroupLabel.TryGetValue(id, out var label) ? label : Str(g, "name") ?? "";
                    emoji.Add(new EmojiSection(id, name, EmojiModel.GroupGlyph.TryGetValue(id, out var glyph) ? glyph : "", items));
                }
            }
        }
        List<EmojiSection> symbols, kaomoji;
        using (var doc = JsonDocument.Parse(tabsJson))
        {
            symbols = Tab(doc.RootElement, "symbols");
            kaomoji = Tab(doc.RootElement, "kaomoji");
        }
        return new EmojiCatalog(emoji, symbols, kaomoji, byChar);
    }

    static List<EmojiSection> Tab(JsonElement root, string tab)
    {
        var list = new List<EmojiSection>();
        if (!root.TryGetProperty(tab, out var secs) || secs.ValueKind != JsonValueKind.Array) return list;
        foreach (var s in secs.EnumerateArray())
        {
            string id = Str(s, "id") ?? "";
            var items = new List<EmojiItem>();
            if (s.TryGetProperty("items", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var r in rows.EnumerateArray())
                    items.Add(EmojiModel.MkItem(At(r, 0) ?? "", At(r, 1) ?? "", At(r, 2), null, id) with { Tab = tab });
            list.Add(new EmojiSection(id, Str(s, "name") ?? "", "", items));
        }
        return list;
    }

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static string? At(JsonElement a, int i) => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() : null;
}

/// <summary>
/// THE EMOJI PICKER'S PURE LOGIC: a port of ui/emoji/model.js (skin tones, the catalogue's items, search and ranking,
/// recents, the grid's keys, placement). The spec is Electron's own output: EmojiModelGoldenTests replays the answers
/// native/tools/parity/clipemoji/make-golden.cjs took from model.js. Nothing here logs an emoji.
/// </summary>
public static class EmojiModel
{
    public const double PanelW = 360, PanelH = 424, Margin = 18;
    public const int Cols = 8;
    public const int RecentMax = 32;     // four rows
    public const int SearchLimit = 160;
    public static readonly IReadOnlyList<int> Tones = new[] { 0x1F3FB, 0x1F3FC, 0x1F3FD, 0x1F3FE, 0x1F3FF };
    public static readonly IReadOnlyList<string> ToneNames = new[] { "Default", "Light", "Medium-light", "Medium", "Medium-dark", "Dark" };
    const int Fe0f = 0xFE0F;

    public static readonly IReadOnlyDictionary<string, string> GroupGlyph = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["recent"] = "\U0001F552", ["smileys"] = "\U0001F600", ["people"] = "\U0001F44B", ["nature"] = "\U0001F43B", ["food"] = "\U0001F354",
        ["travel"] = "\U0001F697", ["activities"] = "\U000026BD", ["objects"] = "\U0001F4A1", ["symbols"] = "\U00002764\U0000FE0F", ["flags"] = "\U0001F3C1",
    };

    public static readonly IReadOnlyDictionary<string, string> GroupLabel = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["recent"] = "Most recently used", ["smileys"] = "Smileys & emotion", ["people"] = "People & body", ["nature"] = "Animals & nature",
        ["food"] = "Food & drink", ["travel"] = "Travel & places", ["activities"] = "Activities", ["objects"] = "Objects", ["symbols"] = "Symbols", ["flags"] = "Flags",
    };

    /// JavaScript's Math.round (halves go up, also for negatives).
    internal static double JsRound(double v) => Math.Floor(v + 0.5);

    /// The string's code points as Array.from gives them (a lone surrogate is one element).
    internal static List<string> CodePoints(string s)
    {
        var o = new List<string>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { o.Add(s.Substring(i, 2)); i++; }
            else o.Add(s[i].ToString());
        }
        return o;
    }

    static int Cp(string c) => c.Length == 2 && char.IsSurrogatePair(c[0], c[1]) ? char.ConvertToUtf32(c[0], c[1]) : c[0];

    // ---- skin tones ---------------------------------------------------------------------------

    /// <summary><paramref name="s"/> with <paramref name="tone"/> (a modifier code point) after the code points at
    /// <paramref name="plan"/>; an FE0F right after a toned code point goes (the tone takes its place).</summary>
    public static string WithTone(string s, IReadOnlyList<int> plan, int tone)
    {
        var seq = CodePoints(s);
        var sb = new StringBuilder();
        bool toned = false;
        for (int i = 0; i < seq.Count; i++)
        {
            if (Cp(seq[i]) == Fe0f && toned) { toned = false; continue; }
            toned = false;
            sb.Append(seq[i]);
            if (plan.Contains(i)) { sb.Append(char.ConvertFromUtf32(tone)); toned = true; }
        }
        return sb.ToString();
    }

    /// <summary>The plan a data item carries: 1 = after the first code point; "0,4" = those; anything else = no tone.</summary>
    public static IReadOnlyList<int>? PlanOf(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Number && raw.GetDouble() == 1) return new[] { 0 };
        if (raw.ValueKind == JsonValueKind.String && raw.GetString() is { Length: > 0 } s)
            return s.Split(',').Select(p => JsNumber(p) is double d && d >= 0 && Math.Floor(d) == d && d < int.MaxValue ? (int)d : -1).ToArray();
        return null;
    }

    /// <summary>What the cell shows and what is typed: the item in skin tone <paramref name="tone"/> (0 = default, 1..5).</summary>
    public static string Display(EmojiItem item, int tone)
    {
        if (item.Plan == null || item.Plan.Count == 0 || tone < 1 || tone > 5) return item.Ch;
        return WithTone(item.Ch, item.Plan, Tones[tone - 1]);
    }

    /// <summary>model.js clampTone(t): Number(t) when it is a whole 0..5, else 0 (a stored tone of any other shape is the default).</summary>
    public static int ClampTone(JsonElement t)
    {
        double d = t.ValueKind switch
        {
            JsonValueKind.Number => t.GetDouble(),
            JsonValueKind.String => JsNumber(t.GetString() ?? "") ?? double.NaN,
            JsonValueKind.True => 1,
            JsonValueKind.False or JsonValueKind.Null => 0,
            _ => double.NaN,
        };
        return ClampTone(d);
    }

    public static int ClampTone(double t) => t >= 0 && t <= 5 && Math.Floor(t) == t ? (int)t : 0;

    /// JavaScript's Number(string) for the shapes a stored value can have: trimmed, "" = 0, else a decimal (null = NaN).
    static double? JsNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        return double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    /// <summary>The tone modifiers taken out again, so a recent "thumbs up, medium" is named from the base.</summary>
    public static string StripTone(string ch)
    {
        var sb = new StringBuilder();
        var seq = CodePoints(ch);
        for (int i = 0; i < seq.Count; i++)
        {
            int c = Cp(seq[i]);
            if (c >= 0x1F3FB && c <= 0x1F3FF)
            {
                // the FE0F the tone replaced comes back when the base has one (U+261D, U+270C ...)
                if (i == 1 && seq[0].Length == 1 && seq[0][0] >= (char)0x2600 && seq[0][0] <= (char)0x27BF) sb.Append((char)Fe0f);
                continue;
            }
            sb.Append(seq[i]);
        }
        return sb.ToString();
    }

    // ---- the catalogue's items ----------------------------------------------------------------

    static readonly Regex WordSplit = new(@"[\s:,.\-’'()]+", RegexOptions.CultureInvariant);

    internal static List<string> WordsOf(string name, string? kw)
    {
        string w = (name + " " + (kw ?? "").Replace('|', ' ')).ToLowerInvariant();
        return WordSplit.Split(w).Where(x => x.Length > 0).ToList();
    }

    internal static EmojiItem MkItem(string ch, string name, string? kw, IReadOnlyList<int>? plan, string group) =>
        new(ch, name, plan, group, name.ToLowerInvariant(),
            string.IsNullOrEmpty(kw) ? Array.Empty<string>() : kw.ToLowerInvariant().Split('|'),
            WordsOf(name, kw), WordsOf(name, "").Count);

    // ---- search -------------------------------------------------------------------------------

    /// <summary>The most-used emoji, most first (Unicode's "Emoji Frequency" list, model.js POPULAR): only a tie-break.</summary>
    public static IReadOnlyList<string> Popular => PopularList.Value;

    static readonly Lazy<IReadOnlyList<string>> PopularList = new(() =>
    {
        using var doc = JsonDocument.Parse(EmojiTabs.Json);
        string raw = doc.RootElement.TryGetProperty("popular", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var list = new List<string>();
        foreach (var c in CodePoints(raw))
        {
            if (c.Length == 1 && c[0] == (char)Fe0f && list.Count > 0) list[^1] += c;   // an FE0F joins the emoji before it
            else list.Add(c);
        }
        return list;
    });

    public static string Norm(string? q) => Regex.Replace((q ?? "").ToLowerInvariant(), @"\s+", " ").Trim();

    /// <summary>One item's score for the query tokens, 0 = no match. Every token must start a word of the name or a
    /// keyword, with a looser in-word rule for tokens of 3+ letters; name words beat keywords.</summary>
    static double Score(EmojiItem it, string q, string[] toks, IReadOnlyList<string> popular)
    {
        if (it.LName == q) return 1000;
        double s = 0;
        bool weak = true;
        foreach (var tok in toks)
        {
            double best = 0;
            for (int w = 0; w < it.Words.Count; w++)
            {
                string word = it.Words[w];
                bool inName = w < it.NameWords;
                if (word == tok) best = Math.Max(best, inName ? 200 : 120);
                else if (word.StartsWith(tok, StringComparison.Ordinal)) best = Math.Max(best, inName ? 80 : 40);
                else if (tok.Length >= 3 && word.IndexOf(tok, StringComparison.Ordinal) > 0) best = Math.Max(best, 10);
            }
            if (best == 0) return 0;
            if (best > 10) weak = false;
            s += best;
        }
        if (it.LName.StartsWith(q, StringComparison.Ordinal)) s += 40;              // the name starts with what was typed
        if (it.NameWords >= 1 && it.NameWords <= it.Words.Count && it.Words[it.NameWords - 1] == toks[^1]) s += 30;   // ...or ends with it
        int pop = IndexOf(popular, it.Ch);
        if (pop >= 0 && !weak) s += 25 - pop * 0.2;                                 // what people pick most
        return s - it.NameWords * 3;                                                // then the plainer name
    }

    static int IndexOf(IReadOnlyList<string> list, string v)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == v) return i;
        return -1;
    }

    /// <summary>Items of <paramref name="sections"/> matching <paramref name="query"/>, best first (ties keep Unicode's order),
    /// at most <paramref name="limit"/> (0 = 160).</summary>
    public static IReadOnlyList<EmojiItem> Search(IReadOnlyList<EmojiSection> sections, string? query, int limit = 0)
    {
        string q = Norm(query);
        if (q.Length == 0) return Array.Empty<EmojiItem>();
        var toks = q.Split(' ');
        var popular = Popular;
        var hits = new List<(EmojiItem It, double Sc, int At)>();
        int at = 0;
        foreach (var sec in sections)
            foreach (var it in sec.Items)
            {
                double sc = Score(it, q, toks, popular);
                if (sc != 0) hits.Add((it, sc, at));
                at++;
            }
        return hits.OrderByDescending(h => h.Sc).ThenBy(h => h.At).Take(limit > 0 ? limit : SearchLimit).Select(h => h.It).ToList();
    }

    // ---- recents ------------------------------------------------------------------------------

    /// The string entries of a stored list (null for anything that is not a string), in order; not a list = none.
    internal static IEnumerable<string?> Entries(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in list.EnumerateArray()) yield return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    /// <summary>Recents after <paramref name="ch"/> was typed: newest first, no repeats, at most 32; junk dropped.</summary>
    public static List<string> Remember(IEnumerable<string?>? list, string ch)
    {
        var o = new List<string> { ch };
        if (list == null) return o;
        foreach (var e in list)
        {
            if (o.Count >= RecentMax) break;
            if (!string.IsNullOrEmpty(e) && e != ch) o.Add(e);
        }
        return o;
    }

    public static List<string> Remember(JsonElement list, string ch) => Remember(Entries(list), ch);

    /// <summary>The stored recents as items: shown as typed (with its tone), named from the base emoji.</summary>
    public static List<EmojiItem> RecentItems(IEnumerable<string?>? list, EmojiCatalog cat)
    {
        var o = new List<EmojiItem>();
        if (list == null) return o;
        foreach (var ch in list)
        {
            if (o.Count >= RecentMax) break;
            if (string.IsNullOrEmpty(ch) || ch.Length > 64) continue;
            EmojiItem? b = cat.ByChar.TryGetValue(ch, out var x) ? x : cat.ByChar.TryGetValue(StripTone(ch), out var y) ? y : null;
            o.Add(new EmojiItem(ch, b != null ? b.Name : ch, null, "recent", "", Array.Empty<string>(), Array.Empty<string>(), 0, null, Fixed: true));
        }
        return o;
    }

    public static List<EmojiItem> RecentItems(JsonElement list, EmojiCatalog cat) => RecentItems(Entries(list), cat);

    // ---- keyboard -----------------------------------------------------------------------------

    /// <summary>The grid's rows: each section starts a new row of up to <paramref name="cols"/> cells (flat indices).</summary>
    public static List<int[]> Layout(IReadOnlyList<int> counts, int cols)
    {
        if (cols < 1) cols = 1;
        var rows = new List<int[]>();
        int at = 0;
        foreach (int n in counts)
        {
            for (int i = 0; i < n; i += cols)
            {
                var row = new int[Math.Min(n, i + cols) - i];
                for (int k = i; k < Math.Min(n, i + cols); k++) row[k - i] = at + k;
                rows.Add(row);
            }
            at += n;
        }
        return rows;
    }

    /// <summary>The new selection (a flat index) after <paramref name="key"/> in the grid laid out as <paramref name="rows"/>
    /// (page = rows per PageUp/PageDown, 0 = 5).</summary>
    public static int Move(IReadOnlyList<int[]> rows, int sel, string? key, int page = 0)
    {
        if (rows.Count == 0) return -1;
        int[] lastRow = rows[rows.Count - 1];
        int total = lastRow[lastRow.Length - 1] + 1;
        if (sel < 0 || sel >= total) return 0;
        int r = 0;
        while (r < rows.Count - 1 && rows[r][^1] < sel) r++;
        int col = sel - rows[r][0];
        int To(int row) { row = Math.Max(0, Math.Min(rows.Count - 1, row)); return rows[row][Math.Min(col, rows[row].Length - 1)]; }
        int p = page != 0 ? page : 5;
        switch (key)
        {
            case "ArrowRight": return Math.Min(total - 1, sel + 1);
            case "ArrowLeft": return Math.Max(0, sel - 1);
            case "ArrowDown": return To(r + 1);
            case "ArrowUp": return To(r - 1);
            case "PageDown": return To(r + p);
            case "PageUp": return To(r - p);
            case "Home": return 0;
            case "End": return total - 1;
        }
        return sel;
    }

    static readonly HashSet<string> MoveKeys = new(StringComparer.Ordinal)
        { "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "PageUp", "PageDown", "Home", "End" };

    /// <summary>What a key does in the panel. Enter inserts and stays open (Shift+Enter closes), the other way round with
    /// "Close after picking" on.</summary>
    public static EmojiKeyAction KeyAction(EmojiKey k, bool closeAfter)
    {
        string? key = k.Key;
        if (key == "Escape") return new EmojiKeyAction("close");
        if (key == "Enter") return new EmojiKeyAction("insert", Close: closeAfter != k.Shift);
        if (key == "Backspace") return new EmojiKeyAction("back");
        if (key == "Delete") return new EmojiKeyAction("clear");
        if (key == "Tab") return new EmojiKeyAction("tab", Back: k.Shift);
        if (key != null && MoveKeys.Contains(key)) return new EmojiKeyAction("move", Key: key);
        if (k.Text is { Length: 1 } t && t[0] >= ' ') return new EmojiKeyAction("type", Text: t);
        return new EmojiKeyAction("none");
    }

    // ---- placement ----------------------------------------------------------------------------

    /// <summary>The window rectangle (DIP, the panel plus its margin) near <paramref name="anchor"/>: just under the caret
    /// line, or above it when there is no room under; always inside <paramref name="work"/>. No anchor (or one that is
    /// not a number) = centred. <paramref name="w"/>/<paramref name="h"/> = 0 take the panel's own size.</summary>
    public static EmojiRect Place(EmojiAnchor? anchor, EmojiRect work, double w = 0, double h = 0)
    {
        if (w == 0) w = PanelW + 2 * Margin;
        if (h == 0) h = PanelH + 2 * Margin;
        if (anchor is not EmojiAnchor a || !double.IsFinite(a.X) || !double.IsFinite(a.Y))
            return new EmojiRect(JsRound(work.X + (work.Width - w) / 2), JsRound(work.Y + (work.Height - h) / 2), w, h);
        double ax = JsRound(a.X), ay = JsRound(a.Y);
        double ah = a.H > 0 ? a.H : 0;
        double x = ax - Margin;
        double y = ay + 6 - Margin;                                          // the pane's top 6 DIP under the caret's foot
        if (y + h > work.Y + work.Height) y = ay - ah - 6 - h + Margin;      // above the caret line
        x = Math.Max(work.X, Math.Min(x, work.X + work.Width - w));
        y = Math.Max(work.Y, Math.Min(y, work.Y + work.Height - h));
        return new EmojiRect(JsRound(x), JsRound(y), w, h);
    }
}
