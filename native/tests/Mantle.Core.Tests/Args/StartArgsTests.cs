using System.Linq;
using Xunit;

namespace Mantle.Core.Tests;

// lib/args.test.js and lib/start-wake.test.js as the spec.
public class StartArgsTests
{
    static LaunchArgs P(params string[] a) => LaunchArgs.Parse(a);

    [Fact]
    public void CORE001_a_start_with_neither_live_nor_test_is_refused_with_the_live_line_and_code_2()
    {
        Assert.Equal(RunKind.Refused, P().Kind);
        Assert.Equal(RunKind.Refused, P("--theme=dark").Kind);
        Assert.Equal(RunKind.Live, P("--live").Kind);
        Assert.Equal(RunKind.Test, P("--test", "--profile-name=a").Kind);
        Assert.Equal(RunKind.Scratch, P(@"--user-data=X:\s").Kind);
        Assert.StartsWith("mantle: live mode changes your Windows taskbar. Run with --live to accept", StartLines.LiveGate);
        Assert.Equal(2, ExitCodes.BadArgs);
    }

    [Fact]
    public void CORE002_live_and_test_together_are_refused_in_electrons_words()
    {
        var a = P("--live", "--test", "--profile-name=a");
        Assert.Equal("--live and --test are opposites: --live changes the Windows shell, --test promises not to. Pick one.", a.Errors[0]);
    }

    [Fact]
    public void CORE004_test_only_flags_are_refused_outside_test_and_bar_edge_is_top_or_bottom()
    {
        Assert.Contains("--screenshot only works with --test", P("--live", @"--screenshot=X:\s").Errors[0]);
        Assert.Contains("--flyout only works with --test", P("--flyout=qs").Errors[0]);
        Assert.Contains("--first-run only works with --test", P("--first-run").Errors[0]);
        Assert.Contains("--theme only works with --test", P("--live", "--theme=dark").Errors[0]);
        Assert.Contains("--screenshot needs a directory", P("--test", "--profile-name=a", "--screenshot=").Errors.Last());
        Assert.Equal("bottom", P("--live").BarEdge);
        Assert.Equal("top", P("--live", "--bar-edge=top").BarEdge);
        Assert.Contains("--bar-edge must be \"top\" or \"bottom\"", P("--live", "--bar-edge=left").Errors[0]);
        Assert.True(P("--test", "--profile-name=a", @"--screenshot=X:\s", "--flyout=qs", "--first-run").Ok);
    }

    [Fact]
    public void CORE005_quit_and_open_cannot_be_combined_with_live_or_each_other()
    {
        Assert.Contains("--quit only asks a running Mantle to stop", P("--quit", "--live").Errors[0]);
        Assert.Contains("--open only asks a running Mantle to open a window", P("--open=player", "--live").Errors[0]);
        Assert.Contains("--open and --quit ask a running Mantle for opposite things", P("--open=player", "--quit").Errors[0]);
        Assert.Equal(RunKind.Quit, P("--quit").Kind);
        Assert.Equal(RunKind.Open, P("--open=wallpapers").Kind);
    }

    [Fact]
    public void CORE006_CORE007_flags_main_reads_itself_are_known_and_test_switches_are_ignored_outside_test()
    {
        var a = P("--live", "--dock", "--no-edgeclip", "--no-tbhide", "--log=X:\\m.log", "--no-desktop", "--no-acrylic", "--no-supervise", "--test-crash");
        Assert.True(a.Ok);
        Assert.Empty(a.Unknown);
        Assert.True(a.Has("dock") && a.Has("no-tbhide"));
        Assert.False(a.Desktop);
        Assert.False(a.Acrylic);
        Assert.True(a.NoSupervise);
        Assert.Equal(@"X:\m.log", a.LogPath);
        Assert.Equal(new[] { "--test-crash" }, a.IgnoredTestSwitches().ToArray());
        Assert.Empty(P("--test", "--profile-name=a", "--test-crash").IgnoredTestSwitches());
    }

