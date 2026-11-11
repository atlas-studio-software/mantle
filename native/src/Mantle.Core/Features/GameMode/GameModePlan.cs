using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.GameMode;

/// <summary>The window in front, as the helper's gamemode.scan reports it (fg + its verdict).</summary>
public sealed record GameFg(int Pid, string? Exe = null, string? Title = null, bool Self = false, bool Fullscreen = false,
    bool GameStore = false, int? Quns = null);

/// <summary>One row of the helper's process list (gamemode.scan {procs, detail}).</summary>
public sealed record GameProc(int Pid, string? File, int Ppid = 0, int? Session = null, string? Path = null,
    string? Created = null, string? Prio = null, bool Pending = false, bool Elevated = false, string? Why = null,
    double? CpuMs = null, long Ws = 0);

/// <summary>detectGame's answer. <see cref="How"/>: known, store or exclusive.</summary>
public sealed record GameDetect(bool Game, string Why, string? File = null, int Pid = 0, string Title = "", string Exe = "",
    string? How = null);

/// <summary>Game mode's own game: the process it came on for and when it was last in front (ms).</summary>
public sealed record GameRun(int Pid, string File, long LastFront);

/// <summary>Game mode's state for <see cref="GameModePlan.Step"/>. <see cref="How"/>: auto or manual.</summary>
public sealed record GameModeState(bool On, string How = "auto", GameRun? Game = null, int? SuppressPid = null);

/// <summary>One tick's answer. <see cref="Action"/>: null, on, off, front or unsuppress.</summary>
public sealed record GameStep(string? Action, string Why, GameDetect? Game = null);

/// <summary>Mantle's rest as game mode sees it: resting, why, and the hand-off hold (pids) if any.</summary>
public sealed record GameRestState(bool Resting, string? Why = null, IReadOnlyList<int>? Hold = null);

/// <summary>What the rest step reads: the running games, game mode on/how/game, and the tray switch.</summary>
public sealed record GameRestInput(IReadOnlyList<GameProc> Run, bool On = false, string How = "auto", string? Game = null,
    bool Enabled = true);

/// <summary>The rest step's answer. <see cref="Action"/>: null, rest or wake.</summary>
public sealed record GameRestStep(string? Action, string? Why, IReadOnlyList<int>? Hold);

/// <summary>What <see cref="GameModePlan.Plan"/> needs besides the list.</summary>
public sealed class GamePlanContext
{
    public int GamePid { get; init; }
    public string? GameFile { get; init; }
    public int FgPid { get; init; }
    public int SidecarPid { get; init; }
    public int MainPid { get; init; }
    public int? OwnSession { get; init; }
    public ISet<int>? MantlePids { get; init; }
    public ISet<int>? AudioPids { get; init; }
    public string WinDir { get; init; } = @"C:\Windows";
}

public sealed record GameTarget(int Pid, string? Created, string? File, string Path, string? Prio, string Group);
public sealed record GameLeft(string? File, string Why);
public sealed record GamePlanResult(IReadOnlyList<GameTarget> Targets, IReadOnlyList<GameLeft> Left, int Quiet, int Pending);

/// <summary>An eased process as the restore record keeps it.</summary>
public sealed record GameEased(int Pid, string? Created, string? File, string Group);
public sealed record GameTrim(int Pid, string? Created, string? File, long Ws);
public sealed record GameTrimPlan(IReadOnlyList<GameTrim> Trim, IReadOnlyList<GameLeft> Skip, int Small);
public sealed record GameCpuBase(string? Created, double CpuMs);

/// <summary>
/// The PURE half of game mode (lib/features/_gamemode-plan.js): is a game in front, when does game mode end,
/// which processes it may touch, and the boost's one trim. No clock, no helper, no Windows: the tests drive it
/// with made-up lists. The helper checks every write target again with its own never-touch list.
/// </summary>
public static class GameModePlan
{
    public static readonly string[] KnownGames = { "VALORANT-Win64-Shipping.exe", "RobloxPlayerBeta.exe", "Minecraft.Windows.exe",
        "FortniteClient-Win64-Shipping.exe", "cs2.exe", "r5apex.exe", "RocketLeague.exe", "GTA5.exe", "Overwatch.exe",
        "League of Legends.exe", "EscapeFromTarkov.exe", "Cyberpunk2077.exe" };
    static readonly string[] Java = { "javaw.exe", "java.exe" };
    static readonly Regex Launchers = new(@"^(RiotClient|LeagueClient|MinecraftLauncher|Minecraft Launcher|RobloxPlayerLauncher|RobloxStudio|EpicGamesLauncher|steam|Battle\.net)", RegexOptions.IgnoreCase);
    /// <summary>The running-game names: the known games plus Riot's stub.</summary>
    public static readonly string[] RunExes = KnownGames.Concat(new[] { "VALORANT.exe" }).ToArray();

