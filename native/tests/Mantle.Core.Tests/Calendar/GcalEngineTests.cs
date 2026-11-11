using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Calendar;
using Xunit;

namespace Mantle.Core.Tests.Calendar;

/// lib/features/_gcal-store.js and _gcal-engine.js, ported: the encrypted files, the cache, the sync, the backoff and
/// the sign-out generation counter. A scratch folder, a fake Google and a hand-driven clock; never the network.
public class GcalEngineTests
{
    static bool B(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string? S(JsonNode? n) => GcalCore.Str(n);

    // ------------------------------------------------------------------ the store

    [Fact]
    public void The_store_reads_back_what_it_wrote_and_a_broken_file_is_said_not_thrown()
    {
        using var k = new GcalKit();
        Assert.Null(k.Store.GetToken().Value);
        Assert.Null(k.Store.GetToken().Broken);
        k.Store.PutToken(GcalFake.Token(k.Now).ToJson());
        k.Store.PutClient(GcalFake.Client);
        Assert.Equal(GcalFake.Refresh, S(k.Store.GetToken().Value?["refresh"]));
        Assert.Equal(GcalFake.ClientId, S(k.Store.GetClient().Value?["clientId"]));
        Assert.Equal(new[] { "client.bin", "token.bin" }, k.Files());   // no .tmp left behind

        File.WriteAllBytes(k.F("token.bin"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
        var broken = k.Store.GetToken();
        Assert.Null(broken.Value);
        Assert.Contains("could not be decrypted", broken.Broken);

        k.Store.PutCache(new JsonObject { ["events"] = new JsonArray(), ["syncedAt"] = 5 });
        Assert.Equal(5L, GcalCore.Num(k.Store.GetCache()?["syncedAt"]));
        File.WriteAllText(k.F("cache.json"), "{not json");
        Assert.Null(k.Store.GetCache());
    }

    [Fact]
    public void Remove_deletes_the_file_and_every_tmp_a_crash_left_and_a_missing_file_is_fine()
    {
        using var k = new GcalKit();
        k.Store.PutToken(GcalFake.Token(k.Now).ToJson());
        File.WriteAllText(k.F("token.bin.4242.tmp"), "half");
        File.WriteAllText(k.F("token.bin.17.tmp"), "half");
        File.WriteAllText(k.F("tokenXbin.1.tmp"), "not ours");
        k.Store.Remove("token.bin");
        Assert.Equal(new[] { "tokenXbin.1.tmp" }, k.Files());
        k.Store.Remove("token.bin");   // again: nothing there, no throw
        k.Store.DeleteCache();
    }

    [Fact]
    public void With_Electrons_Local_State_key_the_files_are_Chromiums_v10_AES_GCM_and_plain_DPAPI_still_reads()
    {
        using var d = new TestDir("gcal-v10");
        var files = new SafeFile(WriteGate.Scratch(d.Path));
        string dir = Path.Combine(d.Path, "gcal");
        // a profile Electron never ran in: plain DPAPI
        var plain = new GcalStore(dir, files);
        plain.PutClient(GcalFake.Client);
        byte[] dp = File.ReadAllBytes(Path.Combine(dir, "client.bin"));
        Assert.False(dp.AsSpan(0, 3).SequenceEqual("v10"u8));
        Assert.Equal(GcalFake.ClientId, S(JsonNode.Parse(Dpapi.Unprotect(dp))?["clientId"]));

        // Electron's Local State (a made-up key in the scratch folder, never the real one)
        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = Encoding.ASCII.GetBytes("DPAPI").Concat(Dpapi.Protect(key)).ToArray();
        string ls = d.Write("Local State", new JsonObject { ["os_crypt"] = new JsonObject { ["encrypted_key"] = Convert.ToBase64String(wrapped) } }.ToJsonString());
        var both = new GcalStore(dir, files, localStatePath: ls);
        Assert.Equal(GcalFake.ClientId, S(both.GetClient().Value?["clientId"]));   // the old plain file still reads

        both.PutToken(GcalFake.Token(GcalFake.Now).ToJson());
        byte[] v10 = File.ReadAllBytes(Path.Combine(dir, "token.bin"));
        Assert.True(v10.AsSpan(0, 3).SequenceEqual("v10"u8));
        int n = v10.Length - 3 - 12 - 16;
        var back = new byte[n];
        using (var g = new AesGcm(key, 16)) g.Decrypt(v10.AsSpan(3, 12), v10.AsSpan(15, n), v10.AsSpan(15 + n, 16), back);
        Assert.Equal(GcalFake.Refresh, S(JsonNode.Parse(back)?["refresh"]));
        Assert.Equal(GcalFake.Refresh, S(both.GetToken().Value?["refresh"]));
        // without the key a v10 file is "broken", never a crash
        Assert.NotNull(new GcalStore(dir, files, localStatePath: Path.Combine(d.Path, "none")).GetToken().Broken);
    }

    // ------------------------------------------------------------------ the engine

    [Fact]
    public async Task Sign_in_saves_the_token_syncs_the_on_calendars_caches_the_events_and_plans_the_next_sync()
    {
        using var k = new GcalKit();
        var r = await k.SignedIn();
        Assert.True(B(r, "ok"), r.ToJsonString());
        Assert.Equal(GcalFake.Account, S(r["account"]));
        Assert.Equal(new[] { "exchange", "list", "events me@example.com", "events work@group.calendar.google.com" }, k.G.Calls);   // Holidays is off
        Assert.Equal(new[] { "cache.json", "client.bin", "token.bin" }, k.Files());
        var st = k.Status;
        Assert.True(B(st, "connected"));
        Assert.True(B(st, "signedIn"));
        Assert.False(B(st, "syncing"));
        Assert.Equal(k.Now, GcalCore.Num(st["syncedAt"]));
        Assert.Null(st["error"]);
        var ev = (JsonArray)k.LastEvents["events"]!;
        Assert.Equal(2, ev.Count);   // the cancelled one is left out
        Assert.Equal(new[] { GcalFake.Title1, GcalFake.Title2 }, ev.Select(e => S(e!["title"])).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(GcalEngine.PeriodMs, k.Live!.Ms);
        Assert.Equal(GcalFake.Account, S(k.Store.GetToken().Value?["account"]));
    }

    [Fact]
    public async Task A_restart_draws_the_cache_with_no_network_and_a_cache_with_no_sign_in_is_deleted()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        var k2Dir = k.Store.Dir;
        k.Engine.Stop();
        k.G.Calls.Clear(); k.Pubs.Clear(); k.Timers.Clear();
        k.Engine.Start();
        Assert.Empty(k.G.Calls);
        Assert.Equal(2, ((JsonArray)k.LastEvents["events"]!).Count);
        Assert.Equal(GcalEngine.FirstMs, k.Live!.Ms);   // the first sync waits 4 s

        k.Engine.Stop();
        k.Store.DeleteToken();
        k.Engine.Start();
        Assert.False(File.Exists(k.F("cache.json")));
        Assert.Equal(0, ((JsonArray)k.LastEvents["events"]!).Count);
        Assert.True(Directory.Exists(k2Dir));
    }

    [Fact]
    public async Task An_expired_access_token_is_refreshed_once_and_saved_and_a_dead_refresh_token_asks_for_sign_in()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        k.Now += 2 * 3_600_000;   // the hour is up
        k.G.Calls.Clear();
        await k.Engine.SyncNow("test", force: true);
        Assert.Equal("refresh", k.G.Calls[0]);
        Assert.Equal(GcalFake.Access2, k.G.AccessUsed.Last());
        Assert.Equal(GcalFake.Access2, S(k.Store.GetToken().Value?["access"]));
        Assert.Equal(GcalFake.Refresh, S(k.Store.GetToken().Value?["refresh"]));   // Google sent none: the old one stays

        k.Now += 2 * 3_600_000;
        k.G.RefreshThrows = new GcalError(400, JsonNode.Parse("{\"error\":\"invalid_grant\"}"), null);
        await k.Engine.SyncNow("test", force: true);
        var st = k.Status;
        Assert.True(B(st, "needsSignIn"));
        Assert.False(B(st, "connected"));
        Assert.Equal("auth", S(st["error"]?["kind"]));
        Assert.Null(k.Live);   // no retry timer while a sign-in is needed
    }

    [Fact]
    public async Task A_failed_sync_backs_off_and_one_bad_calendar_is_a_partial_not_a_failure()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        k.G.ListThrows = new GcalError(503, null, null);
        await k.Engine.SyncNow("test", force: true);
        var st = k.Status;
        Assert.Equal("server", S(st["error"]?["kind"]));
        long wait = (GcalCore.Num(st["nextAt"]) ?? 0) - k.Now;
        Assert.Equal(wait, k.Live!.Ms);
        Assert.InRange(wait, 30_000, 120_000);   // ~1 min with jitter
        Assert.Contains(k.Log, l => l.StartsWith("sync failed (test): server", StringComparison.Ordinal));
        Assert.Equal(2, ((JsonArray)k.LastEvents["events"]!).Count);   // the last good events stay

        k.G.ListThrows = null;
        k.G.EventsThrow["work@group.calendar.google.com"] = new GcalError(500, null, null);
        k.Now += 5 * 60_000;
        await k.Engine.SyncNow("test", force: true);
        st = k.Status;
        Assert.Equal("partial", S(st["error"]?["kind"]));
        Assert.Contains("Work", S(st["error"]?["reason"]));
        Assert.Equal(new[] { GcalFake.Title1 }, ((JsonArray)k.LastEvents["events"]!).Select(e => S(e!["title"])).ToArray());
    }

    [Fact]
    public async Task Sign_out_while_a_sync_is_still_out_drops_its_answer_so_nothing_is_cached_again()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        k.G.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = k.Engine.SyncNow("test", force: true);
        Assert.False(sync.IsCompleted);
        var o = await k.Engine.SignOut();
        Assert.True(B(o, "ok"));
        k.G.ListGate.SetResult();
        await sync.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(k.F("cache.json")));
        Assert.False(File.Exists(k.F("token.bin")));
        Assert.Equal(0, ((JsonArray)k.Engine.EventsPayload()["events"]!).Count);
        Assert.False(B(k.Status, "signedIn"));
    }

    [Fact]
    public async Task A_calendar_choice_is_saved_and_the_month_ask_covers_or_fetches()
    {
        using var k = new GcalKit();
        await k.SignedIn();
        var r = k.Engine.SetCalendar("holidays", true);
        Assert.True(B(r, "ok"), r.ToJsonString());
        Assert.True(k.Saved["holidays"]);
        var w = k.Engine.Want(2026, 10);   // November 2026: the window around now covers it
        Assert.True(B(w, "covered"), w.ToJsonString());
        Assert.False(B(k.Engine.Want(2026, 12), "ok"));   // month 12 is not a month (0..11, like JS)
        k.G.Calls.Clear();
        var far = k.Engine.Want(2027, 6);
        Assert.False(B(far, "covered"));
        Assert.True(B(far, "loading"), far.ToJsonString());
        Assert.Contains("list", k.G.Calls);
    }

    [Fact]
    public void Sign_in_refuses_without_a_client_file_or_without_DPAPI_and_says_why()
    {
        using var k = new GcalKit();
        k.Engine.Start();
        var r = k.Engine.SignIn().GetAwaiter().GetResult();
        Assert.False(B(r, "ok"));
        Assert.Contains("client file", S(r["reason"]));

        using var k2 = new GcalKit(canEncrypt: () => false);
        Assert.Throws<GcalError>(() => k2.Store.PutClient(GcalFake.Client));
        Assert.Empty(k2.Files());
        var bad = k2.Engine.SetClient("{\"installed\":{\"client_id\":\"" + GcalFake.ClientId + "\",\"client_secret\":\"x\"}}");
        Assert.False(B(bad, "ok"));
        Assert.Contains("DPAPI", S(bad["reason"]));
    }
}
