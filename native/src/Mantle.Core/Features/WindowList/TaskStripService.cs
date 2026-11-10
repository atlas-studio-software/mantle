using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Mantle.Core.Features.Icons;
using Mantle.Core.Features.Pinned;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.WindowList;

public sealed class TaskStripOptions
{
    public IPlatform Platform = null!;
    public IHostScheduler Host = null!;
    /// The TaskBar pins folder to read; null = no pins, with <see cref="PinsNote"/> said instead (a test run with no scratch folder).
    public string? PinsDir;
    public string? PinsNote;
    /// The Taskband Favorites value (throws with plain words); null = not read.
    public Func<byte[]>? ReadFavorites;
    /// Mantle's data folder (features\jumplist.json, Mantle's own pin overlay, read on every build).
    public string UserData = "";
    /// features\jumplist.json's store (through the ONE write gate): a dragged pin order is saved here (jumplist.order).
    /// Null = a drag of a pin is refused in words.
    public Mantle.Core.Data.IStore? Overlay;
    /// A --test run: the order's log line says "(in this test profile)".
    public bool Test;
    /// A --test run's MANTLE_TEST_ORDER_FAIL: every pin order save fails with these words (the spring-back test).
    public string? OrderFailForTest;
    /// The new strip, on the host thread (the bar marshals it to its UI thread).
    public Action<TaskList> Publish = _ => { };
    /// A windows push that is not a list (BAR-259), or null when a good one clears it.
    public Action<string?> WindowsProblem = _ => { };
    /// The active marks re-read from a windows push, between rebuilds (bar.js renderWindows), on the host thread.
    public Action<TaskList>? Marks;
    public Action<string> Log = _ => { };
    /// Watch the pins folder (500 ms debounce, re-arm every 30 s). Off in unit tests.
    public bool WatchPins = true;
    public ITaskActions? Actions;
}

/// <summary>
/// The strip's host side (main.js: lastWindows, buildPinned, pinnedOnce, schedulePinnedPush, the pins cache and watch,
/// 'bar.open' / 'bar.window'), on the host thread. The running windows come from the platform's <c>windows</c> push
/// (the sidecar, out of process); if none has arrived, one <c>windows</c> call is made rather than an empty strip (BAR-286).
/// </summary>
public sealed class TaskStripService : IDisposable
{
    public static readonly TimeSpan PushEvery = TimeSpan.FromMilliseconds(1000);   // schedulePinnedPush (BAR-297)
    public static readonly TimeSpan PinsCacheFor = TimeSpan.FromMinutes(5);       // PINS_CACHE_MS (BAR-279)
    public static readonly TimeSpan PinsDebounce = TimeSpan.FromMilliseconds(500); // lib/pinned.js DEBOUNCE_MS (BAR-277)
    public static readonly TimeSpan PinsRetry = TimeSpan.FromSeconds(30);          // RETRY_MS (BAR-278)

    readonly TaskStripOptions _o;
    readonly SidecarCall _call;
    readonly IconResolver _icons;
    readonly RunningOrder _runOrder = new();
    readonly FirstSeenOrder _firstSeen = new();
    readonly List<IDisposable> _subs = new();
    List<WinEntry>? _lastWindows;
    PinnedResult? _pinsCache;
    double _pinsCacheAt;
    string? _pushWhy;
    IDisposable? _pushTimer;
    double _lastPushAt = double.NegativeInfinity;
    string? _lastProblem;
    Task<TaskList>? _building;
    FileSystemWatcher? _watch;
    IDisposable? _watchDebounce, _watchRetry;
    string? _watchReason;
    bool _disposed;

    public TaskList Last { get; private set; } = TaskList.Empty;
    public int Builds { get; private set; }
    public int WindowPushes { get; private set; }

    public TaskStripService(TaskStripOptions o)
    {
        _o = o;
        _call = TaskJson.From(o.Platform);
        _icons = new IconResolver(_call, () => o.Host.NowMs,
            () => o.Platform.Status.Ready ? null : (o.Platform.Status.Reason.Length > 0 ? o.Platform.Status.Reason : "it is not running"));
    }

    public void Start()
    {
        _subs.Add(_o.Platform.On("windows", e => OnWindows(e.Body)));
        _subs.Add(_o.Platform.On("appchanged", _ => { _pinsCache = null; Schedule("a Store app changed"); }));
        _subs.Add(_o.Platform.OnReady(() => Schedule("the sidecar is ready")));
        if (_o.WatchPins && _o.PinsDir != null) ArmWatch();
        Schedule("the bar started");
    }

