using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// The minimal HTTP/1.1 the auto-pause API needs, PURE: read a request head, write an answer. One request per connection: the
/// Electron host answers every request with <c>Connection: close</c>, and so does this.
/// </summary>
public static class AutoPauseHttp
{
    /// Node's default header cap (16 KB); a longer head gets 431.
    public const int MaxHead = 16384;

    static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A parsed request head. <c>Headers</c> by lower-case name: every value in order (a header sent twice has two).</summary>
    public sealed record Head(string Method, string Target, IReadOnlyDictionary<string, List<string>> Headers)
    {
        /// The header's value (two sent are joined with ", ", as Node does for most headers); null when absent.
        public string? Get(string name) => Headers.TryGetValue(name, out var v) ? string.Join(", ", v) : null;
        public bool Twice(string name) => Headers.TryGetValue(name, out var v) && v.Count > 1;
    }

    /// <summary>Parse the head (everything before the blank line, without it). Null + words when it is not HTTP/1.x.</summary>
    public static Head? ParseHead(string head, out string? error)
    {
        error = null;
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[0].Length == 0 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) { error = "not an HTTP/1.x request line"; return null; }
        if (first[0].Any(c => c < 'A' || c > 'Z')) { error = "the method is not a word in capitals"; return null; }
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0) continue;
            int c = l.IndexOf(':');
            if (c <= 0 || l.Substring(0, c).Any(ch => ch <= ' ' || ch >= 127)) { error = "a header line without a name"; return null; }
            var name = l.Substring(0, c).ToLowerInvariant();
            var value = l.Substring(c + 1).Trim(' ', '\t');
            if (!headers.TryGetValue(name, out var list)) headers[name] = list = new List<string>();
            list.Add(value);
        }
        return new Head(first[0], first[1], headers);
    }

    /// <summary>The body's length: 0 when none is sent; -1 when it cannot be read here (chunked, or a Content-Length that is not
    /// one plain number).</summary>
    public static long BodyLength(Head h)
    {
        if (h.Headers.ContainsKey("transfer-encoding")) return -1;
        if (!h.Headers.TryGetValue("content-length", out var v)) return 0;
        if (v.Distinct().Count() != 1) return -1;
        return long.TryParse(v[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1;
    }

    static string Reason(int code) => code switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
        413 => "Payload Too Large", 431 => "Request Header Fields Too Large", 500 => "Internal Server Error", _ => "Status",
    };

    /// <summary>The answer's bytes: status line, the answer's headers, Content-Length, then the JSON (UTF-8, as JSON.stringify writes it).</summary>
    public static byte[] Write(AutoPauseAnswer a)
    {
        var body = a.Json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(a.Json.ToJsonString(JsonOpts));
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(a.Code).Append(' ').Append(Reason(a.Code)).Append("\r\n");
        foreach (var kv in a.Headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        if (a.Json != null) sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var all = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, all, 0, head.Length);
        Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
        return all;
    }

    /// A plain answer for a request that never reached the host (a head that could not be read).
    public static AutoPauseAnswer Plain(int code, string reason) => new(code, new System.Text.Json.Nodes.JsonObject { ["ok"] = false, ["reason"] = reason },
        new List<KeyValuePair<string, string>> { new("Content-Type", "application/json"), new("Cache-Control", "no-store"), new("Connection", "close") });
}

/// <summary>
/// The thin loopback server: a TcpListener on 127.0.0.1 ONLY (IPAddress.Loopback, never another interface, so no URL ACL and
/// no firewall prompt), exclusive (binding is the election's lock), one request per connection. It reads, hands the request to
/// <see cref="AutoPauseHost.Handle"/> on the host thread, and writes the answer. While a /watch waits it watches the socket:
/// the client closing it is <c>gone</c>. Not unit-tested (socket code); the host's decisions are.
/// </summary>
public sealed class AutoPauseServer : IDisposable
{
    const int ReadTimeoutMs = 10000;
    readonly TcpListener _listener;
    readonly AutoPauseHost _host;
    readonly HostClock _clock;
    readonly Action<string> _log;
    readonly CancellationTokenSource _stop = new();
    readonly HashSet<TcpClient> _open = new();

    AutoPauseServer(TcpListener l, AutoPauseHost host, HostClock clock, Action<string> log) { _listener = l; _host = host; _clock = clock; _log = log; }

