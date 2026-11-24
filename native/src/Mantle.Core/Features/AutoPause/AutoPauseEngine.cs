using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features.AutoPause;

/// <summary>The rules' constants (lib/features/_autopause-core.js DEFAULTS, the standalone bridge's MEASURED numbers).</summary>
public sealed record AutoPauseConfig
{
    public int PollMs { get; init; } = 250;
    public long LivenessWindowMs { get; init; } = 3000;
    public long ResumeDelayMs { get; init; } = 4000;
    public double PeakThreshold { get; init; } = 0.0008;
    public long HoldMinPlayingMs { get; init; } = 0;
    public long HoldMinPosOnlyMs { get; init; } = 8000;
    public long MinHoldMs { get; init; } = 6000;
    public bool RepauseWhileHeld { get; init; } = true;
    public int RepauseGiveUpAfter { get; init; } = 2;
    public long RepauseGiveUpCooldownMs { get; init; } = 300000;
    public long RepauseCheckEveryMs { get; init; } = 1000;
    /// No re-pause within this of our own resume (the bridge's 1500).
    public long ResumeGraceMs { get; init; } = 1500;
    /// The bridge's browserHoldTtlMs: an HTTP hold not refreshed ends after this.
    public long HttpHoldTtlMs { get; init; } = 180000;
    /// 'app' kind only; NOT measured (the bridge never watched such apps).
    public long AnyMinRunMs { get; init; } = 2000;
}

/// <summary>What Spotify did: ok, or the reason in plain words.</summary>
public readonly record struct SpotifyResult(bool Ok, string? Reason)
{
    public static SpotifyResult Done => new(true, null);
    public static SpotifyResult No(string reason) => new(false, reason);
}

/// <summary>The engine's hands on Spotify. State() is "playing" | "paused" | "stopped" | "none". Only the HOST wires a real
/// one; a client's engine gets one whose State() is always "none", so it can never pause or resume.</summary>
public interface ISpotifyControl
{
    string State();
    Task<SpotifyResult> Pause();
    Task<SpotifyResult> Play();
}

/// <summary>One source's reading this tick: <c>Kind</c> media | browser | app; <c>Peak</c> -1 = unknown (never a fake 0).</summary>
public sealed record AutoPauseSource(string Key, string Kind, string? Label, string? Status, double? PositionMs, double Peak);

/// <summary>What a hold call did: paused, refreshed, no-spotify, already-paused, error, skipped, resumed, nothing,
/// inherited, handed-off (the Electron action words, so /pause and /resume answer the same).</summary>
public sealed record HoldOutcome(string Action, string? Reason = null)
{
    /// handOff only: whether we had Spotify paused when we handed it over.
    public bool Paused { get; init; }
}

public sealed record HoldView(string Key, bool Native, string Label, string Kind, long AgeMs, long SinceMs, bool Successor, bool Live);
public sealed record TrackView(string Key, string Kind, bool Real, bool Held, bool Adopted, double Peak, string Status, long RunMs);
public sealed record EngineSnapshot(IReadOnlyList<HoldView> Holds, bool WePaused, int EpisodeRepauses, bool Suppressed, long SuppressedForMs,
    string LastError, string LastAction, IReadOnlyList<TrackView> Tracks);

/// <summary>
/// lib/features/_autopause-core.js, ported: the RULES, pure and clock-injected. The owner feeds it what Windows says each
/// tick; this decides when Spotify is held paused and when it is handed back. Every Spotify action goes through ONE serial
/// queue, so a release can never overtake the pause before it. Only resume what WE paused. Every hold has an expiry.
/// Threading: call it from one thread (the host thread); it is not locked.
/// </summary>
public sealed class AutoPauseEngine
{
    sealed class Track
    {
        public string Key = "", Kind = "media", Label = "", Status = "unknown";
        public bool HasPos, PosRecent, PeakRecent, RealPlaying, RunHadAudio, Held, Adopted;
        public double LastPos = -1, LastPeak = -1;
        public int PosChanges;
        public long LastPosChangeMs = -1, PrevPosChangeMs = -1, LastPeakOkMs = -1, LastRealMs = -1, RunStart = -1, FirstPeakMs = -1, RunMs = -1, HoldStartMs = -1, Seen = -1;
    }

    sealed class Hold
    {
        public long At, Since, Ttl;
        public bool Native, Successor;
        public string Label = "", Kind = "";
    }

