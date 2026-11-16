using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// Part 9, what the user sees: a known game starting (before its window exists) takes the bar away and its exit
/// brings it back (TOP-267/269); something moving the bar's window gets it put back (TOP-227/228); Explorer coming
/// back asks for the strip again (TOP-250). The "game" is a copy of ping.exe this test starts and ends itself (by its
/// own handle), counted as a known game through the --test-only MANTLE_TOPBAR_TEST_GAME. Explorer is never restarted:
/// TaskbarCreated is posted to the top bar's own window only. Nothing of the runner's own is moved.
/// </summary>
public partial class TopBarSurfaceTests
{
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] static extern bool MoveWin(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    [TestMethod]
    public void A_known_game_starting_takes_the_bar_away_and_its_exit_brings_it_back()
    {
        FrameProbe.CursorAway();
        const string Game = "mantle-tb-game.exe";
        using var app = Launch("topbar-game", env: p => p.Environment["MANTLE_TOPBAR_TEST_GAME"] = Game);
        IntPtr bar = Up(app);
        Host.LogContains(() => app.Log, "topbar: test run: MANTLE_TOPBAR_TEST_GAME counts " + Game + " as known games running", "the test game was not taken");
        string exe = Path.Combine(app.Ud, Game);
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe, true);
        var game = Host.Track(Process.Start(new ProcessStartInfo(exe, "-n 120 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!);
        try
        {
            // game mode's watch backstop is 12 s at rest (the bar's focus events feed it); a game in front is seen at once
            Host.LogContains(() => app.Log, "topbar: a known game running; the top bar stepped aside until it exits (a --test instance holds no strip, so there was none to let go)",
                "the bar did not step aside for a known game running", 25000);
            Host.LogContains(() => app.Log, "topbar: stepped aside (game started): a known game running: the bar stays aside with its strip let go until it exits");
            Assert.IsTrue(Host.Until(() => !Win32.IsWindowVisible(bar), 3000), "the bar is still visible while a known game runs");
            Assert.AreEqual(0, FrameProbe.WorkArea().Top, "a --test run moved the work area");
            Assert.IsFalse(app.Log.Contains(Game, StringComparison.OrdinalIgnoreCase) && app.Log.Split('\n').Any(l => l.Contains(Game, StringComparison.OrdinalIgnoreCase) && !l.Contains("MANTLE_TOPBAR_TEST_GAME", StringComparison.Ordinal)),
                "the log names the game");
            Host.Screenshot("topbar-game-aside");
        }
        finally { Host.Kill(game); }
        // a game runs: the watch reads every second, so its exit is seen within a couple of seconds
        Host.LogContains(() => app.Log, "topbar: a known game exited after ", "the game's exit was not seen", 10000);
        Host.LogContains(() => app.Log, "topbar: back (game exited): the game exited");
        Assert.IsTrue(Host.Until(() => Win32.IsWindowVisible(bar), 4000), "the bar did not come back after the game exited");
        Host.Screenshot("topbar-game-back");
    }

    [TestMethod]
    public void A_moved_window_is_put_back_and_Explorer_coming_back_asks_for_the_strip_again()
    {
        FrameProbe.CursorAway();
        using var app = Launch("topbar-drift");
        IntPtr bar = Up(app);
        FrameProbe.GetWindowRect(bar, out var home);
        Thread.Sleep(400);
        // something outside Mantle moves it 60 px down and narrower (SWP_NOZORDER | SWP_NOACTIVATE)
        MoveWin(bar, IntPtr.Zero, home.Left, home.Top + 60, (home.Right - home.Left) / 2, home.Bottom - home.Top, 0x0004 | 0x0010);
        Host.LogContains(() => app.Log, "topbar: the window drifted to ", "a moved window was not put back", 4000);
        Assert.IsTrue(Host.Until(() => { FrameProbe.GetWindowRect(bar, out var r); return r.Left == home.Left && r.Top == home.Top && r.Right == home.Right; }, 3000),
            "the window is not back at the top, full width");
        Host.Screenshot("topbar-put-back");

        uint msg = RegisterWindowMessageW("TaskbarCreated");
        Assert.AreNotEqual(0u, msg);
        Assert.IsTrue(PostMessageW(bar, msg, IntPtr.Zero, IntPtr.Zero), "could not post TaskbarCreated to the bar's own window");
        Host.LogContains(() => app.Log, "topbar: Explorer is back (TaskbarCreated)", "the bar did not hear TaskbarCreated", 4000);
        Host.LogContains(() => app.Log, "topbar: app bar Explorer restarted: dry run (--test): would have reserved ", "Explorer coming back did not ask for the strip again", 4000);
        Assert.AreEqual(0, FrameProbe.WorkArea().Top, "a --test run moved the work area");
        Assert.IsTrue(Win32.IsWindowVisible(bar));
    }
}
