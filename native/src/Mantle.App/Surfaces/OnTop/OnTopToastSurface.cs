using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mantle.App.Native;
using Mantle.App.Shell;
using Mantle.App.Shell.Windowing;
using Mantle.App.Surfaces.Osd;
using Mantle.Core.Features.OnTop;
using Mantle.Core.Features.Osd;
using Mantle.Core.Look;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Mantle.App.Surfaces.OnTop;

// The keep-on-top result pop-up (lib/features/ontop.js toastWin / toast / hideToast, ui/ontop/; BAR-341): "Kept on top:
// Notes" with the reason or the hint ("Win+Ctrl+T again to stop") under it, at the volume pop-up's place, shown without
// taking focus, click-through, and raised to the top of the topmost band on EVERY show so it never comes up under the
// window just pinned. The pane fades after 1.8 s (3.4 s for a refusal with its reason); a backstop hides the window at
// 4.3 s; the window is destroyed after 30 s unused (OnTopToast in Mantle.Core holds the numbers).
// Native, better than Electron: the surface itself is only a subscription; no window exists until the first result, so a
// session that never pins anything never pays for one. A fault while drawing loses only the pop-up: results are still in
// the strip's log line ("bar tasks: keep on top: ..."), and the fault is logged here.
// UI Automation / tests: the window is "Mantle keep on top"; the pane "ontop.toast" (Name = the text, HelpText = the line).
[Surface("ontop", AtStart = true)]
internal sealed class OnTopToastSurface : ISurface
{
    const int SwHide = 0;
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    ISurfaceContext _ctx = null!;
    IDisposable? _sub, _themeSub;
    Window? _win;
    IntPtr _hwnd;
    Grid? _root;
    Border? _pane;
    TextBlock? _text, _line, _warn, _hint, _glyph;
    DispatcherQueueTimer? _hold, _fade, _backstop, _idle;
    bool _dark, _shown, _activated;

    public bool Shown => _shown;

    public void Create(ISurfaceContext ctx)
    {
        _ctx = ctx;
        _sub = ctx.On<JsonElement>(OnTopFeature.ToastEvent, OnToast);
        _themeSub = ctx.On<ThemePayload>("theme", t => { _dark = t.View.Theme == "dark"; if (_root != null) _root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light; });
        ctx.Log.Line("listening for keep-on-top results; no window until the first one");
    }

    DispatcherQueueTimer Timer(int ms, Action tick)
    {
        var t = _ctx.Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms); t.IsRepeating = false;
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    void Make()
    {
        _root = (Grid)XamlReader.Load(
            "<Grid " + Ns + " Padding='18'>" +
            "<Border x:Name='Pane' HorizontalAlignment='Center' VerticalAlignment='Center' MinHeight='44' MaxWidth='360' Padding='12,6,16,6'" +
            " CornerRadius='{ThemeResource LgRPillCorner}' Background='{ThemeResource LgFillStrong}' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='1'>" +
            "<Grid ColumnSpacing='10'><Grid.ColumnDefinitions><ColumnDefinition Width='26'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>" +
            "<TextBlock x:Name='Glyph' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='15' HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{ThemeResource LgAcc}'/>" +
            "<StackPanel Grid.Column='1' VerticalAlignment='Center'>" +
            "<TextBlock x:Name='Text' FontSize='13' FontWeight='SemiBold' TextTrimming='CharacterEllipsis' Foreground='{ThemeResource LgInk}' FontFamily='{ThemeResource LgFont}'/>" +
            "<TextBlock x:Name='Line' FontSize='11.5' LineHeight='15' TextWrapping='WrapWholeWords' MaxLines='2' Foreground='{ThemeResource LgInk2}' FontFamily='{ThemeResource LgFont}'/>" +
            "<TextBlock x:Name='Warn' FontSize='11.5' LineHeight='15' TextWrapping='WrapWholeWords' MaxLines='2' Foreground='{ThemeResource LgWarn}' FontFamily='{ThemeResource LgFont}'/>" +
            "<TextBlock x:Name='Hint' FontSize='11.5' LineHeight='15' TextTrimming='CharacterEllipsis' Foreground='{ThemeResource LgInk3}' FontFamily='{ThemeResource LgFont}'/>" +
            "</StackPanel></Grid></Border></Grid>");
        _pane = (Border)_root.FindName("Pane");
        _text = (TextBlock)_root.FindName("Text");
        _line = (TextBlock)_root.FindName("Line");
        _warn = (TextBlock)_root.FindName("Warn");
        _hint = (TextBlock)_root.FindName("Hint");
        _glyph = (TextBlock)_root.FindName("Glyph");
        AutomationProperties.SetAutomationId(_pane, "ontop.toast");
        _root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light;
        _win = new Window { Title = "Mantle keep on top", Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        _win.SystemBackdrop = new SeeThroughBackdrop(_ctx.Log.Line);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        WindowKinds.Frameless(_hwnd);
        WindowKinds.StripFrame(_hwnd);
        // never focusable, never in the way of a click (ontop.js: focusable false, setIgnoreMouseEvents)
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Win32.SetLayeredWindowAttributes(_hwnd, 0, (byte)(Host.Quiet ? 0 : 255), Win32.LWA_ALPHA);   // WIN-028: a quiet test window draws nothing
        TransparentBackdrop.MakeSeeThrough(_hwnd);
        _ctx.Log.Line("made the pop-up window");
    }

    void OnToast(JsonElement p)
    {
        var s = OnTopToast.Read(p);
        if (s == null) return;
        try {
            if (_win == null) Make();
            _idle?.Stop();
            _text!.Text = s.Text;
            // the second line: the hint in the quiet ink, a refusal's reason in the warning colour, else the reason
            var show = s.Line.Length == 0 ? null : s.LineIsHint ? _hint : s.Tone == "warn" ? _warn : _line;
            foreach (var t in new[] { _line!, _warn!, _hint! }) { t.Text = t == show ? s.Line : ""; t.Visibility = t == show ? Visibility.Visible : Visibility.Collapsed; }
            _glyph!.Text = s.Did == "unpinned" ? "" : s.Tone == "warn" ? "" : "";
            AutomationProperties.SetName(_pane!, s.Text);
            AutomationProperties.SetHelpText(_pane!, s.Line);
            _pane!.Opacity = 1;
            Place();
            _hold?.Stop(); _fade?.Stop(); _backstop?.Stop();
            _hold = Timer(s.HoldMs, () => { _pane!.Opacity = 0; _fade = Timer(OnTopToast.FadeMs, () => HideNow("faded")); _fade.Start(); });
            _backstop = Timer(OnTopToast.BackstopMs, () => HideNow("the backstop"));
            _hold.Start(); _backstop.Start();
            _ctx.Log.Line($"shown ({s.Did}, {s.Tone}; holds {s.HoldMs} ms)");   // never the text: it names the app
        } catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException or XamlParseException) {
            _ctx.Log.Line($"the pop-up could not show ({e.GetType().Name}: {e.Message}); the result is still in the log");
            Drop("it failed to draw");
        }
    }

