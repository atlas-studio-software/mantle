using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// [Feature("autopause")] (lib/features/autopause.js): Spotify pauses while another app plays video or sound and starts
/// again after. The wiring of the protocol-2 core: the engine (rules), AutoPauseSpotify (its hands, HOST only), the
/// bridge-compatible host, the link (who decides with Quartz), the coexistence check (the standalone helper wins) and
/// the ticking loop with the sound meter and AutoPauseAttrib (who is playing).
///
/// COST: nothing runs while Spotify is not playing and nothing is held (the NOW PLAYING watch wakes it). While wanted: one
/// verdict every 250 ms, and a meter read (sidecar autopause.peaks, 1500 ms) ONLY while another app has a media session
/// ('media' mode) or in 'any' mode. No tick or meter read in Mantle's first 5 s (0 in --test). The coexistence check
/// every 15 s. Rest = true: a game stops it and every hold is handed back first.
///
/// SAFETY: only the HOST touches Spotify (AutoPauseSpotify refuses otherwise); the old helper and its task are only READ;
/// a coexistence check that throws counts as "in charge"; --test sends dry runs only and never uses the real ports
/// (MANTLE_AUTOPAUSE_PORT names a test port; with none Mantle decides alone and binds nothing); stopping hands back every
/// hold and resumes what Mantle paused. Privacy: the log gets counts and states, never an app name or a title.
/// </summary>
[Feature("autopause", "Spotify auto-pause", true, Rest = true)]
public sealed class AutoPauseFeature : IFeature
{
    public const string StatusEvent = "autopause.status";
    public const string Apps = "apps";
    public const string PortEnv = "MANTLE_AUTOPAUSE_PORT";
    public const int CoexistMsDefault = 15000, StartQuietMsDefault = 5000, WarmingMax = 8, PeaksTimeoutMs = 1500;
    public const string Stopped = "Mantle stopped auto-pause";
    public const string TurnedOff = "auto-pause was turned off";
    public const string HelperInCharge = "the standalone helper is in charge";

    public static readonly IReadOnlyList<FeatureOption> Options = new[]
    {
        new FeatureOption(Apps, "Pause Spotify while", FeatureOptionKind.Choice, "media")
        {
            Choices = new[] { new FeatureChoice("media", "Another app plays video or sound"), new FeatureChoice("any", "Any other app makes a sound") },
            Help = "Like a lesson or video app, or a YouTube video. \"Any sound\" also counts calls, games and notification sounds",
        },
    };

    /// <summary>What the feature is given (tests: a fake check, the socket-free net, a manual clock). Null = the real thing.</summary>
    /// <param name="Check">The coexistence check (live: AutoPauseNet.Reads over the task file, schtasks and /ping).</param>
    /// <param name="Get">The link's loopback GET (live: AutoPauseNet.Get).</param>
    /// <param name="Bind">Binds the host's port (live: AutoPauseServer.TryStart on 127.0.0.1).</param>
    /// <param name="Ports">The link's ports (live: 8787-8790; --test: MANTLE_AUTOPAUSE_PORT or none).</param>
    public sealed record Host(HostClock? Clock = null, Func<Task<AutoPauseCoexist.Verdict>>? Check = null, LinkGet? Get = null,
        AutoPauseBind? Bind = null, IReadOnlyList<int>? Ports = null, int? StartQuietMs = null, int? CoexistMs = null,
        IReadOnlyCollection<long>? MantlePids = null, Func<long>? Now = null, Media? NowPlaying = null);

    /// <summary>NOW PLAYING (MediaService): its newest payload and its change watch. The app hands it over when media is up.</summary>
    public sealed record Media(Func<JsonObject?> Read, Func<Action<JsonObject>, IDisposable> OnChange);

    static Host? _configured;
    static Media? _media;
    public static void Configure(Host h) => _configured = h;
    /// The app's media block calls this once MediaService exists (it may come after the feature installs: it is looked up
    /// again at every coexistence check).
    public static void ConfigureMedia(Media m) => _media = m;