    void OnWindows(System.Text.Json.JsonElement body)
    {
        WindowPushes++;
        var list = RunningWindows.WithoutMantle(TaskJson.Get(body, "list"));
        if (list == null) {
            _o.WindowsProblem("the list of open windows could not be read, so the running marks may be out of date");
            return;
        }
        _o.WindowsProblem(null);
        _lastWindows = list;
        // bar.js renderWindows: the active line follows the window in front on every push, not once a second, and a
        // click decides focus or minimise from the same marks the user sees
        var marked = TaskStripBuilder.MarkActive(Last, list);
        if (!ReferenceEquals(marked, Last)) { Last = marked; _o.Marks?.Invoke(marked); }
        Schedule("the running windows changed");
    }

    /// Push at most once a second however often the window set churns, reasons merged (schedulePinnedPush).
    public void Schedule(string why)
    {
        if (_disposed) return;
        _pushWhy = _pushWhy != null && _pushWhy != why && !_pushWhy.Contains(why, StringComparison.Ordinal) ? _pushWhy + "; " + why : why;
        if (_pushTimer != null) return;
        double wait = Math.Max(0, PushEvery.TotalMilliseconds - (_o.Host.NowMs - _lastPushAt));
        _pushTimer = _o.Host.Delay(TimeSpan.FromMilliseconds(wait), () => _ = PushAsync());
    }

    async Task PushAsync()
    {
        _pushTimer = null;
        string reason = _pushWhy ?? "something changed";
        _pushWhy = null;
        _lastPushAt = _o.Host.NowMs;
        var res = await BuildOnceAsync().ConfigureAwait(true);
        if (_disposed) return;
        _o.Publish(res);
        // once per CHANGE of reason, not once per second
        if (!res.Ok && res.Reason != _lastProblem) { _lastProblem = res.Reason; _o.Log($"mantle: the bar's pinned list is incomplete ({reason}): {res.Reason}"); }
        else if (res.Ok && _lastProblem != null) { _lastProblem = null; _o.Log("mantle: the bar's pinned list is complete again"); }
    }

    /// One build at a time, however many things ask at once (pinnedOnce).
    public Task<TaskList> BuildOnceAsync()
    {
        if (_building != null) return _building;
        // A build that finishes synchronously (windows already pushed, pins cached, every icon known) has already
        // cleared _building inside BuildCoreAsync: keeping its finished task here would answer every later build
        // with that first list, and the strip would never change again.
        var t = BuildCoreAsync();
        if (!t.IsCompleted) _building = t;
        return t;
    }

    async Task<TaskList> BuildCoreAsync()
    {
        TaskList res;
        try { res = await BuildAsync().ConfigureAwait(true); }
        catch (Exception e) { res = new TaskList(false, "the pinned list could not be built: " + e.Message, Array.Empty<TaskItem>()); }
        // a push that arrived while it was built: its marks win over the ones the build started from
        if (_lastWindows != null) res = TaskStripBuilder.MarkActive(res, _lastWindows);
        _building = null;
        Last = res;
        Builds++;
        return res;
    }