    void Place()
    {
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)Math.Round(OnTopToast.WindowW * scale), h = (int)Math.Round(OnTopToast.WindowH * scale), x = 0, y = 0;
        var area = DisplayArea.Primary;
        if (area != null && area.WorkArea.Width > 0) {
            var wa = area.WorkArea;
            var spot = OsdModel.Place(OsdSurface.BarPill(), new OsdRect(wa.X, wa.Y, wa.Width, wa.Height), w, h, 0);
            x = spot.X; y = spot.Y;
        }
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        IntPtr over = Win32.GetForegroundWindow();
        if (!_activated) {
            // WinUI starts drawing at the first Activate; a no-focus window gives the foreground straight back (OsdSurface)
            _activated = true;
            _win!.Activate();
            if (over != IntPtr.Zero && Win32.GetForegroundWindow() == _hwnd) Win32.SetForegroundWindow(over);
        } else Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.StripFrame(_hwnd);
        _shown = true;
        // on top EVERY time: the window just pinned is at the top of the topmost band (ontop.js raiseWin.up)
        WindowKinds.Raise(_hwnd, _ctx.Dispatcher, "ontop", over, (ok, why) => { if (_ctx.Test) _ctx.Log.Line(ok ? "raised: on top of everything" : "raised: NOT on top: " + why); });
    }

    void HideNow(string why)
    {
        _hold?.Stop(); _fade?.Stop(); _backstop?.Stop();
        if (_shown && _hwnd != IntPtr.Zero) { Win32.ShowWindow(_hwnd, SwHide); _shown = false; _ctx.Log.Line("hidden (" + why + ")"); }
        _idle ??= Timer(OnTopToast.IdleMs, () => Drop("30 s unused"));
        _idle.Stop(); _idle.Start();
    }

    /// Destroy the window (30 s unused, or it failed): the next result makes a new one.
    void Drop(string why)
    {
        _hold?.Stop(); _fade?.Stop(); _backstop?.Stop(); _idle?.Stop();
        _shown = false;
        if (_win == null) return;
        try { _win.Close(); } catch (Exception e) when (e is InvalidOperationException or COMException) { }
        _win = null; _activated = false; _root = null; _pane = null; _text = _line = _warn = _hint = _glyph = null; _hwnd = IntPtr.Zero;
        _ctx.Log.Line("the pop-up window was destroyed (" + why + ")");
    }

    public void Show(string why) => _ctx.Log.Line("shows only a keep-on-top result (" + why + ")");
    public void Hide(string why) { if (_shown) HideNow(why); }
    public void Destroy(string why)
    {
        _sub?.Dispose(); _themeSub?.Dispose();
        Drop(why);
    }
}
