using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mantle.Core.Features.WindowList;

/// One window of the sidecar's list (sidecar/Windows.cs Build: hwnd, title, exe, pid, aumid, host, minimized,
/// foreground, icon, iconFrom, iconPending), after main.js#withoutMantle.
public sealed record WinEntry(string Hwnd, string Title, string? Exe, int Pid, string? Aumid, string? Host,
    bool Minimized, bool Foreground, string? Icon, string? IconFrom, bool IconPending)
{
    public static WinEntry? From(JsonElement w)
    {
        if (w.ValueKind != JsonValueKind.Object) return null;
        string? hwnd = TaskJson.Str(w, "hwnd");
        if (string.IsNullOrEmpty(hwnd)) return null;
        return new WinEntry(hwnd, TaskJson.Str(w, "title") ?? "", NullIfEmpty(TaskJson.Str(w, "exe")), (int)TaskJson.Num(w, "pid"),
            NullIfEmpty(TaskJson.Str(w, "aumid")), NullIfEmpty(TaskJson.Str(w, "host")), TaskJson.Bool(w, "minimized"), TaskJson.Bool(w, "foreground"),
            NullIfEmpty(TaskJson.Str(w, "icon")), NullIfEmpty(TaskJson.Str(w, "iconFrom")), TaskJson.Bool(w, "iconPending"));
    }
    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// One window as the bar keeps it on an item (main.js#groupRunning's window record; the pid rides along for Keep
/// on top's handle-reuse guard, BAR-299).
public sealed record TaskWindow(string Hwnd, int Pid, string Title, bool Active, bool Minimized);

/// One app's windows (main.js#groupRunning's group): keyed AUMID first, exe second (BAR-287).
public sealed class RunGroup
{
    public string Key = "";
    public string? Aumid;
    public string? Exe;
    public string? ExeRaw;
    public string? Host;
    public string? Icon;
    public bool IconPending;
    public string Title = "";
    public readonly List<TaskWindow> Windows = new();
}

public static class RunningWindows
{
    public const string MantleAumid = "com.owensilva.mantle";

    /// main.js MANTLE_TITLES (the sidecar's AltTab.cs MantleTitles has the same list), lower case.
    static readonly HashSet<string> MantleTitles = new(StringComparer.Ordinal) {
        "mantle bar", "mantle desktop", "mantle launcher", "mantle wallpapers", "mantle switcher",
        "mantle task view", "mantle volume", "mantle clipboard", "mantle snap", "mantle screenshot", "mantle top bar",
    };
    /// Mantle's APP windows: listed on the bar like any app's (MANTLE_APP_TITLES).
    static readonly HashSet<string> MantleAppTitles = new(StringComparer.Ordinal) { "Mantle Settings", "Mantle Task Manager", "Mantle Files", "Mantle Player" };

    /// <summary>
    /// Mantle's own shell windows are never running apps (main.js#isMantleWindow, BAR-290): the AUMID, or
    /// electron.exe with one of Mantle's window titles (electron.exe alone is not enough: the user's browser is one).
    /// Native adds its own exe: MantleNative.exe with a shell title or a pop-up's ("Mantle pop-up: ..."), so a test
    /// instance never lists the live shell, nor the reverse (the sidecar only skips its own parent's windows).
    /// </summary>
    public static bool IsMantleWindow(WinEntry w)
    {
        string exe = w.Exe != null ? Path.GetFileName(w.Exe).ToLowerInvariant() : "";
        if (exe == "electron.exe" && MantleAppTitles.Contains(w.Title)) return false;
        if (w.Aumid != null && w.Aumid.Equals(MantleAumid, StringComparison.OrdinalIgnoreCase)) return true;
        string t = w.Title.Trim().ToLowerInvariant();
        if (exe == "electron.exe") return MantleTitles.Contains(t);
        if (exe == "mantlenative.exe") return MantleTitles.Contains(t) || t.StartsWith("mantle pop-up", StringComparison.Ordinal);
        return false;
    }

    /// The sidecar's list in, Mantle's own windows out (main.js#withoutMantle). Null when it is not a list.
    public static List<WinEntry>? WithoutMantle(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var outList = new List<WinEntry>();
        foreach (var w in list.EnumerateArray())
            if (WinEntry.From(w) is WinEntry e && !IsMantleWindow(e)) outList.Add(e);
        return outList;
    }

    /// The running windows, one group per app, in the sidecar's order (main.js#groupRunning).
    public static List<RunGroup> Group(IEnumerable<WinEntry> list)
    {
        var groups = new Dictionary<string, RunGroup>(StringComparer.Ordinal);
        var order = new List<RunGroup>();
        foreach (var w in list) {
            string? aumid = TaskJson.Low(w.Aumid), exe = TaskJson.Low(w.Exe);
            string key = aumid ?? exe ?? "hwnd:" + w.Hwnd;
            if (!groups.TryGetValue(key, out var g)) {
                g = new RunGroup { Key = key, Aumid = aumid, Exe = exe, ExeRaw = w.Exe, Host = w.Host, Icon = w.Icon, Title = w.Title };
                groups[key] = g; order.Add(g);
            }
            if (g.Icon == null && w.Icon != null) g.Icon = w.Icon;
            g.IconPending = g.Icon == null && (g.IconPending || w.IconPending);
            // a group learns its other identifier from a later window
            if (g.Aumid == null && aumid != null) g.Aumid = aumid;
            if (g.Exe == null && exe != null) { g.Exe = exe; g.ExeRaw = w.Exe; }
            if (g.Host == null && w.Host != null) g.Host = w.Host;
            g.Windows.Add(new TaskWindow(w.Hwnd, w.Pid, w.Title, w.Foreground, w.Minimized));
        }
        return order;
    }

    /// A readable name for a running app no pin claimed (main.js nameForExe): "notepad.exe" -> "Notepad".
    public static string NameForExe(string? exe, string? fallback)
    {
        if (string.IsNullOrEmpty(exe)) return string.IsNullOrEmpty(fallback) ? "Unnamed" : fallback;
        string b = Path.GetFileName(exe);
        if (b.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) b = b[..^4];
        return b.Length > 0 ? char.ToUpperInvariant(b[0]) + b[1..] : (string.IsNullOrEmpty(fallback) ? "Unnamed" : fallback);
    }

    /// bar.js isActive: a minimized window is never the active one, whatever the flat list says.
    public static bool IsActive(TaskWindow w) => w.Active && !w.Minimized;

    /// The sorted hwnd signature (bar.js renderWindows): a change re-reads the strip (BAR-261).
    public static string HwndSignature(IEnumerable<WinEntry> list)
        => string.Join(",", list.Select(w => w.Hwnd).OrderBy(h => h, StringComparer.Ordinal));
}
