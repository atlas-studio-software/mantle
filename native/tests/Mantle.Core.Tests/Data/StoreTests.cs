using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests.Data;

/// <summary>
/// The writable stores (PLAN.md 3.7 (a), contracts.md IStore): Electron's exact bytes, unknown keys kept,
/// tmp-and-rename, the .unreadable rule, change detection, and the read-only gate. The round-trip fixtures
/// (Fixtures/roundtrip, made by gen.js with Node's own JSON.stringify) are the spec for every file's format.
/// </summary>
public class StoreTests
{
    static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "roundtrip");

    static IEnumerable<string> FixtureNames()
        => Directory.GetFiles(FixtureDir, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(FixtureDir, f).Replace('/', '\\'))
            .OrderBy(n => n, StringComparer.Ordinal);

    static Stores ScratchStores(TestDir d, ListLog? log = null) => new(d.Path, new SafeFile(WriteGate.Scratch(d.Path)), log ?? new ListLog());

    static JsonNode Probe() => JsJson.Parse("{\"added\":\"by native\",\"n\":0.1,\"list\":[1,2.5,\"\u00e9\U0001F600\"]}")!;

    [Theory]
    [InlineData(0.0, "0")] [InlineData(-0.0, "0")] [InlineData(1.0, "1")] [InlineData(-1.0, "-1")]
    [InlineData(0.1, "0.1")] [InlineData(12.5, "12.5")] [InlineData(1e21, "1e+21")] [InlineData(1e20, "100000000000000000000")]
    [InlineData(1.5e-7, "1.5e-7")] [InlineData(0.000001, "0.000001")] [InlineData(123456789012.0, "123456789012")]
    [InlineData(9007199254740991.0, "9007199254740991")] [InlineData(-2.5e-300, "-2.5e-300")] [InlineData(4.35, "4.35")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")] [InlineData(5e-324, "5e-324")] [InlineData(0.3333333333333333, "0.3333333333333333")]
    public void Numbers_print_the_way_JavaScript_prints_them(double d, string js) => Assert.Equal(js, JsJson.Number(d));

    [Fact]
    public void Strings_escape_only_what_JSON_stringify_escapes()
    {
        var sb = new StringBuilder();
        JsJson.WriteString(sb, "a\"b\\c\td\ne\u0007f\u001bg\u007fh\u00e9\U0001F600\u2028\ud800");
        Assert.Equal("\"a\\\"b\\\\c\\td\\ne\\u0007f\\u001bg\u007fh\u00e9\U0001F600\u2028\\ud800\"", sb.ToString());
    }

    [Fact]
    public void Array_index_keys_come_first_and_a_repeated_key_keeps_its_first_place_and_last_value()
    {
        var n = JsJson.Parse("{\"b\":1,\"10\":2,\"a\":3,\"2\":4,\"b\":5,\"01\":6}");
        Assert.Equal("{\"2\":4,\"10\":2,\"b\":5,\"a\":3,\"01\":6}", JsJson.Stringify(n, 0));
    }

    [Fact]
    public void There_is_a_fixture_for_every_file_a_store_may_open()
    {
        var names = FixtureNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = DataFileSpecs.All.Where(s => !s.NeverWritten && !names.Contains(s.Name)).Select(s => s.Name).ToList();
        Assert.True(missing.Count == 0, "no round-trip fixture for: " + string.Join(", ", missing));
        Assert.Contains(@"features\emoji.json", names);   // and a feature's own settings file
    }

    [Fact]
    public void Every_fixture_round_trips_byte_for_byte_and_a_change_writes_exactly_what_Electron_would()
    {
        var bad = new List<string>();
        foreach (var rel in FixtureNames()) {
            using var d = new TestDir("rt");
            byte[] original = File.ReadAllBytes(Path.Combine(FixtureDir, rel));
            byte[] after = File.ReadAllBytes(Path.Combine(FixtureDir, rel + ".after"));
            string dst = d.File(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, original);
            using var stores = ScratchStores(d);
            var s = stores.Open(rel);
            if (s.State != ReadState.Ok) { bad.Add(rel + ": loaded as " + s.State + " (" + s.Reason + ")"); continue; }
            Assert.True(s.TryGet("unknownFromTheFuture", out _), rel + ": the unknown key was not loaded");
            Assert.True(s.UpdateAsync(_ => { }).Result.Ok, rel + ": the rewrite failed");
            if (!File.ReadAllBytes(dst).SequenceEqual(original)) bad.Add(rel + ": a rewrite with no change did not give the same bytes");
            var w = s.SetAsync("nativeProbe", Probe()).Result;
            Assert.True(w.Ok, rel + ": " + w.Reason);
            if (!File.ReadAllBytes(dst).SequenceEqual(after)) bad.Add(rel + ": after one change the bytes are not what JSON.stringify writes (" + Encoding.UTF8.GetString(File.ReadAllBytes(dst)).Length + " chars)");
            var left = Directory.GetFiles(d.Path, "*.tmp", SearchOption.AllDirectories);
            if (left.Length > 0) bad.Add(rel + ": a .tmp was left behind");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void An_unusable_file_is_left_alone_on_open_and_moved_aside_before_the_first_write_never_overwritten()
    {
        using var d = new TestDir("aside");
        d.Write("features.json", "{\"clipboard\":");
        var log = new ListLog();
        using var stores = ScratchStores(d, log);
        var s = stores.Open("features.json");
        Assert.Equal(ReadState.Corrupt, s.State);
        Assert.Contains("not valid JSON", s.Reason);
        Assert.Equal("{}", s.Snapshot().GetRawText());
        Assert.Equal("{\"clipboard\":", File.ReadAllText(d.File("features.json")));   // opening changes nothing
        Assert.DoesNotContain(log.Lines, l => l.Contains(d.Path, StringComparison.OrdinalIgnoreCase));   // name only, never the path

        Assert.True(s.SetAsync("clipboard", JsonValue.Create(true)).Result.Ok);
        Assert.Equal("{\"clipboard\":", File.ReadAllText(d.File("features.json.unreadable")));
        Assert.Equal("{\n  \"clipboard\": true\n}", File.ReadAllText(d.File("features.json")));
        Assert.Contains("kept as features.json.unreadable", s.Reason);

        // a second bad file never replaces the first aside copy
        using var d2 = new TestDir("aside2");
        d2.Write(@"features\timer.json", "[1,2]");
        d2.Write(@"features\timer.json.unreadable", "older");
        using var st2 = new Stores(d2.Path, new SafeFile(WriteGate.Scratch(d2.Path)), new ListLog());
        var t = st2.Open(@"features\timer.json");
        Assert.Equal(ReadState.Corrupt, t.State);
        Assert.Contains("not a JSON object", t.Reason);
        Assert.True(t.SetAsync("clock", JsonValue.Create(true)).Result.Ok);
        Assert.Equal("older", File.ReadAllText(d2.File(@"features\timer.json.unreadable")));
        var extra = Directory.GetFiles(d2.File("features"), "timer.json.unreadable-*");
        Assert.Single(extra);
        Assert.Equal("[1,2]", File.ReadAllText(extra[0]));
    }

    [Fact]
    public void A_theme_file_over_Electrons_64_KB_limit_is_unusable_and_set_aside()
    {
        using var d = new TestDir("big");
        d.Write("theme.json", "{\"mode\":\"dark\",\"pad\":\"" + new string('x', 70 * 1024) + "\"}");
        using var stores = ScratchStores(d);
        var s = stores.Open("theme.json");
        Assert.Equal(ReadState.TooLarge, s.State);
        Assert.False(s.TryGet("mode", out _));
        Assert.True(s.SetAsync("mode", JsonValue.Create("light")).Result.Ok);
        Assert.True(File.Exists(d.File("theme.json.unreadable")));
    }

    [Fact]
    public void The_hand_written_media_guard_file_is_read_and_never_written()
    {
        using var d = new TestDir("mg");
        d.Write("media-guard.json", "{\"spotify\":{\"guard\":false}}");
        using var stores = ScratchStores(d);
        var s = stores.Open("media-guard.json");
        Assert.True(s.TryGet("spotify", out var v));
        Assert.Equal("{\"guard\":false}", v.GetRawText());
        var w = s.SetAsync("x", JsonValue.Create(1)).Result;
        Assert.False(w.Ok);
        Assert.Contains("never writes it", w.Reason);
        Assert.Equal("{\"spotify\":{\"guard\":false}}", File.ReadAllText(d.File("media-guard.json")));
    }

    [Fact]
    public void A_missing_file_reads_as_empty_and_removing_an_absent_key_writes_nothing()
    {
        using var d = new TestDir("miss");
        using var stores = ScratchStores(d);
        var s = stores.Open("switcher.json");
        Assert.Equal(ReadState.Missing, s.State);
        Assert.True(s.RemoveAsync("altTab").Result.Ok);
        Assert.False(File.Exists(d.File("switcher.json")));
        Assert.True(s.SetAsync("altTab", JsonValue.Create(false)).Result.Ok);
        Assert.Equal(ReadState.Ok, s.State);
        Assert.Equal("{\n  \"altTab\": false\n}", File.ReadAllText(d.File("switcher.json")));
        Assert.True(s.RemoveAsync("altTab").Result.Ok);
        Assert.Equal("{}", File.ReadAllText(d.File("switcher.json")));
    }

    [Fact]
    public void Update_makes_several_changes_in_one_write_and_keeps_the_rest()
    {
        using var d = new TestDir("upd");
        d.Write("look-sync.json", "{\"lookSync\":true,\"future\":{\"x\":1}}");
        using var stores = ScratchStores(d);
        var s = stores.Open("look-sync.json");
        Assert.True(s.UpdateAsync(o => { o["lookSync"] = false; o["accent"] = new JsonObject { ["hue"] = 200 }; }).Result.Ok);
        Assert.Equal("{\n  \"lookSync\": false,\n  \"future\": {\n    \"x\": 1\n  },\n  \"accent\": {\n    \"hue\": 200\n  }\n}", File.ReadAllText(d.File("look-sync.json")));
    }

    [Fact]
    public void One_store_per_file_and_only_safe_relative_names()
    {
        using var d = new TestDir("open");
        using var stores = ScratchStores(d);
        Assert.Same(stores.Open("theme.json"), stores.Open("THEME.json"));
        Assert.Same(stores.Open(@"features\clipboard.json"), stores.Open("features/clipboard.json"));
        Assert.Equal(@"features\clipboard.json", stores.Open("features/clipboard.json").Name);
        foreach (var bad in new[] { @"..\theme.json", @"C:\x\theme.json", @"\theme.json", "nothing-like-this.json", @"features\Bad.json", @"features\a\b.json", "" })
            Assert.Throws<ArgumentException>(() => stores.Open(bad));
    }

    [Fact]
    public void A_change_made_by_another_program_is_read_again_and_told_once_and_own_writes_are_not()
    {
        using var d = new TestDir("chg");
        d.Write("theme.json", "{\"mode\":\"dark\"}");
        using var stores = ScratchStores(d);
        var s = stores.Open("theme.json");
        int told = 0;
        using var off = s.OnChanged(_ => told++);
        Assert.True(s.SetAsync("mode", JsonValue.Create("light")).Result.Ok);
        stores.CheckNow();
        Assert.Equal(0, told);
        System.Threading.Thread.Sleep(20);
        d.WriteLikeElectron("theme.json", "{\n  \"mode\": \"system\"\n}");
        File.SetLastWriteTimeUtc(d.File("theme.json"), DateTime.UtcNow.AddSeconds(5));
        stores.CheckNow();
        Assert.Equal(1, told);
        Assert.True(s.TryGet("mode", out var m));
        Assert.Equal("system", m.GetString());
        stores.CheckNow();
        Assert.Equal(1, told);
    }

    [Fact]
    public void A_refused_write_changes_nothing_on_disk_but_the_value_holds_for_this_run()
    {
        using var d = new TestDir("gate");
        d.Write("theme.json", "{\"mode\":\"dark\"}");
        d.Write("features.json", "not json");
        var before = Directory.GetFiles(d.Path).ToDictionary(f => f, File.ReadAllBytes);
        using var stores = new Stores(d.Path, new SafeFile(WriteGate.Never("a test says no")), new ListLog());
        var t = stores.Open("theme.json");
        var w = t.SetAsync("mode", JsonValue.Create("light")).Result;
        Assert.False(w.Ok);
        Assert.Contains("a test says no", w.Reason);
        Assert.True(t.TryGet("mode", out var m));
        Assert.Equal("light", m.GetString());
        Assert.False(stores.Open("features.json").SetAsync("x", JsonValue.Create(true)).Result.Ok);   // not moved aside either
        Assert.Equal(before.Keys.OrderBy(k => k), Directory.GetFiles(d.Path).OrderBy(k => k));
        foreach (var (f, bytes) in before) Assert.True(File.ReadAllBytes(f).SequenceEqual(bytes), "changed: " + f);
    }
}
