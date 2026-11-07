using System;
using System.Collections.Concurrent;
using System.IO;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests;

public class WatcherTests
{
    static DataPaths PathsFor(TestDir d) => DataPaths.Resolve(LaunchArgs.Parse(new[] { "--user-data=" + d.Path }), _ => null, Path.GetTempPath());

    static DataChange Next(BlockingCollection<DataChange> q, DataFile want, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) {
            if (q.TryTake(out var c, TimeSpan.FromMilliseconds(250)) && c.File == want) return c;
        }
        throw new Xunit.Sdk.XunitException($"no {want} change within {seconds} s");
    }

    [Fact]
    public void Electrons_tmp_and_rename_save_shows_up_live()
    {
        using var d = new TestDir("watch");
        d.CopyFixtureProfile();
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        Assert.Equal(ThemeMode.Dark, data.Theme.Value.Mode);
        var q = new BlockingCollection<DataChange>();
        data.Changed += c => q.Add(c);
        data.StartWatching(TimeSpan.FromSeconds(30));   // a slow poll: the watcher has to carry this one

        d.WriteLikeElectron("theme.json", "{\n  \"mode\": \"light\"\n}");
        Next(q, DataFile.Theme);
        Assert.Equal(ThemeMode.Light, data.Theme.Value.Mode);
    }

    [Fact]
    public void A_file_in_a_folder_that_appears_later_is_picked_up_by_the_poll()
    {
        using var d = new TestDir("watch");
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        Assert.Equal(ReadState.Missing, data.SharedLook.State);
        var q = new BlockingCollection<DataChange>();
        data.Changed += c => q.Add(c);
        data.StartWatching(TimeSpan.FromMilliseconds(300));

        d.WriteLikeElectron(@"look-sync\look.json", "{\"schema\":1,\"rev\":1,\"look\":{\"theme\":{\"v\":\"dark\",\"rev\":1,\"by\":\"quartz\"}}}");
        Next(q, DataFile.SharedLook);
        Assert.Equal(ThemeMode.Dark, data.SharedLook.Value.Theme);
    }

    [Fact]
    public void A_deleted_file_falls_back_to_defaults_and_says_missing()
    {
        using var d = new TestDir("watch");
        d.CopyFixtureProfile();
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        var q = new BlockingCollection<DataChange>();
        data.Changed += c => q.Add(c);
        data.StartWatching(TimeSpan.FromMilliseconds(500));
        File.Delete(d.File("features.json"));
        var c = Next(q, DataFile.Features);
        Assert.Equal(ReadState.Missing, data.Features.State);
        Assert.Contains("missing", c.Description);
    }

    [Fact]
    public void A_file_turned_corrupt_is_reported_and_the_defaults_used()
    {
        using var d = new TestDir("watch");
        d.CopyFixtureProfile();
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        var q = new BlockingCollection<DataChange>();
        data.Changed += c => q.Add(c);
        data.StartWatching(TimeSpan.FromMilliseconds(500));
        d.WriteLikeElectron("theme.json", "{\"mode\": ");
        var c = Next(q, DataFile.Theme);
        Assert.Equal(ReadState.Corrupt, data.Theme.State);
        Assert.Equal(ThemeMode.System, data.Theme.Value.Mode);
        Assert.Contains("corrupt", c.Description);
    }

    [Fact]
    public void Nothing_changed_nothing_raised()
    {
        using var d = new TestDir("watch");
        d.CopyFixtureProfile();
        using var data = new MantleData(PathsFor(d), new ListLog());
        data.Load();
        int n = 0;
        data.Changed += _ => n++;
        data.CheckNow();
        data.CheckNow();
        Assert.Equal(0, n);
    }
}
