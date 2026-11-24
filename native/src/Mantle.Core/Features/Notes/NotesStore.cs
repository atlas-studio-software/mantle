using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Notes;

public sealed record NotesLoad(bool Ok, IReadOnlyList<string> Texts, bool Missing = false, bool Refused = false, string? Reason = null, int Markers = 0, bool Preamble = false);

public sealed record NotesSaved(bool Ok, string? Reason = null, string? Outside = null, bool Skipped = false, int Count = 0, int Chars = 0, long Bytes = 0);

/// <summary>
/// lane r10-notes: the quick notes file on disk (lib/features/_notes-store.js createStore). Every change to a file goes
/// through <see cref="SafeFile"/>, so the write gate is asked first: a run that may not write the folder (the user's own
/// Documents while Electron Mantle owns it, a profile that is not --live) saves NOTHING and answers why; the text stays
/// in the pad. Nothing here throws, and no reason ever carries a word of note text (paths and codes only).
///
/// SAVES never lose text: tmp-and-rename (notes.md.tmp-&lt;pid&gt;, a leftover removed first), the rename retried for
/// ~1 s while another program holds the file, notes.md checked again before EVERY try; a version changed outside Mantle
/// (or never read, or refused at load) is first kept as "notes (changed outside Mantle ...).md", and when that copy
/// cannot be made the save is refused. A damaged notes.md (too big, not UTF-8, too many notes) is never opened and never
/// written over unkept: it stays exactly where it is (Electron's rule; it is not renamed .unreadable, so the user still
/// finds it) and is copied aside before the first save.
/// </summary>
public sealed class NotesStore
{
    public string Dir { get; }
    public string File { get; }
    public string DeletedFile { get; }

    readonly SafeFile _safe;
    readonly Func<DateTime> _localNow;
    readonly Func<long> _nowMs;
    readonly Action<int> _sleep;
    readonly int _pid;
    readonly object _one = new();   // saves run one at a time
    readonly List<long> _copies = new();
    Stat? _known;                    // what was last read or written; null = never read (or refused)
    string? _lastText;

    public NotesStore(string dir, SafeFile safe, Func<DateTime>? localNow = null, Func<long>? nowMs = null, Action<int>? sleep = null, int? pid = null)
    {
        Dir = dir ?? throw new ArgumentNullException(nameof(dir));
        _safe = safe ?? throw new ArgumentNullException(nameof(safe));
        File = Path.Combine(dir, "notes.md");
        DeletedFile = Path.Combine(dir, "deleted.md");
        _localNow = localNow ?? (() => DateTime.Now);
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _sleep = sleep ?? System.Threading.Thread.Sleep;
        _pid = pid ?? Environment.ProcessId;
    }

    public string TmpFile => File + ".tmp-" + _pid;

    readonly record struct Stat(bool Exists, bool IsDir, long Ticks, long Size, string? Error);

    Stat StatOf(string p)
    {
        try {
            if (Directory.Exists(p)) return new Stat(true, true, 0, 0, null);
            var fi = new FileInfo(p);
            if (!fi.Exists) return new Stat(false, false, 0, 0, null);
            return new Stat(true, false, fi.LastWriteTimeUtc.Ticks, fi.Length, null);
        } catch (Exception e) { return new Stat(false, false, 0, 0, NotesFormat.Why(e, p)); }
    }

    static bool Same(Stat a, Stat? b) => b is Stat k && a.Error == null && a.Exists == k.Exists && (!a.Exists || (a.Ticks == k.Ticks && a.Size == k.Size));

