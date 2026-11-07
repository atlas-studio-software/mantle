// Chips.cs - the bar's problem list (ui/bar/bar.js chipData / fail / renderChips). On the island the
// strip is gone: ONE pip on the "more" button stands in for every chip, warn-coloured when any chip is a
// warning, and its tooltip says each one in words (bar.css .more .mpip). The edge bar's strip is round 3.
using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.App.Surfaces.Bar.Frame;

internal sealed record Chip(string Key, string Label, string Reason, string Tone);

internal sealed class Chips
{
    readonly Dictionary<string, Chip> _failures = new();
    string _sig = "";

    /// bar.js fail(): re-reporting the SAME failure is a no-op; returns true when the list changed.
    public bool Fail(string key, string label, string reason, string tone = "")
    {
        var c = new Chip(key, label, Cap(reason), tone);
        if (_failures.TryGetValue(key, out var was) && was == c) return false;
        _failures[key] = c;
        return true;
    }

    public bool Clear(string key) => _failures.Remove(key);

    /// bar.js chipData(): failures first (a dead bridge reads first), then what the shell state says.
    public List<Chip> List(ShellState? s)
    {
        var o = _failures.Values.OrderBy(c => c.Key == "bridge" ? 0 : 1).ToList();
        if (s == null) return o;
        if (s.Sidecar.Status != PlatformState.Ok)
            o.Add(s.Sidecar.Status == PlatformState.Starting
                ? new Chip("sidecar", "Starting up", "Mantle is still connecting to Windows; apps, sound and network fill in within a few seconds", "")
                : new Chip("sidecar", "Windows link lost", "Mantle cannot reach Windows right now, so apps, sound and network may not update. If it does not come back, restart Mantle from this menu", "warn"));
        if (s.Mode == ShellMode.Live && !s.Taskbar.Hidden)
            o.Add(new Chip("taskbar", "Windows taskbar showing", "Windows would not let Mantle hide its taskbar. Restarting Mantle usually fixes this", ""));
        if (!s.Desktop.Attached)
            o.Add(new Chip("desktop", "Desktop not attached", "Mantle's desktop could not sit on the wallpaper, so desktop icons may not show. Restarting Mantle usually fixes this", ""));
        if (!s.Hotkey.WinTap && s.Mode != ShellMode.Test)
            o.Add(new Chip("hotkey", "Windows key not taken", "Tapping the Windows key opens Windows' Start, not Mantle's launcher. Restarting Mantle usually fixes this", ""));
        if (!string.IsNullOrEmpty(s.Bar.Reason) && !s.Bar.Ok && s.Bar.Reason != ShellState.NotStartedYet)
            o.Add(new Chip("bar", "Bar may not hide", "The bar may stay up instead of tucking away. Restarting Mantle usually fixes this", ""));
        if (!s.Tray.Watch && s.Tray.Ok)
            o.Add(new Chip("traywatch", "Tray icons not updating", "New or changed tray icons will not show until Mantle restarts", ""));
        return o;
    }

    /// renderChips(): (shown, warn, tooltip) for the pip, or null when nothing changed since the last call.
    public (bool Shown, bool Warn, string Tip)? Pip(ShellState? s)
    {
        var l = List(s);
        string sig = string.Join("\u0001", l.Select(c => c.Tone + "|" + c.Label + "|" + c.Reason));
        if (sig == _sig) return null;
        _sig = sig;
        return (l.Count > 0, l.Any(c => c.Tone == "warn"), string.Join("\n", l.Select(c => c.Label + ": " + c.Reason)));
    }

    static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// notifications.js update(): the notification list could not be read -> 'notifications off' (warn). The
    /// sidecar's Notifications.cs words; the notification centre itself is a later round.
    public static Chip? NotificationAccess()
    {
        try
        {
            var a = global::Windows.UI.Notifications.Management.UserNotificationListener.Current.GetAccessStatus();
            if (a == global::Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed) return null;
            return new Chip("notifs", "notifications off", a == global::Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Denied
                ? "Windows is not letting apps read your notifications (Settings > Privacy & security > Notifications > Let apps access your notifications is off)"
                : "Windows has not been asked whether Mantle may read your notifications, and Mantle never asks (it would pop a prompt): turn on Settings > Privacy & security > Notifications > Let apps access your notifications", "warn");
        }
        catch (Exception e)
        {
            return new Chip("notifs", "notifications off", "Windows would not say whether Mantle may read notifications (" + e.GetType().Name + ")", "warn");
        }
    }
}
