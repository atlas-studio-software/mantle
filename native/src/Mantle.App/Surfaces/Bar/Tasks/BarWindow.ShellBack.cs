using System;
using System.Runtime.InteropServices;
using Mantle.Core.Args;
using Mantle.Core.Features.WindowList;

namespace Mantle.App;

/// <summary>
/// Explorer came back (BAR-158, 161, 163; lane native-r7-strip-3). Explorer broadcasts TaskbarCreated to every
/// top-level window when it starts; the bar window hears it in its subclass (BarWindow.Frame.cs SubProc, one line) and
/// does the bar's part here: one per 3 s, the dock slot asked for again, the Windows-taskbar watch re-made on the new
/// taskbar's thread, the bar raised. The rules and words are Mantle.Core's BarShellBack. No poll and no timer: nothing
/// runs until the message comes.
/// </summary>
/// <remarks>
/// A test (on a CI runner) stands in for Explorer by posting TaskbarCreated to the bar's own window only, found in
/// Mantle's own process (the pattern of Surfaces/Desktop/ShellWatch.cs): a broadcast would reach every app on the
/// machine, and Explorer is never restarted by a test.
/// </remarks>
public sealed partial class BarWindow
{
    static readonly uint TaskbarCreatedMsg = ShellBackNative.RegisterWindowMessageW("TaskbarCreated");
    readonly BarShellBack _shellBack = new();

    /// The bar subclass's one-line hook: true when this message is Explorer's TaskbarCreated (handled here, posted).
    bool HeardShellBack(uint msg)
    {
        if (msg != TaskbarCreatedMsg || TaskbarCreatedMsg == 0) return false;
        DispatcherQueue.TryEnqueue(() => ShellBack("TaskbarCreated"));
        return true;
    }

    void ShellBack(string how)
    {
        if (!_shellBack.Take(Environment.TickCount64)) return;
        bool up = IsUp, watching = _hookShow != IntPtr.Zero;
        Host.Log.Line(BarShellBack.Line(how, _shellBack.Count, FrameMode, up, watching, Host.Args.Kind != RunKind.Live));
        foreach (var step in BarShellBack.Steps(FrameMode, up, watching)) {
            try {
                switch (step) {
                    case "dock": _ = DockBar("Explorer is back"); break;
                    case "rewatch": DisarmWatch(); ArmWatch(); break;
                    default: RaiseBar("Explorer is back"); break;
                }
            } catch (Exception e) when (e is COMException or InvalidOperationException) {
                Host.Log.Line($"bar: Explorer is back: {step} failed ({e.Message})");
            }
        }
    }
}

internal static class ShellBackNative
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessageW(string name);
}
