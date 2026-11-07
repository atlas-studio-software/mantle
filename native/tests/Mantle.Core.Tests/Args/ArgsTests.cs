using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Mantle.Core.Tests;

public class ArgsTests
{
    static LaunchArgs P(params string[] a) => LaunchArgs.Parse(a);

    [Fact]
    public void Spike_args_still_work()
    {
        var a = P(@"X:\bin\MantleNative.exe", "--theme=dark", "--bench-out=\"X:\\bench out\"", "--no-backdrop");
        Assert.True(a.Ok, string.Join("; ", a.Errors));
        Assert.Equal("dark", a.Theme);
        Assert.Equal(@"X:\bench out", a.BenchOut);
        Assert.True(a.NoBackdrop);
        Assert.False(a.Test);
        Assert.Equal(3, a.Raw.Count);   // the exe path is not an argument
    }

    [Fact]
    public void Theme_is_case_insensitive_and_validated()
    {
        Assert.Equal("light", P("--theme=LIGHT").Theme);
        Assert.Equal("system", P("--theme=system").Theme);
        var bad = P("--theme=purple");
        Assert.False(bad.Ok);
        Assert.Contains("purple", bad.Errors[0]);
        Assert.Null(bad.Theme);
    }

    [Fact]
    public void No_theme_means_follow_the_settings()
    {
        Assert.Null(P().Theme);
    }

    [Fact]
    public void Test_needs_a_profile_name()
    {
        var a = P("--test");
        Assert.False(a.Ok);
        Assert.Contains("--profile-name", a.Errors[0]);
        Assert.True(P("--test", "--profile-name=lane-1").Ok);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("-lead")]
    [InlineData("has space")]
    [InlineData("slash/x")]
    [InlineData("back\\x")]
    [InlineData("quote\"x")]
    public void Profile_names_that_could_escape_the_folder_are_refused(string name)
    {
        var a = P("--test", "--profile-name=" + name);
        Assert.False(a.Ok);
    }

    [Fact]
    public void Profile_name_length_limit_is_64()
    {
        Assert.True(P("--test", "--profile-name=" + new string('a', 64)).Ok);
        Assert.False(P("--test", "--profile-name=" + new string('a', 65)).Ok);
    }

    [Fact]
    public void Live_and_test_are_opposites()
    {
        Assert.False(P("--live", "--test", "--profile-name=x").Ok);
        Assert.False(P("--live", "--quit").Ok);
        Assert.True(P("--live").Ok);
        Assert.True(P("--test", "--profile-name=x", "--quit").Ok);
    }

    [Fact]
    public void Flag_names_are_exact()
    {
        var a = P("--test-live-bar", "--testing", "--test=1", "--profile-name=x");
        Assert.True(a.Test);   // "--test=1" is the test flag (lib/args.js rule)
        Assert.Equal(new List<string> { "--testing" }, a.Unknown);   // --test-live-bar is one of Electron's --test-only switches (CORE-007)
        Assert.Equal("test", LaunchArgs.NameOf("--test"));
        Assert.Equal("test-live", LaunchArgs.NameOf("--test-live=1"));
        Assert.Null(LaunchArgs.NameOf("-t"));
        Assert.Null(LaunchArgs.NameOf("plain"));
    }

    [Fact]
    public void User_data_must_be_a_full_path()
    {
        Assert.False(P("--user-data=relative\\dir").Ok);
        Assert.False(P("--user-data=").Ok);
        var a = P(@"--user-data=X:\fixtures\p1");
        Assert.True(a.Ok);
        Assert.Equal(@"X:\fixtures\p1", a.UserData);
    }

    [Fact]
    public void Empty_bench_out_is_an_error()
    {
        Assert.False(P("--bench-out=").Ok);
    }

    [Fact]
    public void Unknown_args_are_kept_not_fatal()
    {
        var a = P("--some-future-flag=1", "--theme=dark");
        Assert.True(a.Ok);
        Assert.Equal(new List<string> { "--some-future-flag=1" }, a.Unknown);
    }
}

public class PathsTests
{
    static System.Func<string, string?> Env(Dictionary<string, string> d) => k => d.TryGetValue(k, out var v) ? v : null;

