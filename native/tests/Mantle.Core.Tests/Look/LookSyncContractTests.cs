using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Xunit;

namespace Mantle.Core.Tests.Look;

/// <summary>
/// The look-sync project's own contract tests (~/look-sync/test/look-sync.test.js at 0b55b9c), vendored case for case
/// and run against the C# port with a live folder watcher, in temp folders only. Each test names the JS test it is.
/// Not vendored: "the live store is never the default in tests" (DataPaths and the read-only gate own that here;
/// ReadOnlyTests proves it). "two processes racing writes" runs as two instances on two threads in one process.
/// </summary>
public class LookSyncContractTests
{
    readonly Xunit.Abstractions.ITestOutputHelper? _out;
    public LookSyncContractTests(Xunit.Abstractions.ITestOutputHelper output) { _out = output; }

    const string LocalJson = "{\"theme\":\"light\",\"accent\":{\"hue\":30},\"glass\":{\"dark\":{\"blurPx\":8,\"dim\":0.6,\"panel\":-0.1},\"light\":{\"blurPx\":12,\"dim\":0.45,\"panel\":0},\"render\":\"picture\"}}";

    // FAST: debounceMs 30, corruptRetryMs 60, rewatchDelays [100, 200, 400], retryDelays [5, 10, 20]
    static LookSync Mk(string dir, string app = "helm", bool enabled = true, int debounce = 30, int corruptRetry = 60, int[]? rewatch = null, string? gateRoot = null, Action<string>? trace = null)
        => new(new LookSyncOptions {
            App = app, Dir = dir, Files = new SafeFile(WriteGate.Scratch(gateRoot ?? dir)), Enabled = enabled,
            Local = () => (JsonObject)JsJson.Parse(LocalJson)!, DebounceMs = debounce, CorruptRetryMs = corruptRetry,
            RewatchDelays = rewatch ?? new[] { 100, 200, 400 }, RetryDelays = new[] { 5, 10, 20 }, Trace = trace,
        });

    static string FileIn(string d) => Path.Combine(d, "look.json");
    static JsonObject ReadJson(string d) => (JsonObject)JsJson.Parse(File.ReadAllText(FileIn(d)))!;

    static Task<T> WaitFor<T>(Action<Action<T>> on, Action<Action<T>> off, Func<T, bool>? pred = null, int ms = 4000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<T>? h = null;
        h = x => { if (pred == null || pred(x)) { off(h!); tcs.TrySetResult(x); } };
        on(h);
        var cts = new CancellationTokenSource(ms);
        cts.Token.Register(() => { off(h); tcs.TrySetException(new TimeoutException("timed out waiting")); });
        return tcs.Task;
    }
    static Task<LookStatus> Status(LookSync s, Func<LookStatus, bool>? p = null, int ms = 4000) => WaitFor<LookStatus>(h => s.Status += h, h => s.Status -= h, p, ms);
    static Task<LookApply> Applied(LookSync s, Func<LookApply, bool>? p = null, int ms = 4000) => WaitFor<LookApply>(h => s.Apply += h, h => s.Apply -= h, p, ms);
    static Task<LookDesktop> Desk(LookSync s) => WaitFor<LookDesktop>(h => s.Desktop += h, h => s.Desktop -= h);
    static JsonNode V(string json) => JsJson.Parse(json)!;

