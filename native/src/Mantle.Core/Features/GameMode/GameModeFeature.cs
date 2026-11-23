using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using static Mantle.Core.Features.GameMode.GameModeJson;

namespace Mantle.Core.Features.GameMode;

/// <summary>
/// Game mode (lib/features/gamemode.js), the service around its pure half <see cref="GameModePlan"/>: the scan tick,
/// the power and Energy saver steps, easing the background through the helper (gamemode.apply), the boost's one trim
/// a minute in (gamemode.trimbg), the restore record (&lt;data root&gt;\gamemode-restore.json) and its put-back after a
/// crash, what could not be put back tried again every 30 s, the shared game watch it drives, and the rest it asks of
/// the host (<see cref="IFeatureHostServices.RestAsync"/>, RestCoordinator) while a known game runs.
///
/// A --test run is a DRY RUN (Electron's ctx.flags.test): every write is planned and logged as
/// <c>gamemode: dry run (--test): would ask the sidecar &lt;verb&gt; {...}; nothing was sent</c> and never sent. Reads
/// (gamemode.scan, powermode.get, audio sessions) are reads. MANTLE_GAMEMODE_TEST_GAME (test runs only) names stand-in
/// exes that count as known games RUNNING, so the rest is proved end to end. Deviation: Electron skipped the crash
/// put-back in a test run; native runs it as a dry run against the test profile's record (never %APPDATA%\mantle), so
/// a test can prove it, and drops that record.
/// </summary>
[Feature("gamemode", "Game mode (quick settings)", true)]
public sealed class GameModeFeature : IFeature
{
    public const string StateEvent = "gamemode.state", TestGameEnv = "MANTLE_GAMEMODE_TEST_GAME", RecordName = "gamemode-restore.json";
    const int IdleMs = 3000, OnMs = 5000, RestMs = 5000, BackstopMs = 12000, RetryMs = 30000, RetryMax = 20, Chunk = 40;
    const string WatchKey = "gamemode-watch";
    const string NotReady = "the sidecar is restarting (Mantle's background helper)";

    public static readonly IReadOnlyList<FeatureOption> Options = new[]
    {
        new FeatureOption("auto", "Turn on by itself for games", FeatureOptionKind.Bool, true) { Help = "Game mode starts when a known game starts and ends when it exits" },
        new FeatureOption("liftSaver", "Lift Energy saver while gaming on battery", FeatureOptionKind.Bool, false) { Help = "Energy saver forces Best power efficiency; this lifts it for the game and puts it back after" },
        new FeatureOption("boost", "Boost: free memory and power for the game", FeatureOptionKind.Bool, true) { Help = "Background apps get Efficiency mode, and a minute in, idle ones hand their unused memory back to Windows once. Nothing is ever closed; everything goes back when the game ends" },
    };

    /// <summary>What the host gives game mode (App.xaml.cs, block "game mode wiring"); tests make their own.</summary>
    /// <param name="DataRoot">Where gamemode-restore.json lives (the test profile in --test).</param>
    /// <param name="OwnPids">Mantle's own process ids (never the sidecar's): eased with the background as "Mantle itself".</param>
    /// <param name="WallpaperHold">Hold Mantle's wallpaper paused (why) or let it go (null); true when one was playing. Null: this build cannot.</param>
    public sealed record Host(string DataRoot, Func<IReadOnlyList<int>>? OwnPids = null, Func<string?, bool>? WallpaperHold = null,
        HostClock? Clock = null, Func<long>? FreeMem = null, Action<string, string>? Spawn = null, long TrimAfterMs = GameModePlan.TrimAfterMs,
        double IdleCpuMs = GameModePlan.IdleCpuMs, IReadOnlyList<string>? TestGames = null, int? MainPid = null, Mantle.Core.Data.SafeFile? Files = null,
        long ReplanMs = ReplanEveryMs);
    /// <summary>gamemode.js REPLAN_MS: while on, the plan is redone this often so apps started since are eased too (BAR-781).</summary>
    public const long ReplanEveryMs = 30000;
    /// <summary>A --test run only: the replan's interval in ms (at least 500), so a UI test need not wait 30 s.</summary>
    public const string TestReplanEnv = "MANTLE_TEST_GAMEMODE_REPLAN_MS";
    static Host? _configured;
    public static void Configure(Host h) => _configured = h;

    readonly Host? _given;
    public GameModeFeature() { }
    public GameModeFeature(Host h) { _given = h; }

    sealed class Boost
    {
        public string State = "off"; public string? Text, Why, Suggest; public bool Warn;
        public long Freed, WouldFree; public int Trimmed, WouldTrim; public long? FreeBefore, FreeAfter, At;
        public List<GameLeft> Skip = new(), Refused = new();
    }
    sealed class Rest { public bool Resting, Busy; public string? Why, Text; public long? Since; public IReadOnlyList<int>? Hold; public JsonObject? Last; }
    sealed class Leftover { public JsonObject Rec = null!; public long At; public int Tries; public bool GaveUp; public string Text = ""; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    GameWatch? _w;
    IDisposable? _drive, _timer, _wTimer;
    int? _timerMs;
    bool _dry, _stopped, _ticking, _again, _watching, _crashChecked, _replanSoon, _boostWas;
    string _reason = "starting";
    string? _busy, _how, _scErr;
    bool _on;
    long? _since;
    JsonObject? _game;   // {file, pid, title, why, lastFront}
    GameFg? _lastFg;
    List<GameProc>? _lastProcs;
    List<GameProc> _lastRun = new();
    readonly Dictionary<int, string> _runSeen = new();
    int? _suppressPid;
    readonly List<JsonObject> _actions = new();
    JsonObject? _record, _last, _fromCrash;
    Leftover? _leftover;
    readonly Rest _rest = new();
    Boost? _boost;
    Dictionary<int, GameCpuBase>? _cpuBase;
    long _cpuBaseAt, _lastReplan, _replanMs = ReplanEveryMs;
    int _replans;
    HashSet<int> _trimDone = new();
    (int GamePid, string? GameFile, List<int> Pids)? _crashTrim;
    List<GameTarget> _dryEased = new();
    readonly GameWatchMemory _procMem = new();
    TaskCompletionSource? _onDone;
    IReadOnlyList<string> _testGames = Array.Empty<string>();
    string _recordPath = "";

    // ------------------------------------------------------------------ install

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host(Path.Combine(Path.GetTempPath(), "mantle-gamemode-unconfigured"));
        _clock = _h.Clock ?? new HostClock();
        _dry = ctx.Flags.Test || Environment.GetEnvironmentVariable("MANTLE_GAMEMODE_DRY") == "1";
        _replanMs = _h.ReplanMs;
        if (ctx.Flags.Test && long.TryParse(Environment.GetEnvironmentVariable(TestReplanEnv), out var rp) && rp >= 500)
        {
            _replanMs = rp;
            ctx.Log($"test run: {TestReplanEnv}={rp}: while on, the plan is redone every {rp} ms (live: {ReplanEveryMs / 1000} s)");
        }
        _recordPath = Path.Combine(_h.DataRoot, RecordName);
        _testGames = ctx.Flags.Test
            ? (_h.TestGames ?? (Environment.GetEnvironmentVariable(TestGameEnv) ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray())
            : Array.Empty<string>();
        if (_testGames.Count > 0) ctx.Log($"test run: {TestGameEnv} counts {string.Join(", ", _testGames)} as known games running");
        _boostWas = Set().Boost;
        _crashChecked = false;
        _w = (ctx as IFeatureHostServices)?.GameWatch;

        ctx.Replay(StateEvent, () => El(View()));
        ctx.Command("state", _ => Ok(View()));
        ctx.Command("set", c => Wrap(SetAsync(c.Args)));
        ctx.Command("auto", c => Wrap(SwitchAsync("auto", c.Args, on => $"automatic: {(on ? "true" : "false")}")));
        ctx.Command("liftSaver", c => Wrap(SwitchAsync("liftSaver", c.Args, on => $"lift Energy saver while gaming: {(on ? "true" : "false")}")));
        ctx.Command("boost", c => Wrap(BoostSwitchAsync(c.Args)));
        ctx.OnOption("*", (_, _) => { _ = SyncBoost("Settings"); Push(); });
        ctx.Platform.On("topbar", e => { if (e.Body.ValueKind == JsonValueKind.Object && e.Body.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "focus") { if (ctx.Enabled) Schedule(150); else WatchSchedule(150); } });
        ctx.Platform.On("powermode", ev => { if (_on && _busy == null && _record != null) _ = ApplyPowerThenPush(); });
        ctx.Platform.OnReady(() => _ = OnReadyAsync());
        ctx.OnEnabled(on => {
            if (on) Schedule(0);
            else { _timer?.Dispose(); _timer = null; _ = OffThenRest(); }
            WatchSchedule(); Push();
        });
        if (_w != null) _drive = _w.Drive(new Driver(this));
        return ValueTask.CompletedTask;
    }

