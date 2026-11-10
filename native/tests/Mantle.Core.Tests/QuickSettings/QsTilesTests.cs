using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.QuickSettings;
using Mantle.Core.Tests.Features;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.QuickSettings;

/// <summary>
/// Quick settings' second tile row: QsTiles (lib/features/_qstiles-core.js and _project-core.js tile(), the words) and
/// QsService.Tiles (lib/features/qstiles.js and project.js's open/cast). Every switch and way out in a --test run is a
/// dry run: logged with what it would have asked, never sent.
/// </summary>
public class QsTilesTests
{
    static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

    // the Electron mock's state (ui/bar/mock-mantle.js qxState / pjState with no query)
    public const string MockTiles = "{\"on\":true,\"dnd\":{\"ok\":true,\"on\":false,\"writable\":true,\"reason\":null,\"rule\":null}," +
        "\"airplane\":{\"ok\":true,\"on\":false,\"canSet\":true,\"reason\":null},\"focus\":null,\"timer\":{\"ok\":true,\"reason\":null}}";
    public const string MockProject = "{\"on\":true,\"phase\":\"closed\",\"read\":{\"current\":\"extend\",\"available\":[\"internal\",\"clone\",\"extend\",\"external\"],\"unavailable\":{},\"connected\":2,\"pending\":null}}";
    const string Fixture = "{\"qstiles\":" + MockTiles + ",\"project\":" + MockProject + "}";

    sealed class Kit
    {
        public readonly FakePlatform P = new();
        public readonly TestEvents E = new();
        public readonly ManualScheduler S = new();
        public readonly List<string> Lines = new();
        public QsService Svc = null!;
        public Kit Make(bool test, string? fixture = null)
        {
            P.Start();
            Svc = new QsService(new QsOptions { Platform = P, Events = E, Host = S, Test = test, Log = Lines.Add, Fixture = fixture });
            return this;
        }
        public string Log => string.Join("\n", Lines);
        public string[] Writes => P.Calls.Where(c => c.Verb is "sysnotify.dnd" or "netair.set" or "keys").Select(c => c.Verb + " " + c.ArgsJson).ToArray();
    }

    static JsonObject State(string tiles, string? project = null)
    {
        var s = O(tiles);
        s["project"] = project == null ? null : O(project);
        return s;
    }

    [Fact]
    public void The_mock_state_draws_the_row_as_qs_tiles_js_does()
    {
        var st = State(MockTiles, MockProject);
        Assert.Equal(new[] { "airplane", "dnd", "focus", "project", "cast" }, QsTiles.Order.Select(o => o.Key));
        var a = QsTiles.Airplane(st);
        Assert.Equal(("Off", false, false), (a.Sub, a.On, a.Dead));
        Assert.Equal("Turn airplane mode on: Wi-Fi, Bluetooth and mobile data go off", a.Tip);
        Assert.Equal("Off", QsTiles.Dnd(st).Sub);
        Assert.Equal("Turn Do not disturb on: banners are silenced, Mantle’s too", QsTiles.Dnd(st).Tip);
        var f = QsTiles.Focus(st, 0);
        Assert.Equal("30 min", f.Sub);
        Assert.Equal("Start 30 minutes of focus: Mantle’s timer and Do not disturb", f.Tip);
        var p = QsTiles.Project(st);
        Assert.Equal(("Extend", false), (p.Sub, p.On));
        Assert.Equal("Now: Extend. Click to choose how your screens are used (Win+P)", p.Tip);
        Assert.Equal("Windows’ panel", QsTiles.Cast().Sub);
    }

    [Fact]
    public void Do_not_disturb_words_never_fake_an_off()
    {
        Assert.Equal("Reading…", QsTiles.Dnd(O("{\"on\":true}")).Sub);
        Assert.True(QsTiles.Dnd(O("{\"on\":true}")).Reading);
        Assert.Equal("Unknown", QsTiles.Dnd(O("{\"on\":true,\"readError\":\"x\"}")).Sub);
        var dead = QsTiles.Dnd(O("{\"dnd\":{\"ok\":false,\"writable\":false,\"reason\":\"windows’ quiet-hours object is not available\"}}"));
        Assert.Equal(("Unavailable", true), (dead.Sub, dead.Dead));
        Assert.Equal("Do not disturb: Windows’ quiet-hours object is not available", dead.Tip);
        Assert.Equal("On · Focus", QsTiles.Dnd(O("{\"dnd\":{\"ok\":true,\"on\":true},\"focus\":{\"on\":true,\"endAt\":0,\"dndByFocus\":true}}")).Sub);
        Assert.Equal("Off · a rule is on", QsTiles.Dnd(O("{\"dnd\":{\"ok\":true,\"on\":false,\"rule\":\"alarms only\"}}")).Sub);
        Assert.Equal("Switched off", QsTiles.Dnd(O("{\"on\":false}")).Sub);
    }