    /// The notes in the file. Ok=false = not opened (Texts is then one empty note) with the reason in plain words.
    public NotesLoad Load()
    {
        lock (_one) {
            var s = StatOf(File);
            if (s.Error != null) { _known = null; return new NotesLoad(false, One, Reason: $"could not read {File}: {s.Error}"); }
            if (!s.Exists) {
                // A file in the way of the folder reads as missing too: one empty note, never a crash (NOTE-070).
                _known = new Stat(false, false, 0, 0, null); _lastText = null;
                return new NotesLoad(true, One, Missing: true);
            }
            if (s.IsDir) { _known = null; return new NotesLoad(false, One, Reason: $"{File} is a folder, not a file"); }
            if (s.Size > NotesFormat.MaxBytes) return Refuse($"{File} is {NotesFormat.Mb(s.Size)}, over the {NotesFormat.Mb(NotesFormat.MaxBytes)} the pad opens");
            byte[] buf;
            try { buf = System.IO.File.ReadAllBytes(File); }
            catch (Exception e) { _known = null; return new NotesLoad(false, One, Reason: $"could not read {File}: {NotesFormat.Why(e, File)}"); }
            if (buf.Length > NotesFormat.MaxBytes) return Refuse($"{File} is {NotesFormat.Mb(buf.Length)}, over the {NotesFormat.Mb(NotesFormat.MaxBytes)} the pad opens");
            if (!NotesFormat.ValidUtf8(buf))
                return Refuse($"{File} is not UTF-8 text{(buf.Length >= 2 && buf[0] == 0xFF && buf[1] == 0xFE ? " (it looks like UTF-16)" : "")}");
            string raw = NotesFormat.Utf8NoBom.GetString(buf);
            var p = NotesFormat.Parse(raw);
            if (p.Texts.Count > NotesFormat.MaxNotes) return Refuse($"{File} holds {p.Texts.Count} notes, over the {NotesFormat.MaxNotes} the pad opens");
            var after = StatOf(File);
            _known = after.Exists ? after : s;
            _lastText = raw;
            return new NotesLoad(true, p.Texts, Markers: p.Markers, Preamble: p.Preamble);
        }
    }

    static readonly string[] One = { "" };

    NotesLoad Refuse(string words)
    {
        _known = null; _lastText = null;   // the first save keeps this version as a copy
        return new NotesLoad(false, One, Refused: true,
            Reason: words + ", so the pad did not open it; it is left as it is, and if you save notes here it is first kept as a copy");
    }

    /// notes.md as text, or null when it is a version the pad never holds (too big, not UTF-8). Throws on a read error.
    string? ReadText(Stat s)
    {
        if (s.Size > NotesFormat.MaxBytes) return null;
        var buf = System.IO.File.ReadAllBytes(File);
        if (buf.Length > NotesFormat.MaxBytes || !NotesFormat.ValidUtf8(buf)) return null;
        return NotesFormat.Utf8NoBom.GetString(buf);
    }

    /// Did notes.md change on disk since it was last read or written? A touch with the same text is not a change (NOTE-057).
    public bool ChangedOnDisk()
    {
        lock (_one) {
            var s = StatOf(File);
            if (s.Error != null) return false;
            if (Same(s, _known)) return false;
            if (!s.Exists) return _known is Stat k && k.Exists;
            try {
                string? raw = ReadText(s);
                if (raw != null && _lastText != null && raw == _lastText) { _known = s; return false; }
            } catch (Exception) { /* unreadable: report it changed; the caller loads and says why */ }
            return true;
        }
    }

