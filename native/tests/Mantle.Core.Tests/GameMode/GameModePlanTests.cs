using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features.GameMode;
using Xunit;

namespace Mantle.Core.Tests.GameMode;

/// <summary>lib/features/gamemode.test.js, the pure rules (_gamemode-plan.js): the same cases.</summary>
public class GameModePlanTests
{
    const long MB = 1048576, GB = 1073741824;
    static GameFg Fg(string exe, string title = "", bool full = false, bool store = false, int? quns = null, int pid = 900) =>
        new(pid, $@"C:\x\{exe}", title, false, full, store, quns);
    static readonly GameFg V = Fg("VALORANT-Win64-Shipping.exe");
    static readonly GameProc[] WithGame = { new(900, "VALORANT-Win64-Shipping.exe"), new(5, "Discord.exe") };
    static readonly GameModeState OnAuto = new(true, "auto", new GameRun(900, "VALORANT-Win64-Shipping.exe", 1000));

    [Fact]
    public void Known_games_count_in_front_even_windowed_and_launchers_never_do()
    {
        Assert.True(GameModePlan.DetectGame(V, null).Game);
        Assert.Equal("known", GameModePlan.DetectGame(Fg("RobloxPlayerBeta.exe", "Roblox"), null).How);
        foreach (var g in new[] { "Minecraft.Windows.exe", "FortniteClient-Win64-Shipping.exe", "cs2.exe", "r5apex.exe", "RocketLeague.exe", "GTA5.exe", "Overwatch.exe", "League of Legends.exe", "EscapeFromTarkov.exe", "Cyberpunk2077.exe" })
            Assert.True(GameModePlan.DetectGame(Fg(g), null).Game, g);
        var riot = GameModePlan.DetectGame(Fg("RiotClientServices.exe", "Riot Client", full: true, store: true), null);
        Assert.False(riot.Game);
        Assert.Equal("RiotClientServices.exe is a launcher, not the game", riot.Why);
        foreach (var l in new[] { "LeagueClient.exe", "MinecraftLauncher.exe", "Minecraft Launcher.exe", "RobloxPlayerLauncher.exe", "RobloxStudio.exe", "EpicGamesLauncher.exe", "steam.exe", "Battle.net.exe" })
            Assert.False(GameModePlan.DetectGame(Fg(l, full: true, quns: 3), null).Game, l);
    }

    [Fact]
    public void Java_counts_only_with_a_Minecraft_window()
    {
        Assert.True(GameModePlan.DetectGame(Fg("javaw.exe", "Minecraft 1.21.1 - Singleplayer"), null).Game);
        Assert.True(GameModePlan.DetectGame(Fg("java.exe", "minecraft"), null).Game);
        Assert.False(GameModePlan.DetectGame(Fg("javaw.exe", "IntelliJ IDEA"), null).Game);
    }

    [Fact]
    public void Game_Bar_list_needs_a_full_monitor_and_exclusive_D3D_always_counts()
    {
        var store = GameModePlan.DetectGame(Fg("Hades.exe", "Hades", full: true, store: true), null);
        Assert.Equal("store", store.How);
        Assert.Equal("Hades.exe is on Windows' own game list (Game Bar) and fills its monitor", store.Why);
        Assert.False(GameModePlan.DetectGame(Fg("Hades.exe", "Hades", store: true), null).Game);
        Assert.Equal("exclusive", GameModePlan.DetectGame(Fg("old.exe", "x", quns: 3), null).How);
        Assert.False(GameModePlan.DetectGame(Fg("msedge.exe", "YouTube", full: true, quns: 2), null).Game);
    }

    [Fact]
    public void Not_a_game_reasons_are_worded()
    {
        Assert.Equal("Mantle is in front", GameModePlan.DetectGame(V with { Self = true }, null).Why);
        Assert.Equal("no app is in front", GameModePlan.DetectGame(null, null).Why);
        Assert.Equal("no app is in front", GameModePlan.DetectGame(new GameFg(0), null).Why);
        Assert.Equal("the app in front could not be named", GameModePlan.DetectGame(new GameFg(900, null, "VALORANT"), null).Why);
        Assert.Equal("msedge.exe fills the screen but is not a game", GameModePlan.DetectGame(Fg("msedge.exe", full: true), null).Why);
        Assert.Equal("notepad.exe is not a game", GameModePlan.DetectGame(Fg("notepad.exe"), null).Why);
    }

