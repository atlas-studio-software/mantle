using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.AutoPause;
using Xunit;

namespace Mantle.Core.Tests.Features.AutoPause;

// lib/features/autopause-link.test.js, ported SOCKET-FREE (FakeNet carries each GET to the AutoPauseHost bound on a made-up port):
// AUTO-093..AUTO-097, AUTO-099..AUTO-105, AUTO-107, AUTO-108, AUTO-115, AUTO-116. Written, not run (no SDK on the laptop).
public class AutoPauseLinkTests
{
    static readonly IReadOnlyList<int> P = Ap.Ports;

    [Fact]
    public void Alone_one_app_hosts_on_the_first_port_and_pauses_and_resumes_for_its_own_hold() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var a = new LinkApp("quartz", P, sp, net, c);
        await a.Link.Start();
        Assert.Equal(("host", (int?)P[0]), (a.Link.Role, a.Link.Port));
        await a.Hold(true);
        Assert.Equal(("paused", "quartz:1"), (sp.State, Ap.J(sp.Pauses)));
        await a.Hold(false);
        Assert.Equal(("playing", "quartz:1"), (sp.State, Ap.J(sp.Plays)));
        await a.Link.Stop("done");
        Assert.False(a.Host.Running);
    });

    [Fact]
    public void Both_one_host_one_client_and_only_the_host_touches_Spotify() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        Assert.Equal(("host", "client", (int?)P[0]), (m.Link.Role, q.Link.Role, q.Link.Port));
        Assert.False(q.Host.Running);                                  // the client binds nothing
        await q.Hold(true); await m.Engine.Idle();
        Assert.Equal(("paused", "mantle:1"), (sp.State, Ap.J(sp.Pauses)));   // a client hold is paused by the host only
        Assert.Contains(net.Routes, r => r.StartsWith("quartz " + P[0] + "/pause?src=quartz&can=host"));
        await m.Hold(true);
        Assert.Equal("mantle:1", Ap.J(sp.Pauses));                     // a second hold does not pause twice
        await q.Hold(false); await m.Engine.Idle();
        Assert.Equal(("paused", 0), (sp.State, sp.TotalPlays));        // one hold ending keeps Spotify paused for the other
        await m.Hold(false);
        Assert.Equal(("playing", "mantle:1"), (sp.State, Ap.J(sp.Plays)));
        var st = q.Link.HostStatus!;
        Assert.Equal(("mantle", 2), ((string?)st["server"], (int)st["protocol"]!));
        await q.Link.Stop("done"); await m.Link.Stop("done");
    });

    [Fact]
    public void Takeover_graceful_the_client_hosts_and_Spotify_stays_paused_across_the_change() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        await q.Hold(true); await m.Engine.Idle();
        time.AdvanceMs(60);
        await m.Link.Stop("Mantle quits");
        time.AdvanceMs(AutoPauseLink.ReelectMs + 50);
        Assert.Equal("host", q.Link.Role);
        Assert.Equal(("paused", 0, 1), (sp.State, sp.TotalPlays, sp.TotalPauses));   // no resume-then-pause
        Assert.True(q.Engine.Snapshot(c.NowMs).WePaused);                             // the new host owns the pause
        await q.Hold(false);
        Assert.Equal(("playing", "quartz:1"), (sp.State, Ap.J(sp.Plays)));
        await q.Link.Stop("done");
    });

    [Fact]
    public void Takeover_right_after_a_status_change_never_resumes_across_it_five_times() => HostLoop.Run(async (loop, time) =>
    {
        int good = 0;
        for (int i = 0; i < 5; i++)
        {
            var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
            var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
            await m.Link.Start(); await q.Link.Start();
            await q.Hold(true); await m.Engine.Idle();
            await m.Link.Stop("Mantle quits");                   // inside the 40 ms gap between a /watch answer and the next poll
            time.AdvanceMs(AutoPauseLink.ReelectMs + 50);
            if (q.Link.Role == "host" && sp.State == "paused" && sp.TotalPlays == 0) good++;
            await q.Hold(false); await q.Link.Stop("done");
        }
        Assert.Equal(5, good);
    });

    [Fact]
    public void No_successor_the_host_hands_Spotify_back_on_the_way_out_and_the_other_app_still_takes_over() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        await m.Hold(true);
        await m.Link.Stop("Mantle quits");
        Assert.Equal(("playing", "mantle:1"), (sp.State, Ap.J(sp.Plays)));
        time.AdvanceMs(AutoPauseLink.ReelectMs + 50);
        Assert.True(q.Link.Role == "host", $"quartz is {q.Link.Role} || quartz log: {string.Join(" / ", q.Logs.TakeLast(15))} || mantle log: {string.Join(" / ", m.Logs.TakeLast(10))}");
        Assert.Equal((1, 1), (sp.TotalPauses, sp.TotalPlays));
        await q.Link.Stop("done");
    });

    [Fact]
    public void Host_crash_the_client_takes_over_inherits_and_resumes_the_stranded_pause_at_once() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        await m.Hold(true);
        time.AdvanceMs(100);
        Assert.True(q.Link.HostStatus!["wePaused"]!.GetValue<bool>());
        net.Crash("mantle"); m.Host.Stop("crash");                     // every socket drops, nothing is handed back
        time.AdvanceMs(AutoPauseLink.ReelectMs + 50);
        Assert.Equal(("host", "playing"), (q.Link.Role, sp.State));
        Assert.Equal(("quartz:1", "mantle:1"), (Ap.J(sp.Plays), Ap.J(sp.Pauses)));   // exactly one pause and one resume
    });

    [Fact]
    public void Client_crash_its_hold_is_dropped_the_moment_its_watch_socket_closes() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        await q.Hold(true); await m.Engine.Idle();
        time.AdvanceMs(100);
        Assert.True(m.Host.Watching > 0);
        net.Crash("quartz");
        loop.Drain();
        Assert.Equal(("playing", "mantle:1"), (sp.State, Ap.J(sp.Plays)));   // at once, not after 180 s
        Assert.Contains(m.Logs, l => l.StartsWith("a client app went away: its hold was dropped"));
    });

    [Fact]
    public void Simultaneous_start_gives_exactly_one_host_five_times() => HostLoop.Run(async (loop, time) =>
    {
        int good = 0;
        for (int i = 0; i < 5; i++)
        {
            var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
            var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
            await Task.WhenAll(m.Link.Start(), q.Link.Start());
            var roles = string.Join("/", new[] { m.Link.Role, q.Link.Role }.OrderBy(x => x));
            var bound = new[] { m.Host.Running, q.Host.Running }.Count(x => x);
            if (roles == "client/host" && bound == 1) good++;
            await Task.WhenAll(m.Link.Stop("done"), q.Link.Stop("done"));
        }
        Assert.Equal(5, good);
    });

    [Fact]
    public void A_host_that_bound_a_higher_port_yields_to_the_host_below_it() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await Task.WhenAll(m.Link.Start(), q.Link.Start());
        var client = m.Link.Role == "client" ? m : q;
        Assert.Equal((int?)P[0], client.Link.Port);
        var host = client == m ? q : m;
        Assert.True(client.Logs.Any(l => l.Contains("another app is the auto-pause host")),
            $"client log: {string.Join(" / ", client.Logs.TakeLast(12))} || host log: {string.Join(" / ", host.Logs.TakeLast(12))}");
        await Task.WhenAll(m.Link.Stop("done"), q.Link.Stop("done"));
    });

    [Fact]
    public void Cannot_host_with_nobody_hosting_is_none_binds_nothing_and_its_hold_goes_to_a_host_that_appears() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var q = new LinkApp("quartz", P, sp, net, c, canHost: false);
        await q.Link.Start();
        await q.Hold(true);
        Assert.Equal(("none", false), (q.Link.Role, q.Host.Running));
        Assert.Equal(("playing", 0), (sp.State, sp.TotalPauses));     // Spotify untouched
        var m = new LinkApp("mantle", P, sp, net, c);
        await m.Link.Start();
        time.AdvanceMs(AutoPauseLink.NonePollMs + 10);                  // it re-looks every 10 s while it holds
        await m.Engine.Idle();
        Assert.Equal(("client", "mantle:1"), (q.Link.Role, Ap.J(sp.Pauses)));
        Assert.DoesNotContain(net.Routes, r => r.Contains("can=host"));  // a client that cannot host never says it can
    });

    [Fact]
    public void All_four_ports_taken_by_other_software_still_decides_alone_and_says_so() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        foreach (var p in P) net.TakeForeign(p);
        var a = new LinkApp("mantle", P, sp, net, c);
        await a.Link.Start();
        Assert.Equal(("host", (int?)null), (a.Link.Role, a.Link.Port));
        Assert.Contains("the bridge-compatible API could not start: ports 8787-8790 are all taken", a.Logs);
        await a.Hold(true);
        Assert.Equal("paused", sp.State);
    });

    [Fact]
    public void The_old_bridge_is_a_host_and_a_client_that_may_host_takes_over_by_polling_its_ping() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var c = Ap.Clock(loop, time);
        bool alive = true;
        var routes = new List<string>();
        LinkGet legacy = async (port, route, ms, cancel) =>
        {
            await Task.Yield();
            routes.Add(route);
            if (!alive || port != P[0]) return LinkReply.Failed("ECONNREFUSED");
            if (route.StartsWith("/watch")) return new LinkReply(404, new JsonObject { ["ok"] = false, ["reason"] = "not found" });
            return new LinkReply(200, new JsonObject { ["ok"] = true, ["app"] = "spotify-autopause", ["version"] = "1.5.3", ["spotifyFound"] = true, ["spotifyState"] = "playing", ["heldByUs"] = false, ["holds"] = new JsonArray() });
        };
        var logs = new List<string>();
        int? bound = null;
        var link = new AutoPauseLink(new AutoPauseLink.Options("quartz", P, () => true, () => Task.FromResult(bound = P[0]), _ => bound = null,
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, logs.Add, legacy, c));
        await link.Start();
        Assert.Equal(("client", (int?)P[0]), (link.Role, link.Port));
        Assert.DoesNotContain(routes, r => r.StartsWith("/watch"));         // no /watch on protocol < 2
        Assert.Contains(logs, l => l.Contains("(the standalone helper)"));
        alive = false;
        time.AdvanceMs(AutoPauseLink.LegacyPollMs + 10);
        Assert.Equal("host", link.Role);
    });

    [Fact]
    public void A_client_refreshes_its_hold_every_60_s_and_releases_it_with_resume() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        await q.Hold(true);
        int Pauses() => net.Routes.Count(r => r.StartsWith("quartz " + P[0] + "/pause?src=quartz"));
        Assert.Equal(1, Pauses());
        time.AdvanceMs(AutoPauseLink.RefreshMs - 10); Assert.Equal(1, Pauses());
        time.AdvanceMs(20); Assert.Equal(2, Pauses());
        await q.Hold(false);
        Assert.Contains(net.Routes, r => r == "quartz " + P[0] + "/resume?src=quartz");
        time.AdvanceMs(AutoPauseLink.RefreshMs * 2); Assert.Equal(2, Pauses());   // no refresh once released
    });

    [Fact]
    public void A_client_stop_sends_its_release_first_then_leaves() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c); var q = new LinkApp("quartz", P, sp, net, c);
        await m.Link.Start(); await q.Link.Start();
        await q.Hold(true); await m.Engine.Idle();
        await q.Link.Stop("Quartz quits"); await m.Engine.Idle();
        Assert.Equal(("off", "playing"), (q.Link.Role, sp.State));
        Assert.Contains(net.Routes, r => r == "quartz " + P[0] + "/resume?src=quartz");
    });

    [Fact]
    public void A_host_that_does_not_take_the_hold_triggers_a_reelection_150_ms_later() => HostLoop.Run(async (loop, time) =>
    {
        var c = Ap.Clock(loop, time);
        int calls = 0; var logs = new List<string>();
        LinkGet get = async (port, route, ms, cancel) =>
        {
            await Task.Yield();
            if (port != P[0]) return LinkReply.Failed("ECONNREFUSED");
            if (route.StartsWith("/ping")) { calls++; return new LinkReply(200, new JsonObject { ["ok"] = true, ["app"] = "spotify-autopause", ["server"] = "quartz", ["protocol"] = 1 }); }
            return new LinkReply(500, new JsonObject { ["ok"] = false });
        };
        var link = new AutoPauseLink(new AutoPauseLink.Options("mantle", P, () => false, () => Task.FromResult<int?>(null), _ => { },
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, logs.Add, get, c));
        await link.Start();
        await link.SetHeld(true);
        Assert.Contains("auto-pause link: the host did not take our hold; looking again", logs);
        int before = calls;
        time.AdvanceMs(AutoPauseLink.ReelectMs - 10); Assert.Equal(before, calls);
        time.AdvanceMs(20); Assert.True(calls > before);
    });

    [Fact]
    public void Election_rules_lowest_port_wins_and_the_yield_down_rule()
    {
        var hostAt = (int port) => new LinkReply(200, new JsonObject { ["ok"] = true, ["app"] = "spotify-autopause", ["port"] = port });
        var none = LinkReply.Failed("ECONNREFUSED");
        var pick = AutoPauseElection.Pick(P, new[] { none, hostAt(P[1]), hostAt(P[2]), none });
        Assert.Equal(P[1], pick!.Value.Port);
        Assert.Null(AutoPauseElection.Pick(P, new[] { none, new LinkReply(200, new JsonObject { ["ok"] = true, ["app"] = "other" }), new LinkReply(200, new JsonObject { ["ok"] = false, ["app"] = "spotify-autopause" }), new LinkReply(404, null) }));
        Assert.Equal(AutoPauseElection.Step.Client, AutoPauseElection.Next(true, true));
        Assert.Equal(AutoPauseElection.Step.Client, AutoPauseElection.Next(true, false));
        Assert.Equal(AutoPauseElection.Step.Bind, AutoPauseElection.Next(false, true));
        Assert.Equal(AutoPauseElection.Step.None, AutoPauseElection.Next(false, false));
        Assert.Equal(AutoPauseElection.AfterBind.Yield, AutoPauseElection.After(P[1], true));
        Assert.Equal(AutoPauseElection.AfterBind.Host, AutoPauseElection.After(P[0], false));
        Assert.Equal(AutoPauseElection.AfterBind.HostAlone, AutoPauseElection.After(null, false));
        Assert.True(AutoPauseElection.Watches(new JsonObject { ["protocol"] = 2 }));
        Assert.False(AutoPauseElection.Watches(new JsonObject { ["app"] = "spotify-autopause" }));
        Assert.Equal(new[] { 8787, 8788, 8789, 8790 }, AutoPauseLink.Ports);
        Assert.Equal((600, 4000, 250000, 60000, 3000, 10000, 150), (AutoPauseLink.ProbeMs, AutoPauseLink.CallMs, AutoPauseLink.WatchWaitMs, AutoPauseLink.RefreshMs, AutoPauseLink.LegacyPollMs, AutoPauseLink.NonePollMs, AutoPauseLink.ReelectMs));
    }

    [Fact]
    public void The_gate_refuses_a_web_page_on_the_protocol_2_routes_and_nothing_is_held() => HostLoop.Run(async (loop, time) =>
    {
        var sp = new FakeSpotify(); var net = new FakeNet(); var c = Ap.Clock(loop, time);
        var m = new LinkApp("mantle", P, sp, net, c);
        await m.Link.Start();
        var page = await m.Host.Handle(Ap.Req("/watch?src=x", P[0], origin: "https://evil.example"));
        var nocors = await m.Host.Handle(Ap.Req("/pause?src=x", P[0], site: "cross-site"));
        var rebind = await m.Host.Handle(Ap.Req("/pause?src=x", P[0], host: "evil.example:" + P[0]));
        Assert.Equal((403, 403, 403), (page!.Code, nocors!.Code, rebind!.Code));
        Assert.Equal((0, 0), (sp.TotalPauses, m.Engine.Snapshot(c.NowMs).Holds.Count));
    });
}