    readonly Host? _given;
    public AutoPauseFeature() { }
    public AutoPauseFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    AutoPauseEngine _engine = null!;
    AutoPauseSpotify _sp = null!;
    AutoPauseHost _host = null!;
    AutoPauseLink _link = null!;
    HashSet<long> _pids = new();
    bool _test, _stopped, _standby = true, _ticking, _checking;
    string _standbyWords = "", _meterWhy = "", _lastSig = "";
    int _warming, _startQuietMs, _coexistMs;
    long _installedAt;
    IDisposable? _timer, _coexistTimer, _offMedia;
    JsonObject? _last;

    long Now() => _h.Now?.Invoke() ?? _clock.NowMs;
    string Mode() => _ctx.Settings.TryGet(Apps, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == "any" ? "any" : "media";
    Media? MediaSource() => _h.NowPlaying ?? _media;
    void Log(string t) => _ctx.Log(t);

    /// <summary>--test ports: MANTLE_AUTOPAUSE_PORT when it is a whole number above 1024, else none (AUTO-109). Live: 8787-8790.</summary>
    public static IReadOnlyList<int> PortsFor(bool test, string? env)
    {
        if (!test) return AutoPauseLink.Ports;
        return int.TryParse(env, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p > 1024 && p <= 65535
            ? new[] { p } : Array.Empty<int>();
    }

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        _test = ctx.Flags.Test;
        _startQuietMs = _h.StartQuietMs ?? (_test ? 0 : StartQuietMsDefault);
        _coexistMs = _h.CoexistMs ?? CoexistMsDefault;
        _pids = new HashSet<long>(_h.MantlePids ?? Array.Empty<long>()) { Environment.ProcessId };
        _installedAt = Now();
        var ports = _h.Ports ?? PortsFor(_test, Environment.GetEnvironmentVariable(PortEnv));
        if (_test && ports.Count == 0) Log("test: no " + PortEnv + ", so Mantle decides alone (dry run) and binds no port");

        _sp = new AutoPauseSpotify(ctx.Platform, () => _link != null && _link.Role == "host", _test, Now, Log);
        _engine = new AutoPauseEngine(_sp, Now, Log);
        var bind = _h.Bind ?? new AutoPauseBind((port, host) => AutoPauseServer.TryStart(port, host, _clock, Log));
        _host = new AutoPauseHost(new AutoPauseHost.Deps(_engine, () => (_sp.Found, _sp.Seen(), _sp.Aumid), ReadMedia, a => _sp.Control(a),
            Log, Now, _clock, bind, ports, "mantle", Changed: () => { Publish(); if (Wanted()) Schedule(0); }));
        _link = new AutoPauseLink(new AutoPauseLink.Options("mantle", ports, () => !_stopped && !_standby && _ctx.Enabled,
            () => Task.FromResult(_host.Start()), why => _host.Stop(why),
            _ => Task.CompletedTask,
            OnHost, Retire, Log, _h.Get ?? new LinkGet(AutoPauseNet.Get), _clock, OnChange: () => Publish()));

        ctx.Replay(StatusEvent, () => El(_last ?? Publish()));
        ctx.Command("status", _ => Guard("status", () =>
        {
            var st = (JsonObject)Publish().DeepClone();
            st["detail"] = SnapJson(_engine.Snapshot(Now()));
            st["test"] = _test;
            return Task.FromResult(CommandResult.Success(El(st)));
        }));
        ctx.Command("switch", c => Guard("switch", () => Switch(c.Args)));
        ctx.OnOption(Apps, (_, _) => { Log("counting: " + Mode()); Publish(); });
        ctx.OnEnabled(on =>
        {
            if (!on) _ = Safe("the auto-pause link", async () => { await SyncLink(TurnedOff).ConfigureAwait(true); await ReleaseAll(TurnedOff).ConfigureAwait(true); });
            else { _engine.Adopt(false); _ = CoexistCheck(); OnMedia(ReadMedia()); }
            Publish();
        });
        ArmCoexist();
        _ = CoexistCheck();
        ctx.Health(true);
        Log("installed" + (_test ? " (test: Spotify is never paused or played, only said)" : ""));
        Publish();
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        _timer?.Dispose(); _timer = null;
        _coexistTimer?.Dispose(); _coexistTimer = null;
        _offMedia?.Dispose(); _offMedia = null;
        if (_link == null) return;
        try
        {   // leave the link first (a successor inherits), then hand back every hold this app still holds (AUTO-081/082)
            await _link.Stop(Stopped).ConfigureAwait(true);
            await _engine.ReleaseAll(Stopped).ConfigureAwait(true);
            await _engine.Idle().ConfigureAwait(true);
        }
        catch (Exception e) { Log($"handing Spotify back threw: {e.GetType().Name}: {e.Message}"); }
        _sp.ClearSim();
        Log(stop.Rest ? "resting for a game; every hold was handed back" : "stopped; every hold was handed back");
    }

    // ---- the link's hooks (_autopause-link.js host.start / onHost / retire)
    async Task OnHost(string from, JsonObject? last)
    {
        if (last != null)
        {
            bool paused = B(last["wePaused"]) || B(last["heldByUs"]);
            long supp = B(last["repauseSuppressed"]) && last["repauseSuppressedForMs"] is JsonValue v && v.TryGetValue(out double ms) && double.IsFinite(ms) ? (long)ms : 0;
            await _engine.Inherit(paused, supp).ConfigureAwait(true);
        }
        Publish();
        if (Wanted()) Schedule(0);
    }

    async Task Retire(string why)
    {
        if (_host.SuccessorWatching()) { var r = _engine.HandOff(); Log($"handed Spotify over to the other app ({r.Action})"); }
        else { await _engine.ReleaseAll(why).ConfigureAwait(true); _host.Notify(); }
        _sp.ClearSim();
    }

    /// Taking part only while this auto-pause is on and the standalone helper is not in charge.
    async Task SyncLink(string why)
    {
        bool want = !_stopped && !_standby && _ctx.Enabled;
        try
        {
            if (want) { if (_link.Role == "off") await _link.Start().ConfigureAwait(true); else await _link.Sync().ConfigureAwait(true); }
            else await _link.Stop(why).ConfigureAwait(true);
        }
        catch (Exception e) { Log($"the auto-pause link threw: {e.GetType().Name}: {e.Message}"); }
    }

    void Forward()
    {
        bool h = _engine.Snapshot(Now()).Holds.Any(x => x.Native);
        if (h != _link.Held) _ = Safe("forwarding a hold", () => _link.SetHeld(h));
    }

    // ---- status (AUTO-011..024)
    JsonObject Publish()
    {
        var role = _link?.Role ?? "off";
        var hs = role == "client" ? _link!.HostStatus : null;
        (string Server, bool Paused)? via = hs == null ? null
            : (Str(hs["server"]) is { Length: > 0 } s ? s : "the auto-pause helper", B(hs["heldByUs"]));
        var st = StatusOf(new StatusInput(_ctx.Enabled, _standby, _standbyWords, Mode(), _meterWhy, _engine.Snapshot(Now()), via));
        st["role"] = role;
        _last = st;
        var sig = string.Join("\u0001", Str(st["state"]), Str(st["words"]), Str(st["card"]), Str(st["mode"]), role);
        if (sig != _lastSig)
        {
            _lastSig = sig;
            try { _ctx.Broadcast(StatusEvent, El(st)); } catch (Exception e) { Log($"the status push failed: {e.GetType().Name}"); }
        }
        if (_link != null && role != "off") Forward();
        _host?.Notify();
        return st;
    }

    public sealed record StatusInput(bool Enabled, bool Standby, string? StandbyWords, string? Mode, string? MeterWhy, EngineSnapshot? Snap,
        (string Server, bool Paused)? Via = null);

    /// <summary>The words of every hold (AUTO-018/019). PURE.</summary>
    public static string HoldWords(IEnumerable<HoldView> holds)
    {
        var names = new List<string>();
        bool http = false;
        foreach (var h in holds)
        {
            if (!h.Native) { http = true; continue; }
            var n = h.Kind == "browser" ? $"{(string.IsNullOrEmpty(h.Label) ? "your browser" : h.Label)} plays a video" : $"{(string.IsNullOrEmpty(h.Label) ? "another app" : h.Label)} plays";
            if (!names.Contains(n)) names.Add(n);
        }
        if (http && names.Count == 0) names.Add("a video plays in your browser");
        if (names.Count == 0) return "Paused while another app plays";
        if (names.Count == 1) return "Paused while " + names[0];
        return $"Paused while {names.Count} apps play";
    }

    /// <summary>The plain words for every state (lib/features/autopause.js statusOf). PURE.</summary>
    public static JsonObject StatusOf(StatusInput x)
    {
        var mode = x.Mode == "any" ? "any" : "media";
        JsonObject O(string state, string words, string card) => new() { ["ok"] = true, ["mode"] = mode, ["apps"] = mode, ["state"] = state, ["words"] = words, ["card"] = card };
        if (!x.Enabled) return O("off", "Off", "");
        if (x.Standby) return O("standby", x.StandbyWords ?? "", "");
        var holds = x.Snap?.Holds ?? Array.Empty<HoldView>();
        bool wePaused = x.Snap?.WePaused == true;
        var err = x.Snap?.LastError;
        if (!string.IsNullOrEmpty(err) && (holds.Count > 0 || wePaused)) return O("error", "Spotify could not be paused: " + err, "Couldn’t pause Spotify");
        if (holds.Count > 0 && x.Snap!.Suppressed)
        {
            var min = Math.Max(1, (long)Math.Round(x.Snap.SuppressedForMs / 60000.0, MidpointRounding.AwayFromZero));
            return O("backoff", $"You started Spotify again, so auto-pause leaves it alone for {min} min", "Auto-pause is leaving Spotify alone for now");
        }
        var via = x.Via is { } v ? Cap(v.Server) : "";
        if (via.Length > 0 && holds.Count > 0)
        {
            var w = HoldWords(holds);
            return O("holding", $"{w} ({via} pauses it)", x.Via!.Value.Paused ? w : "");
        }
        if (holds.Count > 0)
        {
            var w = wePaused ? HoldWords(holds) : "Holding Spotify" + HoldWords(holds)["Paused".Length..];
            return O("holding", w, wePaused ? w : "");
        }
        var what = mode == "any" ? "any other app makes a sound" : "another app plays video or sound";
        var note = string.IsNullOrEmpty(x.MeterWhy) ? "" : $". App sound levels can’t be read right now ({x.MeterWhy}), so Mantle goes by what apps report";
        var shared = via.Length > 0 ? $". {via} is also running and controls Spotify for both" : "";
        return O("idle", $"On: Spotify pauses while {what}{note}{shared}", "");
    }

    static string Cap(string? s) => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..];

