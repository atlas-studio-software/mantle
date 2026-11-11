using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Xunit;

namespace Mantle.Core.Tests.Platform;

// Alone: an inheritable probe handle is open during these tests, and another test's Process.Start running at
// the same moment would take a copy of it (bInheritHandles=TRUE) and make the inheritance check fail for nothing.
[CollectionDefinition(SidecarJobCollection.Name, DisableParallelization = true)]
public sealed class SidecarJobCollection { public const string Name = "sidecar job (no other process starts meanwhile)"; }

/// <summary>
/// Lane native-r7-sidecar: the real launcher puts every sidecar in a kill-on-close job and hands it only its
/// three pipes. The children here are PING.EXE (a console program that runs ~30 s and prints), started and
/// ended by these tests only, by their own PID or by closing the test's own job.
/// </summary>
[Collection(SidecarJobCollection.Name)]
public class SidecarJobTests
{
    static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
    static readonly string[] PingArgs = { "-n", "30", "127.0.0.1" };

    sealed class Sink : ISidecarSink
    {
        readonly StringBuilder _out = new();
        public readonly ManualResetEventSlim Ended = new();
        public int? Code;
        public string Out { get { lock (_out) return _out.ToString(); } }
        public void Stdout(ISidecarProcess from, string chunk) { lock (_out) _out.Append(chunk); }
        public void Stderr(ISidecarProcess from, string chunk) { }
        public void Exited(ISidecarProcess from, int? code) { Code = code; Ended.Set(); }
    }

