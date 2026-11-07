// Bar spike: primitives 3 (bar placement + reveal) and 6 (multi-monitor + DPI) of native Mantle.
//
//   Bar.exe --out=<dir> --tag=<name> [--mode=hover|dock|restore] [--anim=comp|swp|swpflush]
//           [--theme=dark|light] [--trace-frames=N]
//
// hover    the default Mantle bar: hidden at rest, a cursor poll of the bottom 4 DIP reveals it.
// dock     a real app bar (ABM_NEW/SETPOS) reserving 48 DIP of the work area, always shown.
// restore  put back what a killed instance changed (shell-state.json), then exit.
// anim     comp = a parked window, the pill animated by the compositor (Translation/Scale/Opacity);
//          swp = the window walked with SetWindowPos on its own thread at ~60 fps (the sidecar's
//          Morph.cs); swpflush = the same, paced by DwmFlush instead of Sleep.
//
// Writes <out>/<tag>-log.txt, <tag>-events.jsonl (one JSON object per line: t = unix ms, ev, ...),
// <tag>-ready.json and <tag>-exit.json. Quit cleanly with WM_CLOSE to the bar window.
using System;
using System.IO;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace BarSpike;

sealed class Opts
{
    public string Out = ".", Tag = "bar", Mode = "hover", Anim = "comp", Theme = "dark";
    public int TraceFrames;
    public static Opts Parse(string[] argv)
    {
        var o = new Opts();
        foreach (var a in argv) {
            string V() => a[(a.IndexOf('=') + 1)..].Trim('"');
            if (a.StartsWith("--out=")) o.Out = V();
            else if (a.StartsWith("--tag=")) o.Tag = V();
            else if (a.StartsWith("--mode=")) o.Mode = V();
            else if (a.StartsWith("--anim=")) o.Anim = V();
            else if (a.StartsWith("--theme=")) o.Theme = V();
            else if (a.StartsWith("--trace-frames=")) o.TraceFrames = int.Parse(V());
        }
        return o;
    }
}

static class Log
{
    static readonly object Gate = new();
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static string P(string name) => Path.Combine(App.O.Out, $"{App.O.Tag}-{name}");
    public static void Line(string s)
    {
        lock (Gate) { try { File.AppendAllText(P("log.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { } }
    }
    /// One event line. `data` is an anonymous object; its properties are merged after t and ev.
    public static void Ev(string ev, object? data = null)
    {
        string body = data == null ? "" : JsonSerializer.Serialize(data);
        string line = $"{{\"t\":{Now()},\"ev\":\"{ev}\"" + (body.Length > 2 ? "," + body[1..] : "}");
        lock (Gate) { try { File.AppendAllText(P("events.jsonl"), line + Environment.NewLine); } catch { } }
        Line(ev + " " + body);
    }
    public static void Json(string name, object o)
    {
        string tmp = P(name) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(o));
        File.Move(tmp, P(name), true);
    }
}

public partial class App : Application
{
    internal static Opts O = new();
    BarWindow? _bar;

    public App()
    {
        O = Opts.Parse(Environment.GetCommandLineArgs());
        Directory.CreateDirectory(O.Out);
        InitializeComponent();
        UnhandledException += (_, e) => Log.Ev("unhandled", new { error = e.Exception.ToString() });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Ev("launched", new { O.Mode, O.Anim, O.Theme, O.TraceFrames, pid = Environment.ProcessId });
        // The dirty-start guard runs first, in every mode: a killed instance's changes go back before
        // this one records its own original state.
        string restored = ShellState.RestoreIfStale();
        Log.Ev("dirtyStart", new { restored });
        if (O.Mode == "restore") {
            Log.Json("restore.json", new { restored, taskbarState = Taskbar.State(), workArea = W.WorkArea().J });
            Environment.Exit(0);
            return;
        }
        _bar = new BarWindow(O, restored);
    }
}
