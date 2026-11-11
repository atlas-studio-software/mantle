using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// Google Calendar proving itself inside a --test instance (lib/features/_gcal-selftest.js; MANTLE_GCAL_SELFTEST=1, test
/// only). MOCKED GOOGLE: a fake answers every request; nothing leaves the PC and no real token or client file is touched.
/// REAL here: Windows DPAPI encrypting the fake sign-in on disk, the store in the test profile (through the write gate),
/// the engine. Result: one line, "gcal: SELFTEST n passed, m failed", in the log.
/// </summary>
public static class GcalSelfTest
{
    sealed class Fake : IGcalGoogle
    {
        public readonly string Refresh;
        public readonly List<string> Revoked = new();
        public Fake(string refresh) => Refresh = refresh;
        public Task<GcalToken> ExchangeAsync(GcalClient client, string code, string verifier, string redirectUri) => throw new GcalError("other", "not used");
        public Task<GcalToken> RefreshAsync(GcalClient client, string refreshToken) =>
            Task.FromResult(new GcalToken("selftest-access", Refresh, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000, new[] { GcalCore.Scope }));
        public Task RevokeAsync(string token) { Revoked.Add(token); return Task.CompletedTask; }
        public Task<JsonArray> CalendarListAsync(string access) =>
            Task.FromResult((JsonArray)JsonNode.Parse("[{\"id\":\"self@example.com\",\"summary\":\"Self\",\"primary\":true,\"backgroundColor\":\"#039be5\"}]")!);
        public Task<GcalPage> EventsAsync(string access, string calId, long from, long to)
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            string next = GcalCore.NextDate(day);
            var items = (JsonArray)JsonNode.Parse("[{\"id\":\"e1\",\"summary\":\"Selftest event\",\"start\":{\"date\":\"" + day + "\"},\"end\":{\"date\":\"" + next + "\"},\"htmlLink\":\"https://www.google.com/calendar/event?eid=x\"}]")!;
            return Task.FromResult(new GcalPage(items, null, false));
        }
    }

    public static async Task<(int Passed, List<string> Fails)> RunAsync(string dir, SafeFile files, JsonObject? lastStatus, Action<string> log)
    {
        int passed = 0; var fails = new List<string>();
        void Check(string name, bool ok, string? d = null) { if (ok) passed++; else fails.Add(name + (d != null ? " (" + GcalCore.Cut(d, 160) + ")" : "")); }
        try {
            Check("the feature published its status at install", lastStatus != null && GcalCore.Bool(lastStatus["test"]) && !GcalCore.Bool(lastStatus["signedIn"]), lastStatus?.ToJsonString());
            Check("DPAPI is available", Dpapi.Available());
            var store = new GcalStore(dir, files);
            string fake = "selftest-refresh-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var google = new Fake(fake);
            var pubs = new List<JsonObject?>();
            var eng = new GcalEngine(new GcalEngineDeps {
                Store = store, Google = google,
                SignIn = _ => Task.FromResult(new GcalToken("selftest-access", fake, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3_600_000, new[] { GcalCore.Scope })),
                Choices = () => new Dictionary<string, bool>(), SaveChoices = _ => { },
                Publish = (_, e) => pubs.Add(e), After = (_, _) => new Nothing(),
            });
            eng.Start();
            var sc = eng.SetClient("{\"installed\":{\"client_id\":\"1-selftest.apps.googleusercontent.com\",\"client_secret\":\"selftest-secret\"}}");
            Check("client saved", GcalCore.Bool(sc["ok"]), sc.ToJsonString());
            var si = await eng.SignIn().ConfigureAwait(true);
            Check("fake sign-in + sync", GcalCore.Bool(si["ok"]) && GcalCore.Str(si["account"]) == "self@example.com", si.ToJsonString());
            byte[] tok = File.ReadAllBytes(Path.Combine(dir, "token.bin"));
            string tokText = Encoding.UTF8.GetString(tok) + Encoding.Unicode.GetString(tok);
            Check("token.bin is encrypted (no token text on disk)", !tokText.Contains(fake, StringComparison.Ordinal) && !tokText.Contains("selftest-access", StringComparison.Ordinal));
            byte[] cl = File.ReadAllBytes(Path.Combine(dir, "client.bin"));
            Check("client.bin is encrypted", !(Encoding.UTF8.GetString(cl) + Encoding.Unicode.GetString(cl)).Contains("selftest-secret", StringComparison.Ordinal));
            Check("token decrypts back", GcalCore.Str(store.GetToken().Value?["refresh"]) == fake);
            Check("cache written", (store.GetCache()?["events"] as JsonArray)?.Count == 1);
            Check("events published without links", pubs.Any(p => p?["events"] is JsonArray a && a.Count == 1 && a[0] is JsonObject o && !o.ContainsKey("link")));
            Check("the consent URL asks for calendar.readonly only", OnlyScope(GcalCore.AuthUrl("1-x.apps.googleusercontent.com", "http://127.0.0.1:5000/", "c", "s")));
            Check("the host guard refuses anyone but Google", !GcalGoogle.Allowed("https://example.com/x") && GcalGoogle.Allowed(GcalCore.TokenUrl));
            var so = await eng.SignOut().ConfigureAwait(true);
            Check("sign out deletes the token AND the cache and revokes", GcalCore.Bool(so["ok"]) && !File.Exists(Path.Combine(dir, "token.bin")) && !File.Exists(Path.Combine(dir, "cache.json")) && google.Revoked.Count == 1, so.ToJsonString());
            eng.Stop();
        } catch (Exception e) { fails.Add("threw: " + e.GetType().Name + ": " + e.Message); }
        try { if (Directory.Exists(dir)) foreach (var f in Directory.GetFiles(dir)) files.Remove(f); } catch (Exception) { /* leave it */ }
        log("SELFTEST " + passed + " passed, " + fails.Count + " failed" + (fails.Count > 0 ? " -- " + string.Join("; ", fails) : ""));
        return (passed, fails);
    }

    /// The consent URL's scope parameter is exactly calendar.readonly.
    public static bool OnlyScope(string url)
    {
        var q = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToList();
        var scopes = q.Where(p => p[0] == "scope").Select(p => Uri.UnescapeDataString(p[1].Replace('+', ' '))).ToList();
        return scopes.Count == 1 && scopes[0] == GcalCore.Scope;
    }

    sealed class Nothing : IDisposable { public void Dispose() { } }
}
