using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.GameMode;

namespace Mantle.Core.Features.TopBar;

// lib/features/topbar.js's KNOWN GAMES RUNNING (TOP-267..272): the full-screen verdict needs a game in front AND full
// screen, and a game can come up under the strip as a maximised WINDOW. So the process list is followed: from the
// moment a known game STARTS (before its window exists, in front or not) the bar hides, its menus close, its stats
// stop, the peek watch stops, the drag guard goes off and the app bar is RELEASED, so the work area is the whole
// screen. Nothing re-reserves it (display changes, re-checks) while the game runs, and an alt-tab or a second game
// changes nothing. When the last one exits the strip is reserved again, once, and the bar comes back. `overGames` does
// not apply to a known game.
//
// THE SHARED GAME WATCH (GameWatch.cs): game mode is the one process-list reader and hands every answer to this
// follower: at once after a foreground change (game mode follows the same `topbar` focus events), every RunMs while a
// known game runs, and its slow backstop at rest (12 s while the bar's focus watch feeds it, instead of game mode's own
// 3 s idle tick, so following it costs the machine less, not more). Only with no game mode reading at all does the
// follower read on its own, every RunMs, as Electron does. No timer of the bar's own runs at rest.
public sealed partial class TopBarFeature
{
    public const int RunMs = 1000;
    /// --test only: extra exe names counted as known games running (Electron's MANTLE_TOPBAR_TEST_GAME).
    public const string TestGameEnv = "MANTLE_TOPBAR_TEST_GAME";

    public sealed record RunInfo(int Pid, string? File, IReadOnlyList<GameProc> All, long Since, string? Released);
    RunInfo? _run;
    readonly Dictionary<int, string> _runSeen = new();
    (bool? Ok, string? Reason) _runWatch = (null, "starting");
    GameWatchFollower? _rw;
    readonly GameWatchGate _runGate = new();
    string[] _testGames = Array.Empty<string>();

    /// Read by the tests: the known game(s) running now, or null.
    public RunInfo? Run => _run;
    public (bool? Ok, string? Reason) RunWatch => _runWatch;