    static readonly (Regex Re, string Why)[] Never =
    {
        (new(@"^(VALORANT|Riot|vgc|vgtray|vgk|vanguard|League|EasyAntiCheat|EAAntiCheat|BEService|BattlEye|FACEIT)", RegexOptions.IgnoreCase), "a game or its anti-cheat"),
        (new(@"^(javaw|java|Minecraft|Roblox|cs2|csgo|r5apex|Fortnite|RocketLeague|GTA5|Overwatch|EscapeFromTarkov|Cyberpunk2077)", RegexOptions.IgnoreCase), "a game"),
        (new(@"^(GameBar|XboxPcApp|steam|EpicGames)", RegexOptions.IgnoreCase), "game platform"),
        (new(@"^(Discord|TeamSpeak|ts3client|Mumble|Teams|ms-teams|Zoom|Skype|WhatsApp)", RegexOptions.IgnoreCase), "voice and calls"),
        (new(@"^(Spotify|vlc|wmplayer|Music\.UI|Microsoft\.Media\.Player|audiodg)", RegexOptions.IgnoreCase), "music and video"),
        (new(@"^(obs|Streamlabs|Medal)", RegexOptions.IgnoreCase), "recording"),
        (new(@"^(lghub|Razer|SteelSeries|iCUE|Corsair|NVIDIA|nvcontainer)", RegexOptions.IgnoreCase), "mouse, keyboard and GPU software: slowing it adds input lag"),
        (new(@"^(TabTip|GameInput|TextInputHost|ctfmon)", RegexOptions.IgnoreCase), "typing and controller input"),
        (new(@"^(Defender|MsMpEng|MpDefender|SecurityHealth|NisSrv)", RegexOptions.IgnoreCase), "security"),
        (new(@"^Mantle\.Sidecar", RegexOptions.IgnoreCase), "Mantle's keyboard helper: slowing it would slow every key"),
        (new(@"^(RtkAud|Realtek|Nahimic|Dolby|Waves|MaxxAudio|voicemeeter|EarTrumpet|SoundSwitch|Krisp)", RegexOptions.IgnoreCase), "sound"),
        (new(@"^(PowerToys|AutoHotkey|Logi|Wacom|WTablet|DS4Windows|x360ce|ViGEm|Synaptics|SynTP|ETDCtrl)", RegexOptions.IgnoreCase), "keyboard, mouse and controller software: slowing it adds input lag"),
        (new(@"^(tailscale|openvpn|wireguard|NordVPN|ProtonVPN|ExpressVPN|Surfshark|mullvad|Cloudflare|warp-svc|ZeroTier|Killer)", RegexOptions.IgnoreCase), "the network: game traffic goes through it"),
        (new(@"^explorer", RegexOptions.IgnoreCase), "Windows' own shell"),
    };

    /// <summary>A game not in front this long ends game mode (a quick alt-tab does not).</summary>
    public const long AwayMs = 90_000;
    public const long TrimAfterMs = 60_000;
    public const double IdleCpuMs = 600;
    public const long TrimMinBytes = 16L * 1048576;
    public const long LowFreeBytes = 1536L * 1048576;
    public const int TrimChunk = 4;
    public const long TrimCallBytes = 1024L * 1048576;
    static readonly Regex NoTrim = new(@"^(Helm|claude)", RegexOptions.IgnoreCase);

