using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mantle.App.Surfaces.Desktop;

/// <summary>
/// What the desktop layer cannot hear itself once it is a child of Explorer's desktop window: Windows sends
/// WM_DISPLAYCHANGE and Explorer broadcasts TaskbarCreated to TOP-LEVEL windows only. So a hidden top-level window
/// (never shown, no taskbar button, its own thread and message loop, like the tray icon's) listens for both and hands
/// them to the layer: a resolution or scale change (DESK-016, lib/display-fit.js) and Explorer coming back after a
/// restart (DESK-012, lib/foreign-parent.js hookShellRestart). Nothing here changes anything in Windows.
/// </summary>
/// <remarks>
/// A test (on a CI runner) stands in for both by posting the same messages to THIS window only (found by its title in
/// Mantle's own process): a TaskbarCreated broadcast would reach every app on the machine, and Explorer is never
/// restarted by a test.
/// </remarks>
internal sealed class ShellWatch : IDisposable
{
    public const string Title = "Mantle shell watch";
    const uint WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A, WM_CLOSE = 0x0010, WM_QUIT = 0x0012;
    const uint SPI_SETWORKAREA = 0x002F;
    const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x00000080;
    static int _classes;

    readonly Action<string> _display, _shellBack, _log;
    readonly WndProcFn _proc;   // kept alive for as long as the window is
    Thread? _thread;
    IntPtr _hwnd;
    uint _taskbarCreated;
    volatile bool _closed;

    public ShellWatch(Action<string> display, Action<string> shellBack, Action<string> log)
    {
        _display = display; _shellBack = shellBack; _log = log; _proc = WndProc;
    }

    /// Make the window on its own thread; null when it is up, else why not (in words).
    public string? Start()
    {
        string? err = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready, e => err = e)) { IsBackground = true, Name = "shell watch" };
        _thread.Start();
        if (!ready.Wait(3000)) return "the shell watch window was not made in 3 s";
        return err;
    }

    void Loop(ManualResetEventSlim ready, Action<string> fail)
    {
        try {
            string cls = $"MantleNativeShellWatch.{Environment.ProcessId}.{Interlocked.Increment(ref _classes)}";
            var inst = GetModuleHandleW(null);
            var wc = new WNDCLASSEXW {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcFn>(_proc),
                hInstance = inst, lpszClassName = Marshal.StringToHGlobalUni(cls),
            };
            if (RegisterClassExW(ref wc) == 0) { fail($"its window class was refused (error {Marshal.GetLastPInvokeError()})"); return; }
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, cls, Title, WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { fail($"its window was refused (error {Marshal.GetLastPInvokeError()})"); return; }
        } catch (Exception e) { fail($"its window could not be made ({e.Message})"); return; }
        finally { ready.Set(); }
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
    }

    IntPtr WndProc(IntPtr h, uint msg, IntPtr wp, IntPtr lp)
    {
        try {
            if (msg == _taskbarCreated && _taskbarCreated != 0) { _shellBack("TaskbarCreated"); return IntPtr.Zero; }
            switch (msg) {
                case WM_DISPLAYCHANGE: { long v = lp.ToInt64(); _display($"WM_DISPLAYCHANGE {v & 0xFFFF}x{(v >> 16) & 0xFFFF}"); break; }
                case WM_DPICHANGED: _display($"WM_DPICHANGED {wp.ToInt64() & 0xFFFF}"); break;
                case WM_SETTINGCHANGE: if ((uint)wp.ToInt64() == SPI_SETWORKAREA) _display("the work area changed"); break;
                case WM_CLOSE: if (!_closed) return IntPtr.Zero; DestroyWindow(h); return IntPtr.Zero;   // only Dispose closes it
                case 0x0002 /* WM_DESTROY */: PostQuitMessage(0); return IntPtr.Zero;
            }
        } catch (Exception e) { _log("shell watch: a message could not be handled: " + e.Message); }
        return DefWindowProcW(h, msg, wp, lp);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
}
