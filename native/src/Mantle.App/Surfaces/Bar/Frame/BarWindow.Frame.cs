using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.App.Shell.Material;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App;

/// The bar frame (bar.md "Bar shell"): where the bar sits, when it shows, how it moves, staying on top, the dock
/// app bar, display and DPI changes, and the live glass. Three modes:
///   hover (a live run, or MANTLE_TEST_HOVER=1): hidden at rest, revealed by the bottom 4 DIP (BAR-009, BAR-026);
///   dock (--dock): a permanent app bar reserved through the platform (BAR-106), never hidden;
///   up (every other --test or scratch run): shown and still, which the bench, parity and smoke stages expect.
public sealed partial class BarWindow
{
    const int RevealMs = 560, HideMs = 240, FadeOnlyMs = 120, DisplayQuietMs = 400;
    const double DownY = BarGeometry.BarH * 0.75, DownS = 0.94, FadeK = 0.4;   // bar.css down pose; opacity in the first 40 %
    // ui/shared/glass-light.css --m-spring (CSS linear(), 41 evenly spaced points)
    static readonly float[] Spring = { 0f, 0.0208f, 0.0739f, 0.1478f, 0.2337f, 0.3245f, 0.4153f, 0.5025f, 0.5838f, 0.6577f, 0.7235f,
        0.7808f, 0.8301f, 0.8716f, 0.906f, 0.9341f, 0.9566f, 0.9742f, 0.9876f, 0.9977f, 1.0049f, 1.0098f, 1.0128f, 1.0145f,
        1.0151f, 1.015f, 1.0143f, 1.0132f, 1.012f, 1.0106f, 1.0092f, 1.0079f, 1.0066f, 1.0055f, 1.0044f, 1.0035f, 1.0028f,
        1.0021f, 1.0015f, 1.0011f, 1f };

    BarGeometry _g;
    internal string FrameMode { get; private set; } = "up";
    volatile bool _revealed;
    bool _hiding, _gaveUp;
    long _leftAt = -1, _outAt = -1;
    int _tickPending, _animSeq, _reveals;
    volatile bool _stopPoll;
    string? _lastBlock;
    Visual? _vis;
    LiveGlass? _live;
    int _glassW, _glassH;
    WriteableBitmap? _glassBmp;
    IBarShell? _shell;
    DispatcherQueueTimer? _burst;
    bool _burstPending; string _burstWhy = "";
    FrameNative.SubclassProc? _sub;
    Win32.WinEventProc? _hookProc;
    IntPtr _hookShow, _hookFg, _hookTray;
    readonly List<long> _raiseTimes = new();
    readonly bool _reducedMotion = !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    internal bool ReducedMotion => _reducedMotion;
    /// The shared spring (samples 0..1) the pop-ups' entrance rides too (BAR-387).
    internal static float[] SpringCurve => Spring;

    /// Pop-ups hold the bar up while one is open (WIN-019, BAR-102); the bar closes them when it hides (BAR-024).
    internal Func<bool>? HoldsBar;
    internal event Action<string>? Hiding;
    internal event Action? Revealed;
    internal bool IsUp => FrameMode != "hover" || (_revealed && !_hiding);
    internal IntPtr Hwnd => _hwnd;
    internal BarGeometry Geometry => _g;

    /// The host wires the platform in (the dock app bar). Without it a dock run reserves nothing and says so.
    internal void UseShell(IBarShell shell) { _shell = shell; if (FrameMode == "dock" && _started) _ = DockBar("the platform was wired to the bar"); }
    bool _started;

    static string PickMode()
    {
        if (Host.Args.Has("dock")) return "dock";
        if (Host.Args.Kind == RunKind.Live || Host.Env("MANTLE_TEST_HOVER") == "1") return "hover";
        return "up";
    }

    void TakeGeometry(BarGeometry g)
    {
        _g = g;
        _scale = g.Scale; _monitor = g.Monitor; _pillW = g.PillWdip; _pillPx = g.Pill;
        _winPx = new Windows.Graphics.RectInt32(g.Window.Left, g.Window.Top, g.Window.Right - g.Window.Left, g.Window.Bottom - g.Window.Top);
    }

