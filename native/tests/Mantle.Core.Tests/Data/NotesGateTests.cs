using System;
using System.IO;
using Mantle.Core.Data;
using Mantle.Core.Features.Notes;
using Mantle.Core.Safety;
using Xunit;

namespace Mantle.Core.Tests.Data;

/// <summary>
/// lane r11-notes-gate: THE NOTES RULE of the write gate (Data/WriteGate.cs). Native may save the user's notes only as files
/// directly in &lt;profile&gt;\Documents\Mantle Notes (NotesPaths.LiveDir), in a --live run that is the only shell. Every
/// other case is refused in plain words, and each refusal is its own test. The profile is a fake string (never the real
/// one), the Documents folder Windows reports is a fake string, and the link check is a fake, so nothing here reads or
/// touches the real Documents folder. The one test that writes files uses a fresh temp profile.
/// </summary>
public sealed class NotesGateTests : IDisposable
{
    const string Profile = @"C:\Users\notes-gate-fake";
    static readonly string Docs = Path.Combine(Profile, "Documents");
    static readonly string Notes = Path.Combine(Docs, "Mantle Notes");
    static readonly string NotesMd = Path.Combine(Notes, "notes.md");

    readonly TestDir _appData = new("notes-gate-appdata");
    public void Dispose() => _appData.Dispose();

    /// The gate a run with these flags gets. Defaults: the only shell, Windows' Documents is the literal one, no links.
    WriteGate Gate(string[] argv, Func<string?>? otherShell = null, string? knownDocuments = null, Func<string, bool>? isLink = null,
                   bool unknownShell = false)
    {
        var args = LaunchArgs.Parse(argv);
        var paths = DataPaths.Resolve(args, k => k == "APPDATA" ? _appData.Path : null, Path.GetTempPath());
        return WriteGate.For(paths, args, unknownShell ? null : otherShell ?? (() => null), Profile, knownDocuments ?? Docs,
            isLink ?? (_ => false));
    }

    WriteGate Live(Func<string?>? otherShell = null, string? knownDocuments = null, Func<string, bool>? isLink = null)
        => Gate(new[] { "--live" }, otherShell, knownDocuments, isLink);

    // ---- 1. allowed

    [Fact]
    public void Allowed_a_file_directly_in_Mantle_Notes_in_a_live_run_that_is_the_only_shell()
    {
        var g = Live();
        Assert.Equal(Profile, g.NotesProfile);
        Assert.Null(g.Refusal(NotesMd));
        Assert.Null(g.Refusal(NotesMd + ".tmp-4242"));                 // NotesStore's tmp file is a direct child too
        Assert.Null(g.Refusal(Path.Combine(Notes, "deleted.md")));
        Assert.Null(g.Refusal(Notes));                                  // the folder itself (made on the first save)
        Assert.Null(g.Refusal(NotesMd.Replace('\\', '/')));            // the same file written with forward slashes
        Assert.Null(g.Refusal(NotesMd.ToUpperInvariant()));            // Windows names are not case-sensitive
    }

    [Fact]
    public void Allowed_the_literal_Documents_even_when_Windows_reports_a_moved_one()
    {
        Assert.Null(Live(knownDocuments: Path.Combine(Profile, "OneDrive", "Documents")).Refusal(NotesMd));
    }

    // ---- 2. refused, each with its plain reason

