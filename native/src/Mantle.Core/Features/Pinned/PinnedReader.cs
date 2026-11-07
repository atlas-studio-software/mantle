using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Features.WindowList;

namespace Mantle.Core.Features.Pinned;

// The port of lib/pinned.js (BAR-262..278): the taskbar pins are READ from Windows' own data, never written:
//   source 1: the .lnk files in %APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar;
//   source 2: the order, from HKCU\...\Explorer\Taskband Favorites (the sidecar's `pins` walks it through the
//             shell and also returns the Store-app pins, which have no .lnk at all);
//   source 3: each .lnk through the sidecar's `lnk` (IShellLink + the AUMID), else the built-in MS-SHLLINK reader.
// Every per-item failure keeps the item with a plain reason; only a missing folder fails the list.

/// One pin (lib/pinned.js listPinned item; the overlay's kind 'mantle' adds Mantle's own pins).
public sealed class Pin
{
    public string Id = "";
    public string Name = "";
    /// "lnk" (a shortcut file), "app" (a Store-app pin with no .lnk), "mantle" (Mantle's own overlay pin).
    public string Kind = "lnk";
    public string? LnkPath;
    public string? Target;
    public string Args = "";
    /// The target exe in lower case, only when it is a file on disk.
    public string? Exe;
    public string? Aumid;
    public string? IconPath;
    public int IconIndex;
    /// A sidecar-rendered PNG data URL: used only for pins without a file exe (BAR-273).
    public string? IconPng;
    public int Order;
    public string? Reason;

    public Pin Copy() => (Pin)MemberwiseClone();
}

/// lib/pinned.js's reply: ok false only when the pins folder cannot be read; ok true may carry notes.
public sealed record PinnedResult(bool Ok, string? Reason, IReadOnlyList<Pin> Items);

public sealed class PinnedOptions
{
    /// The TaskBar folder itself (<see cref="PinnedReader.PinsDir"/> of %APPDATA%; a test's scratch folder).
    public string PinsDir = "";
    /// The Taskband Favorites REG_BINARY; throws with plain words when it cannot be read. Null: not read at all.
    public Func<byte[]>? ReadFavorites;
    /// The sidecar (pins, lnk, app); null = no connection (lib/pinned.js "no sidecar connection was passed").
    public SidecarCall? Call;
    /// %VAR% expansion source (process.env on Windows is case-insensitive).
    public Func<string, string?> Env = Environment.GetEnvironmentVariable;
    public Func<string, bool> IsFile = File.Exists;
    /// The folder watch's parked failure (watchReason), merged into the notes.
    public string? WatchReason;
}

public static class PinnedReader
{
    public static readonly string[] PinsSubpath = { "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar" };
    public const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";

    public static string PinsDir(string appData) => Path.Combine(new[] { appData }.Concat(PinsSubpath).ToArray());

    /// Why the pins folder failed, in plain words and with NO path (lib/pinned.js folderWhy).
    public static string FolderWhy(Exception e) => e switch {
        DirectoryNotFoundException => "is missing",
        FileNotFoundException => "is missing",
        UnauthorizedAccessException => "could not be opened: access was denied",
        IOException io when io.HResult == unchecked((int)0x8007010B) => "is not a folder",   // ERROR_DIRECTORY
        _ => "could not be read" + (e.HResult != 0 ? " (0x" + e.HResult.ToString("X8") + ")" : ""),
    };

    static readonly Regex VarRe = new(@"%([^%\\/]+)%", RegexOptions.Compiled);

    /// %VAR% expanded case-insensitively; an unknown variable is left as written (lib/pinned.js expandVars).
    public static string ExpandVars(string? s, Func<string, string?> env)
        => VarRe.Replace(s ?? "", m => env(m.Groups[1].Value) is string v ? v : m.Value);

    static readonly Regex IconLocRe = new(@"^(.*[^,]),\s*(-?\d+)$", RegexOptions.Compiled | RegexOptions.Singleline);

