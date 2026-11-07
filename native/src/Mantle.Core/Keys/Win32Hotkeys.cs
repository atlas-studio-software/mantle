using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Mantle.Core.Keys;

/// <summary>An accelerator in Electron's words ("Ctrl+Alt+Shift+Backspace", "Alt+Space") as RegisterHotKey wants it.</summary>
public readonly record struct Accelerator(uint Modifiers, uint Vk)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;

    static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Return"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B,
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25,
        ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["PrintScreen"] = 0x2C,
    };

    /// Null, with <paramref name="error"/> in words, when the text is not an accelerator this can register.
    public static Accelerator? Parse(string? text, out string? error)
    {
        error = null;
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[^1].Length == 0) { error = $"\"{text}\" names no key"; return null; }
        uint mods = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": case "control": case "commandorcontrol": case "cmdorctrl": mods |= Ctrl; break;
                case "alt": case "option": mods |= Alt; break;
                case "shift": mods |= Shift; break;
                case "win": case "super": case "meta": mods |= Win; break;
                default: error = $"\"{parts[i]}\" in \"{text}\" is not a modifier (Ctrl, Alt, Shift, Win)"; return null;
            }
        }
        var key = parts[^1];
        uint vk;
        if (Named.TryGetValue(key, out var n)) vk = n;
        else if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) vk = char.ToUpperInvariant(key[0]);
        else if (key.Length >= 2 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key.AsSpan(1), out var f) && f >= 1 && f <= 24) vk = (uint)(0x70 + f - 1);
        else { error = $"\"{key}\" in \"{text}\" is not a key this can register"; return null; }
        return new Accelerator(mods, vk);
    }
}

/// <summary>
/// <see cref="IGlobalHotkeys"/> on RegisterHotKey (Electron's globalShortcut does the same). A thread of its
/// own runs its own message loop, so a busy host or UI thread never delays a key, and WM_HOTKEY arrives
/// there with no window at all. The handler is posted to the host through <c>post</c>. Every register and
/// unregister runs on that thread (RegisterHotKey belongs to the thread that called it).
/// </summary>
/// <remarks>
/// Both of Mantle's own shortcuts are expressible here, so no low-level hook is needed for them (the spike's
/// numbers: native/spikes/windowing/hotkeys/README.md, 50 of 50 Alt+Space, median 0.47 ms, no window menu).
/// The Win-key gestures (a solo tap, Win+Tab) stay the sidecar hook's. Never construct this in a --test run:
/// a registered shortcut swallows its keys system-wide (ShellShortcuts.Register refuses to call it then).
/// </remarks>
public sealed class Win32Hotkeys : IGlobalHotkeys, IDisposable
{
    const uint WM_HOTKEY = 0x0312, WM_APP_WORK = 0x8000 + 0x31, WM_QUIT = 0x0012, PM_NOREMOVE = 0;
    const uint MOD_NOREPEAT = 0x4000;
    const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    readonly Action<Action> _post;
    readonly Action<string> _log;
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    readonly ConcurrentQueue<Action> _work = new();
    readonly Dictionary<int, (string Accel, Action Fired)> _keys = new();   // hotkey thread only
    readonly TimeSpan _wait;
    uint _tid;
    int _nextId = 1;
    volatile bool _closed;

    /// <param name="post">How a fired shortcut reaches the host thread (the host's Post).</param>
    public Win32Hotkeys(Action<Action> post, Action<string>? log = null, TimeSpan? wait = null)
    {
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log ?? (_ => { });
        _wait = wait ?? TimeSpan.FromSeconds(2);
        _thread = new Thread(Loop) { IsBackground = true, Name = "mantle hotkeys" };
        _thread.Start();
        if (!_ready.Wait(_wait)) throw new InvalidOperationException("the shortcut thread did not start within 2 s");
    }

    /// The hotkey thread's id (tests post to it).
    internal uint ThreadId => _tid;

    public bool Register(string accelerator, Action fired)
    {
        ArgumentNullException.ThrowIfNull(fired);
        var a = Accelerator.Parse(accelerator, out var error) ?? throw new ArgumentException(error, nameof(accelerator));
        return On(() =>
        {
            var id = _nextId++;
            if (RegisterHotKey(IntPtr.Zero, id, a.Modifiers | MOD_NOREPEAT, a.Vk)) { _keys[id] = (accelerator, fired); return true; }
            var e = Marshal.GetLastPInvokeError();
            if (e == ERROR_HOTKEY_ALREADY_REGISTERED) return false;
            throw new InvalidOperationException($"Windows refused {accelerator} (error {e})");
        });
    }

    public void UnregisterAll()
    {
        if (_closed) return;
        On(() => { foreach (var id in _keys.Keys) UnregisterHotKey(IntPtr.Zero, id); _keys.Clear(); return true; });
    }

    /// The accelerators registered now, in words.
    public IReadOnlyList<string> Registered => On(() => { var l = new List<string>(); foreach (var k in _keys.Values) l.Add(k.Accel); return (IReadOnlyList<string>)l; });

    T On<T>(Func<T> f)
    {
        if (_closed) throw new ObjectDisposedException(nameof(Win32Hotkeys), "the shortcut thread is closed");
        T result = default!;
        Exception? err = null;
        using var done = new ManualResetEventSlim();
        _work.Enqueue(() => { try { result = f(); } catch (Exception e) { err = e; } finally { done.Set(); } });
        if (!PostThreadMessageW(_tid, WM_APP_WORK, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException($"the shortcut thread could not be reached (error {Marshal.GetLastPInvokeError()})");
        if (!done.Wait(_wait)) throw new InvalidOperationException($"the shortcut thread did not answer within {_wait.TotalSeconds:0} s");
        if (err != null) throw err;
        return result;
    }

    void Loop()
    {
        _tid = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // makes the thread's message queue before anyone posts to it
        _ready.Set();
        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == WM_APP_WORK) { while (_work.TryDequeue(out var w)) w(); continue; }
            if (m.message == WM_HOTKEY && _keys.TryGetValue((int)m.wParam, out var k))
            {
                var fired = k.Fired;
                var name = k.Accel;
                _post(() =>
                {
                    try { fired(); }
                    catch (Exception e) { _log($"the {name} shortcut's handler threw: {e.Message}"); }
                });
            }
        }
        foreach (var id in _keys.Keys) UnregisterHotKey(IntPtr.Zero, id);
        _keys.Clear();
        while (_work.TryDequeue(out var w)) w();
    }

    public void Dispose()
    {
        if (_closed) return;
        try { UnregisterAll(); } catch (Exception e) { _log($"the shortcuts were not let go cleanly: {e.Message}"); }
        _closed = true;
        PostThreadMessageW(_tid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(_wait);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX, ptY; public uint lPrivate; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
