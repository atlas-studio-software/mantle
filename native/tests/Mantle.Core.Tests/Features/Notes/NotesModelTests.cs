using System;
using System.IO;
using System.Linq;
using System.Text;
using Mantle.Core.Features.Notes;
using Xunit;

namespace Mantle.Core.Tests.Features.Notes;

// lane r10-notes: the pure half of quick notes (lib/features/_notes-store.js serialize/parse/escape, ui/notes/notes.js
// label/fullTitle/prune/step/delete/undo/debounce/find). No file, no clock: everything is passed in.
public class NotesModelTests
{
    // ---- format (NOTE-053, NOTE-054, NOTE-055, NOTE-039) ----

    [Fact]
    public void Each_note_is_the_marker_line_then_its_text_then_a_newline_and_no_notes_is_one_empty_note()
    {
        Assert.Equal("<!-- mantle:note -->\nfirst\n<!-- mantle:note -->\nsecond\n", NotesFormat.Serialize(new[] { "first", "second" }));
        Assert.Equal("<!-- mantle:note -->\n\n", NotesFormat.Serialize(Array.Empty<string>()));
        Assert.Equal(new[] { "first", "second" }, NotesFormat.Parse(NotesFormat.Serialize(new[] { "first", "second" })).Texts);
    }

    [Fact]
    public void A_file_without_a_marker_is_one_note_and_text_above_the_first_marker_is_a_note_of_its_own()
    {
        var p = NotesFormat.Parse("written by hand\nline two\n");
        Assert.Equal(new[] { "written by hand\nline two" }, p.Texts);
        Assert.Equal(0, p.Markers);
        var q = NotesFormat.Parse("a hand edit\n<!-- mantle:note -->\nkept\n");
        Assert.True(q.Preamble);
        Assert.Equal(new[] { "a hand edit", "kept" }, q.Texts);
    }

    [Fact]
    public void Crlf_reads_as_lf_a_bom_is_dropped_and_a_cr_inside_a_note_is_written_as_lf()
    {
        Assert.Equal(new[] { "one\ntwo" }, NotesFormat.Parse("\uFEFF<!-- mantle:note -->\r\none\r\ntwo\r\n").Texts);
        Assert.Equal("<!-- mantle:note -->\na\nb\n", NotesFormat.Serialize(new[] { "a\rb" }));
    }

    [Fact]
    public void A_text_line_that_looks_like_a_marker_round_trips_exactly()
    {
        string tricky = "<!-- mantle:note -->\n\\<!-- mantle:note -->\n\\\\<!-- mantle:note x\nplain";
        string file = NotesFormat.Serialize(new[] { tricky, "next" });
        Assert.Contains("\n\\<!-- mantle:note -->\n\\\\<!-- mantle:note -->\n\\\\\\<!-- mantle:note x\n", file, StringComparison.Ordinal);
        Assert.Equal(new[] { tricky, "next" }, NotesFormat.Parse(file).Texts);
    }