    [Fact]
    public void An_unreadable_path_is_named_from_the_process_list_by_pid()
    {
        var noPath = new GameFg(900, "", "VALORANT");
        var d = GameModePlan.DetectGame(noPath, WithGame);
        Assert.True(d.Game);
        Assert.Equal("VALORANT-Win64-Shipping.exe", d.File);
        Assert.False(GameModePlan.DetectGame(noPath, null).Game);
        Assert.Equal("on", GameModePlan.Step(new GameModeState(false), noPath, WithGame, 0).Action);
    }

    [Fact]
    public void Step_turns_on_for_a_game_and_off_when_it_exits_or_stays_away()
    {
        var d = GameModePlan.Step(new GameModeState(false), V, null, 0);
        Assert.Equal("on", d.Action);
        Assert.Equal(900, d.Game!.Pid);
        Assert.Null(GameModePlan.Step(new GameModeState(false), V, null, 0, auto: false).Action);
        Assert.Equal("automatic game mode is off", GameModePlan.Step(new GameModeState(false), V, null, 0, auto: false).Why);
        Assert.Equal("front", GameModePlan.Step(OnAuto, V, WithGame, 2000).Action);
        Assert.Null(GameModePlan.Step(OnAuto, Fg("Discord.exe", pid: 5), WithGame, 61000).Action);
        var away = GameModePlan.Step(OnAuto, Fg("Discord.exe", pid: 5), WithGame, 91000);
        Assert.Equal("off", away.Action);
        Assert.Equal("VALORANT-Win64-Shipping.exe has not been in front for 90 s", away.Why);
        var gone = GameModePlan.Step(OnAuto, Fg("explorer.exe", pid: 6), new[] { new GameProc(5, "x") }, 2000);
        Assert.Equal("VALORANT-Win64-Shipping.exe exited", gone.Why);
        Assert.Equal("off", GameModePlan.Step(OnAuto, Fg("explorer.exe", pid: 6), new[] { new GameProc(900, "notepad.exe") }, 2000).Action);
    }

    [Fact]
    public void Manual_stays_on_and_off_by_hand_holds_for_that_run()
    {
        Assert.Null(GameModePlan.Step(new GameModeState(true, "manual", new GameRun(0, "", 0)), Fg("explorer.exe"), new GameProc[0], 999999).Action);
        var held = GameModePlan.Step(new GameModeState(false, SuppressPid: 900), V, WithGame, 0);
        Assert.Null(held.Action);
        Assert.Equal("turned off by hand for this run of VALORANT-Win64-Shipping.exe", held.Why);
        var again = GameModePlan.Step(new GameModeState(false, SuppressPid: 900), Fg("explorer.exe", pid: 6), new GameProc[0], 0);
        Assert.Equal("unsuppress", again.Action);
        Assert.Equal("the game you turned game mode off for has exited", again.Why);
    }

    [Fact]
    public void Running_games_count_behind_other_windows_in_the_users_own_session()
    {
        var other = Fg("Discord.exe", pid: 5);
        Assert.Single(GameModePlan.RunningGames(other, new[] { new GameProc(900, "VALORANT-Win64-Shipping.exe", Session: 1) }, 1));
        Assert.Single(GameModePlan.RunningGames(null, new[] { new GameProc(41, "valorant.exe", Session: 1) }, 1));
        Assert.Empty(GameModePlan.RunningGames(null, new[] { new GameProc(7, "RiotClientServices.exe", Session: 1), new GameProc(8, "Riot Client.exe", Session: 1) }, 1));
        Assert.Equal(2, GameModePlan.RunningGames(null, new[] { new GameProc(10, "RobloxPlayerBeta.exe", Session: 1), new GameProc(11, "Minecraft.Windows.exe", Session: 1) }, 1).Count);
        Assert.Empty(GameModePlan.RunningGames(null, new[] { new GameProc(900, "VALORANT-Win64-Shipping.exe", Session: 2) }, 1));
        var standin = new[] { new GameProc(60, "mantle-standin-game.exe", Session: 1) };
        Assert.Empty(GameModePlan.RunningGames(null, standin, 1));
        Assert.Single(GameModePlan.RunningGames(null, standin, 1, extra: new[] { "mantle-standin-game.exe" }));
        Assert.Empty(GameModePlan.RunningGames(null, null, null));
    }

