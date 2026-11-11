using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// "Sign in with Google": OAuth 2.0 for installed apps (lib/features/_gcal-auth.js).
///   1. a one-shot HTTP listener on 127.0.0.1, on a free port the OS picks (never a fixed one);
///   2. PKCE S256 + a random state;
///   3. the consent page opens in the user's OWN default browser (<see cref="GcalAuthOptions.OpenExternal"/>), never a web view;
///   4. the browser comes back to http://127.0.0.1:&lt;port&gt;/?code=..&amp;state=..; a wrong or missing state is refused; the
///      listener closes after the first real answer or after 5 minutes;
///   5. the code + verifier are exchanged for tokens (<see cref="IGcalGoogle.ExchangeAsync"/>).
/// The listener answers only 127.0.0.1, serves one tiny self-contained page, and logs nothing from the query string.
/// </summary>
public static class GcalAuth
{
    public const int WaitMs = 5 * 60 * 1000;

    public static string Page(string title, string text)
    {
        static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>" + Esc(title) + "</title>" +
            "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">" +
            "<style>body{font:15px system-ui,sans-serif;background:#f3f3f3;color:#1b1b1b;display:grid;place-items:center;height:100vh;margin:0}" +
            "main{background:#fff;border-radius:12px;padding:28px 32px;box-shadow:0 1px 3px rgba(0,0,0,.12);max-width:420px}" +
            "h1{font-size:18px;margin:0 0 8px}p{margin:0;color:#555}@media (prefers-color-scheme:dark){body{background:#1c1c1c;color:#eee}main{background:#2a2a2a;box-shadow:none}p{color:#bbb}}</style>" +
            "</head><body><main><h1>" + Esc(title) + "</h1><p>" + Esc(text) + "</p></main></body></html>";
    }

