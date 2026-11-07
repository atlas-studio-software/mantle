// TopmostProbe.cs - CI only: which way of opening a pop-up gets it ABOVE the app in front while another
// process holds the foreground (the windowing spike's top risk: SetWindowPos(HWND_TOPMOST) reported
// success and WS_EX_TOPMOST stayed off). Runs only in a --test instance on a CI runner with
// MANTLE_TEST_TOPMOST_PROBE=<dir>. It pre-creates the "parked" window at start, then waits for <dir>\go.txt
// ("x,y": where the test's fixture window sits; the test gives the fixture the foreground before writing
// it) and opens one small window per technique over that spot. <dir>\result.json says what each call
// returned and read back; the test proves the z-order itself from outside (IsAbove, WS_EX_TOPMOST).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Mantle.App.Native;
using Windows.Graphics;

namespace Mantle.App.Shell.Windowing;

internal static class TopmostProbe
{
    static string? _dir;
    static IntPtr _bar;
    static Window? _parked;
    static DispatcherQueueTimer? _poll;
    static readonly List<Window> Keep = new();

    public static void Start(IntPtr barHwnd, DispatcherQueue q)
    {
        string? dir = Host.Env("MANTLE_TEST_TOPMOST_PROBE");
        bool ci = !string.IsNullOrEmpty(Host.Env("CI")) || !string.IsNullOrEmpty(Host.Env("GITHUB_ACTIONS"));
        if (string.IsNullOrEmpty(dir) || !Host.Args.Test || !ci) return;
        _dir = dir; _bar = barHwnd;
        Directory.CreateDirectory(dir);
        // made now, while the process is starting (the launching process may hand it the foreground)
        _parked = Make("parked", aot: true, Colors.Teal);
        _parked.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 60, 60));
        _parked.AppWindow.Show(false);
        WindowKinds.Cloak(Hwnd(_parked), true);
        File.WriteAllText(Path.Combine(dir, "ready.txt"), $"parked {Hwnd(_parked)} topmost {WindowKinds.IsTopmost(Hwnd(_parked))} fg {Win32.GetForegroundWindow()}");
        _poll = q.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(100);
        _poll.Tick += (_, _) => { string go = Path.Combine(_dir!, "go.txt"); if (File.Exists(go)) { _poll!.Stop(); Run(File.ReadAllText(go)); } };
        _poll.Start();
    }

    static IntPtr Hwnd(Window w) => WinRT.Interop.WindowNative.GetWindowHandle(w);

    static Window Make(string name, bool aot, global::Windows.UI.Color c)
    {
        var w = new Window { Title = "Mantle probe " + name, Content = new Grid { Background = new SolidColorBrush(c) } };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = aot;
        p.SetBorderAndTitleBar(false, false);
        w.AppWindow.SetPresenter(p);
        w.AppWindow.IsShownInSwitchers = false;
        IntPtr h = Hwnd(w);
        long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW | WindowKinds.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex));
        Keep.Add(w);
        return w;
    }

    static void Run(string go)
    {
        var parts = go.Trim().Split(',');
        int x0 = int.Parse(parts[0]), y0 = int.Parse(parts[1]);
        var sb = new StringBuilder("{\"fgAtGo\":" + Win32.GetForegroundWindow() + ",\"windows\":[");
        int i = 0;
        void One(string name, Action<Window, IntPtr, RectInt32> open, bool aot, global::Windows.UI.Color c, Window? existing = null)
        {
            var r = new RectInt32(x0 + 10 + (i % 6) * 65, y0 + 40 + (i / 6) * 65, 60, 60);
            i++;
            var w = existing ?? Make(name, aot, c);
            IntPtr h = Hwnd(w), fg0 = Win32.GetForegroundWindow();
            string err = "";
            try { open(w, h, r); } catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
            bool swp = WindowKinds.SetWindowPosE(h, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            int le = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (sb[^1] != '[') sb.Append(',');
            sb.Append($"{{\"name\":\"{name}\",\"hwnd\":{h},\"swp\":{(swp ? "true" : "false")},\"lastError\":{le},\"topmostNow\":{(WindowKinds.IsTopmost(h) ? "true" : "false")},\"fgBefore\":{fg0},\"fgAfter\":{Win32.GetForegroundWindow()},\"error\":\"{err.Replace("\"", "'")}\"}}");
        }
        void Place(Window w, RectInt32 r) => w.AppWindow.MoveAndResize(r);
        // B: presenter always-on-top, shown without activation
        One("show", (w, h, r) => { Place(w, r); w.AppWindow.Show(false); }, true, Colors.Orange);
        // C: no presenter always-on-top, shown without activation
        One("show-noaot", (w, h, r) => { Place(w, r); w.AppWindow.Show(false); }, false, Colors.Gold);
        // D: topmost set while still hidden, then SW_SHOWNA
        One("pre-topmost", (w, h, r) => { Place(w, r); Win32.SetWindowPos(h, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE); Win32.ShowWindow(h, 8 /* SW_SHOWNA */); }, false, Colors.YellowGreen);
        // E: owned by the (topmost) bar, SW_SHOWNA
        One("owned", (w, h, r) => { Place(w, r); WindowKinds.SetOwner(h, _bar); Win32.ShowWindow(h, 8); }, false, Colors.Green);
        // F: owned by the bar, presenter always-on-top, AppWindow.Show(false)
        One("owned-aot", (w, h, r) => { Place(w, r); WindowKinds.SetOwner(h, _bar); w.AppWindow.Show(false); }, true, Colors.Cyan);
        // G: SetWindowPos(TOPMOST, SWP_SHOWWINDOW) as the show itself
        One("swp-show", (w, h, r) => Win32.SetWindowPos(h, Win32.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Win32.SWP_NOACTIVATE | 0x0040 /* SWP_SHOWWINDOW */), false, Colors.Blue);
        // H: parked at start (cloaked off screen), moved in and uncloaked
        One("parked", (w, h, r) => { Win32.SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, Win32.SWP_NOACTIVATE | 0x0004 /* SWP_NOZORDER */); WindowKinds.Cloak(h, false); }, true, Colors.Teal, _parked);
        // I: hidden window re-created by the bar's thread with WS_EX_TOPMOST in the style before the first show
        One("exstyle", (w, h, r) => { Place(w, r); Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE).ToInt64() | 0x8)); Win32.ShowWindow(h, 8); }, false, Colors.Purple);
        // LAST: the spike's way (presenter always-on-top, Activate); Activate may take the foreground, so it
        // runs after every no-activate technique has been read
        One("activate", (w, h, r) => { Place(w, r); w.Activate(); }, true, Colors.Red);
        // WindowKinds.Raise (WIN-011): shown, raised, checked 60 ms later against the app in front
        IntPtr fgAtGo = Win32.GetForegroundWindow();
        var rw = Make("raise", aot: true, Colors.HotPink); IntPtr rh = Hwnd(rw);
        rw.AppWindow.MoveAndResize(new RectInt32(x0 + 10 + (i % 6) * 65, y0 + 40 + (i / 6) * 65, 60, 60));
        rw.AppWindow.Show(false);
        WindowKinds.Raise(rh, rw.DispatcherQueue, "probe raise", fgAtGo, (ok, why) =>
            File.WriteAllText(Path.Combine(_dir!, "raise.txt"), $"{rh} " + (ok ? "ok" : "covered: " + why)));
        sb.Append("]}");
        File.WriteAllText(Path.Combine(_dir!, "result.json"), sb.ToString());
        Host.Log.Line("topmost probe: " + i + " windows opened; result.json written");
    }
}
