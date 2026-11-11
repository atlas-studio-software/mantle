using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.App.Native;
using Mantle.Core;
using Mantle.Core.Commands;
using Mantle.Core.Features.JumpList;
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
    Mantle.Core.Data.Stores? _stores;
    JumpListService? _jl;
    ICommandBus? _bus;
    IDisposable? _ontopSub, _toastSub;

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
    public void AttachOnHost(IPlatform platform, IHostScheduler host, ICommandBus? bus = null, IEvents? events = null)
    {
        if (_svc != null) return;
        _host = host;
        var (dir, note, fav) = PinsSource(Host.Args, Host.Env);
        // Mantle's own pin list (features\jumplist.json) for a dragged order, through the ONE write gate (a test or scratch
        // profile writes its own; the user's own only in a --live run that is the only shell). Its own Stores: the
        // feature host's are made later in HostStart, and no native feature opens jumplist.json yet.
        Mantle.Core.Data.IStore? overlay = null;
        string? overlayWhy = null;
        try { _stores = Mantle.Core.Data.Stores.For(Host.Paths, Host.Gate, Host.Log); overlay = _stores.Open(@"features\jumplist.json"); }
        catch (Exception e) { overlayWhy = e.Message; }
        string? orderFail = Host.Args.Test && Host.Env("MANTLE_TEST_ORDER_FAIL") is { Length: > 0 } f ? "a test asked every pin order save to fail (MANTLE_TEST_ORDER_FAIL: " + f + ")" : null;
        if (overlayWhy != null) _log("bar tasks: Mantle's own pin list could not be opened, so a dragged pin order is not saved: " + overlayWhy);
        _svc = new TaskStripService(new TaskStripOptions {
            Platform = platform, Host = host, PinsDir = dir, PinsNote = note, ReadFavorites = fav, UserData = Host.Paths.DataRoot,
            Overlay = overlay, Test = Host.Args.Test, OrderFailForTest = orderFail,
            Log = _log, Actions = new BarTaskActions(platform, _ui, Host.Args.Kind == RunKind.Live, _log),
            Publish = list => _ui.TryEnqueue(() => { Last = list; Changed?.Invoke(); }),
            Marks = list => _ui.TryEnqueue(() => { Last = list; Changed?.Invoke(); }),
            WindowsProblem = p => _ui.TryEnqueue(() => { if (WindowsProblem == p) return; WindowsProblem = p; Changed?.Invoke(); }),
        });
        // the right-click menu (lib/features/jumplist.js): its pin rows write the same store as the dragged order
        string? jlFixture = Host.Args.Test ? Host.Env("MANTLE_TEST_JUMPLIST") : null;
        _jl = new JumpListService(new JumpListOptions { Platform = platform, Overlay = overlay, Test = Host.Args.Test, Fixture = jlFixture, Log = _log });
        _bus = bus;
        AttachOnTop(events);
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

    /// A drop (jumplist.order {group, ids, from, to}): the answer comes back on the UI thread.
    public void Order(string group, IReadOnlyList<string> ids, int from, int to, Action<OrderSaved> done)
    {
        if (_svc == null || _host == null) { done(new OrderSaved(false, "the new order was not saved: the bar is not connected to Mantle's host yet")); return; }
        var svc = _svc;
        var copy = ids.ToArray();
        _host.Post(async () => {
            OrderSaved r;
            try { r = await svc.OrderAsync(group, copy, from, to).ConfigureAwait(true); }
            catch (Exception e) { r = new OrderSaved(false, "the new order was not saved: " + e.Message); }
            _ui.TryEnqueue(() => done(r));
        });
    }

    // ---------------------------------------------------------------- the right-click menu (BarWindow.JumpList.cs)
    /// Run <paramref name="work"/> on the host thread; its answer (or <paramref name="fail"/> of what it threw) comes back on the UI thread.
    void OnHost<T>(Func<ValueTask<T>> work, Func<string, T> fail, Action<T> done)
    {
        if (_host == null || _jl == null) { done(fail("the bar is not connected to Mantle's host yet")); return; }
        _host.Post(async () => {
            T r;
            try { r = await work().ConfigureAwait(true); }
            catch (Exception e) { r = fail(e.Message); }
            _ui.TryEnqueue(() => done(r));
        });
    }

    static JumpAct Failed(string why) => new(false, why);

    public void JumpGet(JumpApp app, Action<JumpListReply> done) =>
        OnHost(() => _jl!.GetAsync(app), w => new JumpListReply(false, false, w, Array.Empty<JumpSection>(), null, null, null, 0), done);
    public void JumpOpen(JumpApp app, string item, string? title, Action<JumpAct> done) => OnHost(() => _jl!.OpenAsync(app, item, title), Failed, done);
    public void JumpRemove(JumpApp app, string item, string? title, Action<JumpAct> done) => OnHost(() => _jl!.RemoveAsync(app, item, title), Failed, done);
    public void JumpClose(IReadOnlyList<string> hwnds, Action<JumpAct> done) => OnHost(() => _jl!.CloseAsync(hwnds), Failed, done);

    /// Pin / Unpin from taskbar (Mantle's own list); the strip is rebuilt at once after it, done or not (refreshPinned).
    public void JumpPin(JumpApp app, bool pin, bool undo, Action<JumpAct> done) => OnHost(async () => {
        var r = await _jl!.PinAsync(app, pin, undo).ConfigureAwait(true);
        _svc?.Refresh(pin ? "an app was pinned from its menu" : "an app was unpinned from its menu");
        return r;
    }, Failed, done);

    // ---------------------------------------------------------------- keep on top (lib/features/ontop.js through the bus)
    /// Windows Mantle keeps on top (hwnd -> name), from the <c>ontop.state</c> push; UI thread.
    public IReadOnlyDictionary<string, string> OnTopPinned { get; private set; } = new Dictionary<string, string>();
    /// Why the bar cannot show which windows are kept on top (ui/bar/ontop.js CANT), or null: set only once the bar
    /// knows it will not hear the state (no event channel, the subscription failed, or the state never answered).
    public string? OnTopProblem { get; private set; }
    bool _onTopKnown;

    /// BAR-334 test hook (--test only): MANTLE_TEST_ONTOP_CHANNEL=0 attaches the strip as if it had no event channel.
    public const string OnTopChannelEnv = "MANTLE_TEST_ONTOP_CHANNEL";

    void OnTopCant(string why)
    {
        _log("bar tasks: keep on top: " + why + ", so every app's tooltip and the menu say the bar can't show which windows are kept on top");
        _ui.TryEnqueue(() => { OnTopProblem = Mantle.Core.Features.OnTop.OnTopWords.Cant; OnTopChanged?.Invoke(); });
    }
    public event Action? OnTopChanged;
    public event Action<string>? OnTopToast;

    void AttachOnTop(IEvents? events)
    {
        if (Host.Args.Test && Host.Env(OnTopChannelEnv) == "0") events = null;
        if (events == null) { OnTopCant("the bar has no event channel" + (Host.Args.Test && Host.Env(OnTopChannelEnv) == "0" ? " (" + OnTopChannelEnv + "=0)" : "")); return; }
        try {
            _ontopSub = events.Subscribe<System.Text.Json.JsonElement>(Mantle.Core.Features.OnTop.OnTopFeature.StateEvent, TakeOnTop);
            // the feature is installed after the strip attaches, so its replay never reached this subscription: ask once
            _ontopAsk = _host?.Delay(TimeSpan.FromMilliseconds(1500), () => _ = AskOnTop(20));
            _toastSub = events.Subscribe<System.Text.Json.JsonElement>(Mantle.Core.Features.OnTop.OnTopFeature.ToastEvent, t => {
                string text = t.TryGetProperty("text", out var x) ? x.ToString() : "";
                string why = t.TryGetProperty("reason", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.String ? " - " + r.GetString() : "";
                _ui.TryEnqueue(() => OnTopToast?.Invoke(text + why));
            });
        } catch (Exception e) { OnTopCant("could not subscribe to the pin state (" + e.Message + ")"); }
    }

    IDisposable? _ontopAsk;

    void TakeOnTop(System.Text.Json.JsonElement s)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (s.ValueKind == System.Text.Json.JsonValueKind.Object && s.TryGetProperty("pinned", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var p in arr.EnumerateArray())
                if (p.TryGetProperty("hwnd", out var h)) d[h.ToString()] = p.TryGetProperty("name", out var n) ? n.ToString() : "";
        _ui.TryEnqueue(() => {
            if (!_onTopKnown || OnTopProblem != null) _log($"bar tasks: keep on top: the pin state is known ({d.Count} window(s) kept on top)");
            _onTopKnown = true;
            OnTopPinned = d; OnTopProblem = null; OnTopChanged?.Invoke();
        });
    }

    /// On the host thread: ontop.state, retried every 1.5 s while the feature is not up yet (at most <paramref name="left"/> times).
    async Task AskOnTop(int left)
    {
        if (_bus == null || _svc == null) return;
        var c = await _bus.CallAsync("ontop.state", null, "bar").ConfigureAwait(true);
        if (c.Ok) { TakeOnTop(c.Result); return; }
        if (left == 20) _log("bar tasks: keep on top: the pin state did not answer yet (" + (c.Error ?? "no answer") + "); asking again every 1.5 s");
        if (left > 1) _ontopAsk = _host?.Delay(TimeSpan.FromMilliseconds(1500), () => _ = AskOnTop(left - 1));
        else OnTopCant("the pin state never answered (" + (c.Error ?? "no answer") + ")");
    }

    /// The menu's "Keep on top" / "Stop keeping on top" (ontop.set {hwnd, pid, name, on}); a refusal comes back in words.
    public void OnTopSet(string hwnd, int pid, bool on, string name, Action<JumpAct> done)
    {
        if (_bus == null || _host == null) { done(new JumpAct(false, "keep on top: the bar did not reach Mantle's commands")); return; }
        var bus = _bus;
        var args = new JsonObject { ["hwnd"] = hwnd, ["on"] = on, ["name"] = name.Length > 48 ? name[..48] : name };
        if (pid > 0) args["pid"] = pid;
        _host.Post(async () => {
            JumpAct r;
            try {
                var c = await bus.CallAsync("ontop.set", args, "bar").ConfigureAwait(true);
                if (!c.Ok) r = new JumpAct(false, c.Error ?? "keep on top did not answer");
                else {
                    var o = c.Result;
                    bool ok = o.TryGetProperty("ok", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.True;
                    string text = o.TryGetProperty("text", out var t) ? t.ToString() : "";
                    string? why = o.TryGetProperty("reason", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.String ? w.GetString() : null;
                    string did = o.TryGetProperty("did", out var dd) ? dd.ToString() : "";
                    r = new JumpAct(ok, ok ? text : (why != null ? $"{text}: {why}" : text), DryRun: did == "dry", Did: did);
                }
            } catch (Exception e) { r = new JumpAct(false, e.Message); }
            _ui.TryEnqueue(() => done(r));
        });
    }

    public void Dispose()
    {
        _ontopSub?.Dispose(); _toastSub?.Dispose(); _ontopAsk?.Dispose();
        var s = _svc; _svc = null;
        var st = _stores; _stores = null;
        if (s != null) _host?.Post(() => { s.Dispose(); st?.Dispose(); });
        else st?.Dispose();
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
