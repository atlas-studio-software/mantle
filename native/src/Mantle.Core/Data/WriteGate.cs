using System;
using System.IO;
using Mantle.Core.Features.Notes;

namespace Mantle.Core.Data;

/// <summary>
/// THE READ-ONLY GATE (native/docs/contracts.md, "Coordinator decisions for round 2"). Until the switch-over,
/// native must never write the user's real Electron data. Every file write native makes under the data root or
/// the shared look folder goes through <see cref="SafeFile"/>, and SafeFile asks this gate first:
///
///   - a test or scratch profile (<c>--test</c>, or <c>--user-data</c> that is not the user's own folder): allowed;
///   - the user's own profile (<c>%APPDATA%\mantle</c>, <c>%APPDATA%\look-sync</c>): allowed ONLY when the app runs
///     <c>--live</c> AND is the only shell (Electron Mantle is not running). Anything else is refused in words,
///     and the change stays in memory for this run only;
///   - the user's notes folder (lane r11-notes-gate, see "THE NOTES RULE" below): files directly in
///     <c>&lt;profile&gt;\Documents\Mantle Notes</c>, under the same --live + only-shell rule;
///   - any other path: always refused.
///
/// THE NOTES RULE (lane r11-notes-gate). The notes folder is <see cref="NotesPaths.LiveDir"/> of the profile this gate is
/// given (<see cref="NotesProfile"/>). The app builds the gate with <see cref="NotesPaths.UserProfile"/> (the known-folder
/// API, FOLDERID_Profile) and hands the notes feature the SAME string (<c>Host.Gate.NotesProfile</c> in App.xaml.cs), so the
/// pad and the gate can never name two different folders; and because the folder is <c>NotesPaths.LiveDir</c> itself, if
/// NotesPaths ever moves the folder, the gate moves with it. Keep it that way: never build the notes folder here by hand.
/// Allowed: the folder itself (made on the first save) and files DIRECTLY in it (Electron's notes.md, deleted.md, the
/// outside-change copies and their tmp files are all direct children: lib/features/_notes-store.js), in a --live run that
/// is the only shell. Refused, each in plain words: a <c>.</c> / <c>..</c> step, a name ending in a space or a dot, an
/// alternate data stream (<c>x.md:evil</c>, refused for every path), any other path under the literal Documents (a
/// subfolder of Mantle Notes, <c>Mantle Notes2</c>, <c>Documents\x.md</c>), the Documents folder Windows reports when it is
/// not the literal one (OneDrive), a link (junction or symbolic link, any reparse point) at Documents, at Mantle Notes or at
/// the file, a --test run, a run that is not --live, and another shell running. In a --test run nothing under the literal
/// Documents is ever written, not even inside a --test data root.
///
/// The gate fails closed: when nobody has told it whether another shell is running, it assumes one is; a path that cannot
/// be normalised or a link check that throws is refused. ReadOnlyTests proves that no write path skips it.
/// </summary>
public sealed class WriteGate
{
    readonly string? _dataRoot;
    readonly string? _lookDir;
    readonly bool _userData;
    readonly bool _userLook;
    readonly bool _live;
    readonly bool _test;
    readonly Func<string?> _otherShell;
    readonly string? _docsDir, _notesDir, _knownDocs;
    readonly Func<string, bool> _isLink;
    string? _never;

    WriteGate(string? dataRoot, string? lookDir, bool userData, bool userLook, bool live, Func<string?> otherShell,
              bool test = false, string? notesProfile = null, string? knownDocuments = null, Func<string, bool>? isLink = null)
    {
        _dataRoot = dataRoot == null ? null : Norm(dataRoot);
        _lookDir = lookDir == null ? null : Norm(lookDir);
        _userData = userData;
        _userLook = userLook;
        _live = live;
        _test = test;
        _otherShell = otherShell;
        _isLink = isLink ?? IsLink;
        if (!string.IsNullOrWhiteSpace(notesProfile)) {
            try {
                // the folder is NotesPaths' own (see THE NOTES RULE): never rebuilt here
                _docsDir = Norm(NotesPaths.DocsRoot(notesProfile));
                _notesDir = Norm(NotesPaths.LiveDir(notesProfile));
                NotesProfile = notesProfile;
            } catch (Exception) {
                _docsDir = _notesDir = null;   // fail closed: no notes rule, so the notes folder is "outside" and refused
            }
            if (!string.IsNullOrWhiteSpace(knownDocuments)) {
                try { _knownDocs = Norm(knownDocuments); } catch (Exception) { _knownDocs = null; }
            }
        }
    }

