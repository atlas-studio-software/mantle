using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.Core.Features.Desktop;
using Mantle.Core.State;

namespace Mantle.App.Surfaces.Desktop;

// THE LAYER FOLLOWS THE DISPLAY AND OUTLIVES EXPLORER (lane native-r7-desktop-3; main.js desktopGone, scheduleDesktopAttach,
// remakeDesktop, desktopGiveUp, shellRestarted; lib/display-fit.js makeDesktopFit).
//
// - A display change (DESK-016): the first of a burst sizes the layer to the main display at once, the rest become one
//   more after 400 ms of quiet; through the sidecar's desktop {fit:true} (physical px, mapped into the window it is a child
//   of), never while it is held hidden on its way under the icons (sized as soon as it is there), never in --test (an
//   ordinary small window). Then its content is checked against the display at the display's scale; a mismatch gets one
//   resize (1 px shorter, then back) so it re-reads the scale, and is said in words if that does not help.
// - Closed while Mantle runs (DESK-011): Explorer restarting takes its child windows with it. A new layer is made and put
//   under the icons on a back-off of 1.5, 3, 5, 8, 12, 20 s, at most 5 remakes in ten minutes, held hidden until it is
//   there; out of tries it is closed, never shown over the apps. Windows put back (DESK-013): not made again.
// - Explorer came back (DESK-012, TaskbarCreated, heard by ShellWatch): a layer waiting for its next try goes now, one that
//   was given up on is made again, one still there is re-checked (the sidecar rejoins an orphan). One per 3 s.
// A --test run makes the layer again (shown as an ordinary window, never attached) and says the rest in words.
internal sealed partial class DesktopSurface
{
    void StartShellWatch()
    {
        var q = _ctx.Dispatcher;
        _shellWatch = new ShellWatch(why => q.TryEnqueue(() => DisplayChanged(why)), how => q.TryEnqueue(() => ShellBack(how)), _ctx.Log.Line);
        string? err = _shellWatch.Start();
        _ctx.Log.Line(err == null
            ? "listening for display changes and Explorer coming back (TaskbarCreated) on a hidden window of its own"
            : $"display changes and Explorer coming back go unheard: {err}");
    }

    // ---- closed while Mantle runs ----

    void Gone(Microsoft.UI.Xaml.Window win, string how)
    {
        if (_destroyed || _win != win) return;   // our own Destroy, or a window already replaced
        _win = null; _shown = false; _held = false;
        System.Threading.Interlocked.Exchange(ref s_attached, 0);
        _recheckT?.Stop();
        CloseMenu("the desktop layer closed");
        var v = DesktopLayer.Verdict(false, Paused(), _remakeAt, Environment.TickCount64);
        _remakeAt = v.At;
        if (v.Reason == null) return;
        SetDesktopPiece(new DesktopPiece(false, null, v.Reason));
        _ctx.Log.Line($"it closed while Mantle runs ({how}): {(v.Again ? $"making a new one ({v.At.Count} of {DesktopLayer.RemakeMax} in ten minutes)" : v.Reason)}");
        if (v.Again) ScheduleAttach(0, true, null);
    }

    /// Try n: make the window (first try only) and put it under the icons, after the back-off for n.
    void ScheduleAttach(int n, bool make, string? when)
    {
        _attachT?.Stop();
        _attachPending = (n, make, when);
        int ms = DesktopLayer.AttachBackoffMs[Math.Min(n, DesktopLayer.AttachBackoffMs.Count - 1)];
        _attachT = Timer(TimeSpan.FromMilliseconds(ms), () => {
            if (_attachPending is not { } p) return;
            _attachPending = null;
            _ = RemakeAsync(p.N, p.Make, p.When);
        });
        _attachT.Start();
    }

