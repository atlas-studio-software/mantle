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
using Mantle.Core.Look;
using Mantle.Core.Platform;
using Mantle.Core.Safety;
using Mantle.Core.State;
using Mantle.App.Shell.Hosting;
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

    /// The key Match Windows reads and watches: Windows' own, or a --test run's scratch key (MANTLE_TEST_THEME_KEY).
    internal static string ThemeKey => ThemeCore.KeyFor(Host.Args.Test, Host.Env);

    /// The theme rebroadcast's UI end (main.js broadcast('theme')): raised on the UI thread after every change of the
    /// theme or of its --lg-* overrides (theme.set, Match Windows, another app's look). Surfaces built from descriptors
    /// get the same payload as the host's "theme" event; the bar (BarWindow) takes it here until it is one.
    internal static event Action<ThemePayload>? ThemeShown;
    /// The last payload ThemeShown carried (null before the host is up).
    internal static ThemePayload? ThemeNow { get; private set; }

    public MantleApp()
    {
        var mode = StartMode();
        if (mode == ThemeMode.Dark) RequestedTheme = ApplicationTheme.Dark;
        else if (mode == ThemeMode.Light) RequestedTheme = ApplicationTheme.Light;
        else {
            // Match Windows: resolved from the key this run watches (a test's scratch key, never the user's, in a test run)
            var w = ThemeCore.ReadWindowsMode(ThemeKey);
            RequestedTheme = w.Mode == "dark" ? ApplicationTheme.Dark : ApplicationTheme.Light;
        }
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
        // the surface host (Shell/Host): every [Surface] of the catalog; made once the host's pieces exist (HostStart)
        _surfaces = new SurfaceHost(_queue, Host.Args.Test, SurfaceSet.TestSurfacesWanted(Host.Args.Test, Host.Env), Host.Log);
        Host.Log.Line($"launched; theme {ThemeSettings.Word(StartMode())}{(Host.Args.Theme != null ? " (pinned by --theme)" : " (from " + Host.Data.Theme.Name + ")")}; app theme {RequestedTheme}");
        _window = new BarWindow();
        _window.FollowHostTheme();   // the bar takes the host's theme (ThemeShown) and keeps its own theme.json path
        // The bar closed = quit through the ONE shutdown. Surfaces made at start (the launcher, the OSD) are hidden windows,
        // so the bar is no longer the app's last window and WinUI's "last window closed" exit would never come.
        // (Both the launcher and the OSD lanes needed this; one guarded copy.)
        _barOpen = true;
        _window.Closed += (_, _) => { _barOpen = false; if (!_shutdown.Started) Quit("the bar window was closed", 0); };
        Host.Marks.Add("window");
        _window.ShowBar();

        Host.Data.Changed += c => {
            _queue.TryEnqueue(() => _window?.OnDataChanged(c));
            // the host's theme follows theme.json too (Electron's Settings writes it), so every surface hears "theme"
            if (c.File == DataFile.Theme) _hostLoop?.Post(() => _look?.ThemeFileChanged());
        };
        Host.Data.StartWatching();
        StartHost();
        _session = SessionWindow.Create(Host.Log.Line, (q, wp, lp) => OnSession(q, wp, lp), why => _hostLoop?.Post(() => OnWake(why)));
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
    LookShell? _look;
    SurfaceHost? _surfaces;
    SurfaceControl? _surfaceControl;
    StateFeed? _feed;
    SessionEnd? _sessionEnd;
    RestCoordinator? _rest;
    AppBarLease? _appBar;
    volatile bool _barOpen;
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
        // the surfaces' road from the host thread (surface.show/hide/toggle/list, Alt+Space, --open): posted to the UI thread
        _surfaceControl = new SurfaceControl(() => _surfaces?.Set, work => _surfaces?.PostUi(work) ?? false);
        _offs.Add(Catalogs.Register(_bus, new SurfaceCommands(_surfaceControl)));
        _platform = new ProcessPlatform(opts, new ProcessSidecarLauncher(loop), loop);
        // --- lane native-bar-tasks: the bar's task strip (pins + running apps) reads through the platform's sidecar.
        // The bar was made in OnLaunched before StartHost, so its strip host already exists; this is the host thread.
        _window?.Tasks?.AttachOnHost(_platform, loop);
        // --- end native-bar-tasks
        // the bar's dock (--dock only; hover is the live default) reserves its app bar through the platform. A test or
        // scratch run's sidecar is passive, so it refuses and nothing is reserved in Windows (the dock line says so).
        var platform = _platform;
        // the host holds the lease on the app bar, so "put Windows back" gives it back first, while the sidecar can still be asked
        var appBar = _appBar = new AppBarLease(new Mantle.App.Surfaces.Bar.Frame.PlatformBarShell(platform, loop), platform);
        _queue?.TryEnqueue(() => {
            if (_window == null) return;
            _window.UseShell(appBar);
            Host.Log.Line($"bar: the platform is wired to the bar's dock ({(Host.Args.Has("dock") ? "--dock: it asks for its app bar now" : "not docked: hover or up, nothing to reserve")}{(live ? "" : "; this run's sidecar is passive and reserves nothing")})");
        });
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
            // main.js restoreWindows: the app bar first (while the sidecar can still be asked), then the rest. The desktop
            // layer adds ("the desktop layer", detach) here when it is built (none in this build).
            MoreReleases = () => appBar.Releases("Windows was put back"),
        });

        // the feature host on the ONE write gate's stores
        _stores = Stores.For(Host.Paths, Host.Gate, Host.Log);
        var clock = new HostClock(null, loop);
        _features = new FeatureHost(new FeatureHostOptions {
            Flags = new FeatureFlags(test, live), Platform = _platform, Commands = _bus, Events = _events, Stores = _stores,
            State = _hub, Log = log, Clock = clock,
        });
        // features.list always; features.fire only in a --test run (its [Command] When = TestRun), both generated
        _offs.Add(Catalogs.Register(_bus, new FeatureCommands(_features)));
        _offs.Add(Catalogs.Register(_bus, new HostCommands(_hub, RestoreAsync, why => Quit(why, 0), () => { _platform.Retry(); return "asked the sidecar to start again"; })));
        _offs.Add(Catalogs.RegisterStatic(_bus));
        FeatureSeam.Start(_hub, () => _features.Load(Catalogs.Features), log);

        // the tray and the shell shortcuts (feature-host-2)
        _hotkeys = Mantle.App.Shell.Tray.ShellTray.MakeHotkeys(test, loop.Post, log);
        Mantle.App.Shell.Tray.ShellTray.RegisterShortcuts(_hub, test, _hotkeys, why => _supervisor?.StandDown(why), RestoreAsync, why => Quit(why, 0),
            () => _ = SurfaceControl.ToggleFromKey(_surfaceControl!, "launcher", ShellShortcutsKey, log), log);   // main.js toggleLauncher('Alt+Space')
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

        // the look (main.js getTheme + getLook): theme.json and look-sync.json through the ONE gate's stores, look.json
        // through its writer (only a --live sole shell writes the user's own; test and scratch runs their own folder)
        string themeKey = ThemeKey;
        if (themeKey != ThemeCore.PersonalizeKey) log($"theme: this test run follows Windows' mode from HKCU\\{themeKey} (MANTLE_TEST_THEME_KEY), never the user's own");
        _look = new LookShell(new LookShellOptions {
            Stores = _stores, Files = Host.Files, LookDir = Host.Paths.LookSyncDir, WindowsMode = () => ThemeCore.ReadWindowsMode(themeKey),
            Post = loop.Post, Events = _events, Log = Host.Log, PinnedTheme = Host.Args.Theme,
        });
        _offs.Add(Catalogs.Register(_bus, _look));
        _look.Shown += p => { ThemeNow = p; _queue?.TryEnqueue(() => ThemeShown?.Invoke(p)); };
        ThemeNow = _look.Payload();
        _look.Start();
        _look.FollowWindows(themeKey);   // Match Windows follows a change at once (RegNotifyChangeKeyValue), not by polling
        log($"theme: Mantle is {_look.Now}; the host's theme reaches {ThemeShown?.GetInvocationList().Length ?? 0} surface(s) directly" +
            (ThemeShown == null ? " (the bar still follows theme.json itself)" : ""));
        // main.js replayAnswer('theme'): a surface that subscribes gets the theme now, so its first frame is right
        var look = _look;
        _offs.Add(_events.Replay("theme", () => look.Payload()));
        // ---- launcher wiring (lane native-launcher): apps.index / apps.search / apps.launch, the index warmed at start (LAU-017).
        // A --test run indexes only the scratch fixture MANTLE_TEST_LAUNCHER_DIR names, never the machine's Start Menu.
        string launcherWhy = "";
        var launcherInputs = test ? Mantle.Core.Features.LauncherIndex.IndexInputs.ForTestRun(Host.Env(Mantle.Core.Features.LauncherIndex.IndexInputs.TestDirEnv), out launcherWhy)
            : Mantle.Core.Features.LauncherIndex.IndexInputs.ForWindows();
        if (test) log("launcher: " + launcherWhy);
        var launcher = new Mantle.Core.Features.Run.LauncherService(launcherInputs, test, Host.Log);
        _offs.Add(Mantle.Core.Features.Run.LauncherCommands.Register(_bus, new Mantle.Core.Features.Run.LauncherCommands(launcher)));
        // the rows' icons: the sidecar's "icon" / "app" through the shared IconResolver (main.js makeIconResolver); its own cache
        var launcherPlatform = _platform;
        var launcherIcons = new Mantle.Core.Features.Icons.IconResolver(Mantle.Core.Features.Run.LauncherService.SidecarOf(launcherPlatform), () => loop.NowMs,
            () => launcherPlatform.Status.Ready ? null : (launcherPlatform.Status.Reason.Length > 0 ? launcherPlatform.Status.Reason : "it is not running"));
        launcher.IconSource = path => launcherIcons.ForAsync(new[] { path });
        // the launcher searches the host's snapshot on its own thread (no bus hop per keystroke: LAU-016 latency)
        Mantle.App.Surfaces.Launcher.LauncherSurface.Service = launcher;
        _ = launcher.IndexAsync();
        // ---- end launcher wiring
        StartSurfaces(log);
        // main.js restMantle: game mode's host.rest(on, why) destroys the surfaces (the surface host hears "mantle.rest"),
        // hands the Win key to Windows' Start and trims; the wake makes the AtStart surfaces again. The bar and its app bar
        // are kept, as Electron keeps them.
        var surfaces = _surfaces;
        _rest = new RestCoordinator(new RestOptions {
            Platform = _platform, Events = _events, State = _hub, Clock = clock, Features = _features, Test = test,
            NoLauncher = Host.Env("MANTLE_NO_LAUNCHER") == "1" || !SurfaceHost.Builds(ShellRestParts.Launcher),
            Closing = () => _shutdown.Started, Log = l => log("mantle: " + l),
            Parts = new ShellRestParts(new ShellRestPartsOptions {
                Made = () => surfaces?.Made ?? Array.Empty<string>(),
                BarOpen = () => _barOpen,
                HideLauncher = why => surfaces?.PostUi(() => surfaces.Set?.Hide(ShellRestParts.Launcher, why)),
            }),
        });
        _rest.TrimSoon("60 s after start");

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

    /// Alt+Space (ShellShortcuts.Launcher): it toggles the "launcher" surface.
    const string ShellShortcutsKey = Mantle.Core.Keys.ShellShortcuts.Launcher;

    /// The surface host's start (host thread): the UI thread's copy of ShellState (coalesced), then the AtStart
    /// surfaces made on the UI thread, once the bus, the events, the state and the look exist.
    void StartSurfaces(Action<string> log)
    {
        var hub = _hub!; var loop = _hostLoop!; var surfaces = _surfaces;
        if (surfaces == null) { log("surfaces: the surface host was never made, so no surface is made"); return; }
        var feed = _feed = new StateFeed(hub.Current, surfaces.PostUi, log);
        _offs.Add(hub.OnChange(feed.Push));
        // a rest while a game runs ("mantle.rest" from the rest coordinator): every surface destroyed; the AtStart ones made
        // again on the wake, the others on their next use
        _offs.Add(_events!.Subscribe<RestSignal>("mantle.rest", sig => surfaces.PostUi(() => {
            if (surfaces.Set is not { } set) return;
            var names = sig.On ? set.Rest(sig.Why ?? "a game runs") : set.Wake(sig.Why ?? "the rest is over");
            Host.Log.Line($"surfaces: {(sig.On ? "rest" : "wake")}: {(names.Count == 0 ? "nothing to " + (sig.On ? "destroy" : "make again") : (sig.On ? "destroyed " : "made again ") + string.Join(", ", names))}");
        })));
        var wires = new SurfaceWires { PostUi = surfaces.PostUi, PostHost = loop.Post, Bus = _bus!, Events = _events!, Feed = feed, Log = Host.Log };
        if (!surfaces.PostUi(() => surfaces.Start(wires, "start"))) log("surfaces: the UI thread is gone, so no surface was made");
    }

    /// --open=&lt;target&gt; from a second start (main.js 'second-instance'): the allow-list again, then the surface of
    /// that name is shown (made on first use). A --test run also opens the build's test stand-ins.
    async Task OpenAsync(string[] argv)
    {
        var req = OpenRequest.From(argv, Host.Args.Test ? SurfaceHost.TestTargets() : null);
        if (req == null) return;
        if (!req.Ok) { Host.Log.Line(req.Line); return; }
        if (_surfaceControl == null) { Host.Log.Line($"mantle: a second start asked for {req.Label} (--open={req.Target}), but it did not open: the surfaces are not set up"); return; }
        var a = await _surfaceControl.RunAsync("show", req.Target!, $"a second start (--open={req.Target})").ConfigureAwait(true);
        Host.Log.Line(a.Ok ? req.Line : $"mantle: a second start asked for {req.Label} (--open={req.Target}), but it did not open: {a.Said}");
    }

    /// --test-surface=&lt;show|hide|toggle&gt;:&lt;name&gt; from a second start (test only): through the surface.* commands.
    async Task SurfaceFromSecondAsync(string spec)
    {
        int c = spec.IndexOf(':');
        string verb = c > 0 ? spec[..c] : "?", name = c > 0 ? spec[(c + 1)..] : spec;
        if (_bus == null) { Host.Log.Line($"mantle: test-surface {spec}: the command bus is not up"); return; }
        const string who = "a second instance (test)";
        var r = await _bus.CallAsync("surface." + verb, new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["why"] = who }, who).ConfigureAwait(true);
        string said = r.Ok && r.Result.ValueKind == System.Text.Json.JsonValueKind.Object && r.Result.TryGetProperty("said", out var w) ? w.GetString() ?? "" : r.Error ?? "";
        Host.Log.Line($"mantle: test-surface {verb}:{name}: {(r.Ok ? "ok" : "NOT ok")}: {said}");
    }

    /// --test-theme=&lt;mode&gt; from a second start (test only): theme.set {mode}, the call a page makes; the answer is logged.
    async Task ThemeFromSecondAsync(string mode)
    {
        if (_bus == null) { Host.Log.Line($"mantle: test-theme {mode}: the command bus is not up"); return; }
        var r = await _bus.CallAsync("theme.set", new System.Text.Json.Nodes.JsonObject { ["mode"] = mode }, "a second instance (test)").ConfigureAwait(true);
        Host.Log.Line($"mantle: test-theme {mode}: {(r.Ok ? "ok: " + (r.Result.ValueKind == System.Text.Json.JsonValueKind.Undefined ? "(no answer)" : r.Result.GetRawText()) : "NOT ok: " + r.Error)}");
    }

    /// --test-rest=&lt;on|off&gt;[:&lt;why&gt;] from a second start (test only): host.rest(on, why), the call game mode makes.
    async Task RestFromSecondAsync(string spec)
    {
        int c = spec.IndexOf(':');
        string verb = c > 0 ? spec[..c] : spec, why = c > 0 && c < spec.Length - 1 ? spec[(c + 1)..] : "a test asked";
        if (verb is not ("on" or "off")) { Host.Log.Line($"mantle: test-rest {spec}: NOT ok: say on or off (--test-rest=on:<why>)"); return; }
        if (_rest == null) { Host.Log.Line($"mantle: test-rest {verb}: NOT ok: the rest is not set up"); return; }
        var a = await _rest.RestAsync(verb == "on", why).ConfigureAwait(true);
        Host.Log.Line($"mantle: test-rest {verb}: {(a.Ok ? "ok" : "NOT ok")}: {a.Text ?? a.Reason}");
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
            case SecondAsk.Open: _ = OpenAsync(argv); break;
            case SecondAsk.Surface: _ = SurfaceFromSecondAsync(value ?? ""); break;
            case SecondAsk.Theme: _ = ThemeFromSecondAsync(value ?? ""); break;
            case SecondAsk.Rest: _ = RestFromSecondAsync(value ?? ""); break;
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

    /// Resume and unlock (host thread): fs watching can miss events across sleep, so the shared look is read once.
    void OnWake(string why)
    {
        if (_look != null) _look.Refresh(why);
        else Host.Log.Line($"mantle: {why}; the look is not up yet, so there is nothing to re-read");
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
            steps.Add(new("destroy the surfaces", TimeSpan.FromSeconds(2), async _ => {
                if (_surfaces == null) return;
                string said = await _surfaces.OnUiAsync(s => { var g = s.Close(why); return g.Count == 0 ? "none was made, so none was destroyed" : "destroyed " + string.Join(", ", g); }).ConfigureAwait(true);
                Host.Log.Line("surfaces: " + said);
            }));
            steps.Add(new("put Windows back", TimeSpan.FromSeconds(10), _ => RestoreAsync(why)));
            // the restore gives the app bar back first (MoreReleases); when it could not run (a test run, the link down) and
            // one is still reserved, it goes here, still BEFORE the sidecar stops (main.js shutdown step 1)
            steps.Add(new("give the app bar back", TimeSpan.FromSeconds(4), async _ => {
                if (_appBar is not { } lease || lease.Reserved == IntPtr.Zero) return;
                var r = await lease.GiveBackAsync().ConfigureAwait(true);
                Host.Log.Line(r.Ok ? "mantle: app bar removed" : $"mantle: the app bar is still registered ({r.Error}); Explorer drops it when the bar's window goes");
            }));
            steps.Add(new("save what is still being saved", TimeSpan.FromSeconds(3), _ => _look?.SavedAsync() ?? Task.CompletedTask));
            steps.Add(new("stop the sidecar", TimeSpan.FromSeconds(4), async _ => { if (_platform != null) await _platform.StopAsync(why).ConfigureAwait(true); }));
            steps.Add(new("destroy the tray", TimeSpan.FromSeconds(1), _ => { _tray?.Dispose(); (_hotkeys as IDisposable)?.Dispose(); _broadcast?.Dispose(); _look?.Dispose(); foreach (var o in _offs) o.Dispose(); _stores?.Dispose(); return Task.CompletedTask; }));
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
        const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_POWERBROADCAST = 0x0218, WM_WTSSESSION_CHANGE = 0x02B1;
        const int PBT_APMRESUMEAUTOMATIC = 0x12, WTS_SESSION_UNLOCK = 0x8;
        delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
        static WndProc? _proc;
        static Func<bool, IntPtr, IntPtr, IntPtr>? _on;
        static Action<string>? _wake;
        [DllImport("wtsapi32.dll", SetLastError = true)] static extern bool WTSRegisterSessionNotification(IntPtr h, int flags);
        [DllImport("wtsapi32.dll")] static extern bool WTSUnRegisterSessionNotification(IntPtr h);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEXW { public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

        /// <param name="wake">resume from sleep and session unlock (main.js powerMonitor 'resume' / 'unlock-screen'), on this window's thread</param>
        public static object? Create(Action<string> log, Func<bool, IntPtr, IntPtr, IntPtr> on, Action<string> wake)
        {
            try {
                _on = on; _wake = wake;
                _proc = Proc;
                var c = new WNDCLASSEXW { cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = "MantleNativeSession" };
                if (RegisterClassExW(ref c) == 0) { log($"mantle: the session-end window class was not registered (error {Marshal.GetLastWin32Error()}); a sign-out then ends Mantle without its restore, and the sidecar's own restore on stdin EOF puts Windows back"); return null; }
                IntPtr h = CreateWindowExW(0x00000080 /* WS_EX_TOOLWINDOW */, "MantleNativeSession", "Mantle session", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, c.hInstance, IntPtr.Zero);
                if (h == IntPtr.Zero) { log($"mantle: the session-end window was not made (error {Marshal.GetLastWin32Error()}); the sidecar's own restore covers a sign-out"); return null; }
                log("mantle: listening for Windows sign-out, restart and shut down (session-end window)");
                // the same window hears resume (WM_POWERBROADCAST goes to every top-level window) and, once registered, unlock
                if (!WTSRegisterSessionNotification(h, 0 /* NOTIFY_FOR_THIS_SESSION */))
                    log($"mantle: unlock notices were not registered (error {Marshal.GetLastWin32Error()}); the shared look is still re-read on resume and on every change");
                return h;
            } catch (Exception e) { log("mantle: the session-end window failed: " + e.Message); return null; }
        }

        public static void Destroy(object? h) { if (h is IntPtr p && p != IntPtr.Zero) { WTSUnRegisterSessionNotification(p); DestroyWindow(p); } }

        static IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
        {
            if (m == WM_POWERBROADCAST && (long)w == PBT_APMRESUMEAUTOMATIC) { try { _wake?.Invoke("the PC woke from sleep"); } catch (Exception) { } return (IntPtr)1; }
            if (m == WM_WTSSESSION_CHANGE && (long)w == WTS_SESSION_UNLOCK) { try { _wake?.Invoke("the screen was unlocked"); } catch (Exception) { } return IntPtr.Zero; }
            if ((m == WM_QUERYENDSESSION || m == WM_ENDSESSION) && _on != null) {
                try { return _on(m == WM_QUERYENDSESSION, w, l); } catch (Exception) { return m == WM_QUERYENDSESSION ? (IntPtr)1 : IntPtr.Zero; }
            }
            return DefWindowProcW(h, m, w, l);
        }
    }
}
