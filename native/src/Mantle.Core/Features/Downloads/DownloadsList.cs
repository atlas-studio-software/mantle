using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Downloads;

/// <summary>One entry of a Downloads read (the sidecar's downloads.list shape). Times are Unix ms.</summary>
public sealed record DownloadEntry(string? Name, bool Dir = false, long Size = 0, long BirthMs = 0, long MtimeMs = 0);

/// <summary>A row of the shelf.</summary>
public sealed record ShelfItem(string Name, bool Dir, long Size, long At, bool Fresh);

/// <summary>What the shelf shows: the newest rows, how many are new, still downloading, skipped, and listed.</summary>
public sealed record Shelf(IReadOnlyList<ShelfItem> Items, int NewCount, int Partial, int Skipped, int Shown);

/// <summary>A failed folder read in plain words: state missing/unreadable, the code for the log only.</summary>
public sealed record DownloadsWhy(string State, string Code, string Reason);

/// <summary>The plain read's answer (downloads.list's shape).</summary>
public sealed record DownloadsRead(bool Ok, string Folder, string Via, IReadOnlyList<DownloadEntry> Items, int Total = 0, int Unspellable = 0,
    DownloadsWhy? Why = null);

/// <summary>
/// What the Downloads shelf shows (lib/features/_downloads-list.js) and the shelf's wording (ui/bar/downloads.js ago,
/// size, meta, kindOf, shown, sentence). Pure except <see cref="ListFolder"/>. PRIVACY: nothing here logs, and no
/// reason carries a file name or a path.
/// </summary>
public static class DownloadsList
{
    public static readonly IReadOnlySet<string> PartialExts = new HashSet<string>(StringComparer.Ordinal) { ".crdownload", ".part", ".partial", ".tmp", ".opdownload" };
    static readonly HashSet<string> Junk = new(StringComparer.Ordinal) { "desktop.ini", "thumbs.db", "ehthumbs.db", ".ds_store" };
    /// <summary>A partial unwritten this long is an abandoned one, not a download.</summary>
    public const long PartialStaleMs = 10 * 60 * 1000;
    /// <summary>Jargon a reason must never put on screen.</summary>
    public static readonly Regex Jargon = new(@"\b(E[A-Z]{3,}|HRESULT|0x[0-9a-fA-F]{4,}|[Ss]idecar|[Ee]lectron|hwnd|IPC|[Rr]enderer|dryRun|undefined|null|NaN|\w+Exception)\b|\[object");

    /// <summary>"partial", "hidden" or null (shown).</summary>
    public static string? SkipReason(string? name)
    {
        var low = (name ?? "").ToLowerInvariant();
        if (low.Length == 0) return "hidden";
        if (Junk.Contains(low) || low.StartsWith('.') || low.StartsWith("~$", StringComparison.Ordinal)) return "hidden";
        int dot = low.LastIndexOf('.');
        if (dot > 0 && PartialExts.Contains(low[dot..])) return "partial";
        return null;
    }

    /// <summary>Newest ARRIVAL: the later of the creation and the last-write time.</summary>
    public static long Arrival(DownloadEntry e) => Math.Max(e.BirthMs, e.MtimeMs);

    /// <summary>The shelf: newest first (at most <paramref name="max"/>), partials and hidden ones counted, never rows.</summary>
    public static Shelf MakeShelf(IEnumerable<DownloadEntry?>? entries, long seenAt, long now, int max = 12)
    {
        var keep = new List<DownloadEntry>(); int partial = 0, skipped = 0;
        foreach (var e in entries ?? Array.Empty<DownloadEntry?>())
        {
            if (e?.Name == null) { skipped++; continue; }
            var why = SkipReason(e.Name);
            if (why == "partial") { if (e.MtimeMs > 0 && now - e.MtimeMs > PartialStaleMs) skipped++; else partial++; continue; }
            if (why != null) { skipped++; continue; }
            keep.Add(e);
        }
        keep.Sort(ByArrival);
        int newCount = keep.Count(e => Arrival(e) > seenAt);
        var items = keep.Take(Math.Max(0, max)).Select(e => new ShelfItem(e.Name!, e.Dir, e.Dir ? 0 : Math.Max(0, e.Size), Arrival(e), Arrival(e) > seenAt)).ToList();
        return new(items, newCount, partial, skipped, keep.Count);
    }