    /// XAML sizes from the geometry; after the first time also the shadow (it is drawn for one pill width).
    /// The pill's hairlines the way Chromium draws them at any scale: the 1 px CSS border (`.bar::after`) is
    /// floor(scale) device px, at least 1, and the lip (`--lg-lip-up: inset 0 1px 0`) is a shadow, so exactly
    /// 1 DIP, anti-aliased, starting right inside that border. WinUI's layout rounding drew both 2 px at 150 %
    /// (parity run 42371182726: the top edge a row thicker and the lip a row lower, light-150 0.9616, dark-150 0.955).
    void SnapHairlines(double s)
    {
        double border = Math.Max(1, Math.Floor(s)) / s;   // DIP
        Edge.BorderThickness = new Thickness(border);
        Lip.UseLayoutRounding = false;
        Lip.Margin = new Thickness(border);
    }

    void ApplyGeometry(BarGeometry g, string why)
    {
        bool widthChanged = Math.Abs(g.PillWdip - _pillW) > 0.01;
        TakeGeometry(g);
        Root.Width = _pillW + 2 * BarGeometry.ShadowSide;
        Root.Height = BarGeometry.ShadowTop + BarGeometry.BarH + BarGeometry.Lift;
        Pill.Width = _pillW;
        Pill.Margin = new Thickness(BarGeometry.ShadowSide, BarGeometry.ShadowTop, 0, 0);
        SnapHairlines(g.Scale);
        if (_vis != null) _vis.CenterPoint = new Vector3((float)(Root.Width / 2), (float)(BarGeometry.ShadowTop + BarGeometry.BarH), 0);
        if (widthChanged && !_dark && _started) AddShadow();
        if (_started) Host.Log.Line($"bar placed ({why}): {g.Describe()}");
    }

    void Move(uint extra = 0) =>
        Win32.SetWindowPos(_hwnd, FrameNative.HWND_TOPMOST, _winPx.X, _winPx.Y, _winPx.Width, _winPx.Height, FrameNative.SWP_NOACTIVATE | extra);

    /// After the first show (ShowBar): the mode starts here.
    void StartFrame()
    {
        _started = true;
        _sub = SubProc;
        FrameNative.SetWindowSubclass(_hwnd, _sub, new UIntPtr(0x4D46), IntPtr.Zero);
        _live = new LiveGlass(_hwnd, DispatcherQueue, PrepareShot, ShowShot, Host.Log.Line);
        Closed += (_, _) => StopFrame();
        StartPopups();
        Host.Log.Line($"bar frame: {FrameMode} mode ({ModeWhy()}); {_g.Describe()}; {(_reducedMotion ? "Windows animation effects are off: fades only" : "full motion")}");
        if (FrameMode == "hover") {
            _vis!.Opacity = 0;   // BAR-016: nothing paints before the first reveal
            CompositionTarget.Rendering += HideAfterFirstFrame;
            StartPoll();
            Host.Log.Line("bar keep zone: " + BarIntent.Describe(_g));
        } else {
            _revealed = true;
            if (FrameMode == "dock") { _ = DockBar("start"); _live.Reveal(_dark); }
        }
    }

    static string ModeWhy() => Host.Args.Has("dock") ? "--dock: a permanent app bar"
        : Host.Args.Kind == RunKind.Live ? "a live run: hidden at rest, revealed at the bottom edge"
        : Host.Env("MANTLE_TEST_HOVER") == "1" ? "MANTLE_TEST_HOVER=1 asks a test run for the live hover behaviour"
        : "a test or scratch run keeps the bar up and still (MANTLE_TEST_HOVER=1 for the hover behaviour)";

    void HideAfterFirstFrame(object? s, object e)
    {
        CompositionTarget.Rendering -= HideAfterFirstFrame;
        if (_revealed) return;
        Win32.ShowWindow(_hwnd, FrameNative.SW_HIDE);
        ClearPicture();
        Host.Log.Line("bar hidden: at rest until the cursor reaches the bottom edge");
        Tick();
        IdleProbe.Arm(DispatcherQueue);   // test runs only (MANTLE_TEST_IDLE_PROBE): what wakes the app at rest
    }

    void StopFrame()
    {
        _stopPoll = true;
        DisarmWatch();
        _live?.Hide();
        if (FrameMode == "dock" && _shell != null) {
            try { if (!_shell.RemoveAsync(_hwnd).Wait(4000)) Host.Log.Line("dock: removing the app bar took over 4 s (Explorer drops it when the window goes)"); }
            catch (AggregateException e) { Host.Log.Line("dock: removing the app bar failed: " + e.InnerException?.Message); }
        }
    }

