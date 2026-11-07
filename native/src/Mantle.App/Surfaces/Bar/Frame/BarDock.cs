using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;

namespace Mantle.App.Surfaces.Bar.Frame;

/// What the bar frame asks of Windows that only the platform may do: the dock app bar (BAR-106, BAR-108). The
/// bar never calls SHAppBarMessage itself; a --test or scratch run has a passive platform, which refuses, and
/// then nothing is reserved and the bar says so.
internal interface IBarShell
{
    /// Reserve `sizePx` physical px along `edge` for `hwnd`: the slot Explorer gave (physical px), or null and why.
    Task<(Win32.RECT? Slot, string How)> ReserveAsync(IntPtr hwnd, string edge, int sizePx);
    Task<string> RemoveAsync(IntPtr hwnd);
}

/// The sidecar's `appbar` verb (main.js applyLive dock branch). The size goes out in the sidecar's units, which
/// for today's per-monitor-aware sidecar are physical px (BAR-107), and the rect comes back in the same units.
/// IPlatform is host-thread only, so every call is posted to the host loop.
internal sealed class PlatformBarShell : IBarShell
{
    readonly IPlatform _platform;
    readonly HostLoop _host;
    public PlatformBarShell(IPlatform platform, HostLoop host) { _platform = platform; _host = host; }

    Task<PlatformReply> Call(JsonObject args) => _host.RunAsync(() => _platform.CallAsync("appbar", args, TimeSpan.FromSeconds(4)).AsTask());

    public async Task<(Win32.RECT? Slot, string How)> ReserveAsync(IntPtr hwnd, string edge, int sizePx)
    {
        var r = await Call(new JsonObject { ["hwnd"] = hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), ["edge"] = edge, ["size"] = sizePx }).ConfigureAwait(false);
        if (!r.Ok) return (null, "the app bar was not reserved: " + (r.Error ?? r.Outcome.ToString()));
        try {
            var a = r.Result.GetProperty("rect");
            var rc = new Win32.RECT { Left = a[0].GetInt32(), Top = a[1].GetInt32(), Right = a[2].GetInt32(), Bottom = a[3].GetInt32() };
            return (rc, $"app bar {sizePx} px reserved through Mantle's helper: slot {FrameNative.Rect(rc)}");
        } catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or FormatException) {
            return (null, "Mantle's helper reserved the app bar but its answer had no usable rect (" + e.Message + ")");
        }
    }

    public async Task<string> RemoveAsync(IntPtr hwnd)
    {
        var r = await Call(new JsonObject { ["hwnd"] = hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture), ["remove"] = true }).ConfigureAwait(false);
        return r.Ok ? "app bar removed" : "the app bar could not be removed: " + (r.Error ?? r.Outcome.ToString()) + " (Explorer drops it by itself when the window goes)";
    }
}
