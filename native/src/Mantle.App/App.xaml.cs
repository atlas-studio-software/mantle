using System;
using System.Linq;
using Mantle.Core;
using Mantle.Core.Data;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mantle.App;

/// <summary>
/// Command line (Mantle.Core.LaunchArgs has the full list):
///   MantleNative.exe [--theme=light|dark|system] [--bench-out=&lt;dir&gt;] [--no-backdrop]
///                    [--test --profile-name=&lt;x&gt;] [--user-data=&lt;dir&gt;] [--quit]
///   --theme       pins the island's appearance for this run; without it the bar follows
///                 Mantle's theme.json (Electron's Settings > Personalization > Colors)
///   --bench-out   write ready.json (first frame, geometry), cells.json (dock cell rects) and
///                 native-log.txt there, for the bench
///   --no-backdrop skip the captured, blurred backdrop: the tint carries the island alone
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
        // The app-wide theme is fixed before the resources load, so XAML and code see the same tokens.
        // System leaves it to Windows' app mode, like Mantle's "Match Windows".
        var mode = StartMode();
        if (mode == ThemeMode.Dark) RequestedTheme = ApplicationTheme.Dark;
        else if (mode == ThemeMode.Light) RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) => {
            Host.Log.Line("UNHANDLED: " + e.Exception);
            // SAFE-018: the crash record (only where this run may write its profile)
            var plan = Mantle.Core.Lifecycle.Crash.Record(Host.Paths.DataRoot, Mantle.Core.Safety.WriteGate.May(Host.Paths, Host.Args.Kind),
                e.Exception?.Message, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Host.Log.Line("mantle: Mantle " + plan.Why);
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
        StartPlatform();
        Host.Instance.Listen(
            argv => _queue.TryEnqueue(() => OnSecondLaunch(argv)),
            err => Host.Log.Line("single instance: " + err));
    }

    void OnSecondLaunch(string[] argv)
    {
        Host.Log.Line("single instance: a second launch handed off: " + (argv.Length == 0 ? "(no arguments)" : string.Join(' ', argv)));
        if (argv.Any(a => LaunchArgs.NameOf(a) == "quit")) {
            Host.Log.Line("--quit from a second launch: closing");
            StopPlatform("Mantle was asked to quit (--quit)");
            _window?.Close();
            Exit();
        }
    }

    // The platform on its own host thread (PLAN.md A6): today's Mantle.Sidecar.exe, passive unless --live.
    Mantle.Core.Lifecycle.HostLoop? _hostLoop;
    Mantle.Core.Platform.ProcessPlatform? _platform;

    void StartPlatform()
    {
        try {
            var choice = Mantle.Core.Platform.SidecarLocator.Resolve(Host.Args, Host.Env, AppContext.BaseDirectory);
            Host.Log.Line(choice.Line);
            _hostLoop = new Mantle.Core.Lifecycle.HostLoop();
            _hostLoop.OnError += e => Host.Log.Line("mantle-native: a host-thread job failed: " + e);
            var opts = new Mantle.Core.Platform.SidecarOptions {
                ExePath = choice.Path, ExeOverride = choice.Override, Passive = Host.Args.Kind != RunKind.Live,
                StateFile = System.IO.Path.Combine(Host.Paths.DataRoot, "shell-state.json"),
                Log = (level, message) => Host.Log.Line($"[sidecar {level}] {message}"),
            };
            _hostLoop.Post(() => {
                _platform = new Mantle.Core.Platform.ProcessPlatform(opts, new Mantle.Core.Platform.ProcessSidecarLauncher(_hostLoop), _hostLoop);
                _platform.OnStatus(s => Host.Log.Line($"mantle-native: sidecar {s.State.ToString().ToLowerInvariant()}: {s.Reason}"));
                _platform.Start();
            });
        } catch (Exception e) {
            Host.Log.Line("mantle-native: the platform could not be set up: " + e.Message);
        }
    }

    void StopPlatform(string why)
    {
        var loop = _hostLoop;
        if (loop == null) return;
        try {
            var stopped = loop.RunAsync(async () => { if (_platform != null) await _platform.StopAsync(why); return true; });
            if (!stopped.Wait(TimeSpan.FromSeconds(4))) Host.Log.Line("mantle-native: the sidecar did not stop within 4 s; it ends itself when this process exits (--parent-pid)");
        } catch (Exception e) {
            Host.Log.Line("mantle-native: stopping the sidecar failed: " + e.Message);
        }
    }
}
