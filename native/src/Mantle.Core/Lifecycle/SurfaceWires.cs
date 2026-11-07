using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Logging;
using Mantle.Core.State;

namespace Mantle.Core.Lifecycle;

// The threads between the host and the surfaces (PLAN.md 3.3), without UI types so the unit tests can drive
// them: a surface's UI thread posts commands to the host thread and never waits on it; the host thread posts
// state snapshots and events to the UI thread. Mantle.App's SurfaceContext is a thin ISurfaceContext over
// SurfaceLink; SurfaceControl is what surface.show/hide/toggle and Alt+Space call from the host thread.

/// <summary>How a surface link reaches the two threads and the host's pieces.</summary>
public sealed class SurfaceWires
{
    /// Run on the surface's UI thread; false when that thread is gone (Mantle is closing).
    public required Func<Action, bool> PostUi { get; init; }
    /// Run on the host thread, after what is queued there.
    public required Action<Action> PostHost { get; init; }
    public required ICommandBus Bus { get; init; }
    public required IEvents Events { get; init; }
    public required StateFeed Feed { get; init; }
    public required ILogSink Log { get; init; }
}

/// <summary>
/// ShellState for the UI thread: the host thread pushes every snapshot; the UI thread gets the LAST of a burst
/// (coalesced: one UI job at a time) and keeps it as its own copy, so a surface reads State without a lock.
/// </summary>
public sealed class StateFeed
{
    readonly Func<Action, bool> _postUi;
    readonly Action<string> _log;
    readonly object _gate = new();
    readonly List<Action<ShellState>> _handlers = new();
    ShellState? _pending;
    bool _queued;

    public StateFeed(ShellState initial, Func<Action, bool> postUi, Action<string> log)
    {
        Current = initial; _postUi = postUi; _log = log;
    }

    /// The snapshot this UI thread has been given (UI thread).
    public ShellState Current { get; private set; }

    /// How many UI jobs carried snapshots (for the tests: a burst is one job).
    public int Deliveries { get; private set; }

    /// A new snapshot (any thread; the host thread in the app).
    public void Push(ShellState s)
    {
        lock (_gate) {
            _pending = s;
            if (_queued) return;
            _queued = true;
        }
        if (!_postUi(Deliver)) lock (_gate) _queued = false;
    }

    void Deliver()
    {
        ShellState s;
        lock (_gate) { s = _pending!; _pending = null; _queued = false; }
        Current = s;
        Deliveries++;
        foreach (var h in _handlers.ToArray()) {
            try { h(s); } catch (Exception e) { _log($"surfaces: a state handler failed ({e.GetType().Name}: {e.Message})"); }
        }
    }

    /// Each new snapshot, on the UI thread (UI thread).
    public IDisposable Subscribe(Action<ShellState> handler)
    {
        _handlers.Add(handler);
        return new Undo(() => _handlers.Remove(handler));
    }

    internal sealed class Undo : IDisposable
    {
        Action? _a;
        public Undo(Action a) { _a = a; }
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }
}

/// <summary>A log whose every line starts with the surface's name ("launcher: ...").</summary>
public sealed class PrefixedLog : ILogSink
{
    readonly ILogSink _inner;
    readonly string _prefix;
    public PrefixedLog(ILogSink inner, string name) { _inner = inner; _prefix = name + ": "; }
    public void Line(string text)
    {
        foreach (var l in (text ?? "").Split('\n')) _inner.Line(_prefix + l.TrimEnd('\r'));
    }
}

/// <summary>
/// The host end of one surface's context (ISurfaceContext minus the dispatcher). Every member is for the surface's
/// UI thread; the host side runs on the host thread. Disposed when the surface is destroyed: its subscriptions go,
/// and nothing already queued for it is delivered after that.
/// </summary>
public sealed class SurfaceLink : IDisposable
{
    readonly SurfaceWires _w;
    readonly List<IDisposable> _subs = new();
    bool _gone;

    public SurfaceLink(string name, SurfaceWires wires)
    {
        Name = name; _w = wires;
        Log = new PrefixedLog(wires.Log, name);
    }

    public string Name { get; }
    public ILogSink Log { get; }
    public ShellState State => _w.Feed.Current;

    public IDisposable OnState(Action<ShellState> handler)
    {
        var d = _w.Feed.Subscribe(s => { if (!_gone) handler(s); });
        _subs.Add(d);
        return d;
    }