    // ------------------------------------------------------------------ the hover poll (BAR-027)
    /// A plain background thread: one GetCursorPos per 80 ms hidden, 60 ms shown. The UI thread is woken only when
    /// the cursor is in the band or the bar is up, so a hidden bar costs one cursor read per tick and nothing else.
    void StartPoll()
    {
        new Thread(() => {
            while (!_stopPoll) {
                Thread.Sleep(_revealed ? BarIntent.PollShownMs : BarIntent.PollHiddenMs);
                if (!FrameNative.GetCursorPos(out var pt)) continue;
                var g = _g;
                bool inBand = BarIntent.InBand(g, pt.X, pt.Y);
                if (!_revealed && !inBand) continue;
                if (Interlocked.Exchange(ref _tickPending, 1) == 1) continue;   // one tick queued at a time
                DispatcherQueue.TryEnqueue(() => { Volatile.Write(ref _tickPending, 0); HoverTick(pt, inBand); });
            }
        }) { IsBackground = true, Name = "bar hover poll" }.Start();
    }

    void HoverTick(Win32.POINT pt, bool inBand)
    {
        long now = Environment.TickCount64;
        if (!_revealed) {
            if (!inBand) return;
            string? block = FullscreenBlock.Blocks(now);
            if (block != null) { if (block != _lastBlock) Host.Log.Line("bar blocked: " + block); }
            else Reveal("the cursor reached the bottom edge");
            _lastBlock = block;
            return;
        }
        string zone = BarIntent.Zone(_g, pt.X, pt.Y);
        if (HoldsBar?.Invoke() == true) zone = "in";
        if (zone == "in" && _hiding) { Reveal("the cursor came back before the bar finished sliding away"); return; }   // BAR-099
        if (_hiding) return;
        string? hide = BarIntent.ShownStep(zone, now, ref _leftAt, ref _outAt);
        if (hide != null) BeginHide(hide); else CheckOnTop("poll");
    }

    // ------------------------------------------------------------------ reveal and hide
    /// Also the way a pop-up brings a hidden bar back (BAR-406).
    internal void Reveal(string why)
    {
        if (FrameMode != "hover") return;
        bool wasHiding = _hiding, wasUp = _revealed && !_hiding;
        if (wasUp) return;
        _hiding = false; _leftAt = _outAt = -1;
        var g = BarGeometry.Compute();   // every reveal re-reads the geometry: a change while hidden is never missed
        if (!g.SameAs(_g)) ApplyGeometry(g, "reveal: the display changed while the bar was hidden");
        if (!wasHiding) PictureBeforeShow();   // BAR-030: one picture while the window is still hidden
        _revealed = true; _reveals++;
        Move(FrameNative.SWP_SHOWWINDOW);
        Animate(true);
        Host.Log.Line($"bar revealed ({why}){(wasHiding ? " mid-hide" : "")}: reveal {_reveals}");
        RaiseBar("reveal");
        ArmWatch();
        _live?.Reveal(_dark);
        Tick();   // BAR-025, BAR-417: seconds while up, repainted at once
        Revealed?.Invoke();
    }

    void BeginHide(string why)
    {
        if (!_revealed || _hiding) return;
        _hiding = true;
        Hiding?.Invoke(why);   // BAR-024: any open pop-up closes
        _live?.Hide();         // BAR-105: capture stops with the hide
        Host.Log.Line("bar hiding: " + why);
        Animate(false);
        Tick();
    }

    void HideDone()
    {
        Win32.ShowWindow(_hwnd, FrameNative.SW_HIDE);
        _revealed = false; _hiding = false;
        DisarmWatch();
        ClearPicture();   // BAR-038: only after the exit has finished
        Host.Log.Line("bar hidden: " + (Win32.IsWindowVisible(_hwnd) ? "STILL VISIBLE after SW_HIDE" : "window hidden, nothing renders"));
    }

