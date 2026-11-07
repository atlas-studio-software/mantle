// WindowList.cs - the running apps, event-driven, in-process.
//
// Same rules as the sidecar's Windows.Build (sidecar/Windows.cs): a visible, unowned, titled,
// non-tool, non-cloaked top-level window that is not the shell's own counts, grouped by exe.
// The sidecar rebuilds on WinEvents plus a 250 ms tick and a 2 s poll, then sends JSON to
// Electron's main, which forwards it to the bar page. Here the WinEvent hook runs on the UI
// thread and a 60 ms debounce rebuilds the list: no timer runs while nothing changes.
// NOT ported (migration work): Store apps by AUMID (Apps.cs), pins, the Mantle-app exceptions,
// window titles in a flyout.
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Native;

internal sealed class AppEntry
{
    public string Key = "";          // the exe path, lower case
    public string Exe = "";
    public string Name = "";
    public string Title = "";        // its front window's title, else its first window's (the UI Automation Name)
    public uint Pid;
    public readonly List<IntPtr> Windows = new();
    public bool Active;              // one of its windows is the foreground window
    public bool AllMinimized;
}

internal sealed class WindowList : IDisposable
{
    static readonly HashSet<string> SkipClasses = new(StringComparer.Ordinal) {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "MSCTFIME UI", "IME",
    };

    readonly DispatcherQueueTimer _debounce;
    readonly Win32.WinEventProc _proc;   // kept alive: the hook calls it from native code
    readonly List<IntPtr> _hooks = new();
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly Dictionary<IntPtr, uint> _pidOf = new();
    readonly Dictionary<uint, string> _exeOfPid = new();
    string _signature = "";

    public List<AppEntry> Apps { get; private set; } = new();
    public event Action? Changed;
    public int Rebuilds { get; private set; }

    public WindowList(DispatcherQueue queue)
    {
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(60);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Rebuild(false);
        _proc = OnEvent;
    }

    public void Start()
    {
        uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_CREATE, Win32.EVENT_OBJECT_HIDE, IntPtr.Zero, _proc, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _proc, 0, 0, flags));
        _hooks.Add(Win32.SetWinEventHook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED, IntPtr.Zero, _proc, 0, 0, flags));
        Rebuild(true);
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;            // OBJID_WINDOW, the window itself
        if (ev != Win32.EVENT_SYSTEM_FOREGROUND && Win32.GetAncestor(hwnd, 2 /* GA_ROOT */) != hwnd) return;
        if (ev == Win32.EVENT_OBJECT_NAMECHANGE && !_pidOf.ContainsKey(hwnd)) return; // a title change on a window we do not list
        _debounce.Stop();
        _debounce.Start();
    }

    public void Rebuild(bool force)
    {
        var byExe = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        var order = new List<AppEntry>();
        var pidOf = new Dictionary<IntPtr, uint>();
        IntPtr fg = Win32.GetForegroundWindow();

        Win32.EnumWindows((hwnd, _) => {
            try {
                if (!Win32.IsWindowVisible(hwnd)) return true;
                if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return true;
                long ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
                if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return true;
                string title = Win32.WindowText(hwnd);
                if (title.Length == 0) return true;
                if (SkipClasses.Contains(Win32.ClassName(hwnd))) return true;
                if (Win32.IsCloaked(hwnd)) return true;
                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == _ownPid) return true;
                if (!_exeOfPid.TryGetValue(pid, out string? exe)) { exe = Win32.ExeOf(pid); _exeOfPid[pid] = exe; }
                if (exe.Length == 0) return true;
                pidOf[hwnd] = pid;
                if (!byExe.TryGetValue(exe, out AppEntry? app)) {
                    app = new AppEntry { Key = exe.ToLowerInvariant(), Exe = exe, Name = Path.GetFileNameWithoutExtension(exe), Pid = pid, AllMinimized = true };
                    byExe[exe] = app; order.Add(app);
                }
                app.Windows.Add(hwnd);
                if (app.Title.Length == 0 || hwnd == fg) app.Title = title;
                if (hwnd == fg) app.Active = true;
                if (!Win32.IsIconic(hwnd)) app.AllMinimized = false;
            } catch (Exception) {
                // one bad window must never truncate the list (the sidecar's rule)
            }
            return true;
        }, IntPtr.Zero);

        // pids that went away drop out of the exe cache
        var live = new HashSet<uint>(pidOf.Values);
        foreach (var k in new List<uint>(_exeOfPid.Keys)) if (!live.Contains(k)) _exeOfPid.Remove(k);

        // the dock keeps first-seen order (Windows' own taskbar does the same)
        var prev = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Apps.Count; i++) prev[Apps[i].Key] = i;
        order.Sort((a, b) => {
            int ia = prev.TryGetValue(a.Key, out int x) ? x : int.MaxValue;
            int ib = prev.TryGetValue(b.Key, out int y) ? y : int.MaxValue;
            return ia.CompareTo(ib);
        });

        var sig = new System.Text.StringBuilder();
        foreach (var a in order) sig.Append(a.Key).Append('|').Append(a.Title).Append('|').Append(a.Windows.Count).Append(a.Active ? "*" : "").Append(a.AllMinimized ? "_" : "").Append(';');
        _pidOf.Clear(); foreach (var kv in pidOf) _pidOf[kv.Key] = kv.Value;
        Rebuilds++;
        if (!force && sig.ToString() == _signature) return;
        _signature = sig.ToString();
        Apps = order;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var h in _hooks) if (h != IntPtr.Zero) Win32.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
