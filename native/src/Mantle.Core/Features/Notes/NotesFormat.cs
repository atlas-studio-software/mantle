using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mantle.Core.Features.Notes;

/// <summary>
/// lane r10-notes: the quick notes FILE format, pure (lib/features/_notes-store.js serialize / parse / escapeText,
/// ui/notes/notes.js label / fullTitle). Several notes in ONE Markdown file the user can read by hand:
/// <code>
///   &lt;!-- mantle:note --&gt;
///   first note's text
///   &lt;!-- mantle:note --&gt;
///   second note's text
/// </code>
/// Each note = the marker line, then its text, then "\n". A file without any marker is ONE note. CRLF reads as LF,
/// a UTF-8 BOM is dropped, text above the first marker is a note of its own. A text line that looks like a marker gets
/// one more leading backslash on write and loses one on read, so any text round-trips exactly.
/// Nothing here ever puts note text into a reason: reasons carry paths and codes only.
/// </summary>
public static class NotesFormat
{
    public const string Marker = "<!-- mantle:note -->";
    const string MarkerPrefix = "<!-- mantle:note";
    public const string DeletedMarkerPrefix = "<!-- mantle:deleted ";
    public const long MaxBytes = 8L * 1024 * 1024;   // the most the pad opens or saves
    public const int MaxNotes = 500;                 // the most notes (tabs) the pad opens
    public const int MaxCopies = 10;                 // outside-change copies ...
    public const long CopyWindowMs = 10 * 60 * 1000; // ... per 10 minutes
    public const int Restarts = 3;                   // a save that finds notes.md changed under it starts over this often
    public const int RetryTries = 10, RetryMs = 100; // rename retries (~1 s)
    public const int TabChars = 18, TitleChars = 120;

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// "8.0 MB" (Electron's mb()).
    public static string Mb(long n) => (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    static string P2(int n) => n.ToString("00", CultureInfo.InvariantCulture);

    /// "2026-09-27 211403" (local time): the outside-change copy's and the rescue file's name.
    public static string Stamp(DateTime t) => $"{t.Year:0000}-{P2(t.Month)}-{P2(t.Day)} {P2(t.Hour)}{P2(t.Minute)}{P2(t.Second)}";

    /// "2026-09-27 21:14:03" (local time): deleted.md's marker.
    public static string StampHuman(DateTime t) => $"{t.Year:0000}-{P2(t.Month)}-{P2(t.Day)} {P2(t.Hour)}:{P2(t.Minute)}:{P2(t.Second)}";

    /// Valid UTF-8? (Electron's buffer.isUtf8)
    public static bool ValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try { StrictUtf8.GetCharCount(bytes); return true; } catch (DecoderFallbackException) { return false; }
    }

    static bool IsMarker(string line) => line.StartsWith(MarkerPrefix, StringComparison.Ordinal);

    /// /^\\*&lt;!-- mantle:note/: zero or more backslashes, then the marker's start.
    static bool NeedsEscape(string line) => line.TrimStart('\\').StartsWith(MarkerPrefix, StringComparison.Ordinal);

    /// /^\\+&lt;!-- mantle:note/: one or more backslashes, then the marker's start.
    static bool HasEscape(string line) => line.Length > 0 && line[0] == '\\' && NeedsEscape(line);