    [Fact]
    public void Live_root_is_appdata_mantle()
    {
        var env = Env(new() { ["APPDATA"] = @"X:\Roaming" });
        var p = DataPaths.Resolve(LaunchArgs.Parse(new string[0]), env, @"X:\Temp\");
        Assert.Equal(@"X:\Roaming\mantle", p.DataRoot);
        Assert.Equal(@"X:\Roaming\look-sync", p.LookSyncDir);
        Assert.Equal(@"X:\Roaming\mantle\mantle-native.log", p.LogFile);
        Assert.True(p.IsUserProfile);
    }

    [Fact]
    public void Test_root_is_electrons_test_folder_and_never_the_users()
    {
        var env = Env(new() { ["APPDATA"] = @"X:\Roaming" });
        var p = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--test", "--profile-name=lane-a" }), env, @"X:\Temp\");
        Assert.Equal(@"X:\Temp\mantle-test-lane-a", p.DataRoot);
        Assert.Equal(@"X:\Temp\mantle-test-lane-a\look-sync", p.LookSyncDir);   // never %APPDATA%\look-sync
        Assert.Equal(@"X:\Temp\mantle-test-lane-a\mantle-native.log", p.LogFile);
        Assert.False(p.IsUserProfile);
        Assert.DoesNotContain("Roaming", p.DataRoot + p.LookSyncDir + p.LogFile);
    }

    [Fact]
    public void User_data_wins_and_keeps_the_shared_look_inside()
    {
        var env = Env(new() { ["APPDATA"] = @"X:\Roaming" });
        var p = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--test", "--profile-name=a", @"--user-data=X:\fx\p\" }), env, @"X:\Temp");
        Assert.Equal(@"X:\fx\p", p.DataRoot);
        Assert.Equal(@"X:\fx\p\look-sync", p.LookSyncDir);
    }

    [Fact]
    public void Look_sync_dir_env_is_honoured()
    {
        var env = Env(new() { ["APPDATA"] = @"X:\Roaming", ["LOOK_SYNC_DIR"] = @"X:\shared" });
        var p = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--test", "--profile-name=a" }), env, @"X:\Temp");
        Assert.Equal(@"X:\shared", p.LookSyncDir);
    }

    [Fact]
    public void Quiet_windows_only_in_test_runs_without_the_opt_out()
    {
        var test = LaunchArgs.Parse(new[] { "--test", "--profile-name=a" });
        var live = LaunchArgs.Parse(new string[0]);
        Assert.True(TestQuiet.Wanted(test, _ => null));
        Assert.False(TestQuiet.Wanted(test, k => k == "MANTLE_TEST_VISIBLE" ? "1" : null));
        Assert.True(TestQuiet.Wanted(test, k => k == "MANTLE_TEST_VISIBLE" ? "0" : null));
        Assert.False(TestQuiet.Wanted(live, _ => null));
    }
}

public class InstanceKeyTests
{
    [Fact]
    public void Same_root_same_key_whatever_the_spelling()
    {
        string a = InstanceKey.For(@"X:\Roaming\mantle");
        Assert.Equal(a, InstanceKey.For(@"x:\roaming\MANTLE\"));
        Assert.Equal(a, InstanceKey.For("X:/Roaming/mantle"));
        Assert.Equal(a, InstanceKey.For(@"X:\Roaming\other\..\mantle"));
        Assert.StartsWith("MantleNative-", a);
        Assert.Equal("MantleNative-".Length + 16, a.Length);
    }

    [Fact]
    public void A_test_profile_never_collides_with_the_users_instance()
    {
        var env = new Dictionary<string, string> { ["APPDATA"] = @"X:\Roaming" };
        string user = InstanceKey.For(DataPaths.Resolve(LaunchArgs.Parse(new string[0]), k => env.TryGetValue(k, out var v) ? v : null, @"X:\Temp").DataRoot);
        string t1 = InstanceKey.For(DataPaths.Resolve(LaunchArgs.Parse(new[] { "--test", "--profile-name=one" }), k => env.TryGetValue(k, out var v) ? v : null, @"X:\Temp").DataRoot);
        string t2 = InstanceKey.For(DataPaths.Resolve(LaunchArgs.Parse(new[] { "--test", "--profile-name=two" }), k => env.TryGetValue(k, out var v) ? v : null, @"X:\Temp").DataRoot);
        Assert.NotEqual(user, t1);
        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void Names_are_session_scoped_and_never_electrons()
    {
        string k = InstanceKey.For(@"X:\r\mantle");
        Assert.StartsWith(@"Local\MantleNative-", InstanceKey.MutexName(k));
        Assert.EndsWith("-s3", InstanceKey.PipeName(k, 3));
    }
}