    // ---- the ticking loop (AUTO-026..032)
    JsonObject? ReadMedia()
    {
        try
        {
            var m = MediaSource()?.Read();
            return m != null && !(m["ok"] is JsonValue ok && ok.TryGetValue(out bool b) && !b) ? m : null;
        }
        catch (Exception e) { Log($"reading NOW PLAYING threw: {e.GetType().Name}"); return null; }
    }

    void EnsureMediaWatch()
    {
        if (_offMedia != null || _stopped) return;
        var src = MediaSource();
        if (src == null) return;
        try { _offMedia = src.OnChange(m => _clock.Post(() => OnMedia(m))); }
        catch (Exception e) { Log($"watching NOW PLAYING threw: {e.GetType().Name}"); }
    }

    bool Wanted()
    {
        if (_stopped || _standby || !_ctx.Enabled) return false;
        var s = _engine.Snapshot(Now());
        return s.Holds.Count > 0 || s.WePaused || _sp.Seen() == "playing";
    }

    async Task TickOnce()
    {
        var media = ReadMedia();
        var mode = Mode();
        var a0 = AutoPauseAttrib.Sources(media, null, mode, _pids);
        _sp.FromMedia(media);
        if (mode == "media" && a0.Counts.Media == 0) { _warming = 0; await _engine.Tick(Now(), Array.Empty<AutoPauseSource>()).ConfigureAwait(true); return; }
        var r = await _ctx.Platform.CallAsync("autopause.peaks", new JsonObject { ["everyMs"] = _engine.Config.PollMs }, TimeSpan.FromMilliseconds(PeaksTimeoutMs)).ConfigureAwait(true);
        var res = r.Ok && r.Result.ValueKind == JsonValueKind.Object ? JsonNode.Parse(r.Result.GetRawText()) as JsonObject : null;
        bool resOk = res != null && !(res["ok"] is JsonValue ov && ov.TryGetValue(out bool okb) && !okb);
        bool warming = resOk && B(res!["warming"]);
        if (warming && ++_warming <= WarmingMax) return;
        _warming = 0;
        var peaks = resOk && !warming ? res : null;
        var why = peaks != null ? "" : warming ? "the sound meter did not start"
            : Str(res?["reason"]) is { Length: > 0 } rr ? rr : !string.IsNullOrEmpty(r.Error) ? r.Error! : "no answer";
        if (why != _meterWhy) { _meterWhy = why; Log(why.Length > 0 ? "app sound levels unreadable; judging by media status only" : "app sound levels readable again"); }
        var a = AutoPauseAttrib.Sources(media, peaks, mode, _pids);
        await _engine.Tick(Now(), a.Sources).ConfigureAwait(true);
    }

