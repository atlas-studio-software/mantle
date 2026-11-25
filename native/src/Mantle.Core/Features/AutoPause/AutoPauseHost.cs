using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.AutoPause;

/// <summary>One request as the loopback server read it. <c>Header</c> looks a header up by lower-case name (null = absent).
/// <c>BodyTooBig</c>: its Content-Length was over 4 KB, so the body was never read.</summary>
public sealed record AutoPauseRequest(string Method, string Target, Func<string, string?> Header, string Body = "", bool BodyTooBig = false,
    Func<string, bool>? Duplicated = null);

/// <summary>An answer: the status code, the JSON (null for a 204), and the headers besides Content-Length.</summary>
public sealed record AutoPauseAnswer(int Code, JsonObject? Json, IReadOnlyList<KeyValuePair<string, string>> Headers);

/// <summary>Binds the host's API on 127.0.0.1:<c>port</c> and serves it through <see cref="AutoPauseHost.Handle"/>; null when the
/// port is taken. Disposing it closes the listener and every open connection. Tests pass a fake (no socket).</summary>
public delegate IDisposable? AutoPauseBind(int port, AutoPauseHost host);

/// <summary>
/// The auto-pause HOST's HTTP API (lib/features/_autopause-compat.js, ported; docs/autopause-protocol.md "Routes"). Whichever
/// app binds 127.0.0.1:8787-8790 first is the host, the one decision maker over Spotify; the other is a client that sends its
/// holds here. The routes are the old standalone bridge's (/ping /status /pause /resume /now /sessions /control /config), so
/// a browser extension written for it keeps working, plus protocol 2 (/pause ttl and can=host, /watch). SOCKET-FREE: the
/// server (<see cref="AutoPauseServer"/>) parses and writes; this decides. Host thread only.
/// </summary>
public sealed class AutoPauseHost
{
    public const int Protocol = 2;
    public const long WatchMs = 240000, WatchGapMs = 2000;
    public static readonly IReadOnlyDictionary<string, string> Commands = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["play"] = "play", ["pause"] = "pause", ["toggle"] = "toggle", ["next"] = "next", ["prev"] = "previous" };

    /// <summary>What the host is given. <c>Spotify</c>: found, state (playing/paused/stopped/none), aumid. <c>Media</c>: the NOW
    /// PLAYING read ({sessions, shown}) or null. <c>Control</c>: a transport command on Spotify's session.</summary>
    public sealed record Deps(AutoPauseEngine Engine, Func<(bool Found, string State, string? Aumid)> Spotify, Func<JsonObject?> Media,
        Func<string, Task<SpotifyResult>> Control, Action<string> Log, Func<long> Now, HostClock Clock, AutoPauseBind Bind,
        IReadOnlyList<int> Ports, string Name = "mantle", Action? Changed = null, Func<Task>? Refresh = null);

    sealed class Watcher
    {
        public string Src = "", Since = "";
        public IDisposable? Timer;
        public CancellationTokenRegistration Gone;
        public bool Done;
        public TaskCompletionSource<AutoPauseAnswer?> Answer = new();
    }

    readonly Deps _d;
    readonly List<Watcher> _watchers = new();
    readonly Dictionary<string, long> _watchedAt = new(StringComparer.Ordinal);
    readonly Dictionary<string, (long At, int Skipped)> _refusedLogged = new(StringComparer.Ordinal);
    IDisposable? _server;
    int? _port;
    bool _closing;

    public AutoPauseHost(Deps d) { _d = d ?? throw new ArgumentNullException(nameof(d)); }

    public int? Port => _port;
    public bool Running => _server != null;
    public int Watching => _watchers.Count;
    string Name => _d.Name;

    // ---- status

    /// <summary>GET /ping and /status: {ok, app, server, version, protocol, port, spotifyFound, spotifyState, heldByUs, wePaused,
    /// control, holds, repauseCount, repauseSuppressed, repauseSuppressedForMs, browserHoldTtlMs, adDuck, [extra], sig}.</summary>
    public JsonObject StatusJson(string? action = null, string? reason = null)
    {
        var sp = _d.Spotify();
        var snap = _d.Engine.Snapshot(_d.Now());
        var holds = snap.Holds.Select(h => h.Native ? "native" : StripHttp(h.Key)).ToList();
        var j = new JsonObject
        {
            ["ok"] = true, ["app"] = "spotify-autopause", ["server"] = Name, ["version"] = Name, ["protocol"] = Protocol, ["port"] = _port,
            ["spotifyFound"] = sp.Found, ["spotifyState"] = sp.Found ? sp.State : "none", ["heldByUs"] = snap.WePaused && snap.Holds.Count > 0,
            ["wePaused"] = snap.WePaused, ["control"] = "smtc", ["holds"] = new JsonArray(holds.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["repauseCount"] = snap.EpisodeRepauses, ["repauseSuppressed"] = snap.Suppressed, ["repauseSuppressedForMs"] = snap.SuppressedForMs,
            ["browserHoldTtlMs"] = _d.Engine.Config.HttpHoldTtlMs, ["adDuck"] = null,
        };
        if (action != null) { j["action"] = action; j["reason"] = reason ?? ""; }
        j["sig"] = Sig(sp.Found ? sp.State : "none", snap.WePaused, holds, snap.Suppressed);
        return j;
    }

    /// <summary>What a client needs to follow the host, as one string: a /watch answers when it changes.</summary>
    public static string Sig(string spotifyState, bool wePaused, IEnumerable<string> holds, bool suppressed) =>
        string.Join("|", spotifyState, wePaused ? "1" : "0", string.Join(",", holds), suppressed ? "1" : "0");

    static string StripHttp(string key) => key.StartsWith("http:", StringComparison.Ordinal) ? key.Substring(5) : key;

    // ---- /watch bookkeeping

    /// <summary>Answer every /watch whose picture is out of date (the owner calls this after anything moves).</summary>
    public void Notify()
    {
        if (_watchers.Count == 0 || _server == null) return;
        var j = StatusJson();
        var sig = (string)j["sig"]!;
        foreach (var w in _watchers.ToList()) if (w.Since != sig) AnswerWatch(w, j);
    }

    void AnswerWatch(Watcher w, JsonObject? j)
    {
        if (!_watchers.Remove(w)) return;
        if (w.Src.Length > 0) _watchedAt[w.Src] = _d.Now();
        w.Timer?.Dispose();
        w.Done = true;
        w.Gone.Dispose();
        w.Answer.TrySetResult(Json(200, (JsonObject)(j ?? StatusJson()).DeepClone(), null));
    }

    /// <summary>/watch?src=&amp;since=: held open until the picture changes (or 240 s). The socket IS the liveness signal both ways: a
    /// client whose socket closes (<paramref name="gone"/>) before we answered has gone, so its hold is dropped NOW, unless it has
    /// another watch open. Resolves null when the host closes without answering (the client sees the socket close).</summary>
    Task<AutoPauseAnswer?> Watch(AutoPauseQuery q, string? origin, CancellationToken gone)
    {
        var src = q.Has("src") ? AutoPauseGate.SrcKey(q.Get("src")) : "";
        var w = new Watcher { Src = src, Since = q.Get("since") ?? "" };
        var j = StatusJson();
        if (w.Since != (string)j["sig"]!) return Task.FromResult<AutoPauseAnswer?>(Json(200, j, origin));
        _watchers.Add(w);
        if (src.Length > 0) _watchedAt[src] = _d.Now();
        w.Timer = _d.Clock.Later(TimeSpan.FromMilliseconds(WatchMs), () => AnswerWatch(w, null));
        var o = origin;   // the CORS echo goes on the answer when it comes
        w.Gone = gone.Register(() => _d.Clock.Post(() => WatchClosed(w)));
        return w.Answer.Task.ContinueWith(t => t.Result is { } a && o != null ? a with { Headers = WithOrigin(a.Headers, o) } : t.Result,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    void WatchClosed(Watcher w)
    {
        if (w.Done || _closing) return;
        _watchers.Remove(w);
        w.Done = true;
        w.Timer?.Dispose();
        w.Answer.TrySetResult(null);
        if (w.Src.Length == 0 || _watchers.Any(x => x.Src == w.Src)) return;
        _watchedAt.Remove(w.Src);
        _ = DropClient(w.Src);
    }

    async Task DropClient(string src)
    {
        try
        {
            var r = await _d.Engine.RemoveHold("http:" + src, "its app closed").ConfigureAwait(true);
            if (r.Action != "skipped") _d.Log($"a client app went away: its hold was dropped ({r.Action})");
            _d.Changed?.Invoke();
            Notify();
        }
        catch (Exception e) { _d.Log($"dropping a gone client's hold threw: {e.GetType().Name}: {e.Message}"); }
    }

    /// <summary>Live holds from clients that can take over AND hold a /watch open (or did within 2 s, the moment between an
    /// answer and their next poll): they will notice our exit and take the pause over (the handoff rule).</summary>
    public bool SuccessorWatching()
    {
        var srcs = new HashSet<string>(_watchers.Select(w => w.Src).Where(s => s.Length > 0), StringComparer.Ordinal);
        var t = _d.Now();
        foreach (var (s, at) in _watchedAt) if (t - at < WatchGapMs) srcs.Add(s);
        return _d.Engine.Snapshot(_d.Now()).Holds.Any(h => h.Successor && h.Live && !h.Native && srcs.Contains(StripHttp(h.Key)));
    }

    // ---- the request

    /// <summary>One request, in Electron's order: the gate (403), OPTIONS (204), the method (405), the body cap (413), the URL (400),
    /// then /watch or a route (404 / 500). <paramref name="gone"/> fires when the caller's socket closes. Null = close, no answer.</summary>
    public Task<AutoPauseAnswer?> Handle(AutoPauseRequest req, CancellationToken gone = default)
    {
        var origin = req.Header("origin");
        if (string.IsNullOrEmpty(origin)) origin = null;
        var gate = AutoPauseGate.Check(req.Header, _port, req.Duplicated);
        if (!gate.Allow)
        {
            LogRefusal(gate.Kind, gate.Words);
            return Done(Json(403, new JsonObject { ["ok"] = false, ["reason"] = AutoPauseGate.Refused }, null));
        }
        if (req.Method == "OPTIONS")
        {
            var h = new List<KeyValuePair<string, string>> { new("Vary", "Origin") };
            if (origin != null) h.Add(new("Access-Control-Allow-Origin", origin));
            h.Add(new("Access-Control-Allow-Methods", "GET, POST, OPTIONS"));
            h.Add(new("Access-Control-Allow-Headers", "Content-Type"));
            h.Add(new("Access-Control-Allow-Private-Network", "true"));
            h.Add(new("Connection", "close"));
            return Done(new AutoPauseAnswer(204, null, h));
        }
        if (req.Method != "GET" && req.Method != "POST") return Done(Json(405, Fail("GET or POST only"), origin));
        if (req.BodyTooBig) { _d.Log("a request with a body over 4 KB was refused"); return Done(Json(413, Fail("body over 4 KB"), origin)); }
        if (!TryTarget(req.Target, out var path, out var query)) { _d.Log("a request with a URL that could not be read was refused"); return Done(Json(400, Fail("bad url"), origin)); }
        var q = new AutoPauseQuery(query);
        if (path == "/watch") return Watch(q, origin, gone);
        return RouteSafe(req, path, q, origin);
    }

    async Task<AutoPauseAnswer?> RouteSafe(AutoPauseRequest req, string path, AutoPauseQuery q, string? origin)
    {
        try
        {
            var (code, obj) = await Route(req, path, q).ConfigureAwait(true);
            return Json(code, obj, origin);
        }
        catch (Exception e)
        {
            _d.Log($"a compatible-API request threw: {e}");
            return Json(500, Fail($"{Name} could not do that"), origin);
        }
    }

    async Task<(int, JsonObject)> Route(AutoPauseRequest req, string p, AutoPauseQuery q)
    {
        if (p == "/ping" || p == "/status") return (200, StatusJson());
        if (p == "/pause")
        {
            var key = AutoPauseGate.SrcKey(q.Get("src"));
            var ttl = AutoPauseGate.Ttl(q.Get("ttl"));
            if (_d.Refresh != null) await _d.Refresh().ConfigureAwait(true);
            var r = await _d.Engine.AddHold("http:" + key, false, _d.Now(), kind: "http", ttl: ttl, successor: q.Get("can") == "host").ConfigureAwait(true);
            _d.Log($"a hold came in over HTTP ({r.Action})");
            _d.Changed?.Invoke();
            Notify();
            return (200, StatusJson(r.Action, r.Reason));
        }
        if (p == "/resume")
        {
            var key = AutoPauseGate.SrcKey(q.Get("src"));
            var r = await _d.Engine.RemoveHold("http:" + key, "its app handed it back").ConfigureAwait(true);
            _d.Changed?.Invoke();
            Notify();
            return (200, StatusJson(r.Action, r.Reason));
        }
        if (p == "/now") { if (_d.Refresh != null) await _d.Refresh().ConfigureAwait(true); return (200, NowJson(_d.Media(), q, _d.Engine.Snapshot(_d.Now()))); }
        if (p == "/sessions")
        {
            var snap = _d.Engine.Snapshot(_d.Now());
            var list = new JsonArray(snap.Tracks.Select(t => (JsonNode?)new JsonObject { ["kind"] = t.Kind, ["realPlaying"] = t.Real, ["held"] = t.Held, ["status"] = t.Status }).ToArray());
            return (200, new JsonObject { ["ok"] = true, ["sessions"] = list });
        }
        if (p == "/control")
        {
            var cmd = q.Get("cmd") ?? "";
            if (cmd.Length == 0 && req.Body.Length > 0)
            {
                try { using var doc = JsonDocument.Parse(req.Body); cmd = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : ""; }
                catch (JsonException) { cmd = ""; }
            }
            if (!Commands.TryGetValue(cmd, out var action)) return (200, new JsonObject { ["ok"] = false, ["cmd"] = cmd.Length > 16 ? cmd.Substring(0, 16) : cmd, ["reason"] = "bad cmd" });
            var snap = _d.Engine.Snapshot(_d.Now());
            if ((cmd == "play" || cmd == "toggle") && snap.WePaused && snap.Holds.Count > 0) return (200, new JsonObject { ["ok"] = false, ["cmd"] = cmd, ["reason"] = "held", ["holds"] = snap.Holds.Count });
            var r = await _d.Control(action).ConfigureAwait(true);
            return (200, r.Ok ? new JsonObject { ["ok"] = true, ["cmd"] = cmd, ["result"] = true, ["source"] = Name }
                              : new JsonObject { ["ok"] = false, ["cmd"] = cmd, ["reason"] = string.IsNullOrEmpty(r.Reason) ? "no session" : r.Reason });
        }
        if (p == "/config")
        {
            if (req.Method == "POST") return (200, Fail($"{char.ToUpperInvariant(Name[0])}{Name.Substring(1)} does not turn Spotify’s ads down, so there is nothing to change here"));
            return (200, new JsonObject { ["ok"] = true, ["config"] = new JsonObject { ["adDuck"] = new JsonObject { ["enabled"] = false } }, ["server"] = Name });
        }
        return (404, Fail("not found"));
    }

    /// <summary>GET /now, PURE: the now-playing read of the shown session (else a playing one, else the first), art only when its
    /// hash differs from ?artHash= and never with ?art=0.</summary>
    public static JsonObject NowJson(JsonObject? m, AutoPauseQuery q, EngineSnapshot snap)
    {
        var list = m?["sessions"] as JsonArray;
        JsonObject? s = m?["shown"] as JsonObject;
        if (s == null && list != null) s = list.OfType<JsonObject>().FirstOrDefault(x => Bool(x["playing"])) ?? list.OfType<JsonObject>().FirstOrDefault();
        if (s == null)
            return new JsonObject { ["ok"] = true, ["source"] = "", ["title"] = "", ["artist"] = "", ["album"] = "", ["status"] = "none", ["positionMs"] = 0, ["durationMs"] = 0,
                ["canNext"] = false, ["canPrev"] = false, ["canPlayPause"] = false, ["held"] = false, ["art"] = null, ["artHash"] = "" };
        var tl = s["timeline"] is JsonObject t && !(t["available"] is JsonValue av && av.TryGetValue(out bool a) && !a) ? t : new JsonObject();
        var can = s["can"] as JsonObject ?? new JsonObject();
        var pos = Num(tl["positionNowMs"]); if (pos == 0) pos = Num(tl["positionMs"]);
        var status = Str(s["status"]); if (status.Length == 0) status = "none";
        var outJ = new JsonObject
        {
            ["ok"] = true, ["source"] = Str(s["aumid"]), ["title"] = Str(s["title"]), ["artist"] = Str(s["artist"]), ["album"] = Str(s["album"]),
            ["status"] = status, ["positionMs"] = (long)Math.Floor(pos + 0.5), ["durationMs"] = (long)Math.Floor(Num(tl["durationMs"]) + 0.5),
            ["canNext"] = Bool(can["next"]), ["canPrev"] = Bool(can["previous"]), ["canPlayPause"] = Bool(can["toggle"]) || Bool(can["play"]) || Bool(can["pause"]),
            ["held"] = snap.WePaused && snap.Holds.Count > 0, ["art"] = null, ["artMime"] = "", ["artHash"] = "",
        };
        var url = s["art"] is JsonValue sv && sv.TryGetValue(out string? u1) ? u1 ?? "" : s["art"] is JsonObject ao && ao["url"] is JsonValue uv && uv.TryGetValue(out string? u2) ? u2 ?? "" : "";
        var mm = Regex.Match(url, "^data:(image/[a-z]+);base64,(.+)$", RegexOptions.IgnoreCase);
        if (mm.Success)
        {
            var key = Str(s["artKey"]); if (key.Length == 0) key = url.Length > 64 ? url.Substring(url.Length - 64) : url;
            int h = 0;
            unchecked { foreach (var ch in key) h = h * 31 + ch; }
            var hash = ((uint)h).ToString("x");
            outJ["artHash"] = hash;
            outJ["artMime"] = mm.Groups[1].Value;
            if (q.Get("art") != "0" && q.Get("artHash") != hash) outJ["art"] = mm.Groups[2].Value;
        }
        return outJ;
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";
    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;
    // Any number: a value built in code holds an int or a long (TryGetValue<double> refuses those), a parsed one a JsonElement.
    static double Num(JsonNode? n) => Mantle.Core.Look.LookSyncCore.TryNum(n, out double d) && double.IsFinite(d) ? d : 0;

    // ---- start / stop

    /// <summary>Bind the first free port. Null = all taken by other software (logged): this app still decides for itself.</summary>
    public int? Start()
    {
        if (_server != null) return _port;
        foreach (var p in _d.Ports)
        {
            IDisposable? s;
            try { s = _d.Bind(p, this); }
            catch (Exception e) { _d.Log($"binding 127.0.0.1:{p} threw: {e.GetType().Name}: {e.Message}"); s = null; }
            if (s != null) { _server = s; _port = p; _d.Log($"serving the bridge-compatible API on 127.0.0.1:{p}"); return p; }
        }
        _d.Log(_d.Ports.Count == 0 ? "the bridge-compatible API is not served (no port to use)" : "the bridge-compatible API could not start: ports 8787-8790 are all taken");
        return null;
    }

    /// <summary>Close the API. Open /watch polls are not answered: their sockets close, which is how a client learns we left.</summary>
    public void Stop(string? why)
    {
        if (_server == null) return;
        var s = _server;
        _closing = true;
        foreach (var w in _watchers.ToList()) { w.Timer?.Dispose(); w.Done = true; w.Gone.Dispose(); w.Answer.TrySetResult(null); }
        _watchers.Clear();
        _server = null; _port = null;
        try { s.Dispose(); } catch (Exception e) { _d.Log($"closing the bridge-compatible API threw: {e.GetType().Name}"); }
        _closing = false;
        _d.Log($"bridge-compatible API closed ({(string.IsNullOrEmpty(why) ? "stopped" : why)})");
    }

    // ---- helpers

    static Task<AutoPauseAnswer?> Done(AutoPauseAnswer a) => Task.FromResult<AutoPauseAnswer?>(a);
    static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };

    /// Every JSON answer's headers (send() in the Electron host): no-store, Connection: close, Vary: Origin, CORS echoed past the gate.
    static AutoPauseAnswer Json(int code, JsonObject obj, string? origin)
    {
        var h = new List<KeyValuePair<string, string>> { new("Vary", "Origin") };
        if (origin != null) h.Add(new("Access-Control-Allow-Origin", origin));
        h.Add(new("Content-Type", "application/json"));
        h.Add(new("Cache-Control", "no-store"));
        h.Add(new("Connection", "close"));
        h.Add(new("Access-Control-Allow-Methods", "GET, POST, OPTIONS"));
        h.Add(new("Access-Control-Allow-Headers", "Content-Type"));
        return new AutoPauseAnswer(code, obj, h);
    }

    static IReadOnlyList<KeyValuePair<string, string>> WithOrigin(IReadOnlyList<KeyValuePair<string, string>> h, string origin)
    {
        if (h.Any(kv => kv.Key == "Access-Control-Allow-Origin")) return h;
        var l = h.ToList();
        l.Insert(1, new("Access-Control-Allow-Origin", origin));
        return l;
    }

    /// <summary>The request target as Node's <c>new URL(req.url, 'http://127.0.0.1')</c> would give its path and query: an
    /// origin-form "/path?query" or an absolute http URL. Anything else is a bad URL.</summary>
    public static bool TryTarget(string target, out string path, out string query)
    {
        path = ""; query = "";
        var t = target ?? "";
        int hash = t.IndexOf('#'); if (hash >= 0) t = t.Substring(0, hash);
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
            path = u.AbsolutePath; query = u.Query; return true;
        }
        if (!t.StartsWith('/') || t.StartsWith("//", StringComparison.Ordinal)) return false;
        if (t.Any(char.IsWhiteSpace)) return false;
        int qi = t.IndexOf('?');
        path = qi < 0 ? t : t.Substring(0, qi);
        query = qi < 0 ? "" : t.Substring(qi);
        return true;
    }

    /// Every refusal has words in the log, but a page cannot flood it: the first of each kind, then one line a minute with the count.
    void LogRefusal(string kind, string words)
    {
        var now = _d.Now();
        if (_refusedLogged.TryGetValue(kind, out var e) && now - e.At < 60000) { _refusedLogged[kind] = (e.At, e.Skipped + 1); return; }
        var more = _refusedLogged.TryGetValue(kind, out var e2) && e2.Skipped > 0 ? $" ({e2.Skipped} more like it in the last minute)" : "";
        _refusedLogged[kind] = (now, 0);
        _d.Log(words + more);
    }
}
