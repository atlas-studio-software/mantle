// HotkeySpike: primitive 8, global shortcuts in the WinUI process. KeyHook (the port) runs the
// low-level keyboard hook on its own thread and posts every gesture to this UI thread, which logs it
// with the high-resolution timestamps the check needs for latency (QPC is one clock for every process).
//
//   HotkeySpike.exe --out=<dir> [--accept-injected] [--hook-on=own|ui] [--altspace=hook|registerhotkey]
// Writes <out>/ready.json, <out>/status.json (every 250 ms), <out>/events.jsonl (one line per gesture
// handled on the UI thread) and <out>/log.txt. Takes commands from <out>/cmd.txt ("<id> <verb> <arg>",
// the newest line wins): block <ms> = the UI thread sleeps, stall <ms> = the next hook callback sleeps,
// freeze <ms> = the hook thread stops pumping, drop = the hook is removed behind KeyHook's back (test).
using System;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HotkeySpike;

public partial class App : Application
{
    string _out = ".", _hookOn = "own", _altSpace = "hook";
    bool _acceptInjected;
    Window? _win;
    TextBlock? _text;
    KeyHook? _hook;
    DispatcherQueue? _dq;
    int _uiThread, _n, _lastCmd;

    public App()
    {
        foreach (var a in Environment.GetCommandLineArgs()) {
            if (a.StartsWith("--out=")) _out = a[6..].Trim('"');
            else if (a == "--accept-injected") _acceptInjected = true;
            else if (a.StartsWith("--hook-on=")) _hookOn = a[10..];
            else if (a.StartsWith("--altspace=")) _altSpace = a[11..];
        }
        Directory.CreateDirectory(_out);
        InitializeComponent();
        UnhandledException += (_, e) => { Log("UNHANDLED: " + e.Exception); e.Handled = true; };
    }

    void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(_out, "log.txt"), DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dq = DispatcherQueue.GetForCurrentThread();
        _uiThread = Environment.CurrentManagedThreadId;
        _text = new TextBlock { Text = "hotkey spike: waiting for a gesture", FontSize = 20, Margin = new Thickness(16) };
        _win = new Window { Content = _text, Title = "HotkeySpike" };
        _win.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(40, 40, 520, 200));
        _win.Activate();

        _hook = new KeyHook(_dq, OnGesture, Log) {
            AcceptInjected = _acceptInjected,
            AltSpaceByRegisterHotKey = _altSpace == "registerhotkey",
        };
        _hook.Start(_hookOn == "ui");
        Log($"started: hook on {_hookOn} thread, alt+space by {_altSpace}, accept injected {_acceptInjected}");

        var t = _dq.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(100);
        t.IsRepeating = true;
        int ticks = 0;
        t.Tick += (_, _) => { Commands(); if (++ticks % 3 == 0) WriteStatus(); };
        t.Start();
        WriteStatus();
        File.WriteAllText(Path.Combine(_out, "ready.json"), JsonSerializer.Serialize(new {
            pid = Environment.ProcessId, uiThread = _uiThread, hookOn = _hookOn, altSpace = _altSpace, acceptInjected = _acceptInjected,
            registerHotKey = _hook.RegisterHotKeyOk, qpcFrequency = Stopwatch.Frequency,
            lowLevelHooksTimeout = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "LowLevelHooksTimeout", null)?.ToString(),
        }));
    }

    /// On the UI thread: one gesture. uiQpc is taken first, so the latency is the marshalling alone.
    void OnGesture(int ev, long hookQpc, int keyTime, string via)
    {
        long uiQpc = Stopwatch.GetTimestamp();
        _n++;
        bool onUi = Environment.CurrentManagedThreadId == _uiThread;
        try {
            File.AppendAllText(Path.Combine(_out, "events.jsonl"), JsonSerializer.Serialize(new {
                n = _n, ev = KeyHook.Name(ev), via, hookQpc, uiQpc, keyTime, onUiThread = onUi }) + Environment.NewLine);
        } catch { }
        if (_text != null) _text.Text = $"#{_n} {KeyHook.Name(ev)} via {via}";
    }

    void Commands()
    {
        string f = Path.Combine(_out, "cmd.txt");
        if (!File.Exists(f)) return;
        string[] parts;
        try { parts = File.ReadAllText(f).Trim().Split(' '); } catch { return; }
        if (parts.Length < 2 || !int.TryParse(parts[0], out int id) || id <= _lastCmd) return;
        _lastCmd = id;
        int arg = parts.Length > 2 && int.TryParse(parts[2], out int a) ? a : 0;
        Log($"cmd {id}: {parts[1]} {arg}");
        WriteStatus();
        if (parts[1] == "block") { Thread.Sleep(arg); Log($"cmd {id}: UI thread free again"); }
        else if (parts[1] == "stall") _hook!.StallNext(arg);
        else if (parts[1] == "freeze") _hook!.Freeze(arg);
        else if (parts[1] == "drop") _hook!.SimulateDrop();
        else if (parts[1] == "quit") Exit();
        WriteStatus();
    }

    void WriteStatus()
    {
        try {
            string tmp = Path.Combine(_out, "status.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { lastCmd = _lastCmd, handled = _n, hook = _hook?.Status() }));
            File.Move(tmp, Path.Combine(_out, "status.json"), true);
        } catch { }
    }
}