    static bool Until(Func<bool> f, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (f()) return true; Thread.Sleep(50); }
        return f();
    }

    static void KillOwn(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: false); p.WaitForExit(5000); } catch (Exception) { /* gone */ }
    }

    [Fact]
    public void The_job_holds_libuvs_flags_kill_on_close_and_silent_breakaway_for_what_the_sidecar_starts()
    {
        using var job = SidecarJob.TryCreate(out string why);
        Assert.True(job != null, why);
        uint f = job!.LimitFlags();
        Assert.Equal(SidecarJob.Flags, f);
        Assert.True((f & SidecarJob.KillOnJobClose) != 0);
        Assert.True((f & SidecarJob.SilentBreakawayOk) != 0, "without silent breakaway the taskbar guard and the user's launched apps would die with Mantle");
    }

    [Fact]
    public void The_apps_job_is_made_once_and_is_the_one_the_default_launcher_uses()
    {
        using var loop = new HostLoop("sidecar job test");
        var a = SidecarJob.ForThisProcess(out string why);
        Assert.True(a != null, why);
        Assert.Same(a, SidecarJob.ForThisProcess(out _));
        Assert.Same(a, new ProcessSidecarLauncher(loop).Job);
    }

    [Fact]
    public void A_started_sidecar_is_in_the_job_talks_on_its_pipes_a_restart_joins_the_same_job_and_closing_the_job_ends_both()
    {
        using var loop = new HostLoop("sidecar job test");
        var job = SidecarJob.TryCreate(out string why);
        Assert.True(job != null, why);
        var launcher = new ProcessSidecarLauncher(loop, job);
        var s1 = new Sink(); var s2 = new Sink();
        ISidecarProcess? p1 = null, p2 = null;
        try {
            p1 = launcher.Start(Ping, PingArgs, s1);
            Assert.Contains("in Mantle's own job", p1.Containment);
            Assert.True(Until(() => s1.Out.Contains("127.0.0.1"), 10000), "nothing came on stdout: " + s1.Out);
            p2 = launcher.Start(Ping, PingArgs, s2);   // what a restart does: the same launcher, so the same job
            Assert.Contains("in Mantle's own job", p2.Containment);
            using (var a = Process.GetProcessById(p1.Pid)) Assert.True(job!.Contains(a.SafeHandle), "the first child is not in the job");
            using (var b = Process.GetProcessById(p2.Pid)) Assert.True(job!.Contains(b.SafeHandle), "the restarted child is not in the job");
            Assert.False(p1.HasExited);

            job!.Dispose();   // what Windows does when the app's process ends, however it ends
            Assert.True(s1.Ended.Wait(5000), "closing the job did not end the first child");
            Assert.True(s2.Ended.Wait(5000), "closing the job did not end the restarted child");
            Assert.True(p1.HasExited && p2.HasExited);
        } finally {
            if (p1 != null) KillOwn(p1.Pid);
            if (p2 != null) KillOwn(p2.Pid);
            job?.Dispose();
        }
    }

    [Fact]
    public void Kill_ends_only_that_child_and_its_exit_arrives()
    {
        using var loop = new HostLoop("sidecar job test");
        using var job = SidecarJob.TryCreate(out string why);
        var launcher = new ProcessSidecarLauncher(loop, job);
        var s = new Sink();
        var p = launcher.Start(Ping, PingArgs, s);
        try {
            Assert.True(Until(() => s.Out.Length > 0, 10000));
            p.Kill();
            Assert.True(s.Ended.Wait(5000), "Kill did not end it");
            Assert.True(p.HasExited);
            Assert.NotEqual(0, s.Code);
            Assert.False(p.TryWriteLine("{}", _ => { }, out string? reason));
            Assert.NotNull(reason);
        } finally { KillOwn(p.Pid); }
    }

    [Fact]
    public void A_started_sidecar_inherits_none_of_the_apps_handles()
    {
        using var loop = new HostLoop("sidecar job test");
        using var job = SidecarJob.TryCreate(out _);
        var launcher = new ProcessSidecarLauncher(loop, job);
        // an inheritable handle of "the app", like the stdout pipe a test host or CI step gives it
        using var probe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var s = new Sink();
        var p = launcher.Start(Ping, PingArgs, s);
        try {
            Assert.True(Until(() => s.Out.Length > 0, 10000));
            probe.DisposeLocalCopyOfClientHandle();
            // the write end is now held only by whoever inherited it: nobody, so the read ends at once
            var read = Task.Run(() => probe.Read(new byte[1], 0, 1));
            Assert.True(read.Wait(3000), "the child holds a copy of the app's inheritable handle (the read never ended)");
            Assert.Equal(0, read.Result);
            Assert.False(p.HasExited, "the child ended early, so the check above proved nothing");
        } finally { KillOwn(p.Pid); }
    }

    [Fact]
    public void Control_Process_Start_with_redirection_does_hand_the_child_the_apps_handles()
    {
        // the check above is not empty: the old launcher's way (Process.Start, bInheritHandles=TRUE, no list) fails it
        using var probe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var psi = new ProcessStartInfo(Ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        foreach (var a in PingArgs) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        Task<int>? read = null;
        try {
            probe.DisposeLocalCopyOfClientHandle();
            read = Task.Run(() => probe.Read(new byte[1], 0, 1));
            Assert.False(read.Wait(1500), "the control child did not inherit the probe, so the inheritance test cannot tell");
        } finally {
            try { p.Kill(entireProcessTree: false); p.WaitForExit(5000); } catch (Exception) { }
        }
        Assert.True(read!.Wait(5000), "the read did not end once the control child was gone");
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("--parent-pid=1234")]
    [InlineData("with space")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData(@"C:\a dir\trailing\")]
    [InlineData(@"C:\no-space\trailing\")]
    [InlineData(@"two\\""quotes")]
    [InlineData("tab\there")]
    [InlineData(@"--state-file=C:\Users\x y\AppData\Roaming\mantle\state.json")]
    public void Command_lines_come_back_whole_through_CommandLineToArgvW(string arg)
    {
        string exe = @"C:\Program Files\Mantle\Mantle.Sidecar.exe";
        var args = new[] { "--first", arg, "last" };
        string line = CommandLine.Build(exe, args);
        var back = Split(line);
        Assert.Equal(new[] { exe, "--first", arg, "last" }, back);
    }

    [Fact]
    public void A_plain_argument_is_not_quoted()
    {
        Assert.Equal("--passive", CommandLine.Quote("--passive"));
        Assert.Equal("\"\"", CommandLine.Quote(""));
        Assert.Equal("\"a b\"", CommandLine.Quote("a b"));
        Assert.Equal(@"""a b\\""", CommandLine.Quote(@"a b\"));      // a backslash before the closing quote is doubled
        Assert.Equal(@"C:\x\y", CommandLine.Quote(@"C:\x\y"));       // elsewhere backslashes stay as they are
        Assert.Equal(@"""say \""hi\""""", CommandLine.Quote("say \"hi\""));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    static string[] Split(string line)
    {
        IntPtr argv = CommandLineToArgvW(line, out int argc);
        Assert.NotEqual(IntPtr.Zero, argv);
        try {
            var r = new List<string>();
            for (int i = 0; i < argc; i++) r.Add(Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!);
            return r.ToArray();
        } finally { LocalFree(argv); }
    }
}
