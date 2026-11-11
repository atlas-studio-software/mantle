using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mantle.UiTests;

/// <summary>
/// What one process does while it rests, thread by thread, read from outside (the test process), so the app needs no
/// probe of its own. Per thread over a window: CPU time as Windows charges it (whole 15.6 ms clock ticks, the unit of
/// <see cref="Process.TotalProcessorTime"/>), CPU cycles (exact: QueryThreadCycleTime, shown as ms at this machine's
/// cycle rate), context switches (how many times the thread woke), its name (Thread.Name reaches the OS as the thread
/// description) and the module its start address is in. Threads that start and end inside the window are counted in
/// the process line only.
/// </summary>
internal static class ThreadCpu
{
    public sealed class T
    {
        public int Tid; public string Name = "", Start = "", Wait = "";
        public long Ticks100ns; public ulong Cycles; public uint Switches;
    }

    public sealed class Snap
    {
        public long At = Stopwatch.GetTimestamp();
        public long ProcTicks100ns; public ulong ProcCycles;
        public Dictionary<int, T> Threads = new();
    }

    public sealed record Row(int Tid, string Name, string Start, double TickMs, double CycleMs, uint Switches, string Wait);

    public sealed class Result
    {
        public double WallMs, ProcTickMs, ProcCycleMs;
        public int ThreadsBefore, ThreadsAfter, Started, Ended;
        public List<Row> Rows = new();
        public double TickPct => Math.Round(100 * ProcTickMs / WallMs, 3);
        public double CyclePct => Math.Round(100 * ProcCycleMs / WallMs, 3);
        public long Switches => Rows.Sum(r => (long)r.Switches);

