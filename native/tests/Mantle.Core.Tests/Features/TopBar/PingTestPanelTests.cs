using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The ping test's part of the PING dropdown (ui/topbar/pingtest.js; its checks in ui/topbar/pingtest-shots.js) on
/// PingTestFake's made-up runs, which carry pingtest-shots.js's rows and caps.
/// </summary>
public sealed class PingTestPanelTests
{
    sealed class Main
    {
        public readonly List<(string Verb, JsonObject Args)> Calls = new();
        public Func<string, JsonObject, JsonObject> Answer = (_, _) => new JsonObject { ["ok"] = true };
        public int Renders;
        public PingTestPanel Panel = null!;
        public double Now = 1_000_000;
        public Main()
        {
            Panel = new PingTestPanel((v, a) => { Calls.Add((v, a)); return Task.FromResult(Answer(v, a)); }, () => Renders++, () => Now);
        }
    }

    static string Words(IEnumerable<PtRow> rows) => string.Join(" | ", rows.Select(r => r.Kind switch
    {
        PtKind.Sep => "---",
        PtKind.Tabs or PtKind.Chips => "[" + string.Join(", ", r.Cells!.Select(c => c.Text + (c.Cls == "on" ? "*" : ""))) + "]",
        PtKind.Head => "# " + r.Text,
        PtKind.Info => (r.Cls.Length > 0 ? r.Cls + ": " : "") + r.Text,
        PtKind.Go => "> " + r.Text + (r.Value is { Length: > 0 } v ? " [" + v + "]" : "") + (r.Cls.Length > 0 ? " (" + r.Cls + ")" : ""),
        PtKind.Row2 => r.Text + " = " + r.Value + (r.Cls == "hi" ? " (amber)" : ""),
        PtKind.Progress => "progress " + r.Progress,
        PtKind.NameBox => "box '" + r.Value + "'",
        PtKind.HistRow => "row " + string.Join(" / ", r.Cells!.Select(c => c.Text + (c.Badge != null ? " " + c.Badge : "") + (c.Cls == "hi" ? "!" : ""))),
        _ => "table " + string.Join(" ; ", r.Cells!.Select(c => c.Text + (c.Cls == "hi" || c.Cls == "worse" ? "!" : "") + (c.Badge != null ? "+" + c.Badge : ""))),
    }));

    static JsonObject DoneRun(PingTestFake f, double t)
    {
        f.Start(t);
        JsonObject? last = null;
        while (f.Step(t += 1000) is { } s) last = s;
        return last!;
    }

    [Fact]
    public void Idle_shows_two_tabs_and_Run_ping_test_with_its_cost_TOP_139_140()
    {
        var m = new Main();
        Assert.Equal("--- | [Test*, History] | # Ping test | > Run ping test [about 30 s · up to 170 MB] | Pings every Valorant location and your router 20 times, then measures download and upload speed with speed.cloudflare.com (download from a nearby Linode server if Cloudflare is busy). Uses up to 170 MB of data.",
            Words(m.Panel.Rows()));
    }

    [Fact]
    public async Task Run_shows_starting_at_once_and_a_refusal_rereads_the_state_TOP_141_144()
    {
        var m = new Main();
        m.Answer = (v, a) => (string?)a["action"] == "start" ? new JsonObject { ["ok"] = false, ["reason"] = "a ping test is already running" } : new JsonObject { ["phase"] = "idle" };
        var t = m.Panel.Act("pt-start");
        Assert.Contains("Finding your router and the servers…", Words(m.Panel.Rows()));
        await t;
        Assert.Equal(new[] { "start", "state" }, m.Calls.Select(c => (string)c.Args["action"]!));
        Assert.Contains("bad: The ping test could not start: a ping test is already running", Words(m.Panel.Rows()));

        // a failed state read says so and is tried again at most every 5 s while the dropdown draws
        m.Calls.Clear();
        m.Answer = (_, _) => new JsonObject { ["ok"] = false, ["reason"] = "the Mantle helper is not running" };
        await m.Panel.Read();
        Assert.Contains("bad: The last ping test could not be read: the Mantle helper is not running", Words(m.Panel.Rows()));
        Assert.Single(m.Calls);
        m.Now += 5001;
        m.Panel.Rows();
        Assert.Equal(2, m.Calls.Count);
    }

