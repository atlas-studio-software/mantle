using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Mantle.Core.Features.Notes;

/// <summary>One note in the pad: an id for this session (never written) and its text.</summary>
public sealed class PadNote
{
    public string Id { get; }
    public string Text { get; internal set; }
    internal PadNote(string id, string text) { Id = id; Text = text; }
}

/// <summary>
/// lane r10-notes: the pad's editing model, pure (ui/notes/notes.js state S): the notes as tabs, the open one, the
/// edit counter (rev) and what was last saved, new / prune / delete / undo, tab stepping. No file and no clock in here:
/// the time comes in as an argument (<see cref="NotesAutosave"/> holds the debounce), the file is <see cref="NotesStore"/>.
/// </summary>
public sealed class NotesPad
{
    public const int UndoMs = 8000, ClickGapMs = 400;

    readonly List<PadNote> _notes = new();
    int _seq;
    public IReadOnlyList<PadNote> Notes => _notes;
    public string ActiveId { get; private set; } = "";
    public bool Loaded { get; private set; }
    /// Why the file was not opened; typing is off then, so nothing is overwritten (NOTE-097).
    public string? LoadError { get; private set; }
    public long Rev { get; private set; }
    public long SavedRev { get; private set; }
    public bool Dirty => Rev > SavedRev;
    public bool ReadOnly => !Loaded || Deleting;
    public bool Deleting { get; private set; }

    public NotesPad() { _notes.Add(new PadNote(NewId(), "")); ActiveId = _notes[0].Id; }

    string NewId() => "p" + (++_seq).ToString(CultureInfo.InvariantCulture);

    public int ActiveIndex => Math.Max(0, _notes.FindIndex(n => n.Id == ActiveId));
    public PadNote Active => _notes[ActiveIndex];

    public IReadOnlyList<string> Texts() => _notes.Select(n => n.Text).ToArray();

    /// <summary>Put the file's notes in the pad and open the remembered tab, clamped to the notes that exist (NOTE-035).
    /// <paramref name="loadError"/> set = the file was not opened: one blank note, read-only (NOTE-066, NOTE-097).</summary>
    public void Load(IReadOnlyList<string> texts, int active, string? loadError = null)
    {
        _notes.Clear();
        foreach (var t in texts.Count == 0 ? new[] { "" } : texts) _notes.Add(new PadNote(NewId(), t ?? ""));
        ActiveId = _notes[Math.Clamp(active, 0, _notes.Count - 1)].Id;
        LoadError = loadError;
        Loaded = loadError == null;
        Rev = 0; SavedRev = 0;
    }

    /// The text of a note changed (typing, paste). False when typing is off or the note is gone.
    public bool Edit(string id, string text)
    {
        if (ReadOnly) return false;
        var n = _notes.FirstOrDefault(x => x.Id == id);
        if (n == null) return false;
        if (n.Text == text) return true;
        n.Text = text ?? "";
        Rev++;
        return true;
    }

    /// The text up to <paramref name="rev"/> is on disk.
    public void MarkSaved(long rev) { if (rev > SavedRev) SavedRev = Math.Min(rev, Rev); }

    /// Text that came back from a rest unsaved: marked unsaved so the next tick or keystroke saves it (NOTE-091).
    public void ForceDirty() { if (Rev <= SavedRev) Rev = SavedRev + 1; }

    /// Open another tab (NOTE-027).
    public bool Activate(string id)
    {
        if (!_notes.Any(n => n.Id == id)) return false;
        ActiveId = id;
        return true;
    }

    /// <summary>"+" / Ctrl+N: a blank note right after the open one, opened (NOTE-036). Null before load or on a load error.</summary>
    public string? NewNote()
    {
        if (!Loaded) return null;
        var n = new PadNote(NewId(), "");
        _notes.Insert(ActiveIndex + 1, n);
        ActiveId = n.Id;
        Rev++;
        return n.Id;
    }

    /// <summary>On close blank extra notes go, never the last one; when all are blank the open one stays (NOTE-037).</summary>
    public bool Prune()
    {
        var keep = _notes.Where(n => n.Text.Trim().Length > 0).ToList();
        if (keep.Count == _notes.Count) return false;
        if (keep.Count == 0) {
            if (_notes.Count == 1) return false;
            keep.Add(_notes.FirstOrDefault(n => n.Id == ActiveId) ?? _notes[0]);
        }
        string was = ActiveId;
        _notes.Clear();
        _notes.AddRange(keep);
        if (!_notes.Any(n => n.Id == was)) ActiveId = _notes[^1].Id;
        Rev++;
        return true;
    }

    /// <summary>ArrowRight/Left on the tabs, Ctrl+Tab / Ctrl+Shift+Tab (wraps; NOTE-028, NOTE-031). Null with one note.</summary>
    public string? Step(int by)
    {
        if (_notes.Count < 2) return null;
        int i = ((ActiveIndex + by) % _notes.Count + _notes.Count) % _notes.Count;
        ActiveId = _notes[i].Id;
        return ActiveId;
    }

    /// Home / End on a tab (NOTE-029).
    public string First() { ActiveId = _notes[0].Id; return ActiveId; }
    public string Last() { ActiveId = _notes[^1].Id; return ActiveId; }

    /// <summary>Delete is off with one blank note, before load and during a delete (NOTE-078).</summary>
    public bool CanDelete => Loaded && !Deleting && !(_notes.Count == 1 && _notes[0].Text.Trim().Length == 0);

    public sealed record Deleted(string Id, int Index, string Text, long At, string? StandIn);

    Deleted? _undo;
    long _lastDeleteAt = long.MinValue / 2;   // far past, without overflowing nowMs - _lastDeleteAt
    public Deleted? Undo => _undo;