    [Fact]
    public void Javaw_is_remembered_once_seen_in_front_with_Minecraft_and_forgotten_when_gone()
    {
        var seen = new Dictionary<int, string>();
        var java = new[] { new GameProc(70, "javaw.exe", Session: 1) };
        Assert.Empty(GameModePlan.RunningGames(null, java, 1, seen));
        var run = GameModePlan.RunningGames(Fg("javaw.exe", "Minecraft 1.21", pid: 70), java, 1, seen);
        Assert.Equal(70, Assert.Single(run).Pid);
        Assert.Equal("javaw.exe", seen[70]);
        Assert.Single(GameModePlan.RunningGames(Fg("Discord.exe", pid: 5), java, 1, seen));
        GameModePlan.RunningGames(null, new GameProc[0], 1, seen);
        Assert.True(seen.ContainsKey(70));   // an empty list (a failed snapshot) forgets nothing
        GameModePlan.RunningGames(null, new[] { new GameProc(5, "Discord.exe", Session: 1) }, 1, seen);
        Assert.False(seen.ContainsKey(70));
    }

    [Fact]
    public void Game_label_joins_each_name_once()
    {
        Assert.Equal("VALORANT", GameModePlan.GameLabel(new[] { "VALORANT.exe", @"C:\x\VALORANT-Win64-Shipping.exe" }));
        Assert.Equal("VALORANT, RobloxPlayerBeta", GameModePlan.GameLabel(new[] { "VALORANT.exe", "RobloxPlayerBeta.exe" }));
        Assert.Equal("", GameModePlan.GameLabel(null));
    }

    [Fact]
    public void Rest_step_rests_while_a_game_runs_and_wakes_in_plain_words()
    {
        var run = new[] { new GameProc(800, "VALORANT.exe"), new GameProc(900, "VALORANT-Win64-Shipping.exe") };
        var resting = new GameRestState(true, "VALORANT runs");
        var d = GameModePlan.RestStep(new GameRestState(false), new GameRestInput(run));
        Assert.Equal(("rest", "VALORANT runs"), (d.Action, d.Why));
        Assert.Null(GameModePlan.RestStep(resting, new GameRestInput(run)).Action);
        d = GameModePlan.RestStep(resting, new GameRestInput(new GameProc[0]));
        Assert.Equal(("wake", "VALORANT exited"), (d.Action, d.Why));
        Assert.Null(GameModePlan.RestStep(resting, new GameRestInput(new GameProc[0], true, "auto", "VALORANT-Win64-Shipping.exe")).Action);
        d = GameModePlan.RestStep(new GameRestState(false), new GameRestInput(new GameProc[0], true, "manual"));
        Assert.Equal(("rest", "game mode is on (by hand)"), (d.Action, d.Why));
        d = GameModePlan.RestStep(resting with { Hold = new[] { 900 } }, new GameRestInput(run));
        Assert.Equal("wake", d.Action);
        Assert.Equal("game mode was turned off by hand for this game", d.Why);
        Assert.Equal(900, d.Hold![0]);
        d = GameModePlan.RestStep(new GameRestState(false, null, new[] { 900 }), new GameRestInput(run));
        Assert.Null(d.Action);
        Assert.NotNull(d.Hold);
        d = GameModePlan.RestStep(new GameRestState(false, null, new[] { 900 }), new GameRestInput(new[] { new GameProc(901, "VALORANT-Win64-Shipping.exe") }));
        Assert.Equal("rest", d.Action);
        Assert.Null(d.Hold);
        d = GameModePlan.RestStep(resting, new GameRestInput(run, Enabled: false));
        Assert.Equal(("wake", "game mode was switched off in Mantle's tray menu"), (d.Action, d.Why));
    }

    [Fact]
    public void Rest_why_tells_a_game_from_game_mode_on_by_hand()
    {
        var run = new[] { new GameProc(900, "VALORANT-Win64-Shipping.exe") };
        string? W(GameRestInput x) => GameModePlan.RestStep(new GameRestState(false), x).Why;
        var games = new[] { W(new(run)), W(new(run.Append(new GameProc(950, "RobloxPlayerBeta.exe")).ToArray())),
            W(new(new GameProc[0], true, "auto", "VALORANT-Win64-Shipping.exe")), W(new(new GameProc[0], true, "manual", "cs2.exe")),
            W(new(new GameProc[0], true, "auto", "SomeStoreGame.exe")) };
        Assert.All(games, w => Assert.True(GameModePlan.RestIsGame(w), w));
        var none = new[] { W(new(new GameProc[0], true, "manual")), W(new(new GameProc[0], true, "auto")), null, "", "VALORANT exited" };
        Assert.All(none, w => Assert.False(GameModePlan.RestIsGame(w), w));
    }