    [Fact]
    public void Running_shows_progress_steps_and_Cancel_TOP_142()
    {
        var m = new Main(); var f = new PingTestFake();
        f.Start(1000);
        m.Panel.OnRun(f.Step(2000));
        var w = Words(m.Panel.Rows());
        Assert.Contains("progress 0.3 | step: Pinging 7 places, 20 times each…", w);
        Assert.Contains("table Place ; Avg ; Best–worst ; Jitter ; Loss ; Your router", w);
        Assert.Contains("US East (Georgia) ; waiting…", w);
        Assert.EndsWith("> Cancel", w);
        m.Panel.OnRun(f.Step(3000));
        Assert.Contains("step: Download speed… 58.3 Mbps", Words(m.Panel.Rows()));
        Assert.Contains("Download = 58.3 Mbps | Upload = next", Words(m.Panel.Rows()));
    }

    [Fact]
    public void A_finished_run_amber_on_exactly_the_bad_values_with_the_verdict_and_speeds_TOP_145_146_147_148_149_150_151()
    {
        var m = new Main(); var f = new PingTestFake();
        var done = DoneRun(f, 1_760_000_000_000);
        m.Panel.OnRun(done);
        var w = Words(m.Panel.Rows());
        Assert.Contains("# Ping test · " + PingTestPanel.Clock((double)done["endedAt"]!), w);
        Assert.Contains("net: On Wi-Fi “Test Wi-Fi” | link: 5 GHz · ch 157 · 70% · 1201/576 Mbps · -55 dBm · 802.11ax · access point 02:00:5e:00:00:01", w);
        Assert.Contains("US West (N. California)+best ; 16 ms ; 14–19 ; 1.2 ms ; 0%", w);
        Assert.Contains("US Central (Illinois) ; 65 ms ; 63–74 ; 2.6 ms ; 5%!", w);
        Assert.Contains("US East (N. Virginia) ; 102 ms! ; 77–131 ; 13 ms! ; 0%", w);
        Assert.Contains("Your router ; 2.4 ms ; 1.6–4.1 ; 0.6 ms ; 0%", w);
        Assert.Contains("verdict: No lag found: your router answers in 2.4 ms and the closest server in 16 ms.", w);
        Assert.Contains("key: Jitter: how much the ping jumps between replies. Loss: pings that never came back. Amber: 100+ ms (router 20+), jitter 10+ ms, any loss.", w);
        Assert.Contains("# Speed | Download = 62.0 Mbps | Upload = 30.8 Mbps | Download: 62.0 Mbps (Cloudflare) | Upload: 30.8 Mbps (Cloudflare)", w);
        Assert.Contains("Used 85 MB of data with Cloudflare.", w);
        Assert.DoesNotContain("> Save…", w);   // no history read yet: not kept, so no Save…
        Assert.Contains("> Run again [about 30 s · up to 85 MB]", w);

        // the second (even) made-up run: a slow router, so the lag starts at home
        var lag = DoneRun(f, 1_760_000_100_000);
        m.Panel.OnRun(lag);
        Assert.Contains("verdict bad: The lag starts at your Wi-Fi: your own router takes 28 ms on average, jumps by 26 ms.", Words(m.Panel.Rows()));
    }