    (string? Path, string? Reason) CopyAside()
    {
        long t = _nowMs();
        while (_copies.Count > 0 && t - _copies[0] >= NotesFormat.CopyWindowMs) _copies.RemoveAt(0);
        if (_copies.Count >= NotesFormat.MaxCopies)
            return (null, $"it changed outside Mantle {_copies.Count} times in {NotesFormat.CopyWindowMs / 60000} min and each version was already kept as a copy in {Dir}; close the program that keeps changing it (your text stays in the pad and is saved once it stops)");
        string b = $"notes (changed outside Mantle {NotesFormat.Stamp(_localNow())})";
        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(File); } catch (Exception e) { return (null, NotesFormat.Why(e, File)); }
        for (int n = 1; n < 50; n++) {
            string p = Path.Combine(Dir, (n == 1 ? b : $"{b} {n}") + ".md");
            if (System.IO.File.Exists(p) || Directory.Exists(p)) continue;   // never over an earlier copy
            string? bad = _safe.WriteAtomic(p, bytes, p + ".tmp-" + _pid);
            if (bad != null) return (null, $"{p}: {bad}");
            _copies.Add(t);
            return (p, null);
        }
        return (null, $"too many copies named \"{b}\" already in {Dir}");
    }

    static bool Held(string reason) => reason.StartsWith("EBUSY", StringComparison.Ordinal) || reason.StartsWith("EPERM", StringComparison.Ordinal) || reason.StartsWith("EACCES", StringComparison.Ordinal);

    /// <summary>Write the notes (NOTE-043, NOTE-056, NOTE-058, NOTE-062, NOTE-067). One save at a time; the newest call wins.</summary>
    public NotesSaved Save(IReadOnlyList<string>? texts)
    {
        var list = (texts ?? Array.Empty<string>()).Select(t => t ?? "").ToArray();
        lock (_one) return WriteNow(list);
    }

    NotesSaved WriteNow(string[] texts)
    {
        int count = Math.Max(1, texts.Length);
        int chars = texts.Sum(t => t.Length);
        string pre = $"could not save {File}: ";
        if (chars > NotesFormat.MaxBytes)
            return new(false, pre + $"the notes are {NotesFormat.Mb(chars)} of text, over the {NotesFormat.Mb(NotesFormat.MaxBytes)} the pad saves; the text stays in the pad (make it shorter)");
        string? gate = _safe.Gate.Refusal(File) ?? _safe.Gate.Refusal(TmpFile);
        if (gate != null) return new(false, pre + gate);
        if (System.IO.File.Exists(Dir)) return new(false, pre + $"a file is in the way of the folder {Dir}");
        string? dirBad = _safe.EnsureFolder(Dir);
        if (dirBad != null) return new(false, pre + $"the folder could not be made: {NotesFormat.WhyCode(dirBad, Dir)}");
        string out_ = NotesFormat.Serialize(texts);
        byte[] bytes = NotesFormat.Utf8NoBom.GetBytes(out_);
        if (bytes.LongLength > NotesFormat.MaxBytes)
            return new(false, pre + $"the notes are {NotesFormat.Mb(bytes.LongLength)}, over the {NotesFormat.Mb(NotesFormat.MaxBytes)} the pad saves; the text stays in the pad (make it shorter)");
        string? outside = null;
        for (int attempt = 0; ; attempt++) {
            var s = StatOf(File);
            if (s.Error != null) return new(false, $"could not check {File} before saving: {s.Error}", outside);
            if (s.Exists && s.IsDir) return new(false, $"could not save: {File} is a folder, not a file", outside);
            if (s.Exists && !Same(s, _known)) {
                // Changed since it was last read or written (or never read, or refused): keep that version first.
                string? raw;
                try { raw = ReadText(s); }
                catch (Exception e) { return new(false, $"{File} changed outside Mantle and could not be read ({NotesFormat.Why(e, File)}), so it was not overwritten", outside); }
                if (raw != null && raw == out_) { _known = s; _lastText = raw; return new(true, null, outside, true, count, chars, bytes.LongLength); }
                if (raw == null || _lastText == null || raw != _lastText) {
                    var c = CopyAside();
                    if (c.Path == null) return new(false, $"{File} changed outside Mantle and a copy of that version could not be kept ({c.Reason}), so it was not overwritten", outside);
                    outside = c.Path;
                }
            } else if (s.Exists && out_ == _lastText) {
                return new(true, null, outside, true, count, chars, bytes.LongLength);   // identical text on disk: nothing to write
            }
            _safe.Remove(TmpFile);   // a leftover (or a planted link) goes first
            string? err = null;
            bool changed = false;
            for (int i = 0; i < NotesFormat.RetryTries; i++) {
                if (!Same(StatOf(File), s)) { changed = true; break; }   // notes.md must still be the version checked above
                err = _safe.WriteAtomic(File, bytes, TmpFile);
                if (err == null || !Held(err)) break;
                _sleep(NotesFormat.RetryMs);
            }
            _safe.Remove(TmpFile);   // no tmp file ever stays
            if (changed) {
                if (attempt + 1 < NotesFormat.Restarts) continue;   // start over: that version is kept first
                return new(false, pre + $"it kept changing outside Mantle while saving ({NotesFormat.Restarts} tries), so it was not overwritten", outside);
            }
            if (err != null) return new(false, pre + Words(err, File), outside);
            var after = StatOf(File);
            _known = after.Exists ? after : null;
            _lastText = out_;
            return new(true, null, outside, false, count, chars, bytes.LongLength);
        }
    }

    /// SafeFile answers a bare code ("EBUSY (after 3 tries)") or a sentence; codes become NOTE-069's words.
    static string Words(string err, string path)
    {
        int sp = err.IndexOf(' ');
        string code = sp < 0 ? err : err.Substring(0, sp);
        return code.Length > 0 && code.All(ch => ch is >= 'A' and <= 'Z') ? NotesFormat.WhyCode(code, path) : err;
    }

    /// <summary>Keep one deleted note in deleted.md (a dated marker, then its text) BEFORE it leaves notes.md (NOTE-072, NOTE-075).
    /// deleted.md only grows: the old bytes plus the new block are written tmp-and-rename, so a failure leaves it as it was.</summary>
    public NotesSaved AppendDeleted(string? text)
    {
        lock (_one) {
            string pre = $"could not keep the note in {DeletedFile}: ";
            string? gate = _safe.Gate.Refusal(DeletedFile);
            if (gate != null) return new(false, pre + gate);
            string? dirBad = System.IO.File.Exists(Dir) ? $"a file is in the way of the folder {Dir}" : _safe.EnsureFolder(Dir);
            if (dirBad != null) return new(false, pre + dirBad);
            byte[] old = Array.Empty<byte>();
            try {
                var fi = new FileInfo(DeletedFile);
                if (fi.Exists) {
                    if (fi.LinkTarget != null)
                        return new(false, pre + "it is a link to another file, so Mantle did not write into it (move it away and delete again)");
                    old = System.IO.File.ReadAllBytes(DeletedFile);
                }
            } catch (Exception e) { return new(false, pre + NotesFormat.Why(e, DeletedFile)); }
            byte[] block = NotesFormat.Utf8NoBom.GetBytes(NotesFormat.DeletedBlock(text, _localNow()));
            var all = new byte[old.Length + block.Length];
            Buffer.BlockCopy(old, 0, all, 0, old.Length);
            Buffer.BlockCopy(block, 0, all, old.Length, block.Length);
            string tmp = DeletedFile + ".tmp-" + _pid;
            _safe.Remove(tmp);
            string? err = _safe.WriteAtomic(DeletedFile, all, tmp);
            _safe.Remove(tmp);
            return err == null ? new(true) : new(false, pre + Words(err, DeletedFile));
        }
    }

    /// <summary>A last-ditch copy of unsaved notes when Mantle stops and notes.md cannot be written (NOTE-092):
    /// "notes-rescue YYYY-MM-DD HHMMSS.md" in <paramref name="folder"/>, never over an existing file.</summary>
    public static (string? File, string? Reason) WriteRescue(SafeFile safe, string folder, IReadOnlyList<string> texts, DateTime local, int? pid = null)
    {
        string p = Path.Combine(folder, $"notes-rescue {NotesFormat.Stamp(local)}.md");
        if (System.IO.File.Exists(p)) return (null, NotesFormat.WhyCode("EEXIST", p));
        string tmp = p + ".tmp-" + (pid ?? Environment.ProcessId);
        string? err = safe.WriteAtomic(p, NotesFormat.Utf8NoBom.GetBytes(NotesFormat.Serialize(texts)), tmp);
        safe.Remove(tmp);
        return err == null ? (p, null) : (null, Words(err, p));
    }

    /// Rescue files an earlier run left, newest last (names only).
    public static IReadOnlyList<string> ListRescue(string folder)
    {
        try {
            return Directory.GetFiles(folder, "notes-rescue *.md")
                .Where(f => Path.GetFileName(f).StartsWith("notes-rescue ", StringComparison.Ordinal))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToArray();
        } catch (Exception) { return Array.Empty<string>(); }
    }
}
