using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;

namespace Mantle.Core.Tests;

/// A host scheduler with a virtual clock: nothing runs until the test advances time.
public sealed class ManualScheduler : IHostScheduler
{
    readonly Queue<Action> _posted = new();
    readonly List<(double At, long Seq, Timer T)> _timers = new();
    long _seq;
    public double NowMs { get; private set; }
    public bool IsHostThread => true;

    public void Post(Action work) => _posted.Enqueue(work);

    public IDisposable Delay(TimeSpan after, Action work)
    {
        var t = new Timer(work);
        _timers.Add((NowMs + Math.Max(0, after.TotalMilliseconds), _seq++, t));
        return t;
    }

    /// Run everything posted, then every timer due within <paramref name="ms"/>, in time order.
    public void Advance(double ms = 0)
    {
        double end = NowMs + ms;
        Drain();
        while (true) {
            var due = _timers.Where(x => !x.T.Cancelled && x.At <= end).OrderBy(x => x.At).ThenBy(x => x.Seq).FirstOrDefault();
            if (due.T == null) break;
            _timers.Remove(due);
            NowMs = Math.Max(NowMs, due.At);
            due.T.Fire();
            Drain();
        }
        _timers.RemoveAll(x => x.T.Cancelled);
        NowMs = end;
    }

    void Drain() { while (_posted.Count > 0) _posted.Dequeue()(); }

    public sealed class Timer : IDisposable
    {
        readonly Action _work;
        public bool Cancelled;
        public Timer(Action w) { _work = w; }
        public void Fire() { if (!Cancelled) { Cancelled = true; _work(); } }
        public void Dispose() => Cancelled = true;
    }
}

/// A sidecar process that never runs: the test plays its stdout and its exit.
public sealed class FakeSidecar : ISidecarProcess
{
    static int _pids = 4000;
    public readonly List<string> Written = new();
    public bool InputEnded, Killed, BrokenPipe;
    public int Pid { get; } = ++_pids;
    public bool HasExited { get; set; }
    public ISidecarSink Sink = null!;

    public bool TryWriteLine(string line, Action<string> onFailed, out string? reason)
    {
        if (HasExited || InputEnded) { reason = "it is not running (stopped, or restarting)"; return false; }
        if (BrokenPipe) { onFailed("EPIPE"); reason = null; return true; }
        Written.Add(line); reason = null; return true;
    }
    public void EndInput() => InputEnded = true;
    public void Kill() => Killed = true;

    /// The id of the last request for <paramref name="cmd"/>.
    public long LastId(string cmd)
    {
        for (int i = Written.Count - 1; i >= 0; i--) {
            using var d = JsonDocument.Parse(Written[i]);
            if (d.RootElement.GetProperty("cmd").GetString() == cmd) return d.RootElement.GetProperty("id").GetInt64();
        }
        throw new InvalidOperationException("no request for " + cmd);
    }
    public void Say(string json) => Sink.Stdout(this, json + "\n");
    public void Ready(bool passive = true) => Say($"{{\"ev\":\"ready\",\"pid\":{Pid},\"passive\":{(passive ? "true" : "false")},\"version\":\"0.3.0\"}}");
    public void Exit(int code) { HasExited = true; Sink.Exited(this, code); }
}

public sealed class FakeLauncher : ISidecarLauncher
{
    public readonly List<FakeSidecar> Started = new();
    public List<IReadOnlyList<string>> Args = new();
    public string? Refuse;
    public FakeSidecar Last => Started[^1];
    public ISidecarProcess Start(string exe, IReadOnlyList<string> args, ISidecarSink sink)
    {
        if (Refuse != null) throw new InvalidOperationException(Refuse);
        var p = new FakeSidecar { Sink = sink };
        Started.Add(p); Args.Add(args);
        return p;
    }
}
