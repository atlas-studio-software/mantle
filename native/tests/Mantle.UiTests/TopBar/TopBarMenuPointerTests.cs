using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.TopBar;

/// <summary>
/// The menus under a real pointer (SendInput: a press, moves in steps, the release), the leftovers of part 4: a press on
/// File pops its dropdown in once (TOP-080) and lets the pointer go; sliding onto Edit switches the menu; hovering Recent
/// Files opens its submenu after 110 ms (TOP-071); a hover re-render never pops in again; releasing on a row picks it
/// (press-drag-release); a click below the bar closes the menu (TOP-078); Tab puts the focus ring on a title (TOP-028).
/// </summary>
public partial class TopBarSurfaceTests
{
    [StructLayout(LayoutKind.Sequential)] struct MIN { public int dx, dy, data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct PIN { public int type; public MIN mi; }   // 40 bytes on x64, as INPUT
    [DllImport("user32.dll")] static extern uint SendInput(uint n, PIN[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    static void Mouse(int flags, int x = 0, int y = 0)
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        var p = new PIN { type = 0, mi = new MIN { flags = flags } };
        if ((flags & 0x8000) != 0) { p.mi.dx = (int)Math.Round(x * 65535.0 / (w - 1)); p.mi.dy = (int)Math.Round(y * 65535.0 / (h - 1)); }
        SendInput(1, new[] { p }, Marshal.SizeOf<PIN>());
    }
    static (int X, int Y) _at;
    static void MoveTo(int x, int y)
    {
        var (x0, y0) = _at;
        for (int i = 1; i <= 8; i++) { Mouse(0x0001 | 0x8000, x0 + (x - x0) * i / 8, y0 + (y - y0) * i / 8); System.Threading.Thread.Sleep(15); }
        _at = (x, y);
    }
    static (int X, int Y) Mid(FlaUI.Core.AutomationElements.AutomationElement e) { var r = e.BoundingRectangle; return (r.Left + r.Width / 2, r.Top + r.Height / 2); }

    [TestMethod]
    public void Menus_follow_a_real_pointer_press_drag_release_slide_hover_and_click_away()
    {
        using var app = Launch("topbar-pointer");
        IntPtr bar = Up(app);
        Host.LogContains(() => app.Log, "focus: Notepad++, ", "the made-up app's focus never reached the strip", 6000);
        var win = Host.Uia.FromHandle(bar);

        // press on File (and hold): its dropdown pops in once
        var file = Mid(Find(win, "topbar.title.m0"));
        _at = (file.X, file.Y + 40);
        MoveTo(file.X, file.Y);
        Mouse(0x0002);
        Host.LogContains(() => app.Log, "topbar: title pressed: m0 (open; 11 rows)", "a press on File did not open it", 4000);
        Host.LogContains(() => app.Log, "topbar: dropdown pop-in: up 3 px over 240 ms, fade 96 ms", "the dropdown did not pop in", 4000);

        // slide onto Edit and back while held (TOP-031)
        MoveTo(Mid(Find(win, "topbar.title.m1")).X, file.Y);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0.0"))?.Name == "Undo", 3000), "sliding onto Edit did not switch the menu");
        MoveTo(file.X, file.Y);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.0.0"))?.Name == "New", 3000), "sliding back onto File did not switch the menu");

        // hover Recent Files: its submenu after 110 ms (TOP-071)
        var recent = Mid(Find(win, "topbar.menu.0.2"));
        MoveTo(recent.X, recent.Y);
        Host.LogContains(() => app.Log, "topbar: submenu opened: Recent Files (level 1, 3 rows)", "hovering Recent Files did not open its submenu", 4000);
        System.Threading.Thread.Sleep(400);
        int pops = Count(app.Log, "dropdown pop-in:");

        // hover Open… (a re-render that closes the submenu): nothing pops in again; then release on it to pick it
        var open = Mid(Find(win, "topbar.menu.0.1"));
        MoveTo(open.X, open.Y);
        Assert.IsTrue(Host.Until(() => win.FindFirstDescendant(cf => cf.ByAutomationId("topbar.menu.1")) == null, 3000), "hovering Open… did not close the submenu");
        System.Threading.Thread.Sleep(300);
        Assert.AreEqual(pops, Count(app.Log, "dropdown pop-in:"), "a re-render popped the dropdown in again");
        Mouse(0x0004);
        Host.LogContains(() => app.Log, "topbar: drag pick: released on a row", "releasing on Open… did not pick it", 4000);
        Host.LogContains(() => app.Log, "topbar: menu closed (", "picking Open… did not close the menu", 4000);

        // click-away: a real click below the bar, beside the dropdown (TOP-078)
        ZOrder.Click(file.X, file.Y);
        Host.LogContains(() => app.Log, "topbar: title pressed: m0 (open; 11 rows)", "a click on File did not open it", 4000);
        System.Threading.Thread.Sleep(300);
        var dd = Find(win, "topbar.menu.0").BoundingRectangle;
        ZOrder.Click(dd.Right + 200, dd.Top + 20);
        Host.LogContains(() => app.Log, "topbar: menu closed (a click outside the menu", "a click beside the dropdown did not close it", 4000);

        // Tab from the keyboard: the focus ring on a strip button (TOP-028)
        if (Win32.GetForegroundWindow() != bar) Assert.IsTrue(ZOrder.Foreground(bar), "the bar never took the foreground, so it cannot get keys");
        FrameProbe.Tap(0x09);
        Host.LogContains(() => app.Log, "topbar: focus ring: topbar.", "Tab did not put the focus ring on a strip button", 4000);
        Assert.IsFalse(app.Proc.HasExited, "the app exited while its menus were used");
    }
}
