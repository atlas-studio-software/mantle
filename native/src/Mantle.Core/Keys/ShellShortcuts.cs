using System;
using System.Collections.Generic;
using Mantle.Core.State;

namespace Mantle.Core.Keys;

/// <summary>System-wide shortcuts (Electron's globalShortcut; natively RegisterHotKey on the hook thread,
/// WM_HOTKEY posted to the host). Register answers false when another app owns the keys; a throw is a fault.</summary>
public interface IGlobalHotkeys
{
    bool Register(string accelerator, Action fired);
    void UnregisterAll();
}

/// <summary>
/// main.js registerShortcuts: Ctrl+Alt+Shift+Backspace (the kill switch: stand the supervisor down, run the
/// verified restore, quit) and Alt+Space (toggle the launcher). Live only: a --test run registers
/// nothing, because a global shortcut swallows its keys system-wide for as long as the run is up.
/// Writes the <c>shortcuts</c> piece with its reason in words, every time.
/// </summary>
public static class ShellShortcuts
{
    public const string KillSwitch = "Ctrl+Alt+Shift+Backspace";
    public const string Launcher = "Alt+Space";

    public static ShortcutsPiece Register(bool test, IGlobalHotkeys? keys, Action killSwitch, Action toggleLauncher)
    {
        if (test || keys == null)
            return new ShortcutsPiece(false, false, test
                ? "test mode: no global shortcuts were registered (they would swallow Alt+Space system-wide while the test runs)"
                : "Mantle has no way to register global shortcuts in this run, so the kill switch is NOT armed and Alt+Space will not open the launcher");
        var problems = new List<string>();
        var kill = false;
        var launcher = false;
        try { kill = keys.Register(KillSwitch, killSwitch); }
        catch (Exception e) { problems.Add($"{KillSwitch} threw ({e.Message})"); }
        if (!kill && problems.Count == 0) problems.Add($"{KillSwitch} is already taken by another app, so the kill switch is NOT armed");
        try { launcher = keys.Register(Launcher, toggleLauncher); }
        catch (Exception e) { problems.Add($"{Launcher} threw ({e.Message})"); }
        if (!launcher && !problems.Exists(p => p.Contains(Launcher, StringComparison.Ordinal)))
            problems.Add($"{Launcher} is already taken by another app, so it will not open the launcher");
        return new ShortcutsPiece(kill, launcher, problems.Count > 0
            ? string.Join("; ", problems)
            : $"{KillSwitch} puts Windows back, {Launcher} opens the launcher");
    }

    /// The tray icon could not be made: the shortcuts' reason says so too (the keyboard is the other way out).
    public static ShortcutsPiece NoTray(ShortcutsPiece p, string why) => p with { Reason = $"{p.Reason}; the tray icon could not be created either ({why})" };
}
