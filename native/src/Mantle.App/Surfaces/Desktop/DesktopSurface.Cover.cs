using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Mantle.Core.Features.Desktop;

namespace Mantle.App.Surfaces.Desktop;

// HIDING THE WIDGETS UNDER WINDOWS (DESK-091..103; lib/features/widgethide.js, ui/desktop/desktop.js applyCover). The
// layer's pane rects (physical px, the same ones it logs) go to CoverWatch, which answers on window events only; a covered
// pane fades out and takes no clicks, and a covered clock / System tile stops ticking / reading once its fade has finished.
// It watches only while the layer is shown, the feature is on and the widgets are on; any other time, and whenever the
// answer is unknown, every widget shows (fails open), with the reason in the log and, for real trouble, the feature's health.
// A --test run watches only with MANTLE_TEST_COVER=1 (the screenshot pair and the other tests keep every pane up).
internal sealed partial class DesktopSurface
{
    public const string CoverTestEnv = "MANTLE_TEST_COVER";

    CoverWatch? _cover;
    IReadOnlyList<CoverArea> _areas = Array.Empty<CoverArea>();
    bool _coverOn = true, _clockParked, _statsParked, _coverTestSaid;
    string _coverSaid = "", _coverIdleSaid = "", _coverHealth = "";
    static readonly bool Reduced = !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    /// Start, re-aim or stop the watch to match the layer's state (the one place that decides it).
    void CoverSync(string why)
    {
        if (_ctx.Test && Host.Env(CoverTestEnv) != "1") {
            if (!_coverTestSaid && _shown) { _coverTestSaid = true; _ctx.Log.Line($"widgets under windows: not watched (a test run watches only with {CoverTestEnv}=1), so every widget shows"); }
            return;
        }
        string? idle = WidgetCover.Idle(_coverOn, _win != null, _shown, _widgetsOn, _areas.Count);
        if (idle != null) {
            if (_cover != null) { SayStopped(_cover, why); _cover.Dispose(); _cover = null; }
            if (idle != _coverIdleSaid) { _coverIdleSaid = idle; _ctx.Log.Line($"widgets under windows: not watched: {idle} ({why})"); }
            ApplyCover(CoverAnswer.Open(_coverOn, idle), why);
            return;
        }
        _coverIdleSaid = "";
        if (_cover != null) { _cover.SetAreas(_areas); return; }
        var q = _ctx.Dispatcher;
        CoverWatch? w = null;
        w = new CoverWatch(_areas, _hwnd,
            (a, how) => q.TryEnqueue(() => { if (_cover == w) ApplyCover(a, how); }),
            err => q.TryEnqueue(() => { if (_cover == w) CoverFailed(WidgetCover.CheckFailed(err)); }),
            _ctx.Test ? new Action<int, string>((n, how) => _ctx.Log.Line($"cover check {n} ({how})")) : null);
        string? e = w.Start();
        if (e != null) { w.Dispose(); CoverFailed(WidgetCover.WatchFailed(e)); return; }
        _cover = w;
        CoverHealth(true, null);
        _ctx.Log.Line($"widgets under windows: watching {_areas.Count} widget(s) on window events only (no poll; {why})");
    }

    void CoverFailed(string reason)
    {
        _ctx.Log.Line("widgets under windows: " + reason);
        ApplyCover(CoverAnswer.Open(true, reason), "the watch failed");
        CoverHealth(false, reason);
    }

    /// The feature's health, said only when it changes (a strip and tray row while it is bad).
    void CoverHealth(bool ok, string? reason)
    {
        string key = ok ? "ok" : reason!;
        if (key == _coverHealth) return;
        _coverHealth = key;
        _ = _ctx.CallAsync("widgethide.health", new JsonObject { ["ok"] = ok, ["reason"] = reason });
    }

    /// The panes draw the answer: covered ones fade out, the rest show; a pane coming back paints / reads first.
    void ApplyCover(CoverAnswer a, string why)
    {
        if (_panes == null) return;
        foreach (var key in WidgetCover.Keys) {
            bool c = a.Is(key);
            if (!c) Unpark(key);
            _panes.SetCovered(key, c, !Reduced, Parked);
        }
        string words = a.Words();
        if (words != _coverSaid) { _coverSaid = words; _ctx.Log.Line($"covered: {words} ({why})"); }
    }

    /// DESK-103: the fade-out has finished, so nothing of the pane is drawn: its clock / reading stops.
    void Parked(string key)
    {
        if (key == "clock" && !_clockParked) { _clockParked = true; _clockT?.Stop(); _ctx.Log.Line("the clock stopped ticking: a window covers it and its fade-out has finished"); }
        else if (key == "stats" && !_statsParked) { _statsParked = true; _statsT?.Stop(); _ctx.Log.Line("the System numbers stopped reading: a window covers them and their fade-out has finished"); }
    }

    /// Uncovered: the current second is painted (and the numbers read) in the same step that starts the fade in.
    void Unpark(string key)
    {
        if (key == "clock" && _clockParked) { _clockParked = false; if (_awake) StartClock(); _ctx.Log.Line("the clock ticks again (uncovered)"); }
        else if (key == "stats" && _statsParked) { _statsParked = false; if (_awake) StartStats(); _ctx.Log.Line("the System numbers read again (uncovered)"); }
    }

    void StopCover()
    {
        _cover?.Dispose(); _cover = null;
    }

    /// The watch stops with what it did, in the log: how many times it looked at the windows.
    void SayStopped(CoverWatch w, string why) => _ctx.Log.Line($"widgets under windows: the watch stopped after {w.Decisions} check(s) ({why})");
}