    public static string BaseName(string? p) { var s = p ?? ""; int i = s.LastIndexOfAny(new[] { '\\', '/' }); return i < 0 ? s : s[(i + 1)..]; }
    public static bool SameFile(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    static bool IsJava(string? f) => Java.Any(j => SameFile(j, f));

    /// <summary>Is the window in front a game? <paramref name="procs"/> names it by pid when its path could not be read.</summary>
    public static GameDetect DetectGame(GameFg? fg, IReadOnlyList<GameProc>? procs)
    {
        if (fg == null || fg.Pid == 0 || fg.Self) return new(false, fg?.Self == true ? "Mantle is in front" : "no app is in front");
        var file = BaseName(fg.Exe);
        if (file.Length == 0 && procs != null) { var p = procs.FirstOrDefault(q => q != null && q.Pid == fg.Pid); if (!string.IsNullOrEmpty(p?.File)) file = p.File!; }
        var title = fg.Title ?? "";
        GameDetect B(bool game, string why, string? how = null) => new(game, why, file.Length == 0 ? null : file, fg.Pid, title, fg.Exe ?? "", how);
        if (file.Length == 0) return B(false, "the app in front could not be named");
        if (Launchers.IsMatch(file)) return B(false, $"{file} is a launcher, not the game");
        if (KnownGames.Any(g => SameFile(g, file))) return B(true, $"{file} is a known game", "known");
        if (IsJava(file) && title.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase)) return B(true, $"{file} has a Minecraft window", "known");
        if (fg.GameStore && fg.Fullscreen) return B(true, $"{file} is on Windows' own game list (Game Bar) and fills its monitor", "store");
        if (fg.Quns == 3) return B(true, $"{file} runs exclusive full-screen Direct3D", "exclusive");
        return B(false, fg.Fullscreen ? $"{file} fills the screen but is not a game" : $"{file} is not a game");
    }

    /// <summary>One tick of the automatic switch (step in _gamemode-plan.js). Never mutates.</summary>
    public static GameStep Step(GameModeState s, GameFg? fg, IReadOnlyList<GameProc>? procs, long now, bool auto = true, long awayMs = AwayMs)
    {
        var d = DetectGame(fg, procs);
        if (s.On)
        {
            if (s.How == "manual") return new(null, "on by hand until turned off", d);
            var g = s.Game ?? new GameRun(0, "", now);
            if (procs != null && !procs.Any(p => p.Pid == g.Pid && SameFile(p.File, g.File))) return new("off", $"{g.File} exited");
            if (d.Game && d.Pid == g.Pid) return new("front", d.Why, d);
            if (now - g.LastFront >= awayMs) return new("off", $"{g.File} has not been in front for {Math.Round(awayMs / 1000.0)} s");
            return new(null, $"{g.File} is running behind {d.File ?? "another window"}");
        }
        if (s.SuppressPid is int sp)
        {
            bool alive = procs == null || procs.Any(p => p.Pid == sp);
            if (!alive) return new("unsuppress", "the game you turned game mode off for has exited");
            if (d.Game && d.Pid == sp) return new(null, $"turned off by hand for this run of {d.File}");
        }
        if (!auto) return new(null, "automatic game mode is off", d);
        return d.Game ? new("on", d.Why, d) : new(null, d.Why, d);
    }

    /// <summary>The known games RUNNING in the user's own session, in front or not. <paramref name="seen"/> (kept by
    /// the caller) remembers a javaw pid once it was in front with a Minecraft window; it is dropped once gone.</summary>
    public static List<GameProc> RunningGames(GameFg? fg, IReadOnlyList<GameProc>? procs, int? ownSession,
        IDictionary<int, string>? seen = null, IEnumerable<string>? extra = null)
    {
        seen ??= new Dictionary<int, string>();
        var list = procs ?? Array.Empty<GameProc>();
        var d = DetectGame(fg, list);
        if (d.Game && IsJava(d.File)) seen[d.Pid] = d.File!;
        var names = RunExes.Concat(extra ?? Array.Empty<string>()).ToList();
        var outp = new List<GameProc>(); var alive = new HashSet<int>();
        foreach (var p in list)
        {
            if (p == null) continue;
            alive.Add(p.Pid);
            if (ownSession != null && p.Session != null && p.Session != ownSession) continue;
            if (names.Any(g => SameFile(g, p.File)) || (seen.TryGetValue(p.Pid, out var f) && SameFile(f, p.File))) outp.Add(new GameProc(p.Pid, p.File));
        }
        if (list.Count > 0) foreach (var pid in seen.Keys.ToList()) if (!alive.Contains(pid)) seen.Remove(pid);
        return outp;
    }

