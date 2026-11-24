using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.AutoPause;

namespace Mantle.Core.Tests.Features.AutoPause;

// The auto-pause test kit: one Spotify shared by every app (counting who paused and played it), a socket-free "network" that
// carries a GET to the AutoPauseHost bound on a port, and an app = engine + host API + link (lib/features/autopause-link.test.js
// makeApp, Quartz's shape: one local hold). Nothing binds a socket; ports are made-up numbers (never 8787-8790).

public sealed class FakeSpotify
{
    public string State = "playing";
    public bool Found = true;
    public readonly Dictionary<string, int> Pauses = new(), Plays = new();
    public int TotalPauses => Pauses.Values.Sum();
    public int TotalPlays => Plays.Values.Sum();

    /// <summary>The engine's view for app <paramref name="name"/>: Spotify only while <paramref name="sees"/> (its host role).</summary>
    public ISpotifyControl For(string name, Func<bool> sees) => new View(this, name, sees);

    sealed class View : ISpotifyControl
    {
        readonly FakeSpotify _s; readonly string _n; readonly Func<bool> _sees;
        public View(FakeSpotify s, string n, Func<bool> sees) { _s = s; _n = n; _sees = sees; }
        public string State() => _sees() ? (_s.Found ? _s.State : "none") : "none";
        public Task<SpotifyResult> Pause() { _s.State = "paused"; _s.Pauses[_n] = _s.Pauses.GetValueOrDefault(_n) + 1; return Task.FromResult(SpotifyResult.Done); }
        public Task<SpotifyResult> Play() { _s.State = "playing"; _s.Plays[_n] = _s.Plays.GetValueOrDefault(_n) + 1; return Task.FromResult(SpotifyResult.Done); }
    }
}

/// <summary>Loopback without sockets: Bind registers a host on a port (taken = null), Get carries one request to it. Every Get
/// yields once first, so two apps that start together really interleave (the simultaneous-start case).</summary>
public sealed class FakeNet
{
    readonly Dictionary<int, AutoPauseHost> _hosts = new();
    readonly HashSet<int> _foreign = new();
    readonly Dictionary<string, List<CancellationTokenSource>> _open = new();
    public readonly HashSet<string> Dead = new();
    public readonly List<string> Routes = new();

    /// A port taken by other software (it answers nothing useful).
    public void TakeForeign(int port) => _foreign.Add(port);

    public AutoPauseBind BindFor(string app) => (port, host) =>
    {
        if (Dead.Contains(app) || _foreign.Contains(port) || _hosts.ContainsKey(port)) return null;
        _hosts[port] = host;
        return new Off(() => _hosts.Remove(port));
    };

    /// <summary>A Get for app <paramref name="app"/>; a dead app's requests never resolve (lib's crash: nothing more is said).</summary>
    public LinkGet GetFor(string app) => async (port, route, timeoutMs, cancel) =>
    {
        await Task.Yield();
        if (Dead.Contains(app)) { await new TaskCompletionSource<bool>().Task; }
        Routes.Add($"{app} {port}{route}");
        if (_foreign.Contains(port)) return new LinkReply(200, new JsonObject { ["ok"] = true, ["app"] = "something-else" });
        if (!_hosts.TryGetValue(port, out var host)) return LinkReply.Failed("ECONNREFUSED");
        var gone = new CancellationTokenSource();
        lock (_open) { if (!_open.TryGetValue(app, out var l)) _open[app] = l = new(); l.Add(gone); }
        var closed = new TaskCompletionSource<AutoPauseAnswer?>();
        using var reg = cancel.Register(() => { gone.Cancel(); closed.TrySetResult(null); });
        gone.Token.Register(() => closed.TrySetResult(null));
        var req = new AutoPauseRequest("GET", route, n => n == "host" ? $"127.0.0.1:{port}" : null);
        var answer = await await Task.WhenAny(host.Handle(req, gone.Token), closed.Task);
        lock (_open) _open[app].Remove(gone);
        if (answer == null) return LinkReply.Failed("closed");
        return new LinkReply(answer.Code, answer.Json?.DeepClone() as JsonObject);
    };

