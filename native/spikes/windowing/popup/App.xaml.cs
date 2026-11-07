// Popup spike app. Two ways to run it:
//
//   Popup.exe --out=<dir> --cold [--frame= --theme= --fill= --x= --y= --w= --h=]
//       opens one popup straight away and writes <out>/cold.json with the process start time and the
//       first frame's time (the cold-open measurement). Exits only when killed.
//   Popup.exe --out=<dir> --pipe=<name>
//       opens NOTHING and serves one command per line on \\.\pipe\<name>, one JSON reply per line:
//         open id=N [frame= theme= fill= hit= park= x= y= w= h= sw= sh= r=]   new window, shown; replies at its first frame
//         show id=N            un-park (in its park mode); replies at the next frame (or 500 ms)
//         park id=N park=MODE  hide it in MODE (apphide | swhide | offscreen | cloak)
//         resize id=N w= h=    same top-left; replies at the next frame
//         top id=N             re-assert topmost (notes say whether it had been lost)
//         close id=N | info id=N | events | fixture x= y= w= h= | gc | quit
//       The pipe thread blocks on the read (no polling), so an idle app costs nothing for the pipe.
// Log: <out>/log.txt.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PopupSpike;

public partial class App : Application
{
    internal static string Out = ".";
    static readonly object s_logLock = new();
    static readonly List<object> s_events = new();
    readonly Dictionary<string, string> _args = new();
    readonly Dictionary<int, PopupWindow> _wins = new();
    DispatcherQueue? _dq;
    Window? _fixture;

    public App()
    {
        foreach (var a in Environment.GetCommandLineArgs().Skip(1)) {
            if (!a.StartsWith("--")) continue;
            int eq = a.IndexOf('=');
            if (eq < 0) _args[a[2..]] = "1"; else _args[a[2..eq]] = a[(eq + 1)..].Trim('"');
        }
        if (_args.TryGetValue("out", out var o)) Out = o;
        Directory.CreateDirectory(Out);
        // Closing the last window must not end the app: the warm and parked measurements close windows.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        InitializeComponent();
        UnhandledException += (_, e) => Log("UNHANDLED: " + e.Exception);
    }

    internal static double NowMs() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;   // precise system time: comparable across processes

