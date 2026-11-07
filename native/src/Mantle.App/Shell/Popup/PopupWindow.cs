using System;
using Mantle.App.Native;
using Mantle.App.Shell.Windowing;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Mantle.App.Shell.Popups;

/// One pop-up window (WIN-006): frameless, see-through, clipped to its shape so clicks outside it reach what is
/// below (popup spike 5), always on top, no frame line. A no-focus pop-up never takes the keyboard (WS_EX_NOACTIVATE);
/// a focusable one asks for it after the show (WIN-015). An instant kind is parked (cloaked, ~1 MB, 0 CPU) between
/// opens instead of being closed (popup spike 7: one parked window per instant pop-up, everything else on demand).
internal sealed class PopupWindow
{
    public string Key { get; }
    public string Name { get; }
    public bool Focusable { get; }
    public bool Instant { get; }
    public Window Win { get; }
    public IntPtr Hwnd { get; }
    public Grid Root { get; }
    public Win32.RECT Rect { get; private set; }
    public bool Parked { get; private set; }
    public bool Painted { get; set; }

    public PopupWindow(string key, string name, bool focusable, bool instant, bool dark, UIElement body)
    {
        Key = key; Name = name; Focusable = focusable; Instant = instant;
        Root = new Grid { RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light };
        // the pop-up surface: --lg-fill-pop with the outer hairline, radius --lg-r-pop (the thicker fill: no picture yet, BAR-374)
        var face = (Border)XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource LgFillPop}' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='1' CornerRadius='{ThemeResource LgRPopCorner}'/>");
        face.Child = body;
        Root.Children.Add(face);
        Win = new Window { Title = "Mantle pop-up: " + name, Content = Root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        Win.AppWindow.SetPresenter(p);
        Win.AppWindow.IsShownInSwitchers = false;
        Win.SystemBackdrop = new TransparentBackdrop();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        WindowKinds.Frameless(Hwnd);
        WindowKinds.StripFrame(Hwnd);
        long ex = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW;
        if (!focusable || Host.Quiet) ex |= Win32.WS_EX_NOACTIVATE;
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;   // WIN-028: a quiet test window draws nothing
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) Win32.SetLayeredWindowAttributes(Hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(Hwnd);
    }

    /// Physical px. The window region is the rounded shape + 1 px (re-applied on every size).
    public void Place(Win32.RECT r, double scale)
    {
        Rect = r;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        Root.Width = w / scale; Root.Height = h / scale;
        Win32.SetWindowPos(Hwnd, IntPtr.Zero, r.Left, r.Top, w, h, Win32.SWP_NOACTIVATE | 0x0004 /* SWP_NOZORDER */);
        WindowKinds.ClipToShape(Hwnd, 0, 0, w, h, (int)Math.Round(16 * scale));
    }

    public void Show()
    {
        if (Parked) { WindowKinds.Cloak(Hwnd, false); Parked = false; }
        else Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.StripFrame(Hwnd);   // the presenter puts frame bits back at a show
    }

    /// An instant kind stays made, cloaked in place; the next open only uncloaks it.
    public void Park()
    {
        if (!Parked) Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.Cloak(Hwnd, true);
        Parked = true;
    }

    public void Close() { try { Win.Close(); } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { } }
}
