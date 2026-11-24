using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.AutoPause;
using Xunit;

namespace Mantle.Core.Tests.Features.AutoPause;

// lib/features/autopause-compat.test.js, ported socket-free (the host's Handle is called directly): the gate, the routes and the
// status shape. AUTO-110..AUTO-114, AUTO-117, AUTO-118, AUTO-120..AUTO-128, AUTO-130 (log names nothing). Written, not run.
public class AutoPauseHostTests
{
    static readonly IReadOnlyList<int> P = Ap.Ports;

    sealed class Rig
    {
        public readonly FakeSpotify Sp = new();
        public readonly FakeNet Net = new();
        public readonly List<string> Logs = new();
        public readonly List<string> Controls = new();
        public readonly AutoPauseEngine Engine;
        public readonly AutoPauseHost Host;
        public readonly HostClock Clock;
        public JsonObject? Media;

        public Rig(HostLoop loop, ManualTime time)
        {
            Clock = Ap.Clock(loop, time);
            var c = Clock;
            Engine = new AutoPauseEngine(Sp.For("mantle", () => true), () => c.NowMs, Logs.Add);
            Host = new AutoPauseHost(new AutoPauseHost.Deps(Engine, () => (Sp.Found, Sp.State, "Spotify.exe"), () => Media,
                cmd => { Controls.Add(cmd); return Task.FromResult(SpotifyResult.Done); }, Logs.Add, () => c.NowMs, c, Net.BindFor("mantle"), P, "mantle"));
        }

        public async Task<AutoPauseAnswer> Call(string target, string method = "GET", string? origin = null, string? site = null, string? host = null, string body = "", bool tooBig = false)
            => (await Host.Handle(Ap.Req(target, Host.Port ?? P[0], method, origin, site, host, body, tooBig)))!;
    }

    static string? H(AutoPauseAnswer a, string name) => a.Headers.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    [Fact]
    public void Gate_rules_word_for_word()
    {
        Assert.True(AutoPauseGate.OriginOk(null) && AutoPauseGate.OriginOk("chrome-extension://abc") && AutoPauseGate.OriginOk("moz-extension://x") && AutoPauseGate.OriginOk("extension://x"));
        Assert.False(AutoPauseGate.OriginOk("https://evil.example") || AutoPauseGate.OriginOk("null"));
        Assert.True(AutoPauseGate.HostOk("127.0.0.1:8787", 8787) && AutoPauseGate.HostOk("localhost:8787", 8787) && AutoPauseGate.HostOk("LOCALHOST", 8787) && AutoPauseGate.HostOk("127.0.0.1", 8787));
        Assert.False(AutoPauseGate.HostOk("evil.example:8787", 8787) || AutoPauseGate.HostOk("127.0.0.1:8788", 8787) || AutoPauseGate.HostOk(null, 8787));
        Assert.True(AutoPauseGate.FetchOk(null, null) && AutoPauseGate.FetchOk(null, "none") && AutoPauseGate.FetchOk(null, " None "));
        Assert.False(AutoPauseGate.FetchOk(null, "cross-site") || AutoPauseGate.FetchOk(null, "same-site") || AutoPauseGate.FetchOk(null, "same-origin") || AutoPauseGate.FetchOk(null, ""));
        Assert.True(AutoPauseGate.FetchOk("chrome-extension://abc", "cross-site"));
        Assert.False(AutoPauseGate.FetchOk("https://evil.example", null));
        Assert.Equal("only this PC’s apps and browser extensions may use this", AutoPauseGate.Refused);
    }

