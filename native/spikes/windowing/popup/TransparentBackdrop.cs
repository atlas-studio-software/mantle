// TransparentBackdrop.cs - a fully see-through window background (the brush half of the bar spike's
// native-spike/MantleBar/Native/TransparentBackdrop.cs). WinUI 3 has no AllowsTransparency; a
// SystemBackdrop whose brush is a fully transparent system-compositor colour brush gives a per-pixel
// transparent window. The other half (DWM frame over the client area, black erase) lives in
// Native.cs: Native.Hook / Native.ExtendFrame, so one subclass proc handles every message we touch.
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PopupSpike;

internal sealed class TransparentBackdrop : SystemBackdrop
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
