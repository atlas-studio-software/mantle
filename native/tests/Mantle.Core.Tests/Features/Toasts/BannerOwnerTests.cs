using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Toasts;
using Xunit;
using static Mantle.Core.Tests.Features.Toasts.ToastFixtures;

namespace Mantle.Core.Tests.Features.Toasts;

// lib/features/_toasts-owner.test.js, ported (TST-077..TST-085, TST-099): the ShowBanner bookkeeping against a
// FAKE registry and a FAKE record file. Section numbers are the JS test's.
public class BannerOwnerTests
{
    [Fact]
    public void LeftToWindows_keeps_system_balloon_call_and_test_ids_apart()
    {
        Assert.NotNull(BannerOwner.LeftToWindows("Windows.SystemToast.BthQuickPair", false));
        Assert.NotNull(BannerOwner.LeftToWindows("NotifyIconGeneratedAumid_99", false));
        Assert.NotNull(BannerOwner.LeftToWindows("Microsoft.YourPhone_8wekyb3d8bbwe!App", false));
        Assert.Null(BannerOwner.LeftToWindows(H, false));
        Assert.NotNull(BannerOwner.LeftToWindows(H, true));                       // a test copy never takes a real app
        Assert.Null(BannerOwner.LeftToWindows("Mantle.ToastTest.e2e", true));     // a test copy takes its own test app
        Assert.NotNull(BannerOwner.LeftToWindows("Mantle.ToastTest.e2e", false)); // a live copy never takes a test app
        Assert.Equal("Windows did not say which app it is", BannerOwner.LeftToWindows("", false));
    }

    [Fact]
    public async Task S1_first_apply_is_write_ahead_and_takes_the_right_apps_with_the_right_originals()
    {
        var reg = Apps(); var store = new FakeStore();
        BannerRecord? atWrite = null;
        reg.OnWrite = _ => atWrite = store.Record;
        var o = new BannerOwner(reg, store);
        var r = await o.ApplyAsync("start");
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(new[] { H, D }.OrderBy(x => x, StringComparer.Ordinal), r.Took.OrderBy(x => x, StringComparer.Ordinal));
        Assert.NotNull(atWrite);
        Assert.Equal(2, atWrite!.Owned.Count);                                   // SAVED before the first registry write
        var rec = store.Rec!;
        Assert.Null(rec["owned"]![H]);                                           // helm original = no value
        Assert.Equal(1, (int)rec["owned"]![D]!);
        Assert.Equal(0, reg.Apps[H].Banner); Assert.Equal(0, reg.Apps[D].Banner);
        Assert.Equal(0, reg.Apps["electron.app.Munder Difflin"].Banner);         // the user's own 0 untouched, not owned
        Assert.False(o.Owns("electron.app.Munder Difflin"));
        Assert.Null(reg.Apps["OperaSoftware.OperaGXWebBrowser.1751952444"].Banner);
        foreach (var k in new[] { "Windows.SystemToast.AutoPlay", "NotifyIconGeneratedAumid_1234", "Microsoft.YourPhone_8wekyb3d8bbwe!App", "Weird.App" })
            Assert.False(o.Owns(k), k);
        Assert.True(o.Owns("COM.OWENSILVA.HELM") && o.Owns("com.squirrel.discord.discord"));   // case-blind

        // 2. a second apply writes nothing and never clobbers the originals
        int before = reg.Writes.Count;
        var r2 = await o.ApplyAsync("periodic");
        Assert.Equal(before, reg.Writes.Count); Assert.Empty(r2.Took);
        Assert.Null(store.Record.Owned[H]); Assert.Equal(1, store.Record.Owned[D]);

        // 3. after a crash (a new owner on the same record): nothing re-taken, originals from the record
        var o3 = new BannerOwner(reg, store);
        var r3 = await o3.ApplyAsync("start after crash");
        Assert.Empty(r3.Took); Assert.Equal(1, store.Record.Owned[D]);

        // 4. restore puts both back (helm by deleting the value), owns nothing; a second restore is a no-op
        var q = await o3.RestoreAsync("quit");
        Assert.True(q.Ok); Assert.Equal(2, q.Restored);
        Assert.Null(reg.Apps[H].Banner); Assert.Equal(1, reg.Apps[D].Banner);
        Assert.Equal(0, store.Record.Owned.Count);
        Assert.Equal(0, reg.Apps["electron.app.Munder Difflin"].Banner);
        var q2 = await o3.RestoreAsync("quit again");
        Assert.True(q2.Ok); Assert.Equal(0, q2.Restored);
        Assert.Equal(2, reg.Writes.Count(w => w.Aumid == H));
    }