    sealed class Driver : IGameWatchDriver
    {
        readonly GameModeFeature _f;
        public Driver(GameModeFeature f) { _f = f; }
        public void Changed()
        {
            if (_f._stopped) return;
            _f.WatchSchedule();
            if (_f._ctx.Enabled && !_f._ticking && _f._timer != null && _f._timerMs > _f.OwnMs()) _f.Schedule();
        }
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        _timer?.Dispose(); _timer = null;
        _wTimer?.Dispose(); _wTimer = null;
        _drive?.Dispose(); _drive = null;
        string why = stop.Reason is { Length: > 0 } r ? $" ({r})" : "";
        if (_rest.Resting && _ctx is IFeatureHostServices hs) _ = hs.RestAsync(false, $"game mode stopped{why}");
        if (_on) await TurnOff($"Mantle is closing{why}").ConfigureAwait(true);
    }

    async Task OffThenRest() { await TurnOff("switched off in the tray").ConfigureAwait(true); await SyncRest("the tray switch").ConfigureAwait(true); }
    async Task ApplyPowerThenPush() { try { await ApplyPower().ConfigureAwait(true); } catch { /* said by its row */ } Push(); }

    // ------------------------------------------------------------------ settings, view

    (bool Auto, bool LiftSaver, bool Boost) Set()
    {
        bool Get(string k, bool d) => _ctx.Settings.TryGet(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
        return (Get("auto", true), Get("liftSaver", false), Get("boost", true));
    }

    long Now() => _clock.Time.GetUtcNow().ToUnixTimeMilliseconds();
    long FreeMem() => _h.FreeMem?.Invoke() ?? Native.FreeBytes();
    int MainPid => _h.MainPid ?? Environment.ProcessId;
    HashSet<int> MantlePids() { try { return new HashSet<int>(_h.OwnPids?.Invoke() ?? new[] { Environment.ProcessId }); } catch { return new HashSet<int>(); } }

    JsonObject View()
    {
        var st = Set();
        JsonNode? boostNow = null;
        if (_on && _boost != null)
        {
            var eased = Eased().Select(e => GameModePlan.BaseName(e.File).ToLowerInvariant()).Distinct().Count();
            boostNow = new JsonObject { ["state"] = _boost.State, ["text"] = _boost.Text, ["freed"] = _boost.Freed, ["trimmed"] = _boost.Trimmed, ["apps"] = eased, ["warn"] = _boost.Warn };
        }
        return new JsonObject
        {
            ["on"] = _on, ["how"] = _how,
            ["game"] = _game == null ? null : new JsonObject { ["file"] = _game["file"]?.DeepClone(), ["title"] = _game["title"]?.DeepClone(), ["why"] = _game["why"]?.DeepClone() },
            ["since"] = _since, ["busy"] = _busy, ["auto"] = st.Auto, ["liftSaver"] = st.LiftSaver, ["boost"] = st.Boost, ["dry"] = _dry,
            ["enabled"] = _ctx.Enabled, ["watching"] = _watching, ["boostNow"] = boostNow, ["reason"] = _reason,
            ["actions"] = new JsonArray(_actions.Select(a => (JsonNode)a.DeepClone()).ToArray()),
            ["last"] = _last?.DeepClone(), ["fromCrash"] = _fromCrash?.DeepClone(), ["record"] = _dry ? null : _recordPath,
            ["rest"] = new JsonObject { ["on"] = _rest.Resting, ["why"] = _rest.Why, ["since"] = _rest.Since, ["text"] = _rest.Text, ["busy"] = _rest.Busy, ["last"] = _rest.Last?.DeepClone() },
            ["leftover"] = _leftover == null ? null : new JsonObject { ["text"] = _leftover.Text, ["tries"] = _leftover.Tries, ["gaveUp"] = _leftover.GaveUp },
        };
    }

    void Push() { if (!_stopped) _ctx.Broadcast(StateEvent, El(View())); }
    void Act(string key, string label, string status, string text)
    {
        _actions.RemoveAll(a => Str(a["key"]) == key);
        _actions.Add(GameModeJson.Act(key, label, status, text));
    }

    static ValueTask<CommandResult> Ok(JsonObject o) => ValueTask.FromResult(CommandResult.Success(El(o)));
    static async ValueTask<CommandResult> Wrap(Task<JsonObject> t) => CommandResult.Success(El(await t.ConfigureAwait(true)));
    static bool? OnArg(JsonElement a) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("on", out var o) ? o.ValueKind == JsonValueKind.True : null;

    async Task<JsonObject> SwitchAsync(string key, JsonElement a, Func<bool, string> line)
    {
        bool on = OnArg(a) == true;
        await _ctx.Settings.SetAsync(key, JsonValue.Create(on)).ConfigureAwait(true);
        _ctx.Log(line(on)); Push();
        return View();
    }

    async Task<JsonObject> BoostSwitchAsync(JsonElement a)
    {
        bool on = OnArg(a) == true;
        await _ctx.Settings.SetAsync("boost", JsonValue.Create(on)).ConfigureAwait(true);
        await SyncBoost("the game mode tile").ConfigureAwait(true);
        Push();
        return View();
    }

    async Task<JsonObject> SetAsync(JsonElement a)
    {
        if (!_ctx.Enabled) { var v = View(); v["ok"] = false; v["reason"] = "game mode is switched off in Mantle's tray menu"; return v; }
        if (OnArg(a) == true)
        {
            if (_on) { var v = View(); v["ok"] = true; return v; }
            var d = GameModePlan.DetectGame(_lastFg, _lastProcs);
            var g = new JsonObject
            {
                ["file"] = d.Game ? d.File : (d.File is { Length: > 0 } f ? f : "the app in front"), ["pid"] = d.Pid, ["title"] = d.Title,
                ["why"] = d.Game ? d.Why : "turned on by hand",
            };
            await TurnOn(g, "manual").ConfigureAwait(true);
            _rest.Hold = null;
        }
        else
        {
            if (_how == "auto" && _game != null) _suppressPid = Int(_game["pid"]);
            // Off by hand while a known game runs = the user wants Mantle back for this run of it.
            _rest.Hold = _lastRun.Count > 0 ? _lastRun.Select(p => p.Pid).ToList() : null;
            await TurnOff("turned off by hand").ConfigureAwait(true);
        }
        await SyncRest("by hand").ConfigureAwait(true);
        var r = View(); r["ok"] = true; return r;
    }

    // ------------------------------------------------------------------ the helper

    sealed class HelperException : Exception { public string Raw; public HelperException(string plain, string raw) : base(plain) { Raw = raw; } }

    /// A failed call throws PLAIN words for the tile; the raw error goes to the log once per change.
    async Task<JsonObject> Sc(string cmd, JsonObject? args, int ms = 6000)
    {
        var r = await _ctx.Platform.CallAsync(cmd, args ?? new JsonObject(), TimeSpan.FromMilliseconds(ms)).ConfigureAwait(true);
        if (!r.Ok)
        {
            var raw = $"{cmd}: {r.Error}";
            if (raw != _scErr) { _scErr = raw; _ctx.Log(raw); }
            throw new HelperException(Plain(r.Error), r.Error ?? "");
        }
        return Node(r.Result) as JsonObject ?? new JsonObject();
    }

    /// A write: sent live; a dry run logs what would have been sent and answers null.
    async Task<JsonObject?> Write(string cmd, JsonObject args, int ms = 6000)
    {
        if (_dry) { _ctx.Log($"dry run (--test): would ask the sidecar {cmd} {args.ToJsonString()}; nothing was sent"); return null; }
        return await Sc(cmd, args, ms).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ the record

    bool RecordAllowed()
    {
        // a test run never reads or writes the live profile's record, whatever the data root says
        if (!_ctx.Flags.Test) return true;
        var live = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mantle");
        return !string.Equals(Path.GetFullPath(_h.DataRoot).TrimEnd('\\'), Path.GetFullPath(live).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
    void WriteRecord(JsonObject? rec)
    {
        if (_dry || rec == null) return;
        var text = rec.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (_h.Files != null)
        {
            // through the ONE write gate (a refusal is said, never silent)
            var why = _h.Files.WriteAtomic(_recordPath, System.Text.Encoding.UTF8.GetBytes(text), _recordPath + ".tmp");
            if (why != null) throw new IOException(why);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_recordPath)!);
        var tmp = _recordPath + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, _recordPath, true);
    }
    void SaveRecord() => WriteRecord(_record);
    void DropRecord()
    {
        if (!RecordAllowed() || !File.Exists(_recordPath)) return;
        if (_h.Files != null) { if (_h.Files.Remove(_recordPath) is { } why) _ctx.Log($"the restore record could not be removed: {why}"); return; }
        try { File.Delete(_recordPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    JsonObject? ReadRecord()
    {
        if (!RecordAllowed()) return null;
        try { return JsonNode.Parse(File.ReadAllText(_recordPath)) as JsonObject; } catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ power

    async Task ApplyPower()
    {
        JsonObject st;
        try { st = await Sc("powermode.get", null, 4000).ConfigureAwait(true); }
        catch (HelperException e) { Act("power", "Power mode", "failed", $"could not be read: {e.Message}"); return; }
        if (False(st["ok"]) || !True(st["available"])) { Act("power", "Power mode", "blocked", Str(st["reason"]) ?? "Windows would not say"); return; }
        string? src = Str(st["source"]) is "ac" or "battery" ? Str(st["source"]) : null;
        string where = src == "battery" ? "on battery" : "plugged in";
        var set = Set();
        if (True(st["saver"]))
        {
            if (set.LiftSaver && src == "battery" && _record?["saver"] == null)
            {
                if (_dry) Act("saver", "Energy saver", "dry", "test run: would lift Energy saver (turn-on level 0%) and put your level back after");
                else
                {
                    try
                    {
                        var r = (await Write("gamemode.saver", new JsonObject { ["at"] = 0 }).ConfigureAwait(true))!;
                        if (!True(r["same"]) && !False(r["ok"]) && _record != null) { _record["saver"] = new JsonObject { ["was"] = r["before"]?.DeepClone(), ["set"] = 0 }; SaveRecord(); }
                        bool ok = !False(r["ok"]); double saver = Num(r["saver"]) ?? 1;
                        Act("saver", "Energy saver", ok && saver < 1 ? "done" : "failed",
                            ok ? (saver < 1 ? $"lifted for the game (turn-on level {Num(r["before"])}% → 0%, put back after)" : "Energy saver stayed on (it may have been turned on by hand): turn it off in quick settings") : Str(r["reason"]) ?? "refused");
                        st = await Sc("powermode.get", null, 4000).ConfigureAwait(true);
                    }
                    catch (HelperException e) { Act("saver", "Energy saver", "failed", e.Message); }
                }
            }
            else if (_record?["saver"] == null)
                Act("saver", "Energy saver", "blocked", $"Energy saver is on, so Windows keeps Best power efficiency {where}" + (src == "battery" && !set.LiftSaver
                    ? ". To lift it while you play, turn on \"Lift Energy saver while gaming on battery\" below" : ""));
        }
        else Act("saver", "Energy saver", "same", $"off {where}");
        if (!True(st["settable"])) { Act("power", "Power mode", "blocked", True(st["saver"]) ? "not changed while Energy saver is on" : Str(st["reason"]) ?? "Windows will not take a power mode now"); return; }
        var mine = Str(st["configured"]?[src == "ac" ? "ac" : "battery"]);
        if (src == null) { Act("power", "Power mode", "blocked", "the power source is unknown"); return; }
        if (mine == "performance") { Act("power", "Power mode", "same", $"Best performance is already your choice {where}"); return; }
        if (_dry)
        {
            _ctx.Log($"dry run (--test): would ask the sidecar gamemode.power {{\"source\":\"{src}\",\"mode\":\"performance\"}}; nothing was sent");
            Act("power", "Power mode", "dry", $"test run: would set Best performance {where} (now {mine}) and put it back after"); return;
        }
        try
        {
            var r = (await Write("gamemode.power", new JsonObject { ["source"] = src, ["mode"] = "performance" }).ConfigureAwait(true))!;
            bool ok = !False(r["ok"]);
            if (ok && !True(r["same"]) && _record != null) { (_record["power"] as JsonObject ?? (JsonObject)(_record["power"] = new JsonObject()))[src] = new JsonObject { ["was"] = r["before"]?.DeepClone(), ["set"] = "performance" }; SaveRecord(); }
            Act("power", "Power mode", ok ? "done" : "failed", ok ? $"Best performance {where} (was {Str(r["before"])}; put back after)" : Str(r["reason"]) ?? "refused");
        }
        catch (HelperException e) { Act("power", "Power mode", "failed", e.Message); }
    }

    // ------------------------------------------------------------------ processes

    async Task<(HashSet<int> Pids, bool Ok, string? Reason)> AudioPids()
    {
        try
        {
            var a = await Sc("audio", new JsonObject { ["op"] = "sessions" }, 4000).ConfigureAwait(true);
            var pids = new HashSet<int>();
            if (a["sessions"] is JsonArray ss) foreach (var s in ss.OfType<JsonObject>()) if (True(s["active"]) && Int(s["pid"]) is > 0 and var p) pids.Add(p);
            return (pids, !False(a["ok"]), Str(a["reason"]));
        }
        catch (HelperException e) { return (new HashSet<int>(), false, e.Message); }
    }

    sealed record Scan(JsonObject Raw, GameFg? Fg, List<GameProc> Procs, int OwnPid, int? OwnSession);
    static Scan ScanOf(JsonObject r) => new(r, Fg(r["fg"]), Procs(r["procs"]), Int(r["ownPid"]), Num(r["ownSession"]) is double s ? (int)s : null);

    JsonArray RecordProcs() => _record?["procs"] as JsonArray ?? new JsonArray();
    IEnumerable<GameEased> Eased() => _dry ? _dryEased.Where(t => t.Group == "background").Select(t => new GameEased(t.Pid, t.Created, t.File, t.Group))
        : RecordProcs().OfType<JsonObject>().Select(GameModeJson.Eased).Where(e => e.Group == "background");

    GamePlanContext PlanContext(Scan scan, HashSet<int>? audio = null) => new()
    {
        GamePid = _game == null ? 0 : Int(_game["pid"]), GameFile = Str(_game?["file"]), FgPid = scan.Fg?.Pid ?? 0, SidecarPid = scan.OwnPid,
        OwnSession = scan.OwnSession, MantlePids = MantlePids(), AudioPids = audio ?? new HashSet<int>(), MainPid = MainPid,
        WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) is { Length: > 0 } wd ? wd : @"C:\Windows",
    };


    async Task ApplyProcs(Scan scan)
    {
        var au = await AudioPids().ConfigureAwait(true);
        if (!au.Ok) { Act("background", "Background apps", "blocked", $"not changed: which apps play sound could not be read ({au.Reason}), and a call or music must never be slowed"); return; }
        var done = new HashSet<int>(RecordProcs().OfType<JsonObject>().Select(r => Int(r["pid"])));
        if (_dry) foreach (var t in _dryEased) done.Add(t.Pid);
        var pl = GameModePlan.Plan(scan.Procs, PlanContext(scan, au.Pids));
        bool boostOn = Set().Boost;
        var fresh = pl.Targets.Where(t => !done.Contains(t.Pid) && (boostOn || t.Group == "mantle")).ToList();
        if (pl.Pending > 0 && !_replanSoon) _ctx.Log($"{pl.Pending} background process(es) were still being read; looking again at the next check");
        _replanSoon = pl.Pending > 0;
        var lines = new Dictionary<string, List<GameTarget>> { ["background"] = new(), ["mantle"] = new() };
        var failed = new List<(string File, string Why)>();
        if (_dry)
        {
            foreach (var t in fresh) lines[t.Group].Add(t);
            if (fresh.Count > 0) _ctx.Log($"dry run (--test): would ask the sidecar gamemode.apply for {fresh.Count} process{(fresh.Count == 1 ? "" : "es")} (Efficiency mode, below-normal); nothing was sent");
        }
        else
        {
            for (int i = 0; i < fresh.Count; i += Chunk)
            {
                var part = fresh.Skip(i).Take(Chunk).ToList();
                JsonArray res;
                try { res = (await Sc("gamemode.apply", new JsonObject { ["items"] = new JsonArray(part.Select(t => (JsonNode)ApplyItem(t)).ToArray()) }, 10000).ConfigureAwait(true))["results"] as JsonArray ?? new JsonArray(); }
                catch (HelperException e) { failed.Add(($"{part.Count} apps", e.Message)); continue; }
                for (int k = 0; k < part.Count; k++)
                {
                    var r = k < res.Count ? res[k] as JsonObject : null;
                    var e = RecordEntry(part[k], r);
                    if (e != null) { RecordProcs().Add((JsonNode)e); lines[part[k].Group].Add(part[k]); }
                    else if (r != null && False(r["ok"]) && !True(r["gone"])) failed.Add((part[k].File ?? "?", Str(r["eco"]?["why"]) ?? Str(r["why"]) ?? "refused"));
                }
                SaveRecord();
            }
        }
        var slowed = lines["background"].Concat(lines["mantle"]).ToList();
        if (slowed.Count > 0 || failed.Count > 0)
            _ctx.Log($"{(_dry ? "dry run: would slow" : "slowed")} {slowed.Count} process{(slowed.Count == 1 ? "" : "es")} (Efficiency mode, below-normal): {(slowed.Count > 0 ? ExeNames(slowed.Select(t => t.File)) : "none")}{(failed.Count > 0 ? $"; not changed: {ExeNames(failed.Select(f => (string?)f.File))}" : "")}");
        List<string?> All(string g) => RecordProcs().OfType<JsonObject>().Where(r => Str(r["group"]) == g).Select(r => Str(r["file"]))
            .Concat(_dry ? _dryEased.Where(t => t.Group == g).Select(t => t.File).Concat(lines[g].Select(t => t.File)) : Enumerable.Empty<string?>()).ToList();
        if (_dry) _dryEased.AddRange(slowed);
        var bg = All("background"); var mt = All("mantle");
        if (_dry) { bg = _dryEased.Where(t => t.Group == "background").Select(t => t.File).ToList(); mt = _dryEased.Where(t => t.Group == "mantle").Select(t => t.File).ToList(); }
        string verb = _dry ? "test run: would put" : "";
        string status = !boostOn && bg.Count == 0 ? "skipped" : _dry ? "dry" : bg.Count > 0 ? "done" : "same";
        string text = (!boostOn && bg.Count == 0 ? "left as they are: the boost is off"
                : bg.Count > 0 ? $"{(verb.Length > 0 ? verb + " " : "")}Efficiency mode{(_dry ? "" : " on")} {bg.Count} app processes: {GameModePlan.Names(bg, 6)}" : "nothing to slow down")
            + (failed.Count > 0 ? $". Not changed: {GameModePlan.Names(failed.Select(f => (string?)f.File), 4)} ({failed[0].Why})" : "")
            + (pl.Left.Count > 0 ? $". Left alone: {string.Join("; ", GameModePlan.LeftByWhy(pl.Left).Select(l => l.Text))}" : "");
        Act("background", "Background apps", status, text);
        Act("mantle", "Mantle itself", _dry ? "dry" : mt.Count > 0 ? "done" : "same",
            mt.Count > 0 ? $"{(_dry ? "test run: would put " : "")}{mt.Count} Mantle process{(mt.Count == 1 ? "" : "es")} in Efficiency mode at below-normal priority (its keyboard helper keeps normal speed, so keys never lag)" : "nothing to change");
        BoostAct();
    }

    // ------------------------------------------------------------------ the boost

    Boost BoostStart() => new() { State = Set().Boost ? "waiting" : "off" };
    static string Lc(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// The boost's words: the tile's line and its Details row.
    void BoostAct()
    {
        var b = _boost;
        if (b == null) return;
        var eased = Eased().Select(e => e.File).ToList();
        b.Warn = false;
        string easedWords = eased.Count > 0 ? $"eased {GameModePlan.AppCount(eased)}" : "no background app needed easing";
        var left = b.Skip.Concat(b.Refused).ToList();
        string leftWords = left.Count > 0 ? $". Not trimmed: {string.Join("; ", GameModePlan.LeftByWhy(left).Select(l => l.Text))}" : "";
        string status = "done", row;
        if (b.State == "off")
        {
            b.Text = "Boost is off: background apps are left as they are";
            status = "skipped"; row = "off: background apps are left as they are. Switch it on below, or in Settings > Gaming";
        }
        else if (b.State is "waiting" or "trimming")
        {
            b.Text = $"Boost: {easedWords}; idle memory is handed back a minute in";
            row = $"{Cap(easedWords)} (Efficiency mode, lower priority). A minute in, idle ones hand their unused memory back to Windows once";
        }
        else if (b.State == "dry")
        {
            status = "dry";
            var ease = easedWords.StartsWith("eased", StringComparison.Ordinal) ? "ease" + easedWords[5..] : easedWords;
            b.Text = $"Test run: the boost would free about {GameModePlan.Bytes(b.WouldFree)} from {b.WouldTrim} idle process{(b.WouldTrim == 1 ? "" : "es")} and {ease}";
            row = b.Text + leftWords;
        }
        else if (b.State == "done")
        {
            var line = GameModePlan.BoostLine(eased, b.Freed, b.Trimmed);
            var free = b.FreeAfter is long fa ? $"; {GameModePlan.Bytes(fa)} of memory free now" : "";
            b.Text = $"Boost: {Lc(line)}{free}";
            row = $"{line}{(b.Trimmed > 0 ? $" ({b.Trimmed} idle process{(b.Trimmed == 1 ? "" : "es")} handed their unused memory back once)" : "; no idle background process held enough memory to trim")}{free}"
                + (b.Suggest != null ? $". {b.Suggest}" : "") + leftWords;
        }
        else
        {
            b.Warn = true; status = b.State;
            b.Text = Set().Boost ? $"Boost: {easedWords}, but idle memory was NOT handed back: {b.Why}" : $"Boost is off, but {b.Why}";
            row = Set().Boost ? $"{Cap(easedWords)}. Idle memory was NOT handed back: {b.Why}" : $"Switched off, but {b.Why}";
        }
        Act("boost", "Boost", status, row);
    }

    async Task BoostTrim(Scan scan)
    {
        var b = _boost;
        if (b == null || b.State != "waiting" || !_on) return;
        b.State = "trimming";
        var au = await AudioPids().ConfigureAwait(true);
        if (!au.Ok)
        {
            b.State = "blocked"; b.Why = $"which apps play sound could not be read ({au.Reason}), and a call or music must never be slowed";
            _ctx.Log($"boost: idle memory not handed back: {b.Why}"); BoostAct(); return;
        }
        var eased = _dry ? _dryEased.Select(t => new GameEased(t.Pid, t.Created, t.File, t.Group)) : RecordProcs().OfType<JsonObject>().Select(GameModeJson.Eased);
        var tp = GameModePlan.TrimPlan(scan.Procs, eased.ToList(), _cpuBase, au.Pids, _trimDone, _h.IdleCpuMs);
        b.Skip = tp.Skip.ToList();
        b.FreeBefore = FreeMem();
        if (_dry)
        {
            b.State = "dry"; b.WouldTrim = tp.Trim.Count; b.WouldFree = tp.Trim.Sum(t => t.Ws);
            _ctx.Log($"boost (DRY RUN): would trim {tp.Trim.Count} idle process{(tp.Trim.Count == 1 ? "" : "es")} ({GameModePlan.Bytes(b.WouldFree)} in use)");
            BoostAct(); return;
        }
        if (tp.Trim.Count > 0)
        {
            var results = new List<JsonObject>(); var tried = new List<GameTrim>();
            int trimmed = 0; long freed = 0; HelperException? threw = null;
            foreach (var part in GameModePlan.TrimCalls(tp.Trim))
            {
                JsonObject res;
                try { res = await Sc("gamemode.trimbg", new JsonObject { ["items"] = new JsonArray(part.Select(t => (JsonNode)new JsonObject { ["pid"] = t.Pid, ["created"] = t.Created, ["file"] = t.File }).ToArray()) }, 15000).ConfigureAwait(true); }
                catch (HelperException e)
                {
                    threw = e; if (trimmed == 0) break;
                    foreach (var t in part) { tried.Add(t); results.Add(new JsonObject { ["pid"] = t.Pid, ["file"] = t.File, ["ok"] = false, ["why"] = e.Message }); }
                    break;
                }
                tried.AddRange(part);
                if (res["results"] is JsonArray ra) results.AddRange(ra.OfType<JsonObject>());
                trimmed += Int(res["trimmed"]); freed += Long(res["freed"]);
            }
            if (threw != null && trimmed == 0) { b.State = "failed"; b.Why = threw.Message; _ctx.Log($"boost: idle memory not handed back: {threw.Message}"); BoostAct(); return; }
            foreach (var t in tried) _trimDone.Add(t.Pid);
            b.Trimmed = trimmed; b.Freed = freed;
            b.Refused = results.Where(r => False(r["ok"]) && !True(r["gone"])).Select(r => new GameLeft(Str(r["file"]), System.Text.RegularExpressions.Regex.Replace(Str(r["why"]) ?? "refused", "^refused: ", ""))).ToList();
            var hard = b.Refused.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.Why, "^Windows refused|would not let")).ToList();
            if (hard.Count > 0 && b.Trimmed == 0) { b.State = "failed"; b.Why = hard[0].Why; _ctx.Log($"boost: idle memory not handed back: {hard[0].Why}"); BoostAct(); return; }
            if (_record != null) { _record["trim"] = new JsonObject { ["pids"] = new JsonArray(_trimDone.Select(p => (JsonNode)p).ToArray()) }; try { SaveRecord(); } catch (IOException) { } }
        }
        b.FreeAfter = FreeMem(); b.At = Now(); b.State = "done";
        if (b.FreeAfter < GameModePlan.LowFreeBytes)
        {
            var big = GameModePlan.BiggestApp(scan.Procs, PlanContext(scan));
            b.Suggest = $"Still only {GameModePlan.Bytes(b.FreeAfter.Value)} free" + (big is { } bg ? $": closing {bg.Name} would free up to {GameModePlan.Bytes(bg.Ws)} (Mantle never closes apps)" : "");
        }
        _ctx.Log($"boost: {b.Trimmed} idle process{(b.Trimmed == 1 ? "" : "es")} handed back {GameModePlan.Bytes(b.Freed)}; free memory {GameModePlan.Bytes(b.FreeBefore ?? 0)} -> {GameModePlan.Bytes(b.FreeAfter ?? 0)}"
            + (b.Refused.Count > 0 ? $"; not trimmed: {ExeNames(b.Refused.Select(r => r.File))}" : ""));
        var at = _since;
        _clock.Later(TimeSpan.FromMinutes(1), () => { if (_on && _since == at) _ctx.Log($"boost: a minute later {GameModePlan.Bytes(FreeMem())} of memory free"); });
        BoostAct();
    }

    /// Mid-game switch: off puts the eased background apps back now; on eases at the next check.
    async Task SyncBoost(string from)
    {
        bool want = Set().Boost;
        if (want == _boostWas) return;
        _boostWas = want;
        _ctx.Log($"boost: switched {(want ? "on" : "off")} ({from})");
        if (!_on || _boost == null) return;
        if (want) { _boost = BoostStart(); _cpuBase = null; _replanSoon = true; BoostAct(); Push(); return; }
        _boost.State = "off";
        var bg = RecordProcs().OfType<JsonObject>().Where(r => Str(r["group"]) == "background").ToList();
        if (_dry) _dryEased.RemoveAll(t => t.Group == "background");
        else if (bg.Count > 0)
        {
            try
            {
                var outp = await Restore(new JsonObject { ["procs"] = new JsonArray(bg.Select(r => (JsonNode)r.DeepClone()).ToArray()) }, "the boost was switched off").ConfigureAwait(true);
                var kept = outp.Keep?["procs"] as JsonArray;
                var keep = RecordProcs().OfType<JsonObject>().Where(r => Str(r["group"]) != "background").Select(r => (JsonNode)r.DeepClone()).ToList();
                if (kept != null) keep.AddRange(kept.Select(k => k!.DeepClone()));
                if (_record != null) { _record["procs"] = new JsonArray(keep.ToArray()); SaveRecord(); }
                if (kept is { Count: > 0 }) { _boost.State = "failed"; _boost.Why = $"not every background app was put back yet ({string.Join("; ", outp.Failed)}); they are put back when game mode ends"; }
            }
            catch (HelperException e) { _boost.State = "failed"; _boost.Why = $"the background apps were not put back yet ({e.Message}); they are put back when game mode ends"; }
        }
        BoostAct(); Push();
    }

    // ------------------------------------------------------------------ wallpaper, Lively, info

    void HoldWallpaper(bool on)
    {
        if (_h.WallpaperHold == null) { if (on) Act("wallpaper", "Wallpaper", "blocked", "this version of Mantle cannot pause its wallpaper for games"); return; }
        if (_dry) { if (on) Act("wallpaper", "Wallpaper", "dry", "test run: would hold the wallpaper paused"); return; }
        bool paused = _h.WallpaperHold(on ? $"game mode: {Str(_game?["file"]) ?? "on"}" : null);
        if (on && _record != null) { _record["wallpaper"] = true; SaveRecord(); Act("wallpaper", "Wallpaper", "done", "held paused until game mode ends" + (paused ? "" : " (no wallpaper is playing)")); }
    }

    void Lively(Scan? scan, bool play)
    {
        var lp = scan?.Procs.FirstOrDefault(p => string.Equals(p.File, "Lively.exe", StringComparison.OrdinalIgnoreCase));
        if (!play && lp == null) { Act("lively", "Lively Wallpaper", "same", "not running (Mantle plays the wallpaper itself)"); return; }
        var exe = lp?.Path ?? Str(_record?["lively"]?["path"]);
        if (exe == null) { if (!play) Act("lively", "Lively Wallpaper", "failed", "running, but its path could not be read"); return; }
        string args = $"app --play {(play ? "true" : "false")}";
        if (_dry) { _ctx.Log($"dry run (--test): would run Lively's own command \"{args}\"; nothing was started"); Act("lively", "Lively Wallpaper", "dry", "test run: would pause Lively with its own pause command"); return; }
        if (play && lp == null) return;   // Lively has exited since: nothing to resume, and never start it
        try
        {
            (_h.Spawn ?? SpawnHidden)(exe, args);
            if (!play && _record != null) { _record["lively"] = new JsonObject { ["path"] = exe }; SaveRecord(); Act("lively", "Lively Wallpaper", "done", "paused with its own pause command; resumed after"); }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { Act("lively", "Lively Wallpaper", "failed", Plain(e.Message)); }
    }

    static void SpawnHidden(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
        System.Diagnostics.Process.Start(psi)?.Dispose();
    }

    void InfoRows()
    {
        Act("notify", "Notifications", "info", "Windows' own rule does this: Settings > System > Notifications > Turn on do not disturb automatically > When playing a game. Windows gives apps no supported way to switch Do not disturb, so game mode does not");
        Act("game", "The game itself", "info", "left alone on purpose: never opened for anything but its name, priority untouched (anti-cheat)");
        Act("topbar", "Top bar", "info", "gives its strip back the moment a known game starts, then closes with the rest of Mantle's hidden windows (see \"Mantle is resting\")");
    }

    // ------------------------------------------------------------------ the rest

    async Task SyncRest(string from)
    {
        if (_rest.Busy || _stopped || _ctx is not IFeatureHostServices hs) return;
        string? gameFile = _game != null && Str(_game["why"]) != "turned on by hand" ? Str(_game["file"]) : null;
        var d = GameModePlan.RestStep(new GameRestState(_rest.Resting, _rest.Why, _rest.Hold), new GameRestInput(_lastRun, _on, _how ?? "auto", gameFile, _ctx.Enabled));
        _rest.Hold = d.Hold;
        if (d.Action == null) return;
        _rest.Busy = true; Push();
        try
        {
            if (d.Action == "rest")
            {
                var r = await hs.RestAsync(true, d.Why!).ConfigureAwait(true);
                if (r.Ok)
                {
                    bool first = !_rest.Resting;
                    _rest.Resting = true; _rest.Why = d.Why; _rest.Since = first ? Now() : _rest.Since;
                    if (first || _rest.Text == null)
                    {
                        _rest.Text = r.Text != null ? $"Mantle is resting while {d.Why}: {r.Text}" : $"Mantle is resting while {d.Why}";
                        _ctx.Log($"{_rest.Text} ({from})");
                    }
                    else _rest.Text = System.Text.RegularExpressions.Regex.Replace(_rest.Text, "^Mantle is resting while .+?: ", $"Mantle is resting while {d.Why}: ");
                }
                else
                {
                    var t = $"Mantle could not rest while {d.Why}: {r.Reason ?? "no answer"}";
                    if (t != _rest.Text) _ctx.Log(t);
                    _rest.Text = t;
                }
            }
            else
            {
                var r = await hs.RestAsync(false, d.Why!).ConfigureAwait(true);
                long mins = _rest.Since is long s ? (long)Math.Round((Now() - s) / 60000.0) : 0;
                var text = r.Ok ? $"Mantle rested {(mins < 1 ? "under a minute" : $"{mins} min")} ({_rest.Why}) and is back: {d.Why}" : $"Mantle could not come back after the game: {r.Reason ?? "no answer"}";
                _rest.Last = new JsonObject { ["at"] = Now(), ["why"] = d.Why, ["minutes"] = mins, ["text"] = text };
                _rest.Resting = false; _rest.Why = null; _rest.Since = null; _rest.Text = null;
                _ctx.Log($"{text}{(r.Back is { Count: > 0 } back ? $" ({string.Join(", ", back)})" : "")}");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _rest.Text = $"Mantle could not {(d.Action == "rest" ? "rest" : "come back")}: {Plain(e.Message)}";
            _ctx.Log(_rest.Text);
        }
        finally { _rest.Busy = false; Push(); }
    }

    // ------------------------------------------------------------------ on / off

    async Task TurnOn(JsonObject game, string how)
    {
        if (_on || _busy != null) return;
        _busy = "on"; _on = true; _how = how; _since = Now();
        _onDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        game["lastFront"] = Now();
        _game = game;
        _actions.Clear();
        // Anything the last game's restore could not put back yet rides along in this record.
        var lo = _leftover is { GaveUp: false } l ? l.Rec : null;
        _leftover = null;
        // The boost: what a crashed instance already trimmed for this same run of the game stays trimmed-once.
        var ct = _crashTrim;
        int gpid = Int(game["pid"]);
        _trimDone = ct is { } c && gpid != 0 && c.GamePid == gpid && GameModePlan.SameFile(c.GameFile, Str(game["file"])) ? new HashSet<int>(c.Pids) : new HashSet<int>();
        _crashTrim = null;
        _boost = BoostStart(); _cpuBase = null; _dryEased = new List<GameTarget>();
        _record = new JsonObject
        {
            ["v"] = 1, ["since"] = _since, ["how"] = how, ["game"] = new JsonObject { ["file"] = Str(game["file"]), ["pid"] = gpid },
            ["power"] = lo?["power"]?.DeepClone() ?? new JsonObject(), ["saver"] = lo?["saver"]?.DeepClone(),
            ["procs"] = lo?["procs"]?.DeepClone() ?? new JsonArray(), ["wallpaper"] = false, ["lively"] = null,
            ["trim"] = new JsonObject { ["pids"] = new JsonArray(_trimDone.Select(p => (JsonNode)p).ToArray()) },
        };
        try { SaveRecord(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Act("record", "Restore record", "failed", $"could not be written ({Plain(e.Message)}): a crash now would leave changes behind until they are undone by hand"); }
        Push();
        try
        {
            await ApplyPower().ConfigureAwait(true);
            var scan = ScanOf(await Sc("gamemode.scan", new JsonObject { ["procs"] = true, ["detail"] = true }, 10000).ConfigureAwait(true));
            _cpuBase = GameModePlan.CpuBase(scan.Procs); _cpuBaseAt = Now();     // the boost's idle rule starts here
            await ApplyProcs(scan).ConfigureAwait(true);
            _lastReplan = Now();
            HoldWallpaper(true);
            Lively(scan, false);
            InfoRows();
        }
        catch (HelperException e) { Act("error", "Game mode", "failed", e.Message); }
        _busy = null;
        _onDone.TrySetResult();
        _ctx.Log($"on ({how}{(_dry ? ", DRY RUN" : "")}): {Str(game["why"]) ?? Str(game["file"])} | {string.Join(" ", _actions.Where(a => Str(a["status"]) != "info").Select(a => $"{Str(a["key"])}={Str(a["status"])}"))}");
        Push();
    }

    sealed class RestoreOut { public int Procs, Gone, Skipped; public List<string> Failed = new(); public JsonObject? Keep; public string? Power, Saver; public string Text = ""; }

    /// Puts back what <paramref name="rec"/> says game mode changed. Throws when the helper stops mid-way (the caller then
    /// keeps the whole record: putting back twice is harmless, ecoIf/prioIf/onlyIf make it a no-op). A dry run sends
    /// nothing and says what it would have put back.
    async Task<RestoreOut> Restore(JsonObject? rec, string why)
    {
        var o = new RestoreOut();
        if (rec == null) return o;
        var keep = new JsonObject { ["v"] = 1, ["since"] = rec["since"]?.DeepClone(), ["how"] = rec["how"]?.DeepClone(), ["game"] = rec["game"]?.DeepClone(), ["power"] = new JsonObject(), ["saver"] = null, ["procs"] = new JsonArray(), ["wallpaper"] = false, ["lively"] = null };
        if (rec["lively"] is JsonObject)
        {
            Scan? s = null;
            try { s = ScanOf(await Sc("gamemode.scan", new JsonObject { ["procs"] = true, ["detail"] = true }, 10000).ConfigureAwait(true)); } catch (HelperException) { }
            Lively(s, true);
        }
        if (True(rec["wallpaper"]) && !_dry) _h.WallpaperHold?.Invoke(null);
        var all = (rec["procs"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        for (int i = 0; i < all.Count; i += Chunk)
        {
            var part = all.Skip(i).Take(Chunk).ToList();
            var args = new JsonObject { ["items"] = new JsonArray(part.Select(e => (JsonNode)RestoreItem(e)).ToArray()) };
            var reply = await Write("gamemode.apply", args, 10000).ConfigureAwait(true);
            if (reply == null) { o.Procs += part.Count; continue; }   // a dry run: counted as it would be put back
            var res = reply["results"] as JsonArray ?? new JsonArray();
            for (int k = 0; k < part.Count; k++)
            {
                var entry = part[k]; int pid = Int(entry["pid"]);
                var r = k < res.Count && res[k] is JsonObject rk && Int(rk["pid"]) == pid ? rk : res.OfType<JsonObject>().FirstOrDefault(x => Int(x["pid"]) == pid);
                if (r == null) { o.Failed.Add($"{GameModePlan.BaseName(Str(entry["file"]))}: no answer"); ((JsonArray)keep["procs"]!).Add((JsonNode)entry.DeepClone()); }
                else if (True(r["gone"])) o.Gone++;
                else if (!False(r["ok"]) && (True(r["eco"]?["skipped"]) || True(r["prio"]?["skipped"]))) o.Skipped++;
                else if (!False(r["ok"])) o.Procs++;
                else { o.Failed.Add($"{Str(r["file"]) ?? Str(entry["file"])}: {Str(r["why"]) ?? Str(r["eco"]?["why"]) ?? Str(r["prio"]?["why"]) ?? "refused"}"); ((JsonArray)keep["procs"]!).Add((JsonNode)entry.DeepClone()); }
            }
        }
        if (rec["power"] is JsonObject power)
            foreach (var (src, pn) in power.ToList())
            {
                if (pn is not JsonObject p) continue;
                string was = Str(p["was"]) ?? "balanced";
                string where = src == "ac" ? "plugged in" : "on battery";
                var r = await Write("gamemode.power", new JsonObject { ["source"] = src, ["mode"] = was, ["onlyIf"] = p["set"]?.DeepClone() }).ConfigureAwait(true);
                string piece;
                if (r == null) piece = $"{where} back to {was}";
                else if (!False(r["ok"])) piece = True(r["skipped"]) ? Str(r["reason"]) ?? $"{where} left as it is" : $"{where} back to {was}";
                else { piece = $"power mode {where} NOT put back: {Str(r["reason"])}"; o.Failed.Add($"power mode {where}: {Str(r["reason"])}"); ((JsonObject)keep["power"]!)[src] = p.DeepClone(); }
                o.Power = (o.Power != null ? o.Power + "; " : "") + piece;
            }
        if (rec["saver"] is JsonObject sv)
        {
            var r = await Write("gamemode.saver", new JsonObject { ["at"] = sv["was"]?.DeepClone(), ["onlyIf"] = sv["set"]?.DeepClone() }).ConfigureAwait(true);
            string was = Num(sv["was"])?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
            if (r == null || !False(r["ok"])) o.Saver = r != null && True(r["skipped"]) ? Str(r["reason"]) : $"Energy saver level back to {was}%";
            else { o.Saver = $"Energy saver level NOT put back: {Str(r["reason"])}"; o.Failed.Add(o.Saver); keep["saver"] = sv.DeepClone(); }
        }
        if (o.Failed.Count > 0) o.Keep = keep;
        o.Text = (_dry ? $"test run: would put {o.Procs} apps back to normal" : $"{o.Procs} apps back to normal")
            + (o.Gone > 0 ? $", {o.Gone} had exited" : "") + (o.Skipped > 0 ? $", {o.Skipped} had changed their own setting (left)" : "")
            + (o.Power != null ? $"; {o.Power}" : "") + (o.Saver != null ? $"; {o.Saver}" : "") + (True(rec["wallpaper"]) ? "; wallpaper let go" : "")
            + (rec["lively"] is JsonObject ? "; Lively resumed" : "") + (o.Failed.Count > 0 ? $"; NOT put back: {string.Join("; ", o.Failed)}" : "");
        _ctx.Log($"restore ({why}): {o.Text}");
        return o;
    }

    async Task TurnOff(string why)
    {
        if (!_on || _busy == "off") return;
        if (_busy == "on" && _onDone != null) await _onDone.Task.ConfigureAwait(true);
        if (!_on) return;
        _busy = "off"; Push();
        RestoreOut o;
        try { o = _dry ? new RestoreOut { Text = "test run: nothing had been changed" } : await Restore(_record, why).ConfigureAwait(true); }
        catch (HelperException e) { o = new RestoreOut { Text = $"could not finish ({e.Message}); Mantle tries again in {RetryMs / 1000} s", Keep = _record }; o.Failed.Add(e.Message); }
        var b = _boost;
        string boosted = b is { State: "done", Trimmed: > 0 } ? $"; the boost had freed {GameModePlan.Bytes(b.Freed)}" : "";
        _last = new JsonObject { ["at"] = Now(), ["why"] = why, ["game"] = Str(_game?["file"]), ["minutes"] = (long)Math.Round((Now() - (_since ?? Now())) / 60000.0), ["text"] = o.Text + boosted, ["ok"] = o.Failed.Count == 0 };
        _boost = null; _cpuBase = null;
        // What could not be put back is kept, on disk, tried again every 30 s, and carried into the next game's record.
        if (o.Failed.Count == 0) DropRecord();
        else HoldLeftover(o.Keep ?? _record!, o.Failed);
        _record = null;
        _on = false; _how = null; _game = null; _since = null; _busy = null;
        _actions.Clear(); _dryEased = new List<GameTarget>();
        _ctx.Log($"off: {why}");
        Push();
    }

    void HoldLeftover(JsonObject rec, List<string> failed, int tries = 0)
    {
        _leftover = new Leftover { Rec = rec, At = Now(), Tries = tries, Text = $"Not put back yet: {string.Join("; ", failed)}. Mantle tries again every {RetryMs / 1000} s" };
        try { WriteRecord(rec); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ctx.Log($"restore record could not be written: {Plain(e.Message)}"); }
    }

    async Task RetryLeftover(string why)
    {
        var l = _leftover;
        if (l == null || l.GaveUp || _on || _busy != null) return;
        l.At = Now(); l.Tries++;
        RestoreOut o;
        try { o = await Restore(l.Rec, why).ConfigureAwait(true); }
        catch (HelperException e) { o = new RestoreOut { Keep = l.Rec }; o.Failed.Add(e.Message); }
        if (_leftover != l) return;   // a game started meanwhile and took it over
        if (o.Failed.Count == 0)
        {
            _leftover = null; DropRecord();
            var later = $"put back on a later try: {o.Text}";
            if (_fromCrash != null && False(_fromCrash["ok"])) { _fromCrash["ok"] = true; _fromCrash["text"] = later; }
            else if (_last != null) { _last["ok"] = true; _last["text"] = later; }
            _ctx.Log($"restore retry {l.Tries}: everything put back");
        }
        else if (l.Tries >= RetryMax)
        {
            DropRecord();
            _leftover = new Leftover { Rec = l.Rec, At = l.At, Tries = l.Tries, GaveUp = true,
                Text = $"Mantle could not put these back: {string.Join("; ", o.Failed)}. Apps go back to normal when they restart; a power setting can be changed in Settings > System > Power & battery" };
            _ctx.Log($"gave up putting back after {l.Tries} tries");
        }
        else HoldLeftover(o.Keep ?? l.Rec, o.Failed, l.Tries);
        Push();
    }

    /// A record on disk at start = Mantle crashed or was killed mid-game: put it back first. Retried after every
    /// sidecar restart until it has been done.
    async Task OnReadyAsync()
    {
        if (!_crashChecked && !_on)
        {
            _crashChecked = true;
            var rec = ReadRecord();
            if (rec?["trim"]?["pids"] is JsonArray tp && tp.Count > 0 && rec["game"] is JsonObject g)
                _crashTrim = (Int(g["pid"]), Str(g["file"]), tp.Select(p => Int(p)).ToList());
            if (rec != null)
            {
                _fromCrash = new JsonObject { ["at"] = Now(), ["game"] = Str(rec["game"]?["file"]), ["text"] = "putting back what the last game changed…", ["ok"] = false };
                Push();
                try
                {
                    var o = await Restore(rec, "left over from before Mantle restarted").ConfigureAwait(true);
                    _fromCrash["text"] = o.Text; _fromCrash["ok"] = o.Failed.Count == 0;
                    if (o.Failed.Count == 0) { DropRecord(); if (_dry) _ctx.Log("test run: the restore record was read from the test profile and dropped; nothing was sent"); }
                    else HoldLeftover(o.Keep ?? rec, o.Failed);
                }
                catch (HelperException e)
                {
                    _fromCrash["text"] = $"could not put it back yet ({e.Message}); Mantle tries again in {RetryMs / 1000} s";
                    HoldLeftover(rec, new List<string> { e.Message });
                }
                Push();
            }
        }
        Schedule(0);
        WatchSchedule();
    }

    // ------------------------------------------------------------------ the watch

    async Task<JsonObject?> ProcScan(bool detail)
    {
        Task<JsonObject> Ask(long? since)
        {
            var a = new JsonObject { ["procs"] = true, ["detail"] = detail, ["key"] = WatchKey };
            if (since != null) a["since"] = since;
            return Sc("gamemode.scan", a, 10000);
        }
        var r = GameWatch.Whole(_procMem, await Ask(detail ? null : _procMem.Gen).ConfigureAwait(true));
        return r ?? GameWatch.Whole(_procMem, await Ask(null).ConfigureAwait(true));
    }

    static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };
    static JsonObject WithOk(JsonObject scan) { var c = (JsonObject)scan.DeepClone(); c["ok"] = true; return c; }

    /// One look (exposed for the unit tests; the timers call it).
    public async Task TickAsync()
    {
        _timer = null;
        if (_stopped || !_ctx.Enabled) return;
        if (_ticking) { _again = true; return; }
        _ticking = true;
        try
        {
            if (!_ctx.Platform.Status.Ready)
            {
                bool was = _watching;
                _watching = false; _reason = "games are not being watched: Mantle's background helper is restarting";
                if (was) Push();
                _w?.Publish(Fail(NotReady));
                return;
            }
            bool replan = _on && (_replanSoon || Now() - _lastReplan >= _replanMs);
            JsonObject raw;
            try { raw = await ProcScan(replan).ConfigureAwait(true) ?? throw new HelperException("the process list came back empty", "empty"); }
            catch (HelperException e) { _w?.Publish(Fail(e.Raw)); throw; }
            _w?.Publish(WithOk(raw));
            WatchSchedule();
            if (!_watching) { _watching = true; _ctx.Health(true); Push(); }
            var scan = ScanOf(raw);
            _lastFg = scan.Fg; _lastProcs = scan.Procs;
            _lastRun = GameModePlan.RunningGames(scan.Fg, scan.Procs, scan.OwnSession, _runSeen, _testGames);
            var gr = _game == null ? null : new GameRun(Int(_game["pid"]), Str(_game["file"]) ?? "", Long(_game["lastFront"]));
            var d = GameModePlan.Step(new GameModeState(_on, _how ?? "auto", gr, _suppressPid), scan.Fg, scan.Procs, Now(), Set().Auto);
            _reason = d.Why;
            if (d.Action == "on" && d.Game is { } g)
                await TurnOn(new JsonObject { ["file"] = g.File, ["pid"] = g.Pid, ["title"] = g.Title, ["why"] = g.Why }, "auto").ConfigureAwait(true);
            else if (d.Action == "off") await TurnOff(d.Why).ConfigureAwait(true);
            else if (d.Action == "front" && _game != null) _game["lastFront"] = Now();
            else if (d.Action == "unsuppress") _suppressPid = null;
            if (replan && d.Action != "off" && _on && _busy == null)
            {
                _lastReplan = Now();
                await ApplyProcs(scan).ConfigureAwait(true); Push();
                if (_ctx.Flags.Test) _ctx.Log($"replan {++_replans}: the plan was redone while on (every {_replanMs} ms)");
            }
            // The boost's one trim: a minute after its CPU baseline (taken from a detail scan).
            var b = _boost;
            if (_on && _busy == null && b is { State: "waiting" })
            {
                if (_cpuBase == null) { if (replan) { _cpuBase = GameModePlan.CpuBase(scan.Procs); _cpuBaseAt = Now(); } }
                else if (Now() - _cpuBaseAt >= _h.TrimAfterMs)
                {
                    try
                    {
                        var ds = replan ? scan : ScanOf(await Sc("gamemode.scan", new JsonObject { ["procs"] = true, ["detail"] = true }, 10000).ConfigureAwait(true));
                        await BoostTrim(ds).ConfigureAwait(true);
                    }
                    catch (HelperException e) { b.State = "failed"; b.Why = e.Message; _ctx.Log($"boost: idle memory not handed back: {e.Message}"); BoostAct(); }
                    Push();
                }
            }
            await SyncRest("the game watch").ConfigureAwait(true);
            if (!_on && _busy == null && _leftover is { GaveUp: false } lo && Now() - lo.At >= RetryMs) await RetryLeftover("trying again").ConfigureAwait(true);
        }
        catch (HelperException e)
        {
            _watching = false; _reason = $"games are not being watched: {e.Message}";
            _ctx.Health(false, _reason); Push();
        }
        finally
        {
            _ticking = false;
            bool again = _again; _again = false;
            Schedule(again ? 150 : null);
        }
    }

    /// The tick's own cadence. At rest: the backstop while every foreground change reaches this watch.
    int OwnMs() => _on ? OnMs : _rest.Resting ? RestMs : _w?.Fed() == true ? BackstopMs : IdleMs;

    void Schedule(int? ms = null)
    {
        if (_stopped || !_ctx.Enabled) return;
        _timer?.Dispose();
        _timerMs = ms ?? OwnMs();
        _timer = _clock.Later(TimeSpan.FromMilliseconds(_timerMs.Value), () => _ = TickAsync());
    }

    int? WatchMs()
    {
        if (_stopped || _w == null || !_w.Wanted()) return null;
        var fast = _w.FastMs();
        if (_ctx.Enabled) return fast;   // game mode's own ticks are the rest of the watch
        return fast ?? (_w.Fed() ? BackstopMs : IdleMs);
    }

    void WatchSchedule(int? ms = null)
    {
        _wTimer?.Dispose(); _wTimer = null;
        var every = WatchMs();
        if (every == null) return;
        _wTimer = _clock.Later(TimeSpan.FromMilliseconds(ms ?? every.Value), () => _ = WatchTick());
    }

    async Task WatchTick()
    {
        _wTimer = null;
        if (_stopped) return;
        if (_ticking) { WatchSchedule(); return; }
        _ticking = true;
        try
        {
            if (!_ctx.Platform.Status.Ready) { _w?.Publish(Fail(NotReady)); return; }
            JsonObject? raw;
            try { raw = await ProcScan(false).ConfigureAwait(true); } catch (HelperException e) { _w?.Publish(Fail(e.Raw)); return; }
            if (raw != null) _w?.Publish(WithOk(raw));
        }
        finally
        {
            _ticking = false;
            bool again = _again; _again = false;
            if (again) Schedule(150);
            WatchSchedule();
        }
    }

    // ------------------------------------------------------------------ free memory

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);
        public static long FreeBytes()
        {
            var m = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            try { return GlobalMemoryStatusEx(ref m) ? (long)m.AvailPhys : 0; } catch (Exception) { return 0; }
        }
    }
}
