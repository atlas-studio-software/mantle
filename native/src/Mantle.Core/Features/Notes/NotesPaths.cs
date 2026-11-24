using System;
using System.IO;

namespace Mantle.Core.Features.Notes;

/// <summary>
/// lane r10-notes: where the notes live (lib/features/notes.js liveDir / notesDir, NOTE-052, NOTE-101).
/// Live: %USERPROFILE%\Documents\Mantle Notes (literally USERPROFILE, never the OneDrive Documents known folder): the
/// SAME folder Electron Mantle writes, so native reads the user's notes as they are (the import is reading, nothing is
/// copied). That folder is outside Mantle's data folders, so the write gate refuses every write there today: a native
/// run saves nothing into it until the gate is told it may (the switch-over), and the pad says so instead of saving.
/// --test never touches it: MANTLE_NOTES_DIR or &lt;data root&gt;\notes, and a folder under Documents is refused.
/// </summary>
public static class NotesPaths
{
    public const string DirEnv = "MANTLE_NOTES_DIR";

    public sealed record Choice(string Dir, string? Note);

    public static string DocsRoot(string userProfile) => Path.Combine(userProfile, "Documents");

    public static string LiveDir(string userProfile) => Path.Combine(DocsRoot(userProfile), "Mantle Notes");

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