    static GameProc P(int pid, string file, string? path = null, int ppid = 0, bool elevated = false, string prio = "normal") =>
        new(pid, file, ppid, 1, path ?? $@"C:\Apps\{file}", pid.ToString(), prio, Elevated: elevated);

    [Fact]
    public void Plan_eases_background_apps_and_leaves_the_never_touch_list_alone()
    {
        var procs = new GameProc[]
        {
            P(900, "VALORANT-Win64-Shipping.exe"), P(901, "CrashHandler.exe", ppid: 900), P(910, "vgc.exe"), P(911, "EasyAntiCheat.exe"),
            P(920, "Discord.exe"), P(930, "Spotify.exe"), P(940, "claude.exe"), P(941, "msedgewebview2.exe", prio: "belownormal"),
            P(950, "Lively.exe"), P(960, "Quartz.exe", @"C:\Q\Quartz.exe"), P(961, "Quartz.exe", @"C:\Q\Quartz.exe"),
            P(970, "electron.exe"), P(971, "Mantle.Sidecar.exe"), P(972, "electron.exe", @"C:\M\electron.exe"),
            P(980, "explorer.exe", @"C:\Windows\explorer.exe"), P(981, "svchost.exe", @"C:\Windows\System32\svchost.exe"),
            P(991, "admintool.exe", elevated: true), P(992, "lghub.exe"), P(993, "notes.exe"), P(994, "fg.exe"),
            new(995, "pending.exe", 0, 1, Pending: true), new(996, "hidden.exe", 0, 1), new(997, "other.exe", 0, 2, @"C:\Apps\other.exe"),
        };
        var pl = GameModePlan.Plan(procs, new GamePlanContext
        {
            GamePid = 900, GameFile = "VALORANT-Win64-Shipping.exe", FgPid = 994, SidecarPid = 971, MainPid = 972, OwnSession = 1,
            MantlePids = new HashSet<int> { 970, 972 }, AudioPids = new HashSet<int> { 961 },
        });
        var tp = pl.Targets.Select(t => t.Pid).ToHashSet();
        string? Why(int pid) => pl.Left.FirstOrDefault(l => l.File == procs.First(p => p.Pid == pid).File)?.Why;
        Assert.True(tp.SetEquals(new[] { 940, 941, 950, 970, 993 }), string.Join(",", tp.OrderBy(x => x)));
        Assert.Equal("the game", Why(901));
        Assert.Equal("a game or its anti-cheat", Why(910));
        Assert.Equal("voice and calls", Why(920));
        Assert.Equal("music and video", Why(930));
        Assert.Equal("playing sound", Why(960));
        Assert.Equal("Mantle's keyboard helper: slowing it would slow every key", Why(971));
        Assert.Equal("Windows' own shell", Why(980));
        Assert.Equal("runs as administrator; Mantle does not", Why(991));
        Assert.Equal("mouse, keyboard and GPU software: slowing it adds input lag", Why(992));
        Assert.Equal("in front", Why(994));
        Assert.Equal("could not be read", Why(996));
        Assert.StartsWith("Mantle's main process", pl.Left.Single(l => l.Why.StartsWith("Mantle's main")).Why);
        Assert.Equal("mantle", pl.Targets.Single(t => t.Pid == 970).Group);
        Assert.Equal("background", pl.Targets.Single(t => t.Pid == 940).Group);
        Assert.Equal(1, pl.Pending);
        Assert.Equal(2, pl.Quiet);   // svchost under Windows, and another session
    }

    [Fact]
    public void Names_and_left_by_why_count_by_name_and_say_the_helper_plainly()
    {
        Assert.Equal("a ×2, b", GameModePlan.Names(new[] { "a.exe", "a.exe", "b.exe" }));
        Assert.Equal("Mantle helper", GameModePlan.Names(new[] { "Mantle.Sidecar.exe" }));
        Assert.Equal("a, b and 1 more", GameModePlan.Names(new[] { "a", "b", "c" }, 2));
        var g = GameModePlan.LeftByWhy(new[] { new GameLeft("Discord.exe", "voice and calls"), new GameLeft("Discord.exe", "voice and calls"), new GameLeft("x.exe", "security") });
        Assert.Equal("Discord ×2 (voice and calls)", g[0].Text);
    }

