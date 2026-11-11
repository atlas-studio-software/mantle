using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mantle.Core.Platform;

/// <summary>
/// The job object the app keeps its sidecar in (lane native-r7-sidecar). Windows ends every process in it
/// when the last handle to the job closes, and the app's handle closes when the app's process ends, however
/// it ends: a clean quit, a crash, Task Manager, <c>Process.Kill</c>. So the sidecar can never outlive the app.
/// <para>The flags are libuv's (the job node puts Electron's children in), so both builds behave the same:
/// KILL_ON_JOB_CLOSE ends the sidecar with the app; BREAKAWAY_OK and SILENT_BREAKAWAY_OK keep only the
/// sidecar itself in the job, never what IT starts (the taskbar guard, which must outlive it, and the
/// user's own apps launched through it); DIE_ON_UNHANDLED_EXCEPTION ends a crashed sidecar at once instead
/// of holding it in an error dialog, so the restart ladder starts on time.</para>
/// </summary>
public sealed class SidecarJob : IDisposable
{
    public const uint KillOnJobClose = 0x2000, BreakawayOk = 0x800, SilentBreakawayOk = 0x1000, DieOnUnhandledException = 0x400;
    public const uint Flags = KillOnJobClose | BreakawayOk | SilentBreakawayOk | DieOnUnhandledException;

    readonly SafeJobHandle _h;
    SidecarJob(SafeJobHandle h) { _h = h; }

    static readonly Lazy<(SidecarJob? Job, string Why)> _app = new(() => {
        var j = TryCreate(out string why);
        return (j, why);
    });

    /// <summary>The one job of this process: made on first use, never closed by hand (it closes with the
    /// process, which is the point). Null with the reason when Windows refused it.</summary>
    public static SidecarJob? ForThisProcess(out string why) { var a = _app.Value; why = a.Why; return a.Job; }

    /// <summary>A new job with <see cref="Flags"/>. Null with the reason when Windows refused it.</summary>
    public static SidecarJob? TryCreate(out string why)
    {
        var h = Win32Job.CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (h.IsInvalid) { why = $"CreateJobObject failed (error {Marshal.GetLastPInvokeError()})"; h.Dispose(); return null; }
        var info = new Win32Job.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = Flags;
        if (!Win32Job.SetInformationJobObject(h, Win32Job.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<Win32Job.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())) {
            why = $"SetInformationJobObject failed (error {Marshal.GetLastPInvokeError()})";
            h.Dispose();
            return null;
        }
        why = "ok";
        return new SidecarJob(h);
    }

    /// <summary>The limit flags Windows holds for this job (read back, not what was asked).</summary>
    public uint LimitFlags()
    {
        var info = new Win32Job.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        if (!Win32Job.QueryInformationJobObject(_h, Win32Job.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<Win32Job.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(), IntPtr.Zero))
            throw new InvalidOperationException($"QueryInformationJobObject failed (error {Marshal.GetLastPInvokeError()})");
        return info.BasicLimitInformation.LimitFlags;
    }

    /// <summary>Put a (still suspended) process in the job. False with the reason when Windows refused.</summary>
    public bool TryAdd(SafeProcessHandle process, out string why)
    {
        if (Win32Job.AssignProcessToJobObject(_h, process)) { why = "ok"; return true; }
        why = $"AssignProcessToJobObject failed (error {Marshal.GetLastPInvokeError()})";
        return false;
    }

    /// <summary>True when the process is in THIS job (IsProcessInJob).</summary>
    public bool Contains(SafeProcessHandle process) =>
        Win32Job.IsProcessInJob(process, _h, out int r) && r != 0;

    /// Closing the job ends every process still in it. The app never calls this on its own job.
    public void Dispose() => _h.Dispose();
}

/// <summary>
/// Windows command-line quoting (the rules CommandLineToArgvW and the C runtime undo), as .NET's own
/// ProcessStartInfo.ArgumentList pastes them: an argument with no space, tab or quote goes as it is;
/// otherwise it is quoted, a quote inside becomes \", and backslashes are doubled only before a quote.
/// </summary>
public static class CommandLine
{
    public static string Build(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        Append(sb, exe);
        foreach (var a in args) { sb.Append(' '); Append(sb, a); }
        return sb.ToString();
    }