    /// "a known game" for one exe (any number of its processes), "N known games" for more: no game named in the log.
    public static string GameWord(IEnumerable<GameProc>? list)
    {
        int n = (list ?? Array.Empty<GameProc>()).Select(g => g.File).Distinct(StringComparer.Ordinal).Count();
        return n > 1 ? $"{n} known games" : "a known game";
    }
    /// The exe names, each once, for the strip's words (status, quick settings): "the game" when none.
    public static string RunNames(IEnumerable<GameProc>? list)
    {
        var s = string.Join(", ", (list ?? Array.Empty<GameProc>()).Select(g => g.File).Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.Ordinal));
        return s.Length > 0 ? s : "the game";
    }

    void GamesInstall()
    {
        if (_test) _testGames = (Environment.GetEnvironmentVariable(TestGameEnv) ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        if (_testGames.Length > 0) _ctx.Log($"test run: {TestGameEnv} counts {string.Join(", ", _testGames)} as known games running");
        var hub = (_ctx as IFeatureHostServices)?.GameWatch ?? new GameWatch();   // no host hub: a watch of its own that nothing drives
        _rw = new GameWatchFollower(hub, new GameWatchFollowerHooks
        {
            Active = () => !_stopped && !_ending && _ctx.Enabled,
            Running = () => _run != null,
            Fed = () => _watch.Ok == true,
            EveryMs = RunMs,
            Read = ReadGames,
            Seen = GameSeen,
            Clock = _clock!,
            Log = s => _ctx.Log(s),
            Gate = _runGate,
        });
    }

    /// The follower's own read (only while nothing drives the watch, and once at start): names and pids only.
    async Task<JsonObject?> ReadGames()
    {
        var r = await _ctx.Platform.CallAsync("gamemode.scan", new JsonObject { ["procs"] = true }, TimeSpan.FromSeconds(3));
        if (!r.Ok) return new JsonObject { ["ok"] = false, ["reason"] = r.Error };
        var o = r.Result.ValueKind == JsonValueKind.Object ? JsonNode.Parse(r.Result.GetRawText()) as JsonObject : null;
        if (o == null) return new JsonObject { ["ok"] = false, ["reason"] = "the sidecar's answer was not an object" };
        o["ok"] = true;
        return o;
    }

    Task GameCheck() => _rw?.CheckAsync() ?? Task.CompletedTask;
    void RunLoop() => _rw?.Start();
    void RunLoopStop() => _rw?.Stop();

    /// For the tests: one answer, as the hub would hand it over.
    internal Task SeenForTest(JsonObject r) => GameSeen(r);

    /// One answer to gamemode.scan {procs:true} ({ok:true, ...} or {ok:false, reason}), from game mode's watch or our own read.
    async Task GameSeen(JsonObject? r)
    {
        if (_stopped) return;
        string? why = r == null ? "the game check sent nothing"
            : !GameModeJson.True(r["ok"]) ? (GameModeJson.Str(r["reason"]) ?? "no reason given")
            : GameModeJson.Str(r["procsReason"]) is { } pr ? pr
            : r["procs"] is not JsonArray ? "the sidecar sent no process list (an old sidecar)"
            : null;
        if (why != null)
        {
            if (_runWatch.Ok != false || _runWatch.Reason != why)
                _ctx.Log($"cannot check for games starting: {why}" + (_run != null ? $"; stays stepped aside for {GameWord(_run.All)} until the check works again" : ""));
            _runWatch = (false, why);   // a failed check changes nothing: never takes the strip back mid-game (TOP-271)
            Health();
            return;
        }
        if (_runWatch.Ok != true)
        {
            if (_runWatch.Ok == false) _ctx.Log("checking for games starting again");
            _runWatch = (true, null);
            Health();
        }
        int? own = GameModeJson.Num(r!["ownSession"]) is double s ? (int)s : null;
        await RunChanged(GameModePlan.RunningGames(GameModeJson.Fg(r["fg"]), GameModeJson.Procs(r["procs"]), own, _runSeen, _testGames));
    }

    async Task RunChanged(List<GameProc> list)
    {
        var was = _run;
        if (list.Count > 0 && was != null) { _run = was with { All = list }; return; }   // still running: an alt-tab or a second game changes nothing
        if (list.Count == 0 && was == null) return;
        if (list.Count > 0)
        {
            var word = GameWord(list);
            _run = new RunInfo(list[0].Pid, list[0].File, list, Now, null);
            await Decide("game started");   // hides, closes a menu, stops the stats
            PeekSync("game started");       // a peek goes with the watch while a known game runs
            await _ctx.Platform.CallAsync("topbar.guard", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500));
            _guard = (false, $"off while {word} runs");
            var rel = await ReleaseStrip($"{word} is running; taken back when it exits");
            if (_run != null) _run = _run with { Released = rel.Ok ? (rel.DryRun ? "dry run" : "released") : $"failed: {rel.Reason}" };
            _ctx.Log($"{word} running; the top bar " +
                (rel.DryRun ? (_live ? "stepped aside until it exits (its window is not made yet, so it held no strip to let go)" : "stepped aside until it exits (a --test instance holds no strip, so there was none to let go)")
                : rel.Ok ? $"let go of the top {TopBarRules.BarDip} DIP and stepped aside until it exits"
                : $"stepped aside until it exits, but could NOT let go of the top {TopBarRules.BarDip} DIP ({rel.Reason}), so maximised windows still start below it"));
            Publish();
            Health();
        }
        else
        {
            _run = null;
            _ctx.Log($"{GameWord(was!.All)} exited after {TopBarModel.JsRound((Now - was.Since) / 1000.0)} s; the top bar takes back the top {TopBarRules.BarDip} DIP and comes back");
            if (!_hidden) Asked("game exited");
            await Decide("game exited");   // shows it and restarts the stats, unless something else still wants it aside
            PeekSync("game exited");       // hidden by hand: the edge is watched again
            await GuardOn();
            if (_live) await ReserveStrip("game exited");
            else _ctx.Log("app bar game exited: a --test instance never reserves space on the screen, so there was nothing to take back");
            Publish();
            Health();
        }
    }

    JsonObject? RunView() => _run is { } r
        ? new JsonObject { ["file"] = r.File, ["all"] = new JsonArray(r.All.Select(g => (JsonNode?)JsonValue.Create(g.File)).ToArray()), ["since"] = r.Since, ["released"] = r.Released }
        : null;
}
