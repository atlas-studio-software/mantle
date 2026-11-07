using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Platform;

// The platform seam (PLAN.md decision A4): everything Mantle asks of Windows goes through IPlatform.
// Two transports implement it, with no difference a caller can see: out of process (Mantle.Sidecar.exe
// over the JSON-lines protocol, exactly as lib/sidecar.js speaks it; round 2, native-core) and in
// process (the same sidecar/*.cs linked into Mantle.exe; phase 7, behind a bench gate). Shaped after
// lib/sidecar.js and native/docs/sidecar-map.md sections 2 and 3. Contract only: no logic here.
//
// Threading: every member is called on the host thread. Replies complete, and every handler runs, on
// the host thread (the transport posts back to the host's SynchronizationContext); a handler never
// runs on a platform lane or on the stdout reader thread.

/// <summary>
/// The platform's worker lanes (threads), by their wire names in sidecar/Lanes.cs: ui is the
/// WinForms message loop (hooks, WinEvents, timers, hidden windows); capture, slow, qs, scan and
/// notif are STA workers; audio and focus are MTA workers.
/// </summary>
/// <remarks>
/// A caller never picks a lane. The platform picks it from the verb (and, for the read-or-switch
/// verbs such as <c>wifi</c> or <c>brightness</c>, from the arguments), exactly as Lanes.For does.
/// The names are here so the in-process transport, the status line and the logs say the same thing.
/// </remarks>
public enum PlatformLane { Ui, Capture, Slow, Audio, Qs, Focus, Scan, Notif }

/// lib/sidecar.js status: starting (no ready line yet), ok (ready), down (stopped, crashed, gave up).
public enum PlatformState { Starting, Ok, Down }

/// From the ready event: <c>{"ev":"ready","pid","passive","version"}</c>.
/// Passive: a <c>--test</c> platform that refuses every shell change.
public sealed record PlatformInfo(int Pid, bool Passive, string Version);

/// The platform's state with its reason in plain words, always, including when it is fine
/// ("sidecar 0.3.0 running as pid 1234"). This is ShellState's <c>sidecar</c> piece.
public sealed record PlatformStatus(PlatformState State, string Reason, PlatformInfo? Info = null)
{
    public bool Ready => State == PlatformState.Ok;
}

/// <summary>How a call ended. Only <see cref="Ok"/> carries a result; every other outcome carries plain words.</summary>
public enum PlatformOutcome
{
    /// <c>{"ok":true,"result":..}</c>.
    Ok,
    /// <c>{"ok":false,"error":"plain words"}</c>: the verb ran (or was refused by the platform) and failed.
    Failed,
    /// <c>"reason":"expired"</c>: the call's ttl passed before a lane reached it, so it never ran.
    Expired,
    /// <c>"reason":"busy"</c>: the lane already held 1024 waiting calls, so it never ran.
    Busy,
    /// <c>"reason":"cancelled"</c>: superseded (a newer morph of the same window), or the caller cancelled.
    Cancelled,
    /// No answer within the caller's timeout. The verb may still run; a late answer is counted, never reported as a mystery.
    TimedOut,
    /// Never sent: the platform is not running or not ready, or the arguments use a reserved key.
    Refused,
}

/// <summary>
/// One call's answer. Never thrown: a failure is a state with a reason, not an exception, because
/// the bar and the launcher put <see cref="Error"/> straight on screen.
/// </summary>
/// <param name="Result">The reply's <c>result</c> (a detached element, safe to keep);
/// <see cref="JsonValueKind.Undefined"/> unless the outcome is Ok.</param>
/// <param name="Error">Plain words for every outcome but Ok, null for Ok.</param>
public sealed record PlatformReply(PlatformOutcome Outcome, JsonElement Result, string? Error)
{
    public bool Ok => Outcome == PlatformOutcome.Ok;

    public static PlatformReply Success(JsonElement result) => new(PlatformOutcome.Ok, result, null);