    /// <summary>"VALORANT" for VALORANT.exe / VALORANT-Win64-Shipping.exe; names joined, each once.</summary>
    public static string GameLabel(IEnumerable<string?>? files)
    {
        var outp = new List<string>();
        foreach (var f in files ?? Array.Empty<string?>())
        {
            var n = Regex.Replace(Regex.Replace(BaseName(f), @"\.exe$", "", RegexOptions.IgnoreCase), @"-Win64-Shipping$", "", RegexOptions.IgnoreCase);
            if (n.Length > 0 && !outp.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) outp.Add(n);
        }
        return string.Join(", ", outp);
    }

    /// <summary>When Mantle rests for a game and when it wakes (restStep). Never mutates.</summary>
    public static GameRestStep RestStep(GameRestState? r, GameRestInput x)
    {
        var run = x.Run ?? Array.Empty<GameProc>();
        var alive = run.Select(p => p.Pid).ToHashSet();
        var hold = r?.Hold != null && r.Hold.Any(alive.Contains) ? r.Hold : null;
        bool runs = run.Count > 0 && hold == null;
        bool want = x.Enabled && (x.On || runs);
        string? why = null;
        if (want)
        {
            var names = GameLabel(runs ? run.Select(p => p.File) : (x.Game != null ? new[] { x.Game } : Array.Empty<string>()));
            why = names.Length > 0 ? $"{names} runs" : (x.How == "manual" ? "game mode is on (by hand)" : "game mode is on");
        }
        bool resting = r?.Resting == true;
        if (want && (!resting || r!.Why != why)) return new("rest", why, hold);
        if (!want && resting)
        {
            var was = r!.Why ?? "the game runs";
            string w = !x.Enabled ? "game mode was switched off in Mantle's tray menu"
                : hold != null && run.Count > 0 ? "game mode was turned off by hand for this game"
                : was.EndsWith(" runs", StringComparison.Ordinal) ? $"{was[..^5]} exited" : $"{was} ended";
            return new("wake", w, hold);
        }
        return new(null, want ? why : null, hold);
    }

    /// <summary>Does a rest's why say a GAME runs ("VALORANT runs"), not game mode on by hand?</summary>
    public static bool RestIsGame(string? why) => why != null && why.EndsWith(" runs", StringComparison.Ordinal) && !why.StartsWith("game mode is on", StringComparison.Ordinal);

    /// <summary>Why a file is never touched, or null.</summary>
    public static string? NeverWhy(string? file)
    {
        foreach (var (re, why) in Never) if (re.IsMatch(file ?? "")) return why;
        return null;
    }

    /// <summary>Which processes get Efficiency mode (and below-normal priority when normal).</summary>
    public static GamePlanResult Plan(IReadOnlyList<GameProc>? procs, GamePlanContext c)
    {
        var targets = new List<GameTarget>(); var left = new List<GameLeft>();
        int quiet = 0, pending = 0;
        var list = procs ?? Array.Empty<GameProc>();
        var byPid = new Dictionary<int, GameProc>();
        foreach (var p in list) if (p != null) byPid[p.Pid] = p;
        bool InGame(GameProc p)
        {
            if (c.GamePid == 0) return false;
            if (p.Pid == c.GamePid) return true;
            if (c.GameFile != null && SameFile(p.File, c.GameFile)) return true;
            var q = p;
            for (int n = 0; q != null && n < 16; n++)
            {
                if (q.Ppid == c.GamePid) return true;
                byPid.TryGetValue(q.Ppid, out var next);
                q = next;
            }
            return false;
        }
        var audioPaths = AudioPaths(list, c.AudioPids);
        var winDir = c.WinDir.TrimEnd('\\').ToLowerInvariant() + "\\";
        foreach (var p in list)
        {
            if (p == null || p.Pid <= 8 || p.Session != c.OwnSession) { quiet++; continue; }
            if (p.Pid == c.SidecarPid) { left.Add(new(p.File, NeverWhy("Mantle.Sidecar.exe")!)); continue; }
            if (c.MainPid != 0 && p.Pid == c.MainPid) { left.Add(new(p.File, "Mantle's main process: its desktop layer sits inside Explorer's desktop, so slowing it can stall Explorer")); continue; }
            if (InGame(p)) { left.Add(new(p.File, "the game")); continue; }
            var nv = NeverWhy(p.File);
            if (nv != null) { left.Add(new(p.File, nv)); continue; }
            if (p.Pid == c.FgPid) { left.Add(new(p.File, "in front")); continue; }
            if (p.Pending) { pending++; continue; }
            if (string.IsNullOrEmpty(p.Path)) { left.Add(new(p.File, p.Why ?? "could not be read")); continue; }
            if (p.Path.ToLowerInvariant().StartsWith(winDir, StringComparison.Ordinal)) { quiet++; continue; }
            if (p.Elevated) { left.Add(new(p.File, "runs as administrator; Mantle does not")); continue; }
            if ((c.AudioPids?.Contains(p.Pid) ?? false) || audioPaths.Contains(p.Path.ToLowerInvariant())) { left.Add(new(p.File, "playing sound")); continue; }
            targets.Add(new(p.Pid, p.Created, p.File, p.Path, p.Prio, c.MantlePids?.Contains(p.Pid) == true ? "mantle" : "background"));
        }
        return new(targets, left, quiet, pending);
    }