    public static string Quote(string arg) { var sb = new StringBuilder(); Append(sb, arg); return sb.ToString(); }

    static void Append(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) { sb.Append(arg); return; }
        sb.Append('"');
        int i = 0;
        while (i < arg.Length) {
            int slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', slashes * 2); break; }        // before the closing quote: doubled
            if (arg[i] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }   // before a quote: doubled, then \"
            else { sb.Append('\\', slashes); sb.Append(arg[i]); }               // anywhere else: as they are
            i++;
        }
        sb.Append('"');
    }
}

/// <summary>
/// One child started with exactly three inherited handles: its stdin, stdout and stderr pipe ends
/// (PROC_THREAD_ATTRIBUTE_HANDLE_LIST). Process.Start with redirection passes bInheritHandles=TRUE with no
/// list, so the child gets EVERY inheritable handle of the app, including the app's own stdout and stderr
/// when something (a test host, a CI step) started the app with pipes; a child that outlives the app then
/// keeps that pipe open and whoever reads it never sees the end (run 43012272746 hung 80 minutes on that).
/// It is created suspended, put in the job, then resumed, so it never runs a single instruction outside it.
/// </summary>
internal sealed class ContainedChild
{
    public required SafeProcessHandle Process { get; init; }
    public required int Pid { get; init; }
    public required FileStream StdIn { get; init; }
    public required FileStream StdOut { get; init; }
    public required FileStream StdErr { get; init; }
    /// Plain words for the log: in the job, or why not.
    public required string Containment { get; init; }
    public required bool InJob { get; init; }

    public static ContainedChild Start(string exe, IReadOnlyList<string> args, string workingDirectory, SidecarJob? job, string noJobWhy)
    {
        var w = new Win32Job.Pipes();
        IntPtr attrList = IntPtr.Zero, handleArray = IntPtr.Zero, cmd = IntPtr.Zero;
        var pi = default(Win32Job.PROCESS_INFORMATION);
        bool created = false;
        try {
            w.Make();
            // only the child's ends are inheritable, and only for this CreateProcess call
            w.MarkChildEndsInheritable();

            nint size = 0;
            Win32Job.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrList = Marshal.AllocHGlobal(size);
            if (!Win32Job.InitializeProcThreadAttributeList(attrList, 1, 0, ref size)) throw Win32Job.Fail("InitializeProcThreadAttributeList");
            handleArray = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleArray, 0, w.ChildIn);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size, w.ChildOut);
            Marshal.WriteIntPtr(handleArray, IntPtr.Size * 2, w.ChildErr);
            if (!Win32Job.UpdateProcThreadAttribute(attrList, 0, Win32Job.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray, IntPtr.Size * 3, IntPtr.Zero, IntPtr.Zero))
                throw Win32Job.Fail("UpdateProcThreadAttribute(HANDLE_LIST)");