    /// <summary>A crash: every socket of the app drops, and it says nothing more.</summary>
    public void Crash(string app)
    {
        Dead.Add(app);
        List<CancellationTokenSource> l;
        lock (_open) l = _open.TryGetValue(app, out var x) ? x.ToList() : new();
        foreach (var c in l) c.Cancel();
    }

    sealed class Off : IDisposable { readonly Action _a; public Off(Action a) { _a = a; } public void Dispose() => _a(); }
}

/// <summary>One app as lib/features/autopause-link.test.js makes it: engine + host API + link, its holds a single local one.</summary>
public sealed class LinkApp
{
    public readonly string Name;
    public readonly List<string> Logs = new();
    public readonly AutoPauseEngine Engine;
    public readonly AutoPauseHost Host;
    public readonly AutoPauseLink Link;

    public LinkApp(string name, IReadOnlyList<int> ports, FakeSpotify sp, FakeNet net, HostClock clock, bool canHost = true, Func<bool>? may = null)
    {
        Name = name;
        AutoPauseLink? link = null;
        Engine = new AutoPauseEngine(sp.For(name, () => link != null && link.Role == "host" && !net.Dead.Contains(name)), () => clock.NowMs, Logs.Add);
        Host = new AutoPauseHost(new AutoPauseHost.Deps(Engine, () => (true, sp.State, "Spotify.exe"), () => null, _ => Task.FromResult(SpotifyResult.Done),
            Logs.Add, () => clock.NowMs, clock, net.BindFor(name), ports, name));
        var host = Host; var engine = Engine;
        link = new AutoPauseLink(new AutoPauseLink.Options(name, ports, () => !net.Dead.Contains(name) && canHost && (may?.Invoke() ?? true),
            () => Task.FromResult(host.Start()), why => host.Stop(why),
            // The owner tells its watchers after its own hold moves (Mantle's publish() calls compat.notify()).
            async h => { if (h) await engine.AddHold("local:" + name, true, clock.NowMs, kind: "browser"); else await engine.RemoveHold("local:" + name, "done"); host.Notify(); },
            async (from, last) =>
            {
                if (last != null)
                {
                    bool paused = B(last["wePaused"]) || B(last["heldByUs"]);
                    long supp = B(last["repauseSuppressed"]) && last["repauseSuppressedForMs"] is JsonValue v && v.TryGetValue(out long ms) ? ms : 0;
                    await engine.Inherit(paused, supp);
                }
                host.Notify();
            },
            async why => { if (host.SuccessorWatching()) engine.HandOff(); else { await engine.ReleaseAll(why); host.Notify(); } },
            Logs.Add, net.GetFor(name), clock));
        Link = link;
    }

    static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;

    public async Task Hold(bool h) { await Link.SetHeld(h); await Engine.Idle(); }
}

public static class Ap
{
    /// Made-up ports for the socket-free network (never the real 8787-8790).
    public static readonly IReadOnlyList<int> Ports = new[] { 48787, 48788, 48789, 48790 };

    public static HostClock Clock(HostLoop loop, ManualTime time) => new(time, loop);

    public static string J(Dictionary<string, int> d) => string.Join(",", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

    /// A request as a browser or app would send it: header names lower-case.
    public static AutoPauseRequest Req(string target, int port, string method = "GET", string? origin = null, string? site = null, string? host = null, string body = "", bool tooBig = false)
    {
        var h = new Dictionary<string, string>(StringComparer.Ordinal) { ["host"] = host ?? $"127.0.0.1:{port}" };
        if (origin != null) h["origin"] = origin;
        if (site != null) h["sec-fetch-site"] = site;
        return new AutoPauseRequest(method, target, n => h.TryGetValue(n, out var v) ? v : null, body, tooBig);
    }
}
