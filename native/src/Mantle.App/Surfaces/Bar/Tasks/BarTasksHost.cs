using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.Core;
using Mantle.Core.Features.Pinned;
using Mantle.Core.Features.WindowList;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Surfaces.Bar.Tasks;

/// <summary>
/// The bar strip's bridge between its UI thread and the host thread (native-bar-tasks). The host side is
/// <see cref="TaskStripService"/> on the platform (the sidecar's <c>windows</c> push, <c>lnk</c>, <c>pins</c>,
/// <c>icon</c>, <c>app</c>); every new strip is re-posted here and raised as <see cref="Changed"/> on the UI thread.
/// Attached once the host is up (<see cref="AttachOnHost"/>, on the host thread).
/// </summary>
// partial: IDisposable is WinRT's IClosable to CsWinRT, so an AOT build wants the type partial (CsWinRT1028)
internal sealed partial class BarTasksHost : IDisposable
{
    readonly DispatcherQueue _ui;
    readonly Action<string> _log;
    TaskStripService? _svc;
    IHostScheduler? _host;

    public BarTasksHost(DispatcherQueue ui, Action<string> log) { _ui = ui; _log = log; }

    /// The latest strip on the UI thread.
    public TaskList Last { get; private set; } = TaskList.Empty;
    public string? WindowsProblem { get; private set; }
    public bool Attached => _svc != null;
    public event Action? Changed;