            var si = new Win32Job.STARTUPINFOEXW();
            si.StartupInfo.cb = Marshal.SizeOf<Win32Job.STARTUPINFOEXW>();
            si.StartupInfo.dwFlags = Win32Job.STARTF_USESTDHANDLES;
            si.StartupInfo.hStdInput = w.ChildIn;
            si.StartupInfo.hStdOutput = w.ChildOut;
            si.StartupInfo.hStdError = w.ChildErr;
            si.lpAttributeList = attrList;
            cmd = Marshal.StringToHGlobalUni(CommandLine.Build(exe, args));
            uint flags = Win32Job.CREATE_NO_WINDOW | Win32Job.CREATE_SUSPENDED | Win32Job.EXTENDED_STARTUPINFO_PRESENT | Win32Job.CREATE_UNICODE_ENVIRONMENT;
            if (!Win32Job.CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, workingDirectory, ref si, out pi))
                throw Win32Job.Fail("CreateProcess");
            created = true;
        } finally {
            w.CloseChildEnds();   // the child has its copies now (or there is no child)
            if (attrList != IntPtr.Zero) { Win32Job.DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleArray != IntPtr.Zero) Marshal.FreeHGlobal(handleArray);
            if (cmd != IntPtr.Zero) Marshal.FreeHGlobal(cmd);
            if (!created) w.CloseParentEnds();
        }

        var proc = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
        string containment;
        bool inJob = false;
        if (job == null) containment = $"NOT in a job of Mantle's ({noJobWhy}): if Mantle is killed, only the sidecar's own parent watch ends it";
        else if (job.TryAdd(proc, out string why)) { inJob = job.Contains(proc); containment = inJob ? "in Mantle's own job (it ends with Mantle, even on a crash or a kill)" : "assigned to Mantle's job, but Windows does not report it inside"; }
        else containment = $"NOT in Mantle's job ({why}): if Mantle is killed, only the sidecar's own parent watch ends it";
        Win32Job.ResumeThread(pi.hThread);
        Win32Job.CloseHandle(pi.hThread);
        return new ContainedChild {
            Process = proc, Pid = pi.dwProcessId, InJob = inJob, Containment = containment,
            StdIn = new FileStream(new SafeFileHandle(w.ParentIn, true), FileAccess.Write, 0),
            StdOut = new FileStream(new SafeFileHandle(w.ParentOut, true), FileAccess.Read, 0),
            StdErr = new FileStream(new SafeFileHandle(w.ParentErr, true), FileAccess.Read, 0),
        };
    }
}

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle() : base(true) { }
    protected override bool ReleaseHandle() => Win32Job.CloseHandle(handle);
}

internal static partial class Win32Job
{
    public const int JobObjectExtendedLimitInformation = 9;
    public const uint CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400, EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_NO_WINDOW = 0x08000000;
    public const int STARTF_USESTDHANDLES = 0x100;
    public const int HANDLE_FLAG_INHERIT = 1;
    public static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002;

    public static InvalidOperationException Fail(string what)
    {
        int e = Marshal.GetLastPInvokeError();
        return new($"{what} failed: {new System.ComponentModel.Win32Exception(e).Message} (error {e})");
    }

    /// The three pipes. Made NOT inheritable; only the child's ends are marked inheritable, just before
    /// CreateProcess, and closed here right after it, so another start in this process has a microseconds-wide
    /// window, not a lifetime, to pick them up.
    public sealed class Pipes
    {
        public IntPtr ChildIn, ParentIn, ParentOut, ChildOut, ParentErr, ChildErr;
        public void Make()
        {
            if (!CreatePipe(out ChildIn, out ParentIn, IntPtr.Zero, 0)) throw Fail("CreatePipe(stdin)");
            if (!CreatePipe(out ParentOut, out ChildOut, IntPtr.Zero, 0)) throw Fail("CreatePipe(stdout)");
            if (!CreatePipe(out ParentErr, out ChildErr, IntPtr.Zero, 0)) throw Fail("CreatePipe(stderr)");
        }
        public void MarkChildEndsInheritable()
        {
            foreach (var h in new[] { ChildIn, ChildOut, ChildErr })
                if (!SetHandleInformation(h, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)) throw Fail("SetHandleInformation");
        }
        public void CloseChildEnds() { Close(ref ChildIn); Close(ref ChildOut); Close(ref ChildErr); }
        public void CloseParentEnds() { Close(ref ParentIn); Close(ref ParentOut); Close(ref ParentErr); }
        static void Close(ref IntPtr h) { if (h != IntPtr.Zero) { CloseHandle(h); h = IntPtr.Zero; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IO_COUNTERS { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct STARTUPINFOEXW { public STARTUPINFOW StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeJobHandle CreateJobObjectW(IntPtr attributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeJobHandle job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle job, out int result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(IntPtr handle, int mask, int flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nint size, IntPtr previous, IntPtr returnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(IntPtr list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcessW(string application, IntPtr commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFOEXW startupInfo, out PROCESS_INFORMATION processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
