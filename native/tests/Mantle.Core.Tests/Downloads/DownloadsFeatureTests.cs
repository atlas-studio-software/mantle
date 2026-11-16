using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.Features.Downloads;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.Downloads;

/// <summary>
/// [Feature("downloads")] (lib/features/downloads.js), the service: the read, seenAt, the watch's debounce, the missing
/// folder's retry, tokens, the --test dry runs and privacy. The folder is a temp folder (Host.Folder), never the user's
/// Downloads; the watch is a fake whose events the test fires.
/// </summary>
public class DownloadsFeatureTests
{
    sealed class W
    {
        public Rig R = null!; public string Dir = "";
        public Action<string?, bool>? Fire; public Action<Exception>? Fail; public int Watches, Closed;
        public string? Folder;
        public List<string> Opened = new();
        public async Task<CommandResultish> Call(string verb, JsonObject? a = null) { var r = await R.Bus.CallAsync("downloads." + verb, a ?? new JsonObject()); return new(r.Ok, r.Ok ? r.Result : default, r.Error); }
        public JsonElement State => (JsonElement)R.Events.Published.Last(p => p.Name == DownloadsFeature.StateEvent).Payload!;
        public string Seen => R.Stores.Get("features\\downloads.json").Snapshot().TryGetProperty("seenAt", out var v) ? v.ToString() : "";
        public void Put(string name, long ageMs, bool dir = false)
        {
            var p = Path.Combine(Dir, name);
            if (dir) Directory.CreateDirectory(p); else File.WriteAllText(p, "x");
            var t = DateTimeOffset.FromUnixTimeMilliseconds(R.Time.GetUtcNow().ToUnixTimeMilliseconds() - ageMs).UtcDateTime;
            if (dir) { Directory.SetCreationTimeUtc(p, t); Directory.SetLastWriteTimeUtc(p, t); } else { File.SetCreationTimeUtc(p, t); File.SetLastWriteTimeUtc(p, t); }
        }
    }
    public readonly record struct CommandResultish(bool Ok, JsonElement Result, string? Error);

    sealed class Handle : IDisposable { readonly Action _a; public Handle(Action a) { _a = a; } public void Dispose() => _a(); }