    [Fact]
    public async Task S5_put_back_by_someone_else_is_taken_again_with_the_originals_kept()
    {
        var reg = Apps(); var store = new FakeStore(); var o = new BannerOwner(reg, store);
        await o.ApplyAsync("start");
        reg.Apps[D].Banner = 1; reg.Apps[H].Banner = null;      // the sidecar's exit wrote the originals back (no marker)
        var r = await o.ApplyAsync("sidecar came back");
        Assert.Equal(2, r.Retook.Count);
        Assert.Equal(0, reg.Apps[D].Banner); Assert.Equal(0, reg.Apps[H].Banner);
        Assert.Equal(1, store.Record.Owned[D]); Assert.Null(store.Record.Owned[H]);
    }

    [Fact]
    public async Task S6_a_user_change_lets_the_app_go_until_a_fresh_on()
    {
        var reg = Apps(); var store = new FakeStore(); var o = new BannerOwner(reg, store);
        await o.ApplyAsync("start");
        reg.Apps[H].Banner = 1;                                    // original was null; 1 is the user's own choice
        var r = await o.ApplyAsync("periodic");
        Assert.Contains(H, r.Released); Assert.False(o.Owns(H)); Assert.True(o.Released(H));
        Assert.Equal(1, reg.Apps[H].Banner);
        var r2 = await o.ApplyAsync("periodic");
        Assert.DoesNotContain(H, r2.Took); Assert.Equal(1, reg.Apps[H].Banner);
        var q = await o.RestoreAsync("quit", keepReleased: true);
        Assert.True(q.Ok); Assert.Equal(1, reg.Apps[H].Banner);
        Assert.Contains(H, store.Record.Released);                 // a quit keeps released
        var r3 = await o.ApplyAsync("switched on", fresh: true);
        Assert.Contains(H, r3.Took); Assert.Equal(1, store.Record.Owned[H]);   // original = his 1
    }

    [Fact]
    public async Task S7_failures_are_said_and_never_leave_a_lie_in_the_record()
    {
        var reg = Apps(); var store = new FakeStore();
        reg.FailFor.Add(D);
        var o = new BannerOwner(reg, store);
        var r = await o.ApplyAsync("start");
        Assert.False(r.Ok); Assert.Single(r.Failed); Assert.Contains("access denied", r.Failed[0].Reason);
        Assert.False(store.Record.Owned.ContainsKey(D));            // a refused NEW take is dropped
        Assert.True(store.Record.Owned.ContainsKey(H));
        reg.FailFor.Clear(); reg.FailFor.Add(H);
        var q = await o.RestoreAsync("quit");
        Assert.False(q.Ok); Assert.True(store.Record.Owned.ContainsKey(H)); Assert.Contains("restore-banners", q.Reason);
        reg.FailFor.Clear();
        var q2 = await o.RestoreAsync("retry");
        Assert.True(q2.Ok); Assert.Equal(1, q2.Restored); Assert.Null(reg.Apps[H].Banner); Assert.Equal(0, store.Record.Owned.Count);
    }

    [Fact]
    public async Task S8_a_record_that_cannot_be_saved_means_nothing_is_written()
    {
        var reg = Apps(); var store = new FakeStore { FailSave = true };
        var r = await new BannerOwner(reg, store).ApplyAsync("start");
        Assert.False(r.Ok); Assert.Empty(reg.Writes); Assert.Contains("changed nothing", r.Reason);
    }

    [Fact]
    public async Task S9_an_unreadable_record_takes_nothing_and_restore_writes_nothing()
    {
        var reg = Apps();
        var store = new FakeStore { File = "{\"v\":1,\"owned\":{\"com.owensilva.helm\":null},\"released\":[]}", FailLoad = true };
        var o = new BannerOwner(reg, store);
        var r = await o.ApplyAsync("start");
        Assert.False(r.Ok); Assert.Empty(reg.Writes); Assert.Contains("could not read", r.Reason);
        var q = await o.RestoreAsync("quit");
        Assert.False(q.Ok); Assert.Empty(reg.Writes); Assert.Contains("left as they are", q.Reason);
        Assert.Equal("Mantle could not read its record of Windows' original pop-up settings (unexpected token in JSON)", o.Counts().RecordWhy);
    }

    [Fact]
    public async Task S10_a_registry_that_cannot_be_read_keeps_the_record_for_later()
    {
        var reg = Apps(); var store = new FakeStore(); var o = new BannerOwner(reg, store);
        await o.ApplyAsync("start");
        reg.ReadFails = true;
        var q = await o.RestoreAsync("quit");
        Assert.False(q.Ok); Assert.Equal(2, store.Record.Owned.Count);
    }

