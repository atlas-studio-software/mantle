using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mantle.App.Native;
using Mantle.Core.Data;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace Mantle.App;

// One dock cell: CellVm lives in Surfaces/Bar/Tasks/BarWindow.Tasks.cs (native-bar-tasks).

// ready.json / cells.json (the bench), source-generated: no reflection, trim and AOT safe
internal sealed record RectJson(int x, int y, int w, int h);
internal sealed record MarkJson(string name, double sinceStartMs);
internal sealed record ReadyJson(long processStartUnixMs, long mainUnixMs, long firstFrameUnixMs, double scale, string theme, string backdrop,
    RectJson window, RectJson pill, bool quiet, string frame, string title, MarkJson[] marks);
internal sealed record CellJson(string name, string title, string automationId, string exe, uint pid, bool active, bool hasIcon, int x, int y, int w, int h, string status);
internal sealed record CellsJson(long at, int rebuilds, CellJson[] cells);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReadyJson))]
[JsonSerializable(typeof(CellsJson))]
internal sealed partial class BenchJson : JsonSerializerContext { }

public sealed partial class BarWindow : Window
{
    // bar.css / main.js geometry, DIP
    const double BarH = 48, Lift = 14, SideGapTotal = 28, MaxW = 1120;
    const double ShadowTop = 56, ShadowSide = 28;   // room in the window for the light drop shadow

    readonly IntPtr _hwnd;
    // the current geometry (Surfaces/Bar/Frame/BarGeometry.cs), re-read on display changes and every reveal
    double _scale;
    Win32.RECT _monitor;
    double _pillW;
    RectInt32 _winPx;
    Win32.RECT _pillPx;
    readonly string? _benchOut = Host.Args.BenchOut;
    bool _dark;
    readonly ObservableCollection<CellVm> _cells = new();
    readonly DispatcherQueueTimer _clock;
    long _firstFrameMs;
    string _backdropState = "not captured";
    string _frameState = "not set";
    Backdrop.Shot? _shot;
    double _blurNow = -1;
    readonly Surfaces.Bar.Frame.Chips _chips = new();

    internal BarWindow()
    {
        InitializeComponent();
        _dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // the primary monitor and its scale, before anything is placed; the frame mode (hover, dock or up)
        FrameMode = PickMode();
        ApplyGeometry(Surfaces.Bar.Frame.BarGeometry.Compute(), "start");
        Root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light;

        SystemBackdrop = new TransparentBackdrop();
        ConfigureWindow();
        PaintBackdrop();
        if (!_dark) AddShadow();   // dark: the lip only, no drop shadow (bar.css, dark .bar --pane-drop)
        BuildGlyphs();
        PoseDown();

        Dock.ItemsSource = _cells;
        StartTasks();   // the strip: pins + running apps from the platform (Surfaces/Bar/Tasks)

        _clock = DispatcherQueue.CreateTimer();
        _clock.IsRepeating = false;
        _clock.Tick += (_, _) => Tick();

        CompositionTarget.Rendering += OnFirstRender;
        Closed += (_, _) => StopTasks();
    }

    void ConfigureWindow()
    {
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);

