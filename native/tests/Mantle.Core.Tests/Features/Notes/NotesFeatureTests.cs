using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Features.Notes;
using Mantle.Core.Keys;
using Xunit;

namespace Mantle.Core.Tests.Features.Notes;

// lane r10-notes: [Feature("notes")] (lib/features/notes.js) on the feature host's test rig: a fake sidecar, a manual
// clock, the notes folder under a fresh temp root and a scratch write gate. Nothing here touches Documents.
// The LOSS rows (saved on close, on quit, on session end, on blur; at most one debounce of typing at risk) are each
// their own test, named Loss_...
public sealed class NotesFeatureTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "mantle-notesf-test-" + Guid.NewGuid().ToString("N")[..8]);
    const string Secret = "my bank PIN is 4321";

    public NotesFeatureTests()
    {
        NotesFeature.ResetModule();
        Directory.CreateDirectory(Path.Combine(_root, "data"));
    }

    public void Dispose()
    {
        NotesFeature.ResetModule();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string Data => Path.Combine(_root, "data");
    string Dir => Path.Combine(Data, "Mantle Notes");
    string NotesMd => Path.Combine(Dir, "notes.md");
    string OnDisk() => File.Exists(NotesMd) ? File.ReadAllText(NotesMd) : "";

    sealed class K { public Rig R = null!; public NotesFeature F => NotesFeature.Current!; }

    void T(Func<K, Task> body, bool surface = true, string? dir = null) => HostLoop.Run(async (_, time) =>
    {
        var k = new K { R = new Rig(time, test: false) };
        k.R.HookTakesAll();
        k.R.Platform.Start();
        var h = new NotesFeature.Host(Dir: dir ?? Dir, Safe: new SafeFile(WriteGate.Scratch(Data)), Clock: new HostClock(time),
            LocalNow: () => new DateTime(2026, 11, 24, 15, 0, 0), RescueDir: Data, Sleep: _ => { }, Surface: surface);
        var a = typeof(NotesFeature).GetCustomAttribute<FeatureAttribute>()!;
        k.R.Host.Load(new[] { FeatureDescriptor.Of(a, () => new NotesFeature(h)) });
        await k.R.Host.Installed;
        await k.R.Host.ApplyLiveAsync();
        await body(k);
    });

    static async Task<JsonElement> Call(K k, string verb, JsonObject? args = null)
    {
        var r = await k.R.Bus.CallAsync("notes." + verb, args);
        Assert.True(r.Ok, r.Error);
        return r.Result;
    }

    static string Type(K k, string text) { string id = k.F.Pad.Active.Id; Assert.True(k.F.Edit(id, text)); return id; }

    [Fact]
    public void The_feature_is_notes_on_by_default_rests_for_games_and_claims_Win_Alt_N()
    {
        var a = typeof(NotesFeature).GetCustomAttribute<FeatureAttribute>()!;
        Assert.Equal(("notes", "Quick notes (Win+Alt+N)", true, true), (a.Name, a.Label, a.DefaultOn, a.Rest));
        Assert.Equal(new[] { "Win+Alt+N" }, a.Chords);
        Assert.True(ChordTable.IsChord("Win+Alt+N"));
    }

    [Fact]
    public void Win_Alt_N_opens_the_closed_pad_with_the_caret_in_the_text_and_closes_the_open_one() => T(async k =>
    {
        Assert.True(k.R.Hub.Piece("notes").Ok);
        Assert.Contains("Win+Alt+N", k.R.Host.Wanted().Chords);
        Assert.True(k.R.Host.FireChord("Win+Alt+N").Delivered);
        Assert.True(k.F.IsOpen);
        Assert.Contains(k.R.Events.Published, p => p.Name == NotesFeature.OpenEvent && ((JsonElement)p.Payload!).GetProperty("focus").GetBoolean());
        k.R.Host.FireChord("Win+Alt+N");
        Assert.False(k.F.IsOpen);
        await Task.Yield();
    });

    [Fact]
    public void Switched_off_the_chord_goes_back_to_Windows_an_open_pad_closes_and_open_is_refused_in_words() => T(async k =>
    {
        await Call(k, "open");
        Assert.True(k.F.IsOpen);
        k.R.Host.SetEnabled("notes", false, "the tray");
        Assert.False(k.F.IsOpen);
        Assert.DoesNotContain("Win+Alt+N", k.R.Host.Wanted().Chords);
        Assert.False(k.R.Host.FireChord("Win+Alt+N").Delivered);
        var r = await k.R.Bus.CallAsync("notes.open");
        Assert.False(r.Ok);
        Assert.Equal(NotesFeature.OffWords, r.Error);
    });

    [Fact]
    public void Without_a_pad_surface_the_feature_is_unhealthy_and_Win_Alt_N_stays_Windows_own() => T(async k =>
    {
        var p = k.R.Hub.Piece("notes");
        Assert.False(p.Ok);
        Assert.Equal(NotesFeature.NoSurface, p.Reason);
        Assert.DoesNotContain("Win+Alt+N", k.R.Host.Wanted().Chords);
        Assert.False(k.R.Host.FireChord("Win+Alt+N").Delivered);
        await Task.Yield();
    }, surface: false);

    [Fact]
    public void While_resting_the_chord_is_Windows_own() => T(async k =>
    {
        Assert.Equal(new[] { "notes" }, await k.R.Host.RestAllAsync("VALORANT runs"));
        Assert.DoesNotContain("Win+Alt+N", k.R.Host.Wanted().Chords);
        Assert.False(k.R.Host.FireChord("Win+Alt+N").Delivered);
        await k.R.Host.WakeAllAsync("VALORANT exited");
        Assert.Contains("Win+Alt+N", k.R.Host.Wanted().Chords);
    });

    [Fact]
    public void A_toggle_from_a_click_that_already_closed_the_pad_stays_closed_and_the_chord_is_exempt_from_the_grace() => T(async k =>
    {
        Assert.Equal("already closed by the click that pressed the icon", (await Call(k, "toggle", new JsonObject { ["wasOpen"] = true })).GetProperty("why").GetString());
        await Call(k, "toggle");
        k.F.Blurred(closes: true);
        Assert.False(k.F.IsOpen);
        Assert.Equal("closed by the same click", (await Call(k, "toggle", new JsonObject { ["via"] = "icon" })).GetProperty("why").GetString());
        k.R.Host.FireChord("Win+Alt+N");
        Assert.True(k.F.IsOpen);
    });

    [Fact]
    public void Loss_typing_is_saved_400_ms_after_it_stops() => T(async k =>
    {
        await Call(k, "open");
        Type(k, "first words");
        k.R.Time.AdvanceMs(300);
        Assert.DoesNotContain("first words", OnDisk(), StringComparison.Ordinal);
        k.R.Time.AdvanceMs(150);
        Assert.Equal("<!-- mantle:note -->\nfirst words\n", OnDisk());
        Assert.False(k.F.Pad.Dirty);
    });

    [Fact]
    public void Loss_a_long_burst_is_on_disk_3_s_after_its_first_key_so_a_crash_costs_at_most_that() => T(async k =>
    {
        await Call(k, "open");
        string id = k.F.Pad.Active.Id;
        string text = "";
        for (int i = 0; i < 30; i++) { text += "x"; k.F.Edit(id, text); k.R.Time.AdvanceMs(100); }
        Assert.Contains("<!-- mantle:note -->\nxxx", OnDisk(), StringComparison.Ordinal);   // saved mid-burst, by the 3 s cap
    });

    [Fact]
    public void Loss_closing_the_pad_saves_at_once() => T(async k =>
    {
        await Call(k, "open");
        Type(k, "typed then closed");
        await Call(k, "close");
        Assert.Equal("<!-- mantle:note -->\ntyped then closed\n", OnDisk());
    });

    [Fact]
    public void Loss_losing_focus_saves_at_once() => T(async k =>
    {
        await Call(k, "open");
        Type(k, "typed then clicked away");
        k.F.Blurred(closes: false);
        Assert.Equal("<!-- mantle:note -->\ntyped then clicked away\n", OnDisk());
    });

    [Fact]
    public void Loss_quitting_saves_text_typed_inside_the_debounce() => T(async k =>
    {
        await Call(k, "open");
        Type(k, "typed just before quit");
        await k.R.Host.StopAllAsync("quit");
        Assert.Equal("<!-- mantle:note -->\ntyped just before quit\n", OnDisk());
    });

    [Fact]
    public void Loss_a_session_end_saves_text_typed_inside_the_debounce() => T(async k =>
    {
        await Call(k, "open");
        Type(k, "typed as Windows signs out");
        await k.R.Host.StopAllAsync("Windows is ending the session");
        Assert.Equal("<!-- mantle:note -->\ntyped as Windows signs out\n", OnDisk());
    });

    [Fact]
    public void Loss_a_quit_that_cannot_save_writes_a_rescue_file_beside_the_settings() => T(async k =>
    {
        await Call(k, "open");
        Type(k, Secret);
        await k.R.Host.StopAllAsync("quit");
        var rescue = NotesStore.ListRescue(Data);
        Assert.Single(rescue);
        Assert.Equal("<!-- mantle:note -->\n" + Secret + "\n", File.ReadAllText(rescue[0]));
        Assert.DoesNotContain(k.R.Lines, l => l.Contains(Secret, StringComparison.Ordinal));
    }, dir: Path.Combine(Path.GetTempPath(), "mantle-notesf-outside-" + Guid.NewGuid().ToString("N")[..8]));   // outside the gate: every save refused

    [Fact]
    public void Loss_unsaved_notes_at_a_rest_are_carried_and_saved_at_wake() => T(async k =>
    {
        await Call(k, "open");
        File.WriteAllText(Dir, "a file in the way of the folder");   // the save at rest fails
        Type(k, "typed as the game started");
        await k.R.Host.RestAllAsync("VALORANT runs");
        Assert.True(NotesFeature.Carrying);
        File.Delete(Dir);
        await k.R.Host.WakeAllAsync("VALORANT exited");
        Assert.False(NotesFeature.Carrying);
        Assert.Equal("<!-- mantle:note -->\ntyped as the game started\n", OnDisk());
    });

    [Fact]
    public void A_failed_save_keeps_the_text_says_why_once_and_tries_again_every_5_s() => T(async k =>
    {
        await Call(k, "open");
        File.WriteAllText(Dir, "a file in the way of the folder");
        Type(k, "kept in the pad");
        k.R.Time.AdvanceMs(450);
        Assert.False(k.F.Saved["ok"]!.GetValue<bool>());
        Assert.True(k.F.Pad.Dirty);
        k.R.Time.AdvanceMs(NotesAutosave.RetryMs * 2);
        Assert.Single(k.R.Lines, l => l.Contains("could not save", StringComparison.Ordinal));
        File.Delete(Dir);
        k.R.Time.AdvanceMs(NotesAutosave.RetryMs);
        Assert.Equal("<!-- mantle:note -->\nkept in the pad\n", OnDisk());
        Assert.True(k.F.Saved["ok"]!.GetValue<bool>());
    });

    [Fact]
    public void Privacy_no_log_line_or_state_push_carries_note_text_and_logs_name_no_path() => T(async k =>
    {
        await Call(k, "open");
        Type(k, Secret);
        k.R.Time.AdvanceMs(450);
        await Call(k, "close");
        Assert.DoesNotContain(k.R.Lines, l => l.Contains(Secret, StringComparison.Ordinal) || l.Contains(Dir, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(k.R.Lines, l => l.Contains("1 note, " + Secret.Length + " characters", StringComparison.Ordinal));
        foreach (var p in k.R.Events.Published.Where(p => p.Name.StartsWith("notes.", StringComparison.Ordinal)))
            Assert.DoesNotContain(Secret, ((JsonElement)p.Payload!).GetRawText(), StringComparison.Ordinal);
    });

    [Fact]
    public void A_deleted_note_is_in_deleted_md_before_it_leaves_notes_md() => T(async k =>
    {
        await Call(k, "open");
        Type(k, "keep me");
        await Call(k, "new");
        Type(k, "delete me");
        k.R.Time.AdvanceMs(450);
        var r = k.F.DeleteNote(k.F.Pad.Active.Id);
        Assert.True(r.Ok, r.Reason);
        Assert.Contains("delete me", File.ReadAllText(Path.Combine(Dir, "deleted.md")), StringComparison.Ordinal);
        Assert.Equal("<!-- mantle:note -->\nkeep me\n", OnDisk());
    });

    [Fact]
    public void Opening_reads_the_users_existing_notes_without_writing_them() => T(async k =>
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(NotesMd, "<!-- mantle:note -->\nfrom Electron\n<!-- mantle:note -->\nsecond\n");
        var before = File.GetLastWriteTimeUtc(NotesMd);
        await Call(k, "open");
        Assert.Equal(new[] { "from Electron", "second" }, k.F.Pad.Texts());
        await Call(k, "close");
        Assert.Equal(before, File.GetLastWriteTimeUtc(NotesMd));   // nothing typed, nothing written
        Assert.Equal(2, (await Call(k, "state")).GetProperty("count").GetInt32());
    });
}
