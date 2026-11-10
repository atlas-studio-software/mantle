// TransparentBackdrop.cs - a fully see-through window background, so the window can be bigger than
// the pill (room for the light theme's soft shadow above it and the 14 DIP lift below) and show
// nothing but the pill. WinUI 3 has no AllowsTransparency; a SystemBackdrop whose brush is a fully
// transparent system-compositor colour brush gives a per-pixel transparent window (the approach the
// WinUIEx library's TransparentTintBackdrop uses).
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Native;

internal sealed partial class TransparentBackdrop : SystemBackdrop
{
    static Windows.UI.Composition.Compositor? s_compositor;
    static IntPtr s_dqController;   // kept for the life of the process

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        EnsureSystemDispatcherQueue();
        s_compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = s_compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }

    // A clear brush follows no backdrop configuration. WinUI's base handler throws when it is called with
    // no target, which happened on a theme change after a full GC and took the whole app down (found by the
    // osd-toasts lane, runs 42775032765..42779579180). Handle it here and never pass it on.
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot) { }

    // The brush alone left the window painted white (light) / black (dark) on the runner (spike run 2):
    // the HWND's redirection surface is still erased with the theme colour under the XAML content.
    // Extending the DWM frame over the whole client area and erasing with black (alpha 0 in the
    // frame) makes those pixels see-through, so only what XAML draws shows.
    [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int l, r, t, b; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Win32.RECT r);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr hdc, ref Win32.RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
    static SubclassProc? s_subclass;   // kept alive: Windows calls it

    public static string MakeSeeThrough(IntPtr hwnd)
    {
        var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
        int hr = DwmExtendFrameIntoClientArea(hwnd, ref m);
        s_subclass ??= (h, msg, wp, lp, id, data) => {
            // No non-client area at all: the client is the whole window. The presenter keeps putting
            // WS_DLGFRAME back (CI runs 1-2), which left a 3 px frame: the pill sat 3 px right and low
            // in UIA and the window edge drew a line. Answering WM_NCCALCSIZE with 0 removes it.
            if (msg == 0x0083 /* WM_NCCALCSIZE */ && wp != IntPtr.Zero) return IntPtr.Zero;
            if (msg == 0x0014 /* WM_ERASEBKGND */) {
                if (GetClientRect(h, out var rc)) FillRect(wp, ref rc, GetStockObject(4 /* BLACK_BRUSH */));
                return new IntPtr(1);
            }
            return DefSubclassProc(h, msg, wp, lp);
        };
        bool sub = SetWindowSubclass(hwnd, s_subclass, new UIntPtr(1), IntPtr.Zero);
        return $"frame extended (hr 0x{hr:X8}), erase subclass {(sub ? "on" : "FAILED")}";
    }

    // Windows.UI.Composition needs a Windows.System dispatcher queue on this thread; WinUI 3 only
    // creates its own (Microsoft.UI.Dispatching) one.
    [StructLayout(LayoutKind.Sequential)]
    struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }
    [DllImport("CoreMessaging.dll")]
    static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

    static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || s_dqController != IntPtr.Zero) return;
        var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
        CreateDispatcherQueueController(o, out s_dqController);
    }
}
