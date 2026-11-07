using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Mantle.Core;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Features.Tray;
using Mantle.Core.Lifecycle;
using Mantle.Core.Platform;
using Mantle.Core.Safety;
using Mantle.Core.State;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mantle.App;

/// <summary>
/// Command line (Mantle.Core.LaunchArgs has the full list):
///   MantleNative.exe [--theme=light|dark|system] [--bench-out=&lt;dir&gt;] [--no-backdrop]
///                    [--test --profile-name=&lt;x&gt;] [--user-data=&lt;dir&gt;] [--quit] [--log=&lt;file&gt;]
///
/// The shell host: the UI thread creates the windows; everything else (ShellState, the command bus, the
/// events, the platform, the shell guard, crash recovery, the feature host, the tray) lives on the host
/// thread (HostLoop), in main.js's order. Every quit road goes through ONE shutdown (CORE-025..028).
/// </summary>
public partial class MantleApp : Application
{
    BarWindow? _window;
    DispatcherQueue? _queue;

    /// The theme the bar shows at start: --theme, else theme.json's mode (system = follow Windows).
    internal static ThemeMode StartMode()
        => Host.Args.Theme switch {
            "dark" => ThemeMode.Dark,
            "light" => ThemeMode.Light,
            "system" => ThemeMode.System,
            _ => Host.Data.Theme.Value.Mode,
        };

    public MantleApp()
    {
        var mode = StartMode();
        if (mode == ThemeMode.Dark) RequestedTheme = ApplicationTheme.Dark;
        else if (mode == ThemeMode.Light) RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) => {
            Host.Log.Line("UNHANDLED: " + e.Exception);
            // SAFE-018: the crash record goes through the ONE write gate; then Windows is put back and we exit 1
            var plan = Crash.Record(Host.Paths.DataRoot, Host.Files, e.Exception?.Message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Host.Log.Line("mantle: Mantle " + plan.Why);
            if (plan.Restart) _supervisor?.CrashNote(Crash.What(e.Exception?.Message));
            else _supervisor?.StandDown("Mantle " + plan.Why);
            e.Handled = true;
            Quit("Mantle stopped with an error", 1, crash: true);
        };
        Host.Marks.Add("app");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();
        Host.Log.Line($"launched; theme {ThemeSettings.Word(StartMode())}{(Host.Args.Theme != null ? " (pinned by --theme)" : " (from " + Host.Data.Theme.Name + ")")}; app theme {RequestedTheme}");
        _window = new BarWindow();
        Host.Marks.Add("window");
        _window.ShowBar();

        Host.Data.Changed += c => _queue.TryEnqueue(() => _window?.OnDataChanged(c));
        Host.Data.StartWatching();
        StartHost();
        _session = SessionWindow.Create(Host.Log.Line, (q, wp, lp) => OnSession(q, wp, lp));
        Host.Instance.Listen(
            argv => _hostLoop?.Post(() => OnSecondLaunch(argv)),
            err => Host.Log.Line("single instance: " + err));
    }

    // ------------------------------------------------------------------ the host thread

    HostLoop? _hostLoop;
    ProcessPlatform? _platform;
    ShellStateHub? _hub;
    CommandBus? _bus;
    EventHub? _events;
    ShellGuard? _guard;
    Supervisor? _supervisor;
    FeatureHost? _features;
    TrayController? _tray;
    Mantle.Core.Keys.IGlobalHotkeys? _hotkeys;
    StateBroadcast? _broadcast;
    Stores? _stores;
    SessionEnd? _sessionEnd;
    readonly Shutdown _shutdown = new(l => Host.Log.Line(l), Host.RecoveryCommand);
    readonly List<IDisposable> _offs = new();
    string? _dirtyNote;
    int _readies;
    object? _session;

    void StartHost()
    {
        try {
            var choice = SidecarLocator.Resolve(Host.Args, Host.Env, AppContext.BaseDirectory);
            Host.Log.Line(choice.Line);
            _hostLoop = new HostLoop();
            _hostLoop.OnError += e => Host.Log.Line("a host-thread job failed: " + e);
            var opts = new SidecarOptions {
                ExePath = choice.Path, ExeOverride = choice.Override, Passive = Host.Args.Kind != RunKind.Live,
                StateFile = Host.StateFile,
                Log = (level, message) => Host.Log.Line($"[sidecar {level}] {message}"),
            };
            _hostLoop.Post(() => HostStart(opts, choice.Path));
        } catch (Exception e) {
            Host.Log.Line("the platform could not be set up: " + e.Message);
        }
    }

