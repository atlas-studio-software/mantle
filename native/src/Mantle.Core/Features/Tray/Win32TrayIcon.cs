using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mantle.Core.Features.Tray;

/// <summary>
/// The live tray icon: Shell_NotifyIcon on a hidden window of its own, on a thread of its own with its own
/// message loop (a busy host or UI thread never freezes the icon or its menu). The icon is the code-drawn
/// square (<see cref="TrayReport.IconBitmap"/>); there is no shipped image. A left click opens the same
/// menu as a right click. When Explorer restarts ("TaskbarCreated") the icon is put back by itself.
/// Never construct this in a --test run (a test run uses <see cref="StandInTrayIcon"/>).
/// </summary>
public sealed class Win32TrayIcon : ITrayIcon
{
    const uint WM_NULL = 0, WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_COMMAND = 0x111, WM_CONTEXTMENU = 0x7B, WM_USER = 0x400;
    const uint WM_TRAY = 0x8000 + 0x41, WM_WORK = 0x8000 + 0x42;
    const uint NIN_SELECT = WM_USER, NIN_KEYSELECT = WM_USER + 1;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_INFO = 0x1, NIIF_WARNING = 0x2, NIIF_NOSOUND = 0x10;
    const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 0x2;
    const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80;
    const int FirstId = 1000, SM_CXSMICON = 49;
    const uint IconId = 1;

    static int _classes;

    readonly Action<Action> _post;
    readonly Action<string> _log;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly ConcurrentQueue<Action> _work = new();
    readonly WndProcFn _proc;       // kept alive for as long as the window is
    string _class = "";
    string? _startError;
    IntPtr _hwnd, _icon, _menu;
    uint _taskbarCreated;
    bool _added, _inMenu, _closed, _closeAfterMenu;
    string _tip = "Mantle";
    IReadOnlyList<TrayItem>? _pendingMenu;
    readonly List<TrayItem> _ids = new();   // command id - FirstId -> row (tray thread only)

    public Win32TrayIcon(Action<Action> post, Action<string>? log = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log ?? (_ => { });
        _proc = WndProc;
        _thread = new Thread(Loop) { IsBackground = true, Name = "mantle tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) _startError = "the tray thread did not start within 5 s";
    }

    public Action<TrayItem>? Chosen { get; set; }
    public bool Draws => true;
    /// The hidden window the icon talks to (tests post to it).
    internal IntPtr Window => _hwnd;
    /// What the menu holds now, as drawn (tests read it back).
    internal int MenuHandleItems => _menu == IntPtr.Zero ? -1 : GetMenuItemCount(_menu);
    internal IntPtr MenuHandle => _menu;
    internal bool InMenu => _inMenu;
    /// The command id a row's click sends (tests post it as the menu would).
    internal int CommandIdOf(string id) { var i = _ids.FindIndex(x => x.Id == id); return i < 0 ? -1 : FirstId + i; }
    internal const uint TrayMessage = WM_TRAY;

    public string? Create(string tip)
    {
        if (_startError != null) return _startError;
        string? err = null;
        using var done = new ManualResetEventSlim();
        if (!Do(() => { _tip = tip; err = Add(); done.Set(); })) return "the tray thread is gone";
        return done.Wait(TimeSpan.FromSeconds(5)) ? err : "the tray thread did not answer within 5 s";
    }

    public void SetTip(string tip) => Do(() =>
    {
        _tip = tip;
        if (!_added) return;
        var d = Data(NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_MODIFY, ref d)) _log("tray: the tooltip could not be changed");
    });

    public void SetMenu(IReadOnlyList<TrayItem> items) => Do(() =>
    {
        if (_inMenu) { _pendingMenu = items; return; }   // never swap a menu that is open on screen
        BuildMenu(items);
    });

    public void Balloon(string title, string text, bool warning) => Do(() =>
    {
        if (!_added) { _log($"tray: no icon, so no balloon \"{title}: {text}\""); return; }
        var d = Data(NIF_INFO);
        Put(d.szInfoTitle, title);
        Put(d.szInfo, text);
        d.dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_NOSOUND;
        if (!Shell_NotifyIconW(NIM_MODIFY, ref d)) _log($"tray: the balloon \"{title}\" could not be shown");
    });

    public void PopUp() => Do(() => Show(0, 0));