    [Fact]
    public void Airplane_mode_is_dead_with_its_reason_when_it_cannot_be_switched()
    {
        var v = QsTiles.Airplane(O("{\"airplane\":{\"ok\":true,\"on\":null,\"canSet\":false,\"reason\":\"windows’ radio management is not registered on this PC\"}}"));
        Assert.Equal(("Unavailable", true, false), (v.Sub, v.Dead, v.On));
        Assert.Equal("Airplane mode: Windows’ radio management is not registered on this PC", v.Tip);
        Assert.Equal(("On", true), (QsTiles.Airplane(O("{\"airplane\":{\"ok\":true,\"on\":true,\"canSet\":false}}")).Sub, QsTiles.Airplane(O("{\"airplane\":{\"ok\":true,\"on\":true,\"canSet\":false}}")).Dead));
        Assert.True(QsTiles.Airplane(O("{\"airplane\":{\"ok\":true,\"on\":true,\"canSet\":true}}")).On);
    }

    [Fact]
    public void Focus_counts_down_in_whole_minutes_and_is_dead_without_the_timer()
    {
        var run = QsTiles.Focus(O("{\"focus\":{\"on\":true,\"endAt\":1500000,\"dndByFocus\":true}}"), 60000);
        Assert.Equal("24 min left", run.Sub);
        Assert.Equal("Focus: 24 min left on Mantle’s timer, with Do not disturb on.\nClick to end it (Do not disturb goes back off)", run.Tip);
        Assert.Equal("1 min left", QsTiles.Focus(O("{\"focus\":{\"on\":true,\"endAt\":10}}"), 20).Sub);
        var dead = QsTiles.Focus(O("{\"timer\":{\"ok\":false,\"reason\":\"" + QsService.NoTimer + "\"}}"), 0);
        Assert.Equal(("Unavailable", true), (dead.Sub, dead.Dead));
        Assert.Equal("Focus: " + QsService.NoTimer, dead.Tip);
        Assert.EndsWith("(Do not disturb cannot be switched here: it is still being read)", QsTiles.Focus(O("{}"), 0).Tip);
    }

    [Fact]
    public void Project_words_one_display_and_a_failed_read()
    {
        Assert.Equal("Choose…", QsTiles.Project(O("{}")).Sub);
        var err = QsTiles.Project(O("{\"project\":{\"on\":true,\"read\":null,\"readError\":\"the display layout could not be read\"}}"));
        Assert.Equal("Unknown", err.Sub);
        Assert.Equal("Project: your displays could not be read (the display layout could not be read). Click to choose anyway", err.Tip);
        var one = QsTiles.Project(O("{\"project\":{\"read\":{\"current\":\"internal\",\"connected\":1}}}"));
        Assert.Equal("PC screen only", one.Sub);
        Assert.StartsWith(QsTiles.OneDisplay, one.Tip);
        Assert.Equal("Second screen", QsTiles.Project(O("{\"project\":{\"read\":{\"current\":\"external\",\"connected\":2}}}")).Sub);
        Assert.Equal("Custom layout", QsTiles.Project(O("{\"project\":{\"read\":{\"current\":\"custom\",\"connected\":2}}}")).Sub);
    }

    [Fact]
    public void The_guess_moves_the_tile_and_focus_takes_do_not_disturb()
    {
        var st = State(MockTiles);
        var g = QsTiles.Guess(st, "focus", true, 1000);
        Assert.True(QsParts.IsTrue(g["dnd"]!["on"]));
        Assert.True(QsParts.IsTrue(g["focus"]!["dndByFocus"]));
        Assert.Equal(1000 + 30 * 60000.0, QsParts.Num(g["focus"]!["endAt"]));
        Assert.False(QsParts.IsTrue(st["dnd"]!["on"]));   // a new object: the held state is not touched
        Assert.True(QsParts.IsTrue(QsTiles.Guess(st, "airplane", true, 0)["airplane"]!["on"]));
        Assert.Equal("Test run: would x", QsTiles.Note("dnd", true, "test run: would x"));
        Assert.Null(QsTiles.Note("dnd", true, null));
        Assert.Equal("Do not disturb did not change, and Mantle gave no reason", QsTiles.Note("dnd", false, null));
    }

