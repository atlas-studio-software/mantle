using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mantle.Core.Features;

// lib/features/_game-watch.js, ported. Game mode's process-list read is the ONE reader: it drives the
// watch and publishes every answer (made whole from the diff-only gamemode.scan replies), and the top
// bar follows it (a read every second while a known game runs, otherwise the slow backstop). The top
// bar reads on its own only while nothing drives. One hub per host (FeatureHost.GameWatch); a feature
// reaches it through IFeatureHostServices. Host thread only.

/// <summary>One follower of the watch (the top bar).</summary>
public interface IGameWatchSubscriber
{
    /// It wants answers now.
    bool Wants();
    /// How often it needs a read (ms), or null for the driver's own pace.
    int? FastMs();
    /// Its own focus events feed it, so the driver may use its slow backstop.
    bool Fed();
    /// One whole answer.
    void Result(JsonObject r);
}

/// <summary>The one reader (game mode): told when what the followers want has changed.</summary>
public interface IGameWatchDriver
{
    void Changed();
}

/// <summary>What <see cref="GameWatch.Whole"/> keeps between replies: the list as of generation <see cref="Gen"/>.</summary>
public sealed class GameWatchMemory
{
    public long? Gen { get; internal set; }
    internal OrderedDictionary<long, JsonObject>? Rows;
    public int? Count => Rows?.Count;
}

/// <summary>The hub (create() in _game-watch.js). A listener that throws never stops the rest.</summary>
public sealed class GameWatch
{
    static readonly string[] Slim = { "pid", "ppid", "file", "session" };

    readonly List<IGameWatchSubscriber> _subs = new();
    readonly List<Action<bool>> _driverFns = new();
    object? _driver;
    IGameWatchDriver? _driverChanged;

    /// <summary>Take the reading over. Disposing the handle gives it back (only if it is still this driver's).</summary>
    public IDisposable Drive(IGameWatchDriver? driver)
    {
        var me = (object?)driver ?? new object();
        _driver = me;
        _driverChanged = driver;
        foreach (var fn in _driverFns.ToArray()) Safe(() => fn(true));
        return new Off(() =>
        {
            if (!ReferenceEquals(_driver, me)) return;
            _driver = null;
            _driverChanged = null;
            foreach (var fn in _driverFns.ToArray()) Safe(() => fn(false));
        });
    }

    public bool Driven => _driver != null;

    public IDisposable OnDriver(Action<bool> fn) { _driverFns.Add(fn); return new Off(() => _driverFns.Remove(fn)); }

    public IDisposable Join(IGameWatchSubscriber sub)
    {
        _subs.Add(sub);
        Changed();
        return new Off(() => { if (_subs.Remove(sub)) Changed(); });
    }

    public void Changed() { var d = _driverChanged; if (d != null) Safe(d.Changed); }

    public bool Wanted() => Each().Any(s => Ask(s.Wants));

    /// The fastest pace a wanting follower needs, or null.
    public int? FastMs()
    {
        int? ms = null;
        foreach (var s in Each())
        {
            if (!Ask(s.Wants)) continue;
            int? m = null;
            try { m = s.FastMs(); } catch { /* one listener's throw never stops the rest */ }
            if (m is > 0 && (ms == null || m < ms)) ms = m;
        }
        return ms;
    }

    /// Every wanting follower is fed by its own events (and at least one wants).
    public bool Fed()
    {
        bool any = false, all = true;
        foreach (var s in Each())
        {
            if (!Ask(s.Wants)) continue;
            any = true;
            if (!Ask(s.Fed)) all = false;
        }
        return any && all;
    }

    public void Publish(JsonObject r)
    {
        foreach (var s in Each()) if (Ask(s.Wants)) Safe(() => s.Result(r));
    }

    IGameWatchSubscriber[] Each() => _subs.ToArray();
    static bool Ask(Func<bool> f) { try { return f(); } catch { return false; } }
    static void Safe(Action a) { try { a(); } catch { /* one listener's throw never stops the rest */ } }

    /// <summary>
    /// Turn a diff-only scan reply ({came, gone, procsSince, procsGen}) into a whole one ({procs}) against the
    /// list <paramref name="mem"/> holds. Null means "ask again for the whole list" (changes against a list
    /// Mantle does not hold are never applied). A whole reply is passed through and kept; an old sidecar's
    /// reply (no procsGen) is passed through and nothing is kept. Only pid, ppid, file and session are kept.
    /// </summary>
    public static JsonObject? Whole(GameWatchMemory mem, JsonObject? r)
    {
        if (r == null) return null;
        if (r["procs"] is JsonArray procs)
        {
            if (Number(r["procsGen"]) is { } gen && !Truthy(r["procsReason"]))
            {
                mem.Gen = gen;
                mem.Rows = new OrderedDictionary<long, JsonObject>();
                foreach (var p in procs.OfType<JsonObject>()) if (Number(p["pid"]) is { } pid) { mem.Rows.Remove(pid); mem.Rows.Add(pid, SlimOf(p)); }
            }
            else { mem.Gen = null; mem.Rows = null; }
            return r;
        }
        if (r["came"] is JsonArray came && r["gone"] is JsonArray gone)
        {
            var g = Number(r["procsGen"]);
            if (mem.Rows == null || Number(r["procsSince"]) != mem.Gen || g == null) { mem.Gen = null; mem.Rows = null; return null; }
            foreach (var x in gone) if (Number(x) is { } pid) mem.Rows.Remove(pid);
            foreach (var p in came.OfType<JsonObject>())
                if (Number(p["pid"]) is { } pid) { mem.Rows.Remove(pid); mem.Rows.Add(pid, SlimOf(p)); }   // a reused pid moves to the end, as a new process does
            mem.Gen = g;
            var out_ = new JsonObject();
            foreach (var kv in r)
                if (kv.Key is not ("came" or "gone" or "procsSince")) out_[kv.Key] = kv.Value?.DeepClone();
            out_["procs"] = new JsonArray(mem.Rows.Values.Select(v => (JsonNode)v.DeepClone()).ToArray());
            return out_;
        }
        return r;
    }