    /// The compositor moves the island (BAR-017, BAR-020, BAR-029): the window sits at its rest place and never
    /// moves; Translation 36 -> 0 DIP and Scale .94 -> 1 on the spring over 560 ms, opacity over the first 40 %.
    /// Out: back to the down pose over 240 ms, accelerating. Every key frame starts from StartingValue, so a
    /// reversal turns round from wherever the island is (BAR-021). Reduced motion: a 120 ms fade only (BAR-032).
    void Animate(bool up)
    {
        var v = _vis!; var c = v.Compositor;
        int seq = ++_animSeq;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var op = c.CreateScalarKeyFrameAnimation();
        if (_reducedMotion) {
            v.Properties.InsertVector3("Translation", Vector3.Zero); v.Scale = Vector3.One;
            op.Duration = TimeSpan.FromMilliseconds(FadeOnlyMs);
            op.InsertExpressionKeyFrame(0f, "this.StartingValue");
            op.InsertKeyFrame(1f, up ? 1f : 0f, c.CreateLinearEasingFunction());
            v.StartAnimation("Opacity", op);
        } else {
            var lin = c.CreateLinearEasingFunction();
            var tr = c.CreateVector3KeyFrameAnimation();
            var sc = c.CreateVector3KeyFrameAnimation();
            if (up) {
                tr.Duration = sc.Duration = TimeSpan.FromMilliseconds(RevealMs);
                op.Duration = TimeSpan.FromMilliseconds(RevealMs * FadeK);
                for (int i = 0; i < Spring.Length; i++) {
                    float t = i / (float)(Spring.Length - 1);
                    string k = Spring[i].ToString("0.#####", CultureInfo.InvariantCulture);
                    tr.InsertExpressionKeyFrame(t, $"this.StartingValue + (Vector3(0, 0, 0) - this.StartingValue) * {k}", lin);
                    sc.InsertExpressionKeyFrame(t, $"this.StartingValue + (Vector3(1, 1, 1) - this.StartingValue) * {k}", lin);
                }
                op.InsertExpressionKeyFrame(0f, "this.StartingValue");
                op.InsertKeyFrame(1f, 1f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
            } else {
                var accel = c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0f), new Vector2(0.8f, 0.15f));
                tr.Duration = sc.Duration = op.Duration = TimeSpan.FromMilliseconds(HideMs);
                tr.InsertExpressionKeyFrame(0f, "this.StartingValue");
                tr.InsertKeyFrame(1f, new Vector3(0, (float)DownY, 0), accel);
                sc.InsertExpressionKeyFrame(0f, "this.StartingValue");
                sc.InsertKeyFrame(1f, new Vector3((float)DownS, (float)DownS, 1), accel);
                op.InsertExpressionKeyFrame(0f, "this.StartingValue");
                op.InsertKeyFrame(1f, 0f, c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f)));
            }
            v.StartAnimation("Translation", tr);
            v.StartAnimation("Scale", sc);
            v.StartAnimation("Opacity", op);
        }
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => { if (seq == _animSeq && !up && _hiding) HideDone(); });
    }

    /// The down pose, set before the first frame (hover mode only).
    void PoseDown()
    {
        // up and dock never move, so Root's composition visual is not touched there at all
        if (FrameMode != "hover") return;
        _vis = ElementCompositionPreview.GetElementVisual(Root);
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        _vis.CenterPoint = new Vector3((float)(Root.Width / 2), (float)(BarGeometry.ShadowTop + BarGeometry.BarH), 0);
        if (!_reducedMotion) { _vis.Properties.InsertVector3("Translation", new Vector3(0, (float)DownY, 0)); _vis.Scale = new Vector3((float)DownS, (float)DownS, 1); }
        _vis.Opacity = 0;
    }

    /// BAR-417: one chained timer, on each second (+20 ms) while the bar is up, on the minute (+250 ms) while down.
    TimeSpan ClockInterval(DateTime now) => IsUp
        ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 20)
        : TimeSpan.FromMilliseconds((60 - now.Second) * 1000 - now.Millisecond + 250);

    // ------------------------------------------------------------------ the glass while up
    void PictureBeforeShow()
    {
        var work = PrepareShot();
        byte[]? px = null; string err = "";
        try { px = work(); if (px == null) err = "the screen behind the bar could not be read"; }
        catch (Exception e) { err = "the picture failed: " + e.Message; }
        ShowShot(px, err);
    }

    Func<byte[]?> PrepareShot()
    {
        var pill = _pillPx; var mon = _monitor; double sigma = Host.Data.EffectiveGlass.For(_dark).BlurPx * _scale;
        double maxSigma = Math.Max(Host.Data.EffectiveGlass.Dark.BlurPx, Host.Data.EffectiveGlass.Light.BlurPx) * _scale;
        double sat = Saturation(_dark);
        _glassW = pill.Right - pill.Left; _glassH = pill.Bottom - pill.Top;
        if (Host.Args.NoBackdrop) return () => null;
        return () => {
            var shot = Backdrop.Grab(pill, mon, maxSigma);
            return shot == null ? null : Backdrop.Blurred(shot, pill, sigma, sat);
        };
    }

    void ShowShot(byte[]? px, string err)
    {
        if (px == null) {
            Glass.Background = null;
            Tint.Background = (Brush)ThemeToken("LgPaneFillFallback");
            _backdropState = Host.Args.NoBackdrop ? "off (--no-backdrop)" : err;
            if (!Host.Args.NoBackdrop) {
                _chips.Fail("glass", "Glass not blurred", "Mantle did not get a picture of the screen behind the bar, so it cannot blur what is behind it and is showing a thicker tint instead: " + err, "warn");
                RenderPip();
            }
            return;
        }
        _glassBmp = Backdrop.ToBitmap(px, _glassW, _glassH, _glassBmp);
        GlassImage.ImageSource = _glassBmp;
        Glass.Background = GlassImage;
        Tint.Background = (Brush)ThemeToken("LgPaneFill");
        _backdropState = "captured";
        if (_chips.Clear("glass")) RenderPip(force: true);
    }

    /// BAR-035: no picture after a hide, the thicker fallback fill until the next reveal's picture.
    void ClearPicture()
    {
        GlassImage.ImageSource = null;
        Glass.Background = null;
        Tint.Background = (Brush)ThemeToken("LgPaneFillFallback");
    }

    // ------------------------------------------------------------------ the dock app bar (BAR-106..108)
    async Task DockBar(string why)
    {
        var g = BarGeometry.Compute();
        int size = (int)Math.Round(BarGeometry.BarH * g.Scale);
        var slot = new Win32.RECT { Left = g.Monitor.Left, Right = g.Monitor.Right, Top = g.Monitor.Bottom - size, Bottom = g.Monitor.Bottom };
        string how;
        if (_shell == null) how = "nothing reserved: the platform is not wired to the bar in this build, so maximised windows may sit under it";
        else {
            var (got, h) = await _shell.ReserveAsync(_hwnd, "bottom", size);
            how = h;
            if (got is Win32.RECT r) slot = r;
        }
        ApplyGeometry(g.InSlot(slot), "dock, " + why);
        Move(FrameNative.SWP_SHOWWINDOW);
        Host.Log.Line($"dock ({why}): {how}; work area {FrameNative.Rect(FrameNative.WorkArea())}");
        if (why == "start" && _shell != null) {
            // BAR-108: Explorer can stack a new app bar over a stale slot from a previous run; ask again once settled
            foreach (int ms in new[] { 300, 1500, 6000 }) {
                var t = DispatcherQueue.CreateTimer(); t.Interval = TimeSpan.FromMilliseconds(ms); t.IsRepeating = false;
                t.Tick += (_, _) => { t.Stop(); _ = DockBar($"{ms} ms after start"); };
                t.Start();
            }
        }
    }

    // ------------------------------------------------------------------ display, DPI, wallpaper (BAR-088)
    IntPtr SubProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        switch (msg) {
            case 0x02E0: {   // WM_DPICHANGED: WinUI's own handler first (its rasterization scale), then our geometry
                IntPtr res = FrameNative.DefSubclassProc(h, msg, wp, lp);
                long dpi = wp.ToInt64() & 0xFFFF;
                DispatcherQueue.TryEnqueue(() => DisplayChanged($"WM_DPICHANGED {dpi}"));
                return res;
            }
            case 0x007E: {   // WM_DISPLAYCHANGE: resolution, a monitor added or removed, the primary switched
                long v = lp.ToInt64();
                DispatcherQueue.TryEnqueue(() => DisplayChanged($"WM_DISPLAYCHANGE {v & 0xFFFF}x{(v >> 16) & 0xFFFF}"));
                break;
            }
            case 0x001A:     // WM_SETTINGCHANGE SPI_SETDESKWALLPAPER: the dark still is cut again
                if (wp.ToInt64() == 0x0014) DispatcherQueue.TryEnqueue(() => _live?.WallpaperChanged());
                break;
        }
        return FrameNative.DefSubclassProc(h, msg, wp, lp);
    }

    /// main.js displayBurst: the first change re-places at once, the rest of a burst become one more after 400 ms quiet.
    void DisplayChanged(string why)
    {
        if (_burst != null && _burst.IsRunning) { _burstPending = true; _burstWhy = why; _burst.Stop(); _burst.Start(); return; }
        Replace(why);
        if (_burst == null) {
            _burst = DispatcherQueue.CreateTimer();
            _burst.Interval = TimeSpan.FromMilliseconds(DisplayQuietMs); _burst.IsRepeating = false;
            _burst.Tick += (_, _) => { if (_burstPending) { _burstPending = false; Replace(_burstWhy + " (end of a burst)"); } };
        }
        _burst.Start();
    }

    void Replace(string why)
    {
        if (FrameMode == "dock") { _ = DockBar(why); return; }
        var g = BarGeometry.Compute();
        if (g.SameAs(_g)) { Host.Log.Line($"bar placed ({why}): unchanged"); return; }
        ApplyGeometry(g, why);
        if (FrameMode == "up" || IsUp) { Move(); if (IsUp && FrameMode == "hover") PictureBeforeShowLater(); }
        // hidden: the window is moved on the next reveal, which re-reads the geometry anyway
    }

    void PictureBeforeShowLater() { if (_live?.Running == true) return; PictureBeforeShow(); }

    // ------------------------------------------------------------------ staying on top (BAR-031)
    IntPtr Over()
    {
        var c = new Win32.POINT { X = (_g.Pill.Left + _g.Pill.Right) / 2, Y = (_g.Pill.Top + _g.Pill.Bottom) / 2 };
        IntPtr w = FrameNative.WindowFromPoint(c);
        return w == IntPtr.Zero ? w : Win32.GetAncestor(w, 2 /* GA_ROOT */);
    }

    /// main.js raiseBar: HWND_TOPMOST only puts us IN the topmost band; prove we are first at the pill's centre.
    void RaiseBar(string why)
    {
        const uint f = FrameNative.SWP_NOMOVE | FrameNative.SWP_NOSIZE | FrameNative.SWP_NOACTIVATE;
        Win32.SetWindowPos(_hwnd, FrameNative.HWND_TOPMOST, 0, 0, 0, 0, f);
        IntPtr over = Over();
        if (over != _hwnd) { Win32.SetWindowPos(_hwnd, FrameNative.HWND_TOPMOST, 0, 0, 0, 0, f); over = Over(); }
        _raiseTimes.Add(Environment.TickCount64);
        if (over != _hwnd) Host.Log.Line($"bar raise ({why}): still covered by a {Win32.ClassName(over)} window");
    }

    /// While up: something new over the pill is answered with a raise, at most 20 in 5 s so two always-on-top
    /// apps cannot fight forever (said once).
    void CheckOnTop(string why)
    {
        if (!IsUp) return;
        IntPtr over = Over();
        if (over == _hwnd || over == IntPtr.Zero) return;
        long now = Environment.TickCount64;
        _raiseTimes.RemoveAll(t => now - t > 5000);
        if (_raiseTimes.Count >= 20) {
            if (!_gaveUp) { _gaveUp = true; Host.Log.Line($"bar raise: gave up after 20 raises in 5 s; a {Win32.ClassName(over)} window keeps covering the bar"); }
            return;
        }
        _gaveUp = false;
        RaiseBar($"{why}: covered by a {Win32.ClassName(over)} window");
    }

    /// Armed only while up (hidden = no hooks): a window shown or brought to the front, and the Windows taskbar's
    /// own thread, trigger a CheckOnTop.
    void ArmWatch()
    {
        if (_hookShow != IntPtr.Zero) return;
        _hookProc ??= OnWinEvent;
        const uint OutOfContextSkipOwn = 0x0 | 0x2;
        _hookShow = Win32.SetWinEventHook(0x8002, 0x8002, IntPtr.Zero, _hookProc, 0, 0, OutOfContextSkipOwn);   // EVENT_OBJECT_SHOW
        _hookFg = Win32.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _hookProc, 0, 0, OutOfContextSkipOwn);     // EVENT_SYSTEM_FOREGROUND
        IntPtr tray = FrameNative.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero) {
            uint tid = Win32.GetWindowThreadProcessId(tray, out uint tpid);
            _hookTray = Win32.SetWinEventHook(0x8002, 0x800B, IntPtr.Zero, _hookProc, tpid, tid, OutOfContextSkipOwn);
        }
    }

    void DisarmWatch()
    {
        foreach (var h in new[] { _hookShow, _hookFg, _hookTray }) if (h != IntPtr.Zero) Win32.UnhookWinEvent(h);
        _hookShow = _hookFg = _hookTray = IntPtr.Zero;
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || !IsUp) return;
        bool trayHook = hook == _hookTray;
        if (!trayHook && Win32.GetAncestor(hwnd, 2) != hwnd) return;
        string src = trayHook ? "the Windows taskbar showed or moved" : ev == 3 ? "a window came to the front" : "a window was shown";
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => CheckOnTop("watch: " + src));
    }
}
