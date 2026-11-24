using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// The engine's hands on Spotify (lib/features/autopause.js control() / spState()). Spotify is paused and resumed only through
/// its own Windows media session: the platform verb <c>media {op:"control", action, aumid, dryRun}</c> that sidecar/Media.cs
/// registers (3000 ms), never media keys.
///
/// SAFETY: <see cref="State"/> is "none" and <see cref="Pause"/>/<see cref="Play"/> are refused (with words, logged) unless this
/// app is the HOST, so a client or a standing-by Mantle can never touch Spotify. In --test every command goes out with
/// dryRun:true and is only said; the simulated state is kept so the rules behave as live. A passive sidecar refuses a real
/// control itself ("media control would change live playback"); that refusal comes back here as the reason, logged.
/// </summary>
public sealed class AutoPauseSpotify : ISpotifyControl
{
    public const int ControlTimeoutMs = 3000, PendingMs = 2000;
    public const string NotHost = "this app is not the auto-pause host, so it does not touch Spotify";

    readonly IPlatform _platform;
    readonly Func<bool> _isHost;
    readonly bool _test;
    readonly Func<long> _now;
    readonly Action<string> _log;
    string? _sim;                          // dryRun: what Spotify WOULD be after our command (the real one never moves)
    (string State, long Until)? _pending;  // live: the expected state after a command, until the media session catches up

    public AutoPauseSpotify(IPlatform platform, Func<bool> isHost, bool test, Func<long> now, Action<string> log)
    {
        _platform = platform; _isHost = isHost; _test = test; _now = now; _log = log;
    }

    public bool Found { get; private set; }
    public string Reported { get; private set; } = "none";
    public string? Aumid { get; private set; }

    /// <summary>Spotify from the NOW PLAYING read: the session whose AUMID contains "spotify" (a playing one preferred).</summary>
    public void FromMedia(JsonObject? media)
    {
        bool found = false; string state = "none"; string? aumid = null;
        if (media?["sessions"] is JsonArray list)
            foreach (var n in list)
            {
                if (n is not JsonObject s || s["aumid"] is not JsonValue av || !av.TryGetValue(out string? a) || string.IsNullOrEmpty(a)) continue;
                if (a.StartsWith(Media.MediaService.MantleAumid, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(a, "spotify", RegexOptions.IgnoreCase)) continue;
                var st = (s["status"] is JsonValue sv && sv.TryGetValue(out string? x) && !string.IsNullOrEmpty(x) ? x : "unknown").ToLowerInvariant();
                if (!found || st == "playing") { found = true; aumid = a; state = st; }
            }
        Found = found; Reported = state; Aumid = aumid;
        if (_sim != null && !found) _sim = null;
    }

    /// Spotify's state as the rules should see it, host or not (the status JSON's spotifyState).
    public string Seen()
    {
        if (_sim != null) return _sim;
        if (_pending is { } p && _now() < p.Until && Reported != p.State) return p.State;
        _pending = null;
        return Found ? Reported : "none";
    }

    /// The ENGINE's view: only the host sees Spotify at all.
    public string State() => _isHost() ? Seen() : "none";

    public Task<SpotifyResult> Pause() => Control("pause");
    public Task<SpotifyResult> Play() => Control("play");

    /// Forget the dry-run state (the host role ended, or auto-pause was switched off).
    public void ClearSim() => _sim = null;

    /// <summary>One transport command on Spotify's session (play, pause, toggle, next, previous). Host only.</summary>
    public async Task<SpotifyResult> Control(string action)
    {
        if (!_isHost()) { _log($"refused to {action} Spotify: {NotHost}"); return SpotifyResult.No(NotHost); }
        if (string.IsNullOrEmpty(Aumid)) return SpotifyResult.No("Spotify is not open");
        var args = new JsonObject { ["op"] = "control", ["action"] = action, ["aumid"] = Aumid, ["dryRun"] = _test };
        var r = await _platform.CallAsync("media", args, TimeSpan.FromMilliseconds(ControlTimeoutMs)).ConfigureAwait(true);
        var res = r.Ok && r.Result.ValueKind == JsonValueKind.Object ? r.Result : (JsonElement?)null;
        bool refused = res is not { } o || (o.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.False);
        if (refused)
        {
            var reason = res is { } o2 && o2.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String && re.GetString() is { Length: > 0 } rs ? rs
                : !string.IsNullOrEmpty(r.Error) ? r.Error! : "Windows did not answer";
            _log($"Spotify {action} did not go through: {reason}");
            return SpotifyResult.No(reason);
        }
        var want = action == "pause" ? "paused" : action == "play" ? "playing" : null;
        if (_test) { if (want != null) _sim = want; _log($"test: would {action} Spotify (nothing sent)"); }
        else if (want != null) _pending = (want, _now() + PendingMs);
        return SpotifyResult.Done;
    }
}
