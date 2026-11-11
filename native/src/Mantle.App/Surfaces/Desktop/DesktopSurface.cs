using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.Core.Features.Desktop;
using Mantle.Core.Look;
using Mantle.Core.Platform;
using Mantle.Core.State;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Desktop;

// THE DESKTOP LAYER (ui/desktop/, lib/windows.js createDesktop, main.js attachDesktop; lane native-r7-desktop).
//
// Live: one frameless, transparent, never-activated, out-of-the-app-lists window the size of the PRIMARY display, put
// behind every app as part of the desktop by the sidecar's `desktop {hwnd}` (Win11 24H2+: a child of Progman straight
// UNDER the icon view and in front of the wallpaper, mode progman-front; else the WorkerW modes), rechecked every 30 s
// in progman-front. Explorer's icon view sits over it and takes every click; Mantle draws no icons of its own.
// --test: an ordinary 960x540 window that is NEVER attached (a dry run, logged), see-through so the test's own backdrop
// shows (Electron's #1a1c20 stand-in is a colour literal the native lint refuses), TestQuiet unless MANTLE_TEST_VISIBLE=1.
// MANTLE_TEST_DESKTOP (test only, DeskFixture) freezes the clock and replaces the readings so every runner draws the same.
//
// Log lines the tests read: "desktop: shown ...", "desktop: panes (physical px on screen) ...", "desktop: theme ...",
// "desktop: menu opened at ...", "desktop: menu: <row> ...", and every dry run.

[Mantle.App.Shell.Surface("desktop", AtStart = true)]
internal sealed partial class DesktopSurface : Mantle.App.Shell.ISurface
{
    // ---- wired by App.xaml.cs (block "desktop wiring") ----
    static IPlatform? _platform;
    static Action<Action>? _postHost;
    static Func<Func<ShellState, ShellState>, bool>? _setState;

    /// The sidecar (for the live attach / recheck) and the "desktop" state piece, both on the host thread.
    public static void Wire(IPlatform platform, Action<Action> postHost, Func<Func<ShellState, ShellState>, bool> setState)
    {
        _platform = platform; _postHost = postHost; _setState = setState;
    }

    /// The live instance (the deskmenu surface opens its menu through it).
    internal static DesktopSurface? Current { get; private set; }

    Mantle.App.Shell.ISurfaceContext _ctx = null!;
    Window? _win;
    Grid? _root;
    IntPtr _hwnd;
    DeskPanes? _panes;
    DeskFixture? _fx;
    bool _dark, _widgetsOn = true, _shown;
    readonly List<IDisposable> _subs = new();
    DispatcherQueueTimer? _clockT, _statsT, _mcT, _recheckT, _reportT;
    int _mcFails, _mcChain;
    readonly SysStats _stats = new();
    string _lastRects = "";
    DeskMenuWindow? _menu;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public bool Shown => _shown && _win?.AppWindow is { IsVisible: true };