    void Schedule(long ms)
    {
        if (_timer != null || _stopped) return;
        _timer = _clock.Later(TimeSpan.FromMilliseconds(Math.Max(0, ms)), () => { _timer = null; _ = Fire(); });
    }

    async Task Fire()
    {
        if (!Wanted()) { Publish(); return; }
        var quiet = _installedAt + _startQuietMs - Now();
        if (quiet > 0) { Schedule(quiet); return; }
        if (_ticking) { Schedule(_engine.Config.PollMs); return; }
        _ticking = true;
        try { await TickOnce().ConfigureAwait(true); }
        catch (Exception e) { Log($"a tick threw: {e.GetType().Name}: {e.Message}"); }
        _ticking = false;
        Publish();
        if (Wanted()) Schedule(_engine.Config.PollMs);
    }

    void OnMedia(JsonObject? m)
    {
        if (_stopped) return;
        _sp.FromMedia(m != null && !(m["ok"] is JsonValue ok && ok.TryGetValue(out bool b) && !b) ? m : null);
        if (Wanted()) Schedule(0);
    }

    async Task ReleaseAll(string why)
    {
        try { await _engine.ReleaseAll(why).ConfigureAwait(true); }
        catch (Exception e) { Log($"handing Spotify back threw: {e.GetType().Name}: {e.Message}"); }
        _sp.ClearSim();
        Publish();
    }