    bool Do(Action a)
    {
        if (_closed || _hwnd == IntPtr.Zero) return false;
        _work.Enqueue(a);
        return PostMessageW(_hwnd, WM_WORK, IntPtr.Zero, IntPtr.Zero);
    }

    void Loop()
    {
        try
        {
            _class = $"MantleNativeTray.{Environment.ProcessId}.{Interlocked.Increment(ref _classes)}";
            var inst = GetModuleHandleW(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcFn>(_proc),
                hInstance = inst,
                lpszClassName = Marshal.StringToHGlobalUni(_class),
            };
            if (RegisterClassExW(ref wc) == 0) { _startError = $"the tray window class was refused (error {Marshal.GetLastPInvokeError()})"; return; }
            _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, _class, "Mantle tray", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) { _startError = $"the tray window was refused (error {Marshal.GetLastPInvokeError()})"; return; }
        }
        catch (Exception e) { _startError = $"the tray window could not be made ({e.Message})"; return; }
        finally { _ready.Set(); }
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    string? Add()
    {
        if (_icon == IntPtr.Zero)
        {
            _icon = MakeIcon(out var why);
            if (_icon == IntPtr.Zero) return why;
        }
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        if (!Shell_NotifyIconW(NIM_ADD, ref d))
        {
            // already there (Explorer kept it across our restart of the thread): change it instead
            if (!Shell_NotifyIconW(NIM_MODIFY, ref d)) return "Windows refused the notification-area icon (Shell_NotifyIcon add)";
        }
        d.uTimeoutOrVersion = 4;   // NOTIFYICON_VERSION_4: the event in LOWORD(lParam), the anchor in wParam
        Shell_NotifyIconW(NIM_SETVERSION, ref d);
        _added = true;
        return null;
    }