        /// The busiest threads first (by cycles, then wakeups), one line each.
        public string Table(int top = 14)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"  process: {TickPct} % of one core charged ({ProcTickMs:0.#} ms in {WallMs / 1000:0.#} s), {CyclePct} % by cycles ({ProcCycleMs:0.##} ms); {Switches} wakeups; threads {ThreadsBefore} -> {ThreadsAfter} ({Started} started, {Ended} ended)");
            sb.AppendLine("  tid    | cycle ms | tick ms | wakeups | wakeups/s | name | start | waiting on");
            foreach (var r in Rows.OrderByDescending(r => r.CycleMs).ThenByDescending(r => r.Switches).Take(top))
                sb.AppendLine($"  {r.Tid,-6} | {r.CycleMs,8:0.00} | {r.TickMs,7:0.#} | {r.Switches,7} | {r.Switches * 1000.0 / WallMs,9:0.0} | {(r.Name.Length > 0 ? r.Name : "-")} | {r.Start} | {r.Wait}");
            int rest = Rows.Count - Math.Min(top, Rows.Count);
            if (rest > 0) sb.AppendLine($"  ... {rest} more thread(s): {Rows.OrderByDescending(r => r.CycleMs).Skip(top).Sum(r => r.CycleMs):0.00} cycle ms, {Rows.OrderByDescending(r => r.CycleMs).Skip(top).Sum(r => (long)r.Switches)} wakeups");
            return sb.ToString().TrimEnd();
        }
    }

    /// Measures `pid` over `ms`: a snapshot, a sleep, a snapshot.
    public static Result Measure(int pid, int ms)
    {
        var a = Take(pid);
        Thread.Sleep(ms);
        var b = Take(pid);
        return Diff(a, b);
    }

    public static Result Diff(Snap a, Snap b)
    {
        double perMs = CyclesPerMs();
        var r = new Result {
            WallMs = (b.At - a.At) * 1000.0 / Stopwatch.Frequency,
            ProcTickMs = (b.ProcTicks100ns - a.ProcTicks100ns) / 10000.0,
            ProcCycleMs = (b.ProcCycles - a.ProcCycles) / perMs,
            ThreadsBefore = a.Threads.Count, ThreadsAfter = b.Threads.Count,
            Started = b.Threads.Keys.Count(k => !a.Threads.ContainsKey(k)),
            Ended = a.Threads.Keys.Count(k => !b.Threads.ContainsKey(k)),
        };
        foreach (var (tid, t1) in b.Threads)
        {
            a.Threads.TryGetValue(tid, out var t0);
            double tick = (t1.Ticks100ns - (t0?.Ticks100ns ?? 0)) / 10000.0;
            double cyc = (t1.Cycles - (t0?.Cycles ?? 0)) / perMs;
            uint sw = t1.Switches - (t0?.Switches ?? 0);
            string name = t1.Name.Length > 0 ? t1.Name : t0?.Name ?? "";
            r.Rows.Add(new Row(tid, (t0 == null ? "(new) " : "") + name, t1.Start, Math.Round(tick, 1), Math.Round(cyc, 3), sw, t1.Wait));
        }
        return r;
    }

    // ---------------------------------------------------------------------------------------- one snapshot
    public static Snap Take(int pid)
    {
        var s = new Snap();
        var mods = Modules(pid);
        byte[] buf = Query();
        s.At = Stopwatch.GetTimestamp();
        int off = 0;
        while (true)
        {
            int next = BitConverter.ToInt32(buf, off);
            long epid = BitConverter.ToInt64(buf, off + 80);
            if (epid == pid)
            {
                int n = BitConverter.ToInt32(buf, off + 4);
                s.ProcCycles = BitConverter.ToUInt64(buf, off + 24);
                s.ProcTicks100ns = BitConverter.ToInt64(buf, off + 40) + BitConverter.ToInt64(buf, off + 48);
                for (int i = 0; i < n; i++)
                {
                    int t = off + 256 + i * 80;   // SYSTEM_THREAD_INFORMATION, 80 bytes on x64
                    var th = new T {
                        Ticks100ns = BitConverter.ToInt64(buf, t) + BitConverter.ToInt64(buf, t + 8),
                        Tid = (int)BitConverter.ToInt64(buf, t + 48),
                        Switches = BitConverter.ToUInt32(buf, t + 64),
                    };
                    int state = BitConverter.ToInt32(buf, t + 68), why = BitConverter.ToInt32(buf, t + 72);
                    th.Wait = state == 5 ? WaitName(why) : "state " + state;
                    Detail(th, mods);
                    s.Threads[th.Tid] = th;
                }
                break;
            }
            if (next == 0) break;
            off += next;
        }
        return s;
    }

    /// KWAIT_REASON: Sleep is DelayExecution, a message wait WrUserRequest, an I/O completion port WrQueue.
    static string WaitName(int w) => w switch {
        0 => "Executive", 4 => "DelayExecution (Sleep)", 5 => "Suspended", 6 => "UserRequest", 7 => "WrExecutive",
        11 => "WrDelayExecution", 13 => "WrUserRequest (messages)", 15 => "WrQueue (completion port)", 16 => "WrLpcReceive",
        17 => "WrLpcReply", 21 => "WrKeyedEvent", 37 => "WrAlertByThreadId", _ => "wait reason " + w,
    };

    static void Detail(T th, List<(long Base, long End, string? Name)> mods)
    {
        IntPtr h = OpenThread(0x0800 | 0x0040, false, (uint)th.Tid);   // QUERY_LIMITED_INFORMATION | QUERY_INFORMATION
        if (h == IntPtr.Zero) h = OpenThread(0x0800, false, (uint)th.Tid);
        if (h == IntPtr.Zero) { th.Start = "(no access)"; return; }
        try
        {
            if (QueryThreadCycleTime(h, out ulong c)) th.Cycles = c;
            if (GetThreadDescription(h, out IntPtr d) >= 0 && d != IntPtr.Zero) { th.Name = Marshal.PtrToStringUni(d) ?? ""; LocalFree(d); }
            if (NtQueryInformationThread(h, 9, out IntPtr start, IntPtr.Size, out _) == 0)
            {
                long a = start.ToInt64();
                var m = mods.FirstOrDefault(x => a >= x.Base && a < x.End);
                th.Start = m.Name != null ? $"{m.Name}+0x{a - m.Base:x}" : $"0x{a:x}";
            }
        }
        finally { CloseHandle(h); }
    }

    static List<(long Base, long End, string? Name)> Modules(int pid)
    {
        var l = new List<(long Base, long End, string? Name)>();
        try
        {
            using var p = Process.GetProcessById(pid);
            foreach (ProcessModule m in p.Modules) l.Add((m.BaseAddress.ToInt64(), m.BaseAddress.ToInt64() + m.ModuleMemorySize, m.ModuleName ?? "?"));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        return l;
    }

    static byte[] Query()
    {
        int size = 1 << 20;
        for (int i = 0; i < 8; i++)
        {
            var buf = new byte[size];
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                uint st = NtQuerySystemInformation(5, pin.AddrOfPinnedObject(), size, out int need);
                if (st == 0) return buf;
                if (st != 0xC0000004) throw new InvalidOperationException($"NtQuerySystemInformation: 0x{st:x8}");
                size = Math.Max(size * 2, need + (1 << 16));
            }
            finally { pin.Free(); }
        }
        throw new InvalidOperationException("NtQuerySystemInformation: the process list kept growing");
    }

    static double _cyclesPerMs;
    /// This machine's cycle counter rate (QueryThreadCycleTime counts at it), from 120 ms of spinning on this thread.
    public static double CyclesPerMs()
    {
        if (_cyclesPerMs > 0) return _cyclesPerMs;
        double best = 0;
        for (int i = 0; i < 3; i++)
        {
            IntPtr me = GetCurrentThread();
            QueryThreadCycleTime(me, out ulong c0);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 40) { }
            QueryThreadCycleTime(me, out ulong c1);
            best = Math.Max(best, (c1 - c0) / sw.Elapsed.TotalMilliseconds);   // the run least interrupted
        }
        return _cyclesPerMs = best;
    }

    [DllImport("ntdll.dll")] static extern uint NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationThread(IntPtr h, int cls, out IntPtr info, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr h, out ulong cycles);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern int GetThreadDescription(IntPtr h, out IntPtr desc);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
}
