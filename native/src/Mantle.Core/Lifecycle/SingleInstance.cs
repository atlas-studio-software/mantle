using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core;

/// <summary>
/// The single-instance key. Electron keys its lock on the userData folder (main.js: "A test instance
/// gets its own userData directory, which is ALSO what keys Electron's single-instance lock"), so
/// native does the same with its own prefix: one MantleNative per data root, a --test profile never
/// meets the user's instance, and no name can ever match Electron's own lock.
/// </summary>
public static class InstanceKey
{
    public const string Prefix = "MantleNative-";

    /// "MantleNative-" + the first 16 hex digits of SHA-256 over the root's full path, upper-cased,
    /// without a trailing separator ("C:\X\" and "c:/x" are the same root).
    public static string For(string dataRoot)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).Replace('/', '\\').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full));
        return Prefix + Convert.ToHexString(hash, 0, 8);
    }

    /// The named mutex: per logon session, like Chromium's process singleton.
    public static string MutexName(string key) => @"Local\" + key;

    /// The hand-off pipe. Pipe names are machine-wide, so the session id keeps two logon sessions apart.
    public static string PipeName(string key, int sessionId) => key + "-s" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

[JsonSerializable(typeof(string[]))]
internal sealed partial class HandoffJson : JsonSerializerContext { }

/// <summary>
/// The claim on one data root, held for the life of the process. The first instance owns it and
/// listens on a named pipe; a second launch on the same root sends its arguments there (the way
/// Electron's 'second-instance' event hands argv to the running app) and exits.
///
/// The mutex has thread affinity: claim and dispose it on the same thread (Main's).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipe;
    CancellationTokenSource? _stop;
    Task? _loop;
    bool _disposed;

    public string Key { get; }
    /// Set when the previous owner died holding the claim (an abandoned mutex): said in the log.
    public string? Note { get; }

    SingleInstance(Mutex m, string key, string? note)
    {
        _mutex = m; Key = key; Note = note;
        _pipe = InstanceKey.PipeName(key, System.Diagnostics.Process.GetCurrentProcess().SessionId);
    }

    /// Take the claim. Null when another live instance holds it (reason says so).
    public static SingleInstance? TryClaim(string key, out string reason)
    {
        var m = new Mutex(false, InstanceKey.MutexName(key));
        try {
            if (m.WaitOne(0)) { reason = "claimed " + key; return new SingleInstance(m, key, null); }
        } catch (AbandonedMutexException) {
            reason = "claimed " + key + " (the previous instance ended without letting go of it)";
            return new SingleInstance(m, key, "the previous instance on this data root ended without releasing its claim");
        }
        m.Dispose();
        reason = "another MantleNative holds " + key;
        return null;
    }

    /// Start listening for hand-offs. onArgs runs on a thread-pool thread; onError gets every
    /// listener failure in words (never swallowed).
    public void Listen(Action<string[]> onArgs, Action<string> onError)
    {
        if (_loop != null) return;
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loop = Task.Run(async () => {
            int failures = 0;
            while (!ct.IsCancellationRequested) {
                NamedPipeServerStream? server = null;
                try {
                    server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    string[]? argv = await ReadMessage(server, ct).ConfigureAwait(false);
                    if (argv != null) {
                        server.WriteByte(1);   // the ack: the sender may now say "handed off" and exit
                        server.Flush();
                        try { onArgs(argv); } catch (Exception e) { onError("a hand-off could not be handled: " + e.Message); }
                    } else onError("a hand-off message could not be read (empty, too long or not a list of arguments)");
                    failures = 0;
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception e) {
                    failures++;
                    onError($"the hand-off pipe failed ({e.GetType().Name}: {e.Message}); retrying in {Math.Min(failures, 10)} s");
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(failures, 10)), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                } finally {
                    server?.Dispose();
                }
            }
        }, ct);
    }

    static async Task<string[]?> ReadMessage(Stream s, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        if (!await ReadExactly(s, lenBuf, ct).ConfigureAwait(false)) return null;
        int len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > 64 * 1024) return null;
        var body = new byte[len];
        if (!await ReadExactly(s, body, ct).ConfigureAwait(false)) return null;
        try { return JsonSerializer.Deserialize(body, HandoffJson.Default.StringArray); } catch (JsonException) { return null; }
    }

    static async Task<bool> ReadExactly(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length) {
            int n = await s.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// From a second launch: give the owner of this key our arguments. True once the owner acked.
    public static bool SendToOwner(string key, IReadOnlyList<string> argv, TimeSpan timeout, out string reason)
    {
        string pipe = InstanceKey.PipeName(key, System.Diagnostics.Process.GetCurrentProcess().SessionId);
        try {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new List<string>(argv).ToArray(), HandoffJson.Default.StringArray);
            client.Write(BitConverter.GetBytes(body.Length));
            client.Write(body);
            client.Flush();
            int ack = client.ReadByte();
            if (ack != 1) { reason = "the running instance took the connection but did not confirm (" + (ack < 0 ? "closed" : "answer " + ack) + ")"; return false; }
            reason = "handed " + argv.Count + " argument(s) to the running instance";
            return true;
        } catch (TimeoutException) {
            reason = $"the running instance did not answer on its pipe within {timeout.TotalSeconds:0.#} s";
            return false;
        } catch (Exception e) {
            reason = $"the hand-off failed ({e.GetType().Name}: {e.Message})";
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stop?.Cancel(); } catch (ObjectDisposedException) { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        _stop?.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread: the OS releases it at exit */ }
        _mutex.Dispose();
    }
}
