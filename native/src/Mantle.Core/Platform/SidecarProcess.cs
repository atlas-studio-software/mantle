using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Mantle.Core.Lifecycle;

namespace Mantle.Core.Platform;

/// <summary>What a started sidecar process tells its owner. Every call arrives on the host thread.</summary>
public interface ISidecarSink
{
    /// A piece of stdout, in order (not split on lines: the client does that, as lib/sidecar.js does).
    void Stdout(ISidecarProcess from, string chunk);
    /// A piece of stderr, in order.
    void Stderr(ISidecarProcess from, string chunk);
    /// The process ended. Arrives after the last Stdout piece. Null code: the exit code could not be read.
    void Exited(ISidecarProcess from, int? code);
}

/// <summary>One running sidecar as the client sees it.</summary>
public interface ISidecarProcess
{
    int Pid { get; }
    bool HasExited { get; }
    /// <summary>Queue one line for stdin. False (with the reason) when stdin is closed or the process is gone.</summary>
    /// <param name="onFailed">The write failed later (a dead pipe): called on the host thread with the reason.</param>
    bool TryWriteLine(string line, Action<string> onFailed, out string? reason);
    /// End stdin: the sidecar's own restore runs and it exits by itself.
    void EndInput();
    /// End the process (never its tree: the taskbar guard it started must outlive it).
    void Kill();
    /// Where it runs, in words for the log: in the app's own kill-on-close job, or why not. Empty: not said.
    string Containment => "";
}

/// Starts sidecar processes (a fake one in tests).
public interface ISidecarLauncher
{
    /// Start <paramref name="exe"/>; throws with plain words when it cannot.
    ISidecarProcess Start(string exe, IReadOnlyList<string> args, ISidecarSink sink);
}

/// <summary>
/// The real launcher: Mantle.Sidecar.exe with redirected stdio, no console window, inside the app's own
/// kill-on-close job (<see cref="SidecarJob"/>) and with ONLY its three pipe ends inherited
/// (<see cref="ContainedChild"/>): it ends with the app however the app ends, and it holds nothing of the
/// app's but its own pipes. Every restart goes through here, so a restarted sidecar joins the same job.
/// Reader threads post every piece to the host thread; a writer thread drains stdin so the host never
/// blocks on a full pipe (lib/sidecar.js writes asynchronously for the same reason).
/// </summary>
public sealed class ProcessSidecarLauncher : ISidecarLauncher
{
    readonly IHostScheduler _host;
    readonly SidecarJob? _job;
    readonly string _noJobWhy;

    /// The app's launcher: the process-wide job (<see cref="SidecarJob.ForThisProcess"/>).
    public ProcessSidecarLauncher(IHostScheduler host) { _host = host; _job = SidecarJob.ForThisProcess(out _noJobWhy); }
    /// A launcher over a given job (tests: a job they can close). Null: no job, said in each start's line.
    public ProcessSidecarLauncher(IHostScheduler host, SidecarJob? job, string noJobWhy = "no job was given") { _host = host; _job = job; _noJobWhy = noJobWhy; }

    /// The job every sidecar from this launcher is put in (null when Windows refused to make it).
    public SidecarJob? Job => _job;

    public ISidecarProcess Start(string exe, IReadOnlyList<string> args, ISidecarSink sink)
    {
        ContainedChild c;
        try {
            c = ContainedChild.Start(exe, args, Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory, _job, _noJobWhy);
        } catch (InvalidOperationException) {
            throw;   // already in plain words (which call failed, Windows' reason, the error number)
        } catch (Exception e) {
            throw new InvalidOperationException(e.Message, e);
        }
        var r = new Running(c, _host, sink);
        r.Begin();
        return r;
    }

    sealed class Running : ISidecarProcess
    {
        static readonly UTF8Encoding Utf8 = new(false);   // no BOM: a BOM would corrupt the first request (SC-001)
        readonly ContainedChild _c;
        readonly IHostScheduler _host;
        readonly ISidecarSink _sink;
        readonly BlockingCollection<(string Line, Action<string> Failed)> _out = new();
        volatile bool _inputEnded;
        volatile bool _exited;

        public Running(ContainedChild c, IHostScheduler host, ISidecarSink sink) { _c = c; _host = host; _sink = sink; Pid = c.Pid; }
        public int Pid { get; }
        public bool HasExited => _exited;
        public string Containment => _c.Containment;

        public void Begin()
        {
            new Thread(ReadErr) { IsBackground = true, Name = "sidecar stderr" }.Start();
            new Thread(ReadOut) { IsBackground = true, Name = "sidecar stdout" }.Start();
            new Thread(WriteIn) { IsBackground = true, Name = "sidecar stdin" }.Start();
        }

        void ReadOut()
        {
            var buf = new char[16384];
            try {
                int n;
                using var r = new StreamReader(_c.StdOut, Utf8, detectEncodingFromByteOrderMarks: false);
                while ((n = r.Read(buf, 0, buf.Length)) > 0) {
                    string chunk = new string(buf, 0, n);
                    _host.Post(() => _sink.Stdout(this, chunk));
                }
            } catch (Exception) { /* the pipe broke: the exit below says what happened */ }
            int? code = null;
            try {
                if (Win32Job.WaitForSingleObject(_c.Process, 0xFFFFFFFF) == 0 && Win32Job.GetExitCodeProcess(_c.Process, out uint x)) code = unchecked((int)x);
            } catch (Exception) { code = null; }
            _exited = true;
            _out.CompleteAdding();
            _host.Post(() => _sink.Exited(this, code));
        }

        void ReadErr()
        {
            var buf = new char[4096];
            try {
                int n;
                using var r = new StreamReader(_c.StdErr, Utf8, detectEncodingFromByteOrderMarks: false);
                while ((n = r.Read(buf, 0, buf.Length)) > 0) {
                    string chunk = new string(buf, 0, n);
                    _host.Post(() => _sink.Stderr(this, chunk));
                }
            } catch (Exception) { }
        }

        void WriteIn()
        {
            var w = new StreamWriter(_c.StdIn, Utf8);
            try {
                foreach (var (line, failed) in _out.GetConsumingEnumerable()) {
                    if (line.Length == 0) { try { w.Close(); } catch (Exception) { } _inputEnded = true; break; }
                    try { w.Write(line); w.Write('\n'); w.Flush(); }
                    catch (Exception e) {
                        _inputEnded = true;
                        string why = e.Message;
                        _host.Post(() => failed(why));
                    }
                }
            } catch (InvalidOperationException) { }
        }

        public bool TryWriteLine(string line, Action<string> onFailed, out string? reason)
        {
            if (_exited || _inputEnded || _out.IsAddingCompleted) { reason = "it is not running (stopped, or restarting)"; return false; }
            try { _out.Add((line, onFailed)); reason = null; return true; }
            catch (InvalidOperationException) { reason = "it is not running (stopped, or restarting)"; return false; }
        }

        public void EndInput()
        {
            try { if (!_out.IsAddingCompleted) { _out.Add(("", _ => { })); _out.CompleteAdding(); } } catch (InvalidOperationException) { }
        }

        public void Kill()
        {
            // TerminateProcess on this one process (never its tree), as Process.Kill(false) did
            try { if (!_exited) Win32Job.TerminateProcess(_c.Process, unchecked((uint)-1)); } catch (Exception) { /* gone already, or refused: the caller checks */ }
        }
    }
}