    public void Create(Mantle.App.Shell.ISurfaceContext ctx)
    {
        _ctx = ctx;
        Current = this;
        if (ctx.Test) {
            _fx = DeskFixture.Parse(Host.Env("MANTLE_TEST_DESKTOP"), out var err);
            if (err != null) ctx.Log.Line(err);
            if (_fx != null) ctx.Log.Line("a --test run with MANTLE_TEST_DESKTOP: the clock, numbers, Mission Control and strip are its made-up readings");
        }
        _panes = new DeskPanes(B);
        _root = new Grid { Background = new SolidColorBrush() };
        _root.Children.Add(_panes.Cluster);
        AutomationProperties.SetAutomationId(_root, "desktop.layer");
        AutomationProperties.SetName(_root, "Desktop layer");   // a panel with no name has no automation peer: the tests read its theme here
        _win = new Window { Title = "Mantle desktop", Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;   // not resizable even in --test: a resizable borderless window stayed a white sheet on the runner
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        _win.SystemBackdrop = new Mantle.App.Surfaces.Osd.SeeThroughBackdrop(ctx.Log.Line);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        Mantle.App.Shell.Windowing.WindowKinds.Frameless(_hwnd);
        Mantle.App.Shell.Windowing.WindowKinds.StripFrame(_hwnd);
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(_hwnd);
        Place();
        _root.SizeChanged += (_, _) => { Fit(); QueueReport("a resize"); };
        _panes.Cluster.SizeChanged += (_, _) => QueueReport("a layout change");
        // the strip's room is what the other three leave, so it follows their sizes too (the Mission Control tile shrinks
        // from its 132 DIP frame to a note when the dashboard is down: the strip was left at the old room, cut short)
        foreach (var pane in new FrameworkElement[] { _panes.Clock, _panes.Stats, _panes.Mc })
            pane.SizeChanged += (_, _) => { if (_root != null && _root.ActualHeight > 0) _panes?.FitHeight(_root.ActualHeight); };

        _subs.Add(ctx.On<ThemePayload>("theme", t => { _dark = t.Theme == "dark"; Paint(); ctx.Log.Line($"theme {t.Theme}: the panes repainted"); }));
        _subs.Add(ctx.OnState(s => Strip(s)));
        _subs.Add(ctx.On<DeskWidgets>("deskmenu.widgets", w => SetWidgets(w.On, "the switch")));
        _subs.Add(ctx.On<DeskMenuShow>("deskmenu.show", m => OpenMenu(m.X, m.Y, m.Seq, "a right-click on the desktop")));
        _subs.Add(ctx.On<DeskMenuHide>("deskmenu.hide", h => CloseMenu(h.Why)));
        if (_fx?.WidgetsOff == true) SetWidgets(false, "MANTLE_TEST_DESKTOP");
        Paint();
        Strip(ctx.State);
        StartClock(); StartStats(); StartMc("made");
        SetDesktopPiece(ctx.Test ? new DesktopPiece(false, null, DesktopLayer.TestNeverAttached) : null);
        ctx.Log.Line($"made hidden ({(ctx.Test ? Host.Quiet ? "test: quiet" : "test: visible" : "live")})");
        // shown here, before the bar's first show, so the bar lands in front of it (the app's front window is the bar's)
        FirstShow();
    }

    void FirstShow()
    {
        if (_win == null) return;
        Show("Mantle started");
        if (!_ctx.Test) _ = AttachAsync("Mantle started");
        else _ctx.Log.Line("dry run (--test): would ask the sidecar to put the layer under the icons (desktop {hwnd}); nothing was sent, Explorer was not touched");
    }

    /// The window's place: the primary display's bounds live; an ordinary 960x540 window in --test, at the work area's corner + 40.
    void Place()
    {
        var mon = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 }, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfoW(mon, ref mi);
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        if (_ctx.Test) {
            int w = (int)Math.Round(DesktopLayer.TestWidth * scale), h = (int)Math.Round(DesktopLayer.TestHeight * scale);
            _win!.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(mi.rcWork.Left + 40, mi.rcWork.Top + 40, w, h));
        } else {
            var r = mi.rcMonitor;
            _win!.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }
    }

    void Fit()
    {
        if (_root == null || _panes == null) return;
        bool shortWin = _root.ActualHeight > 0 && _root.ActualHeight <= 760;
        _panes.Paint(_dark, shortWin);
        _panes.FitHeight(_root.ActualHeight);
    }

    void Paint()
    {
        if (_root == null || _panes == null) return;
        _root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light;
        _panes.Paint(_dark, _root.ActualHeight > 0 && _root.ActualHeight <= 760);
        AutomationProperties.SetItemStatus(_root, _dark ? "dark" : "light");
        _menu?.Paint(_dark);
    }

    // ---- ISurface ----

    public void Show(string why)
    {
        if (_win == null) return;
        // showInactive, never activated: an Activate (the menu's road) made the layer the app's front window, ahead of
        // the bar (ci smoke: MainWindowHandle) and took the foreground for a moment (bench focus checks). It draws without
        // one; what kept it a white sheet was a resizable presenter (CI 43023226210).
        _win.AppWindow.Show(false);
        Mantle.App.Shell.Windowing.WindowKinds.StripFrame(_hwnd);   // the presenter puts frame bits back at a show
        _shown = true;
        var pos = _win.AppWindow.Position; var size = _win.AppWindow.Size;
        string where = _ctx.Test ? "as an ordinary window (a test run never puts it under the icons)" : "on the primary display";
        _ctx.Log.Line($"shown {where} at {pos.X},{pos.Y} {size.Width}x{size.Height} px, scale {(Win32.GetDpiForWindow(_hwnd) / 96.0).ToString("0.##", CultureInfo.InvariantCulture)} ({why})");
        PaintClock();
        QueueReport("shown");
    }

    public void Hide(string why)
    {
        if (_win == null) return;
        CloseMenu(why);
        _win.AppWindow.Hide();
        _shown = false;
        _ctx.Log.Line($"parked, hidden ({why})");
    }

    public void Destroy(string why)
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        foreach (var t in new[] { _clockT, _statsT, _mcT, _recheckT, _reportT }) t?.Stop();
        _mcChain++;
        CloseMenu(why);
        try { _win?.Close(); } catch (Exception e) { _ctx.Log.Line("its window did not close: " + e.Message); }
        _win = null; _root = null; _panes = null; _shown = false;
        if (Current == this) Current = null;
        _ctx.Log.Line($"destroyed ({why})");
    }

    // ---- the state piece and the live attach (never in --test) ----

    void SetDesktopPiece(DesktopPiece? d)
    {
        if (d == null || _setState == null || _postHost == null) return;
        var set = _setState;
        _postHost(() => set(s => s with { Desktop = d }));
    }

    async Task AttachAsync(string why)
    {
        var r = await Sidecar("desktop", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture) });
        if (!r.Ok) {
            SetDesktopPiece(new DesktopPiece(false, null, DesktopLayer.AttachFailed(r.Error ?? "no reason came back", false)));
            _ctx.Log.Line($"NOT under the icons: {r.Error} ({why})");
            return;
        }
        string? mode = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        SetDesktopPiece(new DesktopPiece(true, mode, DesktopLayer.Attached(mode)));
        _ctx.Log.Line($"under the icons ({mode ?? "mode not reported"}) ({why})");
        if (mode == "progman-front") {
            _recheckT ??= Timer(TimeSpan.FromMilliseconds(DesktopLayer.RecheckMs), () => _ = RecheckAsync(), repeat: true);
            _recheckT.Start();
        }
    }

    bool _recheckBad;
    async Task RecheckAsync()
    {
        var r = await Sidecar("desktop", new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["recheck"] = true });
        if (!r.Ok) { _recheckBad = true; SetDesktopPiece(new DesktopPiece(false, "progman-front", DesktopLayer.RecheckFailed(r.Error ?? "no reason came back"))); return; }
        if (_recheckBad) { _recheckBad = false; SetDesktopPiece(new DesktopPiece(true, "progman-front", DesktopLayer.RecheckBack)); }
    }

    /// A sidecar call from this UI thread: sent on the host thread, answered back here.
    Task<PlatformReply> Sidecar(string verb, JsonObject args)
    {
        var tcs = new TaskCompletionSource<PlatformReply>();
        var q = _ctx.Dispatcher;
        if (_platform == null || _postHost == null) { tcs.SetResult(PlatformReply.Fail(PlatformOutcome.Refused, "the desktop layer was not wired to the sidecar")); return tcs.Task; }
        var p = _platform;
        _postHost(async () => {
            PlatformReply r;
            try { r = await p.CallAsync(verb, args, TimeSpan.FromMilliseconds(4000)); }
            catch (Exception e) { r = PlatformReply.Fail(PlatformOutcome.Refused, e.Message); }
            if (!q.TryEnqueue(() => tcs.TrySetResult(r))) tcs.TrySetResult(r);
        });
        return tcs.Task;
    }

    // ---- the widgets ----

    DispatcherQueueTimer Timer(TimeSpan due, Action tick, bool repeat = false)
    {
        var t = _ctx.Dispatcher.CreateTimer();
        t.Interval = due; t.IsRepeating = repeat;
        t.Tick += (_, _) => tick();
        return t;
    }

    void StartClock()
    {
        PaintClock();
        if (_fx?.Now != null) { _ctx.Log.Line($"the clock is frozen at {DeskClock.Of(_fx.Now.Value).Text} (MANTLE_TEST_DESKTOP)"); return; }
        _clockT = Timer(TimeSpan.FromMilliseconds(DeskClock.NextDelayMs(DateTime.Now)), () => {
            PaintClock();
            _clockT!.Interval = TimeSpan.FromMilliseconds(DeskClock.NextDelayMs(DateTime.Now));
            _clockT.Start();
        });
        _clockT.Start();
    }

    void PaintClock()
    {
        var now = _fx?.Now ?? DateTime.Now;
        _panes?.SetClock(DeskClock.Of(now), DeskClock.DateLine(now));
    }

    void StartStats()
    {
        if (_fx?.Stats != null) { _panes?.SetStats(_fx.Stats); return; }
        _panes?.SetStats(DeskStats.Boot);
        void Read() => _ = Task.Run(() => _stats.Read()).ContinueWith(t => _ctx.Dispatcher.TryEnqueue(() => _panes?.SetStats(t.Result)), TaskScheduler.Default);
        Read();
        _statsT = Timer(TimeSpan.FromMilliseconds(DeskStats.EveryMs), Read, repeat: true);
        _statsT.Start();
    }

    void StartMc(string why)
    {
        int chain = ++_mcChain;
        _mcFails = 0;
        if (_fx != null) {
            if (_fx.McOk) _panes?.SetMc(true, McNote.Live);
            else _panes?.SetMc(false, McProbe.Offline(_fx.McReason!, McProbe.NextDelayMs(1), _fx.Now ?? DateTime.Now));
            return;
        }
        _ = ProbeAsync(chain);
    }

    async Task ProbeAsync(int chain)
    {
        bool ok = false; string reason = "";
        try {
            using var r = await Http.GetAsync(McProbe.Origin + "/", HttpCompletionOption.ResponseHeadersRead);
            ok = (int)r.StatusCode < 500;
            if (!ok) reason = $"it answered {(int)r.StatusCode} {r.ReasonPhrase}";
        } catch (Exception e) { reason = e.InnerException?.Message ?? e.Message; }
        if (string.IsNullOrEmpty(reason) && !ok) reason = McProbe.NoReason;
        if (chain != _mcChain) return;   // stopped or restarted while it was out: stale
        _mcFails = ok ? 0 : _mcFails + 1;
        int next = McProbe.NextDelayMs(_mcFails);
        _panes?.SetMc(ok, ok ? McNote.Live : McProbe.Offline(reason, next, DateTime.Now));
        _mcT?.Stop();
        _mcT = Timer(TimeSpan.FromMilliseconds(next), () => _ = ProbeAsync(chain));
        _mcT.Start();
    }

    static class McNote
    {
        /// The dashboard answers: Electron mounts it at half scale in an iframe; that needs WebView2 natively (not yet).
        public const string Live = "Mission Control answers at 127.0.0.1:7373; its live view is not drawn here yet.";
    }

    void Strip(ShellState s)
    {
        if (_panes == null) return;
        var rows = _fx?.Strip ?? DeskStrip.Rows(s);
        _panes.SetStrip(rows, s.Mode == ShellMode.Test);
        string line = rows.Count == 0 ? "nothing" : string.Join("; ", System.Linq.Enumerable.Select(rows, r => r.Title));
        if (line != _stripSaid) { _stripSaid = line; _ctx.Log.Line($"strip: {(rows.Count == 0 ? "hidden (nothing needs attention)" : rows.Count + " row(s): " + line)}"); }
        QueueReport("the strip changed");
    }
    string _stripSaid = "";

    void SetWidgets(bool on, string why)
    {
        _widgetsOn = on;
        _panes?.SetWidgets(on);
        _ctx.Log.Line($"widgets {(on ? "shown" : "hidden")} ({why})");
    }

    internal bool WidgetsOn => _widgetsOn;

    // ---- the pane rects (DESK-094 / 095): physical px on screen, coalesced 120 ms, only when they changed ----

    void QueueReport(string why)
    {
        if (_reportT == null) { _reportT = Timer(TimeSpan.FromMilliseconds(120), Report); }
        _reportT.Stop(); _reportT.Start();
    }

    void Report()
    {
        if (_win == null || _panes == null || _root?.XamlRoot == null || !_shown) return;
        double scale = _root.XamlRoot.RasterizationScale;
        var pos = _win.AppWindow.Position;
        var parts = new List<string>();
        foreach (var p in new[] { _panes.Clock, _panes.Stats, _panes.Mc, _panes.Strip }) {
            string key = (string)p.Tag;
            if (p.Visibility != Visibility.Visible || p.ActualWidth <= 0) { parts.Add($"{key}=0,0,0,0"); continue; }
            var at = p.TransformToVisual(_root).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            var r = DesktopLayer.ToScreen(at.X, at.Y, p.ActualWidth, p.ActualHeight, pos.X, pos.Y, scale);
            parts.Add($"{key}={r.X},{r.Y},{r.Width},{r.Height}");
        }
        string line = string.Join(" ", parts);
        if (line == _lastRects) return;
        _lastRects = line;
        _ctx.Log.Line($"panes (physical px on screen): {line} (scale {scale.ToString("0.##", CultureInfo.InvariantCulture)}, window {_root.ActualWidth.ToString("0", CultureInfo.InvariantCulture)}x{_root.ActualHeight.ToString("0", CultureInfo.InvariantCulture)} DIP)");
    }

    // ---- the menu ----

    /// Open the menu with its panel's corner on the screen point (physical px).
    internal void OpenMenu(int x, int y, int seq, string why)
    {
        if (_menu != null) CloseMenu("another right-click on the desktop");
        _menu = new DeskMenuWindow(_ctx, B, _dark, _widgetsOn, PickAsync, () => CloseMenu("a click outside it"), () => CloseMenu("Esc"));
        var r = _menu.Open(x, y);
        _ctx.Log.Line($"menu opened at {r.X},{r.Y} {r.Width}x{r.Height} px for the point {x},{y} ({why})");
        if (seq > 0) _ = _ctx.CallAsync("deskmenu.ack", new JsonObject { ["seq"] = seq });
    }

    /// The test road (deskmenu surface): open at the fixture's point, else 300,200 px into the layer.
    internal void OpenMenuForTest(string why)
    {
        var pos = _win?.AppWindow.Position ?? new global::Windows.Graphics.PointInt32();
        var at = _fx?.MenuAt ?? (300, 200);
        OpenMenu(pos.X + at.X, pos.Y + at.Y, 0, why);
    }

    internal bool MenuOpen => _menu != null;

    internal void CloseMenu(string why)
    {
        if (_menu == null) return;
        _menu.Close();
        _menu = null;
        _ctx.Log.Line($"menu closed ({why})");
    }

    async Task<string?> PickAsync(DeskMenuRow row)
    {
        if (row.Id == "widgets") {
            bool on = !_widgetsOn;
            var r = await _ctx.CallAsync("deskmenu.setWidgets", new JsonObject { ["on"] = on });
            string? why = !r.Ok ? r.Error : r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False
                ? (r.Result.TryGetProperty("reason", out var rr) ? rr.GetString() : "no reason was given") : null;
            _ctx.Log.Line($"menu: {row.Label}: {(why == null ? "done" : "did not work: " + why)}");
            if (why == null) { SetWidgets(on, "the desktop menu"); CloseMenu("a pick"); }
            return why;
        }
        var args = new JsonObject();
        if (row.Section != null) args["section"] = row.Section;
        if (row.Command == "settings.open") args["via"] = DeskMenu.Via;
        var res = await _ctx.CallAsync(row.Command!, args);
        _ctx.Log.Line($"menu: {row.Label}: {(res.Ok ? "done" : "did not work: " + res.Error)} ({row.Command}{(row.Section != null ? " " + row.Section : "")})");
        if (res.Ok) CloseMenu("a pick");
        return res.Ok ? null : res.Error;
    }

    // ---- tokens ----

    readonly HashSet<string> _missing = new();
    Brush B(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        if (_missing.Add(key)) _ctx?.Log.Line($"the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }
    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }
}

/// <summary>The test road for the menu: <c>--test-surface=show:deskmenu</c> opens it on the desktop layer, hide closes it.</summary>
[Mantle.App.Shell.Surface("deskmenu")]
internal sealed class DeskMenuSurface : Mantle.App.Shell.ISurface
{
    Mantle.App.Shell.ISurfaceContext? _ctx;
    public bool Shown => DesktopSurface.Current?.MenuOpen == true;
    public void Create(Mantle.App.Shell.ISurfaceContext ctx) { _ctx = ctx; }
    public void Show(string why)
    {
        if (DesktopSurface.Current is not { } d) { _ctx?.Log.Line($"no desktop layer to open the menu on ({why})"); return; }
        d.OpenMenuForTest(why);
    }
    public void Hide(string why) => DesktopSurface.Current?.CloseMenu(why);
    public void Destroy(string why) => DesktopSurface.Current?.CloseMenu(why);
}