    static int ByArrival(DownloadEntry a, DownloadEntry b)
    {
        int c = Arrival(b).CompareTo(Arrival(a));
        return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>The words for a failed folder read (never the path an exception's message carries).</summary>
    public static DownloadsWhy FsWhy(Exception? err, string doing)
    {
        return err switch
        {
            DirectoryNotFoundException or FileNotFoundException => new("missing", "ENOENT", "the Downloads folder does not exist (it was moved, renamed or deleted)"),
            IOException io when IsNotDir(io) => new("missing", "ENOTDIR", "the Downloads location is a file, not a folder"),
            UnauthorizedAccessException => new("unreadable", "EACCES", $"Windows did not let Mantle {doing} the Downloads folder (access denied)"),
            _ => new("unreadable", "error", $"the Downloads folder could not be {(doing == "list" ? "listed" : "read")}: Windows reported a disk or network error"),
        };
    }
    static bool IsNotDir(IOException io) => (io.HResult & 0xFFFF) == 267;   // ERROR_DIRECTORY: the name is not a folder

    /// <summary>The plain read (only when the helper cannot answer): the newest <paramref name="max"/> entries plus every
    /// partial and hidden name (uncut, so the shelf can count them). Never throws.</summary>
    public static DownloadsRead ListFolder(string folder, int max = 60)
    {
        try
        {
            if (File.Exists(folder)) return new(false, folder, "plain read", Array.Empty<DownloadEntry>(), Why: new("missing", "ENOTDIR", "the Downloads location is a file, not a folder"));
            var di = new DirectoryInfo(folder);
            var named = new List<DownloadEntry>(); var outp = new List<DownloadEntry>(); int total = 0, unspellable = 0;
            foreach (var fsi in di.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                var name = fsi.Name;
                if (SkipReason(name) != null) { named.Add(new(name)); continue; }
                if (name.Contains('�')) { unspellable++; continue; }
                bool dir = (fsi.Attributes & FileAttributes.Directory) != 0;
                DownloadEntry e;
                try { e = new(name, dir, dir ? 0 : ((FileInfo)fsi).Length, Ms(fsi.CreationTimeUtc), Ms(fsi.LastWriteTimeUtc)); }
                catch (Exception) { e = new(name, dir); }
                outp.Add(e); total++;
                if (outp.Count > max * 2) { outp.Sort(ByArrival); outp.RemoveRange(max, outp.Count - max); }
            }
            outp.Sort(ByArrival);
            return new(true, folder, "plain read", outp.Take(max).Concat(named).ToList(), total + named.Count, unspellable);
        }
        catch (Exception ex)
        {
            return new(false, folder, "plain read", Array.Empty<DownloadEntry>(), Why: FsWhy(ex, "list"));
        }
    }
    static long Ms(DateTime utc) => utc.Year < 1971 ? 0 : new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    /// <summary>"just now", "2 min ago", "3 h ago", "yesterday", "4 days ago", then "Sep 20" (with the year when not this year).</summary>
    public static string Ago(long ms, long now, TimeZoneInfo? zone = null)
    {
        if (ms <= 0) return "";
        long d = now - ms;
        if (d < 60000) return "just now";
        if (d < 3600000) return $"{d / 60000} min ago";
        if (d < 86400000) return $"{d / 3600000} h ago";
        zone ??= TimeZoneInfo.Local;
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(now), zone);
        var midnight = new DateTimeOffset(today.Date, today.Offset).ToUnixTimeMilliseconds();
        var days = (long)Math.Ceiling((midnight - ms) / 86400000.0);
        if (days <= 1) return "yesterday";
        if (days < 7) return $"{days} days ago";
        var t = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone);
        var en = CultureInfo.GetCultureInfo("en-US");
        return t.Year != today.Year ? t.ToString("MMM d, yyyy", en) : t.ToString("MMM d", en);
    }

    /// <summary>Windows' units (1024), one decimal under 10: "812 B", "14 KB", "2.4 MB", "1.2 GB".</summary>
    public static string Size(long bytes)
    {
        double b = Math.Max(0, bytes);
        if (b < 1024) return $"{(long)b} B";
        var u = new[] { "KB", "MB", "GB", "TB" }; int i = -1;
        do { b /= 1024; i++; } while (b >= 1024 && i < u.Length - 1);
        var n = b < 10 ? Math.Round(b * 10, MidpointRounding.AwayFromZero) / 10 : Math.Round(b, MidpointRounding.AwayFromZero);
        return n.ToString(CultureInfo.InvariantCulture) + " " + u[i];
    }

    /// <summary>"2.4 MB · 3 min ago" / "Folder · yesterday".</summary>
    public static string Meta(ShelfItem it, long now, TimeZoneInfo? zone = null) =>
        string.Join(" · ", new[] { it.Dir ? "Folder" : Size(it.Size), Ago(it.At, now, zone) }.Where(s => s.Length > 0));

    static readonly Dictionary<string, string> KindOfExt = BuildKinds();
    static Dictionary<string, string> BuildKinds()
    {
        var kinds = new (string Kind, string Exts)[]
        {
            ("image", "png jpg jpeg gif webp bmp tif tiff heic heif svg ico avif raw cr2 nef psd"),
            ("video", "mp4 mov mkv avi webm wmv m4v flv mpg mpeg 3gp"),
            ("audio", "mp3 wav flac m4a aac ogg opus wma mid midi"),
            ("archive", "zip rar 7z tar gz tgz bz2 xz zst cab"),
            ("app", "exe msi msix msixbundle appx appxbundle bat cmd ps1 appref-ms apk jar"),
            ("disk", "iso img vhd vhdx dmg"),
            ("sheet", "xls xlsx xlsm csv ods numbers tsv"),
            ("slides", "ppt pptx odp key"),
            ("code", "js mjs cjs ts tsx jsx json html htm css py cs cpp c h java go rs rb php sh xml yml yaml toml ini sql ipynb"),
            ("doc", "pdf doc docx odt rtf txt md epub pages log"),
        };
        var m = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, exts) in kinds) foreach (var x in exts.Split(' ')) m[x] = k;
        return m;
    }

    /// <summary>The row's glyph kind: image, video, audio, archive, app, disk, sheet, slides, code, doc, folder or file.</summary>
    public static string KindOf(string? name, bool dir)
    {
        if (dir) return "folder";
        var m = Regex.Match(name ?? "", @"\.([a-z0-9-]{1,12})$", RegexOptions.IgnoreCase);
        return m.Success && KindOfExt.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var k) ? k : "file";
    }

    /// <summary>A name as drawn: bidi controls out, so "invoice&lt;RLO&gt;fdp.exe" cannot pass for a PDF.</summary>
    public static string Shown(string? name) => Regex.Replace(name ?? "", "[؜‎‏‪-‮⁦-⁩]", "");

    /// <summary>A clause as a sentence: a capital first, a full stop last.</summary>
    public static string Sentence(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0) return "";
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return Regex.IsMatch(t, "[.!?…]$") ? t : t + ".";
    }
}