    async Task RemakeAsync(int n, bool make, string? when)
    {
        if (_destroyed || Paused()) return;
        if (make || _win == null) {
            MakeWindow();
            _made++;
            Refill();
            if (_ctx.Test) {
                // a test run never touches the shell: the new layer is made and shown as an ordinary window, never attached
                SetDesktopPiece(new DesktopPiece(false, null, DesktopLayer.TestRemade));
                Show("made again after it closed");
                _ctx.Log.Line($"made again ({_made}) after it closed; a test run never puts it under the icons");
                return;
            }
            _held = true;   // hidden until it is under the icons: shown over the apps it would eat every click
        }
        var r = await AttachAsync(when ?? "made again after Explorer restarted");
        if (_destroyed) return;
        switch (DesktopLayer.AttachNext(r.Ok, n)) {
            case "done":
                _held = false;
                if (_fitPending != null) await FitAsync(_fitPending + ", now that it is under the icons", true);
                Show("made again and under the icons");
                _ctx.Log.Line(when != null
                    ? $"under the icons ({r.Mode ?? "mode not reported"}) on retry {n + 1} {when}"
                    : $"made again and back under the icons ({r.Mode ?? "mode not reported"}) on attempt {n + 1}");
                return;
            case "retry":
                ScheduleAttach(n + 1, false, when);
                return;
            default:
                GiveUp(n + 1, r.Error, when);
                return;
        }
    }

    /// Out of tries: the layer is closed, NEVER shown over the apps; Windows' own wallpaper shows (main.js desktopGiveUp).
    void GiveUp(int tries, string? why, string? when)
    {
        var w = _win;
        _win = null; _held = false; _shown = false;
        _recheckT?.Stop();
        System.Threading.Interlocked.Exchange(ref s_attached, 0);
        try { w?.Close(); } catch (Exception e) { _ctx.Log.Line("its window did not close: " + e.Message); }
        SetDesktopPiece(new DesktopPiece(false, null, DesktopLayer.GaveUp(tries, why, when)));
        _ctx.Log.Line($"{(when != null ? $"could not go under the icons {when}" : "made again but could not go under the icons")} after {tries} tries, so it was closed (never shown over the apps): {why ?? "no reason given"}");
    }

    /// A new window's panes get what the old ones showed.
    void Refill()
    {
        Paint();
        Strip(_ctx.State);
        PaintClock();
        if ((_fx?.Stats ?? _lastStats) is { } st) _panes?.SetStats(st);
        StartMc("made again");
        _panes?.SetWidgets(_widgetsOn);
    }

    // ---- Explorer came back ----

    void ShellBack(string how)
    {
        long now = Environment.TickCount64;
        if (now - _shellBackAt < 3000) return;   // one per 3 s
        _shellBackAt = now;
        _shellBackCount++;
        _ctx.Log.Line(DesktopLayer.ShellBackLine(how, _shellBackCount, _ctx.Test));
        switch (DesktopLayer.ShellBack(_ctx.Test, _destroyed, Paused(), _attachPending != null, _win != null, _held)) {
            case "none": return;
            case "wait": _ctx.Log.Line("Explorer is back and a try to put it under the icons is already on its way"); return;
            case "try-now": {
                var p = _attachPending!.Value;
                _attachT?.Stop(); _attachPending = null;
                _ = RemakeAsync(p.N, p.Make, p.When);
                return;
            }
            case "make": {
                var v = DesktopLayer.Verdict(false, Paused(), _remakeAt, now);
                _remakeAt = v.At;
                if (!v.Again) { _ctx.Log.Line("Explorer is back but it is not made again: " + v.Reason); return; }
                _ctx.Log.Line($"making it again now that Explorer is back ({v.At.Count} of {DesktopLayer.RemakeMax} in ten minutes)");
                SetDesktopPiece(new DesktopPiece(false, null, DesktopLayer.ShellBackMaking));
                ScheduleAttach(0, true, null);
                return;
            }
            default: _ = RecheckAfterShellBack(); return;
        }
    }

    async Task RecheckAfterShellBack()
    {
        var r = await Sidecar("desktop", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["recheck"] = true });
        if (!r.Ok) { _ctx.Log.Line("re-check after Explorer came back failed: " + r.Error); return; }
        bool moved = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("moved", out var m) && m.ValueKind == JsonValueKind.True;
        string? covering = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("covering", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        _ctx.Log.Line($"re-checked after Explorer came back: {(moved ? $"put back ({covering ?? "it had moved"})" : "still in place")}");
    }

    // ---- the display changed ----

    void DisplayChanged(string why)
    {
        if (_burstT is { IsRunning: true }) { _burstPending = true; _burstWhy = why; _burstT.Stop(); _burstT.Start(); return; }
        _ = FitAsync(why, false);
        _burstT ??= Timer(TimeSpan.FromMilliseconds(DesktopLayer.DisplayQuietMs), () => {
            if (!_burstPending) return;
            _burstPending = false;
            _ = FitAsync(_burstWhy + " (end of a burst)", false);
        });
        _burstT.Start();
    }

