using System;
using System.Collections.Generic;
using Mantle.Core.Platform;
using Mantle.Core.State;
using Xunit;

namespace Mantle.Core.Tests.State;

/// ShellState has lib/state.js's shape and initial values, every piece carries a reason, and the hub
/// contract (one writer, change-only publishing) can be met by a minimal test double.
public class ShellStateContractTests
{
    [Fact]
    public void Initial_matches_lib_state_js_initialState()
    {
        var s = ShellState.Initial(ShellMode.Test, BarEdge.Bottom);
        Assert.Equal(ShellMode.Test, s.Mode);
        Assert.Equal(new SidecarPiece(PlatformState.Starting, "sidecar has not answered yet"), s.Sidecar);
        Assert.Equal(new TaskbarPiece(false, null, "not started yet"), s.Taskbar);
        Assert.False(s.Taskbar.Hidden);
        Assert.Equal(new TrayPiece(false, "not started yet", false, "not started yet"), s.Tray);
        Assert.Equal(new BarPiece(BarMode.Hover, BarShape.Pill, false, "not started yet"), s.Bar);
        Assert.True(s.Backdrop.Bar.Ok);
        Assert.False(s.Backdrop.Desktop.On);
        Assert.Equal("none", s.Glass.Material);
        Assert.Empty(s.Features);
        Assert.Null(s.Switcher);
        Assert.Null(s.Notifications);
        Assert.Null(s.Wallpaper);

        foreach (string reason in new[] { s.Sidecar.Reason, s.Taskbar.Reason, s.Workarea.Reason, s.Desktop.Reason, s.Hotkey.Reason,
                                          s.Tray.Reason, s.Tray.WatchReason, s.Bar.Reason, s.EdgeClip.Reason, s.TbHide.Reason,
                                          s.Backdrop.Launcher.Reason, s.Glass.Reason, s.Shortcuts.Reason })
            Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Hidden_mirrors_autohide_and_pieces_compare_by_value()
    {
        var s = ShellState.Initial(ShellMode.Live, BarEdge.Top);
        var t = s with { Taskbar = s.Taskbar with { Autohide = true, WasAutohide = false, Reason = "Mantle turned auto-hide on" } };
        Assert.True(t.Taskbar.Hidden);
        Assert.False(s.Taskbar.Hidden);   // snapshots are immutable
        Assert.Equal(s, ShellState.Initial(ShellMode.Live, BarEdge.Top) with { Features = s.Features });
        Assert.NotEqual(s, t);
    }

    [Fact]
    public void The_hub_publishes_only_real_changes()
    {
        IShellStateHub hub = new TestHub(ShellState.Initial(ShellMode.Test, BarEdge.Bottom));
        var seen = new List<ShellState>();
        using var sub = hub.OnChange(seen.Add);
        Assert.True(hub.Set(s => s with { Bar = s.Bar with { Revealed = true } }));
        Assert.False(hub.Set(s => s with { Bar = s.Bar with { Revealed = true } }));
        var clip = new FeaturePiece("Clipboard history (Win+V)", true, true, "ready", true, "Win+V is Mantle's", null, false);
        Assert.True(hub.Set(s => s with { Features = s.Features.SetItem("clipboard", clip) }));
        Assert.Equal(2, seen.Count);
        Assert.Same(hub.Current, seen[^1]);
        Assert.Equal(clip, hub.Current.Features["clipboard"]);
    }

    sealed class TestHub : IShellStateHub
    {
        readonly List<Action<ShellState>> _subs = new();
        public TestHub(ShellState initial) { Current = initial; }
        public ShellState Current { get; private set; }

        public bool Set(Func<ShellState, ShellState> change)
        {
            var next = change(Current);
            if (next == Current) return false;
            Current = next;
            foreach (var fn in _subs.ToArray()) fn(next);
            return true;
        }

        public IDisposable OnChange(Action<ShellState> handler)
        {
            _subs.Add(handler);
            return new Undo(() => _subs.Remove(handler));
        }
    }

    sealed class Undo : IDisposable
    {
        Action? _undo;
        public Undo(Action undo) { _undo = undo; }
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
