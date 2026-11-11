using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.GameMode;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Features;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.GameMode;

/// <summary>
/// [Feature("gamemode")] (lib/features/gamemode.js), the service: the scan tick turning game mode on and off, the power
/// step, easing through gamemode.apply, the boost's one trim a minute in, the restore record and its put-back after a
/// crash, what could not be put back tried again, the rest hand-off, and the --test dry run (nothing written is ever
/// sent). FakePlatform stands in for the helper; the record lives in a temp folder, never a real profile.
/// </summary>
public class GameModeFeatureTests
{
    const string PowerState = "{\"ok\":true,\"available\":true,\"settable\":true,\"source\":\"ac\",\"saver\":false,\"configured\":{\"ac\":\"balanced\",\"battery\":\"balanced\"}}";
    const string App = "{\"pid\":600,\"ppid\":4,\"file\":\"SomeApp.exe\",\"session\":1,\"path\":\"C:\\\\Program Files\\\\SomeApp\\\\SomeApp.exe\",\"created\":\"111\",\"cpuMs\":10,\"ws\":104857600,\"prio\":\"normal\"}";
    const string Valorant = "{\"pid\":500,\"ppid\":4,\"file\":\"VALORANT-Win64-Shipping.exe\",\"session\":1}";
    const string Standin = "{\"pid\":900,\"ppid\":4,\"file\":\"standin-game.exe\",\"session\":1}";
    const string FgGame = "{\"pid\":500,\"exe\":\"C:\\\\Games\\\\VALORANT-Win64-Shipping.exe\",\"title\":\"VALORANT\",\"fullscreen\":true,\"self\":false}";
    const string FgDesk = "{\"pid\":700,\"exe\":\"C:\\\\Windows\\\\explorer.exe\",\"title\":\"\",\"fullscreen\":false,\"self\":false}";
    const string Record = "{\"v\":1,\"since\":1,\"how\":\"auto\",\"game\":{\"file\":\"VALORANT-Win64-Shipping.exe\",\"pid\":500},\"power\":{\"ac\":{\"was\":\"balanced\",\"set\":\"performance\"}},\"saver\":null,"
        + "\"procs\":[{\"pid\":600,\"created\":\"111\",\"file\":\"SomeApp.exe\",\"group\":\"background\",\"eco\":{\"was\":{\"control\":0,\"state\":0},\"set\":{\"control\":1,\"state\":1}},\"prio\":{\"was\":\"normal\",\"set\":\"belownormal\"}}],"
        + "\"wallpaper\":false,\"lively\":null,\"trim\":{\"pids\":[]}}";

    sealed class G
    {
        public Rig R = null!; public string Dir = ""; public string Fg = FgDesk; public List<string> Procs = new() { App };
        public string RecordPath => Path.Combine(Dir, GameModeFeature.RecordName);
        public string ScanJson() => $"{{\"fg\":{Fg},\"procs\":[{string.Join(",", Procs)}],\"ownPid\":42,\"ownSession\":1}}";
        public IEnumerable<FakePlatform.Call> Writes => R.Platform.Calls.Where(c => c.Verb is "gamemode.apply" or "gamemode.power" or "gamemode.saver" or "gamemode.trimbg");
        public async Task<JsonElement> State() => (await R.Bus.CallAsync("gamemode.state")).Result;
        public async Task<JsonElement> Set(bool on) => (await R.Bus.CallAsync("gamemode.set", new JsonObject { ["on"] = on })).Result;
    }

