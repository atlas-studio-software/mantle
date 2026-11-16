using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Mantle.App.Native;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core.Features.TopBar;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Surfaces.TopBar;

// TOP-227..232: the window put back when something outside Mantle moves or resizes it (lib/features/topbar.js's
// driftCheck / driftRun / driftRetry; the rules and words are Mantle.Core's TopBarDrift). Event-driven: the subclass
// hears WM_WINDOWPOSCHANGED and the check runs 250 ms later, once; a fight's backoff is one queued re-check. Nothing
// runs while the window stays where it is. Hidden (by hand, a game, full-screen video) nothing is put back and a fight
// pauses; ShowWin places it before it shows, and the move that makes re-queues a paused fight's re-check.
internal sealed partial class TopBarSurface
{
    /// TOP-250: Explorer broadcasts it to every top-level window when it starts (the bar's dock hears the same one).
    static readonly uint TaskbarCreatedMsg = ShellBackNative.RegisterWindowMessageW("TaskbarCreated");
    DriftState? _drift;
    DispatcherQueueTimer? _driftT, _driftRetryT;
    (int X, int Y, int W, double Scale)? _want;   // set by Place(): where the window belongs (physical px)
    bool _driftGone;

    /// The subclass's hook: a move or resize happened (ours included: the check then finds it in place).
    void DriftHeard()
    {
        if (_driftGone || _want == null) return;
        if (_drift is { Stuck: true })
        {
            // in a fight the queued re-check owns it (that IS the backoff); a fight paused while hidden resumes here
            if (_driftRetryT == null && Win32.IsWindowVisible(_hwnd)) DriftRetry(_drift.RetryMs > 0 ? _drift.RetryMs : TopBarDrift.BackoffMs[0]);
            return;
        }
        if (_driftT != null) return;
        _driftT = Once(TopBarDrift.CheckMs, () => { _driftT = null; DriftRun(false); });
    }

    void DriftRun(bool retry)
    {
        if (_driftGone || _want is not { } want) return;
        bool was = _drift is { Stuck: true };
        if (!Win32.IsWindowVisible(_hwnd)) return;   // hidden: nothing is put back, a fight pauses
        if (!FrameNative.GetWindowRect(_hwnd, out var got)) return;
        var off = TopBarDrift.Off(got.Left, got.Top, got.Right - got.Left, got.Bottom - got.Top, want.X, want.Y, want.W, want.Scale);
        var step = TopBarDrift.Step(_drift, off, $"{want.X},{want.Y} {want.W} wide", Environment.TickCount64, retry);
        _drift = step.D;
        if (step.Log != null) _ctx.Log.Line(step.Log);
        // the next re-check is queued BEFORE the put-back: its SetWindowPos is heard at once, and DriftHeard must find
        // the re-check already there
        if (step.RetryMs > 0) DriftRetry(step.RetryMs);
        else if (step.Settled) DriftRetry(0);
        if (step.Fix) Place("put back");
        if (was != (_drift is { Stuck: true })) DriftReport();
    }

    /// (Re)queue the fight's one re-check in ms; 0 = none.
    void DriftRetry(int ms)
    {
        _driftRetryT?.Stop(); _driftRetryT = null;
        if (ms > 0) _driftRetryT = Once(ms, () => { _driftRetryT = null; DriftRun(true); });
    }

    /// The feature's health carries the fight (topbar.js health(): the window fight comes right after the page).
    void DriftReport()
    {
        long since = _drift?.Since ?? 0;
        long wall = DateTimeOffset.Now.ToUnixTimeMilliseconds() - (Environment.TickCount64 - since);
        // worded as if shown: the feature drops it while the bar is hidden (a fight pauses then)
        var why = TopBarDrift.Reason(_drift, true,
            _ => DateTimeOffset.FromUnixTimeMilliseconds(wall).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        _ = _ctx.CallAsync("topbar.drift", new JsonObject { ["stuck"] = _drift is { Stuck: true }, ["reason"] = why });
    }

    void DriftStop()
    {
        _driftGone = true;
        _driftT?.Stop(); _driftT = null;
        _driftRetryT?.Stop(); _driftRetryT = null;
    }

    DispatcherQueueTimer Once(int ms, Action fn)
    {
        var t = _ctx.Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms); t.IsRepeating = false;
        t.Tick += (s, _) => { s.Stop(); if (!_driftGone) fn(); };
        t.Start();
        return t;
    }
}
