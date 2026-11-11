using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Mantle.Core.Features.Calendar;
using Xunit;

namespace Mantle.Core.Tests.Calendar;

/// lib/features/_gcal-auth.js, ported: the 127.0.0.1 listener on an OS-picked port. The "browser" here is an
/// HttpClient on loopback that plays Google's redirect back; the code exchange is a fake. No browser opens, no
/// request leaves this PC.
public class GcalAuthTests
{
    static readonly HttpClient Browser = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    sealed class Exchange : IGcalGoogle
    {
        public readonly List<(string Code, string Verifier, string Redirect)> Got = new();
        public GcalToken Answer = GcalFake.Token(GcalFake.Now);
        public Task<GcalToken> ExchangeAsync(GcalClient client, string code, string verifier, string redirectUri)
        { lock (Got) Got.Add((code, verifier, redirectUri)); return Task.FromResult(Answer); }
        public Task<GcalToken> RefreshAsync(GcalClient client, string refreshToken) => throw new InvalidOperationException("not in a sign-in");
        public Task RevokeAsync(string token) => throw new InvalidOperationException("not in a sign-in");
        public Task<System.Text.Json.Nodes.JsonArray> CalendarListAsync(string access) => throw new InvalidOperationException("not in a sign-in");
        public Task<GcalPage> EventsAsync(string access, string calId, long from, long to) => throw new InvalidOperationException("not in a sign-in");
    }

    static string Param(string url, string key) =>
        Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Single(p => p[0] == key)[1].Replace('+', ' '));

    /// A sign-in whose "browser" asks <paramref name="answer"/>(redirect, state) on loopback; the pages it got back are kept.
    static async Task<(Task<GcalToken> SignIn, List<string> Pages, string Url, int Port)> Run(Exchange g, Func<string, string, IEnumerable<string>> answer, int waitMs = 15000)
    {
        var pages = new List<string>();
        var opened = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int port = 0;
        var signIn = GcalAuth.SignInAsync(new GcalAuthOptions {
            Client = GcalFake.Client, Google = g, WaitMs = waitMs, Listening = p => port = p,
            OpenExternal = url => {
                opened.TrySetResult(url);
                string redirect = Param(url, "redirect_uri"), state = Param(url, "state");
                _ = Task.Run(async () => {
                    foreach (var path in answer(redirect, state)) {
                        try {
                            using var res = await Browser.GetAsync(redirect.TrimEnd('/') + path);
                            string body = await res.Content.ReadAsStringAsync();
                            lock (pages) pages.Add((int)res.StatusCode + " " + body);
                        } catch (Exception e) { lock (pages) pages.Add("dropped " + e.GetType().Name); }
                    }
                });
                return null;
            },
        });
        string u = await opened.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (signIn, pages, u, port);
    }

    [Fact]
    public async Task The_redirect_comes_back_to_127_0_0_1_on_an_OS_picked_port_and_the_code_is_exchanged_with_the_PKCE_verifier()
    {
        var g = new Exchange();
        var (signIn, pages, url, port) = await Run(g, (_, state) => new[] { "/favicon.ico", "/?code=fake-code-1&state=" + Uri.EscapeDataString(state) });
        var tok = await signIn.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(GcalFake.Refresh, tok.Refresh);
        Assert.True(port > 0);
        Assert.Equal("http://127.0.0.1:" + port + "/", Param(url, "redirect_uri"));
        var (code, verifier, redirect) = Assert.Single(g.Got);
        Assert.Equal("fake-code-1", code);
        Assert.Equal("http://127.0.0.1:" + port + "/", redirect);
        // the verifier is the one whose S256 is the challenge Google was shown
        Assert.Equal(Param(url, "code_challenge"), GcalCore.B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
        Assert.Equal("S256", Param(url, "code_challenge_method"));
        await WaitFor(() => pages.Count >= 2);
        Assert.StartsWith("404", pages[0]);   // a favicon is not an answer and does not end the sign-in
        Assert.Contains(pages, p => p.StartsWith("200", StringComparison.Ordinal) && p.Contains("Mantle is connected", StringComparison.Ordinal));
        // the listener is gone once it is done
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Browser.GetAsync("http://127.0.0.1:" + port + "/?code=x&state=y"));
    }

    [Fact]
    public async Task A_wrong_state_is_refused_and_nothing_is_exchanged()
    {
        var g = new Exchange();
        var (signIn, pages, _, _) = await Run(g, (_, _) => new[] { "/?code=stolen&state=not-the-one" });
        var e = await Assert.ThrowsAsync<GcalError>(() => signIn.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("did not match", e.Reason);
        Assert.Empty(g.Got);
    }

    [Fact]
    public async Task No_on_Googles_page_and_a_grant_without_calendar_readonly_or_without_a_refresh_token_are_refused_in_words()
    {
        var (s1, _, _, _) = await Run(new Exchange(), (_, state) => new[] { "/?error=access_denied&state=" + Uri.EscapeDataString(state) });
        Assert.Contains("you said no", (await Assert.ThrowsAsync<GcalError>(() => s1.WaitAsync(TimeSpan.FromSeconds(20)))).Reason);

        var narrow = new Exchange { Answer = new GcalToken(GcalFake.Access, GcalFake.Refresh, GcalFake.Now + 3_600_000, new[] { "openid" }) };
        var (s2, _, _, _) = await Run(narrow, (_, state) => new[] { "/?code=c&state=" + Uri.EscapeDataString(state) });
        Assert.Contains("read access to your calendar", (await Assert.ThrowsAsync<GcalError>(() => s2.WaitAsync(TimeSpan.FromSeconds(20)))).Reason);

        var once = new Exchange { Answer = new GcalToken(GcalFake.Access, null, GcalFake.Now + 3_600_000, new[] { GcalCore.Scope }) };
        var (s3, _, _, _) = await Run(once, (_, state) => new[] { "/?code=c&state=" + Uri.EscapeDataString(state) });
        Assert.Contains("lasting sign-in", (await Assert.ThrowsAsync<GcalError>(() => s3.WaitAsync(TimeSpan.FromSeconds(20)))).Reason);
    }

    [Fact]
    public async Task No_answer_in_time_ends_the_sign_in_in_words()
    {
        var (s, _, _, _) = await Run(new Exchange(), (_, _) => Array.Empty<string>(), waitMs: 400);
        var e = await Assert.ThrowsAsync<GcalError>(() => s.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("not finished", e.Reason);
    }

    static async Task WaitFor(Func<bool> ok)
    {
        for (int i = 0; i < 200 && !ok(); i++) await Task.Delay(25);
        Assert.True(ok());
    }
}