    [Fact]
    public void Src_is_kept_when_valid_and_browser_otherwise_and_ttl_is_clamped()
    {
        Assert.Equal("quartz", AutoPauseGate.SrcKey("quartz"));
        Assert.Equal("a:b.c_d-e", AutoPauseGate.SrcKey(" a:b.c_d-e "));
        Assert.Equal("browser", AutoPauseGate.SrcKey("<x>"));
        Assert.Equal("browser", AutoPauseGate.SrcKey(""));
        Assert.Equal("browser", AutoPauseGate.SrcKey(null));
        Assert.Equal(new string('a', 80), AutoPauseGate.SrcKey(new string('a', 81)));   // cut to 80 first, as slice(0, 80) does
        Assert.Equal(0, AutoPauseGate.Ttl(null));
        Assert.Equal(0, AutoPauseGate.Ttl("junk"));
        Assert.Equal(0, AutoPauseGate.Ttl("-5"));
        Assert.Equal(5000, AutoPauseGate.Ttl("1"));
        Assert.Equal(600000, AutoPauseGate.Ttl("99999999"));
        Assert.Equal(30000, AutoPauseGate.Ttl("29999.5"));
        var q = new AutoPauseQuery("?src=a+b&src=second&since=x%7Cy&flag");
        Assert.Equal(("a b", "x|y", true, ""), (q.Get("src"), q.Get("since"), q.Has("flag"), q.Get("flag")));
    }

    [Fact]
    public void A_duplicated_Host_Origin_or_Sec_Fetch_Site_is_refused() // NATIVE: stricter than Node (no browser sends one)
    {
        var v = AutoPauseGate.Check(n => n == "host" ? "127.0.0.1:1" : null, 1, n => n == "origin");
        Assert.False(v.Allow);
        Assert.Equal("duplicate", v.Kind);
    }