    [Fact]
    public async Task S11_a_new_app_is_taken_on_the_next_apply()
    {
        var reg = new FakeReg().Add(H, null); var store = new FakeStore(); var o = new BannerOwner(reg, store);
        await o.ApplyAsync("start");
        reg.Add("Anysphere.Cursor", null);
        var r = await o.ApplyAsync("a new app notified");
        Assert.Equal(new[] { "Anysphere.Cursor" }, r.Took); Assert.Equal(0, reg.Apps["Anysphere.Cursor"].Banner);
    }

    [Fact]
    public async Task S12_operations_never_interleave()
    {
        var reg = Apps(); var store = new FakeStore(); var o = new BannerOwner(reg, store);
        var a = o.ApplyAsync("start"); var b = o.RestoreAsync("quit at once");
        await Task.WhenAll(a, b);
        Assert.Equal(2, a.Result.Took.Count); Assert.Equal(2, b.Result.Restored);
        Assert.Null(reg.Apps[H].Banner); Assert.Equal(1, reg.Apps[D].Banner);
    }

    [Fact]
    public async Task S13_a_test_owner_touches_only_test_ids()
    {
        var reg = Apps().Add("Mantle.ToastTest.e2e", null); var store = new FakeStore();
        var r = await new BannerOwner(reg, store, test: true).ApplyAsync("test start");
        Assert.Equal(new[] { "Mantle.ToastTest.e2e" }, r.Took);
        Assert.All(reg.Writes, w => Assert.Equal("Mantle.ToastTest.e2e", w.Aumid));
    }

    [Fact]
    public async Task S14_put_back_then_the_user_turns_it_off_is_let_go_never_restored_never_retaken()
    {
        var reg = Apps(); var store = new FakeStore(); var logs = new List<string>();
        var o = new BannerOwner(reg, store, logs.Add);
        await o.ApplyAsync("start");
        PutBack(reg, store, "sidecar");                          // the helper's exit put Discord (1) and helm (null) back
        reg.Apps[D].Banner = 0;                                  // Mantle is down; the user turns Discord's banners OFF
        int w0 = reg.Writes.Count;
        var r = await o.ApplyAsync("the helper came back");
        Assert.Contains(D, r.Released); Assert.False(o.Owns(D)); Assert.True(o.Released(D));
        Assert.False(store.Record.Owned.ContainsKey(D)); Assert.False(store.Record.Taken.ContainsKey(D));
        Assert.DoesNotContain(reg.Writes.Skip(w0), x => x.Aumid == D);
        Assert.Contains(H, r.Retook); Assert.Equal(0, reg.Apps[H].Banner);
        Assert.Contains(logs, l => l.Contains("let 1 app(s) go") && l.Contains("put back while Mantle was not running"));
        var q = await o.RestoreAsync("quit", keepReleased: true);
        Assert.True(q.Ok); Assert.Equal(0, reg.Apps[D].Banner); Assert.Null(reg.Apps[H].Banner);
        var r2 = await o.ApplyAsync("next start");
        Assert.DoesNotContain(D, r2.Took); Assert.DoesNotContain(D, r2.Retook); Assert.Equal(0, reg.Apps[D].Banner);
    }

    [Fact]
    public async Task S14b_restore_never_writes_back_the_users_0_after_a_put_back()
    {
        var reg = Apps(); var store = new FakeStore(); var o = new BannerOwner(reg, store);
        await o.ApplyAsync("start");
        // the script put Discord alone back (helm stays Mantle's 0), listing it with the record's stamp
        reg.Apps[D].Banner = 1;
        store.Back.Add(new BackEntry(D, Stamp.Of(store.TakenOf(D)), 0, "script"));
        reg.Apps[D].Banner = 0;                                  // then the user turned Discord's banners off himself
        int w0 = reg.Writes.Count;
        var q = await o.RestoreAsync("quit");
        Assert.DoesNotContain(reg.Writes.Skip(w0), x => x.Aumid == D);
        Assert.Equal(0, reg.Apps[D].Banner);
        Assert.Contains(D, q.Released); Assert.False(store.Record.Owned.ContainsKey(D)); Assert.False(store.Record.Taken.ContainsKey(D));
        Assert.Null(reg.Apps[H].Banner); Assert.Equal(1, q.Restored);  // Mantle's own 0 (helm) still goes back
    }

