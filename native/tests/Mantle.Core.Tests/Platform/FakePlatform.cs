using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Tests.Platform;

/// <summary>
/// An IPlatform for tests: no process, no Windows. Every lane may use it (it is public in the test
/// assembly); native-core owns it. Single-threaded and synchronous: calls answer inline and handlers
/// run inline, which satisfies the host-thread rule trivially.
/// </summary>
/// <remarks>
/// It keeps the client rules a caller can see: calls are refused (outcome Refused, the same words as
/// lib/sidecar.js) while it is not ready or when the arguments use a reserved key, an unknown verb
/// fails with the sidecar's own words, OnReady fires now if ready and after every (re)start, and
/// every status change is published, repeats included.
/// </remarks>
public sealed class FakePlatform : IPlatformControl
{
    public sealed record Call(string Verb, string ArgsJson, TimeSpan Timeout);

    readonly Dictionary<string, Func<JsonElement, PlatformReply>> _verbs = new(StringComparer.Ordinal);
    readonly List<(string Ev, Action<PlatformEvent> Fn)> _events = new();
    readonly List<Action<PlatformStatus>> _status = new();
    readonly List<Action> _ready = new();

    public FakePlatform() { Status = new PlatformStatus(PlatformState.Starting, "sidecar has not been started yet"); }

    public PlatformStatus Status { get; private set; }

    /// Every call that reached the fake, refused ones included, in order.
    public List<Call> Calls { get; } = new();

    public int Starts { get; private set; }

    /// <summary>Answer <paramref name="verb"/> with <paramref name="reply"/> (it gets the args as an object).</summary>
    public FakePlatform Handle(string verb, Func<JsonElement, PlatformReply> reply) { _verbs[verb] = reply; return this; }

    /// Answer <paramref name="verb"/> with this JSON result.
    public FakePlatform Answer(string verb, string resultJson) => Handle(verb, _ => PlatformReply.Success(Parse(resultJson)));

    /// Push an event, as the platform would: <c>Raise("hotkey", "{\"chord\":\"Win+V\"}")</c>.
    public void Raise(string ev, string bodyJson = "{}")
    {
        var body = JsonNode.Parse(bodyJson) as JsonObject ?? throw new ArgumentException("an event body is a JSON object", nameof(bodyJson));
        body["ev"] = ev;
        var e = new PlatformEvent(ev, Parse(body.ToJsonString()));
        foreach (var (name, fn) in _events.ToArray())
            if (name == ev) fn(e);
    }

    /// Move to a state and publish it. Ok fires the OnReady handlers.
    public void SetStatus(PlatformState state, string reason, PlatformInfo? info = null)
    {
        Status = new PlatformStatus(state, reason, info ?? Status.Info);
        foreach (var fn in _status.ToArray()) fn(Status);
        if (state == PlatformState.Ok)
            foreach (var fn in _ready.ToArray()) fn();
    }

    public void Start()
    {
        Starts++;
        var info = new PlatformInfo(0, true, "fake");
        SetStatus(PlatformState.Ok, $"sidecar {info.Version} running as pid {info.Pid}, passive", info);
    }

    public void Retry() => Start();

    public ValueTask StopAsync(string why)
    {
        SetStatus(PlatformState.Down, why);
        return ValueTask.CompletedTask;
    }

    public ValueTask<PlatformReply> CallAsync(string verb, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancel = default)
    {
        string json = args?.ToJsonString() ?? "{}";
        Calls.Add(new Call(verb, json, timeout ?? PlatformDefaults.CallTimeout));
        if (cancel.IsCancellationRequested)
            return Done(PlatformReply.Fail(PlatformOutcome.Cancelled, $"\"{verb}\" was cancelled before it was sent"));
        if (!Status.Ready)
            return Done(PlatformReply.Fail(PlatformOutcome.Refused, $"cannot run \"{verb}\": {Status.Reason}"));
        if (args != null)
            foreach (string key in PlatformDefaults.ReservedArgKeys)
                if (args.ContainsKey(key))
                    return Done(PlatformReply.Fail(PlatformOutcome.Refused,
                        $"cannot run \"{verb}\": its arguments use the reserved key \"{key}\", which would overwrite the request's own"));
        if (!_verbs.TryGetValue(verb, out var handler))
            return Done(PlatformReply.Fail(PlatformOutcome.Failed, $"unknown command: {verb}"));
        return Done(handler(Parse(json)));
    }

    public IDisposable On(string ev, Action<PlatformEvent> handler)
    {
        var entry = (ev, handler);
        _events.Add(entry);
        return new Unsub(() => _events.Remove(entry));
    }

    public IDisposable OnStatus(Action<PlatformStatus> handler)
    {
        _status.Add(handler);
        return new Unsub(() => _status.Remove(handler));
    }

    public IDisposable OnReady(Action handler)
    {
        _ready.Add(handler);
        if (Status.Ready) handler();
        return new Unsub(() => _ready.Remove(handler));
    }

    public int Subscribers => _events.Count + _status.Count + _ready.Count;

    static ValueTask<PlatformReply> Done(PlatformReply r) => ValueTask.FromResult(r);

    static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    sealed class Unsub : IDisposable
    {
        Action? _undo;
        public Unsub(Action undo) { _undo = undo; }
        public void Dispose() { Interlocked.Exchange(ref _undo, null)?.Invoke(); }
    }
}
