using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Data;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The ping test history (lib/features/_pingtest-history.js, its checks in lib/features/pingtest-history.test.js),
/// in a scratch folder through the ONE write gate.
/// </summary>
public sealed class PingTestHistoryTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mantle-pth-" + Guid.NewGuid().ToString("N"));
    readonly List<string> _logs = new();
    const string Wifi = "Secret Home Net", TName = "Keep upstairs";

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    PingTestHistory Make(WriteGate? gate = null) => new(_dir, new SafeFile(gate ?? WriteGate.Scratch(_dir)), _logs.Add);
    string FilePath => Path.Combine(_dir, PingTestHistory.FileName);

    static JsonObject Run(double startedAt, int run, string phase = "done") => new()
    {
        ["run"] = run, ["phase"] = phase, ["startedAt"] = startedAt, ["endedAt"] = startedAt + 30000, ["label"] = "Valorant", ["host"] = "speed.cloudflare.com",
        ["progress"] = 1, ["caps"] = new JsonObject { ["rounds"] = 20 },
        ["wifi"] = new JsonObject { ["kind"] = "wifi", ["name"] = Wifi, ["link"] = new JsonObject { ["kind"] = "wifi", ["band"] = "5 GHz", ["signal"] = 70, ["missing"] = new JsonObject { ["rssi"] = "not given" } } },
        ["rows"] = new JsonArray(
            new JsonObject { ["kind"] = "router", ["name"] = "Your router", ["host"] = "192.0.2.1", ["avg"] = 2.4, ["jitter"] = 0.6, ["loss"] = 0, ["got"] = 20, ["sent"] = 20, ["iface"] = "Wi-Fi", ["extra"] = 1 },
            new JsonObject { ["kind"] = "server", ["name"] = "US West", ["host"] = "a", ["avg"] = 15.8, ["jitter"] = 1.2, ["loss"] = 0, ["got"] = 20, ["sent"] = 20, ["best"] = true }),
        ["down"] = new JsonObject { ["state"] = "ok", ["mbps"] = 62, ["bytes"] = 60e6, ["ms"] = 7000, ["live"] = 61, ["server"] = "Cloudflare", ["tried"] = new JsonArray() },
        ["up"] = new JsonObject { ["state"] = "ok", ["mbps"] = 30.8, ["bytes"] = 25e6 },
    };

    [Fact]
    public void CheckName_trims_and_refuses_what_Electron_refuses_TOP_153()
    {
        Assert.Equal("upstairs", PingTestHistory.CheckName(JsonValue.Create("  upstairs ")).Name);
        Assert.Equal("a name needs 1 to 60 characters", PingTestHistory.CheckName(JsonValue.Create("   ")).Reason);
        Assert.False(PingTestHistory.CheckName(JsonValue.Create(new string('x', 61))).Ok);
        Assert.True(PingTestHistory.CheckName(JsonValue.Create(new string('x', 60))).Ok);
        Assert.Equal("a name cannot hold control characters", PingTestHistory.CheckName(JsonValue.Create("a\u0007b")).Reason);
        Assert.False(PingTestHistory.CheckName(JsonValue.Create(5)).Ok);
        Assert.Equal(50, PingTestHistory.MaxUnsaved);
    }

    [Fact]
    public void EntryOf_keeps_only_the_listed_fields_of_a_finished_run()
    {
        Assert.Null(PingTestHistory.EntryOf(null));
        Assert.Null(PingTestHistory.EntryOf(Run(1000, 1, "ping")));
        var noStart = Run(1000, 1); noStart["startedAt"] = null;
        Assert.Null(PingTestHistory.EntryOf(noStart));
        var e = PingTestHistory.EntryOf(Run(1760000000000, 3))!;
        Assert.Equal("1760000000000-3", (string?)e["id"]);
        Assert.Equal(Wifi, (string?)e["wifi"]!["name"]);
        Assert.Equal("US West", (string?)e["best"]!["name"]);
        Assert.Equal("192.0.2.1", (string?)e["router"]!["host"]);
        Assert.Null(e["down"]!["live"]);
        Assert.False(((JsonObject)e["down"]!).ContainsKey("ms"));
        Assert.Equal("Cloudflare", (string?)e["down"]!["server"]);
        Assert.False(((JsonObject)e["rows"]![0]!).ContainsKey("extra"));
        Assert.False(e.ContainsKey("caps"));
        Assert.Null(e["linkEnd"]);
    }

    [Fact]
    public void Add_save_rename_remove_and_the_file_TOP_153_157()
    {
        var h = Make();
        Assert.Empty(h.View()["tests"]!.AsArray());
        Assert.Null(h.View()["problem"]);
        Assert.NotNull(h.Add(Run(1000, 1)));
        Assert.NotNull(h.Add(Run(2000, 2)));
        Assert.Null(h.Add(Run(2000, 2)));            // the same run twice
        Assert.Null(h.Add(Run(3000, 3, "cancelled")));
        Assert.Equal("2000-2", (string?)h.View()["tests"]![0]!["id"]);   // newest first
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        var doc = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.Equal(1, (int)doc["version"]!);
        Assert.Equal("only a saved test can be renamed", (string?)h.Rename("1000-1", JsonValue.Create("x"))["reason"]);
        var s = h.Save("1000-1", JsonValue.Create("  " + TName + " "));
        Assert.True((bool)s["ok"]!);
        Assert.Equal(TName, (string?)s["entry"]!["name"]);
        Assert.Equal("that test is no longer in the history", (string?)h.Save("9-9", JsonValue.Create("x"))["reason"]);
        Assert.False((bool)h.Save("2000-2", JsonValue.Create(""))["ok"]!);
        Assert.False((bool)h.View()["tests"]![0]!["saved"]!);
        Assert.True((bool)h.Rename("1000-1", JsonValue.Create("Second name"))["ok"]!);
        Assert.True((bool)h.Remove("1000-1")["ok"]!);
        Assert.False((bool)h.Remove("1000-1")["ok"]!);
        var again = Make();
        Assert.Single(again.View()["tests"]!.AsArray());
        Assert.All(_logs, l => { Assert.DoesNotContain(Wifi, l); Assert.DoesNotContain(TName, l); Assert.DoesNotContain("Second name", l); });
    }

    [Fact]
    public void Prune_keeps_the_50_newest_unsaved_and_every_saved_one()
    {
        var h = Make();
        for (int i = 1; i <= 3; i++) { h.Add(Run(i, i)); h.Save($"{i}-{i}", JsonValue.Create("Saved " + i)); }
        for (int i = 10; i < 60; i++) h.Add(Run(i * 10, i));
        h.Add(Run(1000, 99));
        var tests = h.View()["tests"]!.AsArray();
        Assert.Equal(53, tests.Count);
        Assert.Equal(3, tests.Count(t => (bool)t!["saved"]!));
        Assert.DoesNotContain(tests, t => (string?)t!["id"] == "100-10");   // the oldest unsaved went
        Assert.Equal(53, JsonNode.Parse(File.ReadAllText(FilePath))!["tests"]!.AsArray().Count);
    }

    [Fact]
    public void A_damaged_file_is_moved_aside_and_the_history_starts_fresh_TOP_159()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"tests\": [ {\"id\": \"" + Wifi + "\" ");
        var h = Make();
        var p = (string?)h.View()["problem"];
        Assert.Matches(@"^history could not be read: the file is damaged( near character \d+)?; starting fresh, old file kept as ping-tests\.bad\.json$", p);
        Assert.DoesNotContain(Wifi, p);
        Assert.StartsWith("{\"tests\"", File.ReadAllText(Path.Combine(_dir, "ping-tests.bad.json")));
        Assert.NotNull(h.Add(Run(1000, 1)));
        Assert.Contains("history could not be read", (string?)h.View()["problem"]);   // sticky for the session

        // a second bad file never replaces the first (Electron's rename would): it gets its own name
        File.WriteAllText(FilePath, "{\"version\": 1}");
        var h2 = Make();
        Assert.Matches(@"^history could not be read: the file does not hold a list of tests; starting fresh, old file kept as ping-tests\.bad-\d+\.json$", (string?)h2.View()["problem"]);
        Assert.StartsWith("{\"tests\"", File.ReadAllText(Path.Combine(_dir, "ping-tests.bad.json")));
    }

    [Fact]
    public void Entries_that_make_no_sense_are_dropped_with_one_log_line()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"version\":1,\"tests\":[{\"id\":\"1-1\",\"at\":1},{\"id\":\"1-1\"},{\"id\":5},7,{\"id\":\"2-2\",\"at\":2}]}");
        var h = Make();
        Assert.Equal(2, h.View()["tests"]!.AsArray().Count);
        Assert.Null(h.View()["problem"]);
        Assert.Single(_logs);
        Assert.Contains("dropped 3", _logs[0]);
    }

    [Fact]
    public void A_refused_write_keeps_the_tests_in_memory_and_says_why_once()
    {
        var h = Make(WriteGate.Never("this run is not --live, so the user's own ping-tests.json is left exactly as Electron Mantle wrote it"));
        Assert.NotNull(h.Add(Run(1000, 1)));
        Assert.NotNull(h.Add(Run(2000, 2)));
        Assert.Equal("history could not be saved: this run is not --live, so the user's own ping-tests.json is left exactly as Electron Mantle wrote it", (string?)h.View()["problem"]);
        Assert.Equal(2, h.View()["tests"]!.AsArray().Count);
        Assert.Single(_logs, l => l.Contains("could not save", StringComparison.Ordinal));
        Assert.False(File.Exists(FilePath));
    }
}