    /// "C:\x\shell32.dll,15" -> (path, 15); a negative index is a resource id (lib/pinned.js splitIconLocation).
    public static (string? IconPath, int IconIndex) SplitIconLocation(string? s)
    {
        string str = (s ?? "").Trim();
        if (str.Length == 0) return (null, 0);
        var m = IconLocRe.Match(str);
        if (m.Success && int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int idx))
            return (m.Groups[1].Value.Trim(), idx);
        return (str, 0);
    }

    /// Best-effort order: each .lnk name searched in the blob (UTF-16LE, then ANSI, case-insensitive), sorted by
    /// first offset; names the blob does not mention go last, alphabetically; dense 0..n-1 (lib/pinned.js orderNames).
    public static Dictionary<string, int> OrderNames(IReadOnlyList<string> names, byte[]? blob)
    {
        string hay = blob is { Length: > 0 } ? Latin1(blob).ToLowerInvariant() : "";
        var found = new List<(string Name, int At)>();
        var missing = new List<string>();
        foreach (var name in names) {
            int at = -1;
            if (hay.Length > 0) {
                at = hay.IndexOf(Latin1(Encoding.Unicode.GetBytes(name)).ToLowerInvariant(), StringComparison.Ordinal);
                if (at == -1) at = hay.IndexOf(name.ToLowerInvariant(), StringComparison.Ordinal);
            }
            if (at == -1) missing.Add(name); else found.Add((name, at));
        }
        found.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : Compare(a.Name, b.Name));
        missing.Sort(Compare);
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        int i = 0;
        foreach (var f in found) order[f.Name] = i++;
        foreach (var n in missing) order[n] = i++;
        return order;
    }

    /// One byte = one char, as Buffer.toString('latin1'); ToLowerInvariant then lowers only what JS lowers in latin1.
    static string Latin1(byte[] b) => Encoding.Latin1.GetString(b);

    /// String.prototype.localeCompare for file names: culture-aware, case-insensitive first.
    internal static int Compare(string a, string b) => string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase) is int c && c != 0 ? c : string.CompareOrdinal(a, b);

    /// The user's own apps say their own names (lib/pinned.js OWN_APP_NAMES).
    static readonly Dictionary<string, string> OwnAppNames = new(StringComparer.Ordinal) {
        ["quartz.exe"] = "Quartz", ["helm.exe"] = "Helm", ["mantle.exe"] = "Mantle",
    };

    /// The name the bar shows for a .lnk pin: the shortcut's name, or the app's own for the user's apps.
    public static string PinName(string lnkName, string? target)
    {
        string t = target ?? "";
        int cut = Math.Max(t.LastIndexOf('\\'), t.LastIndexOf('/'));
        string b = (cut >= 0 ? t[(cut + 1)..] : t).ToLowerInvariant();
        if (OwnAppNames.TryGetValue(b, out var own)) return own;
        return Regex.Replace(lnkName, @"\.lnk$", "", RegexOptions.IgnoreCase);
    }

    static readonly Regex ShellTargetRe = new(@"^(::|shell:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async ValueTask<PinnedResult> ListAsync(PinnedOptions o)
    {
        List<string> names;
        try {
            names = Directory.EnumerateFiles(o.PinsDir)
                .Select(Path.GetFileName).Where(n => n != null && n.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!).ToList();
            names.Sort(Compare);
        } catch (Exception e) {
            if (e is IOException && File.Exists(o.PinsDir)) return new PinnedResult(false, "Windows' pinned-apps folder is not a folder", Array.Empty<Pin>());
            return new PinnedResult(false, "Windows' pinned-apps folder " + FolderWhy(e), Array.Empty<Pin>());
        }
        var full = names.Select(n => Path.Combine(o.PinsDir, n)).ToList();
        var notes = new List<string>();
        if (o.WatchReason != null) notes.Add(o.WatchReason);

        byte[]? blob = null;
        string? blobReason = null;
        if (o.ReadFavorites == null) blobReason = "the taskbar order was not read";
        else {
            try { blob = o.ReadFavorites(); if (blob == null || blob.Length == 0) { blob = null; blobReason = "the Favorites value is empty"; } }
            catch (Exception e) { blobReason = e.Message; }
        }

        JsonElement pinsReply = default;
        bool havePins = false;
        string? pinsReason = null;
        if (blob == null) pinsReason = "the taskbar order could not be read: " + blobReason;
        else if (o.Call == null) pinsReason = "no sidecar connection was passed to listPinned";
        else {
            try {
                var res = await o.Call("pins", new JsonObject { ["blob"] = Convert.ToBase64String(blob) }, TimeSpan.FromMilliseconds(8000)).ConfigureAwait(true);
                if (res.ValueKind != JsonValueKind.Object || TaskJson.IsFalse(res, "ok")) pinsReason = TaskJson.Str(res, "reason") ?? "the sidecar could not read the taskbar pin list";
                else if (TaskJson.Get(res, "items").ValueKind != JsonValueKind.Array) pinsReason = "the sidecar's pins command returned no items";
                else { pinsReply = res; havePins = true; }
            } catch (Exception e) { pinsReason = e.Message; }
        }
        if (pinsReason != null) notes.Add("the Store-app pins are not shown: " + pinsReason);
        if (blob == null) notes.Add("taskbar order unavailable: " + blobReason);

        var order = OrderNames(names, havePins ? null : blob);

        var resolved = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        string? sidecarReason = null;
        if (o.Call == null) sidecarReason = "no sidecar connection was passed to listPinned";
        else if (full.Count > 0) {
            try {
                var paths = new JsonArray(); foreach (var p in full) paths.Add(p);
                var res = await o.Call("lnk", new JsonObject { ["paths"] = paths }, TimeSpan.FromMilliseconds(8000)).ConfigureAwait(true);
                var list = TaskJson.Get(res, "items");
                if (list.ValueKind != JsonValueKind.Array) sidecarReason = "the sidecar's lnk command returned no items";
                else foreach (var it in list.EnumerateArray()) if (TaskJson.Str(it, "path") is string pp) resolved[pp] = it;
            } catch (Exception e) { sidecarReason = e.Message; }
        }
        if (sidecarReason != null && names.Count > 0) notes.Add("shortcuts read without the sidecar (" + sidecarReason + ")");

        var lnkPins = new List<Pin>();
        for (int i = 0; i < names.Count; i++) {
            string name = names[i], lnkPath = full[i];
            string? target = null, args = null, iconPathRaw = null, aumid = null, iconPng = null, reason = null;
            int? iconIndex = null;
            bool hasRec = resolved.TryGetValue(lnkPath, out var rec);
            if (hasRec && !TaskJson.IsFalse(rec, "ok")) {
                target = TaskJson.Str(rec, "target"); args = TaskJson.Str(rec, "args"); iconPathRaw = TaskJson.Str(rec, "iconPath");
                aumid = TaskJson.Str(rec, "aumid"); iconPng = TaskJson.Str(rec, "iconPng");
                if (TaskJson.Get(rec, "iconIndex").ValueKind == JsonValueKind.Number) iconIndex = (int)TaskJson.Num(rec, "iconIndex");
                if (string.IsNullOrEmpty(target) && string.IsNullOrEmpty(aumid)) reason = TaskJson.Str(rec, "reason") ?? "the sidecar found no target and no app id in this shortcut";
            } else {
                string? sidecarSaid = hasRec ? (TaskJson.Str(rec, "reason") ?? "the sidecar could not read this shortcut") : null;
                try {
                    var parsed = LnkFile.ParseFile(lnkPath);
                    target = parsed.Target; args = parsed.Args; iconPathRaw = parsed.IconPath; iconIndex = parsed.IconIndex;
                    if (string.IsNullOrEmpty(parsed.Target))
                        reason = sidecarSaid ?? (sidecarReason != null ? "no file target (Store app?) and sidecar lnk unavailable" : "no file target (Store app?)");
                } catch (Exception e) { reason = sidecarSaid ?? e.Message; }
            }

            string? t = !string.IsNullOrEmpty(target) ? ExpandVars(target, o.Env) : null;
            bool isShell = t != null && ShellTargetRe.IsMatch(t);
            string? exe = t != null && !isShell && o.IsFile(t) ? t.ToLowerInvariant() : null;
            if (t != null && exe == null && !isShell && reason == null) reason = "the app this pin points at is gone (it was uninstalled or moved)";
            var icon = SplitIconLocation(!string.IsNullOrEmpty(iconPathRaw) ? ExpandVars(iconPathRaw, o.Env) : "");
            string? iconPath = icon.IconPath;
            int idx = iconIndex ?? icon.IconIndex;
            if (string.IsNullOrEmpty(iconPath)) { iconPath = t; idx = 0; }
            lnkPins.Add(new Pin {
                Id = name.ToLowerInvariant(), Name = PinName(name, t), Kind = "lnk", LnkPath = lnkPath, Target = t,
                Args = args ?? "", Exe = exe, Aumid = string.IsNullOrEmpty(aumid) ? null : aumid,
                IconPath = iconPath, IconIndex = idx,
                IconPng = exe == null && !string.IsNullOrEmpty(iconPng) ? iconPng : null,
                Order = order.TryGetValue(name, out int ord) ? ord : i, Reason = reason,
            });
        }

        List<Pin> items;
        if (!havePins) {
            items = lnkPins.ToList();
            items.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : Compare(a.Name, b.Name));
        } else {
            var byFile = new Dictionary<string, Pin>(StringComparer.Ordinal);
            foreach (var p in lnkPins) byFile[Path.GetFileName(p.LnkPath!).ToLowerInvariant()] = p;
            var used = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<Pin>();
            var orphans = new List<string>();
            var blobItems = TaskJson.Get(pinsReply, "items").EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object)
                .Select((x, k) => (x, k)).OrderBy(t2 => TaskJson.Num(t2.x, "index")).ThenBy(t2 => t2.k).Select(t2 => t2.x).ToList();
            foreach (var it in blobItems) {
                string kind = TaskJson.Str(it, "kind") ?? "";
                if (kind == "lnk") {
                    string? lnk = TaskJson.Str(it, "lnk");
                    string key = (lnk != null ? Path.GetFileName(lnk) : (TaskJson.Str(it, "name") ?? "") + ".lnk").ToLowerInvariant();
                    if (byFile.TryGetValue(key, out var pin)) { used.Add(key); merged.Add(pin); }
                    else orphans.Add(TaskJson.Str(it, "name") ?? key);
                } else if (kind == "app" && TaskJson.Str(it, "aumid") is string am && am.Length > 0) {
                    merged.Add(new Pin {
                        Id = "app:" + am, Name = TaskJson.Str(it, "name") is string nm && nm.Length > 0 ? nm : am, Kind = "app",
                        Aumid = am, IconPng = TaskJson.Str(it, "iconPng") is string png && png.Length > 0 ? png : null,
                    });
                }
            }
            var extras = lnkPins.Where(p => !used.Contains(Path.GetFileName(p.LnkPath!).ToLowerInvariant())).ToList();
            extras.Sort((a, b) => Compare(a.Name, b.Name));
            if (extras.Count > 0)
                notes.Add("not in the saved taskbar list, shown last: " + string.Join(", ", extras.Select(p => p.Name)) +
                          " (the saved order is stale, or it was pinned another way)");
            if (orphans.Count > 0) notes.Add("pinned but no shortcut file on disk: " + string.Join(", ", orphans));
            int skipped = (int)TaskJson.Num(pinsReply, "skipped");
            string? pr = TaskJson.Str(pinsReply, "reason");
            if (skipped > 0) notes.Add(skipped + " taskbar entr" + (skipped == 1 ? "y is" : "ies are") + " not apps" + (pr != null ? ": " + pr : ""));
            else if (pr != null) notes.Add(pr);
            items = merged.Concat(extras).ToList();

            // a Store pin without icon bytes gets ONE `app` call (never two), which may also correct its name (BAR-275)
            var need = items.Where(p => p.Kind == "app" && p.IconPng == null).ToList();
            foreach (var p in need) {
                if (o.Call == null) { p.Reason = "no icon for " + p.Name + ": Mantle's helper is not running yet"; continue; }
                try {
                    var r = await o.Call("app", new JsonObject { ["aumid"] = p.Aumid }, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
                    if (!TaskJson.IsFalse(r, "ok") && TaskJson.Str(r, "iconPng") is string png && png.Length > 0) {
                        p.IconPng = png;
                        if (TaskJson.Str(r, "name") is string nm && nm.Length > 0) p.Name = nm;
                    } else p.Reason = "no icon for " + p.Name + ": " + (TaskJson.Str(r, "reason") ?? "Windows gave no icon");
                } catch (Exception e) { p.Reason = "no icon for " + p.Name + ": " + e.Message; }
            }
        }
        for (int i = 0; i < items.Count; i++) items[i].Order = i;
        if (items.Count == 0) notes.Add("nothing is pinned to the taskbar");
        return new PinnedResult(true, notes.Count > 0 ? string.Join("; ", notes) : null, items);
    }
}