        // The 1 px frame line the spike showed around the window: a plain WS_POPUP with no caption,
        // thick frame or border bits, and SWP_FRAMECHANGED so Windows recomputes the frame now.
        long st = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE).ToInt64();
        long frameBits = Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_BORDER | Win32.WS_DLGFRAME;
        long want = (st & ~frameBits) | Win32.WS_POPUP;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_STYLE, new IntPtr(want));
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        long got = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE).ToInt64();
        _frameState = $"style 0x{st:X8} -> 0x{got:X8}" + ((got & frameBits) == 0 && (got & Win32.WS_POPUP) != 0 ? " (WS_POPUP, no frame bits)" : " (frame bits still set)");
        Host.Log.Line("frame: " + _frameState);

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW;
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) {
            bool ok = Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
            Host.Log.Line(ok ? "quiet: layered alpha 0, click-through, not activatable, tool window"
                             : "quiet: SetLayeredWindowAttributes FAILED (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "); the window may be visible");
        }
        Host.Log.Line("see-through: " + TransparentBackdrop.MakeSeeThrough(_hwnd));
        AppWindow.MoveAndResize(_winPx);
    }

    internal void ShowBar()
    {
        if (Host.Quiet) AppWindow.Show(false);   // never activated: a test window never takes the keyboard
        else {
            // WinUI starts drawing at Activate; the bar is not focusable (WIN-001), so the app that was in front
            // gets the foreground straight back (CI: a cold start took it from the app in front)
            IntPtr prevFg = Win32.GetForegroundWindow();
            Activate();
            if (prevFg != IntPtr.Zero && prevFg != _hwnd && Win32.GetForegroundWindow() == _hwnd) Win32.SetForegroundWindow(prevFg);
        }
        // after the first show: a click on the bar must never take the focus from the app in front
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        // the presenter put WS_DLGFRAME | WS_SYSMENU back before the show (CI run 1: 0x84480000), so
        // the frame bits are stripped again now that the window is up
        long frameBits = Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_BORDER | Win32.WS_DLGFRAME;
        long st = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_STYLE, new IntPtr((st & ~frameBits) | Win32.WS_POPUP));
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        long got = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE).ToInt64();
        _frameState += $"; after show 0x{st:X8} -> 0x{got:X8}" + ((got & frameBits) == 0 ? " (no frame bits)" : " (frame bits still set)");
        Host.Log.Line("frame: after show: " + _frameState);
        Shell.Windowing.TopmostProbe.Start(_hwnd, DispatcherQueue);   // CI only (MANTLE_TEST_TOPMOST_PROBE)
        System.Threading.Tasks.Task.Run(() =>
        {
            var n = Surfaces.Bar.Frame.Chips.NotificationAccess();   // a WinRT call: off the UI thread
            // once the notification centre has a list, its health() owns the "notifs" chip (lane native-r7-notif)
            DispatcherQueue.TryEnqueue(() => { if (n != null && _notifs?.Data == null) _chips.Fail(n.Key, n.Label, n.Reason, n.Tone); RenderPip(); });
        });
        Tick();
        StartFrame();
        StartQuickSettings();   // lane native-bar-qs: Volume / Network / Battery open the quick-settings pop-up (Surfaces/Bar/QuickSettings)
        StartNotifications();   // lane native-r7-notif: the bell + centre, the tray strip, the More menu (Surfaces/Bar/Notifications, Tray, Chips)
    }

    // ------------------------------------------------------------------ the glass
    static double Saturation(bool dark) => dark ? 1.0 : 1.5;   // glass-light.css: light saturate(150%), dark none

    void PaintBackdrop()
    {
        var glass = Host.Data.EffectiveGlass;
        if (!Host.Args.NoBackdrop) {
            try {
                // one capture, before the window shows, wide enough for either theme's blur
                _shot = Backdrop.Grab(_pillPx, _monitor, Math.Max(glass.Dark.BlurPx, glass.Light.BlurPx) * _scale);
                _backdropState = _shot != null ? "captured" : "the screen could not be read";
            } catch (Exception e) { _backdropState = "capture failed: " + e.Message; }
        } else _backdropState = "off (--no-backdrop)";
        RenderGlass();
    }

    void RenderGlass()
    {
        double blur = Host.Data.EffectiveGlass.For(_dark).BlurPx;
        WriteableBitmap? pic = null;
        if (_shot != null) {
            try { pic = Backdrop.Render(_shot, _pillPx, blur * _scale, Saturation(_dark)); }
            catch (Exception e) { _backdropState = "render failed: " + e.Message; }
        }
        if (pic != null) {
            GlassImage.ImageSource = pic;
            Tint.ClearValue(Border.BackgroundProperty);
            Tint.Background = (Brush)ThemeToken("LgPaneFill");
        } else {
            Glass.Background = null;
            Tint.Background = (Brush)ThemeToken("LgPaneFillFallback");
        }
        _blurNow = blur;
        Host.Log.Line($"backdrop: {_backdropState}; {( _dark ? "dark" : "light")} blur {blur.ToString(CultureInfo.InvariantCulture)} px");
    }

    // ------------------------------------------------------------------ the pip on "more" (bar.js renderChips)
    void RenderPip(bool force = false)
    {
        var p = _chips.Pip(_shellState);   // the shell state arrives over lane native-r7-notif's surface link (Surfaces/Bar/Notifications)
        if (p == null && !force) return;
        var (shown, warn, tip) = p ?? _pipNow;
        _pipNow = (shown, warn, tip);
        MorePip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        MorePipDot.Fill = (Brush)(warn ? TokenOr("LgWarn", "LgInk2") : ThemeToken("LgInk2"));
        MorePipRing.Fill = (Brush)TokenOr("LgFillStrong", "LgPaneFill");
        ToolTipService.SetToolTip(MoreBtn, shown ? tip : "More");
        AutomationProperties.SetHelpText(MorePip, shown ? (warn ? "warn: " : "") + tip : "");
        Host.Log.Line(shown ? $"more pip: {(warn ? "warn" : "plain")} ({tip.Replace("\n", "; ")})" : "more pip: none");
    }
    (bool, bool, string) _pipNow = (false, false, "");

    /// A token that may not be in Tokens.xaml yet: the stand-in is said in the log, never silent.
    object TokenOr(string key, string standIn)
    {
        try { return ThemeToken(key); }
        catch (InvalidOperationException) { Host.Log.Line($"look: Tokens.xaml has no {key} yet; the bar uses {standIn} for it"); return ThemeToken(standIn); }
    }

    /// A token from Tokens.xaml for the theme the bar shows now (code cannot use {ThemeResource}).
    /// Tokens.xaml is merged through Theme/Theme.xaml, so the search goes into nested merged dictionaries.
    object ThemeToken(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var md in Application.Current.Resources.MergedDictionaries)
            if (md is not Microsoft.UI.Xaml.Controls.XamlControlsResources && FindThemeToken(md, theme, key, 0) is object v)
                return v;
        throw new InvalidOperationException("Tokens.xaml has no " + theme + " " + key);
    }

    static object? FindThemeToken(ResourceDictionary d, string theme, string key, int depth)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out object? td) && td is ResourceDictionary rd && rd.TryGetValue(key, out object? v))
            return v;
        if (depth >= 4) return null;   // Theme.xaml -> Tokens.xaml is one level; the cap only stops a cycle
        foreach (var md in d.MergedDictionaries)
            if (FindThemeToken(md, theme, key, depth + 1) is object found)
                return found;
        return null;
    }

    // ------------------------------------------------------------------ Electron's settings, live
    internal void OnDataChanged(DataChange c)
    {
        Host.Log.Line("data changed: " + c.Description);
        switch (c.File) {
            case DataFile.Theme:
                if (Host.Args.Theme != null) { Host.Log.Line($"look: theme.json changed, but this run is pinned by --theme={Host.Args.Theme}"); return; }
                // the host's theme follows theme.json too (LookShell.ThemeFileChanged) and saves it behind the tell, so
                // once it is up the bar takes the theme from it alone: a save landing on disk never flips the bar back
                // to an older pick that a newer one is about to overwrite
                if (MantleApp.ThemeNow != null) return;
                SetTheme(Resolve(Host.Data.Theme.Value.Mode));
                break;
            case DataFile.LocalLook:
            case DataFile.SharedLook:
                if (Math.Abs(Host.Data.EffectiveGlass.For(_dark).BlurPx - _blurNow) > 0.001) RenderGlass();
                break;
            default:
                Host.Log.Line("look: " + c.File + " is read, but the bar does not use it yet");
                break;
        }
    }

    // The host's theme (MantleApp.ThemeShown: theme.set, Match Windows through the host's own watch of the key this run
    // follows, another app's look, theme.json changed on disk): the bar shows what the host shows. theme.json's own path
    // (OnDataChanged) stays as well: both resolve Match Windows from the same key, so they agree, and SetTheme is a no-op
    // for the second. App.OnLaunched calls this once, before the host starts.
    internal void FollowHostTheme()
    {
        MantleApp.ThemeShown += OnThemeShown;
        Closed += (_, _) => MantleApp.ThemeShown -= OnThemeShown;
        if (MantleApp.ThemeNow is { } now) OnThemeShown(now);
    }

    void OnThemeShown(Mantle.Core.Look.ThemePayload p)
    {
        if (p.Theme is not ("dark" or "light")) { Host.Log.Line($"look: the host's theme \"{p.Theme}\" is not light or dark; the bar keeps {(_dark ? "dark" : "light")}"); return; }
        SetTheme(p.Theme == "dark", $"the host's theme is {p.Theme} ({Mantle.Core.Look.ThemeCore.ModeWords(p.View.Mode)})");
    }

    /// light/dark from a mode; System follows Windows' app mode from the key this run follows (MantleApp.ThemeKey: a
    /// test run's scratch key, else Windows' own), the same read as the host's; light when unknown.
    static bool Resolve(ThemeMode mode)
    {
        if (mode == ThemeMode.Dark) return true;
        if (mode == ThemeMode.Light) return false;
        var w = Mantle.Core.Look.ThemeCore.ReadWindowsMode(MantleApp.ThemeKey);
        if (w.Mode is not ("dark" or "light")) Host.Log.Line("look: Windows' app mode could not be read (" + (w.Why ?? "no answer") + "); Match Windows shows light");
        return w.Mode == "dark";
    }

    void SetTheme(bool dark, string? why = null)
    {
        if (dark == _dark) { Host.Log.Line($"look: {(why != null ? why + "; " : "")}the bar already shows {(dark ? "dark" : "light")}"); return; }
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (dark) ElementCompositionPreview.SetElementChildVisual(ShadowHost, null);
        else AddShadow();
        RenderGlass();
        if (FrameMode == "hover" && !IsUp) ClearPicture();   // a hidden bar keeps no picture (BAR-035)
        _live?.Retheme(dark);
        RenderPip(force: true);
        Host.Log.Line($"look: {why ?? "theme.json says " + ThemeSettings.Word(Host.Data.Theme.Value.Mode)}; the bar now shows {(dark ? "dark" : "light")}");
    }

    // The light island's drop shadow, from glass-light.css -lg-drop-up:
    //   0 -1px 1px rgba(14,18,38,.05), 0 -10px 28px -12px rgba(14,18,38,.22), 0 -26px 56px -28px rgba(14,18,38,.30)
    // A negative spread shrinks the shape before the blur; the third layer's -28 leaves a 48 tall
    // pill no shape at all, so it draws nothing, in CSS as here.
    void AddShadow()
    {
        var c = ElementCompositionPreview.GetElementVisual(ShadowHost).Compositor;
        var host = c.CreateContainerVisual();
        void Layer(double dy, double blur, double spread, byte alpha)
        {
            float w = (float)(_pillW + 2 * spread), h = (float)(BarH + 2 * spread);
            if (w <= 0 || h <= 0) return;
            float r = (float)Math.Max(0, 24 + spread);
            var geo = c.CreateRoundedRectangleGeometry(); geo.Size = new Vector2(w, h); geo.CornerRadius = new Vector2(r, r);
            var shape = c.CreateSpriteShape(geo); shape.FillBrush = c.CreateColorBrush(Colors.Black);
            var sv = c.CreateShapeVisual(); sv.Size = new Vector2(w, h); sv.Shapes.Add(shape);
            var surf = c.CreateVisualSurface(); surf.SourceVisual = sv; surf.SourceSize = new Vector2(w, h);
            var sprite = c.CreateSpriteVisual();
            sprite.Size = new Vector2(w, h);
            sprite.Offset = new Vector3((float)(ShadowSide - spread), (float)(ShadowTop - spread), 0);
            var ds = c.CreateDropShadow();
            ds.Mask = c.CreateSurfaceBrush(surf);
            ds.BlurRadius = (float)blur;
            ds.Offset = new Vector3(0, (float)dy, 0);
            ds.Color = Windows.UI.Color.FromArgb(alpha, 14, 18, 38);
            sprite.Shadow = ds;
            host.Children.InsertAtTop(sprite);
        }
        Layer(-1, 1, 0, 13);
        Layer(-10, 28, -12, 56);
        Layer(-26, 56, -28, 77);
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, host);
    }

    // ------------------------------------------------------------------ glyphs (the SVGs in ui/bar)
    static readonly Regex Tok = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled);
    static string Norm(string d)
    {
        var sb = new StringBuilder();
        foreach (Match m in Tok.Matches(d)) sb.Append(m.Value).Append(' ');
        return sb.ToString().Trim();
    }
    static string Circle(double cx, double cy, double r)
        => FormattableString.Invariant($"M{cx - r} {cy} a{r} {r} 0 1 0 {2 * r} 0 a{r} {r} 0 1 0 {-2 * r} 0 Z");
    static string RoundRect(double x, double y, double w, double h, double r)
        => FormattableString.Invariant($"M{x + r} {y} h{w - 2 * r} a{r} {r} 0 0 1 {r} {r} v{h - 2 * r} a{r} {r} 0 0 1 {-r} {r} h{-(w - 2 * r)} a{r} {r} 0 0 1 {-r} {-r} v{-(h - 2 * r)} a{r} {r} 0 0 1 {r} {-r} Z");

    static UIElement PathEl(string d, bool fill, double stroke = 1.25, string brush = "LgInk2")
    {
        string paint = fill
            ? $"Fill='{{ThemeResource {brush}}}'"
            : FormattableString.Invariant($"Stroke='{{ThemeResource {brush}}}' StrokeThickness='{stroke}' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'");
        string xaml = "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + Norm(d) + "' " + paint + "/>";
        return (UIElement)XamlReader.Load(xaml);
    }

    void BuildGlyphs()
    {
        // the Mantle mark (index.html #start): one even-odd path, the M cut out of a rounded square
        MarkHost.Children.Add(PathEl("M7.2 3h9.6A4.2 4.2 0 0 1 21 7.2v9.6a4.2 4.2 0 0 1-4.2 4.2H7.2A4.2 4.2 0 0 1 3 16.8V7.2A4.2 4.2 0 0 1 7.2 3Zm1.05 4.6a.6.6 0 0 0-.6.6v7.6a.6.6 0 0 0 .6.6h1.3a.6.6 0 0 0 .6-.6v-3.63l1.33 2.06a.6.6 0 0 0 1.01 0l1.33-2.06v3.63a.6.6 0 0 0 .6.6h1.3a.6.6 0 0 0 .6-.6V8.2a.6.6 0 0 0-.6-.6h-1.2a.6.6 0 0 0-.51.29L12 11.3 9.96 7.89a.6.6 0 0 0-.51-.29h-1.2Z", true));
        DownloadsHost.Children.Add(PathEl("M8 2.3v7.6M5 7.1l3 2.9 3-2.9", false));
        DownloadsHost.Children.Add(PathEl("M2.6 10.1v1.9c0 .8.6 1.4 1.4 1.4h8c.8 0 1.4-.6 1.4-1.4v-1.9", false));
        // volume (bar.js volumeSvg): the speaker, plus 0-3 waves by level. The level is not read in
        // the spike (CoreAudio is migration work): no audio device -> no waves, as the real bar
        // draws a null level; otherwise all three.
        VolumeHost.Children.Add(PathEl("M2.2 6.1h2.4L8.1 3.1v9.8L4.6 9.9H2.2z", true));
        if (Win32.waveOutGetNumDevs() > 0)
            foreach (var arc in new[] { "M10 6.2a2.6 2.6 0 0 1 0 3.6", "M11.8 4.9a5 5 0 0 1 0 6.2", "M13.6 3.6a7.4 7.4 0 0 1 0 8.8" })
                VolumeHost.Children.Add(PathEl(arc, false, 1.2));
        // network (net.js): the wired glyph. Wi-Fi bars are migration work.
        NetworkHost.Children.Add(PathEl(RoundRect(2, 2.6, 12, 8.4, 1.3), false, 1.2));
        NetworkHost.Children.Add(PathEl("M6 13.6h4M8 11v2.6", false, 1.2));
        NotifsHost.Children.Add(PathEl("M8 2.2a3.9 3.9 0 0 0-3.9 3.9v2.6L2.9 11h10.2l-1.2-2.3V6.1A3.9 3.9 0 0 0 8 2.2Z", false));
        NotifsHost.Children.Add(PathEl("M6.4 12.9a1.7 1.7 0 0 0 3.2 0", false));
        foreach (var cx in new[] { 3.5, 8, 12.5 }) MoreHost.Children.Add(PathEl(Circle(cx, 8, 1.3), true));
    }


    // ------------------------------------------------------------------ the clock (bar.js tick)
    void Tick()
    {
        var now = DateTime.Now;
        int h = now.Hour % 12; if (h == 0) h = 12;
        string t = h.ToString(CultureInfo.InvariantCulture) + ":" + now.ToString("mm:ss", CultureInfo.InvariantCulture) + (now.Hour < 12 ? " AM" : " PM");
        if (ClockT.Text != t) ClockT.Text = t;
        string d = now.ToString("d", CultureInfo.CurrentCulture);
        if (ClockD.Text != d) ClockD.Text = d;
        // one timer, re-aimed at the next second boundary (+20 ms: never paint the previous second)
        _clock.Interval = ClockInterval(now);   // seconds while up, minutes while down (BAR-417)
        _clock.Start();
    }

    // the dock: Surfaces/Bar/Tasks/BarWindow.Tasks.cs (SyncCells, clicks, cells.json)

    // ------------------------------------------------------------------ first frame, bench output
    void OnFirstRender(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnFirstRender;
        _firstFrameMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Host.Log.Line("first frame " + (_firstFrameMs - Host.Marks.MainUnixMs) + " ms after Main");
        Host.Marks.Add("first-frame");
        if (_benchOut == null) return;
        var marks = new List<MarkJson>();
        foreach (var m in Host.Marks.All) marks.Add(new MarkJson(m.Name, Math.Round(m.SinceStartMs, 1)));
        var ready = new ReadyJson(Host.Marks.ProcessStartUnixMs, Host.Marks.MainUnixMs, _firstFrameMs, _scale, _dark ? "dark" : "light", _backdropState,
            new RectJson(_winPx.X, _winPx.Y, _winPx.Width, _winPx.Height),
            new RectJson(_pillPx.Left, _pillPx.Top, _pillPx.Right - _pillPx.Left, _pillPx.Bottom - _pillPx.Top),
            Host.Quiet, _frameState, Title, marks.ToArray());
        Write("ready.json", JsonSerializer.Serialize(ready, BenchJson.Default.ReadyJson));
    }

    void Write(string name, string json)
    {
        try { File.WriteAllText(Path.Combine(_benchOut!, name), json); }
        catch (Exception e) { Host.Log.Line("write " + name + " failed: " + e.Message); }
    }
}
