using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.Core.Platform;

namespace Mantle.Core.State;

/// One row of the problem strip: the piece and its reason (lib/state.js problems()).
public sealed record Problem(string Piece, string Reason);

/// <summary>
/// The live ShellState (lib/state.js ShellState): one immutable snapshot, replaced copy-on-write.
/// <see cref="Set"/> publishes only when something really moved; the immutable <c>Features</c>
/// dictionary is compared entry by entry, because it compares by reference. Host thread only.
/// </summary>
public sealed class ShellStateHub : IShellStateHub
{
    readonly List<Action<ShellState>> _handlers = new();
    readonly Action<string>? _log;

    public ShellStateHub(ShellState initial, Action<string>? log = null) { Current = initial; _log = log; }

    public ShellState Current { get; private set; }

    public bool Set(Func<ShellState, ShellState> change)
    {
        var next = change(Current) ?? throw new InvalidOperationException("a ShellState change returned null");
        if (Same(Current, next)) return false;
        Current = next;
        foreach (var h in _handlers.ToArray()) {
            try { h(next); }
            catch (Exception e) { _log?.Invoke($"mantle-native: a state subscriber failed ({e.GetType().Name}: {e.Message})"); }
        }
        return true;
    }

    public IDisposable OnChange(Action<ShellState> handler)
    {
        _handlers.Add(handler);
        return new Unsub(() => _handlers.Remove(handler));
    }

    /// Equal by value, the feature map entry by entry.
    public static bool Same(ShellState a, ShellState b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (!SameFeatures(a.Features, b.Features)) return false;
        return a with { Features = b.Features } == b;
    }

    static bool SameFeatures(IReadOnlyDictionary<string, FeaturePiece> a, IReadOnlyDictionary<string, FeaturePiece> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var other) || other != kv.Value) return false;
        return true;
    }

    /// <summary>lib/state.js problems(): every piece that is not healthy, with its reason, in its order.</summary>
    public static IReadOnlyList<Problem> Problems(ShellState d)
    {
        var o = new List<Problem>();
        if (d.Sidecar.Status != PlatformState.Ok) o.Add(new("sidecar", d.Sidecar.Reason));
        if (!d.Taskbar.Autohide) o.Add(new("taskbar", d.Taskbar.Reason));
        if (d.Bar.Mode == BarMode.Dock && !d.Workarea.Reserved) o.Add(new("screen space", d.Workarea.Reason));
        if (!d.Desktop.Attached) o.Add(new("desktop layer", d.Desktop.Reason));
        if (!d.Hotkey.WinTap && !d.Hotkey.Resting) o.Add(new("Win key", d.Hotkey.Reason));
        if (!d.Tray.Ok) o.Add(new("tray", d.Tray.Reason));
        if (!d.Tray.Watch) o.Add(new("taskbar watch", d.Tray.WatchReason));
        if (d.EdgeClip.On && !d.EdgeClip.Applied) o.Add(new("edge clip", d.EdgeClip.Reason));
        if (d.TbHide.On && !d.TbHide.Applied) o.Add(new("taskbar hiding", d.TbHide.Reason));
        foreach (var (who, b) in new[] { ("bar", d.Backdrop.Bar), ("launcher", d.Backdrop.Launcher), ("desktop", d.Backdrop.Desktop) })
            if (b != null && !b.Ok) o.Add(new($"{who} backdrop", b.Reason));
        if (d.Glass.Material != "acrylic-blur-behind") o.Add(new("window glass", d.Glass.Reason));
        if (!d.Shortcuts.KillSwitch || !d.Shortcuts.Launcher) o.Add(new("shortcuts", d.Shortcuts.Reason));
        if (d.Notifications is { Ok: false } n) o.Add(new("notifications", n.Reason));
        if (d.Mode != ShellMode.Test && d.Switcher is { Broken: { } broken }) o.Add(new("Alt+Tab", broken));
        foreach (var (name, p) in d.Features) {
            if (p == null || !p.On || p.Stopped || p.Resting) continue;
            if (!p.Ok) o.Add(new(p.Label.Length > 0 ? p.Label : name, p.Reason.Length > 0 ? p.Reason : "not working, and no reason given"));
            else if (p.Broken != null && d.Mode != ShellMode.Test) o.Add(new(p.Label.Length > 0 ? p.Label : name, p.Broken));
        }
        return o;
    }

    /// The one-line terminal copy of the problem list (main.js state.on('change')).
    public static string ProblemLine(IReadOnlyList<Problem> problems)
        => problems.Count == 0 ? "mantle-native: state: everything Mantle manages is working"
         : "mantle-native: state: " + string.Join("; ", problems.Select(p => $"{p.Piece}: {p.Reason}"));

    sealed class Unsub : IDisposable
    {
        Action? _undo;
        public Unsub(Action undo) { _undo = undo; }
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
