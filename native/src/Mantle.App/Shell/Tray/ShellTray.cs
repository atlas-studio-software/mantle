using System;
using Mantle.Core.Features;
using Mantle.Core.Features.Tray;
using Mantle.Core.Keys;
using Mantle.Core.State;

namespace Mantle.App.Shell.Tray;

/// <summary>
/// The shell's tray and global shortcuts, made the way the run allows (main.js createTray + registerShortcuts).
/// A --test run never draws an icon (a stand-in keeps the menu) and never registers a global shortcut (it would
/// swallow Alt+Space system-wide); a live run gets the Win32 icon and RegisterHotKey, each on a thread of its
/// own. The host-side logic is <see cref="TrayController"/> and <see cref="ShellShortcuts"/> in Mantle.Core.
/// </summary>
public static class ShellTray
{
    /// <summary>The icon for this run: <see cref="Win32TrayIcon"/> live, <see cref="StandInTrayIcon"/> in a test run.</summary>
    /// <param name="postToHost">How the icon's clicks reach the host thread (the host's Post).</param>
    public static ITrayIcon MakeIcon(bool test, Action<Action> postToHost, Action<string>? log = null) =>
        test ? new StandInTrayIcon(log) : new Win32TrayIcon(postToHost, log);

    /// <summary>The global shortcuts for this run: null in a test run (ShellShortcuts.Register then says why).</summary>
    public static IGlobalHotkeys? MakeHotkeys(bool test, Action<Action> postToHost, Action<string>? log = null) =>
        test ? null : new Win32Hotkeys(postToHost, log);

    /// <summary>
    /// Register the kill switch and Alt+Space and write the <c>shortcuts</c> piece (always, with its reason).
    /// The kill switch runs <see cref="ShellShortcuts.KillSwitchRoad"/>: stand down, the verified restore, quit.
    /// </summary>
    public static ShortcutsPiece RegisterShortcuts(IShellStateHub state, bool test, IGlobalHotkeys? keys,
        Action<string> standDown, Func<string, System.Threading.Tasks.Task> restore, Action<string> shutdown, Action toggleLauncher, Action<string>? log = null)
    {
        var piece = ShellShortcuts.Register(test, keys, ShellShortcuts.KillSwitchRoad(standDown, restore, shutdown, log), toggleLauncher);
        state.Set(s => s with { Shortcuts = piece });
        return piece;
    }
}