    /// Test and scratch runs never read the user's own pins: a scratch TaskBar folder comes from MANTLE_TEST_PINS_DIR
    /// and a scratch Favorites value from MANTLE_TEST_TASKBAND_KEY (a key under HKCU), as MANTLE_TEST_THEME_KEY does.
    internal static (string? Dir, string? Note, Func<byte[]>? Favorites) PinsSource(LaunchArgs args, Func<string, string?> env)
    {
        if (args.Kind == RunKind.Live) {
            string appData = env("APPDATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
            return (PinnedReader.PinsDir(appData), null, () => ReadFavorites(PinnedReader.TaskbandKey));
        }
        string? dir = env("MANTLE_TEST_PINS_DIR");
        string? key = env("MANTLE_TEST_TASKBAND_KEY");
        Func<byte[]>? fav = string.IsNullOrEmpty(key) ? null : () => ReadFavorites(key!);
        if (string.IsNullOrEmpty(dir)) return (null, "a test run shows no pins: it never reads the user's own (MANTLE_TEST_PINS_DIR names a scratch pin folder)", null);
        return (dir, null, fav);
    }

    static byte[] ReadFavorites(string key)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key)
            ?? throw new InvalidOperationException(@"HKCU\" + key + " could not be opened (the value is not there)");
        return k.GetValue("Favorites") as byte[] ?? throw new InvalidOperationException(@"HKCU\" + key + " has no REG_BINARY Favorites value");
    }

    /// On the host thread, once: start the strip's host side on the platform.
    public void AttachOnHost(IPlatform platform, IHostScheduler host)
    {
        if (_svc != null) return;
        _host = host;
        var (dir, note, fav) = PinsSource(Host.Args, Host.Env);
        _svc = new TaskStripService(new TaskStripOptions {
            Platform = platform, Host = host, PinsDir = dir, PinsNote = note, ReadFavorites = fav, UserData = Host.Paths.DataRoot,
            Log = _log, Actions = new BarTaskActions(platform, _ui, Host.Args.Kind == RunKind.Live, _log),
            Publish = list => _ui.TryEnqueue(() => { Last = list; Changed?.Invoke(); }),
            Marks = list => _ui.TryEnqueue(() => { Last = list; Changed?.Invoke(); }),
            WindowsProblem = p => _ui.TryEnqueue(() => { if (WindowsProblem == p) return; WindowsProblem = p; Changed?.Invoke(); }),
        });
        _log($"bar tasks: the strip reads the running apps from the platform's windows push; pins: {(dir != null ? (Host.Args.Kind == RunKind.Live ? "Windows' taskbar pins" : "the scratch folder MANTLE_TEST_PINS_DIR") : note)}");
        _svc.Start();
    }

    /// A click (bar.open): the answer comes back on the UI thread.
    public void Open(string id, bool fresh, Action<TaskOpenResult> done)
    {
        if (_svc == null || _host == null) { done(new TaskOpenResult(false, "the bar is not connected to Mantle's host yet")); return; }
        var svc = _svc;
        _host.Post(async () => {
            TaskOpenResult r;
            try { r = await svc.OpenAsync(id, fresh).ConfigureAwait(true); }
            catch (Exception e) { r = new TaskOpenResult(false, "the click failed: " + e.Message); }
            _ui.TryEnqueue(() => done(r));
        });
    }

    /// One window (bar.window {hwnd, action}: focus, minimize, close, toggle): the answer comes back on the UI thread.
    public void Window(string hwnd, string action, Action<TaskOpenResult> done)
    {
        if (_svc == null || _host == null) { done(new TaskOpenResult(false, "the bar is not connected to Mantle's host yet")); return; }
        var svc = _svc;
        _host.Post(async () => {
            TaskOpenResult r;
            try { r = await svc.WindowAsync(hwnd, action).ConfigureAwait(true); }
            catch (Exception e) { r = new TaskOpenResult(false, "the window action failed: " + e.Message); }
            _ui.TryEnqueue(() => done(r));
        });
    }

    public void Dispose()
    {
        var s = _svc; _svc = null;
        if (s != null) _host?.Post(s.Dispose);
    }
}

/// <summary>
/// The strip's window actions. Live: the sidecar's focus / minimize / close / launch, with this process's foreground
/// right handed to the sidecar first (lib/fg-grant.js). Test and scratch runs: the sidecar is passive and refuses
/// them, so a focus or minimise acts in-process on the UI thread (the visible test windows of the UI tests and the
/// bench), and a launch is a dry run that says what it would have done (BAR-201). Called on the host thread.
/// </summary>
internal sealed class BarTaskActions : ITaskActions
{
    readonly IPlatform _p;
    readonly DispatcherQueue _ui;
    readonly bool _live;
    readonly Action<string> _log;

    public BarTaskActions(IPlatform p, DispatcherQueue ui, bool live, Action<string> log) { _p = p; _ui = ui; _live = live; _log = log; }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    void Grant() { if (_p.Status.Info is { Pid: > 0 } i) AllowSetForegroundWindow(i.Pid); }

    Task<string?> OnUi(Func<string?> work)
    {
        var tcs = new TaskCompletionSource<string?>();
        if (!_ui.TryEnqueue(() => { try { tcs.SetResult(work()); } catch (Exception e) { tcs.SetResult(e.Message); } }))
            tcs.SetResult("the bar's window thread is gone");
        return tcs.Task;
    }

    async ValueTask<string?> Sidecar(string verb, TaskWindow w)
    {
        if (verb == "focus") Grant();
        var r = await _p.CallAsync(verb, new JsonObject { ["hwnd"] = w.Hwnd }, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
        return r.Ok ? null : r.Error;
    }

    static IntPtr H(TaskWindow w) => long.TryParse(w.Hwnd, out long v) ? new IntPtr(v) : IntPtr.Zero;

    public async ValueTask<string?> FocusAsync(TaskWindow w)
    {
        if (_live) return await Sidecar("focus", w).ConfigureAwait(true);
        return await OnUi(() => { var r = Focus.Raise(H(w)); return r.StartsWith("focused", StringComparison.Ordinal) ? null : r; }).ConfigureAwait(true);
    }

    public async ValueTask<string?> MinimizeAsync(TaskWindow w)
    {
        if (_live) return await Sidecar("minimize", w).ConfigureAwait(true);
        return await OnUi(() => Win32.ShowWindow(H(w), Win32.SW_MINIMIZE) || Win32.IsIconic(H(w)) ? null : "Windows did not minimise it").ConfigureAwait(true);
    }

    public async ValueTask<string?> CloseAsync(TaskWindow w)
    {
        if (_live) return await Sidecar("close", w).ConfigureAwait(true);
        return "a test run never closes a window from the bar";
    }

    public async ValueTask<TaskOpenResult> LaunchAsync(TaskItem item, bool fresh)
    {
        if (!_live) return TaskOpen.DryRun(item, fresh, _log);
        string? lnk = item.Pin?.LnkPath ?? item.Pin?.Target ?? item.Pin?.Exe;
        string? aumid = item.Pin?.Aumid ?? item.Aumid;
        string again = fresh ? " another copy of" : "";
        if (lnk != null) {
            try {
                Grant();
                using var p = Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                return new TaskOpenResult(true, null, "launched");
            } catch (Exception e) { return new TaskOpenResult(false, $"Windows would not start{again} {item.Name} from {lnk}: {e.Message}"); }
        }
        if (aumid != null) {
            Grant();
            var r = await _p.CallAsync("launch", new JsonObject { ["aumid"] = aumid }, TimeSpan.FromMilliseconds(8000)).ConfigureAwait(true);
            if (!r.Ok) return new TaskOpenResult(false, $"Windows would not start{again} {item.Name} ({aumid}): {r.Error}");
            if (r.Result.ValueKind == System.Text.Json.JsonValueKind.Object && r.Result.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.False)
                return new TaskOpenResult(false, $"Windows would not start{again} {item.Name}: {(r.Result.TryGetProperty("reason", out var why) ? why.ToString() : "the sidecar did not say why")}");
            return new TaskOpenResult(true, null, "launched");
        }
        return TaskOpen.NothingToStart(item);
    }
}