    NOTIFYICONDATAW Data(uint flags)
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = _hwnd, uID = IconId, uFlags = flags,
            uCallbackMessage = WM_TRAY, hIcon = _icon,
        };
        Put(d.szTip, _tip);
        return d;
    }

    static void Put(Span<ushort> dst, string s)
    {
        var n = Math.Min(s.Length, dst.Length - 1);
        for (var i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = 0;
    }

    IntPtr MakeIcon(out string? why)
    {
        why = null;
        var size = GetSystemMetrics(SM_CXSMICON);
        if (size < 16 || size > 256) size = 16;
        var px = TrayReport.IconBitmap(size);
        // the bitmap is premultiplied (as Electron's createFromBitmap wants); an icon's alpha is straight
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a is 0 or 255) continue;
            px[i] = (byte)Math.Min(255, px[i] * 255 / a);
            px[i + 1] = (byte)Math.Min(255, px[i + 1] * 255 / a);
            px[i + 2] = (byte)Math.Min(255, px[i + 2] * 255 / a);
        }
        var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        var color = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) { why = "the tray icon's picture could not be made (CreateDIBSection)"; return IntPtr.Zero; }
        Marshal.Copy(px, 0, bits, px.Length);
        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        if (mask != IntPtr.Zero) DeleteObject(mask);
        if (icon == IntPtr.Zero) why = "the tray icon could not be made from its picture (CreateIconIndirect)";
        return icon;
    }

    void BuildMenu(IReadOnlyList<TrayItem> items)
    {
        if (_menu != IntPtr.Zero) DestroyMenu(_menu);   // destroys the submenus too
        _ids.Clear();
        _menu = Fill(items);
    }

    IntPtr Fill(IReadOnlyList<TrayItem> items)
    {
        var h = CreatePopupMenu();
        foreach (var i in items)
        {
            if (i.Kind == TrayItemKind.Separator) { AppendMenuW(h, MF_SEPARATOR, UIntPtr.Zero, null); continue; }
            var label = i.Label.Replace("&", "&&") + (i.Accelerator != null ? "\t" + i.Accelerator : "");
            var flags = MF_STRING | (i.Enabled ? 0 : MF_GRAYED) | (i.Kind == TrayItemKind.Checkbox && i.Checked ? MF_CHECKED : 0);
            if (i.Kind == TrayItemKind.Submenu && i.Submenu != null)
            {
                AppendMenuW(h, flags | MF_POPUP, (UIntPtr)(ulong)Fill(i.Submenu), label);
                continue;
            }
            _ids.Add(i);
            AppendMenuW(h, flags, (UIntPtr)(uint)(FirstId + _ids.Count - 1), label);
        }
        return h;
    }

    void Show(int x, int y)
    {
        if (_menu == IntPtr.Zero || _inMenu) return;
        if (x == 0 && y == 0 && GetCursorPos(out var p)) { x = p.X; y = p.Y; }
        _inMenu = true;
        try
        {
            SetForegroundWindow(_hwnd);   // without it the menu does not close on a click elsewhere
            TrackPopupMenuEx(_menu, TPM_RIGHTBUTTON, x, y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _inMenu = false;
            if (_closeAfterMenu) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            else if (_pendingMenu != null) { var m = _pendingMenu; _pendingMenu = null; BuildMenu(m); }
        }
    }

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_WORK) { while (_work.TryDequeue(out var a)) a(); return IntPtr.Zero; }
            if (msg == WM_TRAY)
            {
                var ev = (uint)((long)lParam & 0xFFFF);
                if (ev is WM_CONTEXTMENU or NIN_SELECT or NIN_KEYSELECT)
                    Show((short)((long)wParam & 0xFFFF), (short)(((long)wParam >> 16) & 0xFFFF));
                return IntPtr.Zero;
            }
            if (msg == WM_COMMAND)
            {
                var i = (int)((long)wParam & 0xFFFF) - FirstId;
                if (i >= 0 && i < _ids.Count)
                {
                    var item = _ids[i];
                    _post(() => Chosen?.Invoke(item));
                }
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                _added = false;
                var err = Add();
                _log(err == null ? "tray: Explorer restarted; the icon is back" : $"tray: Explorer restarted and the icon could not be put back: {err}");
                return IntPtr.Zero;
            }
            if (msg == WM_CLOSE)
            {
                if (_inMenu) { _closeAfterMenu = true; EndMenu(); return IntPtr.Zero; }   // closed once the menu is off screen
                if (_added) { var d = Data(0); Shell_NotifyIconW(NIM_DELETE, ref d); _added = false; }
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (msg == WM_DESTROY)
            {
                if (_menu != IntPtr.Zero) { DestroyMenu(_menu); _menu = IntPtr.Zero; }
                if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        catch (Exception e) { _log($"tray: a window message ({msg}) threw: {e.Message}"); return IntPtr.Zero; }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(3));
    }

    /// <summary>Put text on the clipboard (Win32, from any thread). Null when it is there, else why not.</summary>
    public static string? CopyText(string text)
    {
        const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 0x2;
        // the clipboard needs an owner window (with none, SetClipboardData fails): a message-only one, for this call
        var owner = CreateWindowExW(0, "STATIC", "Mantle clipboard", 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (owner == IntPtr.Zero) return $"no window to own the clipboard (error {Marshal.GetLastPInvokeError()})";
        var opened = false;
        for (var i = 0; i < 10 && !(opened = OpenClipboard(owner)); i++) Thread.Sleep(20);
        if (!opened) { DestroyWindow(owner); return "another app is holding the clipboard"; }
        try
        {
            if (!EmptyClipboard()) return "the clipboard could not be emptied";
            var bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
            if (h == IntPtr.Zero) return "no memory for the clipboard";
            var p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero) { GlobalFree(h); return "Windows refused the clipboard text"; }
            return null;
        }
        finally { CloseClipboard(); DestroyWindow(owner); }
    }

    /// <summary>Read the clipboard's text (tests).</summary>
    internal static string? ReadText()
    {
        const uint CF_UNICODETEXT = 13;
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    // ---------------------------------------------------------------------------------- Win32
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [InlineArray(128)] struct Chars128 { ushort _c; }
    [InlineArray(256)] struct Chars256 { ushort _c; }
    [InlineArray(64)] struct Chars64 { ushort _c; }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public IntPtr hIcon;
        public Chars128 szTip; public uint dwState; public uint dwStateMask; public Chars256 szInfo; public uint uTimeoutOrVersion;
        public Chars64 szInfoTitle; public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSEXW
    {
        public uint cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance;
        public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public IntPtr lpszMenuName; public IntPtr lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes; public ushort biBitCount; public uint biCompression;
        public uint biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter; public uint biClrUsed; public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon; public uint xHotspot; public uint yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; public uint lPrivate; }

    [DllImport("shell32.dll")] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] static extern bool TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool EndMenu();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr h);
}
