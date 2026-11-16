// IdleCmp.cs - the measuring side of run.ps1 (loaded with Add-Type). It only READS other processes: CPU cycles,
// charged CPU time, memory and their top-level windows. It never stops, signals or changes a process.
//
// CPU: QueryProcessCycleTime (the exact cycles every thread of the process ran, the same counter the native idle
// test's ThreadCpu.cs reads per thread) over a window of wall time, for a fixed set of process handles opened at the
// window's start. A handle keeps the process object, so a process that exits mid-window still reports its cycles,
// and a reused PID can never be read by mistake. Cycles are turned into ms at this machine's cycle rate
// (CyclesPerMs: spinning on this thread against the Stopwatch) and then into % of one core.
// The charged CPU time (GetProcessTimes kernel + user, Windows' 15.6 ms tick accounting) is kept beside it, for
// reference only: on the runner it over- and under-counts short timer-driven wakes (native/docs/lanes/idle.md).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class IdleCmp
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_VM_READ = 0x0010;
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr h, out ulong cycles);
    [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr h, out ulong cycles);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("psapi.dll")] static extern bool GetProcessMemoryInfo(IntPtr h, out PMC c, int cb);

    [StructLayout(LayoutKind.Sequential)]
    struct PMC
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    static double _perMs;
    /// This machine's cycle counter rate in cycles per ms: the least interrupted of 5 spins of 40 ms.
    public static double CyclesPerMs(bool again)
    {
        if (_perMs > 0 && !again) return _perMs;
        double best = 0;
        for (int i = 0; i < 5; i++)
        {
            IntPtr me = GetCurrentThread();
            ulong c0, c1;
            QueryThreadCycleTime(me, out c0);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 40) { }
            QueryThreadCycleTime(me, out c1);
            best = Math.Max(best, (c1 - c0) / sw.Elapsed.TotalMilliseconds);
        }
        if (_perMs <= 0) _perMs = best;
        return best;
    }

    public sealed class Proc
    {
        public int Pid; public string Name = ""; public string Role = "";
        public IntPtr H; public ulong C0, C1; public long T0, T1;
        public double CycleMs, ChargedMs, PrivateMB, WorkingSetMB; public bool Alive, Opened;
    }

    public sealed class Window
    {
        public List<Proc> Procs = new List<Proc>();
        public Stopwatch Sw = new Stopwatch();
        public double WallMs, CycleMs, ChargedMs, CyclePct, ChargedPct, PrivateMB, WorkingSetMB, PerMs;
        public int Opened, AliveAtEnd;
    }

    static long Charged(IntPtr h)
    {
        long c, e, k, u;
        return GetProcessTimes(h, out c, out e, out k, out u) ? k + u : 0;
    }

    /// Opens every PID (query only) and takes the starting cycles and charged time. names/roles are for the report.
    public static Window Begin(int[] pids, string[] names, string[] roles)
    {
        var w = new Window();
        w.PerMs = CyclesPerMs(false);
        for (int i = 0; i < pids.Length; i++)
        {
            var p = new Proc { Pid = pids[i], Name = names[i], Role = roles[i] };
            p.H = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pids[i]);
            if (p.H == IntPtr.Zero) p.H = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pids[i]);
            p.Opened = p.H != IntPtr.Zero;
            if (p.Opened) { QueryProcessCycleTime(p.H, out p.C0); p.T0 = Charged(p.H); w.Opened++; }
            w.Procs.Add(p);
        }
        w.Sw.Start();
        return w;
    }

    /// Takes the ending cycles, charged time and memory, closes the handles and sums.
    public static Window End(Window w)
    {
        w.Sw.Stop();
        w.WallMs = w.Sw.Elapsed.TotalMilliseconds;
        foreach (var p in w.Procs)
        {
            if (!p.Opened) continue;
            QueryProcessCycleTime(p.H, out p.C1); p.T1 = Charged(p.H);
            uint code; p.Alive = GetExitCodeProcess(p.H, out code) && code == 259;   // STILL_ACTIVE
            if (p.Alive)
            {
                PMC m;
                if (GetProcessMemoryInfo(p.H, out m, Marshal.SizeOf(typeof(PMC))))
                {
                    p.PrivateMB = m.PrivateUsage.ToUInt64() / 1048576.0;
                    p.WorkingSetMB = m.WorkingSetSize.ToUInt64() / 1048576.0;
                }
                w.AliveAtEnd++;
            }
            CloseHandle(p.H); p.H = IntPtr.Zero;
            p.CycleMs = (p.C1 - p.C0) / w.PerMs;
            p.ChargedMs = (p.T1 - p.T0) / 10000.0;
            w.CycleMs += p.CycleMs; w.ChargedMs += p.ChargedMs; w.PrivateMB += p.PrivateMB; w.WorkingSetMB += p.WorkingSetMB;
        }
        w.CyclePct = Math.Round(100 * w.CycleMs / w.WallMs, 3);
        w.ChargedPct = Math.Round(100 * w.ChargedMs / w.WallMs, 3);
        return w;
    }

    /// One line per process of a finished window, the busiest first.
    public static string Rows(Window w)
    {
        var sb = new StringBuilder();
        var list = new List<Proc>(w.Procs);
        list.Sort((a, b) => b.CycleMs.CompareTo(a.CycleMs));
        foreach (var p in list)
            sb.AppendLine(string.Format("    pid {0,-6} {1,-22} {2,-28} cycles {3,8:0.00} ms ({4,6:0.000} %)  charged {5,7:0.0} ms  private {6,7:0.0} MB  ws {7,7:0.0} MB{8}",
                p.Pid, p.Name, p.Role.Length > 28 ? p.Role.Substring(0, 28) : p.Role, p.CycleMs, 100 * p.CycleMs / w.WallMs, p.ChargedMs,
                p.PrivateMB, p.WorkingSetMB, !p.Opened ? "  NOT OPENED" : p.Alive ? "" : "  exited in the window"));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ windows of a set of processes (read only)
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, char[] b, int n);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int cb);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public sealed class Win { public long Hwnd; public int Pid; public string Title = "", Class = ""; public RECT R; public bool Cloaked; }

    /// Every visible top-level window owned by one of pids.
    public static Win[] WindowsOf(int[] pids)
    {
        var set = new HashSet<int>(pids ?? new int[0]);
        var list = new List<Win>();
        EnumWindows((h, l) =>
        {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!set.Contains((int)pid) || !IsWindowVisible(h)) return true;
            var t = new char[256]; int n = GetWindowTextW(h, t, t.Length);
            var c = new char[256]; int m = GetClassNameW(h, c, c.Length);
            RECT r; GetWindowRect(h, out r);
            int cl; bool cloaked = DwmGetWindowAttribute(h, 14, out cl, 4) == 0 && cl != 0;   // DWMWA_CLOAKED
            list.Add(new Win { Hwnd = h.ToInt64(), Pid = (int)pid, Title = new string(t, 0, n), Class = new string(c, 0, m), R = r, Cloaked = cloaked });
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    /// { idle, busy } in 100 ns units summed over all cores (kernel time includes idle, so busy = kernel + user - idle):
    /// how busy the whole runner was during a window, to show the noise floor beside an app's number.
    public static long[] SystemTimes()
    {
        long i, k, u;
        GetSystemTimes(out i, out k, out u);
        return new long[] { i, k + u - i };
    }

    /// "pid <pid> <title>" of the foreground window, for the log.
    public static string Foreground()
    {
        IntPtr h = GetForegroundWindow(); uint pid; GetWindowThreadProcessId(h, out pid);
        var t = new char[256]; int n = GetWindowTextW(h, t, t.Length);
        return string.Format("pid {0} \"{1}\"", pid, new string(t, 0, n));
    }
}
