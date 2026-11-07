using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Features.Toasts;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Osd;

// Port of the key half of lib/features/osd.js: a volume key press steps the volume through the sidecar
// (osd.volume, a DRY run in --test so the user's volume is never touched), in press order, at most
// MAX_WAITING behind a slow sidecar; the pop-up shows at once with the last level and moves to the new one
// when the step lands; an open Quick Settings panel means no pop-up; the start-up probe decides health
// (unhealthy = the feature host hands the keys back to Windows, whose own flyout then works); the volume
// watch is held back mid-burst so the number never jumps backwards.
//
// The key swallowing itself (the takeover of Windows' volume flyout) is the feature host's hotkey call, sent
// ONLY in a --live run (FeatureHost.ApplyAsync: never in --test, never before ApplyLiveAsync). It lives in the
// sidecar's hook, so the sidecar dying gives the keys back to Windows by itself: nothing to restore.

public sealed record VolumeState(bool Ok, double? Level, bool Muted, string? Reason);

/// <summary>What the key half asks of the pop-up (the OSD surface through the host, or a fake in tests).</summary>
public interface IOsdView
{
    bool Shown { get; }
    void Show(string kind, VolumeState state, string via);
    void PushLevel(VolumeState state);
}

public sealed class VolumeKeys
{
    public const int MaxWaiting = 6;
    public const int ReprobeMs = 30000;
    public const int SettleMs = 300;
    public const string Note = "volume keys show Mantle’s pop-up (Mantle takes the key, so Windows shows none)";

    static readonly Regex NoDevice = new("no default playback device", RegexOptions.IgnoreCase);
    static readonly Regex Unknown = new("unknown command", RegexOptions.IgnoreCase);

    readonly IPlatform _p;
    readonly IOsdView _view;
    readonly Action<bool, string?> _health;
    readonly Action<string> _log;
    readonly Func<bool> _enabled;
    readonly bool _test;
    readonly IToastClock _clock;
    Task _chain = Task.CompletedTask;
    VolumeState? _vol, _dryVol, _lateWatch;
    double _settleUntil;
    IDisposable? _reprobe, _settleT;
    string _lastSkip = "";
    bool _stopped;

    public VolumeKeys(IPlatform platform, IOsdView view, Action<bool, string?> health, Func<bool> enabled, bool test, IToastClock clock, Action<string>? log = null)
    {
        _p = platform; _view = view; _health = health; _enabled = enabled; _test = test; _clock = clock; _log = log ?? (_ => { });
    }

    public int Waiting { get; private set; }
    public int Presses { get; private set; }
    public bool Healthy { get; private set; } = true;
    public VolumeState? Volume => _vol;

    static string Op(string key) => key switch { "VolumeUp" => "up", "VolumeDown" => "down", "VolumeMute" => "mute", _ => "" };

    void Skip(string why)
    {
        if (why != _lastSkip) _log($"no pop-up: {why}");   // once per reason, not once per key repeat
        _lastSkip = why;
    }

    VolumeState Current() =>
        (_test && _dryVol != null ? _dryVol : _vol) ?? new VolumeState(false, null, false, "the volume has not been read yet");

    void SetHealth(bool ok, string? reason = null)
    {
        Healthy = ok;
        _health(ok, ok ? Note : reason);
        _reprobe?.Dispose(); _reprobe = null;
        if (!ok && !_stopped) _reprobe = _clock.Later(ReprobeMs, () => { _ = ProbeAsync(); });
    }

    static double? Level(JsonElement x) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null;
    static bool Muted(JsonElement x) => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("muted", out var m) && m.ValueKind == JsonValueKind.True;

    async Task<VolumeState> DoStep(string key)
    {
        var args = new JsonObject { ["op"] = Op(key) };
        if (_test)
        {
            args["dry"] = true;
            if (_dryVol != null) args["from"] = new JsonObject { ["level"] = _dryVol.Level, ["muted"] = _dryVol.Muted };
        }
        var r = await _p.CallAsync("osd.volume", args, TimeSpan.FromMilliseconds(1500));
        if (!r.Ok)
        {
            var e = string.IsNullOrEmpty(r.Error) ? "no reason given" : r.Error!;
            // No playback device: Windows' key would do nothing either. Say so on the pop-up; keep the key.
            if (NoDevice.IsMatch(e)) return new VolumeState(false, null, false, $"the volume key did nothing: {e}");
            SetHealth(false, $"Mantle could not step the volume ({e}), so the volume keys went back to Windows");
            return new VolumeState(false, _vol?.Level, _vol?.Muted ?? false, $"the volume key failed: {e}. The keys are Windows’ again");
        }
        var x = r.Result;
        var state = new VolumeState(true, Level(x), Muted(x), null);
        _settleUntil = _clock.NowMs + SettleMs;
        if (_test)
        {
            _dryVol = state;
            var f = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("from", out var fr) ? fr : default;
            var fl = Level(f);
            _log($"dry run (--test): {key} would set the volume {(fl is double d ? Words.Num(d) : "undefined")}{(Muted(f) ? " (muted)" : "")} -> {(state.Level is double n ? Words.Num(n) : "undefined")}{(state.Muted ? " (muted)" : "")}; your volume was not touched");
        }
        else _vol = state;
        return state;
    }

