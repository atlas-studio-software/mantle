using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.LauncherIndex;

// The index's sources, ported from lib/launcher-index.js one function at a time (buildApps, standInApps,
// parseStartApps, storeItems, profileCandidates, buildCommands, buildSettings, parseSleepStates, buildPower).
// Every source returns its rows with a status and a plain-words reason, so an empty list is never silent.

/// <summary>What one source gave: rows, ok | error, and why.</summary>
public sealed record SourceResult(List<LauncherItem> Items, string Status, string Reason);

/// <summary>A Start Menu root: machine-wide ("all users") first, the user's ("you") second.</summary>
public sealed record StartMenuRoot(string Dir, string Scope)
{
    /// The two real roots (never read by a test: tests pass scratch folders).
    public static IReadOnlyList<StartMenuRoot> Windows()
    {
        string pd = Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData";
        string ad = Environment.GetEnvironmentVariable("APPDATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        return [
            new(Path.Combine(pd, "Microsoft", "Windows", "Start Menu", "Programs"), "all users"),
            new(Path.Combine(ad, "Microsoft", "Windows", "Start Menu", "Programs"), "you"),
        ];
    }
}

/// <summary>lib/mantle-apps.js: Mantle's app windows that stand in for Windows' own.</summary>
public static class MantleApps
{
    public const string WinSettingsAumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
    public const string WinExplorerAumid = "microsoft.windows.explorer";
    public const string WinMediaAumid = "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic";

    public sealed record App(string Key, string Title, string WindowsName);

    /// In Electron's key order (settings, taskmgr, files, player): the first that stands in wins.
    public static readonly App[] Apps =
    [
        new("settings", "Mantle Settings", "Windows Settings"),
        new("taskmgr", "Mantle Task Manager", "Windows Task Manager"),
        new("files", "Mantle Files", "File Explorer"),
        new("player", "Mantle Player", "Media Player"),
    ];

    public static App Get(string key) => Apps.First(a => a.Key == key);

    static string Base(string? p) => string.IsNullOrEmpty(p) ? "" : Js.Lower(Path.GetFileName(p.Replace('/', '\\')));

    /// lib/mantle-apps.js#standInFor: a shortcut's parse (or a pin) of Windows' own app -> its Mantle key, or null.
    public static string? StandInFor(string? target = null, string? exe = null, string? aumid = null, string? args = null)
    {
        string a = string.IsNullOrEmpty(aumid) ? "" : Js.Lower(aumid);
        string[] names = [Base(exe), Base(target)];
        string t = Js.Lower((target ?? "").Trim());
        string ar = (args ?? "").Trim();
        if (a == WinSettingsAumid || names.Contains("systemsettings.exe") || t.StartsWith("ms-settings:", StringComparison.Ordinal)) return "settings";
        if (names.Contains("taskmgr.exe")) return "taskmgr";
        if (a == WinExplorerAumid || (names.Contains("explorer.exe") && ar.Length == 0)) return "files";
        if (a == Js.Lower(WinMediaAumid) || a.StartsWith("microsoft.zunemusic_8wekyb3d8bbwe!", StringComparison.Ordinal)) return "player";
        return null;
    }
}