    static void T(bool test, Func<W, Task> body, Action<W>? before = null) => HostLoop.Run(async (_, time) =>
    {
        var w = new W { R = new Rig(time, test), Dir = Path.Combine(Path.GetTempPath(), "mantle-dl-test-" + Guid.NewGuid().ToString("N")[..8]) };
        Directory.CreateDirectory(w.Dir);
        w.Folder = w.Dir;
        // real files carry real times: the manual clock starts at the wall clock so "new" and ages line up
        time.Advance(DateTimeOffset.UtcNow - time.GetUtcNow());
        try
        {
            w.R.Platform.Start();
            before?.Invoke(w);
            var h = new DownloadsFeature.Host(Folder: () => w.Folder, Clock: new HostClock(time),
                Watch: (dir, ev, err) => { w.Watches++; w.Fire = ev; w.Fail = err; return new Handle(() => w.Closed++); },
                Open: p => { w.Opened.Add("open " + p); return null; }, Reveal: p => { w.Opened.Add("reveal " + p); return null; });
            var a = typeof(DownloadsFeature).GetCustomAttribute<FeatureAttribute>()!;
            w.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => new DownloadsFeature(h)) });
            await w.R.Host.Installed;
            await Settle(w);
            await body(w);
        }
        finally { try { Directory.Delete(w.Dir, true); } catch (IOException) { } }
    });

    /// The read runs off the host thread: give it a moment, then let the host's queue run.
    static async Task Settle(W w) { for (int i = 0; i < 20; i++) { await Task.Delay(15); await Task.Yield(); } }

    [Fact]
    public void The_module_fields_are_Electrons()
    {
        var a = typeof(DownloadsFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("downloads", "Downloads shelf (bar)", true, true), (a.Name, a.Label, a.DefaultOn, a.Rest));
        Assert.Empty(a.Chords);
    }

    [Fact]
    public void The_first_run_marks_what_is_there_seen_and_a_later_arrival_is_new_until_seen() => T(false, async w =>
    {
        Assert.NotEqual("", w.Seen);                                   // the first run saved seenAt = now
        Assert.Equal(0, w.State.GetProperty("newCount").GetInt32());
        Assert.Equal(1, w.State.GetProperty("count").GetInt32());
        w.R.Time.AdvanceMs(5000);
        w.Put("report.pdf", 0);
        w.Fire!("report.pdf", false);
        w.R.Time.AdvanceMs(DownloadsFeature.DebounceMs);
        await Settle(w);
        Assert.Equal(1, w.State.GetProperty("newCount").GetInt32());
        var v = await w.Call("list", new JsonObject { ["cached"] = true });
        Assert.True(v.Ok);
        var items = v.Result.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(new[] { "report.pdf", "old.txt" }, items.Select(i => i.GetProperty("name").GetString()));
        Assert.True(items[0].GetProperty("fresh").GetBoolean());
        Assert.False(items[1].GetProperty("fresh").GetBoolean());
        Assert.StartsWith("d", items[0].GetProperty("item").GetString());
        Assert.True((await w.Call("seen")).Ok);
        Assert.Equal(0, w.State.GetProperty("newCount").GetInt32());
        // privacy: counts only in the log, never a name or the folder
        Assert.Contains(w.R.Lines, l => l.Contains("2 items, 1 new [watch]"));
        Assert.DoesNotContain(w.R.Lines, l => l.Contains("report") || l.Contains(w.Dir, StringComparison.OrdinalIgnoreCase));
    }, before: w => w.Put("old.txt", 60000));

    [Fact]
    public void A_burst_of_events_reads_once_after_quiet_but_never_waits_past_four_seconds_and_partial_writes_are_ignored() => T(false, async w =>
    {
        int Reads() => w.R.Lines.Count(l => l.EndsWith("[watch]", StringComparison.Ordinal));
        w.Fire!("a.crdownload", true);                                  // a browser writing its partial: nothing
        w.R.Time.AdvanceMs(5000); await Settle(w);
        Assert.Equal(0, Reads());
        for (int i = 0; i < 10; i++) { w.Put($"f{i}.txt", 0); w.Fire!($"f{i}.txt", false); w.R.Time.AdvanceMs(500); }
        await Settle(w);
        Assert.Equal(1, Reads());                                        // 5 s of events 500 ms apart: one read at the 4 s cap
        w.R.Time.AdvanceMs(DownloadsFeature.DebounceMs); await Settle(w);
        Assert.Equal(2, Reads());                                        // ... and one after the quiet
        Assert.True(w.State.GetProperty("watching").GetBoolean());
        Assert.Equal(1, w.Watches);
    });

    [Fact]
    public void A_missing_folder_is_said_retried_every_two_minutes_and_watched_again_when_back() => T(false, async w =>
    {
        var st = w.State;
        Assert.False(st.GetProperty("ok").GetBoolean());
        Assert.Equal("missing", st.GetProperty("state").GetString());
        Assert.Equal("the Downloads folder does not exist (it was moved, renamed or deleted)", st.GetProperty("reason").GetString());
        var v = await w.Call("list", new JsonObject { ["cached"] = true });
        Assert.Equal(120000, v.Result.GetProperty("retryMs").GetInt32());
        Assert.Equal(0, w.Watches);
        Directory.CreateDirectory(w.Folder!);
        w.R.Time.AdvanceMs(119000); await Settle(w);
        Assert.Equal("missing", w.State.GetProperty("state").GetString());
        w.R.Time.AdvanceMs(1000); await Settle(w);
        Assert.Equal("ok", w.State.GetProperty("state").GetString());
        Assert.Equal(1, w.Watches);
    }, before: w => w.Folder = Path.Combine(w.Dir, "gone"));

    [Fact]
    public void A_watch_that_stops_reads_at_once_and_says_new_downloads_show_when_the_panel_opens() => T(false, async w =>
    {
        w.Fail!(new IOException("the network name is no longer available"));
        await Settle(w);
        Assert.False(w.State.GetProperty("watching").GetBoolean());
        Assert.Equal(DownloadsFeature.WatchWords, w.State.GetProperty("watchReason").GetString());
        Assert.Contains(w.R.Lines, l => l.Contains("the folder watch stopped (IOException)"));
        Assert.Equal(1, w.Closed);
        Assert.Equal(1, w.Watches);                                      // not re-armed by the read its failure asked for
        w.R.Time.AdvanceMs(120000); await Settle(w);
        Assert.Equal(2, w.Watches);                                      // the retry re-arms it
    });

    [Fact]
    public void A_test_run_opens_reveals_and_opens_the_folder_as_dry_runs_and_a_stale_row_is_refused() => T(true, async w =>
    {
        var v = await w.Call("list");
        var item = v.Result.GetProperty("items")[0].GetProperty("item").GetString();
        var o = await w.Call("open", new JsonObject { ["item"] = item });
        Assert.True(o.Ok);
        Assert.Equal("test run: would open it with its default app", o.Result.GetProperty("reason").GetString());
        Assert.Equal("test run: would show it selected in Explorer", (await w.Call("reveal", new JsonObject { ["item"] = item })).Result.GetProperty("reason").GetString());
        Assert.Equal("test run: would open the Downloads folder", (await w.Call("folder")).Result.GetProperty("reason").GetString());
        Assert.Empty(w.Opened);
        var stale = await w.Call("open", new JsonObject { ["item"] = "d999" });
        Assert.False(stale.Ok);
        Assert.Equal("that row is out of date (the list changed); it has been refreshed", stale.Error);
        File.Delete(Path.Combine(w.Dir, "song.mp3"));
        var gone = await w.Call("open", new JsonObject { ["item"] = item });
        Assert.Equal("it is no longer in Downloads (moved or deleted); the list has been refreshed", gone.Error);
        // the same file keeps its token across reads
        w.Put("song.mp3", 1000);
        var a = (await w.Call("list")).Result.GetProperty("items")[0].GetProperty("item").GetString();
        var b = (await w.Call("list")).Result.GetProperty("items")[0].GetProperty("item").GetString();
        Assert.Equal(a, b);
    }, before: w => w.Put("song.mp3", 1000));

    [Fact]
    public void A_live_run_opens_and_reveals_through_Windows_and_hidden_and_partial_files_are_never_rows() => T(false, async w =>
    {
        var v = await w.Call("list");
        var names = v.Result.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "photo.jpg" }, names);
        Assert.Equal(1, v.Result.GetProperty("partial").GetInt32());
        Assert.Equal(1, w.State.GetProperty("partial").GetInt32());
        var item = v.Result.GetProperty("items")[0].GetProperty("item").GetString();
        Assert.True((await w.Call("open", new JsonObject { ["item"] = item })).Ok);
        Assert.True((await w.Call("reveal", new JsonObject { ["item"] = item })).Ok);
        Assert.Equal(new[] { "open " + Path.Combine(w.Dir, "photo.jpg"), "reveal " + Path.Combine(w.Dir, "photo.jpg") }, w.Opened);
    }, before: w => {
        w.Put("photo.jpg", 1000); w.Put("big.iso.crdownload", 0); w.Put("desktop.ini", 0); w.Put("secret.txt", 0);
        File.SetAttributes(Path.Combine(w.Dir, "secret.txt"), FileAttributes.Hidden);
    });

    [Fact]
    public void Windows_open_errors_are_put_in_words()
    {
        Assert.Equal("Windows has no app set for this kind of file. Use Show in folder, then right-click it and choose Open with", DownloadsFeature.OpenWhy("No application is associated with the specified file for this operation"));
        Assert.Equal("Windows did not let Mantle open it (access denied)", DownloadsFeature.OpenWhy("Access is denied."));
        Assert.Equal("it is no longer in Downloads (moved or deleted)", DownloadsFeature.OpenWhy("The system cannot find the file specified."));
        Assert.Equal("the disk is busy", DownloadsFeature.OpenWhy("the disk is busy (0x80070015)"));
    }

    [Fact]
    public void Switched_off_it_pushes_off_closes_the_watch_and_commands_say_so() => T(false, async w =>
    {
        w.R.Host.SetEnabled("downloads", false, "test");
        await Settle(w);
        var st = w.State;
        Assert.False(st.GetProperty("on").GetBoolean());
        Assert.Equal(DownloadsFeature.Off, st.GetProperty("reason").GetString());
        Assert.Equal(1, w.Closed);
    });

    [Fact]
    public void A_future_saved_seenAt_is_reset_with_a_line_and_an_unsaveable_one_says_the_dot_may_come_back() => T(false, async w =>
    {
        Assert.Contains(w.R.Lines, l => l.Contains("the saved seenAt was not a usable time"));
        var v = await w.Call("seen");
        Assert.Equal("Mantle could not save when you last looked here, so the new-download dot may come back after a restart.", v.Result.GetProperty("note").GetString());
    }, before: w => {
        var s = w.R.Stores.Get("features\\downloads.json");
        s.SetAsync("seenAt", JsonValue.Create(1e15)).AsTask().Wait();
        s.FailWrites = "disk full";
    });
}
