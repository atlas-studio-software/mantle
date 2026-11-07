using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Data;

/// <summary>One Mantle setting: its key, JSON type, default, and the profile file that keeps it.</summary>
/// <param name="File">relative to the profile (<c>features\gamemode.json</c>); <c>features\&lt;feature&gt;.json</c> for every feature's own file</param>
/// <param name="Writer">the Electron code that writes it (the native store writes the same file the same way)</param>
public sealed record SettingsKey(string Key, string Type, string Default, string File, string Writer);

/// <summary>
/// The settings key index (docs/parity/settings-mantle.md "Settings key index") as data: every Mantle preference
/// Settings shows, and the file it lives in, so a native page reads and writes the SAME file Electron does through
/// <see cref="Stores"/>. Windows-backed rows (registry, StartupApproved) and Google's encrypted blobs are not
/// profile JSON and are not here. The tests check that every file is one a store may open.
/// </summary>
public static class SettingsKeys
{
    static readonly SettingsKey[] Rows = {
        new("<feature>", "bool", "the feature's defaultOn (all true except lookaway)", "features.json", "lib/features.js#saveSwitches"),
        new("<feature>.<option>", "per option", "per option (SETM-115 to 142)", @"features\<feature>.json", "lib/features.js#setOption"),
        new("corners.actions", "{tl, tr, bl, br}: action ids", "tl taskview, tr off, bl off, br desktop", @"features\corners.json", "lib/features/corners.js (corners.set)"),
        new("apppower.rules", "[{key, exe, path, label, set, at}]", "[]", @"features\apppower.json", "lib/features/apppower.js#persist"),
        new("ramreclaim.pieces", "{piece id: bool}", "per piece defaultOn", @"features\ramreclaim.json", "lib/features/ramreclaim.js (ramreclaim.set)"),
        new("gcal.calendars", "{calendar id: bool}", "primary or Google-selected on", @"features\gcal.json", "lib/features/_gcal-core.js (gcal.setCalendar)"),
        new("gamemode.auto", "bool", "true", @"features\gamemode.json", "lib/features/gamemode.js"),
        new("gamemode.liftSaver", "bool", "false", @"features\gamemode.json", "lib/features/gamemode.js"),
        new("gamemode.boost", "bool", "true", @"features\gamemode.json", "lib/features/gamemode.js"),
        new("sections.<id>.<key>", "any", "none", @"features\settings.json", "section main halves (env.settings.set)"),
        new("altTab", "bool", "true", "switcher.json", "lib/switcher.js (settings.switcher)"),
        new("winKeys", "bool", "true", "switcher.json", "lib/switcher.js (settings.switcher)"),
        new("keepReady.settings", "bool", "false", "keep-ready.json", "lib/keep-ready.js#set"),
        new("keepReady.taskmgr", "bool", "false", "keep-ready.json", "lib/keep-ready.js#set"),
        new("keepReady.files", "bool", "false", "keep-ready.json", "lib/keep-ready.js#set"),
        new("mode", "system | light | dark", "system", "theme.json", "lib/theme.js#save (theme.set)"),
        new("lookSync", "bool", "true", "look-sync.json", "lib/look-host.js#save (look.sync)"),
        new("accent", "{hue: null | 0..360}", "{hue: null}", "look-sync.json", "lib/look-host.js#save (look.accent)"),
        new("glass", "{dark, light: {blurPx, dim, panel}, render}", "dark 5 / .50 / 0, light 12 / .45 / 0, picture", "look-sync.json", "lib/look-host.js#save (look.glass)"),
        new("selected", "builtin:<name> | library id", "none", @"wallpapers\library.json", "lib/wallpaper-lively.js#Library.save (wallpaper.set)"),
    };

    /// Every row, in the index's order.
    public static IReadOnlyList<SettingsKey> All => Rows;

    /// The rows kept in one file (relative to the profile; a feature's own file matches the <c>&lt;feature&gt;</c> rows too).
    public static IEnumerable<SettingsKey> In(string relativeFile)
    {
        string? want = DataFileSpecs.Normalize(relativeFile);
        if (want == null) return Array.Empty<SettingsKey>();
        bool feature = want.StartsWith(@"features\", StringComparison.OrdinalIgnoreCase) && DataFileSpecs.For(want) != null;
        return Rows.Where(r => r.File.Equals(want, StringComparison.OrdinalIgnoreCase) || (feature && r.File == @"features\<feature>.json"));
    }
}
