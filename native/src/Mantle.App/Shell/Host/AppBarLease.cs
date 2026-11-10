using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;

namespace Mantle.App.Shell.Hosting;

/// <summary>
/// The bar's dock app bar as the host sees it (main.js appbarHwnd): the bar asks through this (IBarShell), and the
/// host knows whether a strip of screen is reserved now, so "put Windows back" gives it back FIRST, while the sidecar
/// can still be asked (main.js restoreWindows step 1), instead of the bar's window close doing it after the sidecar has
/// stopped. Once given back it is not reserved again in this run (main.js: a display change re-asks only while
/// appbarHwnd is set), and the bar's own remove on close is then answered here without a call.
/// Thread-safe: the bar calls from its UI thread, the restore runs on the host thread.
/// </summary>
internal sealed class AppBarLease : IBarShell
{
    readonly IBarShell _inner;
    readonly IPlatform _platform;
    readonly object _lock = new();
    IntPtr _reserved;
    string? _gaveBack;

    public AppBarLease(IBarShell inner, IPlatform platform) { _inner = inner; _platform = platform; }

    /// The window whose app bar is registered now, or zero.
    public IntPtr Reserved { get { lock (_lock) return _reserved; } }

    public async Task<(Win32.RECT? Slot, string How)> ReserveAsync(IntPtr hwnd, string edge, int sizePx)
    {
        string? back; lock (_lock) back = _gaveBack;
        if (back != null) return (null, $"the app bar was given back to Windows ({back}), so it is not reserved again in this run");
        var r = await _inner.ReserveAsync(hwnd, edge, sizePx).ConfigureAwait(false);
        if (r.Slot != null) lock (_lock) { if (_gaveBack == null) _reserved = hwnd; }
        return r;
    }

    public async Task<string> RemoveAsync(IntPtr hwnd)
    {
        string? back;
        lock (_lock) { back = _gaveBack; if (_reserved == IntPtr.Zero) return back != null ? $"app bar already given back ({back})" : "no app bar was registered"; }
        var said = await _inner.RemoveAsync(hwnd).ConfigureAwait(false);
        if (said == "app bar removed") lock (_lock) _reserved = IntPtr.Zero;
        return said;
    }

    /// The guard's "more releases" (ShellGuardOptions.MoreReleases): the app bar when one is reserved. Host thread.
    public IReadOnlyList<(string Label, Func<Task<PlatformReply>> Run)> Releases(string why)
    {
        lock (_lock) { _gaveBack ??= why; if (_reserved == IntPtr.Zero) return Array.Empty<(string, Func<Task<PlatformReply>>)>(); }
        return new (string, Func<Task<PlatformReply>>)[] { ("the app bar", () => GiveBackAsync()) };
    }

    /// Remove the app bar now through the platform (host thread, the sidecar still running). Done or not, in words.
    public async Task<PlatformReply> GiveBackAsync()
    {
        IntPtr h; lock (_lock) h = _reserved;
        if (h == IntPtr.Zero) return PlatformReply.Success(default);
        var r = await _platform.CallAsync("appbar", new JsonObject { ["hwnd"] = h.ToInt64().ToString(CultureInfo.InvariantCulture), ["remove"] = true }, TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (r.Ok) lock (_lock) { if (_reserved == h) _reserved = IntPtr.Zero; }
        return r;
    }
}
