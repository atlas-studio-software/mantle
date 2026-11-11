using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Mantle.UiTests;

/// <summary>
/// Every process the UI tests start, and everything those start (each app's Mantle.Sidecar), ends with the test
/// host. The host puts itself in a job object with KILL_ON_JOB_CLOSE before the first test; children join it at
/// creation, and Windows ends whatever is left when the host's handle closes at exit. Nothing is found by name or
/// by parent PID: the job holds the exact set.
/// <para>Why (lane native-r7-topbar, run 43012272746): all 49 tests had finished at 11:37 but <c>dotnet test</c> never
/// returned. The job's cleanup at 12:49 found one live Mantle.Sidecar and no app, test host or dotnet. Test apps are
/// started without redirection, so they inherit the test run's output pipe as an inheritable handle; the app starts
/// its sidecar with Process.Start (bInheritHandles=TRUE), so the sidecar holds that pipe too, and ci.ps1's
/// <c>&amp; dotnet ... *&gt;&gt; $log</c> waits for an end of output that never came.</para>
/// <para>At exit it writes <c>leftover-processes.txt</c> to the UI out folder: whatever was still running in the job
/// 3 s after the last test (name, PID, start time), which is the evidence for the process that would have hung it.</para>
/// </summary>
internal static class TestRunJob
{
    static IntPtr _job;
    static string _how = "not set up";

    [ModuleInitializer]
    internal static void Start()
    {
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { _how = "CreateJobObject failed: " + Marshal.GetLastWin32Error(); return; }
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            { _how = "SetInformationJobObject failed: " + Marshal.GetLastWin32Error(); CloseHandle(job); return; }
            if (!AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
            { _how = "AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error() + " (the host's own job may forbid nesting)"; CloseHandle(job); return; }
            _job = job;   // never closed by hand: the handle closes with this process, which ends the rest
            _how = "the test host and everything it starts are in a kill-on-close job";
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Report();
        }
        catch (Exception e) { _how = "job setup threw: " + e.Message; }
    }

    static void Report()
    {
        try
        {
            var lines = new List<string> { "test run job: " + _how };
            int self = Environment.ProcessId;
            var left = Members().Where(p => p != self).ToList();
            // first a note of what is running at once (the host may be ended before the wait is over)
            File.WriteAllLines(Path.Combine(Host.OutDir, "leftover-processes.txt"),
                lines.Append($"at the host's exit: {left.Count} other process(es) in the job: {string.Join(", ", left.Select(Describe))}"));
            var until = DateTime.UtcNow.AddSeconds(3);
            while (left.Count > 0 && DateTime.UtcNow < until) { Thread.Sleep(200); left = Members().Where(p => p != self).ToList(); }
            lines.Add(left.Count == 0
                ? "nothing outlived the tests by 3 s"
                : left.Count + " process(es) still running 3 s after the tests (Windows ends them as this host exits):");
            foreach (var pid in left) lines.Add("  " + Describe(pid));
            File.WriteAllLines(Path.Combine(Host.OutDir, "leftover-processes.txt"), lines);
        }
        catch (Exception) { /* the job still ends them; this is only the note */ }
    }

    static string Describe(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return $"{p.ProcessName} pid {pid}, started {p.StartTime.ToUniversalTime():HH:mm:ss.fff} UTC"; }
        catch (Exception e) { return $"pid {pid} ({e.Message})"; }
    }

    static List<int> Members()
    {
        var r = new List<int>();
        if (_job == IntPtr.Zero) return r;
        int size = 8 + 8 * 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_job, 3, buf, (uint)size, out _)) return r;   // JobObjectBasicProcessIdList
            int n = Marshal.ReadInt32(buf, 4);
            for (int i = 0; i < n; i++) r.Add((int)Marshal.ReadInt64(buf, 8 + 8 * i));
        }
        finally { Marshal.FreeHGlobal(buf); }
        return r;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr info, uint size, out uint returned);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}