    /// <summary>Runs one sign-in; the token on success, else a <see cref="GcalError"/> whose Reason is plain words.</summary>
    public static async Task<GcalToken> SignInAsync(GcalAuthOptions o)
    {
        if (o.Client == null || string.IsNullOrEmpty(o.Client.ClientId)) throw new GcalError("other", "choose your Google client file first");
        var pk = GcalCore.Pkce(o.RandomBytes);
        string state = GcalCore.NewState(o.RandomBytes);
        TcpListener listener;
        try { listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); }
        catch (SocketException e) { throw new GcalError("other", "the sign-in listener on this PC could not start (" + GcalCore.Cut(e.SocketErrorCode.ToString(), 60) + ")"); }
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = "http://127.0.0.1:" + port + "/";
        o.Listening?.Invoke(port);
        var done = new TaskCompletionSource<GcalToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(o.WaitMs ?? WaitMs);
        using var reg = timeout.Token.Register(() => done.TrySetException(new GcalError("other", "the sign-in was not finished within 5 minutes")));
        int exchanging = 0;
        var serving = new List<Task>();
        var loop = Task.Run(async () => {
            while (!done.Task.IsCompleted) {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false); }
                catch (Exception) { return; }
                lock (serving) serving.Add(Serve(c));
            }
        });
        string url;
        try { url = GcalCore.AuthUrl(o.Client.ClientId, redirectUri, pk.Challenge, state); }
        catch (GcalError e) { listener.Stop(); throw new GcalError("other", e.Reason ?? e.Message); }
        try { if (o.OpenExternal(url) is { } why) done.TrySetException(new GcalError("other", "your browser could not be opened (" + GcalCore.Cut(why, 60) + ")")); }
        catch (Exception e) { done.TrySetException(new GcalError("other", "your browser could not be opened (" + GcalCore.Cut(e.Message, 60) + ")")); }
        try { return await done.Task.ConfigureAwait(true); }
        finally {
            try { listener.Stop(); } catch (Exception) { }
            Task[] left; lock (serving) left = serving.ToArray();
            try { await Task.WhenAll(left).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true); } catch (Exception) { }
        }

        async Task Serve(TcpClient c)
        {
            using (c) {
                try {
                    var s = c.GetStream();
                    s.ReadTimeout = 5000;
                    string? target = await ReadTarget(s).ConfigureAwait(false);
                    if (target == null) { await Send(s, 400, null, null).ConfigureAwait(false); return; }
                    if (!target.StartsWith("GET ", StringComparison.Ordinal)) { await Send(s, 404, null, null).ConfigureAwait(false); return; }
                    Dictionary<string, string> q;
                    try { q = Query(target[4..]); } catch (Exception) { await Send(s, 400, null, null).ConfigureAwait(false); return; }
                    if (!q.ContainsKey("code") && !q.ContainsKey("error")) { await Send(s, 404, null, null).ConfigureAwait(false); return; }   // a favicon or a stray probe
                    if (!GcalCore.StateMatches(state, q.GetValueOrDefault("state") ?? "")) {
                        await Send(s, 400, "Sign-in not accepted", "This answer did not come from the sign-in Mantle started. Start again from Mantle Settings.").ConfigureAwait(false);
                        done.TrySetException(new GcalError("other", "the answer from the browser did not match the sign-in Mantle started, so it was refused"));
                        return;
                    }
                    if (q.TryGetValue("error", out var err)) {
                        await Send(s, 200, "Not connected", "Mantle was not given access. You can close this tab.").ConfigureAwait(false);
                        done.TrySetException(new GcalError("other", err == "access_denied" ? "you said no on Google's page, so Mantle is not connected" : "Google stopped the sign-in (" + GcalCore.Cut(err, 40) + ")"));
                        return;
                    }
                    if (Interlocked.Exchange(ref exchanging, 1) == 1) { await Send(s, 409, "Already signing in", "Mantle is already finishing this sign-in. You can close this tab.").ConfigureAwait(false); return; }
                    async Task No(string reason)
                    {
                        await Send(s, 200, "Not connected", "Mantle could not finish connecting: " + reason + ".").ConfigureAwait(false);
                        done.TrySetException(new GcalError("other", reason));
                    }
                    GcalToken tok;
                    try { tok = await o.Google.ExchangeAsync(o.Client, q["code"], pk.Verifier, redirectUri).ConfigureAwait(false); }
                    catch (Exception e) { await No(GcalCore.Classify(e).Reason).ConfigureAwait(false); return; }
                    if (!tok.Scopes.Contains(GcalCore.Scope)) { await No("Google did not give Mantle read access to your calendar; sign in again and tick the calendar box").ConfigureAwait(false); return; }
                    if (string.IsNullOrEmpty(tok.Refresh)) { await No("Google did not send a lasting sign-in; sign in again").ConfigureAwait(false); return; }
                    await Send(s, 200, "Mantle is connected", "Mantle can now show your Google Calendar. You can close this tab.").ConfigureAwait(false);
                    done.TrySetResult(tok);
                } catch (Exception) { /* a socket the browser dropped: the sign-in still waits for a real answer */ }
            }
        }
    }

    /// The request line's method and target, from at most 8 KB of headers; null when it is not HTTP.
    static async Task<string?> ReadTarget(NetworkStream s)
    {
        var buf = new byte[8192]; int n = 0;
        using var cts = new CancellationTokenSource(5000);
        while (n < buf.Length) {
            int r = await s.ReadAsync(buf.AsMemory(n), cts.Token).ConfigureAwait(false);
            if (r <= 0) break;
            n += r;
            if (Encoding.ASCII.GetString(buf, 0, n).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        string head = Encoding.ASCII.GetString(buf, 0, n);
        int eol = head.IndexOf("\r\n", StringComparison.Ordinal);
        if (eol <= 0) return null;
        var parts = head[..eol].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !parts[1].StartsWith('/')) return null;
        return parts[0] + " " + parts[1];
    }

    static Dictionary<string, string> Query(string target)
    {
        var u = new Uri("http://127.0.0.1" + target);
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        string qs = u.Query.TrimStart('?');
        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            int eq = pair.IndexOf('=');
            string k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            q.TryAdd(k, v);
        }
        return q;
    }

    static async Task Send(NetworkStream s, int code, string? title, string? text)
    {
        string reason = code switch { 200 => "OK", 400 => "Bad Request", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        byte[] body = title == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(Page(title, text ?? ""));
        string head = "HTTP/1.1 " + code + " " + reason + "\r\n" +
            (title == null ? "" : "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\n") +
            "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        if (body.Length > 0) await s.WriteAsync(body).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }
}

/// <summary>What one sign-in needs. OpenExternal opens the URL in the user's own browser and answers null, or why it could not.</summary>
public sealed class GcalAuthOptions
{
    public required GcalClient Client { get; init; }
    public required IGcalGoogle Google { get; init; }
    public required Func<string, string?> OpenExternal { get; init; }
    public Func<int, byte[]>? RandomBytes { get; init; }
    public int? WaitMs { get; init; }
    /// The port the listener got (tests read it; nothing else).
    public Action<int>? Listening { get; init; }
}
