// Desk.cs - the runner desktop's state, for native/tools/ci.ps1 and the bench / parity scripts (loaded with
// Add-Type; default references only). RUNNER ONLY: it minimizes windows, closes folder windows, moves the
// cursor, clicks the desktop and hides the runner's own taskbar. desk.ps1 refuses unless CI / GITHUB_ACTIONS
// is set.
//
// Why it exists: on a runner, a window that asks for attention (FlashWindow, or a SetForegroundWindow the
// system refused) lights its taskbar button, and an auto-hide taskbar then slides up and STAYS up over the
// bottom 48 px until that window is activated. A later stage then clicks the taskbar instead of the bar, or
// captures it. Snapshot() says what the desktop looks like, Reset() puts it back to what every GUI stage
// expects, and the Watch loop logs every attention request and taskbar move with the time, so the stage that
// caused one is named in the CI log.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class Desk
{
    // ------------------------------------------------------------------ Win32
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder b, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr v);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }

    static readonly HashSet<string> ShellClasses = new HashSet<string> {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland" };

    public static bool DpiAware() => SetProcessDpiAwarenessContext(new IntPtr(-4)) != IntPtr.Zero;
    public static int ScreenW => GetSystemMetrics(0);
    public static int ScreenH => GetSystemMetrics(1);

    static string ClassOf(IntPtr h) { var b = new StringBuilder(256); GetClassNameW(h, b, 256); return b.ToString(); }
    static string TitleOf(IntPtr h) { var b = new StringBuilder(256); GetWindowTextW(h, b, 256); return b.ToString(); }
    static uint PidOf(IntPtr h) { GetWindowThreadProcessId(h, out uint p); return p; }
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr p, uint flags, StringBuilder b, ref int n);
    static string ProcName(uint pid)
    {
        IntPtr p = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
        if (p == IntPtr.Zero) return "pid";
        try { var b = new StringBuilder(1024); int n = b.Capacity; return QueryFullProcessImageNameW(p, 0, b, ref n) ? Path.GetFileNameWithoutExtension(b.ToString()) : "pid"; }
        finally { CloseHandle(p); }
    }
    static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

    /// "process 'title' [class]" for one window, for the log.
    public static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(none)";
        uint pid = PidOf(h);
        return ProcName(pid) + " " + pid + " '" + Short(TitleOf(h), 48) + "' [" + ClassOf(h) + "]";
    }

    // ------------------------------------------------------------------ the taskbar
    public static IntPtr Taskbar => FindWindowW("Shell_TrayWnd", null);

    /// The taskbar's rect if it is visible and more than 4 px of it is on screen, else null.
    public static int[] TaskbarShown()
    {
        IntPtr t = Taskbar;
        if (t == IntPtr.Zero || !IsWindowVisible(t) || !GetWindowRect(t, out RECT r)) return null;
        int sh = ScreenH, sw = ScreenW;
        int visH = Math.Min(r.b, sh) - Math.Max(r.t, 0), visW = Math.Min(r.r, sw) - Math.Max(r.l, 0);
        return (visH > 4 && visW > 4) ? new[] { r.l, r.t, r.r, r.b } : null;
    }

    /// ABM_GETSTATE: 1 = auto-hide, 2 = always on top (Windows 11 reports both bits).
    public static int TaskbarState()
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Taskbar };
        return SHAppBarMessage(0x4, ref d).ToInt32();
    }

    public static void TaskbarAutoHide(bool on)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Taskbar, lParam = new IntPtr(on ? 1 : 2) };
        SHAppBarMessage(0xA, ref d);
    }

    /// Hides the taskbar windows outright (they stay hidden until Explorer shows them again).
    public static int HideTaskbar()
    {
        int n = 0;
        foreach (var h in TopWindows(false)) {
            string c = ClassOf(h);
            if ((c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") && IsWindowVisible(h)) { ShowWindow(h, 0); n++; }
        }
        return n;
    }

    /// FLASHW_STOP on every visible top-level window: a lit taskbar button keeps an auto-hide taskbar up.
    public static int StopFlashing()
    {
        int n = 0;
        foreach (var h in TopWindows(true)) {
            var f = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = h, dwFlags = 0 };
            if (FlashWindowEx(ref f)) n++;   // true = it WAS flashing
        }
        return n;
    }

    // ------------------------------------------------------------------ windows
    static List<IntPtr> TopWindows(bool visibleOnly)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => { if (!visibleOnly || IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// An ordinary app window: visible, top-level and unowned, not a tool window, has a title, not the shell's.
    static bool IsAppWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(h, -20).ToInt64();
        if ((ex & 0x80) != 0) return false;   // WS_EX_TOOLWINDOW
        if (TitleOf(h).Length == 0) return false;
        return !ShellClasses.Contains(ClassOf(h));
    }

    /// Every ordinary app window, "process 'title' [class] (min)".
    public static string[] AppWindows()
    {
        var o = new List<string>();
        foreach (var h in TopWindows(true)) if (IsAppWindow(h)) o.Add(Describe(h) + (IsIconic(h) ? " (min)" : ""));
        return o.ToArray();
    }

    /// Minimizes every ordinary app window except those of keepPids, without activating anything. Returns the count.
    public static int MinimizeOthers(int[] keepPids)
    {
        var keep = new HashSet<uint>();
        foreach (var p in keepPids ?? new int[0]) keep.Add((uint)p);
        int n = 0;
        foreach (var h in TopWindows(true)) {
            if (!IsAppWindow(h) || IsIconic(h) || keep.Contains(PidOf(h))) continue;
            if ((GetWindowLongPtr(h, -16).ToInt64() & 0x00020000) == 0) continue;   // no WS_MINIMIZEBOX
            ShowWindowAsync(h, 7 /* SW_SHOWMINNOACTIVE */); n++;
        }
        return n;
    }

    /// WM_CLOSE to every File Explorer folder window (a bench fixture). Returns the count.
    public static int CloseFolderWindows()
    {
        int n = 0;
        foreach (var h in TopWindows(false)) if (ClassOf(h) == "CabinetWClass") { PostMessageW(h, 0x10, IntPtr.Zero, IntPtr.Zero); n++; }
        return n;
    }

    /// Closes (WM_CLOSE, which a property sheet takes as Cancel) every top-level dialog (#32770) whose title is
    /// one of titles: a "System Properties" left open on the runner takes the foreground and the taskbar's attention.
    public static int CloseDialogs(string[] titles)
    {
        int n = 0;
        foreach (var h in TopWindows(false))
            if (ClassOf(h) == "#32770" && Array.IndexOf(titles, TitleOf(h)) >= 0) { PostMessageW(h, 0x10, IntPtr.Zero, IntPtr.Zero); n++; }
        return n;
    }

    /// True when window a is above window b in the z-order (both top-level). Walks down from a.
    public static bool IsAbove(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero || a == b) return false;
        for (IntPtr h = GetWindow(a, 2 /* GW_HWNDNEXT */); h != IntPtr.Zero; h = GetWindow(h, 2)) if (h == b) return true;
        return false;
    }

    /// The first visible top-level window of a process whose title is the given one ("" = any title).
    public static IntPtr WindowOf(int pid, string title)
    {
        foreach (var h in TopWindows(true)) if (PidOf(h) == (uint)pid && (string.IsNullOrEmpty(title) || TitleOf(h) == title)) return h;
        return IntPtr.Zero;
    }

    public static bool IsTopmost(IntPtr h) => (GetWindowLongPtr(h, -20).ToInt64() & 0x8) != 0;

    // ------------------------------------------------------------------ input
    public static uint Click(int x, int y)
    {
        SetCursorPos(x, y);
        int sw = ScreenW, sh = ScreenH;
        int nx = (int)Math.Round(x * 65535.0 / (sw - 1)), ny = (int)Math.Round(y * 65535.0 / (sh - 1));
        var inp = new INPUT[3];
        for (int i = 0; i < 3; i++) { inp[i].type = 0; inp[i].u.mi = new MOUSEINPUT { dx = nx, dy = ny, flags = (i == 0 ? 0x1u : i == 1 ? 0x2u : 0x4u) | 0x8000 }; }
        return SendInput(3, inp, Marshal.SizeOf<INPUT>());
    }
    public static uint Key(ushort vk)
    {
        var inp = new INPUT[2];
        inp[0].type = 1; inp[0].u.ki = new KEYBDINPUT { vk = vk };
        inp[1].type = 1; inp[1].u.ki = new KEYBDINPUT { vk = vk, flags = 0x2 };
        return SendInput(2, inp, Marshal.SizeOf<INPUT>());
    }
    public static void Park(int x, int y) => SetCursorPos(x, y);

    /// The top-level window under a screen point, described.
    public static string At(int x, int y) => Describe(GetAncestor(WindowFromPoint(new POINT { x = x, y = y }), 2 /* GA_ROOT */));
    static bool DesktopAt(int x, int y)
    {
        string c = ClassOf(GetAncestor(WindowFromPoint(new POINT { x = x, y = y }), 2));
        return c == "Progman" || c == "WorkerW";
    }

    // ------------------------------------------------------------------ snapshot and reset
    public static string Snapshot()
    {
        var tb = TaskbarShown();
        IntPtr fg = GetForegroundWindow();
        var apps = AppWindows();
        return "taskbar " + (Taskbar == IntPtr.Zero ? "none" : tb == null ? "hidden" : "SHOWN " + string.Join(",", tb)) +
               " (state " + TaskbarState() + ", window " + (IsWindowVisible(Taskbar) ? "visible" : "hidden") + ")" +
               "; foreground " + Describe(fg) +
               "; " + apps.Length + " app window(s)" + (apps.Length > 0 ? ": " + string.Join("; ", apps) : "");
    }

    /// Puts the desktop back to what a GUI stage expects: taskbar on auto-hide and out of the way, every app
    /// window minimized (except keepPids), the foreground on the desktop, the cursor mid-screen. Returns what it did.
    public static string Reset(int[] keepPids, bool hideIfStuck)
    {
        var did = new List<string>();
        int folders = CloseFolderWindows(); if (folders > 0) did.Add("closed " + folders + " folder window(s)");
        int dialogs = CloseDialogs(new[] { "System Properties" });
        if (dialogs > 0) { did.Add("closed " + dialogs + " System Properties dialog(s)"); Thread.Sleep(300); }
        IntPtr fg = GetForegroundWindow(); string fgc = ClassOf(fg);
        if (fgc == "Windows.UI.Core.CoreWindow" || fgc == "Shell_TrayWnd" || fgc == "XamlExplorerHostIslandWindow") { Key(0x1B); Thread.Sleep(300); did.Add("Esc (a shell surface was in front: " + fgc + ")"); }
        TaskbarAutoHide(true);
        int min = MinimizeOthers(keepPids); if (min > 0) did.Add("minimized " + min);
        Thread.Sleep(400);
        int cx = ScreenW / 2, cy = ScreenH / 3;
        if (DesktopAt(cx, cy)) { Click(cx, cy); did.Add("clicked the desktop"); } else did.Add("no desktop at " + cx + "," + cy + " (" + At(cx, cy) + "): not clicked");
        Park(cx, cy);
        if (WaitHidden(3000)) { did.Add("taskbar hidden"); return string.Join("; ", did); }
        int fl = StopFlashing(); did.Add("stopped flashing on " + fl + " window(s)");
        if (WaitHidden(3000)) { did.Add("taskbar hidden after FLASHW_STOP"); return string.Join("; ", did); }
        if (!hideIfStuck) { did.Add("taskbar STILL SHOWN"); return string.Join("; ", did); }
        int hid = HideTaskbar(); did.Add("taskbar stuck: hid " + hid + " taskbar window(s)");
        did.Add(WaitHidden(1500) ? "taskbar hidden" : "taskbar STILL SHOWN");
        return string.Join("; ", did);
    }

    public static bool WaitHidden(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (TaskbarShown() == null) return true; Thread.Sleep(150); }
        return TaskbarShown() == null;
    }

    // ------------------------------------------------------------------ the watcher
    // A hidden top-level window registered for shell hook messages: HSHELL_FLASH (a window asks for attention)
    // and the activations, plus a 250 ms poll of the taskbar's visibility. One line per event to the log.
    delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSW { public uint style; public WndProc proc; public int cls, wnd; public IntPtr inst, icon, cursor, bg; public string menu, name; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr h; public uint m; public IntPtr w, l; public uint t; public POINT p; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASSW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string s);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr f);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(IntPtr n);

    static WndProc s_proc;   // kept alive: Windows calls it
    static uint s_shellMsg;
    static string s_log;
    static string s_lastTaskbar = "";
    static IntPtr s_lastFg = IntPtr.Zero;

    static void Log(string s)
    {
        string line = DateTime.UtcNow.ToString("HH:mm:ss.fff") + " " + s;
        try { File.AppendAllText(s_log, line + Environment.NewLine); } catch { }
    }

    static void Poll()
    {
        var tb = TaskbarShown();
        string now = tb == null ? "hidden" : "shown " + string.Join(",", tb);
        if (now != s_lastTaskbar) { Log("taskbar " + now + "; foreground " + Describe(GetForegroundWindow())); s_lastTaskbar = now; }
        IntPtr fg = GetForegroundWindow();
        if (fg != s_lastFg) { s_lastFg = fg; Log("foreground " + Describe(fg)); }
    }

    /// Runs until the process is killed.
    public static void Watch(string logPath)
    {
        s_log = logPath;
        s_shellMsg = RegisterWindowMessageW("SHELLHOOK");
        s_proc = (h, m, w, l) => {
            if (m == s_shellMsg) {
                int code = w.ToInt32() & 0x7FFF; bool high = (w.ToInt32() & 0x8000) != 0;
                if (code == 6 && high) Log("FLASH " + Describe(l));                       // HSHELL_FLASH 0x8006
                else if (code == 4) Log((high ? "activated (rude) " : "activated ") + Describe(l)); // HSHELL_WINDOWACTIVATED / RUDEAPPACTIVATED
                else if (code == 1 && TitleOf(l).Length > 0) Log("created " + Describe(l));
            } else if (m == 0x113) Poll();
            return DefWindowProcW(h, m, w, l);
        };
        var wc = new WNDCLASSW { proc = s_proc, inst = GetModuleHandleW(IntPtr.Zero), name = "MantleCiDeskWatch" };
        RegisterClassW(ref wc);
        IntPtr hwnd = CreateWindowExW(0x80 /* TOOLWINDOW */, "MantleCiDeskWatch", "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.inst, IntPtr.Zero);
        Log("watch: window " + (hwnd == IntPtr.Zero ? "FAILED" : "ok") + ", shell hook " + (RegisterShellHookWindow(hwnd) ? "ok" : "FAILED") + "; " + Snapshot());
        SetTimer(hwnd, new UIntPtr(1), 250, IntPtr.Zero);
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
    }
}
