using System;
using System.Collections.Immutable;
using Mantle.Core.Platform;

namespace Mantle.Core.State;

// The one honest picture of what Mantle has and has not managed to do (lib/state.js). Every piece
// carries a plain-words reason at all times, including when it is fine, and the whole snapshot is
// pushed to every surface whenever any of it changes: nothing may fail silently, and a log line does
// not count. The record shape follows lib/state.js initialState() plus the pieces other modules set
// (features, switcher, notifications, wallpaper), with Electron's key names in PascalCase.
// Contract only: native-core owns ShellState and implements the hub (and the problems() strip);
// other lanes write their own pieces through IShellStateHub.Set.
//
// Threading: snapshots are immutable and safe on any thread. Set and OnChange are host-thread only;
// surfaces receive snapshots on their UI thread through their surface context.

public enum ShellMode { Live, Test }

/// The screen edge the bar sits on (--bar-edge); pop-ups open away from it.
public enum BarEdge { Bottom, Top }

public enum BarMode { Hover, Dock }

public enum BarShape { Pill, Edge }

/// <c>sidecar: { status: 'ok'|'starting'|'down', reason }</c>
public sealed record SidecarPiece(PlatformState Status, string Reason);

/// <summary><c>taskbar: { autohide, wasAutohide, hidden, reason }</c>.</summary>
/// <remarks>Mantle flips the OS auto-hide flag and never hides the taskbar window (the 2026-09-21
/// incident). <see cref="WasAutohide"/> is what Windows said before Mantle touched it (null = not
/// read yet). <see cref="Hidden"/> is a mirror of <see cref="Autohide"/>, never set on its own.</remarks>
public sealed record TaskbarPiece(bool Autohide, bool? WasAutohide, string Reason)
{
    public bool Hidden => Autohide;
}

/// <c>workarea: { reserved, reason }</c>: the app bar's screen space (dock mode).
public sealed record WorkareaPiece(bool Reserved, string Reason);

/// <c>desktop: { attached, mode, reason }</c>: the desktop layer under the icons.
public sealed record DesktopPiece(bool Attached, string? Mode, string Reason);

/// <c>hotkey: { winTap, reason, resting? }</c>. Resting: the Win key is Windows' on purpose while a game runs.
public sealed record HotkeyPiece(bool WinTap, string Reason, bool Resting = false);

/// <c>tray: { status: 'ok'|'unavailable', reason, watch: 'on'|'off', watchReason }</c>
public sealed record TrayPiece(bool Ok, string Reason, bool Watch, string WatchReason);

/// <c>bar: { mode, shape, revealed, reveal, ok, reason }</c> (main.js adds reveal and ok to lib/state.js's four).
public sealed record BarPiece(BarMode Mode, BarShape Shape, bool Revealed, string Reason)
{
    /// What reveals the bar in hover mode (main.js REVEAL_BY), null until the bar says.
    public string? Reveal { get; init; }
    /// The bar window is up and placed.
    public bool Ok { get; init; }
}

/// <c>edgeclip: { on, applied, reason }</c>
public sealed record EdgeClipPiece(bool On, bool Applied, string Reason);

/// <c>tbhide: { on, applied, reason }</c>
public sealed record TbHidePiece(bool On, bool Applied, string Reason);

/// One surface's captured backdrop: <c>{ on, ok, fps, ms, reason }</c>.
public sealed record BackdropUse(bool On, bool Ok, double Fps, double Ms, string Reason);

/// <c>backdrop: { bar, launcher, desktop }</c>
public sealed record BackdropPiece(BackdropUse Bar, BackdropUse Launcher, BackdropUse Desktop);

/// <c>glass: { material: 'acrylic-blur-behind'|'none', reason }</c>
public sealed record GlassPiece(string Material, string Reason);

/// <c>shortcuts: { killSwitch, launcher, reason }</c>: a global shortcut another app owns is said, not hidden.
public sealed record ShortcutsPiece(bool KillSwitch, bool Launcher, string Reason);

/// <c>switcher.want: { altTab, winKeys }</c>: the user's tray switches.
public sealed record SwitcherWant(bool AltTab, bool WinKeys);

/// <c>switcher: { want, page, altTab, reason, winKeys, winKeysReason, broken }</c> (lib/switcher.js; absent until it starts).
/// Page: 'ready' or 'down'. Broken: plain words for a switch left on whose feature is Windows' own anyway, else null.
public sealed record SwitcherPiece(SwitcherWant Want, string Page, bool AltTab, string Reason, bool WinKeys, string WinKeysReason, string? Broken);

