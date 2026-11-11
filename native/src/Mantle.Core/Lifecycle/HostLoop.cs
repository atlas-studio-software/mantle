using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Lifecycle;

/// <summary>
/// The host thread's clock and queue (PLAN.md A6): Node's event-loop semantics for the ported main.js
/// logic. Everything posted runs on ONE thread, in order, so the host code needs no locks.
/// </summary>
public interface IHostScheduler
{
    /// Run <paramref name="work"/> on the host thread, after what is already queued.
    void Post(Action work);
    /// Run <paramref name="work"/> on the host thread once <paramref name="after"/> has passed (setTimeout).
    /// Disposing the handle cancels it if it has not run yet.
    IDisposable Delay(TimeSpan after, Action work);
    /// Milliseconds on a monotonic clock (Date.now() differences in the JS).
    double NowMs { get; }
    /// True on the host thread.
    bool IsHostThread { get; }
}

/// <summary>
/// The real host thread: a dedicated background thread running a single-threaded
/// SynchronizationContext, so an <c>await</c> in host code resumes on it. A handler that throws is
/// reported through <see cref="OnError"/> (the crash path decides what that means), never swallowed.
/// </summary>
public sealed class HostLoop : SynchronizationContext, IHostScheduler, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _queue = new();
    readonly Thread _thread;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    volatile bool _stopped;

    /// Every exception a posted item threw (on the host thread).
    public event Action<Exception>? OnError;

    public HostLoop(string name = "mantle host")
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public bool IsHostThread => Thread.CurrentThread == _thread;
    public int ManagedThreadId => _thread.ManagedThreadId;

    void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (cb, state) in _queue.GetConsumingEnumerable()) {
            try { cb(state); }
            catch (Exception e) {
                try { OnError?.Invoke(e); } catch { /* the error reporter itself failed: nothing left to tell */ }
            }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (_stopped) return;
        IdleTally.Hit("host post", state ?? d);   // test runs only (the idle probe); off it is one read
        try { _queue.Add((d, state)); } catch (InvalidOperationException) { /* completed: the loop has stopped */ }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (IsHostThread) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? err = null;
        Post(_ => { try { d(state); } catch (Exception e) { err = e; } finally { done.Set(); } }, null);
        done.Wait();
        if (err != null) throw new InvalidOperationException("a host-thread call failed: " + err.Message, err);
    }

    public override SynchronizationContext CreateCopy() => this;

    public void Post(Action work) => Post(static s => ((Action)s!)(), work);   // the work rides as the state, so the idle tally can name it

    public IDisposable Delay(TimeSpan after, Action work)
    {
        var d = new Delayed(this, work);
        long ms = (long)Math.Max(0, Math.Min(after.TotalMilliseconds, int.MaxValue));
        d.Timer = new Timer(static s => ((Delayed)s!).Fire(), d, ms, Timeout.Infinite);
        return d;
    }

    /// Run <paramref name="work"/> on the host thread and wait for it (for code that is not on it).
    public T Invoke<T>(Func<T> work)
    {
        if (IsHostThread) return work();
        T result = default!;
        Send(_ => result = work(), null);
        return result;
    }

    /// Run an async host-thread job and get its task (it starts on the host thread).
    public Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () => {
            try { tcs.SetResult(await work()); } catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        _queue.CompleteAdding();
        if (!IsHostThread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    sealed class Delayed : IDisposable
    {
        readonly HostLoop _loop;
        readonly Action _work;
        int _state;   // 0 waiting, 1 fired or cancelled
        public Timer? Timer;
        public Delayed(HostLoop loop, Action work) { _loop = loop; _work = work; }
        public void Fire()
        {
            if (Interlocked.Exchange(ref _state, 1) != 0) return;
            Timer?.Dispose();
            IdleTally.Hit("host delay", _work);
            _loop.Post(_work);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _state, 1) != 0) return;
            Timer?.Dispose();
        }
    }
}