    /// One press, queued behind the ones before it (each step reads what the previous wrote).
    Task<VolumeState?> Step(string key)
    {
        if (Waiting >= MaxWaiting)
        {
            Skip($"{key}: {Waiting} presses are already waiting on the sidecar, so this one was dropped");
            return Task.FromResult<VolumeState?>(null);
        }
        Waiting++;
        var prev = _chain;
        var t = Run(prev, key);
        _chain = t;
        return t;
    }

    async Task<VolumeState?> Run(Task prev, string key)
    {
        try { await prev; } catch { /* the previous press's failure is its own */ }
        try { return await DoStep(key); }
        finally { Waiting--; }
    }

    /// A volume chord from the feature host (VolumeUp / VolumeDown / VolumeMute).
    public async Task OnKeyAsync(string key)
    {
        if (!_enabled() || _stopped) return;
        Presses++;
        var done = Step(key);
        // The pop-up shows at once with the last level, and moves to the new one when the step lands.
        var q = await _p.CallAsync("osd.qs", new JsonObject(), TimeSpan.FromMilliseconds(150));
        bool qsOpen = q.Ok && q.Result.ValueKind == JsonValueKind.Object && q.Result.TryGetProperty("qsOpen", out var o) && o.ValueKind == JsonValueKind.True;
        if (qsOpen) Skip($"{key}: the quick-settings panel is open and shows the level");
        else { _lastSkip = ""; _view.Show("volume", Current(), key); }
        var s = await done;
        if (s == null || _stopped) return;
        if (!s.Ok || !qsOpen)
        {
            if (!_view.Shown) _view.Show("volume", s, key);
            else _view.PushLevel(s);
        }
    }

    /// Can Mantle step the volume at all: a dry step changes nothing, and proves the command.
    public async Task ProbeAsync()
    {
        if (_stopped) return;
        var r = await _p.CallAsync("osd.volume", new JsonObject { ["op"] = "up", ["dry"] = true }, TimeSpan.FromMilliseconds(3000));
        if (_stopped) return;
        if (r.Ok)
        {
            SetHealth(true);
            var ms = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ms", out var m) ? m.ToString() : "undefined";
            _log($"volume keys: Mantle's while on (a dry step answered in {ms} ms)");
            return;
        }
        var e = string.IsNullOrEmpty(r.Error) ? "no reason given" : r.Error!;
        if (NoDevice.IsMatch(e)) { SetHealth(true); _log($"volume keys: Mantle's, but there is no playback device right now ({e})"); return; }
        if (Unknown.IsMatch(e)) SetHealth(false, "this sidecar exe predates osd.volume, so Mantle cannot step the volume and the volume keys stay Windows’ (run npm run build:sidecar)");
        else SetHealth(false, $"Mantle could not step the volume ({e}), so the volume keys stay Windows’");
        _log($"volume keys stay Windows': {e}");
    }

    /// The sidecar's volume watch (ev 'volume'): drawn at once outside a burst, held back during one.
    public void OnWatch(JsonElement ev)
    {
        if (_stopped) return;
        bool failed = ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;
        string? reason = ev.ValueKind == JsonValueKind.Object && ev.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String ? rr.GetString() : null;
        var s = failed ? new VolumeState(false, null, false, reason ?? "the volume watch failed without a reason") : new VolumeState(true, Level(ev), Muted(ev), null);
        if (Waiting > 0 || _clock.NowMs < _settleUntil) { _lateWatch = s; SettleLater(); return; }
        DrawWatch(s);
    }

    void DrawWatch(VolumeState s)
    {
        _vol = s;
        if (!_stopped && _view.Shown && !(_test && _dryVol != null)) _view.PushLevel(s);
    }

    void SettleLater()
    {
        _settleT?.Dispose();
        _settleT = _clock.Later((int)Math.Max(20, _settleUntil - _clock.NowMs), () =>
        {
            _settleT = null;
            if (_stopped) return;
            if (Waiting > 0 || _clock.NowMs < _settleUntil) { SettleLater(); return; }
            var s = _lateWatch; _lateWatch = null;
            if (s != null && !(_vol != null && s.Ok == _vol.Ok && s.Level == _vol.Level && s.Muted == _vol.Muted)) DrawWatch(s);
        });
    }

    /// The first reading when the sidecar is ready (only if nothing was read yet).
    public void Seed(VolumeState s) { if (_vol == null) _vol = s; }

    /// A late key, reading or timer afterwards does nothing.
    public void Stop() { _stopped = true; _reprobe?.Dispose(); _settleT?.Dispose(); _reprobe = _settleT = null; }
}
