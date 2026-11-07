using System;
using System.IO;
using System.Threading;

namespace Mantle.Core.Data;

/// <summary>
/// The ONLY code in Mantle.Core.Data and Mantle.Core.Look that changes a file. Every method asks the
/// <see cref="WriteGate"/> first and does nothing at all when it refuses, so the read-only promise (native
/// never writes the user's Electron data before the switch-over) has exactly one place to hold.
/// ReadOnlyTests fails when a writing call appears in any other file of those two folders.
///
/// Every method answers null on success, else plain words; none throws.
/// </summary>
public sealed class SafeFile
{
    public WriteGate Gate { get; }

    public SafeFile(WriteGate gate) => Gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// Node's error code for an exception (ENOENT, EPERM, EBUSY, ...), for messages that must read like Electron's.
    internal static string Code(Exception e) => e switch {
        FileNotFoundException => "ENOENT",
        DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EPERM",
        PathTooLongException => "ENAMETOOLONG",
        IOException io when (io.HResult & 0xFFFF) == 32 => "EBUSY",    // ERROR_SHARING_VIOLATION
        IOException io when (io.HResult & 0xFFFF) == 33 => "EBUSY",    // ERROR_LOCK_VIOLATION
        IOException io when (io.HResult & 0xFFFF) == 80 || (io.HResult & 0xFFFF) == 183 => "EEXIST",
        IOException io when (io.HResult & 0xFFFF) == 112 => "ENOSPC",
        IOException => "EIO",
        _ => e.GetType().Name,
    };

    static bool Retryable(string code) => code is "EBUSY" or "EPERM" or "EACCES";

    /// <summary>
    /// Write <paramref name="bytes"/> to <paramref name="tmpPath"/>, then rename it over <paramref name="path"/>
    /// (Electron's tmp-and-rename). The folder is made when missing. A rename refused as busy is tried again
    /// after each delay in <paramref name="retryMs"/>. On failure the tmp file is removed and the old file is untouched.
    /// </summary>
    public string? WriteAtomic(string path, byte[] bytes, string tmpPath, ReadOnlySpan<int> retryMs = default)
    {
        string? no = Gate.Refusal(path) ?? Gate.Refusal(tmpPath);
        if (no != null) return no;
        try {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                fs.Write(bytes);
                fs.Flush(true);
            }
        } catch (Exception e) {
            TryRemove(tmpPath);
            return "the new copy could not be written (" + Code(e) + ")";
        }
        for (int attempt = 0; ; attempt++) {
            try {
                File.Move(tmpPath, path, overwrite: true);
                return null;
            } catch (Exception e) {
                string code = Code(e);
                if (attempt < retryMs.Length && Retryable(code)) { Thread.Sleep(retryMs[attempt]); continue; }
                TryRemove(tmpPath);
                return code + (attempt > 0 ? " (after " + (attempt + 1) + " tries)" : "");
            }
        }
    }

    /// Rename <paramref name="path"/> to <paramref name="aside"/> (never over an existing file): the
    /// <c>.unreadable</c> rule and look.json's <c>.bad-&lt;time&gt;</c> copies.
    public string? MoveAside(string path, string aside)
    {
        string? no = Gate.Refusal(path) ?? Gate.Refusal(aside);
        if (no != null) return no;
        try { File.Move(path, aside, overwrite: false); return null; }
        catch (Exception e) { return Code(e); }
    }

    /// <summary>Make a folder (and its parents) when it is missing: the look-sync folder before it is watched
    /// (look-sync.js <c>mkdirSync(dir, {recursive: true})</c>). An existing folder is success and asks nothing.</summary>
    public string? EnsureFolder(string dir)
    {
        if (Directory.Exists(dir)) return null;
        string? no = Gate.Refusal(dir);
        if (no != null) return no;
        try { Directory.CreateDirectory(dir); return null; }
        catch (Exception e) { return Code(e); }
    }

    /// Remove one file this code made (a tmp left by a crash, an old still). Missing is success.
    public string? Remove(string path)
    {
        string? no = Gate.Refusal(path);
        if (no != null) return no;
        return TryRemove(path);
    }

    string? TryRemove(string path)
    {
        if (Gate.Refusal(path) != null) return "refused";
        try { if (File.Exists(path)) File.Delete(path); return null; }
        catch (Exception e) { return Code(e); }
    }
}