    static JsonObject SlimOf(JsonObject p)
    {
        var o = new JsonObject();
        foreach (var k in Slim) if (p.TryGetPropertyValue(k, out var v)) o[k] = v?.DeepClone();
        return o;
    }

    static long? Number(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d) && Math.Abs(d % 1) < double.Epsilon) return (long)d;
        return null;
    }

    static bool Truthy(JsonNode? n)
    {
        if (n == null) return false;
        if (n is not JsonValue v) return true;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s.Length > 0;
        if (v.TryGetValue<double>(out var d)) return d != 0 && !double.IsNaN(d);
        return true;
    }

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) { _a = a; }
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }
}

/// <summary>What a follower needs of its feature (follower(hub, h) in _game-watch.js).</summary>
public sealed class GameWatchFollowerHooks
{
    /// The feature is on and wants game answers.
    public required Func<bool> Active { get; init; }
    /// A known game runs now (the reader then goes to <see cref="EveryMs"/>).
    public required Func<bool> Running { get; init; }
    public required int EveryMs { get; init; }
    /// The feature's own focus events feed it.
    public Func<bool> Fed { get; init; } = () => false;
    /// Read the process list itself (only while nothing drives the watch).
    public required Func<Task<JsonObject?>> Read { get; init; }
    /// One answer (whole), from the hub or from its own read.
    public required Func<JsonObject?, Task> Seen { get; init; }
    public required HostClock Clock { get; init; }
    public Action<string> Log { get; init; } = _ => { };
    /// The feature's own "a check is running" flag, shared with its other checks.
    public GameWatchGate Gate { get; init; } = new();
}

/// <summary>A shared "a game check is running" flag.</summary>
public sealed class GameWatchGate { public bool Busy { get; set; } }

/// <summary>A feature that follows the watch: it hears every answer while it wants them, and reads on its own
/// every <see cref="GameWatchFollowerHooks.EveryMs"/> only while nothing drives the watch.</summary>
public sealed class GameWatchFollower
{
    readonly GameWatch _hub;
    readonly GameWatchFollowerHooks _h;
    readonly Sub _sub;
    bool _on, _wasRun;
    IDisposable? _t, _off, _unDriver;
    JsonObject? _next;
    bool _hasNext;

    public GameWatchFollower(GameWatch hub, GameWatchFollowerHooks h)
    {
        _hub = hub; _h = h;
        _sub = new Sub(this);
    }

    public bool On => _on;
    public int OwnReads { get; private set; }

    sealed class Sub : IGameWatchSubscriber
    {
        readonly GameWatchFollower _f;
        public Sub(GameWatchFollower f) { _f = f; }
        public bool Wants() => _f._on && _f._h.Active();
        public int? FastMs() => _f._h.Running() ? _f._h.EveryMs : null;
        public bool Fed() => _f._h.Fed();
        public void Result(JsonObject r) => _f.Take(r);
    }

    void StopTimer() { _t?.Dispose(); _t = null; }

    void Flip()   // a game started or exited: the reader goes to (or leaves) every EveryMs now
    {
        var run = _h.Running();
        if (run != _wasRun) { _wasRun = run; _hub.Changed(); }
    }

    void Drain()
    {
        var n = _next; var has = _hasNext;
        _next = null; _hasNext = false;
        if (has && _on) Take(n);
    }

    void Take(JsonObject? r)
    {
        if (!_on || !_h.Active()) return;
        if (_h.Gate.Busy) { _next = r; _hasNext = true; return; }
        _h.Gate.Busy = true;
        _ = SeenThen(r);
    }

    async Task SeenThen(JsonObject? r)
    {
        await Task.Yield();
        try { await _h.Seen(r); }
        catch (Exception e) { _h.Log($"the game check threw: {e.Message}"); }
        _h.Gate.Busy = false;
        Flip();
        Drain();
    }

    /// One read of its own, now (a no-op while another check runs).
    public async Task CheckAsync()
    {
        if (!_h.Active() || _h.Gate.Busy) return;
        _h.Gate.Busy = true;
        try { OwnReads++; await _h.Seen(await _h.Read()); }
        finally { _h.Gate.Busy = false; Flip(); Drain(); }
    }

    void Loop()
    {
        if (_t != null || !_on || !_h.Active() || _hub.Driven) return;
        _t = _h.Clock.Later(TimeSpan.FromMilliseconds(_h.EveryMs), async () =>
        {
            _t = null;
            try { await CheckAsync(); } catch (Exception e) { _h.Log($"the game check threw: {e.Message}"); }
            Loop();
        });
    }

    public void Start()
    {
        if (!_h.Active()) return;
        _on = true;
        _wasRun = _h.Running();
        _unDriver ??= _hub.OnDriver(_ => { if (_hub.Driven) StopTimer(); else Loop(); });
        _off ??= _hub.Join(_sub);
        Loop();
    }

    public void Stop()
    {
        _on = false; _next = null; _hasNext = false;
        StopTimer();
        _unDriver?.Dispose(); _unDriver = null;
        _off?.Dispose(); _off = null;
    }
}