    void HostStart(SidecarOptions opts, string sidecarExe)
    {
        var loop = _hostLoop!;
        bool live = Host.Args.Kind == RunKind.Live, test = Host.Args.Test;
        var log = new Action<string>(Host.Log.Line);
        _hub = new ShellStateHub(ShellState.Initial(live ? ShellMode.Live : ShellMode.Test, Host.Args.Has("bar-edge") && Host.Args.ValueOf("bar-edge") == "top" ? BarEdge.Top : BarEdge.Bottom), log);
        _bus = new CommandBus(new CallLog(log));
        _events = new EventHub(log);
        _broadcast = new StateBroadcast(_hub, _events, log, () => _tray?.Request());
        _platform = new ProcessPlatform(opts, new ProcessSidecarLauncher(loop), loop);
        _platform.OnStatus(s => {
            Host.Log.Line($"sidecar {s.State.ToString().ToLowerInvariant()}: {s.Reason}");
            _hub.Set(st => st with { Sidecar = new SidecarPiece(s.State, s.Reason) });
        });
        _sessionEnd = new SessionEnd(log, why => Quit(why, 0), () => loop.NowMs);

        // SAFE-019/023: the next start says it, once
        var rec = Crash.Read(System.IO.Path.Combine(Host.Paths.DataRoot, Crash.FileName));
        string? recovery = Crash.RecoveryLine(rec.At, rec.Restarted, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            t => DateTimeOffset.FromUnixTimeMilliseconds(t).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        if (recovery != null) log("mantle: crash recovery: " + recovery);

        _guard = new ShellGuard(new ShellGuardOptions {
            Platform = _platform, State = _hub, Host = loop, Test = test,
            TestTbhide = test && Host.Args.Has("test-tbhide") && !string.IsNullOrEmpty(Host.Env("MANTLE_TBHIDE_TEST_CLASS")),
            TbHide = !Host.Args.Has("no-tbhide"), EdgeClip = !Host.Args.Has("no-edgeclip"), Dock = Host.Args.Has("dock"),
            Pill = !Host.Args.Has("flush") && !Host.Args.Has("dock"),
            RecoveryCommand = Host.RecoveryCommand, Log = log, Blocked = () => _platform.BlockedReason(),
            RunScript = () => DirtyStart.RunScript(Host.RestoreScript, Host.StateFile),
            StopFeatures = async w => { if (_features != null) foreach (var l in await _features.StopAllAsync(w).ConfigureAwait(true)) log("mantle: " + l.Line); },
            Released = w => _features?.Released(w),
            Note = () => _dirtyNote ?? "",
            // TODO(native-windows): the app bar (--dock) and the desktop layer add their releases here
        });

        // the feature host on the ONE write gate's stores
        _stores = Stores.For(Host.Paths, Host.Gate, Host.Log);
        var clock = new HostClock(null, loop);
        _features = new FeatureHost(new FeatureHostOptions {
            Flags = new FeatureFlags(test, live), Platform = _platform, Commands = _bus, Events = _events, Stores = _stores,
            State = _hub, Log = log, Clock = clock,
        });
        _offs.Add(Catalogs.Register(_bus, new FeatureCommands(_features)));   // generated; replaces FeatureCommands.Register
        _offs.Add(Catalogs.Register(_bus, new HostCommands(_hub, RestoreAsync, why => Quit(why, 0), () => { _platform.Retry(); return "asked the sidecar to start again"; })));
        _offs.Add(Catalogs.RegisterStatic(_bus));
        FeatureSeam.Start(_hub, () => _features.Load(Catalogs.Features), log);

        // the tray and the shell shortcuts (feature-host-2)
        _hotkeys = Mantle.App.Shell.Tray.ShellTray.MakeHotkeys(test, loop.Post, log);
        Mantle.App.Shell.Tray.ShellTray.RegisterShortcuts(_hub, test, _hotkeys, why => _supervisor?.StandDown(why), RestoreAsync, why => Quit(why, 0),
            () => log("mantle: Alt+Space: the launcher is not built yet"), log);   // TODO(round 3): toggleLauncher("Alt+Space")
        _tray = new TrayController(new TrayOptions {
            Icon = Mantle.App.Shell.Tray.ShellTray.MakeIcon(test, loop.Post, log), Clock = clock, Commands = _bus, Test = test, State = _hub,
            Features = _features, Log = log,
            Problems = () => ShellStateHub.Problems(_hub.Current).Select(p => new TrayProblem(p.Piece, p.Reason)).ToArray(),
            SidecarStatus = () => _platform.Status.Reason, SidecarGaveUp = () => _platform.GaveUp, Recovery = () => recovery,
            StandDown = why => _supervisor?.StandDown(why), Restore = RestoreAsync, Quit = why => Quit(why, 0),
            Report = via => new ValueTask<string>(Report(via, recovery)),
            WriteTestReport = text => Host.Files.WriteAtomic(System.IO.Path.Combine(Host.Paths.DataRoot, "diagnostic-report.txt"),
                System.Text.Encoding.UTF8.GetBytes(text), System.IO.Path.Combine(Host.Paths.DataRoot, "diagnostic-report.txt.tmp")),
        });
        if (_tray.Start() is { } trayWhy) log("mantle: tray: " + trayWhy);
        // TODO(data-look-2): ThemeFollow.Start + LookHost (look.get/glass/accent/sync, theme rebroadcast, Refresh on resume/unlock)
        // TODO(feature-host-2): RestCoordinator with the surfaces' IRestParts, rest.TrimSoon("60 s after start")

        // SAFE-020..024: crash recovery with the native record (MantleNative.exe, never Electron)
        _supervisor = new Supervisor(new SupervisorOptions {
            DataRoot = Host.Paths.DataRoot, Files = Host.Files, Host = loop, SidecarExe = sidecarExe,
            ExePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "MantleNative.exe"),
            Argv = Host.Args.Raw, Live = live, Test = test, Supervise = Host.Args.Supervise, NoSupervise = Host.Args.NoSupervise,
            ProfileName = Host.Args.ValueOf("profile-name"), AdoptPid = Host.Env("MANTLE_SUPERVISOR_PID"), Log = log,
            Spawn = SpawnSupervisor,
        });
        Environment.SetEnvironmentVariable("MANTLE_SUPERVISOR_PID", null);   // never handed on to the sidecar or to apps
        var sup = _supervisor.Start();
        log($"mantle: crash recovery {(sup.On ? "on" : "off")} ({sup.Via}): {sup.Why}");

        // SAFE-017: the dirty-start guard runs BEFORE the sidecar records a new "original"
        var dirty = DirtyStart.Judge(live, ShellStateNote.Read(Host.StateFile));
        if (dirty.RunRestore) {
            var (ok, why) = DirtyStart.RunScript(Host.RestoreScript, Host.StateFile);
            _dirtyNote = " " + DirtyStart.Outcome(ok, Host.RecoveryCommand, why);
        } else if (dirty.Note != null) _dirtyNote = " " + dirty.Note;

        // CORE-023/SAFE-002: every ready asks the protocol age first (with retries), then takes the shell (live only)
        _platform.OnReady(() => _ = OnReadyAsync());
        _platform.Start();
    }

