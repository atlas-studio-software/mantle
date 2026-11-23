using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Mantle.App.Surfaces.Bar.Frame;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Shell.Material;

/// The glass picture loop of a revealed window (bar.md BAR-034, BAR-037, BAR-038; spikes/windowing/glass Tick,
/// Refresh, WatchWallpaper). Light: about 4 pictures a second, only while the window is up; it STARTS on reveal
/// and STOPS on hide, so a hidden bar captures nothing. Dark: one still per reveal, cut again when the wallpaper
/// changes while up. Each picture: the window is excluded from capture, DWM gets 30 ms to drop it (Electron's
/// BACKDROP_SETTLE_MS 25), the screen is read and blurred on a pool thread, the window is included again and the
/// UI thread only swaps the pixels in.
internal sealed class LiveGlass
{
    public const int LightFps = 4, SettleMs = 30, WallpaperWaitMs = 400;

    readonly IntPtr _hwnd;
    readonly DispatcherQueue _q;
    readonly Func<Func<byte[]?>> _prepare;  // UI thread: snapshot the place; the work it returns runs on a pool thread (capture + blur; null = unreadable)
    readonly Action<byte[]?, string> _show;  // UI thread: the pixels, or null and why
    readonly Action<string> _log;
    readonly DispatcherQueueTimer _timer;
    bool _running, _busy, _dark;
    int _gen;
    long _frames, _failed, _captureTicks;

    public LiveGlass(IntPtr hwnd, DispatcherQueue q, Func<Func<byte[]?>> prepare, Action<byte[]?, string> show, Action<string> log)
    {
        _hwnd = hwnd; _q = q; _prepare = prepare; _show = show; _log = log;
        _timer = q.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(1000 / LightFps);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => { Mantle.Core.Lifecycle.IdleTally.Hit("ui timer (live glass)"); Refresh(null); };
    }

    public bool Running => _running;
    public long Frames => _frames;
    public string Stats => $"{_frames} pictures, {_failed} unreadable, mean capture+blur {(_frames > 0 ? new TimeSpan(_captureTicks / _frames).TotalMilliseconds : 0):0.0} ms";

    /// The window is up (the caller already showed the picture it took while the window was still hidden).
    public void Reveal(bool dark)
    {
        _dark = dark;
        _running = true;
        if (!dark) _timer.Start();
        _log($"glass: {(dark ? "dark: one still for this reveal (re-cut on a wallpaper change)" : $"light: live, {LightFps} pictures a second while up")}");
    }

    /// The window went down: no more pictures until the next reveal. A picture in flight is dropped.
    public void Hide()
    {
        if (!_running) return;
        _running = false; _gen++;
        _timer.Stop();
        _log("glass: stopped (the window is down; nothing is captured while hidden): " + Stats);
    }

    /// The theme changed while up: light runs the loop, dark holds a still.
    public void Retheme(bool dark)
    {
        if (!_running || dark == _dark) return;
        _dark = dark;
        if (dark) _timer.Stop(); else _timer.Start();
        Refresh("the theme changed");
    }

    /// WM_SETTINGCHANGE SPI_SETDESKWALLPAPER: a new cut once Explorer has repainted (dark; light picks it up anyway).
    public void WallpaperChanged()
    {
        if (!_running || !_dark) return;
        int gen = _gen;
        var t = _q.CreateTimer(); t.Interval = TimeSpan.FromMilliseconds(WallpaperWaitMs); t.IsRepeating = false;
        t.Tick += (_, _) => { t.Stop(); if (gen == _gen) Refresh("the wallpaper changed"); };
        t.Start();
    }

    void Refresh(string? why)
    {
        if (!_running || _busy) return;
        _busy = true;
        int gen = _gen;
        bool excluded = FrameNative.SetWindowDisplayAffinity(_hwnd, FrameNative.WDA_EXCLUDEFROMCAPTURE);
        var settle = _q.CreateTimer(); settle.Interval = TimeSpan.FromMilliseconds(SettleMs); settle.IsRepeating = false;
        settle.Tick += (_, _) => {
            settle.Stop();
            var work = _prepare();
            Task.Run(() => {
                long t0 = Stopwatch.GetTimestamp();
                byte[]? px = null; string err = "";
                try { px = work(); if (px == null) err = "the screen behind the bar could not be read"; }
                catch (Exception e) { err = "the picture failed: " + e.Message; }
                long took = Stopwatch.GetTimestamp() - t0;
                _q.TryEnqueue(() => {
                    if (excluded) FrameNative.SetWindowDisplayAffinity(_hwnd, FrameNative.WDA_NONE);
                    _busy = false;
                    if (gen != _gen || !_running) return;   // hidden meanwhile: BAR-038, the hide owns the picture now
                    if (px != null) { _frames++; _captureTicks += took * TimeSpan.TicksPerSecond / Stopwatch.Frequency; }
                    else _failed++;
                    _show(px, px != null ? "" : err);
                    if (why != null) _log($"glass: {why}: new picture ({Stats})");
                });
            });
        };
        settle.Start();
    }
}
