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
        UnhandledException += (_, e) => Host.Log.Line("UNHANDLED: " + e.Exception);
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
        Host.Instance.Listen(
            argv => _queue.TryEnqueue(() => OnSecondLaunch(argv)),
            err => Host.Log.Line("single instance: " + err));
    }

    void OnSecondLaunch(string[] argv)
    {
        Host.Log.Line("single instance: a second launch handed off: " + (argv.Length == 0 ? "(no arguments)" : string.Join(' ', argv)));
        if (argv.Any(a => LaunchArgs.NameOf(a) == "quit")) {
            Host.Log.Line("--quit from a second launch: closing");
            _window?.Close();
            Exit();
        }
    }
}
