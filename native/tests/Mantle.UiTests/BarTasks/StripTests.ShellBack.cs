using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// Explorer coming back, as the bar hears it (BAR-158, 161, 163; lane native-r7-strip-3). The test stands in for
/// Explorer by posting TaskbarCreated to the bar's OWN window only (never a broadcast, which would reach every app on
/// the runner, and Explorer is never restarted). A --dock test run: the dock slot is asked for again through the test's
/// passive helper (nothing is reserved, the work area does not move), the bar is raised, and a repeat inside 3 s is
/// not acted on.
/// </summary>
public partial class StripTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, out RECT r, uint ini);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    static RECT WorkArea() { SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, out var r, 0); return r; }

    [TestMethod]
    public void Explorer_coming_back_asks_for_the_dock_slot_again_and_raises_the_bar_once_per_3_s()
    {
        const string profile = "uitest-strip-shellback";
        _profile = profile;
        using var app = BarApp.Launch(profile, extra: new[] { "--dock" });
        Func<string> log = () => LogSince(profile, 0);
        Host.LogContains(log, "dock (start):", "the docked bar never placed itself", 15000);
        var before = WorkArea();

        // BAR-159 (the bar's part): its window has no owner and no parent but the desktop, so Explorer going away
        // cannot take it down with it (only the desktop layer lives inside Explorer's window; that guard is SAFE-025)
        Assert.AreEqual(IntPtr.Zero, GetWindow(app.Hwnd, 4 /* GW_OWNER */), "the bar window has an owner");
        Assert.AreEqual(GetDesktopWindow(), GetAncestor(app.Hwnd, 1 /* GA_PARENT */), "the bar window is not top-level");

        uint tbc = RegisterWindowMessageW("TaskbarCreated");
        Assert.AreNotEqual(0u, tbc);
        long t0 = Environment.TickCount64;
        Win32.PostMessageW(app.Hwnd, tbc, IntPtr.Zero, IntPtr.Zero);
        Host.LogContains(log, "bar: Explorer is back (TaskbarCreated, 1 this run): asking for the dock slot again (a test run's helper reserves nothing), raising the bar over the new taskbar",
            "the bar did not hear Explorer come back", 5000);
        Host.LogContains(log, "dock (Explorer is back):", "the dock slot was not asked for again", 5000);

        // a repeat inside 3 s (every Mantle window hears the same broadcast) is not acted on
        Win32.PostMessageW(app.Hwnd, tbc, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(600);
        Assert.IsFalse(log().Contains("(TaskbarCreated, 2 this run)", StringComparison.Ordinal), "a repeat inside 3 s was acted on");

        // after 3 s it is Explorer coming back again
        long wait = 3100 - (Environment.TickCount64 - t0);
        if (wait > 0) Thread.Sleep((int)wait);
        Win32.PostMessageW(app.Hwnd, tbc, IntPtr.Zero, IntPtr.Zero);
        Host.LogContains(log, "bar: Explorer is back (TaskbarCreated, 2 this run)", "a TaskbarCreated 3 s later was not acted on", 5000);

        var after = WorkArea();
        Assert.AreEqual(before.Bottom, after.Bottom, $"a test run changed the work area ({before.Bottom} -> {after.Bottom}); it must reserve nothing");
        Assert.IsTrue(Win32.IsWindowVisible(app.Hwnd), "the docked bar hid");
        Assert.AreNotEqual(0L, GetWindowLongPtr(app.Hwnd, -20 /* GWL_EXSTYLE */).ToInt64() & 0x8 /* WS_EX_TOPMOST */, "the bar is not topmost after the raise");
        Assert.IsFalse(log().Contains("bar: Explorer is back: ", StringComparison.Ordinal), "a step failed: " + Tail(log()));
    }
}
