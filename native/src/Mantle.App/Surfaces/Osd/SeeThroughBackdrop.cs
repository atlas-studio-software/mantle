// SeeThroughBackdrop.cs - the OSD's and the toasts' window background: Native/TransparentBackdrop's fully clear
// system-compositor brush (the window is bigger than what it draws: room for the light shadow and the lift), with
// one difference: a change of the default backdrop configuration (theme, activation, high contrast) is answered here
// and never passed on to SystemBackdrop's own handler. A clear brush follows no configuration, and after a full
// garbage collection WinUI calls that handler with no target on the next theme change; the base handler then throws
// "The parameter is incorrect: target", an unhandled error that stops Mantle. Measured on the CI runner (run
// 42777700651, the theme.set and look.json UI tests, 2 runs each): one forced full collection after start and
// then a theme change crashed 4 of 4 app logs, with no OSD shadow and no toasts code involved; without the forced
// collection, 0 of 4. TransparentBackdrop (the bar's, the pop-ups', the launcher's) still passes it on: the same
// override there is the native-windows lane's (docs/lanes/osd-toasts.md, part 4).
using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Osd;

internal sealed partial class SeeThroughBackdrop : SystemBackdrop
{
    static Windows.UI.Composition.Compositor? s_compositor;
    static IntPtr s_dqController;   // kept for the life of the process
    readonly Action<string> _log;   // the surface's own log (its lines carry the surface's name)
    int _said;

    public SeeThroughBackdrop(Action<string> log) { _log = log; }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        EnsureSystemDispatcherQueue();
        s_compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = s_compositor.CreateColorBrush(default);   // alpha 0: no colour at all
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        // nothing to follow (the brush is clear in every theme); said a few times so a log shows the road is taken
        if (_said++ < 3) _log($"backdrop configuration changed (target {(target == null ? "none" : "set")}, root {(xamlRoot == null ? "none" : "set")}): nothing to follow");
    }

    // Windows.UI.Composition needs a Windows.System dispatcher queue on this thread; WinUI 3 only makes its own.
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
