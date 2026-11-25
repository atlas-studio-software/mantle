using System;
using System.IO;

namespace Mantle.Core.Features.Notes;

/// <summary>
/// lane r10-notes: where the notes live (lib/features/notes.js liveDir / notesDir, NOTE-052, NOTE-101).
/// Live: &lt;profile&gt;\Documents\Mantle Notes (the literal Documents under the profile, never the OneDrive Documents known
/// folder): the SAME folder Electron Mantle writes, so native reads the user's notes as they are (the import is reading,
/// nothing is copied). lane r11-notes-gate: a --live run that is the only shell may now SAVE there, through the write gate's
/// notes rule (Data/WriteGate.cs, "THE NOTES RULE"): only files directly in this folder.
/// --test never touches it: MANTLE_NOTES_DIR or &lt;data root&gt;\notes, and a folder under Documents is refused (and the gate
/// refuses everything under the literal Documents in a --test run anyway).
///
/// ONE PROFILE, TWO USERS (lane r11-notes-gate): the write gate builds its notes folder with <see cref="LiveDir"/> of the
/// profile it is given, and the app gives it <see cref="UserProfile"/>; the notes feature takes the gate's own
/// <c>NotesProfile</c>. So the pad and the gate always name the same folder, and if <see cref="LiveDir"/> ever changes,
/// the gate's allowed folder changes with it. Keep the folder defined only here.
/// </summary>
public static class NotesPaths
{
    public const string DirEnv = "MANTLE_NOTES_DIR";

    public sealed record Choice(string Dir, string? Note);

    public static string DocsRoot(string userProfile) => Path.Combine(userProfile, "Documents");

    public static string LiveDir(string userProfile) => Path.Combine(DocsRoot(userProfile), "Mantle Notes");

    /// <summary>lane r11-notes-gate: THE source of the profile for the notes folder, for both the write gate and the notes
    /// feature (see the class comment): the known-folder API's profile folder (FOLDERID_Profile, which
    /// Environment.SpecialFolder.UserProfile asks for). Null when Windows does not say (then nothing is written there).</summary>
    public static string? UserProfile()
    {
        string p;
        try { p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch (Exception) { return null; }
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    /// <summary>The Documents folder Windows reports (FOLDERID_Documents): with OneDrive backup it is OneDrive\Documents.
    /// Notes never use it; it is only compared with the literal one, for the log line and the gate's words.</summary>
    public static string? KnownDocuments()
    {
        string p;
        try { p = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch (Exception) { return null; }
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    /// <summary>The one log line (once per run, App.xaml.cs) when Windows' Documents folder is not the literal
    /// &lt;profile&gt;\Documents; null when they are the same or either is unknown. No path in it (the log names no paths).</summary>
    public static string? DocumentsNote(string? userProfile, string? knownDocuments)
    {
        if (string.IsNullOrWhiteSpace(userProfile) || string.IsNullOrWhiteSpace(knownDocuments)) return null;
        try {
            string lit = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DocsRoot(userProfile)));
            string known = Path.TrimEndingDirectorySeparator(Path.GetFullPath(knownDocuments));
            if (lit.Equals(known, StringComparison.OrdinalIgnoreCase)) return null;
        } catch (Exception) {
            return "notes: Windows' Documents folder could not be compared with %USERPROFILE%\\Documents; the notes are kept only in %USERPROFILE%\\Documents\\Mantle Notes";
        }
        return "notes: Windows' Documents folder is not %USERPROFILE%\\Documents (it was moved, for example into OneDrive); the notes are kept only in %USERPROFILE%\\Documents\\Mantle Notes, the folder Electron Mantle uses, and the moved Documents folder is never written";
    }

    static bool Inside(string dir, string root)
    {
        string d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return d.Equals(r, StringComparison.OrdinalIgnoreCase) || d.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="env">reads an environment variable (tests pass their own)</param>
    public static Choice Resolve(bool test, string userProfile, string dataRoot, string tempDir, Func<string, string?> env)
    {
        if (!test) return new Choice(LiveDir(userProfile), null);
        string fallback = Path.Combine(dataRoot, "notes");
        string? e = env(DirEnv)?.Trim();
        string dir = string.IsNullOrEmpty(e) ? fallback : Path.GetFullPath(e);
        string? note = null;
        string docs = DocsRoot(userProfile);
        if (Inside(dir, docs)) {
            note = $"--test refused the notes folder {dir} (under Documents: your real files); using {fallback}";
            dir = fallback;
        }
        if (Inside(dir, docs)) dir = Path.Combine(tempDir, "mantle-notes-test");
        return new Choice(dir, note);
    }
}