    static HashSet<string> AudioPaths(IEnumerable<GameProc> list, ISet<int>? audioPids)
    {
        var s = new HashSet<string>();
        foreach (var p in list) if (p != null && audioPids?.Contains(p.Pid) == true && !string.IsNullOrEmpty(p.Path)) s.Add(p.Path.ToLowerInvariant());
        return s;
    }

    /// <summary>"claude ×3, msedgewebview2 ×9": counts by name, most first, at most <paramref name="max"/> names.</summary>
    public static string Names(IEnumerable<string?> files, int max = 6)
    {
        var m = new Dictionary<string, int>();
        foreach (var f in files)
        {
            var k = Regex.IsMatch(f ?? "", @"^Mantle\.Sidecar", RegexOptions.IgnoreCase) ? "Mantle helper" : Regex.Replace(f ?? "?", @"\.exe$", "", RegexOptions.IgnoreCase);
            m[k] = m.GetValueOrDefault(k) + 1;
        }
        var arr = m.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
        var shown = arr.Take(max).Select(e => e.Value > 1 ? $"{e.Key} ×{e.Value}" : e.Key).ToList();
        return string.Join(", ", shown) + (arr.Count > shown.Count ? $" and {arr.Count - shown.Count} more" : "");
    }

    /// <summary>The left-alone list grouped by reason: "Discord ×2, Teams (voice and calls)".</summary>
    public static List<(string Why, string Text)> LeftByWhy(IEnumerable<GameLeft> left) =>
        left.GroupBy(l => l.Why).Select(g => (g.Key, $"{Names(g.Select(l => l.File), 4)} ({g.Key})")).ToList();

    /// <summary>The trim list split into gamemode.trimbg calls: at most 4 processes and 1 GB each; a bigger one goes alone.</summary>
    public static List<List<GameTrim>> TrimCalls(IEnumerable<GameTrim>? trim)
    {
        var calls = new List<List<GameTrim>>(); var cur = new List<GameTrim>(); long bytes = 0;
        foreach (var t in trim ?? Array.Empty<GameTrim>())
        {
            long w = Math.Max(0, t.Ws);
            if (cur.Count > 0 && (cur.Count >= TrimChunk || bytes + w > TrimCallBytes)) { calls.Add(cur); cur = new(); bytes = 0; }
            cur.Add(t); bytes += w;
        }
        if (cur.Count > 0) calls.Add(cur);
        return calls;
    }

    /// <summary>"1.2 GB" / "300 MB".</summary>
    public static string Bytes(double n)
    {
        var v = Math.Max(0, n);
        return v >= 1073741824 ? (v / 1073741824).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB" : $"{Math.Round(v / 1048576, MidpointRounding.AwayFromZero)} MB";
    }

    /// <summary>"4 background apps": distinct exe names.</summary>
    public static string AppCount(IEnumerable<string?> files)
    {
        int n = files.Select(f => BaseName(f).ToLowerInvariant()).Distinct().Count();
        return $"{n} background app{(n == 1 ? "" : "s")}";
    }

    /// <summary>The CPU baseline the idle rule compares against.</summary>
    public static Dictionary<int, GameCpuBase> CpuBase(IEnumerable<GameProc>? procs)
    {
        var m = new Dictionary<int, GameCpuBase>();
        foreach (var p in procs ?? Array.Empty<GameProc>()) if (p != null && !string.IsNullOrEmpty(p.Created) && p.CpuMs is double cpu) m[p.Pid] = new(p.Created, cpu);
        return m;
    }

