using System.Collections.Generic;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Features.Desktop;

/// <summary>One "Needs attention" row: bold title, then " — " and the reason.</summary>
public sealed record DeskRow(string Title, string Reason);

/// <summary>
/// The desktop's "Needs attention" strip (ui/desktop/desktop.js PIECES / renderState), pure, over the typed ShellState.
/// Electron's strip and lib/state.js problems() were two copies of one health rule that had drifted (the strip lacked
/// the taskbar watch, edge clip, taskbar hiding, bar / launcher backdrop and notifications rows, and showed a RESTING Win
/// key). Here it is one rule: the strip shows every row ShellStateHub.Problems has, in the strip's own order and words,
/// plus its own wallpaper and pane-backdrop rows.
/// </summary>
public static class DeskStrip
{
    public const string NoReason = "no reason given, which is itself a bug";

    /// The strip shows the same thing as before (same rows in the same order, same test badge): no rebuild, no
    /// re-layout, no log line. Every ShellState change reaches the layer; most of them are not about the strip.
    public static bool Same(IReadOnlyList<DeskRow>? before, bool beforeTest, IReadOnlyList<DeskRow> now, bool nowTest)
    {
        if (before == null || beforeTest != nowTest || before.Count != now.Count) return false;
        for (int i = 0; i < now.Count; i++) if (!Equals(before[i], now[i])) return false;
        return true;
    }

    /// <param name="hasWall">Mantle's own wallpaper plays in the page: the pane-backdrop row is not said.</param>
    /// <param name="localBackdrop">The page's own picture failure, said only when main is not already saying it.</param>
    public static IReadOnlyList<DeskRow> Rows(ShellState s, bool hasWall = false, string? localBackdrop = null)
    {
        var o = new List<DeskRow>();
        bool test = s.Mode == ShellMode.Test;
        static string R(string? r) => string.IsNullOrEmpty(r) ? NoReason : r;
        if (s.Sidecar.Status != PlatformState.Ok) o.Add(new("Sidecar", R(s.Sidecar.Reason)));
        if (!s.Taskbar.Autohide) o.Add(new("Windows taskbar", R(s.Taskbar.Reason)));
        if (s.TbHide.On && !s.TbHide.Applied) o.Add(new("Taskbar hiding", R(s.TbHide.Reason)));
        if (s.Bar.Mode == BarMode.Dock && !s.Workarea.Reserved) o.Add(new("Screen space for the bar", R(s.Workarea.Reason)));
        if (s.EdgeClip.On && !s.EdgeClip.Applied) o.Add(new("Edge clip", R(s.EdgeClip.Reason)));
        if (!s.Desktop.Attached) o.Add(new("Desktop layer", R(s.Desktop.Reason)));
        if (!s.Hotkey.WinTap && !s.Hotkey.Resting) o.Add(new("Win key", R(s.Hotkey.Reason)));
        if (!s.Tray.Ok) o.Add(new("Notification area", R(s.Tray.Reason)));
        if (!s.Tray.Watch) o.Add(new("Taskbar watch", R(s.Tray.WatchReason)));
        if (s.Wallpaper is { } w) {
            if (!w.RulesOk) o.Add(new("Wallpaper pause rules", string.IsNullOrEmpty(w.RulesReason) ? "not being checked, and no reason given" : w.RulesReason));
            if (!w.Ok) o.Add(new("Wallpaper", R(w.Reason)));
        }
        if (s.Glass.Material != "acrylic-blur-behind") o.Add(new("Window glass", R(s.Glass.Reason)));
        if (!s.Shortcuts.KillSwitch || !s.Shortcuts.Launcher) o.Add(new("Shortcuts", R(s.Shortcuts.Reason)));
        if (s.Backdrop.Bar is { Ok: false } bb) o.Add(new("Bar backdrop", R(bb.Reason)));
        if (s.Backdrop.Launcher is { Ok: false } lb) o.Add(new("Launcher backdrop", R(lb.Reason)));
        bool saidBackdrop = false;
        if (!hasWall && s.Backdrop.Desktop is { Ok: false } db) { o.Add(new("Pane backdrop", R(db.Reason))); saidBackdrop = true; }
        if (s.Notifications is { Ok: false } n) o.Add(new("Notifications", R(n.Reason)));
        if (!test && s.Switcher is { Broken: { Length: > 0 } broken }) o.Add(new("Alt+Tab", broken));
        foreach (var (name, p) in s.Features) {
            if (p == null || !p.On || p.Stopped || p.Resting) continue;
            string title = p.Label.Length > 0 ? p.Label : name;
            if (!p.Ok) o.Add(new(title, string.IsNullOrEmpty(p.Reason) ? "not working, and no reason given" : p.Reason));
            else if (p.Broken != null && !test) o.Add(new(title, p.Broken));
        }
        if (!string.IsNullOrEmpty(localBackdrop) && !saidBackdrop && !hasWall) o.Add(new("Pane backdrop", localBackdrop));
        return o;
    }

    /// The heading's count note: "· 3 things", "· test mode · 1 thing".
    public static string Count(int n, bool test) => "· " + (test ? "test mode · " : "") + n + (n == 1 ? " thing" : " things");

    /// No Mantle bridge (Electron's preload failed): the one fatal row. Natively the surface always has its host, so
    /// this is said only when the host context is missing.
    public const string NoBridge = "The Mantle bridge is missing — the desktop layer has no host context, so it cannot read anything from Mantle.";
}
