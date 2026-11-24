using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.AutoPause;

/// <summary>One loopback GET's outcome: the HTTP code and its JSON, or code 0 with the error words. Never throws.</summary>
public sealed record LinkReply(int Code, JsonObject? Json, string? Error = null)
{
    public static LinkReply Failed(string error) => new(0, null, error);
}

/// <summary>One loopback GET of 127.0.0.1:<c>port</c><c>route</c>, no Origin (a local app). Cancelling = destroying the request
/// (it then resolves code 0 "closed"). Must never throw.</summary>
public delegate Task<LinkReply> LinkGet(int port, string route, int timeoutMs, CancellationToken cancel);

/// <summary>
/// The election's pure rules (docs/autopause-protocol.md "Roles" 1): who is a host, which host wins, what an app does next.
/// </summary>
public static class AutoPauseElection
{
    public enum Step { Client, None, Bind }
    public enum AfterBind { Host, HostAlone, Yield }

    /// A /ping answer from a host: 200, ok true, app "spotify-autopause" (the old bridge too).
    public static bool IsHost(LinkReply? r)
    {
        if (r is not { Code: 200, Json: { } j }) return false;
        if (!(j["ok"] is JsonValue ok && ok.TryGetValue(out bool b) && b)) return false;
        var app = j["app"] is JsonValue a && a.TryGetValue(out string? s) ? s ?? "" : "";
        return Regex.IsMatch(app, "spotify-autopause", RegexOptions.IgnoreCase);
    }

    /// <summary>The probe's verdict: the first port, in the list's order (8787 up: the LOWEST wins), whose answer is a host.</summary>
    public static (int Port, JsonObject Status)? Pick(IReadOnlyList<int> ports, IReadOnlyList<LinkReply> replies)
    {
        for (int i = 0; i < ports.Count && i < replies.Count; i++) if (IsHost(replies[i])) return (ports[i], replies[i].Json!);
        return null;
    }

    /// No host found: an app that may host binds; one that may not is "none" (it decides nothing and touches nothing).
    public static Step Next(bool hostFound, bool canHost) => hostFound ? Step.Client : canHost ? Step.Bind : Step.None;

    /// <summary>After binding: no port (all four taken by other software) = host alone, nothing can reach it; a host found BELOW
    /// the port we bound = yield to it (two apps bound at once; binding is the lock, the lower port is the host).</summary>
    public static AfterBind After(int? boundPort, bool hostBelow) => boundPort == null ? AfterBind.HostAlone : hostBelow ? AfterBind.Yield : AfterBind.Host;

    /// A host on protocol 2 or later is followed with /watch; an older one (the standalone bridge) with /ping every 3 s.
    public static bool Watches(JsonObject? status) => status != null && status["protocol"] is JsonValue v && v.TryGetValue(out double p) && p >= 2;
}

/// <summary>
/// Which app is the Spotify auto-pause HOST (lib/features/_autopause-link.js, ported; docs/autopause-protocol.md). Either app
/// works alone, and both together never fight:
///   ELECTION: probe 127.0.0.1:8787-8790; a host answers = be its CLIENT (lowest port wins); none = bind the first free port
///   and be the HOST; a host that bound a higher port looks below it once and yields.
///   CLIENT: our hold goes to the host as GET /pause?src=&lt;name&gt;[&amp;can=host], re-sent every 60 s, /resume when it ends; one
///   /watch long poll stays open: the instant its socket closes we elect again (150 ms later).
///   The old standalone bridge (no /watch) is still a host: a client that could take over polls its /ping every 3 s.
/// Host thread only (timers come back through the HostClock).
/// </summary>
public sealed class AutoPauseLink
{
    public static readonly IReadOnlyList<int> Ports = new[] { 8787, 8788, 8789, 8790 };
    public const int ProbeMs = 600, CallMs = 4000, WatchWaitMs = 250000, RefreshMs = 60000, LegacyPollMs = 3000, NonePollMs = 10000, ReelectMs = 150, HotLoopMs = 40;