    // ---- coexistence (AUTO-084..090)
    void ArmCoexist()
    {
        if (_stopped) return;
        _coexistTimer = _clock.Later(TimeSpan.FromMilliseconds(_coexistMs), () => { _coexistTimer = null; ArmCoexist(); _ = CoexistCheck(); });
    }

    async Task CoexistCheck()
    {
        if (_stopped || _checking) return;
        _checking = true;
        try
        {
            EnsureMediaWatch();
            var check = _h.Check ?? (() => AutoPauseCoexist.Check(AutoPauseNet.Reads()));
            var c = await AutoPauseCoexist.CheckSafe(check, Log).ConfigureAwait(true);
            if (_stopped) return;
            bool was = _standby;
            _standby = c.InCharge;
            _standbyWords = c.Words ?? "";
            if (_standby != was)
            {
                Log(_standby ? $"standing by: the standalone helper is in charge (task {c.TaskState}, bridge {(c.BridgePort != null ? "answering" : "silent")})" : "in charge: the standalone helper is off");
                if (_standby) { await SyncLink(HelperInCharge).ConfigureAwait(true); await ReleaseAll(HelperInCharge).ConfigureAwait(true); }
                else { _engine.Adopt(); OnMedia(ReadMedia()); }
            }
            await SyncLink(_standby ? HelperInCharge : "").ConfigureAwait(true);
            Publish();
        }
        catch (Exception e) { Log($"the coexistence check failed: {e.GetType().Name}: {e.Message}"); }
        finally { _checking = false; }
    }

