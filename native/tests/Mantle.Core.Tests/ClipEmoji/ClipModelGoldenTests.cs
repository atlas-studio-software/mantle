using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Features.Clipboard;
using Xunit;

namespace Mantle.Core.Tests.ClipEmoji;

/// <summary>
/// The clipboard panel's pure logic against Electron's own answers (CLIP-008..011, 017, 038, 045, 058..060, 063, 065, 073):
/// ClipEmojiGolden.g.cs is ui/clipboard/model.js run over fixed inputs by native/tools/parity/clipemoji/make-golden.cjs.
/// Every case is replayed through ClipModel and must give the same answer. The fixture texts are made up; no user data.
/// </summary>
public sealed class ClipModelGoldenTests
{
    static readonly JsonElement G = JsonDocument.Parse(ClipEmojiGolden.Json).RootElement.GetProperty("clip");

    static string? S(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool B(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static double? D(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static int I(JsonElement o, string name) => (int)(D(o, name) ?? 0);

    internal static ClipItem ItemOf(JsonElement o) => new()
    {
        Id = S(o, "id") ?? "", Kind = S(o, "kind") ?? "text", Text = S(o, "text"), Chars = I(o, "chars"), Lines = I(o, "lines"),
        Rich = B(o, "rich"), Pinned = B(o, "pinned"), W = I(o, "w"), H = I(o, "h"), FileCount = I(o, "fileCount"), Ts = D(o, "ts") ?? 0,
        Files = o.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
            ? (IReadOnlyList<ClipFile>)f.EnumerateArray().Select(x => new ClipFile(S(x, "name") ?? "", S(x, "path"), B(x, "dir"))).ToList()
            : Array.Empty<ClipFile>(),
    };

    static List<ClipItem> Items() => G.GetProperty("items").EnumerateArray().Select(ItemOf).ToList();
    static double Now => G.GetProperty("now").GetDouble();

    [Fact]
    public void Constants_match_electron()
    {
        var c = G.GetProperty("consts");
        Assert.Equal(c.GetProperty("PANEL_W").GetDouble(), ClipModel.PanelW);
        Assert.Equal(c.GetProperty("PANEL_H").GetDouble(), ClipModel.PanelH);
        Assert.Equal(c.GetProperty("MARGIN").GetDouble(), ClipModel.Margin);
        Assert.Equal(c.GetProperty("GLASS_SLOW_MS").GetDouble(), ClipModel.GlassSlowMs);
    }

    [Fact]
    public void Filter_matches_electron_for_every_query()
    {
        var items = Items();
        int n = 0;
        foreach (var c in G.GetProperty("filter").EnumerateArray())
        {
            string q = c.GetProperty("q").GetString()!;
            var want = c.GetProperty("ids").EnumerateArray().Select(x => x.GetString()!).ToList();
            var got = ClipModel.Filter(items, q).Select(i => i.Id).ToList();
            Assert.True(want.SequenceEqual(got), $"filter '{q}': want [{string.Join(",", want)}], got [{string.Join(",", got)}]");
            n++;
        }
        Assert.True(n >= 20);
        Assert.Equal(9, items.Count);   // the filter never mutates the list it was given
    }

    [Fact]
    public void Preview_lines_match_electron()
    {
        foreach (var c in G.GetProperty("preview").EnumerateArray())
        {
            string t = c.GetProperty("t").GetString()!;
            int max = c.GetProperty("max").GetInt32();
            var r = c.GetProperty("r");
            var got = max == 0 ? ClipModel.PreviewLines(t) : ClipModel.PreviewLines(t, max);
            var want = r.GetProperty("lines").EnumerateArray().Select(x => x.GetString()!).ToList();
            Assert.True(want.SequenceEqual(got.Lines), $"preview of {JsonSerializer.Serialize(t)} (max {max})");
            Assert.Equal(r.GetProperty("more").GetBoolean(), got.More);
            Assert.Equal(r.GetProperty("blank").GetBoolean(), got.Blank);
        }
    }

    [Fact]
    public void Ago_and_meta_lines_match_electron()
    {
        double now = Now;
        // the golden's past-a-week date ("Nov 6") is Electron's en-US toLocaleDateString; Ago formats with the
        // current culture, so pin it here and the answer does not depend on the runner's locale
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            foreach (var c in G.GetProperty("ago").EnumerateArray())
            {
                double s = c.GetProperty("s").GetDouble();
                Assert.Equal(c.GetProperty("r").GetString(), ClipModel.Ago(now - s * 1000, now));
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var byId = Items().ToDictionary(i => i.Id);
        foreach (var c in G.GetProperty("meta").EnumerateArray())
        {
            string id = c.GetProperty("id").GetString()!;
            ClipItem it = c.TryGetProperty("it", out var raw) ? ItemOf(raw) : byId[id];
            Assert.Equal(c.GetProperty("r").GetString(), ClipModel.Meta(it, now));
        }
    }

    [Fact]
    public void Key_actions_match_electron()
    {
        int n = 0;
        foreach (var c in G.GetProperty("keys").EnumerateArray())
        {
            var e = c.GetProperty("e");
            var st = c.GetProperty("st");
            var key = new ClipKey(S(e, "key")!, B(e, "ctrlKey"), B(e, "altKey"), B(e, "metaKey"));
            var state = new ClipKeyState(S(st, "query"), B(st, "filterArmed"), (int?)D(st, "selStart"), (int?)D(st, "selEnd"));
            string? want = c.GetProperty("r").ValueKind == JsonValueKind.String ? c.GetProperty("r").GetString() : null;
            Assert.True(want == ClipModel.KeyAction(key, state), $"key {e.GetRawText()} in {st.GetRawText()}: want {want ?? "null"}");
            n++;
        }
        Assert.True(n > 500);
    }

    [Fact]
    public void Selection_moves_and_reselect_match_electron()
    {
        foreach (var c in G.GetProperty("moves").EnumerateArray())
        {
            int page = c.GetProperty("page").GetInt32();
            int got = ClipModel.MoveSel(c.GetProperty("sel").GetInt32(), c.GetProperty("a").GetString()!, c.GetProperty("n").GetInt32(), page);
            Assert.True(c.GetProperty("r").GetInt32() == got, $"move {c.GetRawText()}: got {got}");
        }
        foreach (var c in G.GetProperty("reselect").EnumerateArray())
        {
            var ids = c.GetProperty("n").EnumerateArray().Select(x => x.GetString()!).ToList();
            int got = ClipModel.Reselect(c.GetProperty("id").GetString(), c.GetProperty("idx").GetInt32(), ids);
            Assert.True(c.GetProperty("r").GetInt32() == got, $"reselect {c.GetRawText()}: got {got}");
        }
    }

    [Fact]
    public void Placement_matches_electron_on_every_work_area()
    {
        foreach (var c in G.GetProperty("place").EnumerateArray())
        {
            var a = c.GetProperty("a");
            var w = c.GetProperty("w");
            var work = new DipRect(D(w, "x")!.Value, D(w, "y")!.Value, D(w, "width")!.Value, D(w, "height")!.Value);
            var s = c.GetProperty("s");
            ClipPlacement got = s.ValueKind == JsonValueKind.Object
                ? ClipModel.Place(D(a, "x")!.Value, D(a, "y")!.Value, D(a, "h") ?? 0, work, D(s, "w")!.Value, D(s, "h")!.Value, D(s, "m")!.Value)
                : ClipModel.Place(D(a, "x")!.Value, D(a, "y")!.Value, D(a, "h") ?? 0, work);
            var r = c.GetProperty("r");
            var p = r.GetProperty("panel");
            string where = $"anchor {a.GetRawText()} on {w.GetRawText()}";
            Assert.True(new DipRect(D(r, "x")!.Value, D(r, "y")!.Value, D(r, "width")!.Value, D(r, "height")!.Value) == got.Window, "window, " + where);
            Assert.True(new DipRect(D(p, "x")!.Value, D(p, "y")!.Value, D(p, "width")!.Value, D(p, "height")!.Value) == got.Panel, "panel, " + where);
        }
    }

    [Fact]
    public void Dark_glass_report_and_log_line_match_electron()
    {
        foreach (var c in G.GetProperty("glass").EnumerateArray())
        {
            var a = c.GetProperty("a");
            var since = c.GetProperty("since");
            var g = ClipModel.GlassOf(a.ValueKind == JsonValueKind.Object && B(a, "ok"), D(a, "ms"), D(a, "capture"), D(a, "bake"),
                since.ValueKind == JsonValueKind.Number ? since.GetDouble() : null, S(a, "reason"));
            var want = c.GetProperty("g");
            Assert.Equal(B(want, "ok"), g.Ok);
            Assert.Equal(D(want, "ms"), g.Ms);
            Assert.Equal(D(want, "capture"), g.Capture);
            Assert.Equal(D(want, "bake"), g.Bake);
            Assert.Equal(D(want, "sinceOpen"), g.SinceOpen);
            Assert.Equal(S(want, "reason"), g.Reason);
            var line = c.GetProperty("line");
            Assert.Equal(line.ValueKind == JsonValueKind.String ? line.GetString() : null, ClipModel.GlassLine(g, c.GetProperty("first").GetBoolean()));
        }
    }
}
