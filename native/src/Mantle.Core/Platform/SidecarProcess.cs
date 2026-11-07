using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
}

/// Starts sidecar processes (a fake one in tests).
public interface ISidecarLauncher
{
    /// Start <paramref name="exe"/>; throws with plain words when it cannot.
    ISidecarProcess Start(string exe, IReadOnlyList<string> args, ISidecarSink sink);
}

/// <summary>
/// The real launcher: Mantle.Sidecar.exe with redirected stdio, no console window. Reader threads post
/// every piece to the host thread; a writer thread drains stdin so the host never blocks on a full pipe
/// (lib/sidecar.js writes asynchronously for the same reason).
/// </summary>
public sealed class ProcessSidecarLauncher : ISidecarLauncher
{
    readonly IHostScheduler _host;
    public ProcessSidecarLauncher(IHostScheduler host) { _host = host; }

    public ISidecarProcess Start(string exe, IReadOnlyList<string> args, ISidecarSink sink)
    {
        var psi = new ProcessStartInfo(exe) {
            UseShellExecute = false,
            CreateNoWindow = true,               // never flash a console over the user's screen
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),   // a BOM would corrupt the first request (SC-001)
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try {
            p = Process.Start(psi) ?? throw new InvalidOperationException("Windows did not start it");
        } catch (System.ComponentModel.Win32Exception e) {
            throw new InvalidOperationException(e.Message, e);
        }
        var r = new Running(p, _host, sink);
        r.Begin();
        return r;
    }

    sealed class Running : ISidecarProcess
    {
        readonly Process _p;
        readonly IHostScheduler _host;
        readonly ISidecarSink _sink;
        readonly BlockingCollection<(string Line, Action<string> Failed)> _out = new();
        volatile bool _inputEnded;
        volatile bool _exited;

        public Running(Process p, IHostScheduler host, ISidecarSink sink) { _p = p; _host = host; _sink = sink; Pid = p.Id; }
        public int Pid { get; }
        public bool HasExited => _exited;

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
                while ((n = _p.StandardOutput.Read(buf, 0, buf.Length)) > 0) {
                    string chunk = new string(buf, 0, n);
                    _host.Post(() => _sink.Stdout(this, chunk));
                }
            } catch (Exception) { /* the pipe broke: the exit below says what happened */ }
            int? code = null;
            try { _p.WaitForExit(); code = _p.ExitCode; } catch (Exception) { code = null; }
            _exited = true;
            _out.CompleteAdding();
            _host.Post(() => _sink.Exited(this, code));
        }

        void ReadErr()
        {
            var buf = new char[4096];
            try {
                int n;
                while ((n = _p.StandardError.Read(buf, 0, buf.Length)) > 0) {
                    string chunk = new string(buf, 0, n);
                    _host.Post(() => _sink.Stderr(this, chunk));
                }
            } catch (Exception) { }
        }

        void WriteIn()
        {
            var w = _p.StandardInput;
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
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: false); } catch (Exception) { /* gone already, or refused: the caller checks */ }
        }
    }
}
