using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Look;
using Xunit;

namespace Mantle.Core.Tests.Look;

// core-6.md: the theme is told the moment the pick is decided and theme.json is written behind it (in order, still
// flushed to disk before its rename), with a failed save still reported: in the view's note, in the log, and to the
// listeners again. Electron (lib/theme.js) saves first, with no flush, then tells.
public class ThemeSaveBehindTests
{
    sealed class Rig : IDisposable
    {
        public readonly TestDir Dir = new("theme-behind");
        public readonly ListLog Log = new();
        public readonly Stores Stores;
        public readonly List<Action> Queue = new();   // the theme's one thread, pumped by the test
        public readonly List<ThemeView> Told = new();
        public Rig() => Stores = new Stores(Dir.Path, new SafeFile(WriteGate.Scratch(Dir.Path)), Log);
        public JsonStore Store => (JsonStore)Stores.Open("theme.json");
        public ThemeCore Make()
        {
            var t = new ThemeCore(Store, () => new WindowsMode("light", null), null, Log, work => { lock (Queue) Queue.Add(work); });
            t.On(v => Told.Add(v));
            return t;
        }
        public void Pump() { Action[] a; lock (Queue) { a = Queue.ToArray(); Queue.Clear(); } foreach (var w in a) w(); }
        public async Task Landed(ThemeCore t)
        {
            await Store.Behind.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 200 && !t.LastSaved.IsCompleted; i++) { Pump(); await Task.Delay(5); }
            Assert.True(t.LastSaved.IsCompleted, "the save's outcome never came back");
        }
        public string File => Dir.File("theme.json");
        public void Dispose() { Stores.Dispose(); Dir.Dispose(); }
    }

    [Fact]
    public async Task The_listeners_hear_the_pick_before_its_save_has_come_back_and_the_file_lands_after()
    {
        using var r = new Rig();
        var t = r.Make();
        var res = t.Set(JsonValue.Create("dark"));
        Assert.True(res.Ok);
        Assert.Single(r.Told);                       // told inside Set, before anything was saved or pumped
        Assert.Equal("dark", r.Told[0].Theme);
        Assert.NotNull(res.Saved);
        Assert.False(res.Saved!.IsCompleted);        // its outcome waits for the theme's own thread
        await r.Landed(t);
        Assert.Equal("{\n  \"mode\": \"dark\"\n}", File.ReadAllText(r.File));
        Assert.False(File.Exists(r.File + ".tmp"));
        Assert.Single(r.Told);                       // a good save with no note to clear tells nobody again
        Assert.Null(t.Get().Note);
    }

    [Fact]
    public async Task Picks_in_a_row_are_saved_in_order_and_the_last_one_is_what_stays()
    {
        using var r = new Rig();
        var t = r.Make();
        foreach (var m in new[] { "dark", "light", "system", "dark", "light" }) Assert.True(t.Set(JsonValue.Create(m)).Ok);
        Assert.Equal(new[] { "dark", "light", "system", "dark", "light" }, r.Told.Select(v => v.Mode));
        // a write landing while a newer one waits never takes the pick back
        Assert.False(t.FileChanged());
        await r.Landed(t);
        Assert.Equal(0, r.Store.WritesBehind);
        Assert.Contains("\"light\"", File.ReadAllText(r.File));
        Assert.Equal("light", t.Mode);
        Assert.False(t.FileChanged());               // the file now says what Mantle says
        using var again = new Stores(r.Dir.Path, new SafeFile(WriteGate.Scratch(r.Dir.Path)), r.Log);
        Assert.Equal("light", new ThemeCore((JsonStore)again.Open("theme.json"), () => new WindowsMode("light", null), null, r.Log).Mode);
    }

    [Fact]
    public async Task A_save_that_fails_behind_the_tell_is_still_the_note_the_log_line_and_a_second_tell()
    {
        using var r = new Rig();
        var t = r.Make();
        Task saved;
        // the tmp copy is held open, so the write behind fails (EBUSY) after the listeners were told
        using (new FileStream(r.File + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None)) {
            var res = t.Set(JsonValue.Create("dark"));
            Assert.True(res.Ok);
            Assert.Equal("dark", t.Theme);
            saved = res.Saved!;
            await r.Store.Behind.WaitAsync(TimeSpan.FromSeconds(10));
        }
        await r.Landed(t);
        Assert.True(saved.IsCompleted);
        Assert.Equal("dark", t.Theme);                // the pick still applies
        Assert.Equal(2, r.Told.Count);                 // told at once, then again with the note
        Assert.Null(r.Told[0].Note);
        Assert.StartsWith("Dark is on now, but it could not be saved (theme.json could not be saved (the new copy could not be written (", r.Told[1].Note);
        Assert.EndsWith("), so Mantle goes back to the last saved appearance at its next start", t.Get().Note);
        Assert.Contains(r.Log.Lines, l => l.StartsWith("theme: Dark is on now, but it could not be saved", StringComparison.Ordinal));
        // the next good save clears the note and says so
        t.Set(JsonValue.Create("light"));
        await r.Landed(t);
        Assert.Null(t.Get().Note);
        Assert.Null(r.Told[^1].Note);
    }

    [Fact]
    public void What_the_gate_refuses_is_known_at_once_and_is_in_the_first_tell()
    {
        using var dir = new TestDir("theme-behind-never");
        var log = new ListLog();
        using var stores = new Stores(dir.Path, new SafeFile(WriteGate.Never("EPERM")), log);
        var told = new List<ThemeView>();
        var t = new ThemeCore((JsonStore)stores.Open("theme.json"), () => new WindowsMode("light", null), null, log, _ => throw new InvalidOperationException("nothing is behind"));
        t.On(told.Add);
        var res = t.Set(JsonValue.Create("dark"));
        Assert.Null(res.Saved);
        Assert.Equal("Dark is on now, but it could not be saved (EPERM), so Mantle goes back to the last saved appearance at its next start", res.View!.Note);
        Assert.Equal(res.View.Note, Assert.Single(told).Note);
    }

    [Fact]
    public async Task A_store_write_behind_keeps_the_in_memory_document_and_a_sync_write_waits_its_turn()
    {
        using var r = new Rig();
        var s = r.Store;
        var a = s.UpdateBehind(d => d["mode"] = "dark");
        Assert.True(s.TryGet("mode", out var now));
        Assert.Equal("dark", now.GetString());         // the truth at once, saved or not
        var b = s.UpdateAsync(d => d["mode"] = "light");   // behind the first, never before it
        Assert.True((await a).Ok);
        Assert.True((await b).Ok);
        await s.Behind;
        Assert.Contains("\"light\"", File.ReadAllText(r.File));
        Assert.Equal(0, s.WritesBehind);
    }
}
