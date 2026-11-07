using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>
    /// main.js's kill switch handler, as one action: stand the supervisor down FIRST (a crash during the
    /// restore must not bring Mantle back), run the verified restore, then quit whatever the restore did.
    /// A second press while the road runs does nothing new. Every step that fails is logged and the next
    /// one still runs: the kill switch is the way out, so it never stops half way.
    /// </summary>
    public static Action KillSwitchRoad(Action<string> standDown, Func<string, Task> restore, Action<string> shutdown, Action<string>? log = null)
    {
        const string Why = "the kill switch";
        var running = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref running, 1) == 1) { log?.Invoke($"{KillSwitch}: already putting Windows back"); return; }
            log?.Invoke($"{KillSwitch}: standing the supervisor down, putting Windows back, then quitting");
            try { standDown(Why); } catch (Exception e) { log?.Invoke($"{KillSwitch}: the supervisor did not stand down ({e.Message}); putting Windows back anyway"); }
            Task t;
            try { t = restore(Why) ?? Task.CompletedTask; }
            catch (Exception e) { t = Task.FromException(e); }
            t.ContinueWith(r =>
            {
                if (r.IsFaulted) log?.Invoke($"{KillSwitch}: the restore failed ({r.Exception?.GetBaseException().Message}); quitting anyway");
                try { shutdown(Why); } catch (Exception e) { log?.Invoke($"{KillSwitch}: the quit threw ({e.Message})"); }
            }, CancellationToken.None, TaskContinuationOptions.None,
               SynchronizationContext.Current != null ? TaskScheduler.FromCurrentSynchronizationContext() : TaskScheduler.Default);
        };
    }

    /// The tray icon could not be made: the shortcuts' reason says so too (the keyboard is the other way out).
    public static ShortcutsPiece NoTray(ShortcutsPiece p, string why) => p with { Reason = $"{p.Reason}; the tray icon could not be created either ({why})" };
}