    [Fact]
    public async Task First_run_with_sync_on_seeds_the_agreed_defaults_and_adopts_them()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using var s = Mk(d);
        var applied = Applied(s); var st = Status(s, x => x.State == "ok");
        s.Start();
        var a = await applied; var status = await st;
        Assert.Equal("seed", a.Cause);
        Assert.Equal("system", a.Changes["theme"]!.GetValue<string>());
        var doc = ReadJson(d);
        Assert.Equal(1, doc["schema"]!.GetValue<double>());
        Assert.Equal("{\"dark\":{\"blurPx\":5,\"dim\":0.5,\"panel\":0},\"light\":{\"blurPx\":12,\"dim\":0.45,\"panel\":0},\"render\":\"picture\"}", JsJson.Stringify(doc["look"]!["glass"]!["v"], 0));
        Assert.Equal("{\"hue\":null}", JsJson.Stringify(doc["look"]!["accent"]!["v"], 0));
        Assert.Equal("helm", doc["writer"]!["app"]!.GetValue<string>());
        Assert.NotNull(doc["writer"]!["instance"]); Assert.NotNull(doc["writer"]!["pid"]); Assert.NotNull(doc["writer"]!["at"]);
        Assert.False(doc.ContainsKey("wallpaperRequest"));
        Assert.Matches(@"shared defaults \(Match Windows\), seeded by Helm", status.Reason);
    }

    [Fact]
    public async Task Validation_a_bad_field_is_skipped_and_named_the_rest_applies()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        File.WriteAllText(FileIn(d), "{\"schema\":1,\"rev\":9,\"writer\":{\"app\":\"mantle\",\"instance\":\"x\"},\"look\":{\"theme\":{\"v\":\"light\",\"rev\":9,\"by\":\"mantle\"},\"accent\":{\"v\":{\"hue\":999},\"rev\":9,\"by\":\"mantle\"},\"glass\":{\"v\":{\"dark\":{\"blurPx\":100,\"dim\":0.5,\"panel\":0},\"light\":{\"blurPx\":1,\"dim\":0.5,\"panel\":0},\"render\":\"picture\"},\"rev\":9,\"by\":\"quartz\"}}}");
        using var s = Mk(d);
        var applied = Applied(s); var st = Status(s, x => x.State == "ok");
        s.Start();
        var a = await applied; var status = await st;
        Assert.Equal(new[] { "theme" }, a.Changes.Select(p => p.Key));
        Assert.Equal(2, status.Skipped.Count);
        Assert.Matches("Ignored bad accent value from Mantle", status.Reason);
        Assert.Matches("Ignored bad glass value from Quartz", status.Reason);
    }

    [Fact]
    public void Migrate_drops_the_retired_wallpaperRequest()
    {
        var p = LookSyncCore.Parse("{\"schema\":1,\"rev\":1,\"look\":{},\"wallpaperRequest\":{\"rev\":1,\"kind\":\"builtin\",\"id\":\"x\"}}");
        Assert.Equal("ok", p.State);
        Assert.False(p.Doc!.ContainsKey("wallpaperRequest"));
        Assert.False(LookSyncCore.Migrate((JsonObject)V("{\"schema\":1,\"wallpaperRequest\":1}")).ContainsKey("wallpaperRequest"));
    }

    [Fact]
    public async Task Torn_file_that_stays_torn_is_reported_moved_aside_reseeded_from_own_values_never_thrown()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        File.WriteAllText(FileIn(d), "{\"schema\":1,\"rev\":3,\"look\":{\"theme\":{\"v\":\"li");
        using var s = Mk(d);
        var applied = new List<LookApply>(); s.Apply += applied.Add;
        var st = Status(s, x => x.State == "corrupt");
        s.Start();
        var status = await st;
        Assert.Matches(@"damaged; restarted from Helm's settings \(old copy: look\.json\.bad-", status.Reason);
        Assert.Empty(applied);
        Assert.Single(Directory.GetFiles(d, "look.json.bad-*"));
        var doc = ReadJson(d);
        Assert.Equal("light", doc["look"]!["theme"]!["v"]!.GetValue<string>());
        Assert.Equal("{\"hue\":30}", JsJson.Stringify(doc["look"]!["accent"]!["v"], 0));
    }

    [Fact]
    public async Task Torn_read_that_heals_within_the_retry_window_is_not_treated_as_damage()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        File.WriteAllText(FileIn(d), "{\"schema\":1,\"rev\":3,\"lo");
        // The file heals right after the FIRST torn read (the instance's own trace line, said before it waits 300 ms to
        // read again), never after a test-side delay: a Task.Delay(80) on a pool starved by the parallel suite resumed
        // after the re-read had come and gone (the file moved aside as damage, or read mid-write).
        int healed = 0;
        using var s = Mk(d, corruptRetry: 300, trace: line => {
            if (line.StartsWith("check read corrupt", StringComparison.Ordinal) && Interlocked.Exchange(ref healed, 1) == 0)
                File.WriteAllText(FileIn(d), "{\"schema\":1,\"rev\":4,\"writer\":{\"app\":\"quartz\",\"instance\":\"q\"},\"look\":{\"theme\":{\"v\":\"system\",\"rev\":4,\"by\":\"quartz\"}}}");
        });
        var applied = Applied(s);
        s.Start();
        var a = await applied;
        Assert.Equal(1, healed);
        Assert.Equal("system", a.Changes["theme"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(d, "*.bad-*"));
    }

    [Fact]
    public async Task Corrupt_file_while_sync_is_off_is_reported_but_not_touched()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        File.WriteAllText(FileIn(d), "not json at all");
        using var s = Mk(d, enabled: false);
        var st = Status(s, x => (x.Reason ?? "").Contains("damaged"));
        s.Start();
        Assert.Equal("off", (await st).State);
        Assert.Equal("not json at all", File.ReadAllText(FileIn(d)));
    }

    [Fact]
    public async Task Newer_schema_opens_read_only_says_so_and_is_never_written_or_moved()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        const string bytes = "{\"schema\":2,\"rev\":5,\"look\":{\"theme\":{\"v\":\"light\",\"rev\":5}},\"futureThing\":1}";
        File.WriteAllText(FileIn(d), bytes);
        using var s = Mk(d);
        var applied = new List<LookApply>(); s.Apply += applied.Add;
        var st = Status(s, x => x.State == "newer");
        s.Start();
        Assert.Matches("newer version.*update this app", (await st).Reason);
        var r = await s.PublishAsync("theme", V("\"dark\""));
        Assert.False(r.Ok);
        Assert.Matches("newer version", r.Reason);
        Assert.Equal(bytes, File.ReadAllText(FileIn(d)));
        Assert.Empty(applied);
        Assert.Equal(new[] { "look.json" }, Directory.GetFiles(d).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Loop_guard_own_writes_never_come_back_others_follow_stale_field_revs_are_ignored()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using var a = Mk(d, "helm");
        using var b = Mk(d, "quartz");
        var aApplied = new List<LookApply>(); var bApplied = new List<LookApply>();
        a.Apply += x => { lock (aApplied) aApplied.Add(x); }; b.Apply += x => { lock (bApplied) bApplied.Add(x); };
        var aOk = Status(a, x => x.State == "ok"); a.Start(); await aOk;
        var bOk = Status(b, x => x.State == "ok"); b.Start(); await bOk;
        lock (aApplied) aApplied.Clear(); lock (bApplied) bApplied.Clear();

        var bGot = Applied(b);
        var r = await a.PublishAsync("theme", V("\"light\""));
        Assert.True(r.Ok);
        var e = await bGot;
        Assert.Equal("{\"theme\":\"light\"}", JsJson.Stringify(e.Changes, 0));
        Assert.Equal("helm", e.By);
        Assert.True(e.FromSync);
        await Task.Delay(200);
        lock (aApplied) Assert.Empty(aApplied);   // the writer does not re-apply its own change

        // A doc stamped with A's instance but different bytes: ignored by instance.
        var doc = ReadJson(d);
        double rev = doc["rev"]!.GetValue<double>();
        doc["look"]!["accent"] = V("{\"v\":{\"hue\":100},\"rev\":" + (rev + 1) + ",\"by\":\"helm\"}");
        doc["rev"] = rev + 1;
        var bAcc = Applied(b, x => x.Changes.ContainsKey("accent"));
        File.WriteAllText(FileIn(d), JsJson.Stringify(doc, 0));
        await bAcc;
        await Task.Delay(150);
        lock (aApplied) Assert.Empty(aApplied);

        // A field whose rev is not newer than the one applied is not re-applied.
        lock (bApplied) bApplied.Clear();
        var doc2 = ReadJson(d);
        doc2["rev"] = doc2["rev"]!.GetValue<double>() + 1; doc2["writer"] = V("{\"app\":\"mantle\",\"instance\":\"m\"}");
        doc2["look"]!["theme"] = V("{\"v\":\"dark\",\"rev\":1,\"by\":\"mantle\"}");
        var bSaw = Status(b, x => x.State == "ok" && x.By == "mantle");
        File.WriteAllText(FileIn(d), JsJson.Stringify(doc2, 0));
        await bSaw;
        await Task.Delay(100);
        lock (bApplied) Assert.Empty(bApplied);
    }

    [Fact]
    public async Task A_noop_event_same_bytes_is_dropped_by_the_content_hash()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using var s = Mk(d);
        var ok = Status(s, x => x.State == "ok"); s.Start(); await ok;
        await s.WhenIdle();
        int statuses = 0; s.Status += _ => Interlocked.Increment(ref statuses);
        var bytes = File.ReadAllBytes(FileIn(d));
        File.WriteAllBytes(FileIn(d), bytes);
        File.WriteAllBytes(FileIn(d), bytes);
        await Task.Delay(250);
        s.Refresh(); await Task.Delay(50); await s.WhenIdle();
        Assert.Equal(0, statuses);
    }

    [Fact]
    public async Task A_reseeded_file_rev_went_down_is_adopted_again()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using var s = Mk(d);
        var ok = Status(s, x => x.State == "ok"); s.Start(); await ok;
        for (int i = 0; i < 5; i++) await s.PublishAsync("accent", V("{\"hue\":" + i + "}"));
        var got = Applied(s);
        File.WriteAllText(FileIn(d), "{\"schema\":1,\"rev\":1,\"writer\":{\"app\":\"mantle\",\"instance\":\"m2\"},\"look\":{\"theme\":{\"v\":\"system\",\"rev\":1,\"by\":\"mantle\"}}}");
        Assert.Equal("system", (await got).Changes["theme"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_rename_refused_while_a_reader_holds_the_file_is_retried_then_reported_not_thrown()
    {
        // The JS test shims fs.renameSync to throw EBUSY; here a real reader holds look.json open (without delete
        // sharing), which is what Windows does to a replace-rename. Windows says EPERM or EBUSY for it.
        using var t = new TestDir("ls"); var d = t.Path;
        using var s = Mk(d);
        var ok = Status(s, x => x.State == "ok"); s.Start(); await ok;
        await s.WhenIdle();
        using (new FileStream(FileIn(d), FileMode.Open, FileAccess.Read, FileShare.Read)) {
            var err = Status(s, x => x.State == "error");
            var r2 = await s.PublishAsync("theme", V("\"dark\""));
            Assert.False(r2.Ok);
            Assert.Matches("^E(BUSY|PERM|ACCES)$", r2.Code);
            Assert.Matches(@"Can't write .*E(BUSY|PERM|ACCES) \(after 4 tries\)", (await err).Reason);
        }
        Assert.Empty(Directory.GetFiles(d, "*.tmp"));
        // released before the retries run out: it lands. The release follows the writer, not the clock: a thread of its
        // own (never the pool, which the writer's retry sleeps and a busy test run can starve: a Task.Delay(8)
        // continuation once came after all four tries) waits until the writer has closed its tmp copy, the step right
        // before the first rename, which the holder refuses. It lets go 20 ms later, inside the first 50 ms retry delay.
        var holder = new FileStream(FileIn(d), FileMode.Open, FileAccess.Read, FileShare.Read);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long closedAt = -1, releasedAt = -1;
        var releaser = new Thread(() => {
            try {
                while (clock.ElapsedMilliseconds < 10000) {
                    foreach (var tmp in Directory.GetFiles(d, "look.json.helm.*.tmp")) {
                        try { using (new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { } }
                        catch (Exception) { continue; }   // still open for writing (FileShare.None), or gone
                        closedAt = clock.ElapsedMilliseconds;
                        Thread.Sleep(20);
                        return;
                    }
                    Thread.Sleep(1);
                }
            } finally {
                holder.Dispose();
                releasedAt = clock.ElapsedMilliseconds;
            }
        }) { IsBackground = true, Name = "look test: the reader that lets go" };
        releaser.Start();
        using var slow = new LookSync(new LookSyncOptions { App = "helm", Dir = d, Files = new SafeFile(WriteGate.Scratch(d)), RetryDelays = new[] { 50, 100, 200 } });
        var landed = await slow.PublishAsync("theme", V("\"light\""));
        long answeredAt = clock.ElapsedMilliseconds;
        releaser.Join(10000);
        Assert.True(landed.Ok, $"the publish answered at +{answeredAt} ms ({landed.Reason}); the writer closed its tmp copy at +{closedAt} ms, the holder let go at +{releasedAt} ms");
        Assert.True(closedAt >= 0, "the writer's tmp copy was never seen closed");
        Assert.Equal("light", ReadJson(d)["look"]!["theme"]!["v"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(d, "*.tmp"));
        holder.Dispose();
    }

    [Fact]
    public async Task An_unwritable_folder_is_reported_with_its_code()
    {
        using var t = new TestDir("ls");
        string blocker = t.File("store");
        File.WriteAllText(blocker, "a file where the folder should be");
        using var s = Mk(blocker, rewatch: new[] { 60000 }, gateRoot: t.Path);
        var st = Status(s, x => x.State == "error");
        s.Start();
        Assert.Matches("look-sync|store|E[A-Z]+", (await st).Reason);
        var r = await s.PublishAsync("theme", V("\"dark\""));
        Assert.False(r.Ok);
        Assert.Matches("Can't (write|read)", r.Reason);
    }

    [Fact]
    public async Task The_watched_folder_deleted_and_recreated_no_event_storm_rewatches_follows_again()
    {
        using var t = new TestDir("ls"); var d = t.File("watched"); Directory.CreateDirectory(d);
        using var s = Mk(d, gateRoot: t.Path);
        var ok = Status(s, x => x.State == "ok"); s.Start(); await ok;
        var statuses = new List<LookStatus>(); s.Status += x => { lock (statuses) statuses.Add(x); };
        var lost = Status(s, x => x.State == "error" && (x.Reason ?? "").Contains("removed"));
        var back = Status(s, x => x.State == "missing", 5000);
        Directory.Delete(d, true);
        await lost;
        Assert.Matches("file was removed", (await back).Reason);
        await Task.Delay(600);
        lock (statuses) Assert.True(statuses.Count < 8, "no storm: " + statuses.Count + " status events");
        Assert.True(Directory.Exists(d), "folder re-created");

        var got = Applied(s, x => x.Changes["theme"]?.GetValue<string>() == "system");
        using var other = Mk(d, "quartz", gateRoot: t.Path);
        Assert.True((await other.PublishAsync("theme", V("\"system\""))).Ok);
        await got;
    }

    [Fact]
    public async Task Missing_while_running_the_next_local_change_recreates_the_file_from_current_values()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using var s = Mk(d);
        var ok = Status(s, x => x.State == "ok"); s.Start(); await ok;
        await s.WhenIdle();
        var gone = Status(s, x => x.State == "missing");
        File.Delete(FileIn(d));
        Assert.Matches("file was removed", (await gone).Reason);
        Assert.True((await s.PublishAsync("accent", V("{\"hue\":77}"))).Ok);
        var doc = ReadJson(d);
        Assert.Equal("{\"hue\":77}", JsJson.Stringify(doc["look"]!["accent"]!["v"], 0));
        Assert.Equal("light", doc["look"]!["theme"]!["v"]!.GetValue<string>());   // other fields from this app's current values
    }

    [Fact]
    public async Task Sync_switch_off_stops_reading_and_writing_the_look_desktop_still_flows_on_adopts()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using var helm = Mk(d, "helm");
        using var mantle = Mk(d, "mantle");
        var h1 = Status(helm, x => x.State == "ok"); helm.Start(); await h1;
        var m1 = Status(mantle, x => x.State == "ok"); mantle.Start(); await m1;

        var off = Status(helm, x => x.State == "off");
        await helm.SetEnabledAsync(false);
        Assert.Matches("Sync is off", (await off).Reason);
        var applied = new List<LookApply>(); helm.Apply += x => { lock (applied) applied.Add(x); };
        Assert.Equal("Sync is off", (await helm.PublishAsync("theme", V("\"light\""))).Reason);

        var desk = Desk(helm);
        await mantle.PublishAsync("theme", V("\"system\""));
        await mantle.SetEnabledAsync(false);
        var rd = await mantle.PublishDesktopAsync((JsonObject)V("{\"ok\":true,\"reason\":null,\"id\":\"builtin:snow-peak\",\"type\":\"builtin\",\"title\":\"Snow peak\",\"file\":\"C:\\\\x\\\\snow-peak.jpg\",\"still\":null,\"stillSize\":[2560,1440]}"));
        Assert.True(rd.Ok, "Mantle publishes desktop even with sync OFF");
        var dv = await desk;
        Assert.Equal("builtin:snow-peak", dv.Record!["id"]!.GetValue<string>());
        Assert.Null(dv.Reason);
        await Task.Delay(150);
        lock (applied) Assert.Empty(applied);

        var on = Status(helm, x => x.State == "ok" && (x.Reason ?? "").Contains("Now matching"));
        await helm.SetEnabledAsync(true);
        lock (applied) {
            Assert.Single(applied);
            Assert.Equal("adopt", applied[0].Cause);
            Assert.Equal("system", applied[0].Changes["theme"]!.GetValue<string>());
        }
        Assert.Matches(@"Now matching the others \(.*theme changed to Match Windows", (await on).Reason);
    }

    [Fact]
    public async Task Only_Mantle_publishes_the_desktop_record()
    {
        using var t = new TestDir("ls");
        using var s = Mk(t.Path, "quartz");
        var r = await s.PublishDesktopAsync((JsonObject)V("{\"ok\":true,\"id\":\"x\"}"));
        Assert.False(r.Ok);
        Assert.Matches("only Mantle", r.Reason);
    }

    [Fact]
    public async Task Two_writers_racing_the_file_is_always_whole_nothing_throws()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        using (var first = Mk(d)) Assert.True((await first.PublishAsync("theme", V("\"dark\""))).Ok);
        int reads = 0, torn = 0; bool done = false;
        var reader = Task.Run(async () => {
            while (!Volatile.Read(ref done)) {
                var r = LookSyncCore.Read(d);
                if (r.State == "ok") reads++; else if (r.State == "corrupt") torn++;
                await Task.Delay(2);
            }
        });
        async Task<(string App, int Ok, int Fail, List<string> Codes, List<string> Where)> Race(string app)
        {
            using var s = new LookSync(new LookSyncOptions { App = app, Dir = d, Files = new SafeFile(WriteGate.Scratch(d)) });
            int ok = 0, fail = 0; var codes = new List<string>(); var where = new List<string>();
            for (int i = 0; i < 40; i++) {
                var r = await s.PublishAsync("accent", V("{\"hue\":" + i + "}"));
                if (r.Ok) ok++;
                else {
                    fail++; codes.Add(r.Code ?? r.Reason ?? "?");
                    where.Add(((r.Reason ?? "").StartsWith("Can't read", StringComparison.Ordinal) ? "read " : "write ") + (r.Code ?? r.Reason ?? "?"));
                }
            }
            return (app, ok, fail, codes, where);
        }
        var outs = await Task.WhenAll(Task.Run(() => Race("helm")), Task.Run(() => Race("quartz")));
        Volatile.Write(ref done, true); await reader;
        // the JS test's "# race:" line: each writer's landed / failed writes and where each failure came from
        string said = string.Join("; ", outs.Select(o => $"{o.App} ok {o.Ok} fail {o.Fail}" +
            (o.Where.Count == 0 ? "" : " (" + string.Join(", ", o.Where.GroupBy(x => x).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + " x" + g.Count())) + ")")))
            + $"; reader ok {reads} torn {torn}";
        _out?.WriteLine("# race: " + said);
        foreach (var o in outs) {
            Assert.Equal(40, o.Ok + o.Fail);
            foreach (var c in o.Codes) Assert.Matches("^E[A-Z]+$", c);   // reported with its code, never thrown, never torn
        }
        int total = outs.Sum(o => o.Ok);
        Assert.True(total >= 70, "writes landed: " + total + "/80 (" + said + ")");
        Assert.True(torn == 0, "a reader saw a torn file (" + said + ")");
        var final = LookSyncCore.Read(d);
        Assert.Equal("ok", final.State);
        Assert.True(final.Doc!["rev"]!.GetValue<double>() >= 2);
        Assert.Empty(Directory.GetFiles(d, "*.tmp"));
    }

    [Fact]
    public async Task A_recovered_damaged_file_reports_the_true_state_corrupt_once_then_ok_with_the_reason()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        File.WriteAllText(FileIn(d), "not json, and it stays that way");
        using var s = Mk(d);
        var states = new List<LookStatus>(); s.Status += x => { lock (states) states.Add(x); };
        var okAfter = Status(s, x => x.State == "ok" && x.Recovered == "corrupt");
        s.Start();
        var st = await okAfter;
        Assert.Matches(@"damaged; restarted from Helm's settings \(old copy: look\.json\.bad-", st.Reason);
        lock (states) {
            int i = states.FindIndex(x => x.State == "corrupt");
            Assert.True(i >= 0 && states.IndexOf(st) == i + 1, "corrupt is said, and the ok follows it at once");
        }
        Assert.Equal("ok", s.CurrentStatus().State);
        Assert.Equal("helm", st.By);
        Assert.Equal("ok", LookSyncCore.Read(d).State);
        await s.PublishAsync("theme", V("\"dark\""));
        Assert.Equal("ok", s.CurrentStatus().State);
        Assert.Null(s.CurrentStatus().Reason);
    }

    [Fact]
    public async Task Deleting_a_newer_file_unblocks_changes_at_once_before_the_watch_event_lands()
    {
        using var t = new TestDir("ls"); var d = t.Path;
        File.WriteAllText(FileIn(d), "{\"schema\":2,\"rev\":5,\"look\":{}}");
        using var s = Mk(d, debounce: 2000);   // a slow watch: the status alone would still say newer
        var st = Status(s, x => x.State == "newer");
        s.Start();
        await st;
        Assert.Matches("newer version", s.ReadOnlyNow());
        File.Delete(FileIn(d));
        Assert.Equal("newer", s.CurrentStatus().State);
        var caught = Status(s, x => x.State == "missing", 1500);
        Assert.Null(s.ReadOnlyNow());
        await caught;
        var r = await s.PublishAsync("theme", V("\"dark\""));
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(1, ReadJson(d)["schema"]!.GetValue<double>());
        Assert.Equal("dark", ReadJson(d)["look"]!["theme"]!["v"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_read_only_gate_refuses_the_users_real_folder_and_says_so()
    {
        // the rule above every test here: a run that is not --live never writes the user's look-sync folder
        using var t = new TestDir("ls");
        string appData = t.File("AppData"), look = Path.Combine(appData, "look-sync");
        Directory.CreateDirectory(look);
        var paths = DataPaths.Resolve(LaunchArgs.Parse(Array.Empty<string>()), k => k == "APPDATA" ? appData : null, Path.GetTempPath());
        Assert.True(paths.IsUserLook);
        using var s = new LookSync(new LookSyncOptions { App = "mantle", Dir = paths.LookSyncDir, Files = new SafeFile(WriteGate.For(paths, LaunchArgs.Parse(Array.Empty<string>()))) });
        var st = Status(s, x => x.State == "error");
        s.Start();
        Assert.Matches("not --live", (await st).Reason);
        Assert.Empty(Directory.GetFileSystemEntries(look));
    }
}
