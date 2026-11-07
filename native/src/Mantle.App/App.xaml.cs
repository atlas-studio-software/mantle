using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace MantleBar;

/// Command line: MantleBar.exe [--theme=light|dark|system] [--bench-out=<dir>] [--no-backdrop]
///   --theme       the island's appearance (default: follow Windows, like Mantle's "Match Windows")
///   --bench-out   write ready.json (first frame, geometry) and cells.json (dock cell rects) there,
///                 for native-spike/bench/run-bench.ps1. Also the log.
///   --no-backdrop skip the captured, blurred backdrop: the tint carries the island alone (its fallback fill)
internal sealed class BarOptions
{
    public string Theme = "system";
    public string? BenchOut;
    public bool NoBackdrop;
    public readonly long LaunchedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static BarOptions Parse(string[] argv)
    {
        var o = new BarOptions();
        foreach (var a in argv) {
            if (a.StartsWith("--theme=", StringComparison.Ordinal)) o.Theme = a[8..].ToLowerInvariant();
            else if (a.StartsWith("--bench-out=", StringComparison.Ordinal)) o.BenchOut = a[12..].Trim('"');
            else if (a == "--no-backdrop") o.NoBackdrop = true;
        }
        if (o.BenchOut != null) Directory.CreateDirectory(o.BenchOut);
        return o;
    }

    public void Log(string line)
    {
        if (BenchOut == null) return;
        try { File.AppendAllText(Path.Combine(BenchOut, "native-log.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + line + Environment.NewLine); } catch (Exception) { }
    }
}

public partial class App : Application
{
    internal static readonly BarOptions Opt = BarOptions.Parse(Environment.GetCommandLineArgs());
    BarWindow? _window;

    public App()
    {
        // The app-wide theme is fixed before the resources load, so XAML and code see the same tokens.
        if (Opt.Theme == "dark") RequestedTheme = ApplicationTheme.Dark;
        else if (Opt.Theme == "light") RequestedTheme = ApplicationTheme.Light;
        InitializeComponent();
        UnhandledException += (_, e) => Opt.Log("UNHANDLED: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Opt.Log("launched; theme " + Opt.Theme + " (app " + RequestedTheme + ")");
        _window = new BarWindow(Opt);
        _window.ShowBar();
    }
}