    /// Posted to the host thread (the caller name is the surface's), answered on the UI thread. The arguments are
    /// copied first, so the surface may reuse its object at once.
    public ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var copy = args?.DeepClone() as JsonObject;
        void Answer(CommandResult r) { if (!_w.PostUi(() => tcs.TrySetResult(r))) tcs.TrySetResult(r); }
        async Task OnHost()
        {
            CommandResult r;
            try { r = await _w.Bus.CallAsync(name, copy, Name).ConfigureAwait(true); }
            catch (Exception e) { r = CommandResult.Fail($"{name} failed on the host thread: {e.Message}"); }
            Answer(r);
        }
        try {
            _w.PostHost(() => { _ = OnHost(); });
        } catch (Exception e) {
            tcs.TrySetResult(CommandResult.Fail($"{name} could not be posted to the host thread: {e.Message}"));
        }
        return new ValueTask<CommandResult>(tcs.Task);
    }

    /// Subscribed on the host thread; every payload (the replayed current value first, when the event has a replay
    /// source) is re-posted to the UI thread. Disposing it (UI thread) stops deliveries at once.
    public IDisposable On<T>(string ev, Action<T> handler)
    {
        var sub = new EventSub();
        _subs.Add(sub);
        _w.PostHost(() => {
            if (sub.Off || _gone) return;
            try {
                sub.Host = _w.Events.Subscribe<T>(ev, p => {
                    if (sub.Off) return;
                    _w.PostUi(() => {
                        if (sub.Off || _gone) return;
                        try { handler(p); } catch (Exception e) { Log.Line($"its \"{ev}\" handler failed ({e.GetType().Name}: {e.Message})"); }
                    });
                });
            } catch (Exception e) { Log.Line($"could not subscribe \"{ev}\": {e.Message}"); }
        });
        sub.Unsubscribe = () => _w.PostHost(() => sub.Host?.Dispose());
        return sub;
    }

    sealed class EventSub : IDisposable
    {
        public volatile bool Off;
        public IDisposable? Host;
        public Action? Unsubscribe;
        public void Dispose()
        {
            if (Off) return;
            Off = true;
            try { Unsubscribe?.Invoke(); } catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _gone = true;
        foreach (var s in _subs) { try { s.Dispose(); } catch (Exception) { } }
        _subs.Clear();
    }
}

/// <summary>What surface.show/hide/toggle/list and the shell shortcuts ask the surfaces (host thread).</summary>
public interface ISurfaceControl
{
    /// <paramref name="verb"/>: show, hide or toggle.
    Task<SurfaceAnswer> RunAsync(string verb, string name, string why);
    Task<IReadOnlyList<SurfaceInfo>> ListAsync();
}

/// <summary>
/// The host thread's road to the surfaces: each ask is posted to the UI thread that owns the SurfaceSet, and the
/// answer comes back to whoever awaits (the host thread resumes on its own context). Before the set exists the
/// answer says so; it never waits on the UI thread.
/// </summary>
public sealed class SurfaceControl : ISurfaceControl
{
    readonly Func<SurfaceSet?> _set;
    readonly Func<Action, bool> _postUi;

    public SurfaceControl(Func<SurfaceSet?> set, Func<Action, bool> postUi) { _set = set; _postUi = postUi; }

    Task<T> OnUi<T>(Func<SurfaceSet, T> work, Func<string, T> no)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted;
        try {
            posted = _postUi(() => {
                try {
                    var s = _set();
                    tcs.TrySetResult(s == null ? no("the surfaces are not set up yet (Mantle is still starting)") : work(s));
                } catch (Exception e) { tcs.TrySetResult(no("it threw on the UI thread: " + e.Message)); }
            });
        } catch (Exception e) { posted = false; tcs.TrySetResult(no("it could not reach the UI thread: " + e.Message)); }
        if (!posted) tcs.TrySetResult(no("the UI thread is gone (Mantle is closing)"));
        return tcs.Task;
    }

    public Task<SurfaceAnswer> RunAsync(string verb, string name, string why)
        => OnUi(s => s.Run(verb, name, why), r => new SurfaceAnswer(false, name, false, $"{name} was not {Past(verb)}: {r}"));

    public Task<IReadOnlyList<SurfaceInfo>> ListAsync()
        => OnUi(s => s.List(), _ => (IReadOnlyList<SurfaceInfo>)Array.Empty<SurfaceInfo>());

    static string Past(string verb) => verb switch { "show" => "shown", "hide" => "hidden", "toggle" => "toggled", _ => verb };

    /// <summary>A shell shortcut that toggles a surface (Alt+Space -> the launcher, main.js toggleLauncher): an answer
    /// that is not ok is one plain log line, never a silent no-op.</summary>
    public static async Task ToggleFromKey(ISurfaceControl c, string name, string key, Action<string> log)
    {
        SurfaceAnswer a;
        try { a = await c.RunAsync("toggle", name, key).ConfigureAwait(true); }
        catch (Exception e) { log($"mantle: {key}: the {name} toggle threw: {e.Message}"); return; }
        if (!a.Ok) log($"mantle: {key}: {a.Said}");
    }
}
