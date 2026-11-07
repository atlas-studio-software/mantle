using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Platform;
using Mantle.Core.State;
using Mantle.Core.Tests.Platform;

namespace Mantle.Core.Tests.Features;

// The feature host's test kit: a hand-moved clock, a one-thread host loop (Node's event-loop
// semantics, as the real host thread has), and in-memory stand-ins for the bus, events, stores and the
// state hub. Nothing touches a file, a window or the keyboard.

/// <summary>A TimeProvider whose time moves only when a test says so; due timers fire in order.</summary>
public sealed class ManualTime : TimeProvider
{
    long _ticks;
    readonly List<Timer> _timers = new();
    public HostLoop? Loop { get; set; }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public int PendingTimers { get { lock (_timers) return _timers.Count(t => t.Due != null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new Timer(this, callback, state);
        lock (_timers) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        var end = _ticks + by.Ticks;
        while (true)
        {
            Timer? next;
            lock (_timers) next = _timers.Where(t => t.Due is long d && d <= end).OrderBy(t => t.Due).FirstOrDefault();
            if (next == null) break;
            _ticks = Math.Max(_ticks, next.Due!.Value);
            next.Due = next.Period > 0 ? _ticks + next.Period : null;
            next.Callback(next.State);
            Loop?.Drain();
        }
        _ticks = end;
        Loop?.Drain();
    }

    public void AdvanceMs(double ms) => Advance(TimeSpan.FromMilliseconds(ms));

    sealed class Timer : ITimer
    {
        readonly ManualTime _owner;
        public readonly TimerCallback Callback;
        public readonly object? State;
        public long? Due;
        public long Period;
        public Timer(ManualTime owner, TimerCallback cb, object? state) { _owner = owner; Callback = cb; State = state; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._ticks + Math.Max(0, dueTime.Ticks);
            Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            return true;
        }
        public void Dispose() { Due = null; lock (_owner._timers) _owner._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>One thread, one queue: the host thread as a test sees it.</summary>
public sealed class HostLoop : SynchronizationContext
{
    readonly Queue<(SendOrPostCallback Cb, object? State)> _q = new();

    public override void Post(SendOrPostCallback d, object? state) { lock (_q) _q.Enqueue((d, state)); }
    public override void Send(SendOrPostCallback d, object? state) => d(state);

    bool TryOne()
    {
        (SendOrPostCallback Cb, object? State) item;
        lock (_q) { if (_q.Count == 0) return false; item = _q.Dequeue(); }
        item.Cb(item.State);
        return true;
    }

    public void Drain() { while (TryOne()) { } }

    /// <summary>Run a test body on the loop. A body that waits on something nothing will finish fails
    /// with words (advance the clock first) instead of hanging the run.</summary>
    public static void Run(Func<HostLoop, ManualTime, Task> body)
    {
        var prev = Current;
        var loop = new HostLoop();
        var time = new ManualTime { Loop = loop };
        SetSynchronizationContext(loop);
        try
        {
            var t = body(loop, time);
            var idleSince = DateTime.UtcNow;
            while (!t.IsCompleted)
            {
                if (loop.TryOne()) { idleSince = DateTime.UtcNow; continue; }
                if ((DateTime.UtcNow - idleSince).TotalSeconds > 3)
                    throw new Xunit.Sdk.XunitException("the test waits on something nothing will finish (advance the clock before awaiting)");
                Thread.Sleep(1);
            }
            loop.Drain();
            t.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(prev); }
    }
}

public sealed class TestBus : ICommandBus
{
    readonly Dictionary<string, CommandRegistration> _all = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> Names => _all.Keys.ToArray();
    public bool Has(string name) => _all.ContainsKey(name);

    public IDisposable Register(CommandRegistration c)
    {
        if (_all.TryGetValue(c.Name, out var cur)) throw new InvalidOperationException($"the command \"{c.Name}\" already exists (owned by {cur.Owner}; {c.Owner} asked for it too)");
        _all[c.Name] = c;
        return new Off(() => { if (_all.TryGetValue(c.Name, out var now) && ReferenceEquals(now, c)) _all.Remove(c.Name); });
    }

    public async ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null, string caller = "host", CancellationToken cancel = default)
    {
        if (!_all.TryGetValue(name, out var c)) return CommandResult.Fail($"unknown command: {name}");
        var el = FeatureOptions.Parse(args?.ToJsonString() ?? "{}");
        return await c.Handler(new CommandCall(name, el, caller, cancel));
    }
}

public sealed class TestEvents : IEvents
{
    public readonly List<(string Name, object? Payload)> Published = new();
    readonly Dictionary<string, Func<object?>> _replays = new(StringComparer.Ordinal);
    readonly List<(string Name, Action<object?> Fn)> _subs = new();

    public void Publish<T>(string name, T payload)
    {
        Published.Add((name, payload));
        foreach (var s in _subs.Where(s => s.Name == name).ToArray()) s.Fn(payload);
    }

    public IDisposable Subscribe<T>(string name, Action<T> handler)
    {
        (string, Action<object?>) e = (name, o => handler((T)o!));
        _subs.Add(e);
        if (_replays.TryGetValue(name, out var cur)) handler((T)cur()!);
        return new Off(() => _subs.Remove(e));
    }

    public IDisposable Replay<T>(string name, Func<T> current)
    {
        if (_replays.ContainsKey(name)) throw new InvalidOperationException($"{name} already has a replay source");
        Func<object?> f = () => current();
        _replays[name] = f;
        return new Off(() => { if (_replays.TryGetValue(name, out var now) && now == f) _replays.Remove(name); });
    }

    public bool HasReplay(string name) => _replays.ContainsKey(name);
    public object? ReplayValue(string name) => _replays.TryGetValue(name, out var f) ? f() : null;
}

public sealed class TestHub : IShellStateHub
{
    readonly List<Action<ShellState>> _fns = new();
    public ShellState Current { get; private set; } = ShellState.Initial(ShellMode.Test, BarEdge.Bottom);
    public int Changes { get; private set; }

    public bool Set(Func<ShellState, ShellState> change)
    {
        var next = change(Current);
        if (ReferenceEquals(next, Current)) return false;
        var same = next with { Features = Current.Features } == Current
            && next.Features.Count == Current.Features.Count
            && next.Features.All(kv => Current.Features.TryGetValue(kv.Key, out var v) && v == kv.Value);
        if (same) return false;
        Current = next;
        Changes++;
        foreach (var fn in _fns.ToArray()) fn(next);
        return true;
    }

    public IDisposable OnChange(Action<ShellState> handler) { _fns.Add(handler); return new Off(() => _fns.Remove(handler)); }

    public FeaturePiece Piece(string name) => Current.Features[name];
}

public sealed class MemoryStore : IStore
{
    readonly JsonObject _doc = new();
    public MemoryStore(string name) { Name = name; }
    public string Name { get; }
    public ReadState State { get; set; } = ReadState.Missing;
    public string? Reason { get; set; }
    public string? FailWrites { get; set; }
    public int Writes { get; private set; }

    public JsonElement Snapshot() => FeatureOptions.Parse(_doc.ToJsonString());
    public bool TryGet(string key, out JsonElement value)
    {
        if (_doc.TryGetPropertyValue(key, out var n)) { value = FeatureOptions.Parse(n?.ToJsonString() ?? "null"); return true; }
        value = default;
        return false;
    }
    ValueTask<StoreWrite> Done() { if (FailWrites != null) return new(StoreWrite.Failed(FailWrites)); Writes++; return new(StoreWrite.Done); }
    public ValueTask<StoreWrite> SetAsync(string key, JsonNode? value)
    {
        if (FailWrites != null) return new(StoreWrite.Failed(FailWrites));
        _doc[key] = value?.DeepClone();
        return Done();
    }
    public ValueTask<StoreWrite> RemoveAsync(string key) { if (FailWrites == null) _doc.Remove(key); return Done(); }
    public ValueTask<StoreWrite> UpdateAsync(Action<JsonObject> edit) { if (FailWrites == null) edit(_doc); return Done(); }
    public IDisposable OnChanged(Action<IStore> handler) => new Off(() => { });
    public void Put(string key, JsonNode? v) => _doc[key] = v;
}

public sealed class MemoryStores : IStores
{
    public readonly Dictionary<string, MemoryStore> All = new(StringComparer.OrdinalIgnoreCase);
    public IStore Open(string relativePath) => Get(relativePath);
    public MemoryStore Get(string relativePath) => All.TryGetValue(relativePath, out var s) ? s : All[relativePath] = new MemoryStore(relativePath);
}

/// <summary>A feature made of delegates, with a record of what happened to it.</summary>
public sealed class ScriptFeature : IFeature
{
    public Func<IFeatureContext, ValueTask>? Install;
    public Func<FeatureStop, ValueTask>? Stop;
    public IFeatureContext? Ctx;
    public readonly List<FeatureStop> Stops = new();

    public ValueTask InstallAsync(IFeatureContext ctx) { Ctx = ctx; return Install?.Invoke(ctx) ?? ValueTask.CompletedTask; }
    public ValueTask StopAsync(FeatureStop stop) { Stops.Add(stop); return Stop?.Invoke(stop) ?? ValueTask.CompletedTask; }
}

/// <summary>A host with every stand-in, ready to load.</summary>
public sealed class Rig
{
    public readonly FakePlatform Platform = new();
    public readonly TestBus Bus = new();
    public readonly TestEvents Events = new();
    public readonly MemoryStores Stores = new();
    public readonly TestHub Hub = new();
    public readonly List<string> Lines = new();
    public readonly ManualTime Time;
    public readonly Dictionary<string, List<ScriptFeature>> Made = new(StringComparer.Ordinal);
    FeatureHost? _host;

    public Rig(ManualTime time, bool test = true)
    {
        Time = time;
        Test = test;
    }

    public bool Test { get; }
    public FeatureHost Host => _host ??= new FeatureHost(new FeatureHostOptions
    {
        Flags = new FeatureFlags(Test, !Test),
        Platform = Platform,
        Commands = Bus,
        Events = Events,
        Stores = Stores,
        State = Hub,
        Log = Lines.Add,
        Clock = new HostClock(Time),
    });

    /// The newest instance made for a feature.
    public ScriptFeature Last(string name) => Made[name][^1];

    public bool Logged(string part) => Lines.Any(l => l.Contains(part, StringComparison.Ordinal));

    /// A descriptor whose every instance runs <paramref name="setup"/> on the fresh ScriptFeature.
    public FeatureDescriptor D(string name, Action<ScriptFeature>? setup = null, string[]? chords = null, string[]? observe = null,
        bool rest = false, bool defaultOn = true, IReadOnlyList<FeatureOption>? options = null, string? label = null)
    {
        var a = new FeatureAttribute(name, label ?? name.ToUpperInvariant(), defaultOn) { Chords = chords ?? Array.Empty<string>(), Observe = observe ?? Array.Empty<string>(), Rest = rest };
        return FeatureDescriptor.Of(a, () =>
        {
            var f = new ScriptFeature();
            setup?.Invoke(f);
            if (!Made.TryGetValue(name, out var list)) Made[name] = list = new List<ScriptFeature>();
            list.Add(f);
            return f;
        }, options);
    }

    /// The platform answers the hotkey call with the hook holding what was asked.
    public Rig HookTakesAll()
    {
        Platform.Handle("hotkey", args =>
        {
            var chords = args.TryGetProperty("chords", out var c) ? c.GetRawText() : "[]";
            return PlatformReply.Success(FeatureOptions.Parse($"{{\"installed\":true,\"chords\":{chords}}}"));
        });
        return this;
    }

    public int HotkeyCalls => Platform.Calls.Count(c => c.Verb == "hotkey");
    public FakePlatform.Call LastHotkey => Platform.Calls.Last(c => c.Verb == "hotkey");
    /// The last hotkey call's arguments with JSON's escapes undone (the encoder writes "+" as an escape).
    public string LastHotkeyJson => System.Text.RegularExpressions.Regex.Unescape(LastHotkey.ArgsJson);
}

sealed class Off : IDisposable
{
    Action? _a;
    public Off(Action a) { _a = a; }
    public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
}