    [Fact]
    public async Task S15_a_retake_gets_a_new_stamp_so_an_old_marker_never_makes_Mantles_0_the_users()
    {
        double t = 1000;
        var reg = Apps(); var store = new FakeStore();
        var o = new BannerOwner(reg, store, now: () => t);
        await o.ApplyAsync("start");
        var s1 = store.TakenOf(D);
        Assert.Equal(1000, s1); Assert.Equal(1000, store.TakenOf(H));
        PutBack(reg, store, "supervisor");
        var r = await o.ApplyAsync("Mantle came back at once (same clock)");
        var s2 = store.TakenOf(D);
        Assert.Contains(D, r.Retook); Assert.Equal(0, reg.Apps[D].Banner);
        Assert.True(s2 > s1 && s2 == Math.Floor(s2!.Value));
        int w0 = reg.Writes.Count;
        var r2 = await o.ApplyAsync("periodic");
        Assert.True(o.Owns(D)); Assert.DoesNotContain(D, r2.Released); Assert.Equal(w0, reg.Writes.Count);
        t = 500;                                                  // the clock jumps BACK
        PutBack(reg, store, "sidecar");
        await o.ApplyAsync("after a clock jump");
        var s3 = store.TakenOf(D);
        Assert.True(s3 > s2);
        var q = await o.RestoreAsync("quit", keepReleased: true);
        Assert.True(q.Ok); Assert.Equal(2, q.Restored); Assert.Equal(1, reg.Apps[D].Banner); Assert.Null(reg.Apps[H].Banner);
    }

    [Fact]
    public async Task S16_the_stamp_is_on_disk_before_the_0_on_take_and_retake()
    {
        double t = 5000;
        var reg = Apps(); var store = new FakeStore();
        var seen = new List<BannerRecord>();
        reg.OnWrite = _ => seen.Add(store.Record);
        var o = new BannerOwner(reg, store, now: () => t);
        await o.ApplyAsync("start");
        Assert.Single(seen); Assert.Equal(5000, seen[0].Taken[D]); Assert.Equal(5000, seen[0].Taken[H]);
        PutBack(reg, store, "sidecar");
        t = 6000;
        await o.ApplyAsync("retake");
        Assert.Equal(2, seen.Count); Assert.Equal(6000, seen[1].Taken[D]); Assert.Equal(6000, seen[1].Taken[H]);

        var reg2 = Apps(); var store2 = new FakeStore { FailSave = true };
        var r = await new BannerOwner(reg2, store2, now: () => t).ApplyAsync("start");
        Assert.False(r.Ok); Assert.Empty(reg2.Writes);

        var reg3 = Apps(); var store3 = new FakeStore(); reg3.FailFor.Add(D);
        await new BannerOwner(reg3, store3, now: () => t).ApplyAsync("start");
        Assert.False(store3.Record.Owned.ContainsKey(D)); Assert.False(store3.Record.Taken.ContainsKey(D));
        Assert.Equal(6000, store3.TakenOf(H));
    }

    [Fact]
    public async Task S17_a_legacy_record_without_stamps_matches_a_marker_with_took_null()
    {
        double t = 7000;
        static string L(string x) => "Legacy." + x;
        var reg = new FakeReg().Add(L("A"), 0).Add(L("B"), 0).Add(L("C"), 1).Add(L("D"), 0);
        var store = new FakeStore { File = $"{{\"v\":1,\"owned\":{{\"{L("A")}\":1,\"{L("B")}\":1,\"{L("C")}\":1,\"{L("D")}\":1}},\"taken\":{{\"{L("D")}\":42}},\"released\":[]}}" };
        store.Back.Add(new BackEntry(L("A"), Stamp.None));
        store.Back.Add(new BackEntry(L("D"), Stamp.None));        // D has a stamp (42): a null marker does not match
        var o = new BannerOwner(reg, store, now: () => t);
        var r = await o.ApplyAsync("start");
        Assert.Contains(L("A"), r.Released); Assert.False(o.Owns(L("A")));
        Assert.True(o.Owns(L("B")) && o.Owns(L("D"))); Assert.DoesNotContain(L("B"), r.Released); Assert.DoesNotContain(L("D"), r.Released);
        Assert.Contains(L("C"), r.Retook); Assert.Equal(7000, store.TakenOf(L("C")));
        var q = await o.RestoreAsync("quit", keepReleased: true);
        Assert.Equal(3, q.Restored); Assert.Equal(0, reg.Apps[L("A")].Banner);
        Assert.Equal(1, reg.Apps[L("B")].Banner); Assert.Equal(1, reg.Apps[L("C")].Banner); Assert.Equal(1, reg.Apps[L("D")].Banner);
    }

