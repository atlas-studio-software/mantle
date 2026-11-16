using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Mantle.Core.Logging;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Shell.Diagnostics;

/// <summary>
/// The start-up stall meter (lane native-r8-startup): how long the UI thread is held in Mantle's first 10 s, the
/// freeze a user meets who right-clicks the desktop or uses the bar while Mantle starts. Off unless asked: a --test run
/// with MANTLE_TEST_STALL=1 (the start-up UI test). Nothing runs at rest in any run: the meter's own
/// thread ends after its 10 s window.
///
/// How: its own thread queues one empty job on the UI thread at Normal priority (behind whatever is queued, as a click's
/// work would be), waits for it to run, rests <see cref="RestMs"/> and queues the next. A job that runs
/// <see cref="SayMs"/> or more late is a stall; it is logged with the UI thread's own CPU time over it (computing, or
/// waiting on something), the named work (<see cref="UiSlice"/>) the UI thread was in while it was held, and, for a
/// wait, the thread's wait chain (what it waits on: a lock, a COM call, a message to another window). At the end it logs
/// "stall meter: the longest UI-thread stall in the first 10 s was N ms ...", which the UI test reads.
/// </summary>
internal static class StallMeter
{
    public const string TestEnv = "MANTLE_TEST_STALL";
    public const int WindowMs = 10000, RestMs = 50, SayMs = 100;

    static long _main;
    static double SinceMs(long t) => (t - _main) * 1000.0 / Stopwatch.Frequency;
    static string F(double ms) => ms.ToString("0", CultureInfo.InvariantCulture);

    /// Asked for in this run: a --test run with MANTLE_TEST_STALL=1.
    public static bool Asked => Host.Args.Test && Host.Env(TestEnv) == "1";

    /// Called first thing in OnLaunched, on the UI thread; <paramref name="mainTicks"/> is Main's Stopwatch timestamp.
    public static void Start(DispatcherQueue q, Action<string> log, long mainTicks)
    {
        if (!Asked) return;
        _main = mainTicks;
        uint tid = GetCurrentThreadId();
        IntPtr th = OpenThread(ThreadQueryLimited, false, tid);
        UiSlice.Begin(log);
        // the awaits on the UI thread (a command's answer, a delay) are named too: their continuations come through here
        if (SynchronizationContext.Current is { } inner && inner is not TimedContext)
            SynchronizationContext.SetSynchronizationContext(new TimedContext(inner));
        log($"stall meter: on ({TestEnv}=1): the UI thread is timed for {WindowMs / 1000} s from +{F(SinceMs(Stopwatch.GetTimestamp()))} ms after Main; a stall is {SayMs} ms or more");
        var t = new Thread(() => Loop(q, log, tid, th)) { IsBackground = true, Name = "stall meter" };
        t.Start();
    }

    static void Loop(DispatcherQueue q, Action<string> log, uint tid, IntPtr th)
    {
        try {
            using var ran = new ManualResetEventSlim(false);
            long ranAt = 0;
            double longest = 0, longestAt = 0, longestCpu = 0; string longestWhat = "";
            int stalls = 0, jobs = 0;
            long end = Stopwatch.GetTimestamp() + (long)(WindowMs * (double)Stopwatch.Frequency / 1000);
            while (Stopwatch.GetTimestamp() < end) {
                ran.Reset();
                long t0 = Stopwatch.GetTimestamp();
                double cpu0 = Cpu(th);
                if (!q.TryEnqueue(DispatcherQueuePriority.Normal, () => { Interlocked.Exchange(ref ranAt, Stopwatch.GetTimestamp()); ran.Set(); })) { log("stall meter: the UI thread's queue is gone; stopped"); return; }
                var seen = new List<string>();
                var chains = new List<string>();
                double nextChain = 300;
                while (!ran.Wait(20)) {
                    double late = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (late < SayMs) continue;
                    string now = UiSlice.Now ?? "";
                    if (now.Length > 0 && !seen.Contains(now)) seen.Add(now);
                    if (late >= nextChain && chains.Count < 4) {
                        nextChain = late + 700;
                        double cpuSoFar = Cpu(th) - cpu0;
                        if (cpuSoFar < late * 0.5) { string c = WaitChain(tid); if (c.Length > 0 && !chains.Contains(c)) chains.Add(c); }
                    }
                    if (late > 60000) { log("stall meter: the UI thread has been held 60 s; stopped"); return; }
                }
                long r = Interlocked.Read(ref ranAt);
                double ms = (r - t0) * 1000.0 / Stopwatch.Frequency;
                jobs++;
                double cpu = Cpu(th) - cpu0;
                string what = seen.Count == 0 ? "no named work (layout, a XAML event, a timer or an unnamed job)" : string.Join(" > ", seen);
                if (ms >= SayMs) {
                    stalls++;
                    string kind = cpu >= ms * 0.5 ? "computing" : "mostly waiting";
                    log($"stall meter: the UI thread was held {F(ms)} ms (+{F(SinceMs(t0))} to +{F(SinceMs(r))} ms after Main; its own CPU {F(cpu)} ms: {kind}); in: {what}" +
                        (chains.Count == 0 ? "" : "; wait chain: " + string.Join(" | ", chains)));
                }
                if (ms > longest) { longest = ms; longestAt = SinceMs(t0); longestCpu = cpu; longestWhat = what; }
                Thread.Sleep(RestMs);
            }
            log($"stall meter: the longest UI-thread stall in the first {WindowMs / 1000} s was {F(longest)} ms (from +{F(longestAt)} ms after Main; its own CPU {F(longestCpu)} ms" +
                (longest >= SayMs ? "; in: " + longestWhat : "") + $"); {stalls} stall(s) of {SayMs} ms or more in {jobs} queued job(s)");
        } catch (Exception e) {
            log("stall meter: it failed (" + e.GetType().Name + ": " + e.Message + "); stopped");
        } finally {
            UiSlice.End();
            if (th != IntPtr.Zero) CloseHandle(th);
        }
    }