    [Fact]
    public void Verdict_and_words_match_pingtest_js()
    {
        var router = new JsonObject { ["kind"] = "router", ["got"] = 20, ["avg"] = 2.0, ["jitter"] = 0.4, ["loss"] = 0, ["wifi"] = false };
        var server = new JsonObject { ["kind"] = "server", ["got"] = 20, ["avg"] = 115.8, ["jitter"] = 1, ["loss"] = 0, ["best"] = true };
        var s = new JsonObject { ["rows"] = new JsonArray(router, server) };
        Assert.Equal("Your router answers in 2.0 ms, so your home network is fine: the lag is out on the internet.", PingTestPanel.Verdict(s)!.Value.Text);
        Assert.True(PingTestPanel.Verdict(s)!.Value.Bad);
        Assert.Null(PingTestPanel.Verdict(new JsonObject { ["rows"] = new JsonArray() }));
        Assert.Equal("Wi-Fi (name hidden)", PingTestPanel.NetWords(new JsonObject { ["kind"] = "wifi" }, true));
        Assert.Equal("On Ethernet (a cable)", PingTestPanel.NetWords(new JsonObject { ["kind"] = "ethernet", ["name"] = "Ethernet" }, false));
        Assert.Equal("The network was not recorded for this test.", PingTestPanel.NetWords(null, false));
        Assert.Equal("cable · 1000 Mbps", PingTestPanel.LinkWords(new JsonObject { ["kind"] = "ethernet", ["linkMbps"] = 1000 }, false));
        Assert.Equal("5 GHz 70%", PingTestPanel.LinkWords(new JsonObject { ["kind"] = "wifi", ["band"] = "5 GHz", ["signal"] = 70, ["channel"] = 36 }, true));
        Assert.Equal("5 GHz · ch 36. Not given: RSSI (no driver)", PingTestPanel.LinkWords(new JsonObject { ["kind"] = "wifi", ["band"] = "5 GHz", ["channel"] = 36, ["missing"] = new JsonObject { ["rssi"] = "no driver" } }, false));
        Assert.Equal(-1, PingTestPanel.Judge(20, 10, true, 1));
        Assert.Equal(0, PingTestPanel.Judge(20, 20.5, true, 1));
        Assert.Equal(1, PingTestPanel.Judge(60, 30, false, 3));
    }

