using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Mantle.Core.Logging;
using Microsoft.Win32;

namespace Mantle.Core.Look;

/// <summary>
/// Watches Windows' app mode (HKCU\...\Themes\Personalize) with RegNotifyChangeKeyValue, so Match Windows follows a
/// change at once (LOOK-016; the 1 s target of LOOK-025) without polling. Electron gets the same signal as
/// nativeTheme 'updated'. The wait runs on its own background thread; <c>changed</c> is called there, and the
/// caller posts to the host thread. Reads only: nothing here writes the registry.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsThemeWatcher : IDisposable
{
    const uint RegNotifyChangeName = 0x1, RegNotifyChangeLastSet = 0x4;

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(IntPtr hKey, int bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, int fAsynchronous);

    readonly ManualResetEvent _stop = new(false);
    readonly Thread? _thread;

    /// Null while watching, else why the watch could not start or stopped (plain words).
    public string? Problem { get; private set; }

    /// <param name="changed">called (on the watcher's thread) after every change under the key</param>
    /// <param name="subKey">the key under HKCU; tests pass a scratch key of their own</param>
    public WindowsThemeWatcher(Action changed, ILogSink? log = null, string subKey = ThemeCore.PersonalizeKey)
    {
        RegistryKey? key;
        try { key = Registry.CurrentUser.OpenSubKey(subKey, false); }
        catch (Exception e) { key = null; Problem = "Windows' theme key could not be opened (" + e.Message + ")"; }
        if (key == null) {
            Problem ??= "Windows' theme key is missing, so Match Windows is read only at start and on each change Mantle makes";
            log?.Line("theme: " + Problem);
            return;
        }
        _thread = new Thread(() => Loop(key, changed, log)) { IsBackground = true, Name = "mantle-theme-watch" };
        _thread.Start();
    }

    void Loop(RegistryKey key, Action changed, ILogSink? log)
    {
        using var k = key;
        using var ev = new AutoResetEvent(false);
        var waits = new WaitHandle[] { ev, _stop };
        while (true) {
            int rc = RegNotifyChangeKeyValue(k.Handle.DangerousGetHandle(), 0, RegNotifyChangeName | RegNotifyChangeLastSet, ev.SafeWaitHandle.DangerousGetHandle(), 1);
            if (rc != 0) {
                Problem = "watching Windows' theme stopped (error " + rc + ")";
                log?.Line("theme: " + Problem);
                return;
            }
            if (WaitHandle.WaitAny(waits) == 1) return;
            try { changed(); } catch (Exception e) { log?.Line("theme: following a Windows change threw: " + e.Message); }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(2000);
        _stop.Dispose();
    }
}

/// <summary>THE call native-core wires once at start (it owns App.xaml.cs): Match Windows follows Windows live.</summary>
public static class ThemeFollow
{
    /// <summary>Start following Windows' app mode: every change re-reads it on the host thread through
    /// <see cref="ThemeCore.WindowsChanged"/>, which tells the theme listeners only when the view moved.</summary>
    /// <param name="post">runs an action on the host thread (the host's dispatcher)</param>
    /// <returns>stop following (at quit)</returns>
    [SupportedOSPlatform("windows")]
    public static IDisposable Start(ThemeCore theme, Action<Action> post, ILogSink log, string subKey = ThemeCore.PersonalizeKey)
        => new WindowsThemeWatcher(() => post(() => theme.WindowsChanged()), log, subKey);

    /// <summary>The same, for a caller that guards its theme core itself (LookShell): <paramref name="changed"/> runs on the host thread.</summary>
    [SupportedOSPlatform("windows")]
    public static IDisposable Start(Action changed, Action<Action> post, ILogSink log, string subKey = ThemeCore.PersonalizeKey)
        => new WindowsThemeWatcher(() => post(changed), log, subKey);
}
