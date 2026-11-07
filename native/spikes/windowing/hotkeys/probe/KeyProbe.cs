#nullable enable
// KeyProbe.cs - check.ps1's own helpers for the hotkeys lane (Add-Type; not part of HotkeySpike.exe):
// real keyboard input through SendInput (with the QPC time each key was sent, for latency), and what
// the shell has on screen (Start, Task View, a system menu, the Run dialog).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class KeyProbe
{
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct KI { [FieldOffset(0)] public uint type; [FieldOffset(8)] public ushort vk; [FieldOffset(10)] public ushort scan;
                [FieldOffset(12)] public uint flags; [FieldOffset(16)] public uint time; [FieldOffset(24)] public IntPtr extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, KI[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int n);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    static uint Ext(ushort vk) => vk == 0x5B || vk == 0x5C ? 0x1u : 0u;
    static void Send(ushort vk, bool up) { var a = new KI[1]; a[0].type = 1; a[0].vk = vk; a[0].flags = Ext(vk) | (up ? 0x2u : 0u); SendInput(1, a, 40); }

    /// Key down; returns the QPC time just before it was sent.
    public static long Down(ushort vk) { long t = Stopwatch.GetTimestamp(); Send(vk, false); return t; }
    public static long Up(ushort vk) { long t = Stopwatch.GetTimestamp(); Send(vk, true); return t; }
    public static long Qpc() => Stopwatch.GetTimestamp();
    public static long Freq() => Stopwatch.Frequency;
    public static bool Held(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    static string Title(IntPtr h) { var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }
    static string ProcName(IntPtr h) { GetWindowThreadProcessId(h, out uint pid); try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "pid" + pid; } }
    static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int c, 4) == 0 && c != 0;
    static string Desc(IntPtr h) => Cls(h) + " '" + Title(h) + "' " + ProcName(h) + " " + h.ToInt64();

    public static string Foreground() => Desc(GetForegroundWindow());
    public static bool Focus(long h) => SetForegroundWindow(new IntPtr(h));

    /// Every visible, uncloaked top-level window (what is really on screen).
    public static string[] Shown()
    {
        var o = new List<string>();
        EnumWindows((h, l) => { if (IsWindowVisible(h) && !Cloaked(h)) o.Add(Desc(h)); return true; }, IntPtr.Zero);
        return o.ToArray();
    }

    static string Find(Func<IntPtr, bool> f)
    {
        string found = "";
        EnumWindows((h, l) => { if (IsWindowVisible(h) && !Cloaked(h) && f(h)) { found = Desc(h); return false; } return true; }, IntPtr.Zero);
        return found;
    }
    /// "" = not open. Start: a shown window of StartMenuExperienceHost (Start is always there, cloaked when shut).
    public static string StartOpen()
    {
        string fg = ProcName(GetForegroundWindow());
        if (fg == "StartMenuExperienceHost" || fg == "SearchHost") return "foreground " + Foreground();
        return Find(h => ProcName(h) == "StartMenuExperienceHost");
    }
    /// Task View (Win11: XamlExplorerHostIslandWindow titled Task View; Win10: MultitaskingViewFrame).
    /// The runner showed it as the FOREGROUND window while the cloaked/visible scan missed it, so both are asked.
    static bool IsTaskView(IntPtr h) { string c = Cls(h); return c == "MultitaskingViewFrame" || (c == "XamlExplorerHostIslandWindow" && Title(h).Contains("Task")); }
    public static string TaskViewOpen()
    {
        IntPtr fg = GetForegroundWindow();
        if (IsTaskView(fg)) return "foreground " + Desc(fg);
        return Find(IsTaskView);
    }
    /// A window menu (Alt+Space) or any popup menu: class #32768 shown.
    public static string MenuOpen() => Find(h => Cls(h) == "#32768");
    public static string RunOpen() => Find(h => Cls(h) == "#32770" && Title(h) == "Run");
}
