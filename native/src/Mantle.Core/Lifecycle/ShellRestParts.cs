using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Features;

namespace Mantle.Core.Lifecycle;

/// <summary>What the shell's rest parts read and do; every member is safe to call from the host thread.</summary>
public sealed class ShellRestPartsOptions
{
    /// The catalog surfaces whose windows exist now (a snapshot the surface host keeps; any thread).
    public required Func<IReadOnlyList<string>> Made { get; init; }
    /// The bar's window is open (it is kept during a rest, as Electron keeps it).
    public Func<bool> BarOpen { get; init; } = () => true;
    /// Hide the launcher at once (posted to the UI thread). The surfaces' own rest destroys it right after, in the same
    /// UI queue, when "mantle.rest" arrives.
    public Action<string>? HideLauncher { get; init; }
    /// The switcher overlay rests (why) or comes back (null; true when it was made again). None in this build: false.
    public Func<string?, bool>? Switcher { get; init; }
    /// The wallpaper rests (why; true when one was showing) or resumes (null). None in this build: false.
    public Func<string?, bool>? Wallpaper { get; init; }
    /// Mantle's own memory now, in MB (private, working set).
    public Func<(long Private, long WorkingSet)>? Memory { get; init; }
    /// Mantle's own process ids, never the sidecar's (one process in the native app).
    public Func<IReadOnlyList<int>>? Pids { get; init; }
    /// After every rest or wake (the hover poll a game held).
    public Action? AfterRest { get; init; }
}

/// <summary>
/// IRestParts for the native shell (main.js restNow / wakeNow's window half). The launcher and every other catalog
/// surface are destroyed by the surface host when it hears "mantle.rest" (SurfaceSet.Rest) and the AtStart ones are
/// made again on the wake (SurfaceSet.Wake), so this class only keeps the coordinator's view straight:
/// <list type="bullet">
/// <item>Pages: "bar" plus the surfaces made now, so "closed N windows (...)" names what the rest really destroyed;</item>
/// <item>the launcher: alive while it is made and no rest took it; DestroyLauncher hides it at once (the Win key goes
/// back to Windows' Start); MakeLauncher marks it back (the wake's SurfaceSet.Wake makes it, hidden).</item>
/// </list>
/// Host thread.
/// </summary>
public sealed class ShellRestParts : IRestParts
{
    public const string Launcher = "launcher";
    readonly ShellRestPartsOptions _o;
    bool _launcherDown;

    public ShellRestParts(ShellRestPartsOptions o) => _o = o ?? throw new ArgumentNullException(nameof(o));

    IReadOnlyList<string> Made() { try { return _o.Made() ?? Array.Empty<string>(); } catch (Exception) { return Array.Empty<string>(); } }

    public IReadOnlyList<string> Pages()
    {
        var l = new List<string>();
        bool bar; try { bar = _o.BarOpen(); } catch (Exception) { bar = false; }
        if (bar) l.Add("bar");
        l.AddRange(Made());
        return l;
    }

    public (long Private, long WorkingSet) Memory() => _o.Memory?.Invoke() ?? Own();

    /// This process's private bytes and working set in MB.
    public static (long Private, long WorkingSet) Own()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        p.Refresh();
        return (p.PrivateMemorySize64 / 1048576, p.WorkingSet64 / 1048576);
    }

    /// Made now and not taken by a rest. A launcher the wake has made again is still "not alive" to the coordinator
    /// until it asks for it (MakeLauncher), so the Win key is always handed back on the wake.
    public bool LauncherAlive => !_launcherDown && Made().Contains(Launcher);

    public void DestroyLauncher()
    {
        _launcherDown = true;
        _o.HideLauncher?.Invoke("Mantle rests for a game");
    }

    public void MakeLauncher() => _launcherDown = false;

    public bool SwitcherRest(string? why) => _o.Switcher?.Invoke(why) ?? false;
    public bool WallpaperRest(string? why) => _o.Wallpaper?.Invoke(why) ?? false;
    public IReadOnlyList<int> OwnPids() => _o.Pids?.Invoke() ?? new[] { Environment.ProcessId };
    public void AfterRest() => _o.AfterRest?.Invoke();
}