    /// <summary>The gate for this run.</summary>
    /// <param name="otherShell">null when native Mantle is the only shell, else plain words naming the other one
    /// ("Electron Mantle is running"). Omitted = not known, which counts as another shell (fail closed).</param>
    /// <param name="notesProfile">lane r11-notes-gate: the user profile the notes folder is built from
    /// (<see cref="NotesPaths.LiveDir"/>); the app passes <see cref="NotesPaths.UserProfile"/>. Omitted = no notes rule
    /// (the notes folder is outside, refused).</param>
    /// <param name="knownDocuments">the Documents folder Windows reports (<see cref="NotesPaths.KnownDocuments"/>), only for
    /// the plain words when a path is in a moved (OneDrive) Documents folder</param>
    /// <param name="isLink">tests: is this existing path a reparse point (missing = false; throwing = refused)</param>
    public static WriteGate For(DataPaths paths, LaunchArgs args, Func<string?>? otherShell = null,
                                string? notesProfile = null, string? knownDocuments = null, Func<string, bool>? isLink = null)
        => new(paths.DataRoot, paths.LookSyncDir, paths.IsUserProfile, paths.IsUserLook, args.Live,
               otherShell ?? (() => "nothing has confirmed that Electron Mantle is not running"),
               args.Test, notesProfile, knownDocuments, isLink);

    /// A gate that allows writes only under <paramref name="root"/> (tests and fixtures).
    public static WriteGate Scratch(string root) => new(root, root, false, false, false, () => null);

    /// A gate that refuses everything with the given reason (tests).
    public static WriteGate Never(string why) => new(null, null, true, true, false, () => why) { _never = why };

    /// <summary>lane r11-notes-gate: the profile the notes rule was built from (null = no notes rule). The notes feature
    /// takes its folder from this same string, so the two never disagree.</summary>
    public string? NotesProfile { get; }

