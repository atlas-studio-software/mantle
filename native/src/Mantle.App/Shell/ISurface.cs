using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Logging;
using Mantle.Core.State;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Shell;

// A surface is one piece of Mantle's own UI: the bar, the top bar, the launcher, the switcher, the
// OSD, the toasts, the desktop layer, and later the app windows (Settings, Files, Task Manager, ...).
// It is the native form of ui/<surface>/ (PLAN.md section 3.5). A surface only renders: every action
// is a command posted to the host, and everything it shows comes from ShellState snapshots and host
// events. Contract only: the host side (creating surfaces from the catalog, the surface context) is
// native-core's; each surface is its own lane's (the bar frame is native-windows' in round 2).
//
// Threading: every ISurface member is called on the surface's own UI thread (the shell UI thread for
// the shell surfaces), and every ISurfaceContext handler is called there too. A surface never blocks
// that thread on the host or the platform.

/// <summary>
/// Marks a class as a surface. Mantle.Generators (native-core) emits a <see cref="SurfaceDescriptor"/>
/// for each one, so adding a surface never edits a shared list file.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SurfaceAttribute : Attribute
{
    public SurfaceAttribute(string name) { Name = name; }

    /// Electron's name for it (<c>bar</c>, <c>topbar</c>, <c>launcher</c>, <c>osd</c>, ...): it is the
    /// caller name of its commands, the prefix of its log lines and the key of its parity rows.
    public string Name { get; }

    /// Created when Mantle starts (the bar, the desktop layer), not on first use. A surface that must
    /// open instantly (the launcher) is created at start and kept parked, hidden, as Electron does.
    public bool AtStart { get; set; }
}

/// <summary>What the host knows about a surface before it exists.</summary>
/// <param name="Create">A new instance (called on the UI thread the surface will live on).</param>
public sealed record SurfaceDescriptor(string Name, bool AtStart, Func<ISurface> Create);

/// <summary>One surface. Created once, then shown and hidden; destroyed on a rest or at quit.</summary>
public interface ISurface
{
    /// Build the window(s), hidden. In <c>--test</c> every window follows TestQuiet (alpha 0,
    /// click-through, never activated, out of the app lists).
    void Create(ISurfaceContext ctx);

    /// Show it (or bring it to the front). <paramref name="why"/> is for the log ("Win tap", "hover").
    void Show(string why);

    /// Hide it, keeping it parked for the next show.
    void Hide(string why);

    /// Is it on screen right now?
    bool Shown { get; }

    /// Destroy its windows and let go of everything (a rest while a game runs, or quit).
    void Destroy(string why);
}

/// <summary>What a surface gets from the host. Every member is for the surface's own UI thread.</summary>
public interface ISurfaceContext
{
    /// The surface's name (from its attribute).
    string Name { get; }

    /// <c>--test</c>: never put a window in front of the user.
    bool Test { get; }

    /// The surface's UI thread.
    DispatcherQueue Dispatcher { get; }

    ILogSink Log { get; }

    /// The latest snapshot this UI thread has been given.
    ShellState State { get; }

    /// Each new snapshot, on this UI thread (coalesced: a burst of changes may arrive as its last snapshot only).
    IDisposable OnState(Action<ShellState> handler);

    /// Run a host command: posted to the host thread, never blocking this one, resuming on this UI thread.
    /// The caller name is the surface's name.
    ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null);

    /// A host event, re-posted to this UI thread, with the current value at once if it has a replay source.
    IDisposable On<T>(string ev, Action<T> handler);
}
