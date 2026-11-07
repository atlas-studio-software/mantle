using System;
using System.Collections.Generic;
using System.IO;

namespace Mantle.Core.Data;

/// <summary>How Electron writes one file, so native writes the same bytes (docs/data-files.md (a)).</summary>
/// <param name="Indent">the third argument of the writer's <c>JSON.stringify</c> (0 = compact)</param>
/// <param name="TrailingNewline">the writer appends <c>"\n"</c></param>
/// <param name="AsideOverBytes">Electron's own size limit: a bigger file is moved aside as unusable (0 = no limit in Electron)</param>
/// <param name="PidTmp">the writer's tmp is <c>&lt;file&gt;.&lt;pid&gt;.tmp</c> instead of <c>&lt;file&gt;.tmp</c></param>
/// <param name="Writer">the Electron function that writes it, for the log and the tests</param>
/// <param name="NeverWritten">Mantle only reads it (the user edits it by hand): every write is refused</param>
public sealed record DataFileSpec(string Name, int Indent, bool TrailingNewline, long AsideOverBytes, bool PidTmp, string Writer, bool NeverWritten = false)
{
    /// Native's safety ceiling for files Electron sets no limit on: bigger is refused (never moved aside, never read).
    public const long SafetyMaxBytes = 16L * 1024 * 1024;
}

/// <summary>Every JSON object file in the profile that a native store may open, with its exact Electron format.</summary>
public static class DataFileSpecs
{
    const long FeatureMax = 4L * 1024 * 1024;   // lib/features.js SETTINGS_MAX
    const long ThemeMax = 64 * 1024;            // lib/theme.js FILE_MAX

    static readonly DataFileSpec[] Fixed = {
        new("features.json", 2, false, 0, false, "lib/features.js#saveSwitches"),
        new("theme.json", 2, false, ThemeMax, false, "lib/theme.js#save"),
        new("look-sync.json", 2, false, 0, false, "lib/look-host.js#save"),
        new("first-run.json", 2, false, 0, false, "lib/welcome.js#writeFlag"),
        new("installed-consent.json", 2, true, 0, false, "lib/packaged.js#writeConsent"),
        new("crash.json", 0, false, 0, false, "main.js uncaughtException (crashFile)"),
        new("keep-ready.json", 2, true, 0, true, "lib/keep-ready.js#set"),
        new("media-guard.json", 2, false, 0, false, "the user, by hand (lib/media-guard.js reads it)", NeverWritten: true),
        new("notifications-seen.json", 0, false, 0, false, "lib/notifications.js"),
        new("switcher.json", 2, false, 0, false, "lib/switcher.js"),
        new("notif-banners.json", 0, false, 0, false, "lib/features/toasts.js (RECORD_FILE)"),
        new("gamemode-restore.json", 1, false, 0, false, "lib/features/gamemode.js#writeRecord"),
        new("ping-tests.json", 2, false, 0, false, "lib/features/_pingtest-history.js"),
        new("supervisor-main.json", 0, false, 0, false, "lib/supervisor.js#writeRec"),
        new("player-library.json", 0, false, 0, false, "lib/features/_player-library.js"),
        new(@"wallpapers\library.json", 2, false, 0, false, "lib/wallpaper-lively.js#Library.save"),
        new(@"gcal\cache.json", 0, false, 0, false, "lib/features/_gcal-store.js#putCache"),
        new(@"features\ramreclaim-journal.json", 1, false, 0, false, "lib/features/_ramreclaim-core.js#save"),
        new(@"features\taskmgr-history.json", 0, false, 0, false, "lib/features/_taskmgr-history.js#save"),
        new(@"features\files-last.json", 0, false, 0, false, "lib/features/files.js (LAST_FILE)"),
    };

    static readonly Dictionary<string, DataFileSpec> ByName = Build();

    static Dictionary<string, DataFileSpec> Build()
    {
        var d = new Dictionary<string, DataFileSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Fixed) d.Add(s.Name, s);
        return d;
    }

    /// Every fixed entry (the round-trip tests walk this list).
    public static IReadOnlyList<DataFileSpec> All => Fixed;

    /// <summary>The canonical relative name (backslashes, no leading separator), or null when it is not a safe relative path.</summary>
    public static string? Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        string r = relativePath.Replace('/', '\\');
        if (Path.IsPathRooted(r) || r.StartsWith('\\') || r.Contains(':')) return null;
        foreach (var part in r.Split('\\'))
            if (part.Length == 0 || part == "." || part == "..") return null;
        return r;
    }

    /// <summary>The spec for a relative path: a fixed entry, else a feature's own settings file
    /// (<c>features\&lt;name&gt;.json</c>, lib/features.js#settingsFor), else null (not a file a store may open).</summary>
    public static DataFileSpec? For(string relativePath)
    {
        string? r = Normalize(relativePath);
        if (r == null) return null;
        if (ByName.TryGetValue(r, out var s)) return s;
        var parts = r.Split('\\');
        if (parts.Length == 2 && parts[0].Equals("features", StringComparison.OrdinalIgnoreCase) && parts[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            string feature = parts[1][..^5];
            if (IsFeatureName(feature)) return new DataFileSpec(@"features\" + parts[1], 2, false, FeatureMax, false, "lib/features.js#settingsFor");
        }
        return null;
    }

    /// lib/features.js NAME_RE: lower-case letters and digits, starting with a letter.
    static bool IsFeatureName(string n)
    {
        if (n.Length == 0 || n[0] < 'a' || n[0] > 'z') return false;
        foreach (char c in n) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
        return true;
    }
}