    [Fact]
    public async Task A_test_run_dry_runs_every_switch_and_way_out_and_sends_nothing()
    {
        var k = new Kit().Make(test: true, fixture: Fixture);
        var st = k.Svc.TilesState();
        Assert.Equal("Off", QsTiles.Dnd(st).Sub);
        Assert.Equal("Extend", QsTiles.Project(st).Sub);

        var d = await k.Svc.TilesSetAsync("dnd", true);
        Assert.True(QsParts.IsTrue(d["ok"]) && QsParts.IsTrue(d["dryRun"]));
        Assert.Equal("test run: would turn Do not disturb on (its tile)", QsParts.Str(d["reason"]));
        Assert.Contains("qs: dry run (--test): Do not disturb on would ask the sidecar sysnotify.dnd {\"on\":true}; nothing was sent, Windows was not changed", k.Log);
        Assert.False(QsParts.IsTrue(d["state"]!["dnd"]!["on"]));   // the state is not changed by a test

        var a = await k.Svc.TilesSetAsync("airplane", true);
        Assert.Equal("test run: would turn airplane mode on (Wi-Fi, Bluetooth and mobile data go off)", QsParts.Str(a["reason"]));
        Assert.Contains("qs: dry run (--test): airplane mode on would ask the sidecar netair.set {\"on\":true}", k.Log);

        var f = await k.Svc.TilesSetAsync("focus", true);
        Assert.Equal("test run: would start a 30 min Focus timer and turn Do not disturb on", QsParts.Str(f["reason"]));
        Assert.Contains("qs: dry run (--test): Focus would start a 30 min timer labelled \"Focus\" and ask the sidecar sysnotify.dnd {\"on\":true}", k.Log);

        var c = await k.Svc.TilesOpenAsync("cast");
        Assert.Equal("test run: would open Windows' Cast panel (Win+K)", QsParts.Str(c["reason"]));
        Assert.Contains("qs: dry run (--test): would open Windows' Cast panel (sidecar keys win+k); nothing was opened", k.Log);
        var p = await k.Svc.TilesOpenAsync("project");
        Assert.Contains("qs: dry run (--test): would open Windows' Project choices (sidecar keys win+p); nothing was opened", k.Log);
        Assert.True(QsParts.IsTrue(p["dryRun"]));

        Assert.False(QsParts.IsOk(await k.Svc.TilesSetAsync("hotspot", true)));
        Assert.Empty(k.P.Calls);   // a fixture run reads nothing and sends nothing
        Assert.Empty(k.Writes);
    }

    [Fact]
    public async Task A_test_run_without_a_fixture_reads_the_sidecar_but_never_writes()
    {
        var k = new Kit().Make(test: true);
        k.P.Answer("sysnotify.dndGet", "{\"ok\":true,\"on\":false,\"profile\":\"x\",\"writable\":true,\"reason\":null}");
        k.P.Answer("netair.get", "{\"ok\":true,\"airplane\":false,\"canSet\":true,\"reason\":null}");
        k.P.Answer("toasts.quiet", "{\"dnd\":2,\"quns\":5}");
        k.P.Answer("sysdisplay.project", "{\"current\":\"internal\",\"connected\":1,\"available\":[\"internal\"]}");
        var st = await k.Svc.TilesRefreshAsync("the panel opened");
        Assert.Equal("Off · a rule is on", QsTiles.Dnd(st).Sub);
        Assert.Equal("alarms only", QsParts.Str(st["dnd"]!["rule"]));
        Assert.Equal("Off", QsTiles.Airplane(st).Sub);
        Assert.Equal("PC screen only", QsTiles.Project(st).Sub);
        // no timer in the native app: Focus is dead with that reason, and a click is refused before anything is touched
        Assert.True(QsTiles.Focus(st, 0).Dead);
        var f = await k.Svc.TilesSetAsync("focus", true);
        Assert.False(QsParts.IsOk(f));
        Assert.Equal("Focus did not start: " + QsService.NoTimer, QsParts.Str(f["reason"]));
        await k.Svc.TilesSetAsync("dnd", true);
        await k.Svc.TilesSetAsync("airplane", false);
        await k.Svc.TilesOpenAsync("cast");
        Assert.Empty(k.Writes);
        Assert.Contains(k.E.Published, e => e.Name == QsService.TilesEvent);
    }

    [Fact]
    public async Task A_failed_read_says_it_plainly()
    {
        var k = new Kit().Make(test: true);
        var st = await k.Svc.TilesRefreshAsync("start");
        Assert.Equal("Unavailable", QsTiles.Dnd(st).Sub);
        Assert.Equal("Unknown", QsTiles.Project(st).Sub);
        Assert.Contains("qs: the tiles were read (start)", k.Log);
        Assert.Equal(QsService.HelperDown, QsService.Plain("the sidecar timed out"));
        Assert.Equal("Windows refused", QsService.Plain("Windows refused\nstack"));
    }

    [Fact]
    public async Task Live_a_switch_is_sent_and_read_back()
    {
        var k = new Kit().Make(test: false);
        k.P.Answer("netair.get", "{\"ok\":true,\"airplane\":false,\"canSet\":true}");
        k.P.Answer("netair.set", "{\"ok\":true,\"now\":false,\"reason\":null}");
        k.P.Answer("sysnotify.dnd", "{\"ok\":true,\"on\":true}");
        await k.Svc.TilesRefreshAsync("start");
        var a = await k.Svc.TilesSetAsync("airplane", true);
        Assert.False(QsParts.IsOk(a));
        Assert.Equal("Windows still reports airplane mode off", QsParts.Str(a["reason"]));
        var d = await k.Svc.TilesSetAsync("dnd", true);
        Assert.True(QsParts.IsOk(d));
        Assert.Equal(new[] { "netair.set {\"on\":true}", "sysnotify.dnd {\"on\":true}" }, k.Writes);
    }
}