    [Fact]
    public void Refused_a_dot_or_dot_dot_step_anywhere_in_the_path()
    {
        var g = Live();
        string[] dotDot = {
            Path.Combine(Notes, "..", "Mantle Notes", "notes.md"),         // in Mantle Notes, normalises back into it
            Docs + @"\Other\..\Mantle Notes\notes.md",                       // in Documents
            Profile + @"\Desktop\..\Documents\Mantle Notes\notes.md",        // before Documents
            Notes + @"\..\..\AppData\Roaming\mantle\theme.json",             // out of the notes folder altogether
        };
        foreach (var p in dotDot) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("steps out of its folder with \"..\"", why, StringComparison.Ordinal);
        }
        string? dot = g.Refusal(Notes + @"\.\notes.md");
        Assert.NotNull(dot);
        Assert.Contains("steps out of its folder with \".\"", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_another_path_in_Documents()
    {
        var g = Live();
        foreach (var p in new[] { Path.Combine(Docs, "notes.md"), Path.Combine(Docs, "Other", "notes.md"), Docs }) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("is in the user's Documents but not in the Mantle Notes folder", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refused_Mantle_Notes2_a_folder_whose_name_only_starts_like_the_notes_folder()
    {
        var g = Live();
        foreach (var p in new[] { Path.Combine(Docs, "Mantle Notes2", "notes.md"), Path.Combine(Docs, "Mantle Notes2") }) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("is in the user's Documents but not in the Mantle Notes folder", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refused_a_subfolder_of_Mantle_Notes()
    {
        var g = Live();
        foreach (var p in new[] { Path.Combine(Notes, "sub", "notes.md"), Path.Combine(Notes, "sub", "deeper", "x.md") }) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("is in a folder inside Mantle Notes", why, StringComparison.Ordinal);
        }
        // making the subfolder itself: a bare path cannot tell a folder from a file, so the folder question is its own
        string? folder = g.FolderRefusal(Path.Combine(Notes, "sub"));
        Assert.NotNull(folder);
        Assert.Contains("would be a folder inside Mantle Notes", folder, StringComparison.Ordinal);
        Assert.Null(g.FolderRefusal(Notes));   // the notes folder itself is made on the first save
    }

    [Fact]
    public void Refused_an_alternate_data_stream()
    {
        var g = Live();
        foreach (var p in new[] { Path.Combine(Notes, "x.md:evil"), NotesMd + "::$DATA", Path.Combine(_appData.Path, "mantle", "theme.json:evil") }) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("names an alternate data stream", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refused_a_name_ending_in_a_space_or_a_dot()
    {
        var g = Live();
        string[] odd = {
            NotesMd + ".",                                   // Windows would save notes.md
            NotesMd + " ",
            Docs + @"\Mantle Notes.\notes.md",               // the folder name itself
            Docs + @"\Mantle Notes \notes.md",
        };
        foreach (var p in odd) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("ends in a space or a dot", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refused_the_Documents_folder_Windows_reports_when_it_was_moved_into_OneDrive()
    {
        string oneDrive = Path.Combine(Profile, "OneDrive", "Documents");
        var g = Live(knownDocuments: oneDrive);
        foreach (var p in new[] { Path.Combine(oneDrive, "Mantle Notes", "notes.md"), Path.Combine(oneDrive, "Mantle Notes") }) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("is in the Documents folder Windows reports", why, StringComparison.Ordinal);
            Assert.Contains("OneDrive", why, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("documents", "the Documents folder is a link")]
    [InlineData("notes", "the Mantle Notes folder is a link")]
    [InlineData("file", "notes.md is a link")]
    public void Refused_a_junction_or_symbolic_link_at_Documents_at_Mantle_Notes_or_at_the_file(string where, string words)
    {
        string at = where switch { "documents" => Docs, "notes" => Notes, _ => NotesMd };
        var g = Live(isLink: p => string.Equals(Path.TrimEndingDirectorySeparator(p), at, StringComparison.OrdinalIgnoreCase));
        string? why = g.Refusal(NotesMd);
        Assert.NotNull(why);
        Assert.Contains(words, why, StringComparison.Ordinal);
        Assert.Contains("junction or symbolic link", why, StringComparison.Ordinal);
        if (where != "file") Assert.NotNull(g.Refusal(Notes));          // a linked folder is not made either
    }

    [Fact]
    public void Refused_when_the_link_check_itself_fails_fail_closed()
    {
        var g = Live(isLink: _ => throw new UnauthorizedAccessException());
        string? why = g.Refusal(NotesMd);
        Assert.NotNull(why);
        Assert.Contains("could not be checked (UnauthorizedAccessException)", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_another_drive()
    {
        var g = Live();
        foreach (var p in new[] { @"D:\Users\notes-gate-fake\Documents\Mantle Notes\notes.md", @"D:\Mantle Notes\notes.md" }) {
            string? why = g.Refusal(p);
            Assert.NotNull(why);
            Assert.Contains("is outside Mantle's data folders", why, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refused_every_user_file_in_a_run_that_is_not_live()
    {
        var g = Gate(Array.Empty<string>());
        string? notes = g.Refusal(NotesMd);
        Assert.NotNull(notes);
        Assert.Contains("this run is not --live", notes, StringComparison.Ordinal);
        string? theme = g.Refusal(Path.Combine(_appData.Path, "mantle", "theme.json"));   // the user's own profile, too
        Assert.NotNull(theme);
        Assert.Contains("this run is not --live", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_every_file_in_Documents_in_a_test_run()
    {
        var g = Gate(new[] { "--test", "--profile-name=notes-gate" });
        string? why = g.Refusal(NotesMd);
        Assert.NotNull(why);
        Assert.Contains("this run is --test", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Refused_in_a_live_run_while_Electron_runs_or_while_nobody_has_said()
    {
        string? running = Live(otherShell: () => "Electron Mantle is running").Refusal(NotesMd);
        Assert.NotNull(running);
        Assert.Contains("Electron Mantle is running, so the user's notes (notes.md) are left for it", running, StringComparison.Ordinal);
        string? unknown = Gate(new[] { "--live" }, unknownShell: true).Refusal(NotesMd);
        Assert.NotNull(unknown);
        Assert.Contains("nothing has confirmed that Electron Mantle is not running", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_notes_profile_the_notes_folder_is_outside_and_refused()
    {
        var args = LaunchArgs.Parse(new[] { "--live" });
        var paths = DataPaths.Resolve(args, k => k == "APPDATA" ? _appData.Path : null, Path.GetTempPath());
        var g = WriteGate.For(paths, args, () => null);
        Assert.Null(g.NotesProfile);
        string? why = g.Refusal(NotesMd);
        Assert.NotNull(why);
        Assert.Contains("is outside Mantle's data folders", why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_notes_rule_leaves_the_users_own_profile_rule_as_it_was()
    {
        string theme = Path.Combine(_appData.Path, "mantle", "theme.json");
        Assert.Null(Live().Refusal(theme));
        Assert.Contains("Electron Mantle is running", Live(otherShell: () => "Electron Mantle is running").Refusal(theme));
    }

    // ---- 3. one profile for the gate and the pad, and the one log line

    [Fact]
    public void The_apps_gate_takes_the_known_folder_profile_that_the_notes_folder_is_built_from()
    {
        var args = LaunchArgs.Parse(new[] { "--live" });
        var paths = DataPaths.Resolve(args, k => k == "APPDATA" ? _appData.Path : null, Path.GetTempPath());
        var g = TwoShells.Gate(paths, args, k => k == "APPDATA" ? _appData.Path : k == "LOCALAPPDATA" ? _appData.Path : null);
        Assert.Equal(NotesPaths.UserProfile(), g.NotesProfile);
        Assert.NotNull(g.NotesProfile);   // Windows always reports a profile folder on the runner
    }

    [Fact]
    public void The_log_line_comes_only_when_Windows_Documents_is_not_the_literal_one_and_names_no_path()
    {
        string? moved = NotesPaths.DocumentsNote(Profile, Path.Combine(Profile, "OneDrive", "Documents"));
        Assert.NotNull(moved);
        Assert.Contains("it was moved, for example into OneDrive", moved, StringComparison.Ordinal);
        Assert.DoesNotContain("notes-gate-fake", moved, StringComparison.OrdinalIgnoreCase);
        Assert.Null(NotesPaths.DocumentsNote(Profile, Docs));
        Assert.Null(NotesPaths.DocumentsNote(Profile, Docs.ToUpperInvariant() + @"\"));
        Assert.Null(NotesPaths.DocumentsNote(Profile, null));
        Assert.Null(NotesPaths.DocumentsNote(null, Docs));
    }

    // ---- the allowed case with real files: SafeFile through the gate's real link check, on a fresh temp profile

    [Fact]
    public void A_live_only_shell_run_really_saves_into_a_temp_profiles_Mantle_Notes_and_nowhere_else()
    {
        using var prof = new TestDir("notes-gate-profile");
        var args = LaunchArgs.Parse(new[] { "--live" });
        var paths = DataPaths.Resolve(args, k => k == "APPDATA" ? _appData.Path : null, Path.GetTempPath());
        var safe = new SafeFile(WriteGate.For(paths, args, () => null, prof.Path, NotesPaths.DocsRoot(prof.Path)));
        string dir = NotesPaths.LiveDir(prof.Path), file = Path.Combine(dir, "notes.md");

        Assert.Null(safe.EnsureFolder(dir));
        Assert.Null(safe.WriteAtomic(file, new byte[] { 0x68, 0x69 }, file + ".tmp-1"));
        Assert.Equal("hi", File.ReadAllText(file));
        Assert.False(File.Exists(file + ".tmp-1"));

        string beside = Path.Combine(NotesPaths.DocsRoot(prof.Path), "beside.md");
        Assert.NotNull(safe.WriteAtomic(beside, new byte[] { 1 }, beside + ".tmp"));
        Assert.False(File.Exists(beside));

        string sub = Path.Combine(dir, "sub");
        Assert.Contains("would be a folder inside Mantle Notes", safe.EnsureFolder(sub), StringComparison.Ordinal);
        Assert.False(Directory.Exists(sub));
        Assert.Equal(new[] { file }, Directory.GetFileSystemEntries(dir));   // nothing else appeared in Mantle Notes
    }
}