/// <c>notifications: { status: 'ok'|'down', count, unread, reason, signal }</c> (lib/notifications.js; absent until it reads).
public sealed record NotificationsPiece(bool Ok, int Count, int Unread, string Reason, string? Signal);

/// <c>wallpaper: { ok, id, title, paused, reason, rulesOk, rulesReason }</c> (lib/wallpaper.js; absent until it starts).
public sealed record WallpaperPiece(bool Ok, string? Id, string? Title, bool Paused, string Reason, bool RulesOk, string RulesReason);

/// <summary><c>features.&lt;name&gt;: { label, on, ok, reason, keys, keysReason, broken, stopped }</c> (lib/features.js).</summary>
/// <param name="On">Its tray switch.</param>
/// <param name="Ok">Its own health (or why it failed to load or install), with <paramref name="Reason"/>.</param>
/// <param name="Keys">Its chords are Mantle's right now (<paramref name="KeysReason"/> says why or why not).</param>
/// <param name="Broken">Plain words for chords that should be Mantle's and are not, else null.</param>
/// <param name="Stopped">Its stop() ran (a rest is not a stop).</param>
/// <param name="Resting">Stopped on purpose while a game runs: healthy, no strip row.</param>
public sealed record FeaturePiece(string Label, bool On, bool Ok, string Reason, bool Keys, string KeysReason, string? Broken, bool Stopped, bool Resting = false);

/// <summary>The whole snapshot. Replace pieces with <c>with</c>; never mutate.</summary>
/// <remarks>Records compare by value, except <see cref="Features"/>, an immutable dictionary that
/// compares by reference: the hub compares it entry by entry when it decides whether anything changed.</remarks>
public sealed record ShellState(
    ShellMode Mode,
    BarEdge Edge,
    SidecarPiece Sidecar,
    TaskbarPiece Taskbar,
    WorkareaPiece Workarea,
    DesktopPiece Desktop,
    HotkeyPiece Hotkey,
    TrayPiece Tray,
    BarPiece Bar,
    EdgeClipPiece EdgeClip,
    TbHidePiece TbHide,
    BackdropPiece Backdrop,
    GlassPiece Glass,
    ShortcutsPiece Shortcuts,
    ImmutableSortedDictionary<string, FeaturePiece> Features,
    SwitcherPiece? Switcher = null,
    NotificationsPiece? Notifications = null,
    WallpaperPiece? Wallpaper = null)
{
    /// lib/state.js PLAIN_UNKNOWN: the reason of a piece nobody has set yet.
    public const string NotStartedYet = "not started yet";

    /// lib/state.js initialState(mode, edge), value for value.
    public static ShellState Initial(ShellMode mode, BarEdge edge)
    {
        var none = new BackdropUse(false, true, 0, 0, NotStartedYet);
        return new ShellState(
            mode,
            edge,
            new SidecarPiece(PlatformState.Starting, "sidecar has not answered yet"),
            new TaskbarPiece(false, null, NotStartedYet),
            new WorkareaPiece(false, NotStartedYet),
            new DesktopPiece(false, null, NotStartedYet),
            new HotkeyPiece(false, NotStartedYet),
            new TrayPiece(false, NotStartedYet, false, NotStartedYet),
            new BarPiece(BarMode.Hover, BarShape.Pill, false, NotStartedYet),
            new EdgeClipPiece(false, false, NotStartedYet),
            new TbHidePiece(false, false, NotStartedYet),
            new BackdropPiece(none, none, none),
            new GlassPiece("none", NotStartedYet),
            new ShortcutsPiece(false, false, NotStartedYet),
            ImmutableSortedDictionary<string, FeaturePiece>.Empty.WithComparers(StringComparer.Ordinal));
    }
}

/// <summary>The live ShellState: one writer at a time (the host thread), every surface a reader.</summary>
public interface IShellStateHub
{
    /// The current snapshot (the same object until something changes).
    ShellState Current { get; }

    /// <summary>Apply a change (<c>s =&gt; s with { Bar = s.Bar with { Revealed = true } }</c>).</summary>
    /// <returns>False, and nothing is published, when the result equals the current snapshot.</returns>
    bool Set(Func<ShellState, ShellState> change);

    /// Every new snapshot, after each Set that changed something.
    IDisposable OnChange(Action<ShellState> handler);
}
