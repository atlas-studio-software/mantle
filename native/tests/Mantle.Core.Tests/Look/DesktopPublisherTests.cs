using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// DesktopPublisher (lane r10-look; v2 shape since lane r13-desktop-record): native Mantle publishes look.json <c>desktop</c> as
/// lib/look-desktop.js does (CONTRACT.md sections 2 and 4, the Lane 0 freeze and "Retired on v2" at look-sync dca42dc: no
/// <c>guard</c>, no <c>region</c>, no <c>answers</c>). Temp folders only: every test makes its own look-sync folder, and the
/// LOOK_SYNC_DIR case resolves the folder through DataPaths with a fake APPDATA, so the user's real %APPDATA%\look-sync is
/// never named. The wallpaper is a fake source (no WinRT): a few JPEG-looking bytes.
/// </summary>
public class DesktopPublisherTests
{
    const string GlassJson = "{\"dark\":{\"blurPx\":0,\"dim\":0.3,\"panel\":0},\"light\":{\"blurPx\":0,\"dim\":0.3,\"panel\":0},\"render\":\"picture\"}";
    const string LocalJson = "{\"theme\":\"light\",\"accent\":{\"hue\":30},\"glass\":{\"dark\":{\"blurPx\":8,\"dim\":0.6,\"panel\":-0.1},\"light\":{\"blurPx\":12,\"dim\":0.45,\"panel\":0},\"render\":\"picture\"}}";
    /// The v2 record, exactly (CONTRACT section 2 minus the retired keys): no guard, no guardReason, no region, no answers.
    static readonly string[] RecordKeys = { "id", "type", "title", "file", "ok", "reason", "still", "stillSize", "rev", "by" };