    [Fact]
    public void S17_load_drops_bad_stamps_and_stamps_of_apps_no_longer_owned_and_bad_originals()
    {
        var rec = BannerRecord.FromJson(JsonNode.Parse("{\"v\":1,\"owned\":{\"Odd.Stamp\":1,\"New.App\":null,\"Bad.Value\":2},\"taken\":{\"Odd.Stamp\":\"12\",\"Gone.App\":5,\"New.App\":9},\"released\":[\"x\",3]}"));
        Assert.True(rec.Owned.ContainsKey("Odd.Stamp")); Assert.False(rec.Taken.ContainsKey("Odd.Stamp"));
        Assert.False(rec.Taken.ContainsKey("Gone.App")); Assert.Equal(9, rec.Taken["New.App"]);
        Assert.False(rec.Owned.ContainsKey("Bad.Value"));
        Assert.Equal(new[] { "x" }, rec.Released);
    }

    [Fact]
    public async Task S18_a_marker_naming_an_old_null_or_string_stamp_never_releases()
    {
        double t = 1000;
        var reg = new FakeReg().Add(D, 1).Add(H, null); var store = new FakeStore();
        var o = new BannerOwner(reg, store, now: () => t);
        await o.ApplyAsync("start");                              // stamp 1000
        PutBack(reg, store, "sidecar");                           // marker took 1000
        t = 2000;
        await o.ApplyAsync("retake");                             // stamp 2000
        Assert.Equal(2000, store.TakenOf(D));
        store.Back.Add(new BackEntry(D, Stamp.None)); store.Back.Add(new BackEntry(D, Stamp.Other));
        var r = await o.ApplyAsync("periodic");                   // D reads Mantle's 0 with only old/null/string markers
        Assert.True(o.Owns(D)); Assert.DoesNotContain(D, r.Released);
        var q = await o.RestoreAsync("quit", keepReleased: true);
        Assert.Equal(2, q.Restored); Assert.Equal(1, reg.Apps[D].Banner);
    }

    [Fact]
    public async Task S19_unreadable_markers_are_ignored_and_said()
    {
        var reg = Apps(); var store = new FakeStore(); var logs = new List<string>();
        var o = new BannerOwner(reg, store, logs.Add);
        await o.ApplyAsync("start");
        store.FailBack = true;
        var r = await o.ApplyAsync("periodic");
        Assert.True(r.Ok); Assert.Empty(r.Released); Assert.True(o.Owns(D) && o.Owns(H));
        Assert.Contains(logs, l => l.Contains("put-back marker") && l.Contains("could not be read"));
        var q = await o.RestoreAsync("quit");
        Assert.True(q.Ok); Assert.Equal(2, q.Restored);
    }

    [Fact]
    public async Task S20_a_clock_jump_back_onto_an_old_markers_stamp_never_makes_Mantles_0_the_users()
    {
        double t = 1000;
        var reg = new FakeReg().Add(D, 1); var store = new FakeStore();
        var o = new BannerOwner(reg, store, now: () => t);
        await o.ApplyAsync("start");
        PutBack(reg, store, "supervisor");
        await o.RestoreAsync("quit", keepReleased: true);         // not 0: dropped with its stamp
        Assert.False(store.Record.Owned.ContainsKey(D)); Assert.False(store.Record.Taken.ContainsKey(D));
        t = 1000;
        var r = await o.ApplyAsync("next start");
        var s = store.TakenOf(D);
        Assert.Contains(D, r.Took); Assert.NotEqual(1000, s);
        var r2 = await o.ApplyAsync("periodic");
        Assert.True(o.Owns(D)); Assert.DoesNotContain(D, r2.Released);
        var q = await o.RestoreAsync("quit", keepReleased: true);
        Assert.Equal(1, q.Restored); Assert.Equal(1, reg.Apps[D].Banner);
    }

    [Fact]
    public async Task S21_taken_is_keyed_exactly_as_owned_and_an_integer()
    {
        var reg = new FakeReg().Add("Case.App", null); var store = new FakeStore();
        await new BannerOwner(reg, store, now: () => 4242.9).ApplyAsync("start");
        var rec = store.Rec!;
        Assert.Equal(string.Join("|", ((JsonObject)rec["owned"]!).Select(kv => kv.Key)), string.Join("|", ((JsonObject)rec["taken"]!).Select(kv => kv.Key)));
        Assert.Equal("4242", rec["taken"]!["Case.App"]!.ToJsonString());
        Assert.StartsWith("{\"v\":1,\"owned\":", store.File);
    }
}
