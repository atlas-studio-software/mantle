using System;
using System.IO;
using System.Linq;
using System.Text;
using Mantle.Core.Data;
using Mantle.Core.Features.Notes;
using Xunit;

namespace Mantle.Core.Tests.Features.Notes;

// lane r10-notes: the notes FILE (lib/features/_notes-store.js) in a temp folder. Every path is under a fresh temp root;
// nothing here reads or writes the user's Documents. The SAFETY rows (storage: where / format / atomic / the gate /
// a damaged file kept) each have their own test, named Safety_...
public sealed class NotesStoreTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "mantle-notes-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _dir;
    long _now = 1_000_000;
    readonly DateTime _local = new(2026, 11, 24, 15, 1, 2);

    public NotesStoreTests()
    {
        _dir = Path.Combine(_root, "Mantle Notes");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    NotesStore Store(WriteGate? gate = null) => new(_dir, new SafeFile(gate ?? WriteGate.Scratch(_root)), () => _local, () => _now, _ => { }, pid: 4242);

    string NotesMd => Path.Combine(_dir, "notes.md");

    void Plant(string text) { Directory.CreateDirectory(_dir); File.WriteAllText(NotesMd, text, new UTF8Encoding(false)); }

    string[] Copies() => Directory.GetFiles(_dir, "notes (changed outside Mantle *").Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Safety_a_missing_file_loads_as_one_empty_note_and_the_folder_and_file_are_made_on_the_first_save()
    {
        var s = Store();
        var l = s.Load();
        Assert.True(l.Ok);
        Assert.True(l.Missing);
        Assert.Equal(new[] { "" }, l.Texts);
        Assert.False(Directory.Exists(_dir));   // loading makes nothing
        Assert.True(s.Save(new[] { "hello" }).Ok);
        Assert.Equal("<!-- mantle:note -->\nhello\n", File.ReadAllText(NotesMd));
    }

    [Fact]
    public void Safety_the_file_is_utf8_without_a_bom_in_the_marker_format_readable_by_hand()
    {
        var s = Store();
        s.Load();
        Assert.True(s.Save(new[] { "café 😀", "two" }).Ok);
        byte[] b = File.ReadAllBytes(NotesMd);
        Assert.False(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF);
        Assert.Equal("<!-- mantle:note -->\ncafé 😀\n<!-- mantle:note -->\ntwo\n", Encoding.UTF8.GetString(b));
        Assert.Equal(new[] { "café 😀", "two" }, Store().Load().Texts);
    }

    [Fact]
    public void Safety_a_save_is_atomic_a_leftover_tmp_goes_first_and_no_tmp_file_ever_stays()
    {
        var s = Store();
        s.Load();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(s.TmpFile, "a crash left me");
        Assert.True(s.Save(new[] { "new" }).Ok);
        Assert.False(File.Exists(s.TmpFile));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
        Assert.Equal(new[] { "new" }, Store().Load().Texts);
    }

    [Fact]
    public void Safety_a_refused_gate_writes_nothing_at_all_and_says_why()
    {
        Plant("<!-- mantle:note -->\nElectron's text\n");
        byte[] before = File.ReadAllBytes(NotesMd);
        var s = Store(WriteGate.Never("Electron Mantle is running, so the user's own notes.md is left for it"));
        Assert.Equal(new[] { "Electron's text" }, s.Load().Texts);   // reading (the import) is allowed
        var r = s.Save(new[] { "native's text" });
        Assert.False(r.Ok);
        Assert.Contains("Electron Mantle is running", r.Reason, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(NotesMd));
        Assert.Single(Directory.GetFiles(_dir));   // no tmp, no copy
        Assert.False(s.AppendDeleted("x").Ok);
        Assert.False(File.Exists(Path.Combine(_dir, "deleted.md")));
    }

    [Fact]
    public void Safety_a_folder_outside_the_data_folders_is_never_written_even_by_a_scratch_gate()
    {
        // the live folder (Documents\Mantle Notes) is outside Mantle's data folders: today's gate refuses it
        string other = Path.Combine(_root, "elsewhere");
        var s = new NotesStore(Path.Combine(other, "Mantle Notes"), new SafeFile(WriteGate.Scratch(Path.Combine(_root, "data"))), () => _local, () => _now, _ => { });
        var r = s.Save(new[] { "text" });
        Assert.False(r.Ok);
        Assert.Contains("outside Mantle's data folders", r.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public void Safety_a_file_that_is_not_utf8_is_not_opened_and_is_kept_byte_for_byte_before_any_save()
    {
        Directory.CreateDirectory(_dir);
        byte[] ansi = { 0x63, 0x61, 0x66, 0xE9, 0x0A };   // "café\n" in Windows-1252
        File.WriteAllBytes(NotesMd, ansi);
        var s = Store();
        var l = s.Load();
        Assert.False(l.Ok);
        Assert.True(l.Refused);
        Assert.EndsWith(", so the pad did not open it; it is left as it is, and if you save notes here it is first kept as a copy", l.Reason, StringComparison.Ordinal);
        Assert.Contains("is not UTF-8 text", l.Reason, StringComparison.Ordinal);
        Assert.Equal(ansi, File.ReadAllBytes(NotesMd));   // load leaves it exactly as it is
        var r = s.Save(new[] { "typed after" });
        Assert.True(r.Ok);
        Assert.NotNull(r.Outside);
        Assert.Equal(ansi, File.ReadAllBytes(r.Outside!));   // the damaged version is kept, never lost
    }

    [Fact]
    public void Safety_a_utf16_file_is_named_and_a_file_with_over_500_notes_is_not_opened()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(NotesMd, new byte[] { 0xFF, 0xFE, 0x61, 0x00 });
        Assert.Contains("(it looks like UTF-16)", Store().Load().Reason, StringComparison.Ordinal);
        Plant(string.Concat(Enumerable.Repeat("<!-- mantle:note -->\nx\n", 501)));
        var l = Store().Load();
        Assert.False(l.Ok);
        Assert.Contains("holds 501 notes, over the 500 the pad opens", l.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_more_than_8_MB_of_text_is_refused_and_the_old_file_stays()
    {
        Plant("<!-- mantle:note -->\nold\n");
        var s = Store();
        s.Load();
        var r = s.Save(new[] { new string('x', (int)NotesFormat.MaxBytes + 1) });
        Assert.False(r.Ok);
        Assert.Contains("over the 8.0 MB the pad saves; the text stays in the pad (make it shorter)", r.Reason, StringComparison.Ordinal);
        Assert.Equal(new[] { "old" }, Store().Load().Texts);
    }

    [Fact]
    public void Safety_an_outside_change_is_kept_as_a_copy_before_the_save_goes_over_it()
    {
        Plant("<!-- mantle:note -->\nmine\n");
        var s = Store();
        s.Load();
        File.WriteAllText(NotesMd, "<!-- mantle:note -->\nedited in Notepad, longer\n");
        Assert.True(s.ChangedOnDisk());
        var r = s.Save(new[] { "mine, typed on" });
        Assert.True(r.Ok);
        Assert.Equal(new[] { "notes (changed outside Mantle 2026-11-24 150102).md" }, Copies());
        Assert.Equal("<!-- mantle:note -->\nedited in Notepad, longer\n", File.ReadAllText(r.Outside!));
        Assert.Equal(new[] { "mine, typed on" }, Store().Load().Texts);
    }

    [Fact]
    public void A_touch_with_the_same_text_is_not_a_change_and_identical_text_writes_nothing()
    {
        Plant("<!-- mantle:note -->\nsame\n");
        var s = Store();
        s.Load();
        File.SetLastWriteTimeUtc(NotesMd, DateTime.UtcNow.AddMinutes(5));
        Assert.False(s.ChangedOnDisk());
        var r = s.Save(new[] { "same" });
        Assert.True(r.Ok);
        Assert.True(r.Skipped);
        Assert.Empty(Copies());
    }

    [Fact]
    public void At_most_10_outside_change_copies_in_10_min_then_the_save_is_refused_in_words()
    {
        Plant("<!-- mantle:note -->\nv0\n");
        var s = Store();
        s.Load();
        for (int i = 1; i <= 10; i++) {
            File.WriteAllText(NotesMd, "<!-- mantle:note -->\noutside " + new string('!', i) + "\n");
            Assert.True(s.Save(new[] { "pad " + i }).Ok);
        }
        Assert.Equal(10, Copies().Length);
        File.WriteAllText(NotesMd, "<!-- mantle:note -->\nonce more, outside\n");
        var r = s.Save(new[] { "pad 11" });
        Assert.False(r.Ok);
        Assert.Contains("close the program that keeps changing it (your text stays in the pad and is saved once it stops)", r.Reason, StringComparison.Ordinal);
        Assert.Equal("<!-- mantle:note -->\nonce more, outside\n", File.ReadAllText(NotesMd));   // never overwritten unkept
        _now += NotesFormat.CopyWindowMs;
        Assert.True(s.Save(new[] { "pad 11" }).Ok);
    }

    [Fact]
    public void Safety_a_deleted_note_is_appended_to_deleted_md_which_only_grows()
    {
        var s = Store();
        Assert.True(s.AppendDeleted("first gone").Ok);
        Assert.True(s.AppendDeleted("second gone").Ok);
        Assert.Equal("<!-- mantle:deleted 2026-11-24 15:01:02 -->\nfirst gone\n<!-- mantle:deleted 2026-11-24 15:01:02 -->\nsecond gone\n",
            File.ReadAllText(Path.Combine(_dir, "deleted.md")));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void A_file_in_the_way_of_the_folder_loads_as_one_empty_note_and_the_save_says_so()
    {
        File.WriteAllText(_dir, "I am a file");
        var s = Store();
        var l = s.Load();
        Assert.Equal(new[] { "" }, l.Texts);
        var r = s.Save(new[] { "x" });
        Assert.False(r.Ok);
        Assert.Contains("a file is in the way of the folder", r.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Safety_a_rescue_file_is_written_beside_the_settings_and_never_over_an_earlier_one()
    {
        var safe = new SafeFile(WriteGate.Scratch(_root));
        var r = NotesStore.WriteRescue(safe, _root, new[] { "unsaved" }, _local, 4242);
        Assert.NotNull(r.File);
        Assert.Equal("notes-rescue 2026-11-24 150102.md", Path.GetFileName(r.File));
        Assert.Equal("<!-- mantle:note -->\nunsaved\n", File.ReadAllText(r.File!));
        var again = NotesStore.WriteRescue(safe, _root, new[] { "other" }, _local, 4242);
        Assert.Null(again.File);
        Assert.Equal("<!-- mantle:note -->\nunsaved\n", File.ReadAllText(r.File!));
        Assert.Equal(new[] { r.File }, NotesStore.ListRescue(_root));
    }
}