    [Fact]
    public void CORE010_open_requests_follow_the_allow_list_and_carry_play_files()
    {
        var ok = P("--open=Player", @"--play=C:\v\a.mp4", "--play=relative.mp4", "--play=\"D:\\b.mkv\"").OpenRequest!;
        Assert.True(ok.Ok);
        Assert.Equal("player", ok.Target);
        Assert.Equal(new[] { @"C:\v\a.mp4", @"D:\b.mkv" }, ok.Files);
        Assert.Equal("mantle: opened Mantle Player for a second start (--open=player, 2 file(s))", ok.Line);
        var bad = P("--open=calc").OpenRequest!;
        Assert.False(bad.Ok);
        Assert.Equal("mantle: a second start asked to open \"calc\", which is not something Mantle opens from outside (allowed: wallpapers, player)", bad.Line);
        Assert.False(P("--open").OpenRequest!.Ok);
        Assert.Equal("Mantle is not running, so its Wallpapers could not be opened", OpenRequest.NotRunningLine(P("--open=wallpapers").OpenRequest));
        Assert.Equal(64, OpenRequest.PlayFiles(Enumerable.Range(0, 70).Select(i => $@"--play=C:\f{i}.mp3")).Count);
        Assert.Equal((3, 4), (ExitCodes.OpenNotRunning, ExitCodes.OpenRefused));
    }

    [Fact]
    public void CORE014_CORE015_the_live_banner_comes_first_and_only_a_console_start_pauses()
    {
        var lines = StartLines.LiveLines(@"X:\m\tools\restore-windows.ps1", @"X:\p\shell-state.json");
        Assert.Equal("mantle: starting LIVE. Windows will be put back on quit, on crash, and on kill.", lines[0]);
        Assert.Contains("powershell -NoProfile -ExecutionPolicy Bypass -File \"X:\\m\\tools\\restore-windows.ps1\"", lines[1]);
        Assert.Equal((3000, "starting in 3 seconds. Ctrl+C now if that was not what you meant."), StartWake.Countdown(true, true));
        Assert.Equal(0, StartWake.Countdown(true, false).PauseMs);
        Assert.StartsWith("starting now: there is no console to press Ctrl+C in", StartWake.Countdown(true, false).Line);
        Assert.Null(StartWake.Countdown(false, true).Line);
    }

    [Fact]
    public void CORE018_the_first_ping_is_asked_again_at_2_5_15_s_and_an_old_exe_is_final()
    {
        Assert.Equal(StartWake.PingNext.Go, StartWake.Ping(true, null, null, 0).Next);
        Assert.Equal(StartWake.PingNext.Old, StartWake.Ping(false, true, null, 0).Next);
        Assert.Equal(StartWake.PingNext.Old, StartWake.Ping(false, null, "sidecar exe is older than the protocol: x", 0).Next);
        var r = StartWake.Ping(false, false, "timed out", 1);
        Assert.Equal((StartWake.PingNext.Retry, 5000), (r.Next, r.WaitMs));
        Assert.Contains("asking again in 5 s (ask 3 of 4): timed out", r.Reason);
        var g = StartWake.Ping(false, false, "timed out", 3);
        Assert.Equal(StartWake.PingNext.GiveUp, g.Next);
        Assert.Equal("timed out (asked 4 times over 22 s)", g.Reason);
    }

    [Fact]
    public void PKG002_live_test_quit_and_open_pass_straight_through_and_the_log_flag_is_kept()
    {
        foreach (var argv in new[] { new[] { "--live" }, new[] { "--test", "--profile-name=a" }, new[] { "--quit" }, new[] { "--open=player" } })
            Assert.True(P(argv).Ok, string.Join(" ", argv));
        Assert.Equal(new[] { "--live", "--log=X:\\a.log" }, P(@"X:\Mantle.exe", "--live", "--log=X:\\a.log").Raw.ToArray());
        Assert.Contains("--user-data only works without --live", P("--live", @"--user-data=X:\u").Errors[0]);
    }
}