    /// The key releaseAll and inherit release through (never a real hold's key).
    const string AllKey = "\u0000all", InheritKey = "\u0000inherit";

    readonly AutoPauseConfig _cfg;
    readonly ISpotifyControl _sp;
    readonly Action<string> _log;
    readonly Func<long> _now;
    // Insertion order is kept (a JS Map's): the status signature lists holds in the order they came.
    readonly List<string> _trackOrder = new(), _holdOrder = new();
    readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);
    readonly Dictionary<string, Hold> _holds = new(StringComparer.Ordinal);
    bool _wePaused, _backoffLogged, _adoptPending;
    int _episodeRepauses;
    long _suppressedUntil = -1, _lastResumeMs = -100000, _lastRepauseCheck = -1, _adoptUntil = -1;
    string _lastError = "", _lastAction = "";
    HashSet<string> _adoptKeys = new(StringComparer.Ordinal);
    Task _busy = Task.CompletedTask;

    public AutoPauseEngine(ISpotifyControl spotify, Func<long> now, Action<string>? log = null, AutoPauseConfig? config = null)
    {
        _sp = spotify ?? throw new ArgumentNullException(nameof(spotify));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _log = log ?? (_ => { });
        _cfg = config ?? new AutoPauseConfig();
    }

    public AutoPauseConfig Config => _cfg;

    /// Resolves when every Spotify action queued so far has finished (the switch waits on it before answering).
    public Task Idle() => Settled(_busy);

    static async Task Settled(Task t) { try { await t.ConfigureAwait(true); } catch { /* a failed action is in lastError and the log */ } }

    Task<T> Serial<T>(Func<Task<T>> fn)
    {
        var p = RunAfter(_busy, fn);
        _busy = p;   // RunAfter swallows the previous one's failure, so one failure never stops the queue
        return p;
    }

    static async Task<T> RunAfter<T>(Task prev, Func<Task<T>> fn)
    {
        try { await prev.ConfigureAwait(true); } catch { /* the queue never stops on one failure */ }
        return await fn().ConfigureAwait(true);
    }

    void SetHold(string key, Hold h) { if (!_holds.ContainsKey(key)) _holdOrder.Add(key); _holds[key] = h; }
    void DropHold(string key) { if (_holds.Remove(key)) _holdOrder.Remove(key); }
    void ClearHolds() { _holds.Clear(); _holdOrder.Clear(); }

    /// <summary>Take or refresh hold <paramref name="key"/>. The first hold pauses a PLAYING Spotify; a refresh sends nothing.</summary>
    public Task<HoldOutcome> AddHold(string key, bool native, long now, string? label = null, string? kind = null, long ttl = 0, bool successor = false)
    {
        bool refresh = _holds.TryGetValue(key, out var rec);
        rec ??= new Hold { Since = now };
        rec.At = now; rec.Native = native;
        if (!string.IsNullOrEmpty(label)) rec.Label = label!;
        if (!string.IsNullOrEmpty(kind)) rec.Kind = kind!;
        rec.Ttl = ttl > 0 ? ttl : rec.Ttl;
        rec.Successor = successor;
        bool wasEmpty = _holds.Count == 0;
        SetHold(key, rec);
        if (wasEmpty) { _episodeRepauses = 0; _backoffLogged = false; }
        if (refresh) return Task.FromResult(new HoldOutcome("refreshed"));
        return Serial(async () =>
        {
            var state = _sp.State();
            if (state == "none") return new HoldOutcome("no-spotify");
            if (state != "playing") return new HoldOutcome("already-paused");
            var r = await _sp.Pause().ConfigureAwait(true);
            if (r.Ok) { _wePaused = true; _lastError = ""; _lastAction = "paused"; _log($"hold taken ({_holds.Count} held): Spotify paused"); return new HoldOutcome("paused"); }
            _lastError = string.IsNullOrEmpty(r.Reason) ? "Spotify did not answer" : r.Reason!;
            _log($"hold taken ({_holds.Count} held) but the pause failed");
            return new HoldOutcome("error", _lastError);
        });
    }

    /// <summary>Release hold <paramref name="key"/>. Spotify resumes only when the LAST hold ends, only if WE paused it, and not
    /// if the user already started it again (user-resumed).</summary>
    public Task<HoldOutcome> RemoveHold(string key, string? why)
    {
        DropHold(key);
        if (_holds.Count == 0) { _episodeRepauses = 0; _backoffLogged = false; }
        return Serial(async () =>
        {
            if (!_wePaused) return new HoldOutcome("skipped", "not-held");
            if (_holds.Count > 0) return new HoldOutcome("skipped", "other-holds");
            var state = _sp.State();
            if (state == "none") { _wePaused = false; return new HoldOutcome("no-spotify"); }
            if (state == "playing") { _wePaused = false; return new HoldOutcome("skipped", "user-resumed"); }
            var r = await _sp.Play().ConfigureAwait(true);
            if (r.Ok)
            {
                _wePaused = false; _lastResumeMs = _now(); _lastError = ""; _lastAction = "resumed";
                _log($"hold released ({(string.IsNullOrEmpty(why) ? "done" : why)}): Spotify resumed");
                return new HoldOutcome("resumed");
            }
            _lastError = string.IsNullOrEmpty(r.Reason) ? "Spotify did not answer" : r.Reason!;
            _log($"hold released ({(string.IsNullOrEmpty(why) ? "done" : why)}) but the resume failed");
            return new HoldOutcome("error", _lastError);
        });
    }

    /// updateTrack (Update-TrackFromSession): one source's verdict for this tick.
    void UpdateTrack(Track t, AutoPauseSource s, long now)
    {
        t.Seen = now;
        t.Status = (s.Status ?? "unknown").ToLowerInvariant();
        double pos = s.PositionMs is double p && double.IsFinite(p) && p >= 0 ? p : -1;
        if (pos >= 0)
        {
            if (t.HasPos)
            {
                if (Math.Abs(pos - t.LastPos) > 50)
                {
                    t.PosChanges += 1;
                    t.PrevPosChangeMs = t.LastPosChangeMs;
                    t.LastPosChangeMs = now;
                }
            }
            else t.HasPos = true;
            t.LastPos = pos;
        }
        double peak = double.IsFinite(s.Peak) ? s.Peak : -1;
        t.LastPeak = peak;
        if (peak >= 0 && peak > _cfg.PeakThreshold) t.LastPeakOkMs = now;
        var win = _cfg.LivenessWindowMs;
        t.PosRecent = t.PosChanges >= 2 && t.LastPosChangeMs >= 0 && now - t.LastPosChangeMs <= win &&
            t.PrevPosChangeMs >= 0 && t.LastPosChangeMs - t.PrevPosChangeMs <= win;
        t.PeakRecent = t.LastPeakOkMs >= 0 && now - t.LastPeakOkMs <= win;
        bool playing = t.Status == "playing";
        if (t.Kind == "browser") t.RealPlaying = playing && (peak >= 0 || t.LastPeakOkMs >= 0 ? t.PeakRecent : true);
        else if (t.Kind == "app") t.RealPlaying = t.PeakRecent;
        else t.RealPlaying = t.PeakRecent || (playing && t.PosRecent);
    }

    HashSet<string> Verdicts(long now, IEnumerable<AutoPauseSource>? sources)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in sources ?? Array.Empty<AutoPauseSource>())
        {
            if (s == null || string.IsNullOrEmpty(s.Key)) continue;
            seen.Add(s.Key);
            if (!_tracks.TryGetValue(s.Key, out var t)) { t = new Track { Key = s.Key }; _tracks[s.Key] = t; _trackOrder.Add(s.Key); }
            t.Kind = s.Kind == "browser" || s.Kind == "app" ? s.Kind : "media";
            if (!string.IsNullOrEmpty(s.Label)) t.Label = s.Label!;
            UpdateTrack(t, s, now);
        }
        return seen;
    }

    /// <summary>One watcher tick (Invoke-WatcherTick + Invoke-HoldTtlCheck + Invoke-RepauseCheck).</summary>
    public async Task Tick(long now, IEnumerable<AutoPauseSource>? sources)
    {
        var seen = Verdicts(now, sources);
        var jobs = new List<Task>();
        // ADOPTION (see Adopt()): the sources on the first tick after it may take no hold for this run.
        if (_adoptPending) { _adoptPending = false; _adoptUntil = now + _cfg.LivenessWindowMs; _adoptKeys = new HashSet<string>(seen, StringComparer.Ordinal); }
        else if (_adoptKeys.Count > 0 && now > _adoptUntil) _adoptKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in _trackOrder.ToList())
        {
            var t = _tracks[key];
            var hk = "native:" + key;
            if (!seen.Contains(key))
            {   // the session is gone: its hold goes with it
                if (t.Held) jobs.Add(RemoveHold(hk, "its source closed"));
                _tracks.Remove(key); _trackOrder.Remove(key);
                _adoptKeys.Remove(key);     // its next session is news
                continue;
            }
            if (t.RealPlaying)
            {
                t.LastRealMs = now;
                if (t.RunStart < 0) { t.RunStart = now; t.RunHadAudio = false; t.FirstPeakMs = -1; }
                if (t.PeakRecent) t.RunHadAudio = true;
                if (t.LastPeakOkMs == now && t.FirstPeakMs < 0) t.FirstPeakMs = now;
                if (!t.Held && _adoptKeys.Contains(key) && now <= _adoptUntil) t.Adopted = true;
            }
            else if (t.RunStart >= 0 && (t.LastRealMs < 0 || now - t.LastRealMs > _cfg.ResumeDelayMs))
            {
                t.RunStart = -1; t.RunHadAudio = false; t.FirstPeakMs = -1; t.Adopted = false;
            }
            t.RunMs = t.RunStart >= 0 ? now - t.RunStart : -1;
            if (t.RunStart >= 0)
            {
                var gate = t.RunHadAudio ? _cfg.HoldMinPlayingMs : _cfg.HoldMinPosOnlyMs;
                // 'app': AUDIBLE time, not the run (which carries 3 s of liveness after a 500 ms ping).
                bool due = t.Kind == "app" ? t.FirstPeakMs >= 0 && t.LastPeakOkMs - t.FirstPeakMs >= _cfg.AnyMinRunMs : t.RunMs >= gate;
                if (!t.Held && due && !t.Adopted)
                {
                    t.Held = true; t.HoldStartMs = now;
                    jobs.Add(AddHold(hk, true, now, t.Label, t.Kind));
                }
                else if (t.Held && _holds.TryGetValue(hk, out var h)) h.At = now;
            }
            else if (t.Held)
            {
                var heldFor = t.HoldStartMs >= 0 ? now - t.HoldStartMs : -1;
                if (!(_cfg.MinHoldMs > 0 && heldFor >= 0 && heldFor < _cfg.MinHoldMs))
                {
                    t.Held = false; t.HoldStartMs = -1;
                    jobs.Add(RemoveHold(hk, "its sound stopped"));
                }
            }
        }
        // HTTP holds (a client app or a browser over the bridge-compatible API) expire without a refresh.
        if (_cfg.HttpHoldTtlMs > 0)
        {
            foreach (var key in _holdOrder.ToList())
            {
                if (!_holds.TryGetValue(key, out var rec)) continue;
                if (!rec.Native && now - rec.At >= (rec.Ttl > 0 ? rec.Ttl : _cfg.HttpHoldTtlMs)) jobs.Add(RemoveHold(key, "its browser stopped refreshing it"));
            }
        }
        await Task.WhenAll(jobs).ConfigureAwait(true);
        await RepauseCheck(now).ConfigureAwait(true);
    }

    async Task RepauseCheck(long now)
    {
        if (!_cfg.RepauseWhileHeld || _holds.Count == 0) return;
        if (_lastRepauseCheck >= 0 && now - _lastRepauseCheck < _cfg.RepauseCheckEveryMs) return;
        _lastRepauseCheck = now;
        if (now - _lastResumeMs < _cfg.ResumeGraceMs) return;
        if (_suppressedUntil >= 0 && now < _suppressedUntil) return;
        if (_episodeRepauses >= _cfg.RepauseGiveUpAfter)
        {
            _suppressedUntil = now + _cfg.RepauseGiveUpCooldownMs;
            if (!_backoffLogged) { _backoffLogged = true; _log($"Spotify was started {_episodeRepauses} times during a hold: leaving Spotify alone for {(int)Math.Round(_cfg.RepauseGiveUpCooldownMs / 60000.0, MidpointRounding.AwayFromZero)} min"); }
            return;
        }
        await Serial(async () =>
        {
            if (_holds.Count == 0 || _sp.State() != "playing") return 0;
            var r = await _sp.Pause().ConfigureAwait(true);
            if (r.Ok) { _wePaused = true; _episodeRepauses += 1; _lastAction = "repaused"; _log($"Spotify started during a hold: paused again ({_episodeRepauses}/{_cfg.RepauseGiveUpAfter})"); }
            else { _lastError = string.IsNullOrEmpty(r.Reason) ? "Spotify did not answer" : r.Reason!; _log("Spotify started during a hold and the re-pause failed"); }
            return 0;
        }).ConfigureAwait(true);
    }

    /// <summary>Hand everything back (feature off, rest, quit, the standalone helper came back): resume what we paused.</summary>
    public Task<HoldOutcome> ReleaseAll(string why)
    {
        foreach (var t in _tracks.Values) { t.Held = false; t.HoldStartMs = -1; t.RunStart = -1; t.Adopted = false; }
        bool had = _holds.Count > 0;
        ClearHolds();
        _episodeRepauses = 0; _suppressedUntil = -1;
        if (!had && !_wePaused) return Task.FromResult(new HoldOutcome("nothing"));
        return RemoveHold(AllKey, why);
    }

    public EngineSnapshot Snapshot(long now)
    {
        var holds = _holdOrder.Select(k =>
        {
            var r = _holds[k];
            return new HoldView(k, r.Native, r.Label, r.Kind, now - r.At, now - r.Since, r.Successor, r.Native || now - r.At < (r.Ttl > 0 ? r.Ttl : _cfg.HttpHoldTtlMs));
        }).ToList();
        var tracks = _trackOrder.Select(k => { var t = _tracks[k]; return new TrackView(t.Key, t.Kind, t.RealPlaying, t.Held, t.Adopted, t.LastPeak, t.Status, t.RunMs); }).ToList();
        return new EngineSnapshot(holds, _wePaused, _episodeRepauses, _suppressedUntil >= 0 && now < _suppressedUntil,
            _suppressedUntil >= 0 ? Math.Max(0, _suppressedUntil - now) : 0, _lastError, _lastAction, tracks);
    }

    /// <summary>ADOPT: whatever ALREADY plays on the first tick after this (or turns audible within 3 s) is left alone until it
    /// stops. Called when auto-pause takes charge on its own (Mantle starting, the helper switched off), never when someone
    /// turns it on. <c>Adopt(false)</c> cancels one not yet used (someone turned it on: they mean now).</summary>
    public void Adopt(bool on = true)
    {
        _adoptPending = on;
        if (!on) _adoptKeys = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>INHERIT (protocol 2): this app has just become the decision maker and the previous host had Spotify paused.
    /// Only a Spotify that IS paused (or stopped) now is taken; with nothing held it is handed back at once (anti-strand).
    /// <paramref name="suppressedForMs"/> carries the old host's "leave Spotify alone" back-off.</summary>
    public async Task<HoldOutcome> Inherit(bool paused, long suppressedForMs)
    {
        var now = _now();
        if (suppressedForMs > 0) _suppressedUntil = Math.Max(_suppressedUntil, now + suppressedForMs);
        if (!paused) return new HoldOutcome("nothing");
        var r = await Serial(() =>
        {
            var state = _sp.State();
            if (_wePaused || (state != "paused" && state != "stopped")) return Task.FromResult(new HoldOutcome("nothing"));
            _wePaused = true; _lastAction = "inherited";
            _log($"took over a Spotify pause from the previous host ({_holds.Count} held)");
            return Task.FromResult(new HoldOutcome("inherited"));
        }).ConfigureAwait(true);
        // Nobody holds it any more: hand it back now (the anti-strand rule).
        if (r.Action == "inherited" && _holds.Count == 0) return await RemoveHold(InheritKey, "the previous host had paused it and nothing plays").ConfigureAwait(true);
        return r;
    }

    /// <summary>HAND OFF: stop deciding WITHOUT touching Spotify (another app takes over and inherits the pause).</summary>
    public HoldOutcome HandOff()
    {
        foreach (var t in _tracks.Values) { t.Held = false; t.HoldStartMs = -1; t.RunStart = -1; t.Adopted = false; }
        ClearHolds();
        var was = _wePaused;
        _wePaused = false; _episodeRepauses = 0; _suppressedUntil = -1;
        return new HoldOutcome(was ? "handed-off" : "nothing") { Paused = was };
    }
}
