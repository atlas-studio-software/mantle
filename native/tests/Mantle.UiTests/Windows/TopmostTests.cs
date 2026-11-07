using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// Z-order proofs from OUTSIDE the app (the same walk as tools/desk Desk.IsAbove, WS_EX_TOPMOST read back).
internal static class ZOrder
{
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public U u; }

    public static bool IsAbove(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero || a == b) return false;
        for (IntPtr h = GetWindow(a, 2 /* GW_HWNDNEXT */); h != IntPtr.Zero; h = GetWindow(h, 2)) if (h == b) return true;
        return false;
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);

    /// A real click (injected input) at a screen point: the user working in another app, as the spike's
    /// popup check did before a popup came up NOT topmost (runs 4-5).
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var inp = new INPUT[2];
        inp[0].type = 0; inp[0].u.mi = new MOUSEINPUT { flags = 0x0002 /* LEFTDOWN */ };
        inp[1].type = 0; inp[1].u.mi = new MOUSEINPUT { flags = 0x0004 /* LEFTUP */ };
        SendInput(2, inp, Marshal.SizeOf<INPUT>());
    }

    public static bool IsTopmost(IntPtr h) => (GetWindowLongPtr(h, -20).ToInt64() & 0x8) != 0;

    /// Give `h` the foreground from the test process: an injected Alt tap makes this process the last
    /// input's owner, which lifts Windows' foreground lock for one SetForegroundWindow.
    public static bool Foreground(IntPtr h)
    {
        for (int i = 0; i < 5; i++)
        {
            var inp = new INPUT[2];
            inp[0].type = 1; inp[0].u.ki = new KEYBDINPUT { vk = 0x12 };
            inp[1].type = 1; inp[1].u.ki = new KEYBDINPUT { vk = 0x12, flags = 0x2 };
            SendInput(2, inp, Marshal.SizeOf<INPUT>());
            SetForegroundWindow(h);
            if (Host.Until(() => Win32.GetForegroundWindow() == h, 1000)) return true;
        }
        return false;
    }
}

[TestClass]
public sealed class TopmostTests
{
    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    /// The windowing spike's top risk: a window opened while ANOTHER process holds the foreground. The app
    /// opens one small window per technique (Shell/Windows/TopmostProbe.cs) over a fixture that has the
    /// foreground, and the test reads from outside which ones really sit above it.
    [TestMethod]
    public void Popup_opened_while_another_app_has_the_foreground_sits_above_it() => Probe(cold: false);

    /// The same, with Mantle STARTED while the other app is in front (a start at sign-in, or after a crash):
    /// a new process the foreground app did not start has no foreground rights, the spike's failing case.
    [TestMethod]
    public void Bar_and_popups_started_while_another_app_is_in_front_sit_above_it() => Probe(cold: true);

    static void Probe(bool cold)
    {
        Host.Guard();
        string tag = cold ? "cold" : "warm";
        string dir = Path.Combine(Path.GetTempPath(), "mantle-topmost-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var env = new Dictionary<string, string> { ["MANTLE_TEST_TOPMOST_PROBE"] = dir };
        BarApp? app = cold ? null : BarApp.Launch("topmost-" + tag, env: env);
        if (!cold) Assert.IsTrue(Host.Until(() => File.Exists(Path.Combine(dir, "ready.txt")), 15000), "the app never started its topmost probe");
        using var fx = new Host.Fixture("Mantle topmost fixture " + tag, 600, 300);
        Assert.IsTrue(ZOrder.Foreground(fx.Hwnd), "the fixture could not take the foreground, so the probe would prove nothing");
        // then the user clicks in it (a timer or notification pop-up comes up while they work elsewhere)
        ZOrder.Click(600 + 210, 300 + 160);
        Thread.Sleep(300);
        Assert.AreEqual(fx.Hwnd, Win32.GetForegroundWindow(), "the click did not leave the fixture in front");
        if (cold) { File.WriteAllText(Path.Combine(dir, "go.txt"), "600,300"); app = BarApp.Launch("topmost-" + tag, env: env); }
        using var _ = app!;
        bool barTop = ZOrder.IsTopmost(app!.Hwnd), barAbove = ZOrder.IsAbove(app.Hwnd, fx.Hwnd);
        if (!cold) File.WriteAllText(Path.Combine(dir, "go.txt"), "600,300");
        Assert.IsTrue(Host.Until(() => File.Exists(Path.Combine(dir, "result.json")), 20000), "the probe never wrote result.json");
        Thread.Sleep(800);   // let the shows land, then read the z-order from outside
        string ready = File.Exists(Path.Combine(dir, "ready.txt")) ? File.ReadAllText(Path.Combine(dir, "ready.txt")) : "?";
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "result.json")));
        var lines = new List<string> { $"[{tag}] ready: " + ready, $"[{tag}] bar: topmost {barTop}, above the fixture {barAbove}; now topmost {ZOrder.IsTopmost(app.Hwnd)}, above {ZOrder.IsAbove(app.Hwnd, fx.Hwnd)}", $"[{tag}] fixture {fx.Hwnd}, foreground now {Win32.GetForegroundWindow()}" };
        var verdict = new Dictionary<string, bool>();
        foreach (var w in doc.RootElement.GetProperty("windows").EnumerateArray())
        {
            string name = w.GetProperty("name").GetString()!;
            var h = new IntPtr(w.GetProperty("hwnd").GetInt64());
            bool top = ZOrder.IsTopmost(h), above = ZOrder.IsAbove(h, fx.Hwnd), vis = Win32.IsWindowVisible(h);
            verdict[name] = top && above && vis;
            lines.Add($"TOPMOST-PROBE [{tag}] {name}: topmost {top}, above the fixture {above}, visible {vis}; in-app: {w}");
        }
        Assert.IsTrue(Host.Until(() => File.Exists(Path.Combine(dir, "raise.txt")), 5000), "WindowKinds.Raise never reported");
        var raise = File.ReadAllText(Path.Combine(dir, "raise.txt")).Split(' ', 2);
        var rh = new IntPtr(long.Parse(raise[0]));
        lines.Add($"RAISE [{tag}] (WindowKinds.Raise, WIN-011): in-app '{raise[1]}'; from outside topmost {ZOrder.IsTopmost(rh)}, above the fixture {ZOrder.IsAbove(rh, fx.Hwnd)}");
        Host.Screenshot("topmost-probe-" + tag);
        File.WriteAllLines(Path.Combine(Host.OutDir, $"topmost-probe-{tag}.txt"), lines);
        foreach (var l in lines) Console.WriteLine(l);
        Assert.AreEqual(fx.Hwnd, Win32.GetForegroundWindow(), "a probe window took the foreground from the app in front");
        Assert.IsTrue(ZOrder.IsTopmost(app.Hwnd) && ZOrder.IsAbove(app.Hwnd, fx.Hwnd), "the bar is not above the app in front:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
        Assert.AreEqual("ok", raise[1], "WindowKinds.Raise said the pop-up is covered:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
        Assert.IsTrue(ZOrder.IsTopmost(rh) && ZOrder.IsAbove(rh, fx.Hwnd), "WindowKinds.Raise said ok, but from outside the pop-up is not above the app in front");
    }
}
