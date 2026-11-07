using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Mantle.Core.Data;

/// What a read found. Anything but Ok means the defaults are in Value and Reason says why.
public enum ReadState
{
    /// The file was read and used (Skipped may still list fields that were not).
    Ok,
    /// No file: a fresh profile, or Electron has not written it yet.
    Missing,
    /// Not valid JSON, or not the shape Electron writes.
    Corrupt,
    /// The file is there but could not be opened (locked, no access).
    Unreadable,
    /// Bigger than any file Electron writes there.
    TooLarge,
    /// look.json from a newer schema: nothing in it is used (look-sync's own rule).
    Newer,
}

/// <summary>One file's read: the value to use (never null for the settings types), what happened, and why.</summary>
public sealed class ReadResult<T>
{
    public T Value { get; }
    public ReadState State { get; }
    /// Plain words for every state but a clean Ok.
    public string? Reason { get; }
    /// Fields that were present but unusable, each with its reason (the rest of the file was used).
    public IReadOnlyList<string> Skipped { get; }
    /// The file's name as the log shows it (relative to its folder, never a full path).
    public string Name { get; }
    /// Last-write stamp of what was read ("missing" when there was no file).
    public string Stamp { get; }

    internal ReadResult(T value, ReadState state, string? reason, IReadOnlyList<string>? skipped, string name, string stamp)
    {
        Value = value; State = state; Reason = reason; Skipped = skipped ?? Array.Empty<string>(); Name = name; Stamp = stamp;
    }

    public bool UsedDefaults => State != ReadState.Ok;

    /// "theme.json: ok" / "theme.json: corrupt (it is not valid JSON ...); using the defaults".
    public string Describe(string? valueWords = null)
    {
        var sb = new StringBuilder(Name).Append(": ").Append(State.ToString().ToLowerInvariant());
        if (Reason != null) sb.Append(" (").Append(Reason).Append(')');
        if (State != ReadState.Ok) sb.Append("; using the defaults");
        if (valueWords != null) sb.Append(": ").Append(valueWords);
        if (Skipped.Count > 0) sb.Append("; not used: ").Append(string.Join("; ", Skipped));
        return sb.ToString();
    }
}

/// <summary>
/// The ONLY way Mantle.Core.Data touches a file: open for reading, sharing read, write and delete
/// with whoever else has it (Electron writes theme.json etc. as tmp + rename, and must never be
/// blocked by native holding the file). Nothing in this namespace creates, writes, moves or deletes
/// a file or a folder; ReadOnlyTests prove it on disk and in the source.
/// </summary>
internal static class ReadOnlyFile
{
    internal static string StampOf(string path)
    {
        try {
            var fi = new FileInfo(path);
            fi.Refresh();
            return fi.Exists ? fi.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + fi.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : "missing";
        } catch (Exception e) {
            return "error:" + e.GetType().Name;
        }
    }

    /// Read the whole file as UTF-8 text (a BOM is dropped). state is Ok, Missing, Unreadable or TooLarge.
    internal static string? TryRead(string path, long maxBytes, out ReadState state, out string? reason)
    {
        try {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            long len = fs.Length;
            if (len > maxBytes) {
                state = ReadState.TooLarge;
                reason = $"it is {len} bytes; Electron never writes more than {maxBytes} there";
                return null;
            }
            var buf = new byte[len];
            int got = 0;
            while (got < len) {
                int n = fs.Read(buf, got, (int)len - got);
                if (n == 0) break;
                got += n;
            }
            int skip = got >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF ? 3 : 0;
            state = ReadState.Ok;
            reason = null;
            return new UTF8Encoding(false, false).GetString(buf, skip, got - skip);
        } catch (FileNotFoundException) {
            state = ReadState.Missing; reason = "no such file"; return null;
        } catch (DirectoryNotFoundException) {
            state = ReadState.Missing; reason = "its folder does not exist"; return null;
        } catch (UnauthorizedAccessException e) {
            state = ReadState.Unreadable; reason = "access denied (" + e.GetType().Name + ")"; return null;
        } catch (IOException e) {
            state = ReadState.Unreadable; reason = "it could not be read (" + e.GetType().Name + ": " + e.Message + ")"; return null;
        }
    }
}