/// <summary>Source 1: Start Menu shortcuts (lib/launcher-index.js#buildApps, #walkLnks, #standInApps).</summary>
public static class StartMenuApps
{
    /// APP_NOISE: shortcuts to a web page or an uninstaller, never an app you meant to launch.
    public static readonly Regex Noise = new("uninstall|help|read ?me|web ?site|documentation|release notes", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex StandInName = new("task ?manager|settings|file explorer", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static void Walk(string root, List<string> outList, List<string> errors, int depth)
    {
        if (depth > 8) return;                              // a symlink loop would otherwise spin
        FileSystemInfo[] entries;
        try { entries = new DirectoryInfo(root).GetFileSystemInfos(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            errors.Add(root + " (" + Code(e) + ")");        // surfaced, never swallowed
            return;
        }
        foreach (var e in entries)
        {
            string full = Path.Join(root, e.Name);
            // Node's Dirent.isDirectory() is false for a junction or symlink: those are not walked
            bool dir = (e.Attributes & FileAttributes.Directory) != 0 && (e.Attributes & FileAttributes.ReparsePoint) == 0;
            if (dir) Walk(full, outList, errors, depth + 1);
            else if (e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) outList.Add(full);
        }
    }

    /// Node's error code for a failed readdir, as lib/launcher-index.js prints it.
    internal static string Code(Exception e) => e switch
    {
        UnauthorizedAccessException => "EPERM",
        DirectoryNotFoundException or FileNotFoundException => "ENOENT",
        PathTooLongException => "ENAMETOOLONG",
        _ => "EIO",
    };

    public static SourceResult Build(IReadOnlyList<StartMenuRoot> roots)
    {
        var byName = new Dictionary<string, LauncherItem>(StringComparer.Ordinal);
        var order = new List<string>();                     // a JS Map keeps the FIRST insertion's place
        var errors = new List<string>();
        int found = 0;
        foreach (var (dir, scope) in roots)
        {
            if (!Directory.Exists(dir)) { errors.Add("missing folder " + dir); continue; }
            found++;
            var lnks = new List<string>();
            Walk(dir, lnks, errors, 0);
            foreach (var full in lnks)
            {
                string name = Path.GetFileNameWithoutExtension(full);
                if (Noise.IsMatch(name)) continue;
                string folder = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
                string key = Js.Lower(name);
                if (!byName.ContainsKey(key)) order.Add(key);
                byName[key] = new LauncherItem
                {
                    Id = Js.StableId("app", full),
                    Kind = "app",
                    Name = name,
                    Subtitle = Js.Lower(folder) == "programs" ? "Start Menu (" + scope + ")" : folder,
                    Path = full,
                };
            }
        }
        var items = Js.StableSort(StandIn(order.Select(k => byName[k])), (a, b) => Js.LocaleCompare(a.Name, b.Name));
        string status = "ok", reason = "";
        if (found == 0) { status = "error"; reason = "no Start Menu folder could be read: " + string.Join("; ", errors); }
        else if (items.Count == 0) { status = "error"; reason = "the Start Menu folders are empty or unreadable"; }
        else if (errors.Count > 0) reason = errors.Count + " folder(s) unreadable: " + string.Join("; ", errors.Take(2));
        return new SourceResult(items, status, reason);
    }

    /// standInApps: Windows' Task Manager / Settings / File Explorer shortcut -> Mantle's row in its place
    /// (same id) + Windows' own one row below. Only shortcuts named like them are read.
    public static List<LauncherItem> StandIn(IEnumerable<LauncherItem> items)
    {
        var outList = new List<LauncherItem>();
        foreach (var it in items)
        {
            string? key = null;
            if (StandInName.IsMatch(it.Name))
            {
                LnkFile.Parsed? lnk = null;
                try { lnk = LnkFile.ReadFile(it.Path); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { lnk = null; }
                // Windows' own Task Manager.lnk has NO target path; its icon location is Taskmgr.exe, so that counts too
                key = (lnk != null ? MantleApps.StandInFor(target: lnk.Target, exe: lnk.Target != null ? null : lnk.IconPath) : null)
                    ?? (Regex.IsMatch(it.Name, "^task manager$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "taskmgr"
                        : Regex.IsMatch(it.Name, "^file explorer$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ? "files" : null);
            }
            string? mantlePath = key switch { "taskmgr" => "mantle:taskmgr", "settings" => "mantle:settings", "files" => "mantle:files", _ => null };
            if (key == null || mantlePath == null) { outList.Add(it); continue; }
            var a = MantleApps.Get(key);
            string extra = key == "taskmgr" ? "taskmgr ctrl+shift+esc" : key == "files" ? "explorer folders win+e" : "win+i";
            outList.Add(it with
            {
                Name = a.Title,
                Subtitle = "Mantle · in place of " + a.WindowsName,
                Path = mantlePath,
                WindowsPath = it.Path,
                MantleApp = key,
                IconKey = "mantle:" + key,
                Keywords = Js.Lower(a.Title + " " + it.Name + " " + extra),
            });
            outList.Add(it with
            {
                Id = Js.StableId("app", it.Path + "#windows"),
                Name = a.WindowsName,
                Subtitle = it.Subtitle + " · Windows' own",
                Secondary = true,
            });
        }
        return outList;
    }
}

/// <summary>A Store app Windows lists (Get-StartApps: name + AUMID).</summary>
public sealed record StoreApp(string Name, string Aumid);

/// <summary>One answer of the Store list reader.</summary>
public sealed record StoreRead(bool Ok, IReadOnlyList<StoreApp> List, string Reason);

/// <summary>Source 1b: packaged (Store) apps with no .lnk (lib/launcher-index.js#parseStartApps, #storeItems).</summary>
public static class StoreApps
{
    public static readonly Regex AumidRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,200}_[A-Za-z0-9]{8,20}![A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);
    static readonly Regex Skip = new(@"^windows\.immersivecontrolpanel_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// What the reader runs (STORE_PS), in a hidden PowerShell with -NoProfile -NonInteractive -ExecutionPolicy Bypass.
    public const string Script = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "Get-StartApps | Where-Object { $_.AppID -like '*!*' } | ForEach-Object { $_.Name + [char]9 + $_.AppID }";

    public static List<StoreApp> Parse(string text)
    {
        var outList = new List<StoreApp>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (text.StartsWith('\uFEFF')) text = text.Substring(1);
        foreach (var line in Regex.Split(text, "\r?\n"))
        {
            int tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            string name = line.Substring(0, tab).Trim(), aumid = line.Substring(tab + 1).Trim();
            if (name.Length == 0 || !AumidRe.IsMatch(aumid) || seen.Contains(Js.Lower(aumid))) continue;
            seen.Add(Js.Lower(aumid));
            outList.Add(new StoreApp(name, aumid));
        }
        return outList;
    }

    public static List<LauncherItem> Items(IEnumerable<StoreApp>? list, ISet<string> lnkNames)
    {
        var items = new List<LauncherItem>();
        foreach (var (name, aumid) in list ?? [])
        {
            if (Skip.IsMatch(aumid) || StartMenuApps.Noise.IsMatch(name)) continue;
            if (lnkNames.Contains(Js.Lower(name))) continue;            // a desktop shortcut of the same name wins
            if (MantleApps.StandInFor(aumid: aumid) == "player")
            {
                var a = MantleApps.Get("player");
                items.Add(new LauncherItem
                {
                    Id = Js.StableId("app", "aumid:" + aumid), Kind = "app", Name = a.Title,
                    Subtitle = "Mantle · in place of " + a.WindowsName, Path = "mantle:player",
                    WindowsPath = @"shell:AppsFolder\" + aumid, Aumid = aumid, MantleApp = "player", StandsFor = a.WindowsName,
                    IconKey = "mantle:player", Keywords = Js.Lower(a.Title + " " + name + " music video songs films groove"),
                });
                items.Add(new LauncherItem
                {
                    Id = Js.StableId("app", "aumid:" + aumid + "#windows"), Kind = "app", Name = a.WindowsName,
                    Subtitle = "Microsoft Store app · Windows' own", Path = @"shell:AppsFolder\" + aumid, Aumid = aumid, Secondary = true,
                });
                continue;
            }
            items.Add(new LauncherItem
            {
                Id = Js.StableId("app", "aumid:" + aumid), Kind = "app", Name = name,
                Subtitle = "Microsoft Store app", Path = @"shell:AppsFolder\" + aumid, Aumid = aumid,
            });
        }
        return items;
    }

    /// The footer part while the list is missing (lib/launcher-index.js#storePart): "" once it is in.
    public static string Part(bool haveList, bool ok, string reason, bool pending)
    {
        if (haveList) return "";
        if (!ok && reason.Length > 0) return "Store apps are missing: " + reason;
        if (!pending) return "Store apps are missing: Windows did not list them";
        return "Store apps are still loading";
    }
}

/// <summary>Source 2: the user's PowerShell profile functions and aliases (lib/launcher-index.js#buildCommands).</summary>
public static class ProfileCommands
{
    static readonly Regex Func = new(@"^function\s+([A-Za-z0-9_][A-Za-z0-9_.\-]*)", RegexOptions.CultureInvariant);
    static readonly Regex Alias = new(@"^Set-Alias\s+(?:-Name\s+)?([A-Za-z0-9_][A-Za-z0-9_.\-]*)", RegexOptions.CultureInvariant);

    /// Every place a profile can legally live (Documents, %OneDrive%\Documents, ~\OneDrive\Documents; PS 7 then 5.1).
    public static List<string> Candidates(string home, string? oneDrive)
    {
        var docs = new List<string> { Path.Join(home, "Documents") };
        if (!string.IsNullOrEmpty(oneDrive)) docs.Add(Path.Join(oneDrive, "Documents"));
        docs.Add(Path.Join(home, "OneDrive", "Documents"));
        var outList = new List<string>();
        foreach (var d in docs)
        {
            foreach (var f in new[] { Path.Join(d, "PowerShell", "Microsoft.PowerShell_profile.ps1"), Path.Join(d, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1") })
                if (!outList.Contains(f)) outList.Add(f);
        }
        return outList;
    }

    public static SourceResult Build(IEnumerable<string> candidates)
    {
        var found = new Dictionary<string, LauncherItem>(StringComparer.Ordinal);
        var order = new List<string>();
        var read = new List<string>();
        var errors = new List<string>();
        foreach (var file in candidates)
        {
            if (!File.Exists(file)) continue;
            string text;
            // fs.readFileSync(file, 'utf8'): a BOM stays in the text, as U+FEFF
            try { text = Encoding.UTF8.GetString(File.ReadAllBytes(file)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { errors.Add(Path.GetFileName(Path.GetDirectoryName(file) ?? "") + ": " + StartMenuApps.Code(e)); continue; }
            read.Add(file);
            foreach (var line in Regex.Split(text, "\r?\n"))
            {
                var m = Func.Match(line);
                if (!m.Success) m = Alias.Match(line);
                if (!m.Success) continue;
                string name = m.Groups[1].Value;
                if (name.StartsWith('_')) continue;                   // private by convention
                string key = Js.Lower(name);
                if (found.ContainsKey(key)) continue;
                order.Add(key);
                found[key] = new LauncherItem { Id = Js.StableId("cmd", name), Kind = "command", Name = name, Subtitle = "PowerShell profile", Path = name };
            }
        }
        var items = Js.StableSort(order.Select(k => found[k]), (a, b) => Js.LocaleCompare(a.Name, b.Name));
        string status = "ok", reason = "";
        if (read.Count == 0) { status = "ok"; reason = ""; }             // no profile is normal: nothing to warn about
        else if (items.Count == 0) { status = "error"; reason = "no functions or aliases in " + read[0]; }
        else if (errors.Count > 0) reason = "unreadable: " + string.Join("; ", errors);
        return new SourceResult(items, status, reason);
    }
}

/// <summary>Source 3: Settings pages (lib/launcher-index.js#buildSettings, #coverOf, #MANTLE_TARGETS).</summary>
public static partial class SettingsTable
{
    /// MANTLE_TARGETS: Mantle's own windows, listed as settings. [name, uri, words or null].
    public static readonly (string Name, string Uri, string? Words)[] MantleTargets =
    [
        ("Wallpapers", "mantle:wallpapers", null),
        ("Mantle Settings", "mantle:settings", "settings preferences options mantle features shortcuts win+i"),
        ("Mantle Task Manager", "mantle:taskmgr", "task manager processes end task running apps cpu memory ram ctrl+shift+esc"),
        ("Get ready to game (free RAM)", "mantle:taskmgr:game", "free ram memory game close apps task manager"),
        ("Mantle Files", "mantle:files", "files file explorer folders documents downloads browse win+e"),
        ("Mantle Player", "mantle:player", "player media player music video movies films songs albums artists playlist play groove"),
        ("Clips", "mantle:clips", "clips recordings captures videos screenshots game bar record trim share win+g"),
    ];

    static readonly Dictionary<string, string> TargetIcon = new(StringComparer.Ordinal)
    {
        ["mantle:settings"] = "mantle:settings", ["mantle:taskmgr"] = "mantle:taskmgr", ["mantle:taskmgr:game"] = "mantle:taskmgr",
        ["mantle:files"] = "mantle:files", ["mantle:player"] = "mantle:player",
    };

    public sealed record Cover(string Section, string? Page, string? Row);

    static Dictionary<string, Cover>? _covers;

    /// Where Mantle Settings opens a Windows Settings page: a section module's claim, else MANTLE_SETTINGS; null = Windows' own only.
    public static Cover? CoverOf(string? uri)
    {
        var map = _covers ??= MakeCovers();
        return map.TryGetValue(Js.Lower(uri ?? ""), out var c) ? c : null;
    }

    static Dictionary<string, Cover> MakeCovers()
    {
        var m = new Dictionary<string, Cover>(StringComparer.Ordinal);
        foreach (var c in Covered) m[Js.Lower(c.Uri)] = new Cover(c.Section, null, c.Row);
        foreach (var c in Claims) m[Js.Lower(c.Uri)] = new Cover(c.Section, c.Page, c.Row);   // a claim wins over the built-in table
        return m;
    }

    static readonly Regex SettingsWords = new(@"\b(settings?|windows)\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    public static SourceResult Build()
    {
        var listed = new HashSet<string>(Pages.Select(r => Js.Lower(r[1])), StringComparer.Ordinal);
        var rows = new List<(string Name, string Uri, string Words)>();
        foreach (var r in Pages) rows.Add((r[0], r[1], r[2]));
        // a claimed page the table lacks: its own words + where it sits, minus "settings"/"windows"
        foreach (var c in Claims)
            if (!listed.Contains(c.Uri))
                rows.Add((c.Name, c.Uri, SettingsWords.Replace(c.Keywords + " " + c.Where.Replace("›", " "), " ")));
        var items = new List<LauncherItem>();
        foreach (var (name, uri, words) in rows)
        {
            bool covered = CoverOf(uri) != null;
            items.Add(new LauncherItem
            {
                Id = Js.StableId("set", uri), Kind = "setting", Name = name,
                Subtitle = covered ? "Mantle Settings" : "Windows Settings", Path = uri,
                IconKey = covered ? "mantle:settings" : null,
                Keywords = Spaces.Replace(Js.Lower(words ?? ""), " ").Trim(),
            });
        }
        foreach (var (name, uri, words) in MantleTargets)
            items.Add(new LauncherItem
            {
                Id = Js.StableId("set", uri), Kind = "setting", Name = name, Subtitle = "Mantle", Path = uri,
                IconKey = TargetIcon.TryGetValue(uri, out var k) ? k : null, Keywords = string.IsNullOrEmpty(words) ? null : words,
            });
        items.Add(new LauncherItem
        {
            Id = Js.StableId("set", "ms-settings:"), Kind = "setting", Name = "Windows Settings",
            Subtitle = "Windows' own Settings app", Path = "ms-settings:", Secondary = true,
            Keywords = "settings windows settings control panel system settings pc settings",
        });
        return new SourceResult(items, "ok", "");
    }
}

/// <summary>What <c>powercfg /a</c> says (lib/launcher-index.js#parseSleepStates).</summary>
public sealed record SleepStates(bool Ok, string Reason = "", bool Hibernate = false, string HibernateWhy = "", bool Sleep = false,
    string SleepMode = "", string SleepWhy = "", bool FastStartup = false);

/// <summary>Source 4: power and session rows (lib/launcher-index.js#POWER, #buildPower).</summary>
public static class PowerRows
{
    public sealed record Def(string Action, string Name, string Subtitle, string Glyph, string[] Words, string? Confirm = null);

    public static readonly Def[] Power =
    [
        new("lock", "Lock", "Session", "lock", ["lock screen", "lock computer", "lock pc"]),
        new("sleep", "Sleep", "Power", "sleep", ["suspend", "standby"]),
        new("hibernate", "Hibernate", "Power", "hibernate", ["hibernation"]),
        new("restart", "Restart", "Power · asks first", "restart", ["reboot", "restart computer", "restart pc"], "Press Enter again to restart"),
        new("shutdown", "Shut down", "Power · asks first", "power", ["shutdown", "turn off", "power off", "switch off"], "Press Enter again to shut down"),
        new("signout", "Sign out", "Session · asks first", "signout", ["log out", "logout", "log off", "logoff", "sign off", "signout"], "Press Enter again to sign out"),
    ];

    /// Typed on their own these reach every row of the group, below anything with that name.
    public static readonly string[] GroupWords = ["power", "session"];

    static readonly Regex HeadAvail = new("^The following sleep states are available", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex HeadNot = new("^The following sleep states are not available", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Indent4 = new(@"^\s{4}\S", RegexOptions.CultureInvariant);

    public static SleepStates ParseSleepStates(string text)
    {
        var lines = Regex.Split(text, "\r?\n");
        int headAvail = Array.FindIndex(lines, l => HeadAvail.IsMatch(l.Trim()));
        int headNot = Array.FindIndex(lines, l => HeadNot.IsMatch(l.Trim()));
        if (headAvail < 0)
        {
            string first = (lines.Length > 0 ? lines[0] : "").Trim();
            return new SleepStates(false, "powercfg /a answered in a layout this parser does not know (\"" + (first.Length > 60 ? first.Substring(0, 60) : first) + "\")");
        }
        int availEnd = headNot > headAvail ? headNot : lines.Length;
        var available = lines.Skip(headAvail + 1).Take(availEnd - headAvail - 1).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var why = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string? current = null;
        if (headNot >= 0)
            foreach (var raw in lines.Skip(headNot + 1))
            {
                if (raw.Trim().Length == 0) continue;
                if (Indent4.IsMatch(raw) && !raw.StartsWith('\t')) { current = raw.Trim(); why[current] = []; }
                else if (current != null) why[current].Add(raw.Trim());
            }
        bool hib = available.Any(s => string.Equals(s, "Hibernate", StringComparison.OrdinalIgnoreCase));
        var standby = available.Where(s => s.StartsWith("Standby", StringComparison.OrdinalIgnoreCase)).ToList();
        string hibWhy = "";
        if (!hib) { hibWhy = why.TryGetValue("Hibernate", out var w) ? string.Join(" ", w) : ""; if (hibWhy.Length == 0) hibWhy = "powercfg /a does not list it as available"; }
        return new SleepStates(true, "", hib, hibWhy, standby.Count > 0, standby.Count > 0 ? standby[0] : "",
            standby.Count > 0 ? "" : "powercfg /a lists no standby state as available",
            available.Any(s => string.Equals(s, "Fast Startup", StringComparison.OrdinalIgnoreCase)));
    }

    /// Shut down does what Start's does: /hybrid only while powercfg /a lists Fast Startup.
    public static string[] ShutdownArgs(SleepStates? s) => s is { Ok: true, FastStartup: true } ? ["/s", "/hybrid", "/t", "0"] : ["/s", "/t", "0"];

    public static SourceResult Build(SleepStates states)
    {
        var notes = new List<string>();
        var items = new List<LauncherItem>();
        foreach (var p in Power)
        {
            if (p.Action == "hibernate")
            {
                if (!states.Ok) { notes.Add("Hibernate left out: " + states.Reason); continue; }
                if (!states.Hibernate) { notes.Add("Hibernate left out: " + states.HibernateWhy); continue; }
            }
            if (p.Action == "sleep" && states.Ok && !states.Sleep) { notes.Add("Sleep left out: " + states.SleepWhy); continue; }
            items.Add(new LauncherItem
            {
                Id = Js.StableId("power", p.Action), Kind = "power", Name = p.Name, Subtitle = p.Subtitle,
                Path = p.Action, Glyph = p.Glyph, PowerWords = p.Words, Confirm = p.Confirm,
            });
        }
        return new SourceResult(items, "ok", string.Join("; ", notes));
    }
}