    [Fact]
    public void Emoji_accents_and_cjk_round_trip_exactly()
    {
        string t = "café 😀 日本語 ñ";
        Assert.Equal(new[] { t }, NotesFormat.Parse(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(NotesFormat.Serialize(new[] { t })))).Texts);
    }

    [Fact]
    public void Utf8_is_checked_strictly_so_an_ansi_file_is_not_valid()
    {
        Assert.True(NotesFormat.ValidUtf8(Encoding.UTF8.GetBytes("café")));
        Assert.False(NotesFormat.ValidUtf8(new byte[] { 0x63, 0x61, 0x66, 0xE9 }));   // "café" in Windows-1252
    }

    [Fact]
    public void Fs_reasons_are_plain_words_with_the_path()
    {
        Assert.Equal("the disk is full", NotesFormat.WhyCode("ENOSPC", @"X:\n.md"));
        Assert.Equal(@"Windows refused access to X:\n.md (read-only, or another program holds it)", NotesFormat.WhyCode("EPERM", @"X:\n.md"));
        Assert.Equal(@"X:\n.md is in use by another program", NotesFormat.WhyCode("EBUSY", @"X:\n.md"));
        Assert.Equal(@"the drive of X:\n.md is read-only", NotesFormat.WhyCode("EROFS", @"X:\n.md"));
        Assert.Equal(@"X:\n.md does not exist", NotesFormat.WhyCode("ENOENT", @"X:\n.md"));
        Assert.Equal(@"X:\n.md is a folder, not a file", NotesFormat.WhyCode("EISDIR", @"X:\n.md"));
        Assert.Equal(@"a file is in the way of the folder X:\d", NotesFormat.WhyCode("ENOTDIR", @"X:\d"));
    }

    [Fact]
    public void Stamps_are_the_copy_name_and_the_deleted_marker_shapes()
    {
        var t = new DateTime(2026, 9, 27, 21, 14, 3);
        Assert.Equal("2026-09-27 211403", NotesFormat.Stamp(t));
        Assert.Equal("<!-- mantle:deleted 2026-09-27 21:14:03 -->\ngone\n", NotesFormat.DeletedBlock("gone", t));
    }

    // ---- tabs (NOTE-025, NOTE-026, NOTE-040) ----

    [Fact]
    public void A_tab_is_the_first_non_blank_line_cut_to_18_chars_and_a_blank_note_reads_New_note()
    {
        Assert.Equal("Shopping", NotesFormat.Label("\n\n   Shopping  \nmilk"));
        Assert.Equal("A very long first…", NotesFormat.Label("A very long first line of a note"));
        Assert.Equal(("New note", true), NotesPad.Tab("  \n\t"));
        Assert.Equal(("Plan", false), NotesPad.Tab("Plan"));
    }

    [Fact]
    public void Only_a_truncated_tab_has_a_tooltip_and_it_is_cut_at_120_chars()
    {
        Assert.Equal("", NotesFormat.FullTitle("Short", NotesFormat.Label("Short")));
        string line = "A very long first line of a note";
        Assert.Equal(line, NotesFormat.FullTitle(line + "\nmore", NotesFormat.Label(line)));
        string huge = new string('x', 300);
        Assert.Equal(new string('x', 119) + "…", NotesFormat.FullTitle(huge, NotesFormat.Label(huge)));
    }

    [Fact]
    public void The_label_of_an_8_MB_note_reads_only_its_first_line()
    {
        string big = "title\n" + new string('y', 8 * 1024 * 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) Assert.Equal("title", NotesFormat.Label(big));
        Assert.True(sw.Elapsed.TotalMilliseconds < 100, "100 labels took " + sw.Elapsed.TotalMilliseconds + " ms");
    }

    // ---- the pad (NOTE-028, NOTE-029, NOTE-031, NOTE-035, NOTE-036, NOTE-037, NOTE-073, NOTE-074, NOTE-076..080, NOTE-097, NOTE-098) ----

    static NotesPad Pad(params string[] texts) { var p = new NotesPad(); p.Load(texts, 0); return p; }

    [Fact]
    public void The_remembered_tab_is_clamped_to_the_notes_that_exist()
    {
        var p = new NotesPad();
        p.Load(new[] { "a", "b" }, 7);
        Assert.Equal(1, p.ActiveIndex);
        p.Load(new[] { "a", "b" }, -3);
        Assert.Equal(0, p.ActiveIndex);
    }

    [Fact]
    public void A_new_note_goes_right_after_the_open_one_and_opens_and_is_refused_before_load()
    {
        Assert.Null(new NotesPad().NewNote());
        var p = Pad("a", "b", "c");
        string id = p.NewNote()!;
        Assert.Equal(1, p.ActiveIndex);
        Assert.Equal(id, p.Notes[1].Id);
        Assert.Equal(new[] { "a", "", "b", "c" }, p.Texts());
    }

    [Fact]
    public void Prune_drops_blank_extras_never_the_last_and_keeps_the_open_one_when_all_are_blank()
    {
        var p = Pad("a", " ", "b", "");
        Assert.True(p.Prune());
        Assert.Equal(new[] { "a", "b" }, p.Texts());
        var q = Pad("", " ", "");
        q.Activate(q.Notes[1].Id);
        Assert.True(q.Prune());
        Assert.Single(q.Notes);
        Assert.Equal(" ", q.Notes[0].Text);
        Assert.False(Pad("").Prune());
    }

    [Fact]
    public void Stepping_wraps_needs_two_notes_and_home_end_open_the_first_and_last()
    {
        Assert.Null(Pad("only").Step(1));
        var p = Pad("a", "b", "c");
        p.Step(-1);
        Assert.Equal(2, p.ActiveIndex);
        p.Step(1);
        Assert.Equal(0, p.ActiveIndex);
        p.Last();
        Assert.Equal(2, p.ActiveIndex);
        p.First();
        Assert.Equal(0, p.ActiveIndex);
    }

    [Fact]
    public void A_load_error_leaves_one_blank_read_only_note_and_delete_off()
    {
        var p = new NotesPad();
        p.Load(new[] { "" }, 0, "the file is not UTF-8 text");
        Assert.True(p.ReadOnly);
        Assert.False(p.Edit(p.Notes[0].Id, "typed"));
        Assert.False(p.CanDelete);
        Assert.False(Pad("").CanDelete);   // one blank note: nothing to delete
    }

    [Fact]
    public void A_delete_keeps_the_text_first_then_opens_the_note_before_and_the_last_leaves_one_blank()
    {
        var kept = new System.Collections.Generic.List<string>();
        var p = Pad("a", "b", "c");
        var r = p.Delete(p.Notes[1].Id, 1000, t => { kept.Add(t); return null; });
        Assert.True(r.Ok);
        Assert.Equal(new[] { "b" }, kept);
        Assert.Equal(new[] { "a", "c" }, p.Texts());
        Assert.Equal(0, p.ActiveIndex);
        var one = Pad("x");
        one.Delete(one.Notes[0].Id, 1000, _ => null);
        Assert.Equal(new[] { "" }, one.Texts());
    }

    [Fact]
    public void A_blank_note_is_deleted_without_keeping_and_a_refused_keep_leaves_the_note()
    {
        var p = Pad("a", "  ");
        bool asked = false;
        Assert.True(p.Delete(p.Notes[1].Id, 1000, _ => { asked = true; return null; }).Ok);
        Assert.False(asked);
        var q = Pad("a", "b");
        var r = q.Delete(q.Notes[0].Id, 1000, _ => "could not keep the note in deleted.md: the disk is full");
        Assert.False(r.Ok);
        Assert.Equal(new[] { "a", "b" }, q.Texts());
    }

    [Fact]
    public void A_double_click_deletes_one_note_and_the_errors_carry_no_text()
    {
        var p = Pad("a", "b", "c");
        p.Delete(p.Notes[2].Id, 1000, _ => null);
        p.Delete(p.Notes[1].Id, 1200, _ => null);   // within 400 ms: ignored
        Assert.Equal(new[] { "a", "b" }, p.Texts());
        Assert.Equal("notes.delete needs the note (id)", p.Delete(null, 5000, _ => null).Reason);
        Assert.Equal("that note is not in the pad any more", p.Delete("p999", 5000, _ => null).Reason);
    }

    [Fact]
    public void Undo_within_8_s_puts_the_note_back_in_its_place_and_the_untouched_stand_in_goes()
    {
        var p = Pad("a", "b");
        p.Delete(p.Notes[1].Id, 1000, _ => null);
        Assert.True(p.UndoDelete(5000));
        Assert.Equal(new[] { "a", "b" }, p.Texts());
        Assert.Equal(1, p.ActiveIndex);
        var one = Pad("only");
        one.Delete(one.Notes[0].Id, 1000, _ => null);
        Assert.True(one.UndoDelete(2000));
        Assert.Equal(new[] { "only" }, one.Texts());
        var late = Pad("a", "b");
        late.Delete(late.Notes[0].Id, 1000, _ => null);
        Assert.False(late.UndoDelete(1000 + NotesPad.UndoMs + 1));
        var closed = Pad("a", "b");
        closed.Delete(closed.Notes[0].Id, 1000, _ => null);
        closed.DropUndo();
        Assert.False(closed.UndoDelete(1100));
    }

    // ---- autosave debounce (NOTE-041) ----

    [Fact]
    public void Autosave_is_400_ms_after_typing_stops_and_at_most_3_s_into_a_burst()
    {
        var a = new NotesAutosave();
        Assert.Null(a.DueAt);
        Assert.Equal(1400, a.Changed(1000));
        Assert.False(a.Due(1399));
        Assert.True(a.Due(1400));
        for (long t = 1000; t <= 3900; t += 100) a.Changed(t);   // a burst, a key every 100 ms
        Assert.Equal(4000, a.DueAt);
        a.Sent();
        Assert.False(a.Pending);
    }

    // ---- find (NOTE-082, NOTE-083, NOTE-084) ----

    [Fact]
    public void Find_is_case_insensitive_across_every_note_and_counts_in_words()
    {
        var hits = NotesFind.All("milk", new[] { "Milk and MILK", "none", "oat milk" });
        Assert.Equal(3, hits.Count);
        Assert.Equal("3 in 2 notes", NotesFind.Count(hits));
        Assert.Equal("2 of 3", NotesFind.Count(hits, 1));
        Assert.Equal("No matches", NotesFind.Count(NotesFind.All("tea", new[] { "milk" })));
        Assert.Equal("999+", NotesFind.Count(NotesFind.All("a", new[] { new string('a', 2000) })).Split(' ')[0]);
        Assert.Equal("milk", NotesFind.QueryFrom("milk"));
        Assert.Null(NotesFind.QueryFrom("two\nlines"));
        Assert.Null(NotesFind.QueryFrom(new string('q', 101)));
    }

    // ---- where (NOTE-052, NOTE-101) ----

    [Fact]
    public void Live_notes_are_under_USERPROFILE_Documents_and_test_never_uses_Documents()
    {
        string prof = Path.Combine(Path.GetTempPath(), "prof");
        string data = Path.Combine(Path.GetTempPath(), "data");
        Assert.Equal(Path.Combine(prof, "Documents", "Mantle Notes"), NotesPaths.Resolve(false, prof, data, Path.GetTempPath(), _ => null).Dir);
        Assert.Equal(Path.Combine(data, "notes"), NotesPaths.Resolve(true, prof, data, Path.GetTempPath(), _ => null).Dir);
        var refused = NotesPaths.Resolve(true, prof, data, Path.GetTempPath(), _ => Path.Combine(prof, "Documents", "x"));
        Assert.Equal(Path.Combine(data, "notes"), refused.Dir);
        Assert.StartsWith("--test refused the notes folder", refused.Note);
    }
}
