using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Mantle.Core.Features.Tray;
using Xunit;

namespace Mantle.Core.Tests
{

/// <summary>A test that puts something real on the desktop (a tray icon, a global shortcut, the clipboard): it runs
/// only on a CI runner (CI / GITHUB_ACTIONS set), and is reported as skipped, with why, anywhere else.</summary>
public sealed class CiFactAttribute : FactAttribute
{
    public static bool OnRunner =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

    public CiFactAttribute()
    {
        if (!OnRunner) Skip = "runs only on a CI runner (CI / GITHUB_ACTIONS set): it puts a real icon, shortcut or clipboard text on this desktop";
    }
}

internal static class Wait
{
    public static bool Until(Func<bool> f, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(20); }
        return f();
    }
}

}

namespace Mantle.Core.Tests.Features
{
    public class Win32TrayIconTests
    {
        const uint WM_COMMAND = 0x111, WM_CANCELMODE = 0x1F, NIN_SELECT = 0x400;

        [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? name);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        static IReadOnlyList<TrayItem> Menu() => TrayModel.Template(new TrayInputs(true, false, Array.Empty<TrayProblem>(), null, "ok", false, true, true, true,
            new[] { new TrayItem(TrayItemKind.Checkbox, "Volume pop-up", "feature:osd", true, true) }, null));

        static bool OurMenuIsOpen()
        {
            var h = FindWindowW("#32768", null);
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out var pid);
            return pid == (uint)Environment.ProcessId;
        }

        [CiFact]
        public void TheIconGoesUp_AClickOpensItsMenu_AChosenRowReachesTheHost_AndItGoesAway()
        {
            var posted = new BlockingCollection<Action>();
            var log = new List<string>();
            var icon = new Win32TrayIcon(posted.Add, l => { lock (log) log.Add(l); });
            var chosen = new List<string>();
            icon.Chosen = i => chosen.Add(i.Id ?? "");
            try
            {
                Assert.Null(icon.Create("Mantle (test run)\nRunning normally"));
                Assert.True(IsWindow(icon.Window));
                var items = Menu();
                icon.SetMenu(items);
                Assert.True(Wait.Until(() => icon.MenuHandleItems == items.Count), $"the menu has {icon.MenuHandleItems} rows, wanted {items.Count}");
                icon.SetTip("Mantle (test run)\n1 thing needs a look: Glass effect");
                icon.Balloon("Mantle test", "a balloon from a CI runner", false);

                // a left click (NIN_SELECT, version 4: the event in LOWORD(lParam), the icon id in HIWORD) opens the menu
                Assert.True(PostMessageW(icon.Window, Win32TrayIcon.TrayMessage, (IntPtr)((200 << 16) | 200), (IntPtr)(NIN_SELECT | (1 << 16))));
                Assert.True(Wait.Until(() => icon.InMenu && OurMenuIsOpen()), "the menu did not open on a left click");
                PostMessageW(icon.Window, WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                Assert.True(Wait.Until(() => !icon.InMenu), "the menu did not close");

                // a click on a row: the menu sends WM_COMMAND with the row's id; the row reaches the host through post
                var quit = icon.CommandIdOf("quit");
                Assert.True(quit > 0);
                PostMessageW(icon.Window, WM_COMMAND, (IntPtr)quit, IntPtr.Zero);
                Assert.True(posted.TryTake(out var run, 3000), "the chosen row was never posted to the host");
                run();
                Assert.Equal(new[] { "quit" }, chosen);
                var feature = icon.CommandIdOf("feature:osd");
                Assert.True(feature > quit || feature > 0);
                PostMessageW(icon.Window, WM_COMMAND, (IntPtr)feature, IntPtr.Zero);
                Assert.True(posted.TryTake(out run, 3000));
                run();
                Assert.Equal("feature:osd", chosen[^1]);
            }
            finally { icon.Dispose(); }
            Assert.True(Wait.Until(() => !IsWindow(icon.Window)), "the tray window outlived Dispose");
            lock (log) Assert.DoesNotContain(log, l => l.Contains("threw", StringComparison.Ordinal));
        }

        [CiFact]
        public void TheClipboard_TakesTheReportText()
        {
            var text = $"Mantle diagnostic report\nCI {Guid.NewGuid():N}\n";
            Assert.Null(Win32TrayIcon.CopyText(text));
            Assert.Equal(text, Win32TrayIcon.ReadText());
        }
    }
}