    static string Lf(string? s) => (s ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// Any text line that could be read as a marker gets one more leading backslash; "\r" becomes "\n".
    public static string EscapeText(string? text)
    {
        var lines = Lf(text).Split('\n');
        for (int i = 0; i < lines.Length; i++) if (NeedsEscape(lines[i])) lines[i] = "\\" + lines[i];
        return string.Join("\n", lines);
    }

    /// texts -> the file's text. Never empty: no notes is written as one empty note.
    public static string Serialize(IReadOnlyList<string>? texts)
    {
        var sb = new StringBuilder();
        if (texts == null || texts.Count == 0) return Marker + "\n\n";
        foreach (var t in texts) sb.Append(Marker).Append('\n').Append(EscapeText(t)).Append('\n');
        return sb.ToString();
    }

    public sealed record Parsed(IReadOnlyList<string> Texts, int Markers, bool Preamble);

    /// The file's text -> its notes. PURE.
    public static Parsed Parse(string? raw)
    {
        string s = raw ?? "";
        if (s.Length > 0 && s[0] == '﻿') s = s.Substring(1);
        s = s.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (s.EndsWith('\n')) s = s.Substring(0, s.Length - 1);   // the last note's own "\n"
        var lines = s.Split('\n');
        int first = Array.FindIndex(lines, IsMarker);
        if (first < 0) return new Parsed(new[] { s }, 0, false);   // hand-written: one note, as is
        var texts = new List<string>();
        string pre = string.Join("\n", lines, 0, first);
        bool preamble = pre.Trim().Length > 0;
        if (preamble) texts.Add(pre);                               // a hand edit above the first marker is kept
        List<string>? body = null;
        int markers = 0;
        for (int i = first; i < lines.Length; i++) {
            if (IsMarker(lines[i])) {
                if (body != null) texts.Add(Unescape(body));
                body = new List<string>();
                markers++;
            } else body!.Add(lines[i]);
        }
        texts.Add(Unescape(body!));
        return new Parsed(texts, markers, preamble);
    }

    static string Unescape(List<string> lines)
    {
        for (int i = 0; i < lines.Count; i++) if (HasEscape(lines[i])) lines[i] = lines[i].Substring(1);
        return string.Join("\n", lines);
    }

    /// One deleted note's block in deleted.md: a dated marker line, then its text.
    public static string DeletedBlock(string? text, DateTime local) => DeletedMarkerPrefix + StampHuman(local) + " -->\n" + EscapeText(text) + "\n";

    static int FirstNonSpace(string s)
    {
        for (int i = 0; i < s.Length; i++) if (!char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    /// <summary>A tab's words: the first line holding text, trimmed and cut to 18 chars with "…" (NOTE-025). Only that one
    /// line is read, so an 8 MB note costs no more than a short one (NOTE-040). "" = a blank note ("New note", dimmed).</summary>
    public static string Label(string? text)
    {
        string s = text ?? "";
        int i = FirstNonSpace(s);
        if (i < 0) return "";
        int nl = s.IndexOf('\n', i);
        int end = Math.Min(nl < 0 ? s.Length : nl, i + 4096);
        string line = s.Substring(i, end - i).TrimEnd();
        return line.Length > TabChars ? line.Substring(0, TabChars - 1).TrimEnd() + "…" : line;
    }

    /// The tooltip of a truncated tab: the whole first line up to 120 chars ("…" past that); "" when the tab is not cut (NOTE-026).
    public static string FullTitle(string? text, string words)
    {
        if (!words.EndsWith('…')) return "";
        string s = text ?? "";
        int i = Math.Max(0, FirstNonSpace(s));
        int nl = s.IndexOf('\n', i);
        int end = Math.Min(nl < 0 ? s.Length : nl, i + TitleChars + 1);
        string line = s.Substring(i, end - i).TrimEnd();
        return line.Length > TitleChars ? line.Substring(0, TitleChars - 1) + "…" : line;
    }

    /// <summary>Plain words for a file-system failure at <paramref name="path"/> (NOTE-069). Paths and codes only, never text.</summary>
    public static string Why(Exception e, string path) => WhyCode(CodeOf(e), path, e.GetType().Name);

    public static string WhyCode(string code, string path, string? other = null) => code switch {
        "ENOSPC" => "the disk is full",
        "EACCES" or "EPERM" => $"Windows refused access to {path} (read-only, or another program holds it)",
        "EBUSY" => $"{path} is in use by another program",
        "EROFS" => $"the drive of {path} is read-only",
        "ENOENT" => $"{path} does not exist",
        "EISDIR" => $"{path} is a folder, not a file",
        "ENOTDIR" or "EEXIST" => $"a file is in the way of the folder {path}",
        _ => $"{path}: {(string.IsNullOrEmpty(code) ? other ?? "unknown error" : code)}",
    };

    /// Node's error code for an exception (the same table as Data/SafeFile.Code, plus the read-only drive).
    public static string CodeOf(Exception e) => e switch {
        System.IO.FileNotFoundException => "ENOENT",
        System.IO.DirectoryNotFoundException => "ENOENT",
        UnauthorizedAccessException => "EPERM",
        System.IO.PathTooLongException => "ENAMETOOLONG",
        System.IO.IOException io when (io.HResult & 0xFFFF) == 32 || (io.HResult & 0xFFFF) == 33 => "EBUSY",
        System.IO.IOException io when (io.HResult & 0xFFFF) == 80 || (io.HResult & 0xFFFF) == 183 => "EEXIST",
        System.IO.IOException io when (io.HResult & 0xFFFF) == 112 => "ENOSPC",
        System.IO.IOException io when (io.HResult & 0xFFFF) == 19 => "EROFS",   // ERROR_WRITE_PROTECT
        System.IO.IOException => "EIO",
        _ => e.GetType().Name,
    };
}