    [Fact]
    public void Trim_plan_takes_only_idle_pages_and_says_why_for_the_rest()
    {
        var procs = new[]
        {
            new GameProc(1, "a.exe", 0, 1, @"C:\a.exe", "1", CpuMs: 100, Ws: 64 * MB), new GameProc(2, "a.exe", 1, 1, @"C:\a.exe", "2", CpuMs: 100, Ws: 64 * MB),
            new GameProc(3, "Helm.exe", 0, 1, @"C:\h.exe", "3", CpuMs: 0, Ws: 900 * MB), new GameProc(4, "b.exe", 0, 1, @"C:\b.exe", "4", CpuMs: 5000, Ws: 64 * MB),
            new GameProc(5, "c.exe", 0, 1, @"C:\c.exe", "5", CpuMs: 0, Ws: 4 * MB), new GameProc(6, "d.exe", 0, 1, @"C:\d.exe", "6", CpuMs: 0, Ws: 64 * MB),
            new GameProc(7, "s.exe", 0, 1, @"C:\s.exe", "7", CpuMs: 0, Ws: 64 * MB),
        };
        var eased = new[] { 1, 2, 3, 4, 5, 6, 7 }.Select(p => new GameEased(p, p.ToString(), "x", "background")).Append(new GameEased(9, "9", "m", "mantle"));
        var bases = new Dictionary<int, GameCpuBase> { [1] = new("1", 100), [2] = new("2", 100), [3] = new("3", 0), [4] = new("4", 100), [5] = new("5", 0), [7] = new("7", 0) };
        var t = GameModePlan.TrimPlan(procs, eased, bases, audioPids: new HashSet<int> { 7 });
        Assert.Equal(new[] { 2 }, t.Trim.Select(x => x.Pid));
        var skip = string.Join("|", t.Skip.Select(s => s.Why));
        Assert.Contains("its app's main process", skip);
        Assert.Contains("Helm and Claude are only eased: an agent may be working", skip);
        Assert.Contains("busy in the last minute", skip);
        Assert.Contains("started after the game", skip);
        Assert.Contains("playing sound", skip);
        Assert.Equal(1, t.Small);
        Assert.Empty(GameModePlan.TrimPlan(procs, eased, bases, done: new HashSet<int> { 2 }).Trim);
    }

    [Fact]
    public void Trim_calls_hold_at_most_four_processes_and_one_GB()
    {
        string Sizes(IEnumerable<long> mbs) => string.Join(" | ", GameModePlan.TrimCalls(mbs.Select((m, i) => new GameTrim(i, null, "x", m * MB)))
            .Select(c => string.Join("+", c.Select(x => x.Ws / MB))));
        Assert.Equal("600 | 600+10+10+10 | 10 | 2048", Sizes(new long[] { 600, 600, 10, 10, 10, 10, 2048 }));
        Assert.Empty(GameModePlan.TrimCalls(null));
        Assert.Equal(GB, GameModePlan.TrimCallBytes);
    }

    [Fact]
    public void Boost_words_and_the_biggest_app_to_name()
    {
        Assert.Equal("Freed 1.2 GB and eased 4 background apps", GameModePlan.BoostLine(new[] { "a.exe", "b.exe", "c.exe", "d.exe", "d.exe" }, (long)(1.2 * GB), 3));
        Assert.Equal("Eased 1 background app", GameModePlan.BoostLine(new[] { "a.exe" }, 0, 0));
        Assert.Equal("No background app to ease", GameModePlan.BoostLine(new string[0], 0, 0));
        Assert.Equal("300 MB", GameModePlan.Bytes(300 * MB));
        var procs = new[] { new GameProc(10, "chrome.exe", 0, 1, @"C:\c\chrome.exe", Ws: 200 * MB), new GameProc(11, "chrome.exe", 0, 1, @"C:\c\chrome.exe", Ws: 200 * MB),
            new GameProc(12, "Discord.exe", 0, 1, @"C:\d.exe", Ws: 900 * MB), new GameProc(900, "VALORANT-Win64-Shipping.exe", 0, 1, @"C:\v.exe", Ws: 3000 * MB),
            new GameProc(13, "small.exe", 0, 1, @"C:\s.exe", Ws: 100 * MB) };
        var b = GameModePlan.BiggestApp(procs, new GamePlanContext { GamePid = 900, OwnSession = 1 });
        Assert.Equal("chrome", b!.Value.Name);
        Assert.Equal(400 * MB, b.Value.Ws);
        Assert.Null(GameModePlan.BiggestApp(procs.Skip(2), new GamePlanContext { GamePid = 900, OwnSession = 1 }));
    }
}