    // ---- the Spotify card's switch (AUTO-005..010)
    async Task<CommandResult> Switch(JsonElement args)
    {
        JsonObject No(string reason) => new() { ["ok"] = false, ["on"] = _ctx.Enabled, ["reason"] = reason };
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("on", out var onEl) || onEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return CommandResult.Success(El(No("on must be true or false")));
        bool on = onEl.ValueKind == JsonValueKind.True;
        var fx = (_ctx as IFeatureHostAccess)?.Features;
        if (fx == null) return CommandResult.Success(El(No("this Mantle can only switch it in Settings (Win+I)")));
        FeatureAnswer? r;
        try { r = fx.SetEnabled("autopause", on); }
        catch (Exception e) { r = new FeatureAnswer(false, string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message); }
        if (r == null || !r.Ok) return CommandResult.Success(El(No(string.IsNullOrEmpty(r?.Reason) ? "the switch did not move" : r!.Reason!)));
        await _engine.Idle().ConfigureAwait(true);   // off: the hand-back has gone out before the card hears "done"
        var st = (JsonObject)Publish().DeepClone();
        st["ok"] = true; st["on"] = _ctx.Enabled;
        return CommandResult.Success(El(st));
    }

    // ---- helpers
    async ValueTask<CommandResult> Guard(string verb, Func<Task<CommandResult>> f)
    {
        try { return await f().ConfigureAwait(true); }
        catch (Exception e) { Log($"{verb} threw ({e.GetType().Name})"); return CommandResult.Fail($"{verb} failed: {(string.IsNullOrEmpty(e.Message) ? e.GetType().Name : e.Message)}"); }
    }

    async Task Safe(string what, Func<Task> f)
    {
        try { await f().ConfigureAwait(true); }
        catch (Exception e) { Log($"{what} threw: {e.GetType().Name}: {e.Message}"); }
    }

    /// The engine snapshot as JSON, built by hand (no reflection serializer). Holds carry labels: this goes to the card, never the log.
    static JsonObject SnapJson(EngineSnapshot s)
    {
        var holds = new JsonArray();
        foreach (var h in s.Holds)
            holds.Add((JsonNode)new JsonObject { ["key"] = h.Key, ["native"] = h.Native, ["label"] = h.Label, ["kind"] = h.Kind, ["ageMs"] = h.AgeMs, ["sinceMs"] = h.SinceMs, ["successor"] = h.Successor, ["live"] = h.Live });
        var tracks = new JsonArray();
        foreach (var t in s.Tracks)
            tracks.Add((JsonNode)new JsonObject { ["key"] = t.Key, ["kind"] = t.Kind, ["real"] = t.Real, ["held"] = t.Held, ["adopted"] = t.Adopted, ["peak"] = t.Peak, ["status"] = t.Status, ["runMs"] = t.RunMs });
        return new JsonObject
        {
            ["holds"] = holds, ["wePaused"] = s.WePaused, ["episodeRepauses"] = s.EpisodeRepauses, ["suppressed"] = s.Suppressed,
            ["suppressedForMs"] = s.SuppressedForMs, ["lastError"] = s.LastError, ["lastAction"] = s.LastAction, ["tracks"] = tracks,
        };
    }

    static bool B(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;
    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}