    /// <summary>Bind 127.0.0.1:<paramref name="port"/> exclusively; null when it is taken (or cannot be bound, logged).</summary>
    public static IDisposable? TryStart(int port, AutoPauseHost host, HostClock clock, Action<string> log)
    {
        var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { l.Start(); }
        catch (SocketException e)
        {
            if (e.SocketErrorCode != SocketError.AddressAlreadyInUse && e.SocketErrorCode != SocketError.AccessDenied) log($"127.0.0.1:{port} could not be bound ({e.SocketErrorCode})");
            try { l.Stop(); } catch (SocketException) { /* never bound */ }
            return null;
        }
        var s = new AutoPauseServer(l, host, clock, log);
        _ = Task.Run(s.AcceptLoop);
        return s;
    }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) { if (_stop.IsCancellationRequested) return; _log($"the bridge-compatible API stopped accepting: {e.SocketErrorCode}"); return; }
            lock (_open) _open.Add(c);
            _ = Task.Run(() => Serve(c));
        }
    }

    async Task Serve(TcpClient c)
    {
        try
        {
            using var _c = c;
            var stream = c.GetStream();
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            using var readCut = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            readCut.CancelAfter(ReadTimeoutMs);
            var (head, rest) = await ReadHead(stream, readCut.Token).ConfigureAwait(false);
            if (head == null) { await Send(stream, AutoPauseHttp.Plain(431, "request head too big or cut off")).ConfigureAwait(false); return; }
            var h = AutoPauseHttp.ParseHead(head, out var err);
            if (h == null) { await Send(stream, AutoPauseHttp.Plain(400, err ?? "bad request")).ConfigureAwait(false); return; }
            long len = AutoPauseHttp.BodyLength(h);
            bool tooBig = len < 0 || len > AutoPauseGate.MaxBody;   // chunked or junk: refused as too big, never read
            string body = "";
            if (!tooBig && len > 0)
            {
                var buf = new byte[len];
                int have = Math.Min(rest.Length, (int)len);
                Buffer.BlockCopy(rest, 0, buf, 0, have);
                while (have < len)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(have, (int)len - have), readCut.Token).ConfigureAwait(false);
                    if (n == 0) return;
                    have += n;
                }
                body = Encoding.UTF8.GetString(buf);
            }
            var req = new AutoPauseRequest(h.Method, h.Target, h.Get, body, tooBig, h.Twice);
            // The client closing its socket while we hold the answer (a /watch) = it has gone.
            _ = WatchClose(stream, gone);
            var answer = await OnHost(req, gone.Token).ConfigureAwait(false);
            if (answer != null) await Send(stream, answer).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* timed out or stopping: the socket closes */ }
        catch (IOException) { /* the client went */ }
        catch (SocketException) { /* the client went */ }
        catch (ObjectDisposedException) { /* stopping */ }
        catch (Exception e) { _log($"a bridge-compatible API connection threw: {e.GetType().Name}: {e.Message}"); }
        finally { lock (_open) _open.Remove(c); }
    }

    Task<AutoPauseAnswer?> OnHost(AutoPauseRequest req, CancellationToken gone)
    {
        var done = new TaskCompletionSource<AutoPauseAnswer?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _clock.Post(() =>
        {
            Task<AutoPauseAnswer?> t;
            try { t = _host.Handle(req, gone); }
            catch (Exception e) { _log($"a compatible-API request threw: {e.GetType().Name}: {e.Message}"); done.TrySetResult(AutoPauseHttp.Plain(500, "mantle could not do that")); return; }
            t.ContinueWith(x => { if (x.IsFaulted) done.TrySetResult(AutoPauseHttp.Plain(500, "mantle could not do that")); else done.TrySetResult(x.Result); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        });
        return done.Task;
    }

    static async Task WatchClose(NetworkStream s, CancellationTokenSource gone)
    {
        var one = new byte[1];
        try { while (await s.ReadAsync(one, gone.Token).ConfigureAwait(false) > 0) { /* a client sends nothing more: ignore */ } }
        catch (Exception) { /* closed, reset or stopping */ }
        try { gone.Cancel(); } catch (ObjectDisposedException) { /* already done */ }
    }

    static async Task<(string? Head, byte[] Tail)> ReadHead(NetworkStream s, CancellationToken cut)
    {
        var buf = new byte[AutoPauseHttp.MaxHead];
        int have = 0;
        while (have < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(have), cut).ConfigureAwait(false);
            if (n == 0) return (null, Array.Empty<byte>());
            int from = Math.Max(0, have - 3);
            have += n;
            for (int i = from; i + 3 < have; i++)
                if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    return (Encoding.Latin1.GetString(buf, 0, i), buf.AsSpan(i + 4, have - i - 4).ToArray());
        }
        return (null, Array.Empty<byte>());
    }

    static async Task Send(NetworkStream s, AutoPauseAnswer a)
    {
        var bytes = AutoPauseHttp.Write(a);
        await s.WriteAsync(bytes).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { return; }
        try { _listener.Stop(); } catch (SocketException) { /* gone */ }
        TcpClient[] open; lock (_open) open = _open.ToArray();
        foreach (var c in open) { try { c.Close(); } catch (Exception) { /* gone */ } }
    }
}
