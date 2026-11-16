using System.Diagnostics;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// TOP-221: the bar's FIRST show never takes the keyboard. Lane native-r8-startup's run 43859364384 saw the start-up
/// show activate the bar, so a pop-up the user had opened in Mantle's first second (the downloads shelf, the
/// notification centre) lost the keyboard and closed. Here the run starts hidden by hand (its own profile's
/// features\topbar.json), so the bar's first show comes from Win+Alt+T while the launcher, opened with
/// MANTLE_TEST_LAUNCHER_KEYS=1 and clicked, holds the keyboard. The launcher must keep it and stay open.
/// </summary>
public partial class TopBarSurfaceTests
{
    [TestMethod]
    public void The_first_show_leaves_the_keyboard_with_a_pop_up_that_has_it()
    {
        FrameProbe.CursorAway();
        using var app = Launch("topbar-firstshow", env: p =>
        {
            p.Environment["MANTLE_TEST_LAUNCHER_KEYS"] = "1";
            string ud = p.ArgumentList.First(a => a.StartsWith("--user-data=", StringComparison.Ordinal))["--user-data=".Length..];
            Directory.CreateDirectory(Path.Combine(ud, "features"));
            File.WriteAllText(Path.Combine(ud, "features", "topbar.json"), "{\"hidden\":true}");
        });
        IntPtr bar = Bar(app);
        Host.LogContains(() => app.Log, "topbar: peek: watching the top edge", "the run did not start hidden by hand (features\\topbar.json)", 15000);
        Assert.IsFalse(Win32.IsWindowVisible(bar), "the bar showed although it started hidden by hand");

        Second(app, "light", "--test-surface=show:launcher");
        IntPtr launcher = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() =>
        {
            EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); if (pid == app.Proc.Id && Win32.Title(h) == "Mantle launcher" && Win32.IsWindowVisible(h)) launcher = h; return true; }, IntPtr.Zero);
            return launcher != IntPtr.Zero;
        }, 8000), "the launcher did not show");
        // the runner may refuse SetForegroundWindow; a real click gives it the keyboard
        FrameProbe.GetWindowRect(launcher, out var lr);
        FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point(lr.Left + (lr.Right - lr.Left) / 2, lr.Top + 30));
        Assert.IsTrue(Host.Until(() => Win32.GetForegroundWindow() == launcher, 4000), "the launcher never got the keyboard: " + Win32.Title(Win32.GetForegroundWindow()));

        Second(app, "light", "--test-chord=Win+Alt+T");   // the bar's first show, while the launcher has the keyboard
        Host.LogContains(() => app.Log, "topbar: first show: ", "the bar's first show was not logged", 6000);
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(bar), 4000), "the bar did not show after Win+Alt+T");
        Thread.Sleep(800);
        Host.Screenshot("topbar-first-show-launcher");
        Assert.IsTrue(app.Log.Contains("topbar: first show: shown inactive, the foreground stays where it was", StringComparison.Ordinal), "the first show took the foreground:\n" + Tail(app.Log));
        Assert.IsFalse(app.Log.Contains("topbar: window activated", StringComparison.Ordinal), "the bar was activated by its first show:\n" + Tail(app.Log));
        Assert.AreEqual(launcher, Win32.GetForegroundWindow(), "the launcher lost the keyboard to the bar's first show (foreground: " + Win32.Title(Win32.GetForegroundWindow()) + ")");
        Assert.IsTrue(Win32.IsWindowVisible(launcher), "the launcher closed when the bar first showed");
    }
}