    /// <summary>The owner's side. <c>HostStart</c> binds (the port, or null); <c>LocalHold</c> is our own hold in our own engine
    /// (host role); <c>OnHost(from, last)</c>: we just became host, <c>last</c> = the old host's status (only after being its
    /// client); <c>Retire(why)</c>: the host role ends (hand off or hand back).</summary>
    public sealed record Options(string Name, IReadOnlyList<int> Ports, Func<bool> CanHost, Func<Task<int?>> HostStart, Action<string> HostStop,
        Func<bool, Task> LocalHold, Func<string, JsonObject?, Task> OnHost, Func<string, Task> Retire, Action<string> Log, LinkGet Get, HostClock Clock,
        Action? OnChange = null);

    readonly Options _o;
    string _role = "off";          // off | host | client | none
    int? _hostPort;                // the host's port (client), or ours (host)
    JsonObject? _last;             // the host's newest status (client role)
    bool _held, _stopped = true, _again;
    Task<string>? _electing;
    int _gen;                      // bumps on every role change: a stale loop or timer sees it and stops
    CancellationTokenSource? _watchReq;
    IDisposable? _refreshTimer, _pollTimer;

    public AutoPauseLink(Options o) { _o = o ?? throw new ArgumentNullException(nameof(o)); }

    public string Role => _role;
    public int? Port => _hostPort;
    public bool Held => _held;
    public JsonObject? HostStatus => _last;

    void Log(string t) { try { _o.Log(t); } catch { /* the owner's log */ } }
    void Changed() { try { _o.OnChange?.Invoke(); } catch (Exception e) { Log($"auto-pause link: the owner's change handler threw: {e.GetType().Name}"); } }
    bool CanHost() { try { return _o.CanHost(); } catch (Exception e) { Log($"auto-pause link: may-we-host threw ({e.GetType().Name}); taken as no"); return false; } }

    IDisposable Later(int ms, Action fn) => _o.Clock.Later(TimeSpan.FromMilliseconds(ms), fn);