    /// The UI thread's own CPU time so far (ms; kernel + user, in the scheduler's ticks).
    static double Cpu(IntPtr th)
    {
        if (th == IntPtr.Zero || !GetThreadTimes(th, out _, out _, out long k, out long u)) return 0;
        return (k + u) / 10000.0;
    }

    // ------------------------------------------------------------------ the wait chain (Wait Chain Traversal)

    static bool _comCallback;

    /// What the UI thread waits on now, in words ("" when it cannot be read).
    static unsafe string WaitChain(uint tid)
    {
        IntPtr s = IntPtr.Zero;
        try {
            if (!_comCallback) {
                _comCallback = true;
                IntPtr ole = LoadLibraryW("ole32.dll");
                if (ole != IntPtr.Zero) {
                    IntPtr call = GetProcAddress(ole, "CoGetCallState"), act = GetProcAddress(ole, "CoGetActivationState");
                    if (call != IntPtr.Zero && act != IntPtr.Zero) RegisterWaitChainCOMCallback(call, act);
                }
            }
            s = OpenThreadWaitChainSession(0, IntPtr.Zero);
            if (s == IntPtr.Zero) return "";
            const int Max = 16, Size = 280;
            byte* buf = stackalloc byte[Max * Size];
            uint count = Max;
            if (!GetThreadWaitChain(s, IntPtr.Zero, 7, tid, ref count, buf, out int cycle)) return "(the wait chain could not be read: error " + Marshal.GetLastPInvokeError() + ")";
            var sb = new StringBuilder();
            for (int i = 0; i < count && i < Max; i++) {
                byte* n = buf + i * Size;
                int type = *(int*)n, status = *(int*)(n + 4);
                if (sb.Length > 0) sb.Append(" -> ");
                if (type == 8) {
                    uint pid = *(uint*)(n + 8), t = *(uint*)(n + 12), wait = *(uint*)(n + 16);
                    sb.Append("thread ").Append(t).Append(pid == (uint)Environment.ProcessId ? "" : " of " + ProcName(pid)).Append(" (").Append(Status(status)).Append(", waiting ").Append(wait).Append(" ms)");
                } else {
                    string name = new string((char*)(n + 8)).Trim();
                    sb.Append(Type(type)).Append(" (").Append(Status(status)).Append(name.Length > 0 ? ": " + name : "").Append(')');
                }
            }
            if (cycle != 0) sb.Append(" (a deadlock cycle)");
            return sb.ToString();
        } catch (Exception e) {
            return "(the wait chain could not be read: " + e.Message + ")";
        } finally {
            if (s != IntPtr.Zero) CloseThreadWaitChainSession(s);
        }
    }

    static string ProcName(uint pid) { try { using var p = Process.GetProcessById((int)pid); return p.ProcessName + " (pid " + pid + ")"; } catch (Exception) { return "pid " + pid; } }

    static string Type(int t) => t switch {
        1 => "a critical section", 2 => "a SendMessage", 3 => "a mutex", 4 => "an ALPC call", 5 => "a COM call", 6 => "a thread wait",
        7 => "a process wait", 9 => "a COM activation", 10 => "an unknown wait", 11 => "socket I/O", 12 => "SMB I/O", _ => "type " + t,
    };

    static string Status(int s) => s switch {
        1 => "no access", 2 => "running", 3 => "blocked", 4 => "pid only", 5 => "pid only (rpcss)", 6 => "owned", 7 => "not owned",
        8 => "abandoned", 9 => "unknown", 10 => "error", _ => "status " + s,
    };

    const uint ThreadQueryLimited = 0x0800;
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
    [DllImport("advapi32.dll", SetLastError = true)] static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
    [DllImport("advapi32.dll")] static extern void CloseThreadWaitChainSession(IntPtr s);
    [DllImport("advapi32.dll", SetLastError = true)] static extern unsafe bool GetThreadWaitChain(IntPtr s, IntPtr context, uint flags, uint tid, ref uint count, byte* nodes, out int isCycle);
    [DllImport("advapi32.dll")] static extern void RegisterWaitChainCOMCallback(IntPtr callState, IntPtr activationState);

    /// The UI thread's synchronization context with every posted continuation run as a named slice (only while the meter
    /// is on; after it, a plain pass-through).
    sealed class TimedContext : SynchronizationContext
    {
        readonly SynchronizationContext _inner;
        public TimedContext(SynchronizationContext inner) { _inner = inner; }
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!UiSlice.On) { _inner.Post(d, state); return; }
            string who = Who(d, state);
            _inner.Post(_ => UiSlice.Run("an await's continuation", who, () => d(state)), null);
        }
        public override void Send(SendOrPostCallback d, object? state) => _inner.Send(d, state);
        public override SynchronizationContext CreateCopy() => this;

        static string Who(SendOrPostCallback d, object? state)
        {
            try {
                object? target = state is Delegate sd ? sd.Target : d.Target;
                return target?.GetType().ToString() ?? "a static method";
            } catch (Exception) { return "?"; }
        }
    }
}
