using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.Core.Features.Desktop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Desktop;

/// <summary>
/// The desktop right-click menu window (ui/desktop/deskmenu.js + deskmenu.css): no frame, no taskbar button, never
/// takes the keyboard, over every app, the foreground window unchanged on open and close. An island panel 224-320 DIP
/// wide, 5 DIP padding, 32 DIP rows with a 16 DIP Fluent glyph and 13 px text, inside a 20 DIP clear margin. Made on
/// each open and destroyed on close (nothing kept hidden). A failed row keeps the menu up with its reason.
/// </summary>
internal sealed class DeskMenuWindow
{
    const double IconSize = 14;
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

    readonly Mantle.App.Shell.ISurfaceContext _ctx;
    readonly Func<string, Brush> _b;
    readonly Func<DeskMenuRow, Task<string?>> _pick;
    readonly Mantle.App.Shell.Popups.PopupWatch? _watch;
    readonly Window _win;
    readonly IntPtr _hwnd;
    readonly Border _panel;
    readonly StackPanel _list = new();
    readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(10, 6, 10, 4), Visibility = Visibility.Collapsed };
    readonly List<(DeskMenuRow Row, Button B)> _items = new();
    readonly List<Border> _seps = new();
    bool _dark, _activated;
    int _sel = -1;
    PxRect _at;

    public DeskMenuWindow(Mantle.App.Shell.ISurfaceContext ctx, Func<string, Brush> brush, bool dark, bool widgetsOn,
        Func<DeskMenuRow, Task<string?>> pick, Action outside, Action esc)
    {
        _ctx = ctx; _b = brush; _dark = dark; _pick = pick;
        foreach (var r in DeskMenu.Rows(widgetsOn)) {
            if (r.Sep) { var s = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) }; _seps.Add(s); _list.Children.Add(s); continue; }
            var glyph = new TextBlock { Text = r.Glyph, FontFamily = Icons, FontSize = IconSize, Width = 16, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = r.Label, FontFamily = TextFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(glyph); row.Children.Add(label);
            var b = new Button {
                Content = row, Height = DeskMenu.RowHeight, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), IsTabStop = false,
            };
            AutomationProperties.SetAutomationId(b, "deskmenu." + r.Id);
            AutomationProperties.SetName(b, r.Label);
            var item = (r, b);
            b.Click += async (_, _) => await Run(item);
            int at = _items.Count;
            b.PointerEntered += (_, _) => Select(at);
            _items.Add(item);
            _list.Children.Add(b);
        }
        AutomationProperties.SetAutomationId(_note, "deskmenu.note");
        _list.Children.Add(_note);
        _panel = new Border { Child = _list, Padding = new Thickness(DeskMenu.Padding), BorderThickness = new Thickness(1), MinWidth = DeskMenu.MinWidth, MaxWidth = DeskMenu.MaxWidth,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(DeskMenu.Inset) };
        _panel.PointerExited += (_, _) => Select(-1);
        AutomationProperties.SetAutomationId(_panel, "deskmenu.panel");
        AutomationProperties.SetName(_panel, "Desktop");
        var root = new Grid { Background = new SolidColorBrush() };
        root.Children.Add(_panel);
        _panel.SizeChanged += (_, _) => Resize();
        _win = new Window { Title = "Mantle desktop menu", Content = root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
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
        // the outside watch (a click elsewhere, Esc) is armed live only: a --test run watches nothing real
        if (!ctx.Test) _watch = new Mantle.App.Shell.Popups.PopupWatch(ctx.Dispatcher, (_, pt) => { if (!Inside(pt.X, pt.Y)) outside(); }, esc, ctx.Log.Line);
        Paint(dark);
    }

    /// The menu's window rect for the point (panel corner on it), shown without taking the keyboard.
    public PxRect Open(int x, int y)
    {
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0; if (scale <= 0) scale = 1;
        var (w, h) = Size();
        var mon = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, 2 /* nearest */);
        var mi = new Win32.MONITORINFO { cbSize = Marshal.SizeOf<Win32.MONITORINFO>() };
        PxRect? area = Win32.GetMonitorInfoW(mon, ref mi) ? new PxRect(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top) : null;
        _at = DeskMenu.Place(x, y, w * scale, h * scale, (int)Math.Round(DeskMenu.Inset * scale), area);
        _win.AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(_at.X, _at.Y, _at.Width, _at.Height));
        if (!_activated) {
            // WinUI starts drawing a window's content at its first Activate (before it the window is a white sheet); this
            // no-focus window gives the foreground straight back to whatever had it (Shell/Popup/PopupWindow.Show)
            _activated = true;
            IntPtr prev = Win32.GetForegroundWindow();
            _win.Activate();
            if (prev != IntPtr.Zero && prev != _hwnd && Win32.GetForegroundWindow() == _hwnd) Win32.SetForegroundWindow(prev);
        }
        else Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        Mantle.App.Shell.Windowing.WindowKinds.StripFrame(_hwnd);   // the presenter puts frame bits back at a show
        Mantle.App.Shell.Windowing.WindowKinds.Raise(_hwnd, _ctx.Dispatcher, "desktop menu");
        _watch?.Arm();
        return _at;
    }

    /// The window's DIP size: the panel (224 wide, 5 rows of 32, two 9 DIP separators, 5 padding, 1 hairline) + the margin.
    (double W, double H) Size()
    {
        double w = _panel.ActualWidth > 0 ? _panel.ActualWidth : DeskMenu.MinWidth;
        double h = _panel.ActualHeight > 0 ? _panel.ActualHeight : 2 * (DeskMenu.Padding + 1) + _items.Count * DeskMenu.RowHeight + _seps.Count * 9;
        return (w + 2 * DeskMenu.Inset, h + 2 * DeskMenu.Inset);
    }

    void Resize()
    {
        if (_at.Width == 0) return;
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0; if (scale <= 0) scale = 1;
        var (w, h) = Size();
        int W = (int)Math.Ceiling(w * scale), H = (int)Math.Ceiling(h * scale);
        if (W == _at.Width && H == _at.Height) return;
        _at = new PxRect(_at.X, _at.Y, W, H);
        _win.AppWindow.Resize(new global::Windows.Graphics.SizeInt32(W, H));
    }

    bool Inside(int x, int y) => x >= _at.X && x < _at.Right && y >= _at.Y && y < _at.Bottom;

    async Task Run((DeskMenuRow Row, Button B) it)
    {
        if (it.B.Tag is "busy") return;
        it.B.Tag = "busy";
        it.B.Background = _b("LgPress");
        string? why;
        try { why = await _pick(it.Row); } catch (Exception e) { why = e.Message; }
        it.B.Tag = null;
        it.B.Background = new SolidColorBrush();
        if (why != null) {
            _note.Text = DeskMenu.Failed(it.Row.Label, why);
            _note.Visibility = Visibility.Visible;
        }
    }

    /// Up / Down / Home / End move the highlight; Enter runs it (nothing highlighted: closes, runs nothing).
    public bool Key(string key, Action close)
    {
        if (key == "enter") { if (_sel < 0) { close(); return true; } _ = Run(_items[_sel]); return true; }
        Select(DeskMenu.Step(_sel, _items.Count, key));
        return true;
    }

    void Select(int i)
    {
        _sel = i;
        for (int k = 0; k < _items.Count; k++) {
            var b = _items[k].B;
            if (b.Tag is "busy") continue;
            b.Background = k == i ? _b("LgHover") : new SolidColorBrush();
            AutomationProperties.SetItemStatus(b, k == i ? "selected" : "");
        }
    }

    public void Paint(bool dark)
    {
        _dark = dark;
        if (_win.Content is FrameworkElement fe) fe.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _panel.Background = _b("LgFillPop");
        _panel.BorderBrush = _b("LgEdgeOut");
        _panel.CornerRadius = new CornerRadius(dark ? 10 : 8);   // --lg-r per theme (Tokens.xaml LgR)
        foreach (var s in _seps) s.Background = _b("LgEdgeOut");
        _note.Foreground = _b("LgWarn");
        foreach (var (_, b) in _items) {
            b.Resources["ButtonBackgroundPointerOver"] = _b("LgHover");
            b.Resources["ButtonBackgroundPressed"] = _b("LgPress");
            b.Resources["ButtonForegroundPointerOver"] = _b("LgInk");
            b.Resources["ButtonForegroundPressed"] = _b("LgInk");
            b.Background = new SolidColorBrush();
            b.CornerRadius = new CornerRadius(6);
            if (b.Content is StackPanel sp) {
                if (sp.Children[0] is TextBlock g) g.Foreground = _b("LgInk2");
                if (sp.Children[1] is TextBlock t) t.Foreground = _b("LgInk");
            }
        }
    }

    public void Close()
    {
        _watch?.Disarm();
        try { _win.Close(); } catch { /* already gone */ }
    }
}