    /// <summary>Which EASED processes get the boost's one trim (only what game mode eased, so plan()'s rules hold).</summary>
    public static GameTrimPlan TrimPlan(IReadOnlyList<GameProc>? procs, IEnumerable<GameEased>? eased, IReadOnlyDictionary<int, GameCpuBase>? cpuBase,
        ISet<int>? audioPids = null, ISet<int>? done = null, double idleCpuMs = IdleCpuMs)
    {
        var list = procs ?? Array.Empty<GameProc>();
        var byPid = new Dictionary<int, GameProc>();
        foreach (var p in list) if (p != null) byPid[p.Pid] = p;
        var parents = new HashSet<int>();
        foreach (var p in list) if (p != null && byPid.TryGetValue(p.Ppid, out var q) && q.Pid != p.Pid && SameFile(q.File, p.File)) parents.Add(q.Pid);
        var audioPaths = AudioPaths(list, audioPids);
        var trim = new List<GameTrim>(); var skip = new List<GameLeft>(); int small = 0;
        foreach (var e in eased ?? Array.Empty<GameEased>())
        {
            if (e == null || e.Group != "background") continue;
            if (done?.Contains(e.Pid) == true) continue;
            if (!byPid.TryGetValue(e.Pid, out var p) || (p.Created != null && e.Created != null && p.Created != e.Created)) continue;
            if (NoTrim.IsMatch(p.File ?? "")) { skip.Add(new(p.File, "Helm and Claude are only eased: an agent may be working")); continue; }
            if (audioPids?.Contains(p.Pid) == true || (p.Path != null && audioPaths.Contains(p.Path.ToLowerInvariant()))) { skip.Add(new(p.File, "playing sound")); continue; }
            if (parents.Contains(p.Pid)) { skip.Add(new(p.File, "its app's main process")); continue; }
            if (cpuBase == null || !cpuBase.TryGetValue(p.Pid, out var b) || b.Created != p.Created) { skip.Add(new(p.File, "started after the game")); continue; }
            if (p.CpuMs is double cpu && cpu - b.CpuMs > idleCpuMs) { skip.Add(new(p.File, "busy in the last minute")); continue; }
            if (!(p.Ws >= TrimMinBytes)) { small++; continue; }
            trim.Add(new(p.Pid, p.Created, p.File, p.Ws));
        }
        return new(trim, skip, small);
    }

    /// <summary>The biggest app still holding memory (by exe name; never the game, never-touch or Mantle), or null under 256 MB.</summary>
    public static (string Name, long Ws)? BiggestApp(IEnumerable<GameProc>? procs, GamePlanContext c)
    {
        var m = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in procs ?? Array.Empty<GameProc>())
        {
            if (p == null || !(p.Ws > 0) || p.Session != c.OwnSession || p.Pid == c.GamePid || p.Pid == c.MainPid || NeverWhy(p.File) != null) continue;
            if (c.MantlePids?.Contains(p.Pid) == true) continue;
            if (string.IsNullOrEmpty(p.Path) || p.Path.ToLowerInvariant().StartsWith(c.WinDir.ToLowerInvariant(), StringComparison.Ordinal)) continue;
            if (c.GameFile != null && SameFile(p.File, c.GameFile)) continue;
            var k = Regex.Replace(BaseName(p.File), @"\.exe$", "", RegexOptions.IgnoreCase);
            m[k] = m.GetValueOrDefault(k) + p.Ws;
        }
        (string Name, long Ws)? best = null;
        foreach (var (name, ws) in m) if (best == null || ws > best.Value.Ws) best = (name, ws);
        return best != null && best.Value.Ws >= 256L * 1048576 ? best : null;
    }

    /// <summary>The boost's one line: "Freed 1.2 GB and eased 4 background apps".</summary>
    public static string BoostLine(IReadOnlyCollection<string?> eased, long freed, int trimmed)
    {
        var e = eased.Count > 0 ? $"eased {AppCount(eased)}" : "no background app to ease";
        return trimmed > 0 ? $"Freed {Bytes(freed)} and {e}" : char.ToUpperInvariant(e[0]) + e[1..];
    }
}