    /// Fire and forget, but never silently: a fault is logged.
    void Fire(Task t, string what) => t.ContinueWith(x => Log($"auto-pause link: {what} threw: {x.Exception?.GetBaseException().Message}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    Task Delay(int ms) { var t = new TaskCompletionSource<bool>(); Later(ms, () => t.TrySetResult(true)); return t.Task; }

    void ClearTimers()
    {
        _refreshTimer?.Dispose(); _refreshTimer = null;
        _pollTimer?.Dispose(); _pollTimer = null;
        if (_watchReq != null) { var r = _watchReq; _watchReq = null; try { r.Cancel(); } catch (ObjectDisposedException) { /* gone */ } }
    }

    async Task<(int Port, JsonObject Status)?> ProbeAll(int? below)
    {
        var list = _o.Ports.Where(p => (below == null || p < below) && p != (_role == "host" ? _hostPort : null)).ToList();
        var hits = await Task.WhenAll(list.Select(p => _o.Get(p, "/ping", ProbeMs, CancellationToken.None))).ConfigureAwait(true);
        return AutoPauseElection.Pick(list, hits);
    }

    /// <summary>Elect now (or join the election running: it runs once more after).</summary>
    public Task<string> Elect(string why)
    {
        if (_stopped) return Task.FromResult(_role);
        if (_electing != null) { _again = true; return _electing; }
        var done = new TaskCompletionSource<string>();
        _electing = done.Task;
        Fire(ElectLoop(why, done), "electing");
        return done.Task;
    }

    async Task ElectLoop(string why, TaskCompletionSource<string> done)
    {
        try
        {
            do
            {
                _again = false;
                try { await ElectOnce(why).ConfigureAwait(true); }
                catch (Exception e) { Log($"auto-pause link: electing threw: {e.Message}"); }
            } while (_again && !_stopped);
        }
        finally { _electing = null; done.TrySetResult(_role); }
    }

    async Task ElectOnce(string why)
    {
        if (_stopped) return;
        if (_role == "host")
        {
            if (!CanHost()) { await RetireRole("this app may not decide any more").ConfigureAwait(true); await ElectOnce(why).ConfigureAwait(true); }
            return;
        }
        var found = await ProbeAll(null).ConfigureAwait(true);
        if (_stopped) return;
        switch (AutoPauseElection.Next(found != null, CanHost()))
        {
            case AutoPauseElection.Step.Client: await BecomeClient(found!.Value).ConfigureAwait(true); return;
            case AutoPauseElection.Step.None: SetRole("none"); ArmNonePoll(); return;
        }
        var p = await _o.HostStart().ConfigureAwait(true);
        if (_stopped) { if (p != null) _o.HostStop("stopped while starting"); return; }
        var lower = p == null ? null : await ProbeAll(p).ConfigureAwait(true);
        switch (AutoPauseElection.After(p, lower != null))
        {
            case AutoPauseElection.AfterBind.HostAlone: await BecomeHost(null, "no free port: deciding for this app alone").ConfigureAwait(true); return;
            case AutoPauseElection.AfterBind.Yield:   // two apps bound at once: the lower port is the host, we were never it
                _o.HostStop("another app is the auto-pause host");
                await BecomeClient(lower!.Value).ConfigureAwait(true); return;
            default: await BecomeHost(p, why).ConfigureAwait(true); return;
        }
    }

    void SetRole(string r, bool force = false)
    {
        if (_role != r || force)
        {
            if (_role != r) Log($"auto-pause link: {_role} -> {r}");
            _role = r; _gen += 1; ClearTimers(); Changed();
        }
    }

    async Task BecomeHost(int? p, string why)
    {
        var from = _role; var prev = _last;
        SetRole("host");
        _hostPort = p; _last = null;
        Log($"auto-pause link: this app is the host on {(p is int n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no port")}{(string.IsNullOrEmpty(why) ? "" : $" ({why})")}");
        if (_held) await _o.LocalHold(true).ConfigureAwait(true);
        await _o.OnHost(from, from == "client" ? prev : null).ConfigureAwait(true);
        Changed();
    }

    async Task BecomeClient((int Port, JsonObject Status) found)
    {
        SetRole("client", true);
        _hostPort = found.Port; _last = found.Status;
        var server = found.Status["server"] is JsonValue sv && sv.TryGetValue(out string? s) && !string.IsNullOrEmpty(s) ? s : "the standalone helper";
        Log($"auto-pause link: a client of the host on {found.Port} ({server})");
        var g = _gen;
        if (AutoPauseElection.Watches(found.Status)) Fire(WatchLoop(g), "the host watch");
        else ArmLegacyPoll(g);
        Changed();
        if (_held) await SendHold(g).ConfigureAwait(true);
    }

    async Task SendHold(int g)
    {
        _refreshTimer?.Dispose(); _refreshTimer = null;
        var q = "/pause?src=" + Uri.EscapeDataString(_o.Name);
        if (CanHost()) q += "&can=host";
        var r = await _o.Get(_hostPort ?? 0, q, CallMs, CancellationToken.None).ConfigureAwait(true);
        if (g != _gen) return;
        if (AutoPauseElection.IsHost(r)) { _last = r.Json; Changed(); }
        else
        {
            Log("auto-pause link: the host did not take our hold; looking again");
            Later(ReelectMs, () => Fire(Elect("the host did not answer"), "electing"));
            return;
        }
        if (_held) _refreshTimer = Later(RefreshMs, () => { _refreshTimer = null; if (g == _gen && _held) Fire(SendHold(g), "refreshing our hold"); });
    }

    async Task SendRelease(int g)
    {
        _refreshTimer?.Dispose(); _refreshTimer = null;
        var r = await _o.Get(_hostPort ?? 0, "/resume?src=" + Uri.EscapeDataString(_o.Name), CallMs, CancellationToken.None).ConfigureAwait(true);
        if (g == _gen && AutoPauseElection.IsHost(r)) { _last = r.Json; Changed(); }
    }

    async Task WatchLoop(int g)
    {
        var since = _last?["sig"] is JsonValue sv && sv.TryGetValue(out string? s0) ? s0 ?? "" : "";
        while (g == _gen && !_stopped)
        {
            var t0 = _o.Clock.NowMs;
            var cts = new CancellationTokenSource();
            if (g == _gen) _watchReq = cts;
            var r = await _o.Get(_hostPort ?? 0, $"/watch?src={Uri.EscapeDataString(_o.Name)}&since={Uri.EscapeDataString(since)}", WatchWaitMs, cts.Token).ConfigureAwait(true);
            if (g != _gen) return;
            _watchReq = null;
            if (AutoPauseElection.IsHost(r))
            {
                _last = r.Json; since = r.Json!["sig"] is JsonValue v && v.TryGetValue(out string? s1) ? s1 ?? "" : ""; Changed();
                if (_o.Clock.NowMs - t0 < HotLoopMs) await Delay(HotLoopMs).ConfigureAwait(true);   // never a hot loop
                continue;
            }
            if (r is { Code: 404 }) { ArmLegacyPoll(g); return; }   // a host without /watch
            break;
        }
        if (g == _gen && !_stopped)
        {
            Log("auto-pause link: the host went away");
            Later(ReelectMs, () => { if (g == _gen) Fire(Elect("the host went away"), "electing"); });
        }
    }

    void ArmLegacyPoll(int g)
    {
        if (!CanHost() || _pollTimer != null) return;   // a client that cannot take over has nothing to check for
        _pollTimer = Later(LegacyPollMs, () => Fire(LegacyPing(g), "polling the old bridge"));
    }

    async Task LegacyPing(int g)
    {
        _pollTimer = null;
        if (g != _gen || _stopped) return;
        var r = await _o.Get(_hostPort ?? 0, "/ping", ProbeMs * 2, CancellationToken.None).ConfigureAwait(true);
        if (g != _gen || _stopped) return;
        if (AutoPauseElection.IsHost(r)) { _last = r.Json; ArmLegacyPoll(g); }
        else await Elect("the host went away").ConfigureAwait(true);
    }

    void ArmNonePoll()
    {
        if (!_held || _pollTimer != null) return;   // nobody hosts and we may not: look again only while we hold
        var g = _gen;
        _pollTimer = Later(NonePollMs, () => { _pollTimer = null; if (g == _gen && _held) Fire(Elect("still holding"), "electing"); });
    }

    async Task RetireRole(string why)
    {
        var p = _hostPort;
        _gen += 1;
        try { await _o.Retire(why).ConfigureAwait(true); } catch (Exception e) { Log($"auto-pause link: retiring threw: {e.Message}"); }
        _o.HostStop(why);
        Log($"auto-pause link: no longer the host on {(p is int n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no port")} ({why})");
        _role = "off"; _hostPort = null; ClearTimers(); Changed();
    }