    /// <summary>
    /// Delete one note (NOTE-071..080). <paramref name="keep"/> keeps a non-blank note's text in deleted.md FIRST and answers
    /// null or why not; a refusal leaves the note where it is (NOTE-074). A blank note is not kept (NOTE-073). A second
    /// delete within 400 ms of the first is ignored (NOTE-077). The note before it opens; the last one leaves one blank (NOTE-076).
    /// </summary>
    public (bool Ok, string? Reason) Delete(string? id, long nowMs, Func<string, string?> keep)
    {
        if (string.IsNullOrEmpty(id)) return (false, "notes.delete needs the note (id)");
        if (!CanDelete) return (false, "the notes pad is not up");
        if (nowMs - _lastDeleteAt < ClickGapMs) return (true, null);
        int i = _notes.FindIndex(n => n.Id == id);
        if (i < 0) return (false, "that note is not in the pad any more");
        var n = _notes[i];
        bool blank = n.Text.Trim().Length == 0;
        Deleting = true;
        try {
            if (!blank) { string? bad = keep(n.Text); if (bad != null) return (false, bad); }
        } finally { Deleting = false; }
        _lastDeleteAt = nowMs;
        _notes.RemoveAt(i);
        string? standIn = null;
        if (_notes.Count == 0) { var b = new PadNote(NewId(), ""); _notes.Add(b); standIn = b.Id; }
        ActiveId = _notes[Math.Max(0, i - 1)].Id;
        Rev++;
        _undo = blank ? null : new Deleted(n.Id, i, n.Text, nowMs, standIn);
        return (true, null);
    }

    /// <summary>Undo within 8 s puts the note back at its place with its text and opens it; an untouched stand-in blank goes (NOTE-079).</summary>
    public bool UndoDelete(long nowMs)
    {
        var u = _undo;
        _undo = null;
        if (u == null || nowMs - u.At > UndoMs || !Loaded) return false;
        if (u.StandIn != null) {
            int s = _notes.FindIndex(n => n.Id == u.StandIn);
            if (s >= 0 && _notes[s].Text.Length == 0) _notes.RemoveAt(s);
        }
        var back = new PadNote(NewId(), u.Text);
        _notes.Insert(Math.Clamp(u.Index, 0, _notes.Count), back);
        ActiveId = back.Id;
        Rev++;
        return true;
    }

    /// Undo is dropped on close.
    public void DropUndo() => _undo = null;

    /// The tab's words: the first line, or "New note" (dimmed) for a blank one (NOTE-025).
    public static (string Words, bool Empty) Tab(string text)
    {
        string w = NotesFormat.Label(text);
        return w.Length == 0 ? ("New note", true) : (w, false);
    }

    public static string Plural(int n, string w) => n.ToString(CultureInfo.InvariantCulture) + " " + w + (n == 1 ? "" : "s");
}

/// <summary>
/// lane r10-notes: the autosave debounce (ui/notes/notes.js #changed): a save 400 ms after typing stops, and never later than
/// 3 s after the first unsaved keystroke of a long burst (NOTE-041). The time is passed in, so tests drive it by hand.
/// </summary>
public sealed class NotesAutosave
{
    public const int QuietMs = 400, MaxWaitMs = 3000, RetryMs = 5000;

    long? _first;
    long _last;

    public bool Pending => _first != null;

    /// A keystroke at <paramref name="nowMs"/>; answers when the save is due.
    public long Changed(long nowMs)
    {
        _first ??= nowMs;
        _last = nowMs;
        return DueAt!.Value;
    }

    public long? DueAt => _first is long f ? Math.Min(_last + QuietMs, f + MaxWaitMs) : null;

    public bool Due(long nowMs) => DueAt is long d && nowMs >= d;

    /// The save went out (whatever it answered: a failed one is tried again on the next keystroke and every 5 s).
    public void Sent() => _first = null;
}

/// <summary>lane r10-notes: find in all notes, pure (ui/notes/notes.js runFind / renderFind; NOTE-082..084).</summary>
public static class NotesFind
{
    public const int MaxHits = 999, MaxSelection = 100;

    public sealed record Hit(int Note, int At, int Length);

    /// Case-insensitive, every note in tab order; positions are in the note's own text.
    public static IReadOnlyList<Hit> All(string? query, IReadOnlyList<string> texts)
    {
        var hits = new List<Hit>();
        if (string.IsNullOrEmpty(query)) return hits;
        for (int n = 0; n < texts.Count; n++) {
            string t = texts[n] ?? "";
            for (int i = t.IndexOf(query, StringComparison.OrdinalIgnoreCase); i >= 0; i = t.IndexOf(query, i + Math.Max(1, query.Length), StringComparison.OrdinalIgnoreCase)) {
                hits.Add(new Hit(n, i, query.Length));
                if (hits.Count > MaxHits) return hits;
                if (i + query.Length >= t.Length) break;
            }
        }
        return hits;
    }

    /// "N in M notes", "k of N" while stepping, "No matches"; capped at "999+".
    public static string Count(IReadOnlyList<Hit> hits, int current = -1)
    {
        if (hits.Count == 0) return "No matches";
        string n = hits.Count > MaxHits ? "999+" : hits.Count.ToString(CultureInfo.InvariantCulture);
        if (current >= 0) return (current + 1).ToString(CultureInfo.InvariantCulture) + " of " + n;
        int m = hits.Select(h => h.Note).Distinct().Count();
        return n + " in " + NotesPad.Plural(m, "note");
    }

    /// A short one-line selection becomes the query (NOTE-082); anything else leaves the query as it was.
    public static string? QueryFrom(string? selection)
        => !string.IsNullOrEmpty(selection) && selection.Length <= MaxSelection && selection.IndexOf('\n') < 0 ? selection : null;
}
