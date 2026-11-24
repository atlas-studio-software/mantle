using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// The REAL reads the feature hands to the pure core (NOT unit-tested: tests inject fakes and never touch a port):
/// the link's loopback GET (_autopause-link.js httpGet: 127.0.0.1 only, no Origin, no proxy, one request per connection,
/// a 1 MB answer cap, never throws) and the coexistence reads (_autopause-coexist.js: the task file, the read-only
/// `schtasks.exe /query` fallback with a 4 s timeout and no window, never PowerShell, and the 700 ms /ping with
/// X-Mantle: coexist). Nothing here can start, stop, enable or edit the helper or its task.
/// </summary>
public static class AutoPauseNet
{
    public const int MaxAnswer = 1048576;

    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.Zero,
        ConnectTimeout = TimeSpan.FromSeconds(4),
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>One loopback GET. Never throws: {code, json} or {0, error}.</summary>
    public static async Task<LinkReply> Get(int port, string route, int timeoutMs, CancellationToken cancel)
    {
        return await Fetch(port, route, timeoutMs, cancel, null).ConfigureAwait(false);
    }

    static async Task<LinkReply> Fetch(int port, string route, int timeoutMs, CancellationToken cancel, (string Name, string Value)? header)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(Math.Max(1, timeoutMs));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{port}{route}"));
            req.Headers.ConnectionClose = true;
            if (header is { } h) req.Headers.TryAddWithoutValidation(h.Name, h.Value);
            using var res = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await using var body = await res.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                buf.Write(chunk, 0, n);
                if (buf.Length > MaxAnswer) return LinkReply.Failed("too big");
            }
            JsonObject? json = null;
            try { json = JsonNode.Parse(Encoding.UTF8.GetString(buf.ToArray())) as JsonObject; } catch (Exception) { json = null; }
            return new LinkReply((int)res.StatusCode, json);
        }
        catch (OperationCanceledException) { return LinkReply.Failed(cancel.IsCancellationRequested ? "closed" : "timeout"); }
        catch (HttpRequestException e) { return LinkReply.Failed(e.HttpRequestError == HttpRequestError.ConnectionError ? "ECONNREFUSED" : "error"); }
        catch (Exception) { return LinkReply.Failed("unreachable"); }
    }

    /// <summary>The coexistence reads over the real machine (AUTO-086/087). Read-only.</summary>
    public static AutoPauseCoexist.Reads Reads() => new(
        () => File.ReadAllBytes(AutoPauseCoexist.TaskFile(Environment.GetEnvironmentVariable("SystemRoot"))),
        Schtasks,
        async port =>
        {
            var r = await Fetch(port, "/ping", AutoPauseCoexist.PingTimeoutMs, CancellationToken.None, ("X-Mantle", "coexist")).ConfigureAwait(false);
            return r.Json;
        });

    /// <summary>`schtasks.exe /query /tn SpotifyAutoPause /fo csv /nh`: a plain exe, a read-only query, no window, 4 s.</summary>
    public static async Task<(bool Failed, string? Output, string? Error)> Schtasks()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SystemRoot");
            var exe = Path.Combine(string.IsNullOrEmpty(root) ? "C:\\Windows" : root, "System32", "schtasks.exe");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in AutoPauseCoexist.SchtasksArgs) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return (true, null, "schtasks did not start");
            var outT = p.StandardOutput.ReadToEndAsync();
            var errT = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(AutoPauseCoexist.SchtasksTimeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch (Exception) { }
                return (true, null, "schtasks did not answer in 4 s");
            }
            var output = await outT.ConfigureAwait(false);
            var error = await errT.ConfigureAwait(false);
            return (p.ExitCode != 0, output, error);
        }
        catch (Exception e) { return (true, null, e.GetType().Name); }
    }
}