    async Task<TaskList> BuildAsync()
    {
        var input = new TaskStripBuilder.Input { Icons = _icons, RunningOrder = _runOrder, FirstSeen = _firstSeen, PinsNote = _o.PinsNote };
        // 1. the running apps; nothing pushed yet = ask once
        var running = _lastWindows;
        if (running == null) {
            try {
                var r = await _call("windows", new System.Text.Json.Nodes.JsonObject(), TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
                running = RunningWindows.WithoutMantle(r) ?? new List<WinEntry>();
                _lastWindows ??= running;
            } catch (Exception e) {
                input.RunningProblem = "the running apps could not be read: " + e.Message;
            }
        }
        input.Running = running;
        // 2. the pins (cached 5 min, only a clean read)
        if (_o.PinsDir != null) {
            var r = _pinsCache != null && _o.Host.NowMs - _pinsCacheAt < PinsCacheFor.TotalMilliseconds ? _pinsCache : null;
            if (r == null) {
                r = await PinnedReader.ListAsync(new PinnedOptions {
                    PinsDir = _o.PinsDir, ReadFavorites = _o.ReadFavorites, Call = _call, WatchReason = _watchReason,
                }).ConfigureAwait(true);
                if (r.Ok && (r.Reason == null || r.Reason.IndexOf("sidecar", StringComparison.OrdinalIgnoreCase) < 0)) { _pinsCache = r; _pinsCacheAt = _o.Host.NowMs; }
            }
            input.Pins = r;
        }
        // 3. Mantle's own pin overlay (unreadable = Windows' pins as they are, with the reason)
        try { input.Overlay = PinOverlay.Read(_o.UserData); }
        catch (Exception e) { input.OverlayProblem = e.Message; }
        return await TaskStripBuilder.BuildAsync(input).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- clicks (main.js 'bar.open' / 'bar.window')
    public async ValueTask<TaskOpenResult> OpenAsync(string id, bool fresh)
    {
        var list = Last.Items.Count > 0 ? Last : await BuildOnceAsync().ConfigureAwait(true);
        if (_o.Actions == null) return new TaskOpenResult(false, "the bar's app actions are not connected");
        var res = await TaskOpen.OpenAsync(list, id, fresh, _o.Actions, _o.Log).ConfigureAwait(true);
        if (res.Ok && res.Action is "focus" or "minimize") Schedule("a window was " + (res.Action == "focus" ? "focused" : "minimised"));
        return res;
    }

    public async ValueTask<TaskOpenResult> WindowAsync(string hwnd, string action)
    {
        if (_o.Actions == null) return new TaskOpenResult(false, "the bar's app actions are not connected");
        return await TaskOpen.WindowAsync(Last, hwnd, action, _o.Actions).ConfigureAwait(true);
    }

    // ---------------------------------------------------------------- drag to reorder (jumplist.order, BAR-243, BAR-252..254)
    /// One group's ids in their new order: pins saved in Mantle's own pin list, running-only apps for this session. The
    /// strip is rebuilt from the host's list straight after, saved or not (bar.js orderRefresh), so a failed save puts
    /// the icons back where the host has them.
    public async ValueTask<OrderSaved> OrderAsync(string? group, IReadOnlyList<string?>? ids, int from, int to)
    {
        var r = await OrderSave.SaveAsync(group, ids, from, to, _o.Overlay, _runOrder, _o.Log, _o.Test, _o.OrderFailForTest).ConfigureAwait(true);
        if (_disposed) return r;
        // a push already waiting is replaced by one for the order: it still keeps to once a second
        _pushTimer?.Dispose(); _pushTimer = null;
        Schedule("the order changed");
        return r;
    }

    // ---------------------------------------------------------------- the pins folder watch (lib/pinned.js watchPins)
    void ArmWatch()
    {
        _watchRetry?.Dispose(); _watchRetry = null;
        try {
            var w = new FileSystemWatcher(_o.PinsDir!) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => _o.Host.Post(PinsChanged);
            w.Created += h; w.Deleted += h; w.Changed += h;
            w.Renamed += (_, _) => _o.Host.Post(PinsChanged);
            w.Error += (_, e) => _o.Host.Post(() => WatchDied("the pinned shortcuts' folder watch stopped (" + e.GetException().Message + ")"));
            w.EnableRaisingEvents = true;
            bool cameBack = _watch == null && _watchReason != null;
            _watch = w;
            _watchReason = null;
            if (cameBack) PinsChanged();   // a watch that comes back fires once
        } catch (Exception e) {
            WatchDied("the pinned shortcuts are not being watched, so a new pin only appears when the window list changes (" +
                      (e is ArgumentException ? "the pins folder is missing" : e.Message) + ")");
        }
    }

    void WatchDied(string why)
    {
        if (_disposed) return;
        _watch?.Dispose(); _watch = null;
        _watchReason = why;
        _watchRetry ??= _o.Host.Delay(PinsRetry, ArmWatch);
    }

    void PinsChanged()
    {
        if (_disposed) return;
        _watchDebounce?.Dispose();
        _watchDebounce = _o.Host.Delay(PinsDebounce, () => { _watchDebounce = null; _pinsCache = null; Schedule("the pinned shortcuts changed"); });
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        _pushTimer?.Dispose(); _watchDebounce?.Dispose(); _watchRetry?.Dispose();
        _watch?.Dispose(); _watch = null;
    }
}