    public static PlatformReply Fail(PlatformOutcome outcome, string error)
    {
        if (outcome == PlatformOutcome.Ok) throw new ArgumentException("a failed reply needs an outcome other than Ok", nameof(outcome));
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("a failed reply needs its reason in plain words", nameof(error));
        return new(outcome, default, error);
    }
}

/// <summary>A pushed event: a protocol line with <c>ev</c> instead of <c>id</c>.</summary>
/// <param name="Name">The <c>ev</c> value: <c>hotkey</c>, <c>windows</c>, <c>battery</c>, <c>clip</c>, ... (sidecar-map.md section 2 lists all 57).</param>
/// <param name="Body">The whole event object, <c>ev</c> included (a detached element, safe to keep).</param>
public sealed record PlatformEvent(string Name, JsonElement Body);

/// Numbers from lib/sidecar.js and lib/features.js that callers and both transports must agree on.
public static class PlatformDefaults
{
    /// lib/sidecar.js CALL_TIMEOUT_MS: a call with no timeout of its own.
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(8000);
    /// lib/features.js ctx.sidecar.send: a feature's call with no timeout of its own.
    public static readonly TimeSpan FeatureCallTimeout = TimeSpan.FromMilliseconds(4000);
    /// lib/sidecar.js READY_WAIT_MS: how long a call made while starting waits for the ready line.
    public static readonly TimeSpan ReadyWait = TimeSpan.FromMilliseconds(8000);
    /// The protocol's own keys: an argument with one of these names would overwrite the request's, so it is refused.
    public static readonly string[] ReservedArgKeys = { "id", "cmd" };
}

/// <summary>What features, surfaces and the host may ask of the platform.</summary>
public interface IPlatform
{
    /// The current state. Read on the host thread; it changes only between host-thread turns.
    PlatformStatus Status { get; }

    /// <summary>
    /// Run one platform verb (<c>tbhide</c>, <c>appbar</c>, <c>clip.list</c>, ...), never throwing.
    /// </summary>
    /// <param name="verb">The wire verb, unchanged from the sidecar (built-in or <c>feature.verb</c>).</param>
    /// <param name="args">The request's arguments; never <c>id</c> or <c>cmd</c>. Serialised before the
    /// call returns, so the caller may reuse the object afterwards.</param>
    /// <param name="timeout">The caller's own deadline (default <see cref="PlatformDefaults.CallTimeout"/>).
    /// It is sent as the request's <c>ttl</c>; a call made while the platform is starting waits for the
    /// ready line and is then sent with the time it has LEFT as its ttl, so the caller never waits longer
    /// in total than it asked for.</param>
    /// <param name="cancel">Gives up waiting (outcome Cancelled); a request already sent may still run.</param>
    ValueTask<PlatformReply> CallAsync(string verb, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancel = default);

    /// Every <paramref name="ev"/> event from now on, until the returned handle is disposed.
    IDisposable On(string ev, Action<PlatformEvent> handler);

    /// Every status change (repeats included: a repeated reason is cheaper than a missed one).
    IDisposable OnStatus(Action<PlatformStatus> handler);

    /// Now, if the platform is ready, and again after EVERY restart: re-apply platform-side state here
    /// (a watch, a hook's chord list, a hidden OSD).
    IDisposable OnReady(Action handler);
}

/// <summary>The host's handle on the platform's life (native-core's Lifecycle); features and surfaces get <see cref="IPlatform"/> only.</summary>
public interface IPlatformControl : IPlatform
{
    /// Start, or restart. Never throws: a failure to start is a status (Down, with the reason).
    void Start();

    /// The way back after the restart ladder gave up (lib/sidecar.js retry()).
    void Retry();

    /// End the platform: its own restore runs (stdin EOF out of process), then it is ended after
    /// the grace time. <paramref name="why"/> is the status reason from then on.
    ValueTask StopAsync(string why);
}
