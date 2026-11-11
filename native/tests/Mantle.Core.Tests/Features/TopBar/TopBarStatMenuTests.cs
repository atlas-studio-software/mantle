using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The stat dropdowns (ui/topbar/topbar.js renderStatMenu / askDetail / renewDetail / openTaskMgr and the keydown for
/// a stat's dropdown) on Electron's mock sample (ui/topbar/mock-mantle.js STATS.ok), and statsDetail's answers.
/// </summary>
public sealed partial class TopBarFeatureTests
{
    static string Words(System.Collections.Generic.IReadOnlyList<StatRow> rows)
        => string.Join(" | ", rows.Select(r => r.Kind switch
        {
            StatRowKind.Row => r.Text + " = " + r.Value,
            StatRowKind.Cores => "cores " + string.Join(",", r.Cores!),
            StatRowKind.Info => (r.Bad ? "warn: " : "info: ") + r.Text,
            StatRowKind.Sep => "---",
            StatRowKind.TaskMgr => r.Text + " [" + r.Value + "]",
            _ => "# " + r.Text,
        }));

    [Fact]
    public void Stat_dropdowns_say_what_Electrons_say_for_each_meter_TOP_103_104_105_106_108_138()
    {
        var s = TopBarFixtures.Stats("ok");
        Assert.Equal("# CPU · 23% | cores 22,61,9,14,37,5,71,18 | # Top processes | Code = 11% | chrome = 6% | Mantle = 1% | MsMpEng = 1% | explorer = 0% | --- | Open Task Manager [Ctrl+Shift+Esc]",
            Words(TopBarStatMenu.Rows("cpu", s)));
        Assert.Equal("# Memory · 9.7 of 15.7 GB | Committed = 13.1 of 23.4 GB | # Top processes | chrome = 2.1 GB | Code = 1.4 GB | Mantle = 0.3 GB | --- | Open Task Manager [Ctrl+Shift+Esc]",
            Words(TopBarStatMenu.Rows("mem", s)));
        Assert.Equal("# GPU · 8% · Intel(R) UHD Graphics | 3D = 8% | VideoDecode = 2% | Copy = 0% | Dedicated memory = 0.1 GB | Shared memory = 1.2 GB | # Top processes | dwm = 4% | chrome = 3% | --- | Open Task Manager [Ctrl+Shift+Esc]",
            Words(TopBarStatMenu.Rows("gpu", s)));
        Assert.Equal("# Network · ↓2.4 MB/s ↑88 KB/s | Wi-Fi = ↓2.4 MB/s ↑88 KB/s | --- | Open Task Manager [Ctrl+Shift+Esc]",
            Words(TopBarStatMenu.Rows("net", s)));
        // PING: the server it measures, then every server of the last pick; no Task Manager row (TOP-138)
        Assert.Equal("# Ping · 23 ms · Valorant US West (N. California) | Measured to = dynamodb.us-west-1.amazonaws.com (AWS us-west-1) | Every = 2 s, by ping"
            + " | # Closest server, picked every 10 min | US West (N. California) = 17 ms | US West (Oregon) = 38 ms | US Central (Texas) = 56 ms",
            Words(TopBarStatMenu.Rows("ping", s)));
        var p = new JsonObject { ["ping"] = new JsonObject { ["state"] = "timeout", ["ms"] = 40, ["server"] = "EU West", ["method"] = "tcp", ["reason"] = "no reply for 3 tries",
            ["picked"] = new JsonArray((JsonNode)new JsonObject { ["name"] = "A", ["why"] = "could not look up a.example" }, new JsonObject { ["name"] = "B", ["why"] = "timed out" }) } };
        Assert.Equal("# Ping · -- · EU West | Measured to =  | Every = 2 s, by connection time | # Closest server, picked every 10 min | A = name not found | B = no reply | warn: no reply for 3 tries",
            Words(TopBarStatMenu.Rows("ping", p)));
        Assert.False(TopBarStatMenu.Rows("ping", new JsonObject { ["ping"] = new JsonObject { ["state"] = "measuring", ["reason"] = "measuring" } }).Last().Bad);
    }

    [Fact]
    public void Until_the_details_come_the_dropdown_says_so_and_a_missing_reading_is_a_warn_line_TOP_107()
    {
        var bare = new JsonObject { ["cpu"] = new JsonObject { ["pct"] = 12 }, ["gpu"] = new JsonObject { ["pct"] = null, ["reason"] = "no GPU counters" },
            ["mem"] = new JsonObject { ["used"] = 1073741824.0, ["total"] = 2147483648.0, ["reason"] = "commit not read" } };
        Assert.Equal("# CPU · 12% | info: Top processes arrive with the next sample. | --- | Open Task Manager [Ctrl+Shift+Esc]", Words(TopBarStatMenu.Rows("cpu", bare)));
        Assert.Equal("# GPU · -- | warn: no GPU counters | info: Top processes arrive with the next sample. | --- | Open Task Manager [Ctrl+Shift+Esc]", Words(TopBarStatMenu.Rows("gpu", bare)));
        Assert.Contains("info: commit not read", Words(TopBarStatMenu.Rows("mem", bare)));   // a reading with a reason: not warn
        Assert.Equal("# Network · ↓-- ↑-- | --- | Open Task Manager [Ctrl+Shift+Esc]", Words(TopBarStatMenu.Rows("net", null)));
        var nogpu = TopBarStatMenu.Rows("gpu", TopBarFixtures.Stats("nogpu"));
        Assert.True(nogpu.Single(r => r.Kind == StatRowKind.Info && r.Text.StartsWith("Windows has no GPU")).Bad);
    }

    [Fact]
    public void A_paused_push_says_stats_paused_with_its_reason_and_a_sample_draws_cells_TOP_099()
    {
        Assert.Equal("the screen is locked", TopBarStatMenu.Paused(TopBarFixtures.Stats("paused")));
        Assert.Equal("paused", TopBarStatMenu.Paused(new JsonObject { ["paused"] = true }));
        Assert.Null(TopBarStatMenu.Paused(TopBarFixtures.Stats("ok")));
        Assert.Null(TopBarStatMenu.Paused(new JsonObject { ["paused"] = false, ["reason"] = "x" }));
        Assert.Equal("stats paused", TopBarStatMenu.PausedWords);
    }

    [Fact]
    public void Details_are_asked_on_open_renewed_after_5_s_never_for_ping_and_samples_keep_the_shape_TOP_100_101_102_109()
    {
        Assert.True(TopBarStatMenu.AsksDetail("cpu")); Assert.False(TopBarStatMenu.AsksDetail("ping")); Assert.False(TopBarStatMenu.AsksDetail("disk"));
        Assert.Equal((5000, 450), (TopBarStatMenu.DetailRenewMs, TopBarStatMenu.RenewAfterSampleMs));
        Assert.False(TopBarStatMenu.RenewDue("mem", 4999, 0, false));
        Assert.True(TopBarStatMenu.RenewDue("mem", 5000, 0, false));
        Assert.False(TopBarStatMenu.RenewDue("mem", 9000, 0, true));   // one renewal waits at a time
        Assert.False(TopBarStatMenu.RenewDue("ping", 9000, 0, false));
        // hung from the cell's right edge, never past the window's left (TOP-100)
        Assert.Equal(620, TopBarStatMenu.Left(900, 280)); Assert.Equal(4, TopBarStatMenu.Left(200, 280));
        // two samples of the same shape are drawn in place; details arriving are a new shape (TOP-102)
        var a = TopBarFixtures.Stats("ok")!; var b = TopBarFixtures.Stats("ok")!;
        b["cpu"]!["pct"] = 71.0; b["cpu"]!["cores"]![3] = 90.0; b["cpu"]!["top"]![0]!["name"] = "Teams";
        Assert.Equal(TopBarStatMenu.Shape(TopBarStatMenu.Rows("cpu", a)), TopBarStatMenu.Shape(TopBarStatMenu.Rows("cpu", b)));
        Assert.NotEqual(TopBarStatMenu.Shape(TopBarStatMenu.Rows("cpu", new JsonObject { ["cpu"] = new JsonObject { ["pct"] = 3 } })), TopBarStatMenu.Shape(TopBarStatMenu.Rows("cpu", a)));
        // TOP-109: Mantle's Task Manager did not open, so Windows' was asked for
        Assert.Equal("Opened Windows' Task Manager: Mantle's did not open (it is resting)", TopBarStatMenu.TaskMgrNote("it is resting", true, null, false));
        Assert.Equal("Task Manager: Mantle's did not open (it is resting) and Windows' did not either: access denied", TopBarStatMenu.TaskMgrNote("it is resting", false, "access denied", false));
        Assert.Equal("Task Manager: Mantle's did not open (x); dry run (--test): would have run T", TopBarStatMenu.TaskMgrNote("x", true, "dry run (--test): would have run T", true));
    }

    [Fact]
    public void From_a_stat_dropdown_left_and_right_walk_the_titles_from_the_first_and_escape_closes_TOP_090()
    {
        var m = TopBarMenu.ForStat("cpu");
        Assert.Equal(0, m.Ti);
        Assert.Equal(new MenuStep(MenuDo.OpenTitle, Index: +1, SelectFirst: true), m.Key("ArrowRight", 0, null, null));
        Assert.Equal(new MenuStep(MenuDo.OpenTitle, Index: -1, SelectFirst: true), m.Key("ArrowLeft", 0, null, null));
        Assert.Equal(1, TopBarMenu.StepTitle(m.Ti, +1, 5)); Assert.Equal(4, TopBarMenu.StepTitle(m.Ti, -1, 5));
        Assert.Equal(new MenuStep(MenuDo.Close, Refocus: true), m.Key("Escape", 0, null, null));
        foreach (var k in new[] { "ArrowDown", "ArrowUp", "Enter", " ", "c" }) Assert.Equal(MenuStep.None, m.Key(k, 0, null, null));
    }

    [Fact]
    public void StatsDetail_asks_the_sidecar_for_a_known_meter_only_TOP_101() => T(async (r, s) =>
    {
        await Up(r, s);
        var ok = await r.Bus.CallAsync("topbar.statsDetail", new JsonObject { ["kind"] = "mem" });
        Assert.True(Flag(ok.Result, "ok"), ok.Result.ToString());
        Assert.Contains(r.Platform.Calls, c => c.Verb == "topbar.stats" && c.ArgsJson.Contains("\"detail\":\"mem\""));
        int n = Calls(r, "topbar.stats");
        Assert.Equal("there are no details for that reading", Reason((await r.Bus.CallAsync("topbar.statsDetail", new JsonObject { ["kind"] = "disk" })).Result));
        Assert.Equal(n, Calls(r, "topbar.stats"));
    });

    [Fact]
    public void StatsDetail_is_refused_while_the_bar_has_stepped_aside_and_not_peeking_TOP_101()
    {
        Assert.Equal("the stats are paused while the top bar is hidden by hand", TopBarStatMenu.Refusal(true, false, "hidden"));
        Assert.Equal("the stats are paused while a game has the screen", TopBarStatMenu.Refusal(true, false, "game"));
        Assert.Null(TopBarStatMenu.Refusal(true, true, "hidden"));   // peeking: the bar is on screen, so are its details
        Assert.Null(TopBarStatMenu.Refusal(false, false, null));
    }

    [Fact]
    public void StatsDetail_on_the_made_up_sample_answers_itself_TOP_101() => T(async (r, s) =>
    {
        await Up(r, s);
        await r.Bus.CallAsync("topbar.testFixture", new JsonObject { ["app"] = "npp", ["stats"] = "ok" });
        int n = Calls(r, "topbar.stats");
        var fx = await r.Bus.CallAsync("topbar.statsDetail", new JsonObject { ["kind"] = "gpu" });
        Assert.True(Flag(fx.Result, "fixture"), fx.Result.ToString());
        Assert.Equal(n, Calls(r, "topbar.stats"));
    });
}