    static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    static bool Under(string path, string? dir)
        => dir != null && (path.Equals(dir, StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when a write to <paramref name="fullPath"/> may go ahead; else why not, in plain words.</summary>
    public string? Refusal(string fullPath)
    {
        if (_never != null) return _never;
        if (fullPath != null && StreamWords(fullPath) is { } stream) return stream;
        string p;
        try { p = Norm(fullPath!); } catch (Exception e) { return "the path could not be checked (" + e.GetType().Name + ")"; }
        bool inData = Under(p, _dataRoot), inLook = Under(p, _lookDir);
        string name = Path.GetFileName(p);
        if (_notesDir != null) {
            string? notes;
            try {
                if (NotesRule(fullPath!, p, inData || inLook, out notes)) return notes;
            } catch (Exception e) {
                return "whether " + name + " may be written could not be checked (" + e.GetType().Name + "), so native Mantle did not write it";
            }
        }
        if (!inData && !inLook) return name + " is outside Mantle's data folders, so native Mantle never writes it";
        bool user = (inData && _userData) || (inLook && _userLook);
        if (!user) return null;
        if (!_live) return "this run is not --live, so the user's own " + name + " is left exactly as Electron Mantle wrote it";
        string? other = _otherShell();
        if (other != null) return other + ", so the user's own " + name + " is left for it (native writes only as the only shell)";
        return null;
    }

    /// An alternate data stream ("x.md:evil"): a colon anywhere after the drive's own "C:". Refused for every path.
    static string? StreamWords(string raw)
    {
        char drive = raw.Length >= 2 ? char.ToLowerInvariant(raw[0]) : '\0';
        int from = raw.Length >= 2 && raw[1] == ':' && drive >= 'a' && drive <= 'z' ? 2 : 0;
        int c = raw.IndexOf(':', from);
        if (c < 0) return null;
        int sep = raw.LastIndexOfAny(new[] { '\\', '/' }, c);
        string seg = raw.Substring(sep + 1);
        return seg + " names an alternate data stream (the part after the colon is hidden inside the file), so native Mantle never writes it";
    }

    /// <summary>THE NOTES RULE (see the class comment). True when <paramref name="p"/> is the notes rule's to decide
    /// (<paramref name="why"/> = null allows it); false when it is not a notes or Documents path at all.</summary>
    bool NotesRule(string raw, string p, bool inMantleFolders, out string? why)
    {
        string notes = _notesDir!, docs = _docsDir!;
        string r = raw.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        bool rawInNotes = r.Equals(notes, StringComparison.OrdinalIgnoreCase)
                          || r.StartsWith(notes + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        bool inDocs = Under(p, docs);
        bool inKnown = !inDocs && !inMantleFolders && _knownDocs != null && Under(p, _knownDocs);
        why = null;
        if (!rawInNotes && !inDocs && !inKnown) return false;
        string name = Path.GetFileName(p);
        if (name.Length == 0) name = p;

        foreach (var seg in r.Split(Path.DirectorySeparatorChar)) {
            if (seg is "." or "..") {
                why = name + ": the path steps out of its folder with \"" + seg + "\", so native Mantle never writes it (only files directly in the Mantle Notes folder are written)";
                return true;
            }
        }
        foreach (var seg in r.Split(Path.DirectorySeparatorChar)) {
            if (seg.Length > 0 && (seg[^1] == ' ' || seg[^1] == '.')) {
                why = "\"" + seg + "\" in the path ends in a space or a dot, which Windows drops, so the name is not what it looks like and native Mantle never writes " + name;
                return true;
            }
        }
        if (!inDocs) {
            why = name + " is in the Documents folder Windows reports (moved, for example into OneDrive), not in %USERPROFILE%\\Documents where Mantle's notes are kept, so native Mantle never writes it";
            return true;
        }
        bool isFolder = p.Equals(notes, StringComparison.OrdinalIgnoreCase);
        bool child = !isFolder && string.Equals(Path.GetDirectoryName(p), notes, StringComparison.OrdinalIgnoreCase);
        if (!isFolder && !child) {
            why = Under(p, notes)
                ? name + " is in a folder inside Mantle Notes; Mantle's notes are only files directly in the Mantle Notes folder, so native Mantle never writes it"
                : name + " is in the user's Documents but not in the Mantle Notes folder, so native Mantle never writes it";
            return true;
        }
        if (_test) {
            why = "this run is --test, so nothing in the user's Documents folder is ever written (not even " + name + " in the notes folder)";
            return true;
        }
        if (!_live) {
            why = "this run is not --live, so the user's notes (" + name + ") are left exactly as Electron Mantle wrote them";
            return true;
        }
        string? other = _otherShell();
        if (other != null) {
            why = other + ", so the user's notes (" + name + ") are left for it (native writes only as the only shell)";
            return true;
        }
        why = LinkWords(isFolder ? null : p, name);
        return true;
    }

    /// A junction or symbolic link (any reparse point) at Documents, at Mantle Notes or at the file itself: refused, because
    /// the write would land somewhere else. A check that throws refuses too (fail closed).
    string? LinkWords(string? file, string name)
    {
        var steps = new (string? At, string What)[] { (_docsDir, "the Documents folder"), (_notesDir, "the Mantle Notes folder"), (file, name) };
        foreach (var (at, what) in steps) {
            if (at == null) continue;
            bool link;
            try { link = _isLink(at); }
            catch (Exception e) { return "whether " + what + " is a link could not be checked (" + e.GetType().Name + "), so native Mantle did not write " + name; }
            if (link) return what + " is a link (a junction or symbolic link) to somewhere else, so native Mantle does not write " + name + " through it";
        }
        return null;
    }

    /// Is this existing path a reparse point (junction, symbolic link, or any other)? Missing = false. The attributes are the
    /// link's own (GetFileAttributes does not follow a link).
    static bool IsLink(string path)
    {
        FileAttributes a;
        try { a = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        return (a & FileAttributes.ReparsePoint) != 0;
    }

    /// True when writes to the user's own profile would be allowed right now (for the log line at start).
    public string Describe()
    {
        if (!_userData && !_userLook) return "writes go to this run's own profile only";
        if (!_live) return "read-only: this run is not --live, so the user's settings are never written";
        string? other = _otherShell();
        return other != null ? "read-only: " + other : "writing the user's settings (--live, the only shell)";
    }
}
