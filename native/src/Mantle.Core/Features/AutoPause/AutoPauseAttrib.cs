using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.AutoPause;

/// <summary>
/// lib/features/_autopause-attrib.js, ported: WHO IS PLAYING. PURE. Ties Windows' media sessions (NOW PLAYING, by AUMID)
/// to the sound meter's audio sessions (sidecar autopause.peaks, each with its process chain) and returns the engine's
/// sources, Spotify's own session and counts. Never counted: Spotify's own tree, Windows system sounds, Mantle's own
/// processes and media sessions (MediaService.MantleAumid, every instance).
/// </summary>
public static class AutoPauseAttrib
{
    public static readonly IReadOnlySet<string> Browsers = new HashSet<string>(StringComparer.Ordinal)
    {
        "chrome.exe", "msedge.exe", "opera.exe", "opera_gx.exe", "brave.exe", "firefox.exe", "vivaldi.exe",
        "arc.exe", "chromium.exe", "iexplore.exe", "waterfox.exe", "librewolf.exe", "thorium.exe", "floorp.exe", "zen.exe",
    };
    static readonly Dictionary<string, string> AumidExe = new(StringComparer.Ordinal)
    {
        ["308046b0af4a39cb"] = "firefox.exe", ["msedge"] = "msedge.exe", ["chrome"] = "chrome.exe", ["brave"] = "brave.exe",
    };
    static readonly Regex AumidBrowser = new(@"chrome|msedge|\bedge\b|opera|firefox|308046b0af4a39cb|brave|vivaldi|chromium|quartz", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static readonly IReadOnlySet<string> NeverByName = new HashSet<string>(StringComparer.Ordinal)
    {
        "msedgewebview2.exe", "applicationframehost.exe", "runtimebroker.exe", "svchost.exe", "explorer.exe", "conhost.exe",
    };

    /// One process of an audio session's chain (leaf first).
    public sealed record ChainEntry(long Pid, string Exe, string? Pkg);
    /// One audio session of the meter.
    public sealed record AudioRow(bool Sys, double Peak, IReadOnlyList<ChainEntry> Chain);
    public sealed record SpotifySeen(bool Found, string? Aumid, string State)
    {
        public static readonly SpotifySeen None = new(false, null, "none");
    }
    public sealed record Counts(int Media, int Audio, int Counted);
    public sealed record Result(IReadOnlyList<AutoPauseSource> Sources, SpotifySeen Spotify, Counts Counts);

    static string Squeeze(string? s)
    {
        var b = new StringBuilder();
        foreach (var c in (s ?? "").ToLowerInvariant()) if (c is >= 'a' and <= 'z' or >= '0' and <= '9') b.Append(c);
        return b.ToString();
    }
    static string Stem(string? exe) => Squeeze(Regex.Replace(exe ?? "", @"\.exe$", "", RegexOptions.IgnoreCase));
    public static bool IsSpotifyAumid(string? a) => (a ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase);
    public static bool IsMantleAumid(string? a) => string.Equals(a ?? "", Media.MediaService.MantleAumid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Does a chain entry belong to this AUMID? (AUTO-041..045)</summary>
    public static Func<ChainEntry, bool> MatcherFor(string? aumid)
    {
        var a = aumid ?? "";
        int bang = a.IndexOf('!');
        if (bang > 0)
        {
            var fam = a[..bang].ToLowerInvariant();
            return e => !string.IsNullOrEmpty(e.Pkg) && e.Pkg!.ToLowerInvariant() == fam;
        }
        if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var b = Regex.Replace(a, @"^.*[\\/]", "").ToLowerInvariant();
            return e => (e.Exe ?? "").ToLowerInvariant() == b;
        }
        var sq = Squeeze(a);
        if (AumidExe.TryGetValue(sq, out var known)) return e => (e.Exe ?? "").ToLowerInvariant() == known;
        if (sq.Length < 4) return _ => false;
        return e =>
        {
            var exe = (e.Exe ?? "").ToLowerInvariant();
            if (exe.Length == 0 || NeverByName.Contains(exe)) return false;
            var st = Stem(exe);
            return st.Length >= 4 && (sq.Contains(st, StringComparison.Ordinal) || st.Contains(sq, StringComparison.Ordinal));
        };
    }

    static bool ChainHas(AudioRow r, Func<ChainEntry, bool> fn) => r.Chain.Any(fn);
    static bool IsSpotifyRow(AudioRow r) => ChainHas(r, e => (e.Exe ?? "").ToLowerInvariant() == "spotify.exe");
    static bool IsMantleRow(AudioRow r, IReadOnlySet<long> mantle) => ChainHas(r, e => mantle.Contains(e.Pid));
    static bool Browserish(string aumid, IEnumerable<AudioRow> rows) =>
        AumidBrowser.IsMatch(aumid) || rows.Any(r => ChainHas(r, e => Browsers.Contains((e.Exe ?? "").ToLowerInvariant())));

    /// "zoom.exe" -> "Zoom"; empty -> "An app" (AUTO-048).
    public static string AppLabel(string? exe)
    {
        var s = Regex.Replace(exe ?? "", @"\.exe$", "", RegexOptions.IgnoreCase);
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : "An app";
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && b;
    // Any number: a value built in code holds an int or a long (TryGetValue<double> refuses those), a parsed one a JsonElement.
    static double? Num(JsonNode? n) => Mantle.Core.Look.LookSyncCore.TryNum(n, out double d) && double.IsFinite(d) ? d : null;

    /// <summary>The meter's rows, or null when the reading is unusable (null, ok:false, no sessions array).</summary>
    public static IReadOnlyList<AudioRow>? Rows(JsonObject? peaks)
    {
        if (peaks == null || peaks["ok"] is JsonValue okv && okv.TryGetValue(out bool ok) && !ok || peaks["sessions"] is not JsonArray list) return null;
        var rows = new List<AudioRow>();
        foreach (var n in list)
        {
            if (n is not JsonObject o) continue;
            var chain = new List<ChainEntry>();
            if (o["chain"] is JsonArray ch)
                foreach (var c in ch)
                    if (c is JsonObject e) chain.Add(new ChainEntry((long)(Num(e["pid"]) ?? -1), Str(e["exe"]) ?? "", Str(e["pkg"])));
            rows.Add(new AudioRow(Bool(o["sys"]), Num(o["peak"]) ?? 0, chain));
        }
        return rows;
    }

    /// <summary>The engine's sources (AUTO-040..052). media = NOW PLAYING's payload ({sessions:[...]}) or null; peaks = the
    /// meter's answer or null (unreadable: every media source's peak is -1, never a fake 0).</summary>
    public static Result Sources(JsonObject? media, JsonObject? peaks, string? mode, IReadOnlySet<long> mantlePids)
    {
        bool any = mode == "any";
        var rowsAll = Rows(peaks);
        var rows = (rowsAll ?? Array.Empty<AudioRow>()).Where(r => !r.Sys && !IsSpotifyRow(r) && !IsMantleRow(r, mantlePids)).ToList();
        var used = new HashSet<AudioRow>(ReferenceEqualityComparer.Instance);
        var output = new List<AutoPauseSource>();
        var spotify = SpotifySeen.None;
        var byAumid = new List<(string Aumid, JsonObject S, bool Playing)>();   // insertion order, as a JS Map
        if (media?["sessions"] is JsonArray sessions)
            foreach (var n in sessions)
            {
                if (n is not JsonObject s) continue;
                var aumid = Str(s["aumid"]);
                if (string.IsNullOrEmpty(aumid) || IsMantleAumid(aumid)) continue;
                var status = Str(s["status"]);
                if (IsSpotifyAumid(aumid))
                {
                    var state = (string.IsNullOrEmpty(status) ? "unknown" : status).ToLowerInvariant();
                    if (!spotify.Found || state == "playing") spotify = new SpotifySeen(true, aumid, state);
                    continue;
                }
                bool playing = status == "playing" || Bool(s["playing"]);
                int at = byAumid.FindIndex(g => g.Aumid == aumid);
                if (at < 0) byAumid.Add((aumid, s, playing));
                else if (!byAumid[at].Playing && playing) byAumid[at] = (aumid, s, true);
            }
        foreach (var (aumid, s, playing) in byAumid)
        {
            var match = MatcherFor(aumid);
            var mine = rows.Where(r => ChainHas(r, match)).ToList();
            if (mine.Count == 0 && rowsAll != null && rowsAll.Any(r => !r.Sys && IsMantleRow(r, mantlePids) && ChainHas(r, match))) continue;
            double peak = -1;
            if (rowsAll != null && mine.Count > 0) { peak = 0; foreach (var r in mine) { used.Add(r); if (r.Peak > peak) peak = r.Peak; } }
            var tl = s["timeline"] as JsonObject;
            if (tl != null && tl["available"] is JsonValue av && av.TryGetValue(out bool avail) && !avail) tl = null;
            var kind = Browserish(aumid, mine) ? "browser" : "media";
            var label = (s["app"] as JsonObject)?["name"] is JsonNode nn && Str(nn) is { Length: > 0 } nm ? nm : "An app";
            var status = playing ? "playing" : (Str(s["status"]) is { Length: > 0 } st ? st : "unknown");
            output.Add(new AutoPauseSource("smtc:" + aumid, kind, label, status, tl != null ? Num(tl["positionMs"]) : null, peak));
        }
        if (any && rowsAll != null)
        {
            var apps = new List<(string Exe, double Peak)>();
            foreach (var r in rows)
            {
                if (used.Contains(r) || r.Chain.Count == 0) continue;
                var exe = (r.Chain[0].Exe ?? "").ToLowerInvariant();
                if (exe.Length == 0) continue;
                int at = apps.FindIndex(a => a.Exe == exe);
                if (at < 0) apps.Add((exe, Math.Max(0, r.Peak)));
                else if (r.Peak > apps[at].Peak) apps[at] = (exe, r.Peak);
            }
            foreach (var (exe, pk) in apps) output.Add(new AutoPauseSource("app:" + exe, "app", AppLabel(exe), "unknown", null, pk));
        }
        return new Result(output, spotify, new Counts(byAumid.Count, rowsAll?.Count ?? 0, rows.Count));
    }
}
