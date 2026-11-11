using System;
using System.IO;
using System.Linq;
using Mantle.Core.Features.Downloads;
using Xunit;

namespace Mantle.Core.Tests.Downloads;

/// <summary>lib/features/downloads.test.js, the pure half (_downloads-list.js and ui/bar/downloads.js ago/size/kindOf/shown).</summary>
public class DownloadsListTests
{
    const long Now = 1_800_000_000_000;

    [Fact]
    public void The_shelf_is_newest_arrival_first_and_counts_partials_and_hidden_files()
    {
        var e = new DownloadEntry?[]
        {
            new("old.pdf", Size: 10, BirthMs: Now - 900_000, MtimeMs: Now - 900_000),
            new("copied.zip", Size: 20, BirthMs: Now - 1000, MtimeMs: Now - 9_000_000),   // arrival = the later of the two
            new("Setup.exe.crdownload", MtimeMs: Now - 5000), new("film.part", MtimeMs: Now - 20 * 60_000),   // one live, one abandoned
            new("desktop.ini"), new("Thumbs.db"), new(".hidden"), new("~$report.docx"), new("x.opdownload"), new(null),
            new("Folder", Dir: true, Size: 999, BirthMs: Now - 100_000),
        };
        var s = DownloadsList.MakeShelf(e, seenAt: Now - 200_000, now: Now);
        Assert.Equal(new[] { "copied.zip", "Folder", "old.pdf" }, s.Items.Select(i => i.Name));
        Assert.Equal(2, s.Partial);       // the live .crdownload and the .opdownload with no write time
        Assert.Equal(6, s.Skipped);       // the abandoned .part, four hidden names, the nameless entry
        Assert.Equal(2, s.NewCount);
        Assert.True(s.Items[0].Fresh);
        Assert.False(s.Items[2].Fresh);
        Assert.Equal(0, s.Items[1].Size); // a folder has no size
        Assert.Single(DownloadsList.MakeShelf(e, 0, Now, max: 1).Items);
    }

    [Fact]
    public void Skip_reasons_cover_every_partial_extension_and_the_conventional_hidden_names()
    {
        foreach (var n in new[] { "a.crdownload", "a.part", "a.partial", "a.TMP", "a.opdownload" }) Assert.Equal("partial", DownloadsList.SkipReason(n));
        foreach (var n in new[] { "", "desktop.ini", "THUMBS.DB", "ehthumbs.db", ".DS_Store", ".git", "~$lock.xlsx" }) Assert.Equal("hidden", DownloadsList.SkipReason(n));
        Assert.Null(DownloadsList.SkipReason("report.pdf"));
    }

    [Fact]
    public void Size_wording_is_in_1024_units_with_one_decimal_under_ten()
    {
        Assert.Equal("812 B", DownloadsList.Size(812));
        Assert.Equal("14 KB", DownloadsList.Size(14 * 1024));
        Assert.Equal("2.4 MB", DownloadsList.Size((long)(2.4 * 1048576)));
        Assert.Equal("1.2 GB", DownloadsList.Size((long)(1.2 * 1073741824)));
        Assert.Equal("0 B", DownloadsList.Size(-5));
    }

    [Fact]
    public void Age_wording_from_just_now_to_a_date()
    {
        var utc = TimeZoneInfo.Utc;
        long now = new DateTimeOffset(2026, 11, 11, 15, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal("just now", DownloadsList.Ago(now - 30_000, now, utc));
        Assert.Equal("just now", DownloadsList.Ago(now + 5_000, now, utc));   // a small clock skew
        Assert.Equal("5 min ago", DownloadsList.Ago(now - 5 * 60_000, now, utc));
        Assert.Equal("3 h ago", DownloadsList.Ago(now - 3 * 3_600_000, now, utc));
        Assert.Equal("yesterday", DownloadsList.Ago(now - 30 * 3_600_000, now, utc));
        Assert.Equal("4 days ago", DownloadsList.Ago(now - 4 * 86_400_000L + 3_600_000, now, utc));
        Assert.Equal("Sep 20", DownloadsList.Ago(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), now, utc));
        Assert.Equal("Dec 1, 2025", DownloadsList.Ago(new DateTimeOffset(2025, 12, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), now, utc));
        Assert.Equal("", DownloadsList.Ago(0, now, utc));
        Assert.Equal("Folder · just now", DownloadsList.Meta(new ShelfItem("f", true, 0, now, false), now, utc));
    }

    [Fact]
    public void Kinds_bidi_names_and_sentences()
    {
        Assert.Equal("image", DownloadsList.KindOf("a.PNG", false));
        Assert.Equal("app", DownloadsList.KindOf("setup.appref-ms", false));
        Assert.Equal("doc", DownloadsList.KindOf("x.pdf", false));
        Assert.Equal("folder", DownloadsList.KindOf("x.pdf", true));
        Assert.Equal("file", DownloadsList.KindOf("noext", false));
        Assert.Equal("invoicefdp.exe", DownloadsList.Shown("invoice‮fdp.exe"));
        Assert.Equal("The folder is gone.", DownloadsList.Sentence("the folder is gone"));
        Assert.Equal("Done!", DownloadsList.Sentence(" Done! "));
    }

    [Fact]
    public void The_plain_read_lists_a_real_folder_and_says_why_in_plain_words_when_it_cannot()
    {
        var dir = Directory.CreateTempSubdirectory("mantle-dl-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(dir, "b.crdownload"), "x");
            File.WriteAllText(Path.Combine(dir, "desktop.ini"), "x");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var r = DownloadsList.ListFolder(dir);
            Assert.True(r.Ok);
            Assert.Equal("plain read", r.Via);
            Assert.Equal(4, r.Total);
            Assert.Equal(5, r.Items.Single(i => i.Name == "a.txt").Size);
            Assert.True(r.Items.Single(i => i.Name == "sub").Dir);
            var s = DownloadsList.MakeShelf(r.Items, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(1, s.Partial);
            Assert.Equal(2, s.Items.Count);

            var missing = DownloadsList.ListFolder(Path.Combine(dir, "nope"));
            Assert.False(missing.Ok);
            Assert.Equal("missing", missing.Why!.State);
            Assert.Equal("the Downloads folder does not exist (it was moved, renamed or deleted)", missing.Why.Reason);
            var file = DownloadsList.ListFolder(Path.Combine(dir, "a.txt"));
            Assert.Equal("the Downloads location is a file, not a folder", file.Why!.Reason);
            foreach (var w in new[] { missing.Why.Reason, file.Why.Reason, DownloadsList.FsWhy(new UnauthorizedAccessException(), "list").Reason, DownloadsList.FsWhy(new IOException("E_FAIL"), "list").Reason })
            {
                Assert.DoesNotMatch(DownloadsList.Jargon, w);
                Assert.DoesNotContain(dir, w);
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