    async Task OnReadyAsync()
    {
        int n = ++_readies;
        if (_shutdown.Started || _platform == null || _guard == null) return;
        (bool Current, bool? DpiAware, bool Answered, string Reason) age = default;
        for (int i = 0; i < 3; i++) {   // the first ping can be late on a cold start: 3 tries
            age = await _platform.ProtocolAgeAsync().ConfigureAwait(true);
            if (age.Answered) break;
            Host.Log.Line($"mantle: the sidecar did not answer its first ping (try {i + 1} of 3): {age.Reason}");
        }
        if (!age.Current) { Host.Log.Line("mantle: " + age.Reason); _guard.Unavailable(age.Reason); return; }
        if (Host.Args.Kind != RunKind.Live) return;
        if (n == 1) await _guard.ApplyLiveAsync().ConfigureAwait(true); else await _guard.ReapplyAfterRestartAsync().ConfigureAwait(true);
        if (_features != null) await _features.ApplyLiveAsync("the sidecar is ready").ConfigureAwait(true);
    }

    ISupervisorChild SpawnSupervisor(string exe, IReadOnlyList<string> args, Action<string> line, Action<int?> exited)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("the supervisor launcher did not start");
        p.EnableRaisingEvents = true;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) _hostLoop?.Post(() => line(e.Data)); };
        p.Exited += (_, _) => { int? c = null; try { c = p.ExitCode; } catch (Exception) { } _hostLoop?.Post(() => exited(c)); };
        p.BeginOutputReadLine();
        return new Child(p.Id);
    }

    sealed class Child : ISupervisorChild { public Child(int pid) { Pid = pid; } public int Pid { get; } }

    string Report(string via, string? recovery)
    {
        var s = _hub!.Current;
        var problems = ShellStateHub.Problems(s).Select(p => new TrayProblem(p.Piece, p.Reason)).ToArray();
        var list = _features?.List() ?? Array.Empty<FeatureInfo>();
        var snap = new ReportSnapshot(DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture), typeof(MantleApp).Assembly.GetName().Version?.ToString(), null,
            ".NET " + Environment.Version + ", Windows App SDK", Environment.OSVersion.VersionString, Host.Args.Test, Environment.TickCount64 / 1000.0,
            Array.Empty<ReportDisplay>(), null, TrayModel.Health(problems, Host.Args.Test, false, _platform!.Status.Reason, _platform.GaveUp), recovery,
            _platform.Status.Reason, _platform.Restarts, _platform.GaveUp, list.Count(f => f.Enabled), list.Count(f => !f.Enabled), Array.Empty<string>(), Array.Empty<string>());
        return TrayReport.Build(snap);
    }

    Task RestoreAsync(string why) => _guard?.RestoreAsync(why) ?? Task.CompletedTask;

    // ------------------------------------------------------------------ second launches and session end

    void OnSecondLaunch(string[] argv)
    {
        Host.Log.Line("single instance: a second launch handed off: " + (argv.Length == 0 ? "(no arguments)" : string.Join(' ', argv)));
        var (ask, value) = SecondLaunch.Route(argv, Host.Args.Test);
        switch (ask) {
            case SecondAsk.Quit: Quit("Mantle was asked to quit (--quit)", 0); break;
            case SecondAsk.Open: Host.Log.Line($"mantle: --open={value}: that window is not built in native Mantle yet"); break;
            case SecondAsk.Tray: Host.Log.Line("mantle: test-tray: " + (_tray?.TestTrayLine() ?? "the tray was never built")); break;
            case SecondAsk.Report:
                if (_tray != null) _ = _tray.CopyReportAsync("a second instance (test)").ContinueWith(t => { if (t.IsFaulted) Host.Log.Line("mantle: test-report failed: " + t.Exception?.GetBaseException().Message); }, TaskScheduler.Default);
                break;
            case SecondAsk.Chord: Host.Log.Line($"mantle: {value} from a second instance: {_features?.FireChord(value!, "the command line")}"); break;
            case SecondAsk.Key: Host.Log.Line($"mantle: {value} from a second instance: {_features?.FireKey(value!, "the command line")}"); break;
            case SecondAsk.Crash:
                Host.Log.Line("mantle: test-crash: throwing an uncaught exception on the next tick (test mode)");
                _queue?.TryEnqueue(() => throw new InvalidOperationException("test crash asked for by a second instance"));
                break;
            case SecondAsk.Memory:
                Host.Log.Line($"mantle: memory ({value}): {Environment.WorkingSet / 1048576.0:0.0} MB working set");
                foreach (var f in _hub?.Current.Features ?? System.Collections.Immutable.ImmutableSortedDictionary<string, FeaturePiece>.Empty)
                    Host.Log.Line($"mantle: state ({value}): {f.Key}: {(f.Value.Ok ? "healthy" : "NOT healthy")}{(f.Value.Resting ? " RESTING" : "")}{(f.Value.Stopped ? " STOPPED" : "")}: {f.Value.Reason} | {f.Value.KeysReason}");
                break;
            case SecondAsk.Click: _events?.Publish("bar.test.click", value ?? ""); break;
            case SecondAsk.Unload: Host.Log.Line("mantle: test-unload: nothing to unload"); break;   // TODO(feature-host): IdleUnloader.UnloadAll
            default: Host.Log.Line("mantle: another Mantle tried to start; this one is already running, so that one exited."); break;
        }
    }

    /// The session window's messages, on the UI thread: answered at once, the road runs on the host thread.
    IntPtr OnSession(bool query, IntPtr wParam, IntPtr lParam)
    {
        ulong lp = unchecked((ulong)(long)lParam);
        if (query) { _hostLoop?.Post(() => _sessionEnd?.Query(lp)); return (IntPtr)1; }
        bool ending = wParam != IntPtr.Zero;
        var se = _sessionEnd;
        if (se == null || _hostLoop == null) return IntPtr.Zero;
        _hostLoop.Post(() => se.End(ending, lp));
        if (ending) {
            // Windows may end the process once this returns: wait for the restore (the road's own 12 s hard stop)
            for (int i = 0; i < 130 && !_quitDone.Task.IsCompleted; i++) _quitDone.Task.Wait(100);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ the ONE shutdown (CORE-025/026)

    readonly TaskCompletionSource<int> _quitDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

    void Quit(string why, int code, bool crash = false)
    {
        var loop = _hostLoop;
        if (loop == null) { Program.ExitCode = code; _queue?.TryEnqueue(Exit); return; }
        loop.Post(() => {
            if (_shutdown.Started) { _ = _shutdown.RunAsync(why, code, Array.Empty<Shutdown.Step>()); return; }
            var steps = new List<Shutdown.Step>();
            if (!crash) steps.Add(new("stand the supervisor down", TimeSpan.FromSeconds(1), _ => { _supervisor?.StandDown(why); return Task.CompletedTask; }));
            steps.Add(new("unregister the global shortcuts", TimeSpan.FromSeconds(1), _ => { _hotkeys?.UnregisterAll(); return Task.CompletedTask; }));
            steps.Add(new("put Windows back", TimeSpan.FromSeconds(10), _ => RestoreAsync(why)));
            steps.Add(new("stop the sidecar", TimeSpan.FromSeconds(4), async _ => { if (_platform != null) await _platform.StopAsync(why).ConfigureAwait(true); }));
            steps.Add(new("destroy the tray", TimeSpan.FromSeconds(1), _ => { _tray?.Dispose(); (_hotkeys as IDisposable)?.Dispose(); _broadcast?.Dispose(); foreach (var o in _offs) o.Dispose(); _stores?.Dispose(); return Task.CompletedTask; }));
            _shutdown.RunAsync(why, code, steps).ContinueWith(t => {
                int exit = t.IsCompletedSuccessfully ? t.Result : code;
                Program.ExitCode = exit;
                _quitDone.TrySetResult(exit);
                _queue?.TryEnqueue(() => { SessionWindow.Destroy(_session); _window?.Close(); Exit(); });
            }, TaskScheduler.Default);
        });
    }

    /// A hidden top-level window: the only kind Windows asks WM_QUERYENDSESSION / WM_ENDSESSION.
    static class SessionWindow
    {
        const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
        delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        static WndProc? _proc;
        static Func<bool, IntPtr, IntPtr, IntPtr>? _on;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW { public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

        public static object? Create(Action<string> log, Func<bool, IntPtr, IntPtr, IntPtr> on)
        {
            try {
                _on = on;
                _proc = Proc;
                var c = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = "MantleNativeSession" };
                if (RegisterClassExW(ref c) == 0) { log($"mantle: the session-end window class was not registered (error {Marshal.GetLastWin32Error()}); a sign-out then ends Mantle without its restore, and the sidecar's own restore on stdin EOF puts Windows back"); return null; }
                IntPtr h = CreateWindowExW(0x00000080 /* WS_EX_TOOLWINDOW */, "MantleNativeSession", "Mantle session", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, c.hInstance, IntPtr.Zero);
                if (h == IntPtr.Zero) { log($"mantle: the session-end window was not made (error {Marshal.GetLastWin32Error()}); the sidecar's own restore covers a sign-out"); return null; }
                log("mantle: listening for Windows sign-out, restart and shut down (session-end window)");
                return h;
            } catch (Exception e) { log("mantle: the session-end window failed: " + e.Message); return null; }
        }

        public static void Destroy(object? h) { if (h is IntPtr p && p != IntPtr.Zero) DestroyWindow(p); }

        static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if ((m == WM_QUERYENDSESSION || m == WM_ENDSESSION) && _on != null) {
                try { return _on(m == WM_QUERYENDSESSION, w, l); } catch (Exception) { return m == WM_QUERYENDSESSION ? (IntPtr)1 : IntPtr.Zero; }
            }
            return DefWindowProcW(h, m, w, l);
        }
    }
}