    [Fact]
    public async Task History_tab_save_name_box_open_delete_and_compare_TOP_153_155_156_157_158()
    {
        var m = new Main(); var f = new PingTestFake();
        var h = new PingTestHistory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mantle-pt-none-" + Guid.NewGuid().ToString("N")), new Mantle.Core.Data.SafeFile(Mantle.Core.Data.WriteGate.Never("memory only in this test")));
        m.Answer = (v, a) =>
        {
            string act = (string)a["action"]!;
            if (v == "topbar.pingTest") return f.State();
            var r = act switch { "save" => h.Save((string)a["id"]!, a["name"]), "rename" => h.Rename((string)a["id"]!, a["name"]), "delete" => h.Remove((string)a["id"]!), _ => new JsonObject { ["ok"] = true } };
            r["history"] = h.View();
            return r;
        };
        await m.Panel.Act("pt-tab:history");
        Assert.Contains("No tests yet. Every finished test is kept here: the last 50, and every one you save, for good.", Words(m.Panel.Rows()));

        var a = DoneRun(f, 1_760_000_000_000); h.Add(a);
        var b = DoneRun(f, 1_760_000_900_000); h.Add(b);
        m.Panel.OnHistory(h.View());
        var w = Words(m.Panel.Rows());
        Assert.Contains("[Test, History · 2*]", w);
        Assert.Contains("[All*, Test Wi-Fi 5G, Test Wi-Fi]", w);   // two networks: the filter chips
        Assert.Contains("row " + PingTestPanel.Stamp(1_760_000_900_000) + " / Test Wi-Fi 5G · 2.4 GHz 41% / 16 ms / ↓62.0 ↑30.8", w);
        Assert.EndsWith("Kept: the last 50 unsaved (2 now) and every saved one.", w);

        await m.Panel.Act("pt-filter:1");
        Assert.Equal(1, m.Panel.Rows().Count(r => r.Kind == PtKind.HistRow));

        // open one: its result, Save…, Delete, Compare with…, Back
        string idA = PingTestHistory.Id(a["startedAt"], a["run"]);
        await m.Panel.Act("pt-filter:all");
        await m.Panel.Act("pt-open:" + idA);
        w = Words(m.Panel.Rows());
        Assert.Contains("> Save… [name it, keep it for good] | > Delete (quiet) | > Compare with… [pick another test] | > Back to history (quiet)", w);

        // Save… opens the name box; Enter saves the typed name
        await m.Panel.Act("pt-save:" + idA);
        Assert.Contains("box '' | > Save [Enter] | > Cancel [Esc] (quiet)", Words(m.Panel.Rows()));
        m.Panel.Typed("upstairs", 8, 8);
        Assert.True(m.Panel.Key("Enter", out var pending));
        await pending;
        Assert.Equal("upstairs", (string?)m.Calls.Last().Args["name"]);
        Assert.Null(m.Panel.Edit);
        Assert.Contains("# upstairs · ", Words(m.Panel.Rows()));
        Assert.Contains("> Rename…", Words(m.Panel.Rows()));

        // Escape leaves the box and keeps the dropdown's content
        await m.Panel.Act("pt-rename:" + idA);
        Assert.Equal("upstairs", m.Panel.Edit!.Value);
        Assert.True(m.Panel.Key("Escape", out _));
        Assert.Null(m.Panel.Edit);

        // Delete asks once
        await m.Panel.Act("pt-del:" + idA);
        Assert.Contains("> Delete: click again to confirm (armed)", Words(m.Panel.Rows()));
        int before = m.Calls.Count;
        await m.Panel.Act("pt-del:" + idA);
        Assert.Equal("delete", (string?)m.Calls[before].Args["action"]);
        Assert.Null(m.Panel.Open);
        Assert.Single(h.View()["tests"]!.AsArray());

        // compare: A = older, B = newer
        var c = DoneRun(f, 1_760_001_800_000); h.Add(c);
        m.Panel.OnHistory(h.View());
        string idB = PingTestHistory.Id(b["startedAt"], b["run"]), idC = PingTestHistory.Id(c["startedAt"], c["run"]);
        await m.Panel.Act("pt-cmp:" + idC);
        Assert.Contains("Pick the test to compare with " + PingTestPanel.Stamp(1_760_001_800_000) + ".", Words(m.Panel.Rows()));
        await m.Panel.Act("pt-cmpto:" + idB);
        w = Words(m.Panel.Rows());
        Assert.Contains("# Compare", w);
        Assert.Contains("cl: Wi-Fi: Test Wi-Fi 5G → Test Wi-Fi.", w);
        Assert.Contains("cl better: Signal: 41 → 70 %, 29 points better.", w);
        Assert.Contains("cl: Download: 62.0 → 62.0 Mbps, about the same (Cloudflare both times).", w);
        Assert.Contains("Your router ; 28 → 2.4 ; 26 ms faster ; 26 → 0.6 ; 0 → 0", w);
        Assert.EndsWith("> Back to history (quiet)", w);
    }

    [Fact]
    public void The_fake_run_steps_to_done_and_a_start_while_running_is_refused()
    {
        var f = new PingTestFake();
        Assert.Equal("idle", (string?)f.State()["phase"]);
        Assert.Equal("starting", (string?)f.Start(1).Snap!["phase"]);
        Assert.Equal("a ping test is already running", f.Start(2).Refusal);
        var phases = new List<string>();
        while (f.Step(3) is { } s) phases.Add((string)s["phase"]!);
        Assert.Equal(new[] { "ping", "down", "up", "done" }, phases);
        f.Start(4); f.Step(5);
        var x = f.Cancel(6);
        Assert.Equal("cancelled", (string?)x["phase"]);
        Assert.Equal("cancelled", (string?)x["down"]!["state"]);
        Assert.False(f.Running);
    }
}