    /// <summary>Start taking part (or elect again if already started).</summary>
    public Task<string> Start()
    {
        if (!_stopped) return Elect("already started");
        _stopped = false;
        return Elect("starting");
    }

    /// <summary>Stop taking part. Host: hand off or hand back (the owner's Retire). Client: our hold goes back first.</summary>
    public async Task Stop(string? why)
    {
        if (_stopped) return;
        if (_role == "host") await RetireRole(string.IsNullOrEmpty(why) ? "stopped" : why!).ConfigureAwait(true);
        else if (_role == "client" && _held) await SendRelease(_gen).ConfigureAwait(true);
        _stopped = true; _held = false;
        _role = "off"; _hostPort = null; _gen += 1; ClearTimers(); Changed();
    }

    /// <summary>Our own "something plays" (Mantle: its watched apps).</summary>
    public async Task SetHeld(bool h)
    {
        if (h == _held) return;
        _held = h;
        if (_stopped) return;
        if (_role == "host") await _o.LocalHold(h).ConfigureAwait(true);
        else if (_role == "client") { if (h) await SendHold(_gen).ConfigureAwait(true); else await SendRelease(_gen).ConfigureAwait(true); }
        else if (h) await Elect("a hold with nowhere to go").ConfigureAwait(true);
    }

    /// <summary>The owner's "may we host" changed (a switch moved): step down, or look for the job.</summary>
    public Task<string> Sync()
    {
        if (_stopped) return Task.FromResult(_role);
        if (_role == "client" && !CanHost() && _pollTimer != null) { _pollTimer.Dispose(); _pollTimer = null; }
        return _role == "client" ? Task.FromResult(_role) : Elect("a switch moved");
    }
}
