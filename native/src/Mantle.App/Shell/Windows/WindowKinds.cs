// WindowKinds.cs - the window primitives every Mantle window kind is built from (spikes/windowing popup 1,
// 5, 7 and bar 3): no frame line (a subclass answering WM_NCCALCSIZE with 0, so the client area IS the
// window, plus a WS_POPUP style with no frame bits), see-through erase, click-through outside the shape
// (SetWindowRgn), parking by DWM cloak, an owner, and the topmost checks.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Mantle.App.Native;

namespace Mantle.App.Shell.Windowing;

internal static class WindowKinds
{
    public const long WS_EX_NOACTIVATE = 0x08000000L, WS_EX_TOPMOST = 0x8L, WS_EX_TRANSPARENT = 0x20L;
    public const long WS_CLIPSIBLINGS = 0x04000000L, WS_CLIPCHILDREN = 0x02000000L;
    const int GWLP_HWNDPARENT = -8;
    const int DWMWA_CLOAK = 13;
    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowPos")] public static extern bool SetWindowPosE(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static bool IsTopmost(IntPtr h) => (Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    public static void Cloak(IntPtr h, bool on) { int v = on ? 1 : 0; Win32.DwmSetWindowAttribute(h, DWMWA_CLOAK, ref v, 4); }

    public static bool IsCloaked(IntPtr h) => Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;

    /// An owned window always sits above its owner, and the owner's topmost band carries it.
    public static void SetOwner(IntPtr h, IntPtr owner) => Win32.SetWindowLongPtr(h, GWLP_HWNDPARENT, owner);

    /// a is above b in the z-order (walk down from a): the same walk as tools/desk Desk.IsAbove.
    public static bool IsAbove(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero || a == b) return false;
        int n = 0;
        for (IntPtr h = Win32.GetWindow(a, 2 /* GW_HWNDNEXT */); h != IntPtr.Zero && n < 4096; h = Win32.GetWindow(h, 2), n++) if (h == b) return true;
        return false;
    }

    // ------------------------------------------------------------------ on top (lib/raise.js, WIN-011)
    /// Every pop-up is put at the very top on EVERY show: HWND_TOPMOST (into the topmost band) then HWND_TOP
    /// (first in it), both without activation. 60 ms later Windows is asked whether it worked: covered ->
    /// raised once more and checked again; still covered -> ONE plain log line per window, never silent.
    /// `over` names the window that should be under it (the app in front when it opened), or zero to
    /// check only that the window is topmost and visible.
    public static void Raise(IntPtr h, Microsoft.UI.Dispatching.DispatcherQueue q, string name, IntPtr over = default, Action<bool, string>? done = null)
    {
        Once(h);
        var t = q.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(60);
        t.IsRepeating = false;
        int tries = 0;
        t.Tick += (_, _) =>
        {
            string why = Covered(h, over);
            if (why.Length > 0 && tries++ == 0) { Once(h); t.Start(); return; }
            if (why.Length > 0 && s_said.Add(h)) Host.Log.Line($"raise: {name} is shown but NOT on top ({why}), after a second raise");
            done?.Invoke(why.Length == 0, why);
        };
        t.Start();
    }
    static readonly HashSet<IntPtr> s_said = new();

    static void Once(IntPtr h)
    {
        const uint f = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE;
        Win32.SetWindowPos(h, Win32.HWND_TOPMOST, 0, 0, 0, 0, f);
        Win32.SetWindowPos(h, HWND_TOP, 0, 0, 0, 0, f);
    }

    /// "" when the window is visible, not cloaked, topmost and (if given) above `over`; else the reason.
    public static string Covered(IntPtr h, IntPtr over)
    {
        if (!Win32.IsWindow(h)) return "the window is gone";
        if (!Win32.IsWindowVisible(h)) return "the window is not visible";
        if (IsCloaked(h)) return "the window is cloaked";
        if (!IsTopmost(h)) return "Windows did not make it topmost";
        if (over != IntPtr.Zero && Win32.IsWindow(over) && !IsAbove(h, over)) return "the app in front is still above it";
        return "";
    }

    // ------------------------------------------------------------------ no frame line
    public sealed class HookState { public bool Erase = true, NcCalc = true; }
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out Win32.RECT r);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref Win32.RECT r, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int i);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    static SubclassProc? s_proc;   // kept alive: Windows calls it
    static readonly Dictionary<IntPtr, HookState> s_state = new();

    /// The client area is the whole window (WM_NCCALCSIZE 0) and the background erases to black, which is
    /// alpha 0 inside the extended DWM frame (see-through). Must be called on the window's own thread.
    public static bool Frameless(IntPtr h, HookState? st = null)
    {
        s_state[h] = st ?? new HookState();
        s_proc ??= Proc;
        bool ok = SetWindowSubclass(h, s_proc, new UIntPtr(0x4D4E), IntPtr.Zero);
        StripFrame(h);
        return ok;
    }

    /// WS_POPUP with no caption / thick frame / border bits; the presenter puts some back at a show, so
    /// call it again after every show.
    public static bool StripFrame(IntPtr h)
    {
        long st = Win32.GetWindowLongPtr(h, Win32.GWL_STYLE).ToInt64();
        long want = (st & Win32.WS_VISIBLE) | Win32.WS_POPUP | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;
        if (st == want) return false;
        Win32.SetWindowLongPtr(h, Win32.GWL_STYLE, new IntPtr(want));
        Win32.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        return true;
    }

    static IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        if (s_state.TryGetValue(h, out var st)) {
            switch (msg) {
                case 0x0014 when st.Erase:   // WM_ERASEBKGND
                    if (GetClientRect(h, out var rc)) FillRect(wp, ref rc, GetStockObject(4 /* BLACK_BRUSH */));
                    return new IntPtr(1);
                case 0x0083 when st.NcCalc:  // WM_NCCALCSIZE: client = window, no frame line at all
                    return IntPtr.Zero;
                case 0x0082:                 // WM_NCDESTROY (comctl32 removes the subclass itself)
                    s_state.Remove(h);
                    break;
            }
        }
        return DefSubclassProc(h, msg, wp, lp);
    }

    /// Clicks and moves outside the shape go to whatever is below, any process (popup spike 5). Window
    /// pixels, relative to the window; re-apply after every resize. +1 px keeps the anti-aliased corner.
    public static void ClipToShape(IntPtr h, int x, int y, int w, int hgt, int radius)
    {
        IntPtr rgn = CreateRoundRectRgn(x, y, x + w + 1, y + hgt + 1, radius * 2, radius * 2);
        SetWindowRgn(h, rgn, true);   // the system owns the region from here
    }
}