    /// The wallpaper the tests show.
    sealed class FakeSource : IDesktopSource
    {
        public DesktopNow Now = new(new DesktopPick("windows:aaa", "picture", "Snow", @"C:\pics\snow.jpg"), null);
        public byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
        public int W = 64, H = 32;
        /// When set, the still cannot be made: Still throws this message.
        public string? StillThrows;
        public readonly List<string> Calls = new();
        public DesktopNow Current() { lock (Calls) Calls.Add("current"); return Now; }
        public DesktopShot Still(DesktopPick pick)
        {
            lock (Calls) Calls.Add("still " + pick.Id);
            if (StillThrows != null) throw new InvalidOperationException(StillThrows);
            return new DesktopShot(true, (byte[])Jpeg.Clone(), W, H, null);
        }
        public void Show(string id, string title) => Now = new(new DesktopPick(id, "picture", title, @"C:\pics\" + title + ".jpg"), null);
    }

    sealed class Rig : IDisposable
    {
        public readonly TestDir T = new("desk");
        public readonly FakeSource Source = new();
        public readonly ListLog Log = new();
        public readonly LookSync Sync;
        public readonly DesktopPublisher Pub;
        /// Runs between the still and the record (DesktopPublisherOptions.BeforeRecord).
        public Action<string?>? BeforeRecord;
        /// Why Mantle rests now, else null.
        public string? Resting;
        public string Dir => T.Path;
        public Rig(bool syncOn = true, int[]? retry = null, string? dir = null, SafeFile? files = null)
        {
            string d = dir ?? T.Path;
            var f = files ?? new SafeFile(WriteGate.Scratch(T.Path));
            Sync = new LookSync(new LookSyncOptions {
                App = "mantle", Dir = d, Files = f, Enabled = syncOn, Local = () => (JsonObject)JsJson.Parse(LocalJson)!,
                RetryDelays = retry ?? new[] { 5, 10, 20 }, Pid = 4242,
            });
            Pub = new DesktopPublisher(new DesktopPublisherOptions {
                Sync = Sync, Files = f, Source = Source, Log = Log.Line, RestingWhy = () => Resting,
                RetryDelays = retry ?? new[] { 5, 10, 20 }, Pid = 4242, BeforeRecord = s => BeforeRecord?.Invoke(s),
            });
        }
        public JsonObject Doc() => (JsonObject)JsJson.Parse(File.ReadAllText(Path.Combine(Sync.Dir, "look.json")))!;
        public JsonObject Record() => (JsonObject)Doc()["desktop"]!;
        public string StillPath(long rev) => Path.Combine(Sync.Dir, "stills", "wall-" + rev + ".jpg");
        public string[] Stills() => Directory.Exists(Path.Combine(Sync.Dir, "stills"))
            ? Directory.GetFiles(Path.Combine(Sync.Dir, "stills")).Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
        public void Dispose() { Sync.Dispose(); T.Dispose(); }
    }

    static LookSync Other(string dir, string app) => new(new LookSyncOptions {
        App = app, Dir = dir, Files = new SafeFile(WriteGate.Scratch(dir)), Local = () => (JsonObject)JsJson.Parse(LocalJson)!,
        RetryDelays = new[] { 5, 10, 20 },
    });

    static string J(JsonNode? n) => n == null ? "null" : JsJson.Stringify(n, 0);
    static double Num(JsonNode? n) { Assert.True(LookSyncCore.TryNum(n, out var d), "not a number: " + J(n)); return d; }

    [Fact]
    public async Task The_still_is_written_before_the_record_and_the_record_names_it()
    {
        using var rig = new Rig();
        bool stillThere = false, recordThere = true;
        rig.BeforeRecord = still => {
            // between the still's write and the record's: the still is there, the record is not yet
            stillThere = still != null && File.Exists(still);
            string lj = Path.Combine(rig.Dir, "look.json");
            recordThere = File.Exists(lj) && JsJson.Parse(File.ReadAllText(lj)) is JsonObject d && d["desktop"] != null;
        };
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        Assert.True(stillThere, "the still must be on disk before the record is written");
        Assert.False(recordThere, "the record must not be written before the still");
        var rec = rig.Record();
        Assert.Equal(rig.StillPath(2), LookSyncCore.Str(rec["still"]));
        Assert.Equal(rig.Source.Jpeg, File.ReadAllBytes(rig.StillPath(2)));
        Assert.Equal(2, Num(rec["rev"]));
        Assert.Equal(2, Num(rig.Doc()["rev"]));
        Assert.True(rec["ok"]!.GetValue<bool>());
        Assert.Equal("mantle", LookSyncCore.Str(rec["by"]));
        Assert.Equal("[64,32]", J(rec["stillSize"]));
        Assert.Null(LookSyncCore.CheckDesktop(rec));
        Assert.Equal(new[] { "current", "still windows:aaa" }, rig.Source.Calls);   // no picture read back: there is no top band
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("desktop: shared the desktop wallpaper: picture windows:aaa, still 64x32 (rev 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_the_last_3_stills_are_kept_after_each_wallpaper_change()
    {
        using var rig = new Rig();
        var revs = new List<double>();
        foreach (var (id, title) in new[] { ("windows:a", "A"), ("windows:b", "B"), ("windows:c", "C"), ("windows:d", "D") }) {
            rig.Source.Show(id, title);
            var o = await rig.Pub.CheckAsync("Windows' wallpaper changed");
            Assert.Equal("published", o.What);
            revs.Add(o.Rev!.Value);
        }
        Assert.Equal(new double[] { 2, 3, 4, 5 }, revs);
        Assert.Equal(new[] { "wall-3.jpg", "wall-4.jpg", "wall-5.jpg" }, rig.Stills());
        Assert.Equal(rig.StillPath(5), LookSyncCore.Str(rig.Record()["still"]));
        Assert.Equal("D", LookSyncCore.Str(rig.Record()["title"]));
    }

    [Fact]
    public async Task The_still_the_record_names_is_never_pruned_and_a_leftover_tmp_goes()
    {
        using var rig = new Rig();
        string stills = Path.Combine(rig.Dir, "stills");
        Directory.CreateDirectory(stills);
        foreach (var n in new[] { "wall-1.jpg", "wall-3.jpg", "wall-4.jpg", "wall-10.jpg", "wall-9.jpg.77.tmp", "notes.txt" })
            File.WriteAllBytes(Path.Combine(stills, n), new byte[] { 1 });
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal(rig.StillPath(2), o.Still);
        // 10, 4 and 3 are the three highest; 2 is the record's own and stays; 1 goes; the tmp goes; a foreign file stays
        Assert.Equal(new[] { "notes.txt", "wall-10.jpg", "wall-2.jpg", "wall-3.jpg", "wall-4.jpg" }, rig.Stills());
    }

    [Fact]
    public async Task The_record_rev_is_one_past_a_concurrent_writers_and_its_field_is_kept()
    {
        using var rig = new Rig();
        Assert.Equal(2, (await rig.Pub.CheckAsync("start")).Rev);
        using var helm = Other(rig.Dir, "helm");
        double helmRev = -1;
        rig.BeforeRecord = _ => {
            // Helm writes the theme while Mantle is between its still and its record
            var w = helm.PublishAsync("theme", JsonValue.Create("dark")).GetAwaiter().GetResult();
            Assert.True(w.Ok, w.Reason);
            helmRev = w.Rev!.Value;
        };
        rig.Source.Show("windows:b", "B");
        var o = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        Assert.Equal(3, helmRev);
        Assert.Equal("published", o.What);
        Assert.Equal(helmRev + 1, o.Rev);
        var doc = rig.Doc();
        Assert.Equal(helmRev + 1, Num(doc["rev"]));
        Assert.Equal(helmRev + 1, Num(doc["desktop"]!["rev"]));
        Assert.Equal("{\"v\":\"dark\",\"rev\":3,\"by\":\"helm\"}", J(doc["look"]!["theme"]));
        // the still was named for the rev the doc had when it was written; the record still names a file that is there
        Assert.Equal(rig.StillPath(3), LookSyncCore.Str(doc["desktop"]!["still"]));
        Assert.True(File.Exists(rig.StillPath(3)));
    }

    [Fact]
    public async Task A_locked_look_json_is_retried_and_the_record_lands()
    {
        // No polling thread (it raced the retry delays and lost under a busy test run, 2 of 8 cloud runs on 2026-11-25): the
        // write's own hook says when the rename over look.json was refused; at that moment the new copy must be waiting as a
        // tmp, and the held file is released there, so the next try lands. The order is fixed, whatever the machine's load.
        using var d = new TestDir("desk-locked");
        string lj = Path.Combine(d.Path, "look.json");
        FileStream? held = null;
        bool sawRefusedRename = false;
        int refusals = 0;
        var files = new SafeFile(WriteGate.Scratch(d.Path)) {
            OnRenameRefused = (path, attempt, code) => {
                if (!string.Equals(path, lj, StringComparison.OrdinalIgnoreCase) || held == null) return;
                refusals++;
                sawRefusedRename = Directory.EnumerateFiles(d.Path, "look.json.mantle.*.tmp").Any();
                held.Dispose();   // the reader lets go inside the first wait
                held = null;
            },
        };
        using var rig = new Rig(retry: new[] { 50, 100, 200 }, dir: d.Path, files: files);
        Assert.Equal("published", (await rig.Pub.CheckAsync("start")).What);
        rig.BeforeRecord = _ => {
            // a reader that does not share delete (antivirus, a backup): the rename over look.json is refused while it holds it
            held = new FileStream(lj, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try {
            rig.Source.Show("windows:b", "B");
            var o = await rig.Pub.CheckAsync("Windows' wallpaper changed");
            Assert.True(sawRefusedRename, "the new copy should wait as a tmp while look.json is held");
            Assert.Equal(1, refusals);   // refused once while held, then it landed
            Assert.Equal("published", o.What);
            Assert.Equal(3, o.Rev);
            Assert.Equal("B", LookSyncCore.Str(rig.Record()["title"]));
            Assert.Empty(Directory.EnumerateFiles(d.Path, "*.tmp"));
        } finally {
            held?.Dispose();
        }
    }

    [Fact]
    public async Task A_write_that_keeps_failing_never_throws_and_is_said_once()
    {
        using var rig = new Rig();
        Assert.Equal("published", (await rig.Pub.CheckAsync("start")).What);
        string lj = Path.Combine(rig.Dir, "look.json");
        using (new FileStream(lj, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            rig.Source.Show("windows:b", "B");
            var a = await rig.Pub.CheckAsync("Windows' wallpaper changed");
            var b = await rig.Pub.CheckAsync("Windows' wallpaper changed");   // a failed write is tried again at the next check
            Assert.Equal("failed", a.What);
            Assert.Equal("failed", b.What);
            Assert.Contains("(after 4 tries)", a.Reason);
            Assert.Equal(a.Reason, b.Reason);
        }
        Assert.Single(rig.Log.Lines, l => l.StartsWith("desktop: the desktop record could not be written: ", StringComparison.Ordinal));
        Assert.Equal("Snow", LookSyncCore.Str(rig.Record()["title"]));   // the last good record stays
        var c = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        Assert.Equal("published", c.What);
        Assert.Equal("B", LookSyncCore.Str(rig.Record()["title"]));
    }

    [Fact]
    public async Task With_sync_off_the_record_is_still_written_and_no_look_field_is()
    {
        using var rig = new Rig(syncOn: false);
        Assert.False(rig.Sync.Enabled);
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        var doc = rig.Doc();
        Assert.Equal("{}", J(doc["look"]));
        Assert.True(doc["desktop"]!["ok"]!.GetValue<bool>());
        Assert.Equal("mantle", LookSyncCore.Str(doc["desktop"]!["by"]));
        Assert.True(File.Exists(LookSyncCore.Str(doc["desktop"]!["still"])));
    }

    [Fact]
    public async Task Every_look_field_another_app_wrote_is_kept_exactly()
    {
        using var rig = new Rig();
        string theme = "{\"v\":\"dark\",\"rev\":7,\"by\":\"helm\"}";
        string accent = "{\"v\":{\"hue\":212},\"rev\":5,\"by\":\"quartz\"}";
        string glass = "{\"v\":{\"dark\":{\"blurPx\":5,\"dim\":0.3,\"panel\":0,\"dimAuto\":false,\"x\":1},\"light\":{\"blurPx\":0,\"dim\":0.3,\"panel\":0},\"render\":\"picture\",\"more\":[1,2]},\"rev\":8,\"by\":\"quartz\"}";
        File.WriteAllText(Path.Combine(rig.Dir, "look.json"),
            "{\"schema\":1,\"rev\":8,\"writer\":{\"app\":\"quartz\",\"instance\":\"q\",\"pid\":1,\"at\":\"2026-11-11T10:00:00.000Z\"},\"look\":{\"theme\":" + theme +
            ",\"accent\":" + accent + ",\"glass\":" + glass + "},\"future\":{\"a\":1}}");
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal(9, o.Rev);
        var doc = rig.Doc();
        Assert.Equal(theme, J(doc["look"]!["theme"]));
        Assert.Equal(accent, J(doc["look"]!["accent"]));
        Assert.Equal(glass, J(doc["look"]!["glass"]));   // dimAuto and the keys look-sync does not know, inside glass, kept
        Assert.Equal(1, Num(doc["schema"]));
        Assert.Equal(rig.StillPath(9), LookSyncCore.Str(doc["desktop"]!["still"]));
        // a key outside look-sync's schema is not Mantle's to keep: the shared module rebuilds the doc on every write (look-sync.js
        // parse), in every app, so it goes as it goes when Helm or Quartz write (lane note r10-look, "Unknown keys")
        Assert.Null(doc["future"]);
    }

    [Fact]
    public async Task LOOK_SYNC_DIR_moves_the_record_and_the_stills()
    {
        using var t = new TestDir("desk-env");
        string roaming = Path.Combine(t.Path, "Roaming"), shared = Path.Combine(t.Path, "shared");
        var args = LaunchArgs.Parse(new[] { "--test", "--profile-name=r10look" });
        var paths = DataPaths.Resolve(args, k => k == "APPDATA" ? roaming : k == "LOOK_SYNC_DIR" ? shared : null, t.Path);
        Assert.Equal(Path.GetFullPath(shared), paths.LookSyncDir);
        Assert.False(paths.IsUserLook);
        using var rig = new Rig(dir: paths.LookSyncDir, files: new SafeFile(WriteGate.For(paths, args)));
        Assert.Equal(Path.Combine(paths.LookSyncDir, "stills"), rig.Pub.StillsDir);
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        Assert.True(File.Exists(Path.Combine(shared, "look.json")));
        Assert.True(File.Exists(Path.Combine(shared, "stills", "wall-2.jpg")));
        Assert.StartsWith(Path.Combine(paths.LookSyncDir, "stills") + Path.DirectorySeparatorChar, o.Still);
        Assert.False(Directory.Exists(Path.Combine(roaming, "look-sync")));
        Assert.False(File.Exists(Path.Combine(rig.Dir, "look.json")));   // the rig's own folder is not the one the env named
    }

    [Fact]
    public async Task The_record_is_the_v2_shape_no_guard_no_region_no_answers_and_schema_stays_1()
    {
        using var rig = new Rig();
        // an old v1 draft's wallpaperRequest in the file: the shared module's migrate drops it on the write (Lane 0 freeze)
        File.WriteAllText(Path.Combine(rig.Dir, "look.json"),
            "{\"schema\":1,\"rev\":4,\"writer\":{\"app\":\"helm\",\"instance\":\"h\",\"pid\":1,\"at\":\"2026-11-11T10:00:00.000Z\"},\"look\":{}," +
            "\"wallpaperRequest\":{\"rev\":4,\"by\":\"helm\",\"kind\":\"builtin\",\"id\":\"snow-peak\",\"path\":null}}");
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        var rec = rig.Record();
        Assert.Equal(RecordKeys, rec.Select(kv => kv.Key).ToArray());
        foreach (var k in DesktopPublisher.NotPublished) Assert.False(rec.ContainsKey(k), k + " must not be in a v2 native record");
        Assert.DoesNotContain("region", J(rec));                                  // not nested anywhere either
        var doc = rig.Doc();
        Assert.False(doc.ContainsKey("wallpaperRequest"));
        Assert.Equal(1, Num(doc["schema"]));                                      // never bumped
        Assert.Equal(Num(doc["rev"]), Num(rec["rev"]));                           // the field's rev is the doc's
        Assert.Null(LookSyncCore.CheckDesktop(rec));
        // a v2 reader with no top band takes the record as it is: the wash stays at Dim with the guard's reason, never guarded
        var look = (JsonObject)JsJson.Parse("{\"glass\":" + GlassJson + "}")!;
        Assert.False(Wash.Alpha(look, rec, "dark").Guarded);
    }

    [Fact]
    public async Task The_same_wallpaper_again_publishes_nothing_and_no_glass_input_republishes()
    {
        // v2: the record carries no top band, so nothing about the glass is in it; only a wallpaper change publishes
        using var rig = new Rig();
        var first = await rig.Pub.CheckAsync("start");
        var same = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        var again = await rig.Pub.CheckAsync("the glass changed");
        Assert.Equal("same", same.What);
        Assert.Equal("same", again.What);
        Assert.Equal(first.Rev, Num(rig.Record()["rev"]));
        Assert.Equal(new[] { "wall-2.jpg" }, rig.Stills());
        Assert.Equal(1, rig.Source.Calls.Count(c => c.StartsWith("still ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_record_with_a_v1_top_band_from_the_last_run_is_republished_once_without_it()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.Combine(rig.Dir, "stills"));
        string still = rig.StillPath(6);
        File.WriteAllBytes(still, rig.Source.Jpeg);
        // what native wrote before v2 (and Electron still writes): the same wallpaper, a still that is there, and a guard
        File.WriteAllText(Path.Combine(rig.Dir, "look.json"),
            "{\"schema\":1,\"rev\":6,\"writer\":{\"app\":\"mantle\",\"instance\":\"old\",\"pid\":1,\"at\":\"2026-11-24T10:00:00.000Z\"},\"look\":{}," +
            "\"desktop\":{\"id\":\"windows:aaa\",\"type\":\"picture\",\"title\":\"Snow\",\"file\":" + J(JsonValue.Create(@"C:\pics\snow.jpg")) +
            ",\"ok\":true,\"reason\":null,\"still\":" + J(JsonValue.Create(still)) + ",\"stillSize\":[64,32]," +
            "\"guard\":{\"dark\":{\"alpha\":0.4,\"contrast\":5,\"dim\":0.3,\"blurPx\":0,\"region\":{\"topDip\":0,\"heightDip\":100},\"monitor\":[160,200]}},\"rev\":6,\"by\":\"mantle\"}}");
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        Assert.Equal(7, o.Rev);
        var rec = rig.Record();
        Assert.Equal(RecordKeys, rec.Select(kv => kv.Key).ToArray());
        // and the next start adopts that clean record
        var next = new DesktopPublisher(new DesktopPublisherOptions { Sync = rig.Sync, Files = new SafeFile(WriteGate.Scratch(rig.Dir)), Source = rig.Source, Log = rig.Log.Line });
        Assert.Equal("adopted", (await next.CheckAsync("start")).What);
        Assert.Equal(7, Num(rig.Record()["rev"]));
    }

    [Fact]
    public async Task During_a_game_rest_a_check_that_would_share_ok_false_keeps_the_last_record()
    {
        using var rig = new Rig();
        var first = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", first.What);
        string before = J(rig.Record());
        rig.Resting = "VALORANT.exe runs";
        // no picture now (the wallpaper unloaded) and, after that, a still that cannot be made: neither is shared while resting
        rig.Source.Now = DesktopNow.None("Windows' wallpaper file is missing");
        var a = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        rig.Source.Show("windows:bbb", "B");
        rig.Source.StillThrows = "the file is in use";
        var b = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        Assert.Equal("kept", a.What);
        Assert.Equal("kept", b.What);
        Assert.Equal(before, J(rig.Record()));                                    // the same record, the same rev
        Assert.Equal(new[] { "wall-2.jpg" }, rig.Stills());
        Assert.Contains(rig.Log.Lines, l => l.Contains("while Mantle rests (VALORANT.exe runs)", StringComparison.Ordinal));
        // the rest ends: the next check says what is true
        rig.Resting = null;
        var c = await rig.Pub.CheckAsync("the rest ended");
        Assert.Equal("published", c.What);
        Assert.False(rig.Record()["ok"]!.GetValue<bool>());
        Assert.Equal("the still could not be made (the file is in use)", LookSyncCore.Str(rig.Record()["reason"]));
    }

    [Fact]
    public async Task A_rest_never_holds_back_a_new_picture_that_has_a_still()
    {
        using var rig = new Rig();
        await rig.Pub.CheckAsync("start");
        rig.Resting = "game mode is on";
        rig.Source.Show("windows:ccc", "C");
        var o = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        Assert.Equal("published", o.What);
        Assert.Equal("C", LookSyncCore.Str(rig.Record()["title"]));
        Assert.True(rig.Record()["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_torn_look_json_is_healed_and_the_record_lands_valid_naming_a_still_that_is_there()
    {
        using var rig = new Rig();
        // a half-written file (another writer died mid-write, outside the tmp + rename road)
        File.WriteAllText(Path.Combine(rig.Dir, "look.json"), "{\"schema\":1,\"rev\":5,\"writer\":{\"app\":\"hel");
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        var doc = rig.Doc();                                                      // parses: the file is whole again
        var rec = (JsonObject)doc["desktop"]!;
        Assert.Null(LookSyncCore.CheckDesktop(rec));
        Assert.Equal(Num(doc["rev"]), Num(rec["rev"]));
        Assert.True(File.Exists(LookSyncCore.Str(rec["still"])));
        Assert.Equal(1, Num(doc["schema"]));
        Assert.Empty(Directory.EnumerateFiles(rig.Dir, "*.tmp"));
    }

    [Fact]
    public async Task A_record_from_the_last_run_that_still_fits_is_adopted_not_republished()
    {
        using var rig = new Rig();
        var first = await rig.Pub.CheckAsync("start");
        var again = new DesktopPublisher(new DesktopPublisherOptions {
            Sync = rig.Sync, Files = new SafeFile(WriteGate.Scratch(rig.Dir)), Source = rig.Source, Log = rig.Log.Line,
        });
        var o = await again.CheckAsync("start");
        Assert.Equal("adopted", o.What);
        Assert.Equal(first.Rev, o.Rev);
        Assert.Equal(first.Rev, Num(rig.Record()["rev"]));
    }

    [Fact]
    public async Task No_picture_publishes_ok_false_with_the_reason()
    {
        using var rig = new Rig();
        rig.Source.Now = DesktopNow.None("Windows shows a plain colour, not a picture");
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        var rec = rig.Record();
        Assert.False(rec["ok"]!.GetValue<bool>());
        Assert.Equal("Windows shows a plain colour, not a picture", LookSyncCore.Str(rec["reason"]));
        Assert.Null(rec["still"]);
        Assert.Equal(RecordKeys, rec.Select(kv => kv.Key).ToArray());   // ok:false is the same shape: no guardReason any more
        Assert.Null(LookSyncCore.CheckDesktop(rec));
        Assert.Empty(rig.Stills());
    }

    [Fact]
    public async Task A_folder_the_gate_refuses_gets_no_still_and_no_record_and_is_said_once()
    {
        using var rig = new Rig(files: new SafeFile(WriteGate.Never("this run is not --live")));
        var a = await rig.Pub.CheckAsync("start");
        var b = await rig.Pub.CheckAsync("Windows' wallpaper changed");
        Assert.Equal("failed", a.What);
        Assert.Equal("the desktop record is not written: this run is not --live", a.Reason);
        Assert.Equal(a.Reason, b.Reason);
        Assert.DoesNotContain(rig.Source.Calls, c => c.StartsWith("still ", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(rig.Dir, "look.json")));
        Assert.Empty(rig.Stills());
        Assert.Single(rig.Log.Lines, l => l.Contains("not --live", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080, 1920, 1080)]   // twice the screen: scaled down to cover it
    [InlineData(1000, 500, 1920, 1080, 1000, 500)]     // smaller than the screen: never upscaled
    [InlineData(8000, 2000, 1920, 1080, 4320, 1080)]   // a wide picture covers the height
    [InlineData(4000, 1000, 1920, 1080, 4000, 1000)]   // covering would need an upscale: its own size
    [InlineData(5, 10, 1, 5, 3, 5)]                    // 2.5 rounds up, as Math.round
    [InlineData(800, 600, 0, 0, 800, 600)]             // no screen size: the picture's own
    public void The_still_is_the_cover_size_of_the_screen_at_most(int iw, int ih, int sw, int sh, int w, int h)
        => Assert.Equal((w, h), DesktopPublisher.CoverSize(iw, ih, sw, sh));

    [Fact]
    public async Task A_source_that_throws_is_a_reason_not_a_crash()
    {
        using var rig = new Rig();
        rig.Source.StillThrows = "boom";
        var o = await rig.Pub.CheckAsync("start");
        Assert.Equal("published", o.What);
        var rec = rig.Record();
        Assert.False(rec["ok"]!.GetValue<bool>());                                // ok:false with the reason, never a crash
        Assert.Equal("the still could not be made (boom)", LookSyncCore.Str(rec["reason"]));
        Assert.Null(rec["still"]);
        Assert.Empty(rig.Stills());
        Assert.Single(rig.Log.Lines, l => l.Contains("boom", StringComparison.Ordinal));
    }
}
