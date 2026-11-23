using System;
using System.Threading;

namespace Mantle.Core.Features;

/// <summary>
/// Timers for host-thread code: every callback runs on the host thread (the SynchronizationContext
/// captured when the clock was made), never on a timer thread, so the feature host keeps Node's
/// one-thing-at-a-time semantics. Time comes from a <see cref="TimeProvider"/>, so tests move it by hand.
/// </summary>
public sealed class HostClock
{
    readonly SynchronizationContext? _host;

    /// Callbacks go to <paramref name="host"/>, or to the context current now (construct it on the host thread).
    public HostClock(TimeProvider? time = null, SynchronizationContext? host = null)
        : this(time, host ?? SynchronizationContext.Current, true) { }

    HostClock(TimeProvider? time, SynchronizationContext? host, bool _)
    {
        Time = time ?? TimeProvider.System;
        _host = host;
    }

    /// A clock whose callbacks run inline on the timer's own thread (single-threaded tests, tools).
    public static HostClock Inline(TimeProvider? time = null) => new(time, null, true);

    public TimeProvider Time { get; }

    /// Milliseconds on a monotonic clock (for "took N ms" and "back after N min").
    public long NowMs => (long)Time.GetElapsedTime(0, Time.GetTimestamp()).TotalMilliseconds;

    /// Run <paramref name="action"/> on the host thread now (or inline when there is no host context).
    public void Post(Action action)
    {
        if (_host == null || SynchronizationContext.Current == _host) { action(); return; }
        _host.Post(static s => ((Action)s!)(), action);
    }

    /// <summary>Run <paramref name="action"/> once, after <paramref name="due"/>, on the host thread.
    /// Disposing the handle before it fires cancels it.</summary>
    public IDisposable Later(TimeSpan due, Action action)
    {
        var once = new Once(action, this);
        once.Timer = Time.CreateTimer(static s => ((Once)s!).Fire(), once, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
        return once;
    }

    sealed class Once : IDisposable
    {
        readonly Action _action;
        readonly HostClock _clock;
        int _state;   // 0 armed, 1 fired or cancelled
        public ITimer? Timer;

        public Once(Action action, HostClock clock) { _action = action; _clock = clock; }

        public void Fire()
        {
            if (Interlocked.Exchange(ref _state, 1) != 0) return;
            Timer?.Dispose();
            Mantle.Core.Lifecycle.IdleTally.Hit("clock later", _action);
            _clock.Post(_action);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 1) != 0) return;
            Timer?.Dispose();
        }
    }
}