    [Fact]
    public void Ping_has_the_bridge_shape_plus_server_mantle_and_protocol_2() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time);
        Assert.Equal(P[0], r.Host.Start());
        Assert.Contains($"serving the bridge-compatible API on 127.0.0.1:{P[0]}", r.Logs);
        var a = await r.Call("/ping");
        var j = a.Json!;
        Assert.Equal(200, a.Code);
        Assert.Equal(new[] { "ok", "app", "server", "version", "protocol", "port", "spotifyFound", "spotifyState", "heldByUs", "wePaused", "control", "holds",
            "repauseCount", "repauseSuppressed", "repauseSuppressedForMs", "browserHoldTtlMs", "adDuck", "sig" }, j.Select(kv => kv.Key).ToArray());
        Assert.Equal(("spotify-autopause", "mantle", 2, P[0], "smtc", 180000L), ((string)j["app"]!, (string)j["server"]!, (int)j["protocol"]!, (int)j["port"]!, (string)j["control"]!, (long)j["browserHoldTtlMs"]!));
        Assert.Equal("playing|0||0", (string)j["sig"]!);
        Assert.False(AutoPauseCoexist.IsOldBridge(j, out _));        // never mistaken for the standalone helper
        Assert.Equal((await r.Call("/status")).Json!.ToJsonString(), (await r.Call("/ping")).Json!.ToJsonString());
        var h = await r.Call("/ping");
        Assert.Equal(("no-store", "close", "Origin", "application/json"), (H(h, "Cache-Control"), H(h, "Connection"), H(h, "Vary"), H(h, "Content-Type")));
    });

    [Fact]
    public void Holds_pause_on_the_first_resume_on_the_last_and_play_is_refused_while_held() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        var a = await r.Call("/pause?src=quartz&can=host");
        Assert.Equal(("paused", "paused"), ((string)a.Json!["action"]!, r.Sp.State));
        Assert.Equal("[\"quartz\"]", a.Json!["holds"]!.ToJsonString());
        var b = await r.Call("/pause?src=operagx", origin: "chrome-extension://operagx");
        Assert.Contains((string)b.Json!["action"]!, new[] { "refreshed", "already-paused" });
        Assert.Equal("chrome-extension://operagx", H(b, "Access-Control-Allow-Origin"));
        var held = await r.Call("/control?cmd=play");
        Assert.Equal((false, "held", 2), ((bool)held.Json!["ok"]!, (string)held.Json!["reason"]!, (int)held.Json!["holds"]!));
        Assert.Empty(r.Controls);
        var r1 = await r.Call("/resume?src=operagx");
        Assert.Equal(("skipped", "other-holds", "paused"), ((string)r1.Json!["action"]!, (string)r1.Json!["reason"]!, r.Sp.State));
        var r2 = await r.Call("/resume?src=quartz");
        Assert.Equal(("resumed", "playing"), ((string)r2.Json!["action"]!, r.Sp.State));
        Assert.Contains("a hold came in over HTTP (paused)", r.Logs);
        Assert.True(r.Logs.All(l => !l.Contains("quartz") && !l.Contains("operagx") && !l.Contains("Song") && !l.Contains("Artist")));   // AUTO-130
    });

    [Fact]
    public void An_unrefreshed_HTTP_hold_expires_after_180_s_or_its_own_ttl() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        await r.Call("/pause?src=quartz");
        time.AdvanceMs(179999); await r.Engine.Tick(r.Clock.NowMs, null);
        Assert.Equal("paused", r.Sp.State);
        time.AdvanceMs(1); await r.Engine.Tick(r.Clock.NowMs, null);
        Assert.Equal(("playing", 0), (r.Sp.State, r.Engine.Snapshot(r.Clock.NowMs).Holds.Count));
        await r.Call("/pause?src=quartz&ttl=1000");                    // clamped up to 5 s
        time.AdvanceMs(4999); await r.Engine.Tick(r.Clock.NowMs, null);
        Assert.Equal("paused", r.Sp.State);
        time.AdvanceMs(1); await r.Engine.Tick(r.Clock.NowMs, null);
        Assert.Equal("playing", r.Sp.State);
    });

    [Fact]
    public void Now_sends_art_only_when_it_changed_and_never_with_art_0() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        Assert.Equal("none", (string)(await r.Call("/now")).Json!["status"]!);   // nothing plays: the empty shape
        r.Media = JsonNode.Parse("{\"sessions\":[{\"aumid\":\"Quartz\",\"title\":\"Song\",\"artist\":\"Artist\",\"status\":\"playing\",\"playing\":true," +
            "\"timeline\":{\"positionMs\":61000,\"durationMs\":200000},\"can\":{\"next\":true,\"previous\":false,\"toggle\":true},\"art\":\"data:image/jpeg;base64,QUJD\"}]}")!.AsObject();
        var n1 = (await r.Call("/now")).Json!;
        Assert.Equal(("Song", 61000L, true, "QUJD", "image/jpeg"), ((string)n1["title"]!, (long)n1["positionMs"]!, (bool)n1["canNext"]!, (string?)n1["art"], (string)n1["artMime"]!));
        Assert.NotEqual("", (string)n1["artHash"]!);
        var n2 = (await r.Call("/now?artHash=" + (string)n1["artHash"]!)).Json!;
        var n3 = (await r.Call("/now?art=0")).Json!;
        Assert.Null(n2["art"]); Assert.Null(n3["art"]);
        Assert.Equal((string)n1["artHash"]!, (string)n2["artHash"]!);
    });

    [Fact]
    public void Control_maps_prev_to_previous_refuses_junk_and_config_refuses_in_words() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        var n = await r.Call("/control", "POST", body: "{\"cmd\":\"prev\"}");
        Assert.Equal((true, "previous"), ((bool)n.Json!["ok"]!, r.Controls.Last()));
        var bad = await r.Call("/control?cmd=rm-rf-everything-now");
        Assert.Equal((false, "bad cmd", "rm-rf-everything"), ((bool)bad.Json!["ok"]!, (string)bad.Json!["reason"]!, (string)bad.Json!["cmd"]!));
        var cfg = await r.Call("/config", "POST");
        Assert.Equal("Mantle does not turn Spotify’s ads down, so there is nothing to change here", (string)cfg.Json!["reason"]!);
        var get = await r.Call("/config");
        Assert.Equal("{\"ok\":true,\"config\":{\"adDuck\":{\"enabled\":false}},\"server\":\"mantle\"}", get.Json!.ToJsonString());
        r.Engine.Tick(r.Clock.NowMs, new[] { new AutoPauseSource("Quartz", "browser", "Quartz", "playing", null, 0.5) }).GetAwaiter().GetResult();
        Assert.Equal("[{\"kind\":\"browser\",\"realPlaying\":true,\"held\":true,\"status\":\"playing\"}]", (await r.Call("/sessions")).Json!["sessions"]!.ToJsonString());
    });

    [Fact]
    public void The_gate_answers_403_before_any_route_and_the_refusal_is_logged_in_words() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        var web = await r.Call("/pause?src=x", origin: "https://evil.example");
        Assert.Equal((403, "only this PC’s apps and browser extensions may use this"), (web.Code, (string)web.Json!["reason"]!));
        Assert.Null(H(web, "Access-Control-Allow-Origin"));
        Assert.Equal(403, (await r.Call("/pause?src=x", host: "evil.example:" + P[0])).Code);
        Assert.Equal(403, (await r.Call("/pause?src=x", site: "cross-site")).Code);
        Assert.Equal(403, (await r.Call("/control?cmd=pause", site: "same-site")).Code);
        Assert.Equal((0, 0), (r.Engine.Snapshot(r.Clock.NowMs).Holds.Count, r.Controls.Count));
        Assert.Equal(200, (await r.Call("/ping", site: "none")).Code);     // a typed URL
        Assert.Equal(200, (await r.Call("/ping")).Code);                   // Node / PowerShell callers
        var ext = await r.Call("/ping", origin: "chrome-extension://flfijohlcdhmggpdgdfohjikaccciegb");
        Assert.Equal((200, "chrome-extension://flfijohlcdhmggpdgdfohjikaccciegb", "Origin"), (ext.Code, H(ext, "Access-Control-Allow-Origin"), H(ext, "Vary")));
        Assert.Contains(r.Logs, l => l.StartsWith("a request from a web page was refused"));
        Assert.Contains(r.Logs, l => l.StartsWith("a request for another host name was refused"));
        Assert.Contains(r.Logs, l => l.StartsWith("a request a browser sent for a web page was refused"));
        Assert.DoesNotContain(r.Logs, l => l.Contains("evil.example"));     // the log never names the site
    });

    [Fact]
    public void Body_cap_preflight_method_url_and_not_found_answers() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        Assert.Equal((413, "body over 4 KB"), ((await r.Call("/control", "POST", tooBig: true)).Code, (string)(await r.Call("/control", "POST", tooBig: true)).Json!["reason"]!));
        var opt = await r.Call("/pause", "OPTIONS", origin: "chrome-extension://x");
        Assert.Equal((204, "true", "GET, POST, OPTIONS"), (opt.Code, H(opt, "Access-Control-Allow-Private-Network"), H(opt, "Access-Control-Allow-Methods")));
        Assert.Equal((405, "GET or POST only"), ((await r.Call("/ping", "PUT")).Code, (string)(await r.Call("/ping", "PUT")).Json!["reason"]!));
        Assert.Equal(400, (await r.Call("ping")).Code);
        Assert.Equal((404, "not found"), ((await r.Call("/debug")).Code, (string)(await r.Call("/debug")).Json!["reason"]!));   // the bridge's debug routes are gone
    });

    [Fact]
    public void Watch_answers_at_once_when_since_differs_else_on_change_or_after_240_s() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        var now = (await r.Host.Handle(Ap.Req("/watch?src=quartz&since=stale", P[0])))!;
        Assert.Equal(200, now.Code);
        var sig = (string)now.Json!["sig"]!;
        var w = r.Host.Handle(Ap.Req("/watch?src=quartz&since=" + Uri.EscapeDataString(sig), P[0]));
        Assert.False(w.IsCompleted);
        Assert.Equal(1, r.Host.Watching);
        await r.Call("/pause?src=other");                                  // the picture changes
        Assert.True(w.IsCompleted);
        Assert.NotEqual(sig, (string)(await w)!.Json!["sig"]!);
        var sig2 = (string)(await r.Call("/ping")).Json!["sig"]!;
        var w2 = r.Host.Handle(Ap.Req("/watch?src=quartz&since=" + Uri.EscapeDataString(sig2), P[0]));
        time.AdvanceMs(AutoPauseHost.WatchMs - 1); Assert.False(w2.IsCompleted);
        time.AdvanceMs(1); Assert.True(w2.IsCompleted);
        Assert.Equal(200, (await w2)!.Code);
    });

    [Fact]
    public void A_watch_that_closes_drops_its_hold_unless_another_watch_of_the_same_src_is_open() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        await r.Call("/pause?src=quartz&can=host");
        var sig = (string)(await r.Call("/ping")).Json!["sig"]!;
        var a = new System.Threading.CancellationTokenSource(); var b = new System.Threading.CancellationTokenSource();
        _ = r.Host.Handle(Ap.Req("/watch?src=quartz&since=" + Uri.EscapeDataString(sig), P[0]), a.Token);
        _ = r.Host.Handle(Ap.Req("/watch?src=quartz&since=" + Uri.EscapeDataString(sig), P[0]), b.Token);
        Assert.True(r.Host.SuccessorWatching());                           // a can=host client watches: a stop hands off
        a.Cancel(); loop.Drain();
        Assert.Equal(("paused", 1), (r.Sp.State, r.Engine.Snapshot(r.Clock.NowMs).Holds.Count));   // the other watch still lives
        b.Cancel(); loop.Drain();
        Assert.Equal(("playing", 0), (r.Sp.State, r.Engine.Snapshot(r.Clock.NowMs).Holds.Count));  // dropped at once, not 180 s later
        Assert.Contains("a client app went away: its hold was dropped (resumed)", r.Logs);
        Assert.False(r.Host.SuccessorWatching());
    });

    [Fact]
    public void Successor_counts_for_2_s_after_its_watch_was_answered_and_only_with_can_host() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time); r.Host.Start();
        await r.Call("/pause?src=browser1");                               // no can=host: never a successor
        var sig = (string)(await r.Call("/ping")).Json!["sig"]!;
        _ = r.Host.Handle(Ap.Req("/watch?src=browser1&since=" + Uri.EscapeDataString(sig), P[0]));
        Assert.False(r.Host.SuccessorWatching());
        await r.Call("/pause?src=quartz&can=host");                        // answers the open watch
        var sig2 = (string)(await r.Call("/ping")).Json!["sig"]!;
        var w = r.Host.Handle(Ap.Req("/watch?src=quartz&since=" + Uri.EscapeDataString(sig2), P[0]));
        await r.Call("/pause?src=other");                                  // answered: quartz is between polls now
        Assert.True(w.IsCompleted);
        time.AdvanceMs(AutoPauseHost.WatchGapMs - 1); Assert.True(r.Host.SuccessorWatching());
        time.AdvanceMs(1); Assert.False(r.Host.SuccessorWatching());
    });

    [Fact]
    public void First_port_taken_falls_to_the_next_and_stop_closes_it_without_answering_watches() => HostLoop.Run(async (loop, time) =>
    {
        var r = new Rig(loop, time);
        r.Net.TakeForeign(P[0]);
        Assert.Equal(P[1], r.Host.Start());
        var sig = (string)(await r.Call("/ping")).Json!["sig"]!;
        var w = r.Host.Handle(Ap.Req("/watch?src=quartz&since=" + Uri.EscapeDataString(sig), P[1]));
        r.Host.Stop("off");
        Assert.True(w.IsCompleted);
        Assert.Null(await w);                                              // the socket closes: that is how a client learns we left
        Assert.False(r.Host.Running);
        Assert.Contains("bridge-compatible API closed (off)", r.Logs);
    });

    [Fact]
    public void Http_head_parse_and_answer_bytes()
    {
        var h = AutoPauseHttp.ParseHead("GET /ping?x=1 HTTP/1.1\r\nHost: 127.0.0.1:8787\r\nOrigin: a\r\norigin: b\r\nContent-Length: 12", out var err)!;
        Assert.Null(err);
        Assert.Equal(("GET", "/ping?x=1", "127.0.0.1:8787", true, 12L), (h.Method, h.Target, h.Get("host"), h.Twice("origin"), AutoPauseHttp.BodyLength(h)));
        Assert.Null(AutoPauseHttp.ParseHead("garbage", out var e2)); Assert.NotNull(e2);
        Assert.Equal(-1, AutoPauseHttp.BodyLength(AutoPauseHttp.ParseHead("POST / HTTP/1.1\r\nTransfer-Encoding: chunked", out _)!));
        var bytes = AutoPauseHttp.Write(new AutoPauseAnswer(200, new JsonObject { ["ok"] = true, ["reason"] = "PC’s" }, new List<KeyValuePair<string, string>> { new("Connection", "close") }));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Length: ", text);
        Assert.EndsWith("\r\n\r\n{\"ok\":true,\"reason\":\"PC’s\"}", text);
    }
}