    static void T(bool test, Func<G, Task> body, IReadOnlyList<string>? testGames = null, Action<G>? before = null) => HostLoop.Run(async (_, time) =>
    {
        var g = new G { R = new Rig(time, test), Dir = Path.Combine(Path.GetTempPath(), "mantle-gm-test-" + Guid.NewGuid().ToString("N")[..8]) };
        Directory.CreateDirectory(g.Dir);
        try
        {
            var p = g.R.Platform;
            p.Start();
            p.Handle("gamemode.scan", _ => PlatformReply.Success(JsonDocument.Parse(g.ScanJson()).RootElement.Clone()));
            p.Answer("powermode.get", PowerState);
            p.Answer("audio", "{\"ok\":true,\"sessions\":[]}");
            p.Answer("gamemode.power", "{\"ok\":true,\"same\":false,\"before\":\"balanced\"}");
            p.Handle("gamemode.apply", a => {
                var items = a.GetProperty("items").EnumerateArray().Select(it => (JsonNode)new JsonObject
                {
                    ["pid"] = it.GetProperty("pid").GetInt32(), ["file"] = it.GetProperty("file").GetString(), ["created"] = "111", ["ok"] = true,
                    ["eco"] = new JsonObject { ["wrote"] = true, ["before"] = new JsonObject { ["control"] = 0, ["state"] = 0 } },
                    ["prio"] = new JsonObject { ["wrote"] = true, ["before"] = "normal" },
                }).ToArray();
                return PlatformReply.Success(JsonDocument.Parse(new JsonObject { ["results"] = new JsonArray(items) }.ToJsonString()).RootElement.Clone());
            });
            p.Answer("gamemode.trimbg", "{\"results\":[{\"pid\":600,\"file\":\"SomeApp.exe\",\"ok\":true}],\"trimmed\":1,\"freed\":52428800}");
            before?.Invoke(g);
            var h = new GameModeFeature.Host(g.Dir, OwnPids: () => Array.Empty<int>(), Clock: new HostClock(time), FreeMem: () => 4L << 30, MainPid: 1, TestGames: testGames);
            var a = typeof(GameModeFeature).GetCustomAttribute<FeatureAttribute>()!;
            g.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => new GameModeFeature(h), GameModeFeature.Options) });
            await g.R.Host.Installed;
            await body(g);
        }
        finally { try { Directory.Delete(g.Dir, true); } catch (IOException) { } }
    });

    static string Act(JsonElement st, string key) =>
        st.GetProperty("actions").EnumerateArray().Where(x => x.GetProperty("key").GetString() == key).Select(x => x.GetProperty("status").GetString() + ": " + x.GetProperty("text").GetString()).FirstOrDefault() ?? "(none)";

    [Fact]
    public void The_module_fields_and_options_are_Electrons()
    {
        var a = typeof(GameModeFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("gamemode", "Game mode (quick settings)", true), (a.Name, a.Label, a.DefaultOn));
        Assert.Empty(a.Chords);
        Assert.Equal(new[] { "auto", "liftSaver", "boost" }, GameModeFeature.Options.Select(o => o.Key));
        Assert.Equal(new object?[] { true, false, true }, GameModeFeature.Options.Select(o => o.Default));
    }

    [Fact]
    public void A_test_run_turns_on_for_a_known_game_as_a_dry_run_and_sends_no_write() => T(true, async g =>
    {
        g.Fg = FgGame; g.Procs = new() { Valorant, App };
        g.R.Time.AdvanceMs(10);
        var st = await g.State();
        Assert.True(st.GetProperty("on").GetBoolean());
        Assert.Equal("auto", st.GetProperty("how").GetString());
        Assert.True(st.GetProperty("dry").GetBoolean());
        Assert.StartsWith("dry: test run: would set Best performance plugged in (now balanced)", Act(st, "power"));
        Assert.StartsWith("dry: test run: would put Efficiency mode 1 app processes: SomeApp", Act(st, "background"));
        Assert.StartsWith("blocked: this version of Mantle cannot pause its wallpaper", Act(st, "wallpaper"));
        Assert.StartsWith("done: Eased ", Act(st, "boost"));   // waiting for its minute: what it eased, and when it trims
    });

    [Fact]
    public void A_test_run_never_sends_a_write_and_writes_no_record() => T(true, async g =>
    {
        g.Fg = FgGame; g.Procs = new() { Valorant, App };
        g.R.Time.AdvanceMs(10);
        g.R.Time.AdvanceMs(65_000);   // replans and the boost's minute
        Assert.True(g.R.Logged("gamemode: dry run (--test): would ask the sidecar gamemode.power {\"source\":\"ac\",\"mode\":\"performance\"}; nothing was sent"));
        Assert.True(g.R.Logged("gamemode: dry run (--test): would ask the sidecar gamemode.apply for 1 process"));
        Assert.True(g.R.Logged("gamemode: boost (DRY RUN): would trim 1 idle process"));
        Assert.Equal(0, g.Writes.Count());
        Assert.False(File.Exists(g.RecordPath));
        var off = await g.Set(false);
        Assert.False(off.GetProperty("on").GetBoolean());
        Assert.Equal("test run: nothing had been changed", off.GetProperty("last").GetProperty("text").GetString());
        Assert.Equal(0, g.Writes.Count());
    });

    [Fact]
    public void A_live_run_eases_records_trims_once_and_puts_everything_back() => T(false, async g =>
    {
        g.Fg = FgGame; g.Procs = new() { Valorant, App };
        g.R.Time.AdvanceMs(10);
        var st = await g.State();
        Assert.True(st.GetProperty("on").GetBoolean());
        Assert.Equal("done: Best performance plugged in (was balanced; put back after)", Act(st, "power"));
        var power = g.Writes.First(c => c.Verb == "gamemode.power");
        Assert.Equal("{\"source\":\"ac\",\"mode\":\"performance\"}", power.ArgsJson);
        var apply = JsonNode.Parse(g.Writes.First(c => c.Verb == "gamemode.apply").ArgsJson)!["items"]![0]!;
        Assert.Equal(600, (int)apply["pid"]!);
        Assert.Equal("belownormal", (string?)apply["prio"]);
        Assert.Equal(1, (int)apply["eco"]!["state"]!);
        var rec = JsonNode.Parse(File.ReadAllText(g.RecordPath))!;
        Assert.Equal("balanced", (string?)rec["power"]!["ac"]!["was"]);
        Assert.Equal(600, (int)rec["procs"]![0]!["pid"]!);

        g.R.Time.AdvanceMs(66_000);   // the boost's minute after its baseline
        Assert.Single(g.Writes.Where(c => c.Verb == "gamemode.trimbg"));
        st = await g.State();
        Assert.StartsWith("Boost: freed 50", st.GetProperty("boostNow").GetProperty("text").GetString());
        g.R.Time.AdvanceMs(60_000);
        Assert.Single(g.Writes.Where(c => c.Verb == "gamemode.trimbg"));   // once per game

        var off = await g.Set(false);
        Assert.False(off.GetProperty("on").GetBoolean());
        var back = g.Writes.Where(c => c.Verb == "gamemode.apply").Last();
        var item = JsonNode.Parse(back.ArgsJson)!["items"]![0]!;
        Assert.Equal("normal", (string?)item["prio"]);
        Assert.Equal("belownormal", (string?)item["prioIf"]);
        Assert.Equal(0, (int)item["eco"]!["state"]!);
        Assert.Equal("{\"source\":\"ac\",\"mode\":\"balanced\",\"onlyIf\":\"performance\"}", g.Writes.Last(c => c.Verb == "gamemode.power").ArgsJson);
        var last = off.GetProperty("last");
        Assert.True(last.GetProperty("ok").GetBoolean());
        Assert.StartsWith("1 apps back to normal; plugged in back to balanced", last.GetProperty("text").GetString());
        Assert.Contains("the boost had freed 50", last.GetProperty("text").GetString());
        Assert.False(File.Exists(g.RecordPath));
        // turned off by hand for this game run: it does not come back on while the same game is in front
        g.R.Time.AdvanceMs(10_000);
        Assert.False((await g.State()).GetProperty("on").GetBoolean());
    });

    [Fact]
    public void The_game_exiting_turns_game_mode_off() => T(false, async g =>
    {
        g.Fg = FgGame; g.Procs = new() { Valorant, App };
        g.R.Time.AdvanceMs(10);
        Assert.True((await g.State()).GetProperty("on").GetBoolean());
        g.Fg = FgDesk; g.Procs = new() { App };
        g.R.Time.AdvanceMs(6000);
        var st = await g.State();
        Assert.False(st.GetProperty("on").GetBoolean());
        Assert.True(g.R.Logged("gamemode: off: "));
        Assert.False(File.Exists(g.RecordPath));
    });

    [Fact]
    public void A_record_left_by_a_crash_is_put_back_at_start() => T(false, async g =>
    {
        var st = await g.State();
        Assert.True(st.GetProperty("fromCrash").GetProperty("ok").GetBoolean());
        Assert.Equal("1 apps back to normal; plugged in back to balanced", st.GetProperty("fromCrash").GetProperty("text").GetString());
        Assert.Equal("{\"source\":\"ac\",\"mode\":\"balanced\",\"onlyIf\":\"performance\"}", g.Writes.Single(c => c.Verb == "gamemode.power").ArgsJson);
        Assert.Single(g.Writes.Where(c => c.Verb == "gamemode.apply"));
        Assert.False(File.Exists(g.RecordPath));
    }, before: g => File.WriteAllText(g.RecordPath, Record));

    [Fact]
    public void A_test_run_puts_a_crash_record_back_as_a_dry_run_and_drops_it() => T(true, async g =>
    {
        var st = await g.State();
        Assert.Equal("test run: would put 1 apps back to normal; plugged in back to balanced", st.GetProperty("fromCrash").GetProperty("text").GetString());
        Assert.Equal(0, g.Writes.Count());
        Assert.True(g.R.Logged("gamemode: dry run (--test): would ask the sidecar gamemode.apply {\"items\":[{\"pid\":600"));
        Assert.True(g.R.Logged("gamemode: test run: the restore record was read from the test profile and dropped; nothing was sent"));
        Assert.False(File.Exists(g.RecordPath));
    }, before: g => File.WriteAllText(g.RecordPath, Record));

    [Fact]
    public void What_could_not_be_put_back_is_kept_and_tried_again() => T(false, async g =>
    {
        var st = await g.State();
        Assert.False(st.GetProperty("fromCrash").GetProperty("ok").GetBoolean());
        Assert.Equal("Not put back yet: power mode plugged in: Windows refused. Mantle tries again every 30 s", st.GetProperty("leftover").GetProperty("text").GetString());
        var kept = JsonNode.Parse(File.ReadAllText(g.RecordPath))!;
        Assert.Equal("balanced", (string?)kept["power"]!["ac"]!["was"]);
        Assert.Empty(kept["procs"]!.AsArray());
        g.R.Platform.Answer("gamemode.power", "{\"ok\":true,\"same\":false,\"before\":\"performance\"}");
        g.R.Time.AdvanceMs(34_000);
        st = await g.State();
        Assert.Equal(JsonValueKind.Null, st.GetProperty("leftover").ValueKind);
        Assert.True(st.GetProperty("fromCrash").GetProperty("ok").GetBoolean());
        Assert.StartsWith("put back on a later try: 0 apps back to normal; plugged in back to balanced", st.GetProperty("fromCrash").GetProperty("text").GetString());
        Assert.False(File.Exists(g.RecordPath));
    }, before: g => { File.WriteAllText(g.RecordPath, Record); g.R.Platform.Answer("gamemode.power", "{\"ok\":false,\"reason\":\"Windows refused\"}"); });

    sealed class FakeRest : IRestHost
    {
        public readonly List<(bool On, string Why)> Calls = new();
        public string? RestingWhy { get; private set; }
        public Task<RestAnswer> RestAsync(bool on, string why)
        {
            Calls.Add((on, why)); RestingWhy = on ? why : null;
            return Task.FromResult(on ? new RestAnswer(true, Text: "closed launcher") : new RestAnswer(true, Back: new[] { "launcher" }));
        }
    }

    [Fact]
    public void A_stand_in_game_running_rests_Mantle_and_its_exit_wakes_it() => T(true, async g =>
    {
        var rest = new FakeRest();
        g.R.Host.RestHost = rest;
        g.Procs = new() { App, Standin };
        g.R.Time.AdvanceMs(10);
        Assert.Equal((true, "standin-game runs"), rest.Calls.Single());
        var st = await g.State();
        Assert.True(st.GetProperty("rest").GetProperty("on").GetBoolean());
        Assert.Equal("Mantle is resting while standin-game runs: closed launcher", st.GetProperty("rest").GetProperty("text").GetString());
        Assert.False(st.GetProperty("on").GetBoolean());   // running, not in front: game mode itself stays off
        g.Procs = new() { App };
        g.R.Time.AdvanceMs(5100);
        Assert.Equal((false, "standin-game exited"), rest.Calls.Last());
        st = await g.State();
        Assert.False(st.GetProperty("rest").GetProperty("on").GetBoolean());
        Assert.StartsWith("Mantle rested under a minute (standin-game runs) and is back: standin-game exited", st.GetProperty("rest").GetProperty("last").GetProperty("text").GetString());
    }, testGames: new[] { "standin-game.exe" });

    [Fact]
    public void Switched_off_in_the_tray_the_set_command_refuses_in_words() => T(true, async g =>
    {
        g.R.Host.SetEnabled("gamemode", false, "the test");
        var st = await g.Set(true);
        Assert.False(st.GetProperty("ok").GetBoolean());
        Assert.Equal("game mode is switched off in Mantle's tray menu", st.GetProperty("reason").GetString());
    });

    [Fact]
    public void The_tile_switches_are_saved_and_the_boost_off_is_said() => T(true, async g =>
    {
        var st = (await g.R.Bus.CallAsync("gamemode.boost", new JsonObject { ["on"] = false })).Result;
        Assert.False(st.GetProperty("boost").GetBoolean());
        Assert.True(g.R.Logged("gamemode: boost: switched off (the game mode tile)"));
        st = (await g.R.Bus.CallAsync("gamemode.auto", new JsonObject { ["on"] = false })).Result;
        Assert.False(st.GetProperty("auto").GetBoolean());
        g.Fg = FgGame; g.Procs = new() { Valorant, App };
        g.R.Time.AdvanceMs(4000);
        Assert.False((await g.State()).GetProperty("on").GetBoolean());   // automatic is off: only by hand
        st = await g.Set(true);
        Assert.True(st.GetProperty("on").GetBoolean());
        Assert.Equal("manual", st.GetProperty("how").GetString());
        Assert.StartsWith("skipped: left as they are: the boost is off", Act(st, "background"));
    });
}