    internal static void Log(string s)
    {
        lock (s_logLock) {
            try { File.AppendAllText(Path.Combine(Out, "log.txt"), $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {s}{Environment.NewLine}"); } catch { }
        }
    }

    internal static void AddEvent(object e) => s_events.Add(e);   // UI thread only

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        double tLaunched = NowMs();
        _dq = DispatcherQueue.GetForCurrentThread();
        Log("launched " + string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));
        if (_args.ContainsKey("cold")) {
            var w = new PopupWindow(1, PopupSpec.From(_args));
            double tCtor = NowMs();
            _wins[1] = w;
            w.Open(tLaunched, 5000, d => {
                double start = (Process.GetCurrentProcess().StartTime.ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
                double? render = d["renderMs"] as double?;
                d["processStartMs"] = start;
                d["startToLaunchedMs"] = tLaunched - start;
                d["ctorMs"] = tCtor - tLaunched;
                d["startToFirstFrameMs"] = render is double r ? tLaunched + r - start : null;
                File.WriteAllText(Path.Combine(Out, "cold.json"), JsonSerializer.Serialize(d));
                Log($"cold: first frame {d["startToFirstFrameMs"]} ms after process start");
            });
        }
        if (_args.TryGetValue("pipe", out var pipe)) new Thread(() => PipeLoop(pipe)) { IsBackground = true, Name = "pipe" }.Start();
    }

    // ------------------------------------------------------------------ the command pipe
    void PipeLoop(string name)
    {
        while (true) {
            try {
                using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
                server.WaitForConnection();
                Log("pipe connected");
                var rd = new StreamReader(server, new UTF8Encoding(false));
                var wr = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
                string? line;
                while ((line = rd.ReadLine()) != null) {
                    double tRecv = NowMs();
                    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    string cmd = line;
                    bool queued = _dq!.TryEnqueue(DispatcherQueuePriority.High, () => {
                        try { Handle(cmd, tRecv, tcs); }
                        catch (Exception ex) { Log($"'{cmd}' threw {ex}"); tcs.TrySetResult(JsonSerializer.Serialize(new { ok = false, error = ex.Message })); }
                    });
                    if (!queued) tcs.TrySetResult("{\"ok\":false,\"error\":\"enqueue failed\"}");
                    wr.WriteLine(tcs.Task.Result);
                }
                Log("pipe closed by the client");
            } catch (Exception ex) { Log("pipe: " + ex.Message); Thread.Sleep(200); }
        }
    }

    void Handle(string line, double tRecv, TaskCompletionSource<string> tcs)
    {
        double queueMs = NowMs() - tRecv;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string verb = parts.Length > 0 ? parts[0] : "";
        var a = new Dictionary<string, string>();
        foreach (var p in parts.Skip(1)) { int eq = p.IndexOf('='); if (eq > 0) a[p[..eq]] = p[(eq + 1)..]; }
        int Int(string k, int d) => a.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : d;
        int id = Int("id", 0);
        void Reply(Dictionary<string, object?> d) { d["queueMs"] = queueMs; tcs.TrySetResult(JsonSerializer.Serialize(d)); }
        Dictionary<string, object?> Ok() => new() { ["ok"] = true };
        PopupWindow? W() => _wins.TryGetValue(id, out var w) ? w : null;

        switch (verb) {
            case "open": {
                if (_wins.ContainsKey(id)) { Reply(new() { ["ok"] = false, ["error"] = $"id {id} exists" }); return; }
                double t1 = NowMs();
                var w = new PopupWindow(id, PopupSpec.From(a));
                double ctorMs = NowMs() - t1;
                _wins[id] = w;
                w.Open(tRecv, 3000, d => { d["ctorMs"] = ctorMs; Reply(d); });
                Log($"open {id}: {line}");
                return;
            }
            case "show": { var w = W(); if (w == null) break; w.Show(tRecv, Reply); return; }
            case "park": { var w = W(); if (w == null) break; Reply(w.Park(a.TryGetValue("park", out var m) ? m : "apphide")); return; }
            case "resize": { var w = W(); if (w == null) break; w.Resize(tRecv, Int("w", 400), Int("h", 200), Reply); return; }
            case "info": { var w = W(); if (w == null) break; Reply(w.Info()); return; }
            case "top": { var w = W(); if (w == null) break; w.EnsureTopmost("top command"); Reply(w.Info()); return; }
            case "close": {
                var w = W(); if (w == null) break;
                double t1 = NowMs();
                w.Close(); _wins.Remove(id);
                var d = Ok(); d["callMs"] = NowMs() - t1; Reply(d);
                return;
            }
            case "events": {
                var d = Ok();
                d["events"] = s_events.ToArray();
                d["moves"] = _wins.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Moves);
                s_events.Clear();
                foreach (var w in _wins.Values) w.Moves = 0;
                Reply(d);
                return;
            }
            case "fixture": {
                // A plain opaque WinUI window on THIS thread: is a same-thread window reached by HTTRANSPARENT?
                var g = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 90, 90, 90)) };
                g.PointerPressed += (_, e) => AddEvent(new { win = -1, kind = "fixturePressed", t = NowMs() });
                var fw = new Window { Content = g };
                var p = OverlappedPresenter.Create(); p.IsResizable = false; p.SetBorderAndTitleBar(false, false);
                fw.AppWindow.SetPresenter(p);
                fw.AppWindow.IsShownInSwitchers = false;
                fw.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(Int("x", 0), Int("y", 0), Int("w", 100), Int("h", 100)));
                fw.Activate();
                _fixture?.Close();
                _fixture = fw;
                foreach (var w in _wins.Values) Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                var d = Ok(); d["hwnd"] = WinRT.Interop.WindowNative.GetWindowHandle(fw).ToInt64(); Reply(d);
                return;
            }
            case "unfixture": { _fixture?.Close(); _fixture = null; Reply(Ok()); return; }
            case "gc": {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                var d = Ok(); d["managedMB"] = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1); Reply(d);
                return;
            }
            case "quit": { Reply(Ok()); _dq!.TryEnqueue(DispatcherQueuePriority.Low, () => Exit()); return; }
            default: Reply(new() { ["ok"] = false, ["error"] = "unknown verb " + verb }); return;
        }
        Reply(new() { ["ok"] = false, ["error"] = $"no window {id}" });
    }
}
