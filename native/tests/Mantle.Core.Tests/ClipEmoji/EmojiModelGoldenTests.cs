using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Features.Emoji;
using Xunit;

namespace Mantle.Core.Tests.ClipEmoji;

/// <summary>
/// The emoji picker's pure logic against Electron's own answers (EMO-006, 007, 014..018, 025, 028, 029, 031..033, 036, 041, 042, 044):
/// ClipEmojiGolden.g.cs is ui/emoji/model.js run over Electron's own data by native/tools/parity/clipemoji/make-golden.cjs.
/// The catalogue is the embedded ui/emoji/emoji-data.js, so a data change in Electron shows here as drift.
/// </summary>
public sealed class EmojiModelGoldenTests
{
    static readonly JsonElement G = JsonDocument.Parse(ClipEmojiGolden.Json).RootElement.GetProperty("emoji");
    static readonly EmojiCatalog Cat = EmojiCatalog.Load();

    static string? S(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool B(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static double? D(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static List<string> Strs(JsonElement a) => a.EnumerateArray().Select(x => x.GetString()!).ToList();
    static List<int> Ints(JsonElement a) => a.EnumerateArray().Select(x => x.GetInt32()).ToList();
    static string Show(IEnumerable<string> s) => string.Join(" ", s.Select(x => string.Join(",", x.EnumerateRunes().Select(r => r.Value.ToString("X")))));

    IReadOnlyList<EmojiSection> TabOf(string tab) => tab switch { "emoji" => Cat.Emoji, "symbols" => Cat.Symbols, "kaomoji" => Cat.Kaomoji, _ => throw new ArgumentException(tab) };

    [Fact]
    public void Constants_match_electron()
    {
        var c = G.GetProperty("consts");
        Assert.Equal(c.GetProperty("PANEL_W").GetDouble(), EmojiModel.PanelW);
        Assert.Equal(c.GetProperty("PANEL_H").GetDouble(), EmojiModel.PanelH);
        Assert.Equal(c.GetProperty("MARGIN").GetDouble(), EmojiModel.Margin);
        Assert.Equal(c.GetProperty("COLS").GetInt32(), EmojiModel.Cols);
        Assert.Equal(c.GetProperty("RECENT_MAX").GetInt32(), EmojiModel.RecentMax);
        Assert.Equal(Ints(c.GetProperty("TONES")), EmojiModel.Tones.ToList());
        Assert.Equal(Strs(c.GetProperty("TONE_NAMES")), EmojiModel.ToneNames.ToList());
        foreach (var p in c.GetProperty("GROUP_LABEL").EnumerateObject()) Assert.Equal(p.Value.GetString(), EmojiModel.GroupLabel[p.Name]);
        foreach (var p in c.GetProperty("GROUP_GLYPH").EnumerateObject()) Assert.Equal(p.Value.GetString(), EmojiModel.GroupGlyph[p.Name]);
        Assert.Equal(c.GetProperty("GROUP_LABEL").EnumerateObject().Count(), EmojiModel.GroupLabel.Count);
    }

    [Fact]
    public void Catalogue_sections_match_electron_on_every_tab()
    {
        foreach (var tab in new[] { "emoji", "symbols", "kaomoji" })
        {
            var want = G.GetProperty("sections").GetProperty(tab).EnumerateArray().ToList();
            var got = TabOf(tab);
            Assert.Equal(want.Count, got.Count);
            for (int i = 0; i < want.Count; i++)
            {
                var w = want[i]; var s = got[i];
                Assert.Equal(S(w, "id"), s.Id);
                Assert.Equal(S(w, "name"), s.Name);
                Assert.Equal(S(w, "glyph") ?? "", s.Glyph);
                Assert.Equal(w.GetProperty("n").GetInt32(), s.Items.Count);
                Assert.Equal(Strs(w.GetProperty("first")), s.Items.Take(3).Select(x => x.Ch).ToList());
                var words = w.GetProperty("words").EnumerateArray().ToList();
                var nameWords = Ints(w.GetProperty("nameWords"));
                for (int k = 0; k < words.Count; k++)
                {
                    Assert.Equal(Strs(words[k]), s.Items[k].Words.ToList());
                    Assert.Equal(nameWords[k], s.Items[k].NameWords);
                }
            }
        }
        Assert.Equal(9, Cat.Emoji.Count);   // EMO-044: nine data groups, in Unicode order
    }

    [Fact]
    public void Search_ranks_like_electron_query_for_query_on_every_tab()
    {
        int n = 0;
        foreach (var c in G.GetProperty("search").EnumerateArray())
        {
            string q = c.GetProperty("q").GetString()!;
            string tab = c.GetProperty("tab").GetString()!;
            int limit = (int)(D(c, "limit") ?? 0);
            var all = EmojiModel.Search(TabOf(tab), q, limit);
            var want = Strs(c.GetProperty("top"));
            var got = all.Take(24).Select(i => i.Ch).ToList();
            Assert.True(c.GetProperty("n").GetInt32() == all.Count, $"'{q}' on {tab}: want {c.GetProperty("n").GetInt32()} results, got {all.Count}");
            Assert.True(want.SequenceEqual(got), $"'{q}' on {tab}: want {Show(want)}, got {Show(got)}");
            n++;
        }
        Assert.True(n > 300);
        Assert.Empty(EmojiModel.Search(Cat.Emoji, "   "));
    }

    [Fact]
    public void Skin_tones_plans_and_clamp_match_electron()
    {
        foreach (var c in G.GetProperty("toned").EnumerateArray())
        {
            string ch = c.GetProperty("ch").GetString()!;
            var it = Cat.ByChar[ch];
            Assert.Equal(Ints(c.GetProperty("plan")), it.Plan!.ToList());
            var d = Strs(c.GetProperty("d"));
            for (int t = 1; t <= 5; t++) Assert.True(d[t - 1] == EmojiModel.Display(it, t), $"{Show(new[] { ch })} in tone {t}");
            Assert.Equal(ch, EmojiModel.Display(it, 0));
        }
        foreach (var c in G.GetProperty("strip").EnumerateArray())
            Assert.True(c.GetProperty("r").GetString() == EmojiModel.StripTone(c.GetProperty("s").GetString()!), "strip " + Show(new[] { c.GetProperty("s").GetString()! }));
        foreach (var c in G.GetProperty("clamp").EnumerateArray())
            Assert.True(c.GetProperty("r").GetInt32() == EmojiModel.ClampTone(c.GetProperty("v")), "clamp " + c.GetProperty("v").GetRawText());
        foreach (var c in G.GetProperty("plans").EnumerateArray())
        {
            var r = c.GetProperty("r");
            var got = EmojiModel.PlanOf(c.GetProperty("raw"));
            if (r.ValueKind == JsonValueKind.Null) Assert.Null(got);
            else Assert.Equal(Ints(r), got!.ToList());
        }
    }

    [Fact]
    public void Recents_remember_and_name_like_electron()
    {
        foreach (var c in G.GetProperty("remember").EnumerateArray())
            Assert.Equal(Strs(c.GetProperty("r")), EmojiModel.Remember(c.GetProperty("l"), c.GetProperty("ch").GetString()!));
        foreach (var c in G.GetProperty("recents").EnumerateArray())
        {
            var want = c.GetProperty("r").EnumerateArray().Select(x => (S(x, "ch"), S(x, "name"))).ToList();
            var got = EmojiModel.RecentItems(c.GetProperty("l"), Cat).Select(x => ((string?)x.Ch, (string?)x.Name)).ToList();
            Assert.Equal(want, got);
        }
    }

    [Fact]
    public void Grid_layout_moves_and_keys_match_electron()
    {
        foreach (var c in G.GetProperty("layouts").EnumerateArray())
        {
            var rows = EmojiModel.Layout(Ints(c.GetProperty("counts")), c.GetProperty("cols").GetInt32());
            var want = c.GetProperty("rows").EnumerateArray().Select(Ints).ToList();
            Assert.Equal(want.Count, rows.Count);
            for (int i = 0; i < want.Count; i++) Assert.Equal(want[i], rows[i].ToList());
        }
        foreach (var c in G.GetProperty("moves").EnumerateArray())
        {
            var rows = EmojiModel.Layout(Ints(c.GetProperty("counts")), c.GetProperty("cols").GetInt32());
            int got = EmojiModel.Move(rows, c.GetProperty("sel").GetInt32(), c.GetProperty("key").GetString(), c.GetProperty("page").GetInt32());
            Assert.True(c.GetProperty("r").GetInt32() == got, $"move {c.GetRawText()}: got {got}");
        }
        foreach (var c in G.GetProperty("keys").EnumerateArray())
        {
            var k = c.GetProperty("k");
            var got = EmojiModel.KeyAction(new EmojiKey(S(k, "key"), B(k, "shift"), S(k, "text")), c.GetProperty("closeAfter").GetBoolean());
            var r = c.GetProperty("r");
            string where = $"key {k.GetRawText()} (closeAfter {c.GetProperty("closeAfter").GetBoolean()})";
            Assert.True(S(r, "do") == got.Do, where + ": do " + got.Do);
            Assert.True(B(r, "close") == got.Close, where + ": close");
            Assert.True(B(r, "back") == got.Back, where + ": back");
            Assert.True(S(r, "key") == got.Key, where + ": key");
            Assert.True(S(r, "text") == got.Text, where + ": text");
        }
    }

    [Fact]
    public void Placement_matches_electron_with_and_without_an_anchor()
    {
        foreach (var c in G.GetProperty("place").EnumerateArray())
        {
            var a = c.GetProperty("a");
            EmojiAnchor? anchor = a.ValueKind != JsonValueKind.Object ? null
                : new EmojiAnchor(D(a, "x") ?? double.NaN, D(a, "y") ?? double.NaN, D(a, "h") ?? 0);
            var w = c.GetProperty("w");
            var work = new EmojiRect(D(w, "x")!.Value, D(w, "y")!.Value, D(w, "width")!.Value, D(w, "height")!.Value);
            var s = c.GetProperty("s");
            var got = s.ValueKind == JsonValueKind.Object ? EmojiModel.Place(anchor, work, D(s, "w")!.Value, D(s, "h")!.Value) : EmojiModel.Place(anchor, work);
            var r = c.GetProperty("r");
            Assert.True(new EmojiRect(D(r, "x")!.Value, D(r, "y")!.Value, D(r, "width")!.Value, D(r, "height")!.Value) == got,
                $"anchor {a.GetRawText()} on {w.GetRawText()}: got {got}");
        }
    }
}