    bool _fitting;
    async Task FitAsync(string why, bool attached)
    {
        var (act, reason) = DesktopLayer.FitVerdict(_win != null, _ctx.Test, Paused(), _held && !attached);
        if (act == "later") { _fitPending = why; _ctx.Log.Line($"not sized yet ({why}): {reason}"); return; }
        if (act == "skip") { _ctx.Log.Line($"not sized ({why}): {reason}"); return; }
        if (_fitting) { _fitPending = why; return; }   // runs never overlap: the one working picks this up
        _fitting = true; _fitPending = null;
        try {
            var r = await Sidecar("desktop", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["fit"] = true });
            if (!r.Ok) {
                string failed = $"the desktop layer could not be sized to the main display ({why}): {r.Error}";
                _ctx.Log.Line(failed);
                SetDesktopPiece(new DesktopPiece(System.Threading.Interlocked.Read(ref s_attached) != 0, _mode, failed));
                return;
            }
            var res = r.Result;
            int[] rect = Array.Empty<int>();
            if (res.ValueKind == JsonValueKind.Object && res.TryGetProperty("rect", out var rr) && rr.ValueKind == JsonValueKind.Array && rr.GetArrayLength() == 4) {
                rect = new int[4]; int i = 0;
                foreach (var v in rr.EnumerateArray()) rect[i++] = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            }
            bool changed = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("changed", out var ch) && ch.ValueKind == JsonValueKind.True;
            bool child = res.ValueKind == JsonValueKind.Object && res.TryGetProperty("child", out var cd) && cd.ValueKind == JsonValueKind.True;
            await Task.Delay(150);
            var fill = Fill();
            if (fill.Ok == false && rect.Length == 4 && rect[2] - rect[0] > 1 && rect[3] - rect[1] > 1) {
                // a scale-only change keeps the physical size, and a child window is not told the scale changed: one resize
                int w = rect[2] - rect[0], h = rect[3] - rect[1];
                var a = await Sidecar("desktop", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["fit"] = true, ["x"] = rect[0], ["y"] = rect[1], ["w"] = w, ["h"] = h - 1 });
                var b = a.Ok ? await Sidecar("desktop", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["fit"] = true }) : a;
                var was = fill;
                if (!b.Ok) fill = (false, $"{was.Reason}; resizing it so it re-reads the scale failed ({b.Error})");
                else {
                    for (int t = 0; t < 5 && _win != null; t++) {
                        await Task.Delay(150);
                        fill = Fill();
                        if (fill.Ok == true) { fill = (true, $"{fill.Reason}, after a resize made it re-read the scale ({was.Reason})"); break; }
                    }
                    if (fill.Ok == false) fill = (false, $"{fill.Reason}; a resize to make it re-read the scale did not help");
                }
            }
            string size = rect.Length == 4 ? $"{rect[2] - rect[0]}x{rect[3] - rect[1]} px" : "size not reported";
            _ctx.Log.Line(changed
                ? $"sized to the main display ({why}): now {size}{(child ? ", inside the desktop" : "")}; {fill.Reason}"
                : $"already the main display's size ({why}): {size}; {fill.Reason}");
            bool on = System.Threading.Interlocked.Read(ref s_attached) != 0;
            SetDesktopPiece(new DesktopPiece(on, _mode, fill.Ok == false ? $"the desktop layer was sized to the display, but {fill.Reason}" : on ? DesktopLayer.Attached(_mode) : DesktopLayer.TestNeverAttached));
        } finally {
            _fitting = false;
            if (_fitPending != null && !_held) { var again = _fitPending; _fitPending = null; _ = FitAsync(again, false); }
        }
    }

    /// The layer's content against the main display at the display's scale (lib/display-fit.js pageFillVerdict).
    (bool? Ok, string Reason) Fill()
    {
        if (_root?.XamlRoot == null) return (null, "the desktop layer did not say how big it is");
        var mon = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfoW(mon, ref mi)) return (null, "the main display reported no size");
        double sf = Win32.GetDpiForMonitor(mon, 0 /* MDT_EFFECTIVE_DPI */, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1;
        return DesktopLayer.PageFill(_root.ActualWidth, _root.ActualHeight, _root.XamlRoot.RasterizationScale,
            mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top, sf);
    }
}
