using System.Drawing;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// Drag to reorder (docs/parity/bar.md BAR-239..254; ui/bar/drag.e2e.test.js drives real input the same way): real
/// pointer input through SendInput (a press, moves in steps, the release), so WinUI's own Button and capture are in
/// the way exactly as for the user. The pinned order is saved in this test profile's own features\jumplist.json.
/// </summary>
public partial class StripTests
{
    /// Which fixture pin a cell is: by its name (a pin that is not running) or its exe (a pin named after a window).
    static string Who(Cell c)
    {
        foreach (var n in new[] { "Alpha", "Beta", "Gamma" }) if (c.Name.Contains(n, StringComparison.Ordinal)) return n;
        string exe = "";
        try { exe = (c.E.Properties.HelpText.ValueOrDefault ?? "").ToLowerInvariant(); } catch (Exception) { }
        return exe.StartsWith("powershell", StringComparison.Ordinal) ? "Alpha" : exe.StartsWith("charmap", StringComparison.Ordinal) ? "Beta"
            : exe.StartsWith("regedit", StringComparison.Ordinal) ? "Gamma" : c.Name;
    }

    static string[] PinOrder(List<Cell> cs) => cs.Where(c => c.Pinned).Select(Who).ToArray();

    static List<Cell> WaitPins(BarApp app, string[] want, int ms, string what)
    {
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return PinOrder(cs).SequenceEqual(want); }, ms, 200),
            $"{what}: want pins [{string.Join(", ", want)}], the strip has [{string.Join(", ", PinOrder(cs))}]; cells: {Describe(cs)}");
        return cs;
    }

    /// A real press on <paramref name="from"/>, moves to <paramref name="to"/> (in steps, through a point just past the
    /// lift), an optional pause for <paramref name="whileHeld"/>, then the release where it stands.
    static void DragTo(BarApp app, Point from, Point to, Action? whileHeld = null)
    {
        var wait = TimeSpan.FromMilliseconds(700) - (DateTime.UtcNow - _lastClick);
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        if (!Host.Until(() => PidAt(from) == (uint)app.Proc.Id, 3000, 100)) {
            Win32.TaskbarAutoHide(true);
            if (!Host.Until(() => PidAt(from) == (uint)app.Proc.Id, 3000, 100))
                Assert.Fail($"the cell at {from} is covered by another window ({Win32.Title(GetAncestor(WindowFromPoint(from), 2))}): the drag would not reach the bar");
        }
        MoveTo(from);
        Mouse.Down(MouseButton.Left);
        Thread.Sleep(60);
        if (Math.Abs(to.X - from.X) > 8) MoveTo(new Point(from.X + Math.Sign(to.X - from.X) * 8, from.Y));
        MoveTo(to);
        Thread.Sleep(250);
        whileHeld?.Invoke();
        Mouse.Up(MouseButton.Left);
        _lastClick = DateTime.UtcNow;
        _lastAt = to;
        Thread.Sleep(500);
    }

    static string Overlay(string profile) => Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile, "features", "jumplist.json");

    [TestMethod]
    public void Dragging_a_pin_from_1_to_3_saves_the_order_in_Mantles_own_pin_list_and_a_restart_keeps_it()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        // no Favorites value: the pins start in alphabetical order (lib/pinned.js)
        var pins = PinFolder("drag-" + tag, ("Gamma Reg", RegeditExe), ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        const string profile = "uitest-strip-drag";
        _profile = profile;
        if (File.Exists(Overlay(profile))) File.Delete(Overlay(profile));
        var env = new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins };
        string[] moved = { "Beta", "Gamma", "Alpha" };

        using (var app = BarApp.Launch(profile, env: env)) {
            var cs = WaitPins(app, new[] { "Alpha", "Beta", "Gamma" }, 20000, "before the drag");
            var pinned = cs.Where(c => c.Pinned).ToList();
            long at = LogLength(profile);
            // the first pin onto the third's slot (a little past its centre)
            var end = Centre(pinned[2].Rect);
            DragTo(app, Centre(pinned[0].Rect), new Point(end.X + 6, end.Y));
            Host.LogContains(() => LogSince(profile, at), "bar tasks: drag: lifted a pinned app at 1 of 3", "the press and move never lifted the icon", 4000);
            Host.LogContains(() => LogSince(profile, at), "bar tasks: drag: dropped at 3 of 3", "the release did not drop it on the third slot", 4000);
            Host.LogContains(() => LogSince(profile, at), "bar tasks: order: a pinned app moved 1 -> 3 of 3, saved (in this test profile)", "the host never saved the new order", 6000);
            cs = WaitPins(app, moved, 4000, "after the drop");
            // on screen in that order too, and the drop opened nothing
            CollectionAssert.AreEqual(cs.OrderBy(c => c.Rect.X).Select(c => c.Index).ToArray(), cs.Select(c => c.Index).ToArray(), $"on-screen order differs from bar.app.<n>: {Describe(cs)}");
            string log = LogSince(profile, at);
            Assert.IsFalse(log.Contains("launch dry run", StringComparison.Ordinal) || log.Contains("bar tasks: test: click on", StringComparison.Ordinal),
                $"the drop also clicked the icon: {Tail(log)}");
            // it holds over the next pushes (a rebuild once a second, the hold gone after 4 s): no hop back
            Thread.Sleep(4500);
            cs = Cells(app);
            CollectionAssert.AreEqual(moved, PinOrder(cs), $"the order hopped back after the drop: {Describe(cs)}");
            // saved in Mantle's own pin list (this profile's), never Windows' pins
            string saved = File.Exists(Overlay(profile)) ? File.ReadAllText(Overlay(profile)) : "(no file)";
            StringAssert.Contains(saved, "\"order\"", $"features\\jumplist.json has no order: {saved}");
            Assert.AreEqual(3, Directory.GetFiles(pins, "*.lnk").Length, "the scratch pins folder changed");
            Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
        }

        // a restart reads the saved order
        using var again = BarApp.Launch(profile, env: env);
        WaitPins(again, moved, 20000, "after a restart");
    }

    [TestMethod]
    public void A_4_px_move_opens_the_app_Esc_springs_the_icon_back_and_a_pin_never_passes_the_divider()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        var pins = PinFolder("drag2-" + tag, ("Alpha Shell", PowerShellExe), ("Beta Map", CharmapExe));
        const string profile = "uitest-strip-drag2";
        _profile = profile;
        if (File.Exists(Overlay(profile))) File.Delete(Overlay(profile));
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
        using var run = new Host.Fixture($"Strip drag running {tag}", 520, 60, "pwsh.exe");   // pinned by nobody: after the divider
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => { cs = Cells(app); return PinOrder(cs).SequenceEqual(new[] { "Alpha", "Beta" }) && Named(cs, run.Title) is { Pinned: false, Open: true } && cs.FirstOrDefault(c => !c.Pinned) is { AfterDivider: true, Index: 2 }; }, 20000, 250),
            $"in 20 s the strip did not show both pins and the running-only fixture after the divider; cells: {Describe(cs)}");
        Cell Pin(string who) => cs.First(c => c.Pinned && Who(c) == who);

        // 1. a 4 px wobble is a click: Beta Map (not running) is opened as a dry run, nothing lifts or is saved
        long at = LogLength(profile);
        var b = Centre(Pin("Beta").Rect);
        DragTo(app, b, new Point(b.X + 4, b.Y));
        string log = "";
        Assert.IsTrue(Host.Until(() => { log = LogSince(profile, at); return log.Contains("launch dry run", StringComparison.Ordinal); }, 6000, 200),
            $"a press that moved 4 px did not open the app ({Landed(app, "Beta Map")}); the log since: {Tail(log)}");
        StringAssert.Contains(log, "Beta Map.lnk", $"the 4 px click opened another app: {Tail(log)}");
        Assert.IsFalse(log.Contains("bar tasks: drag: lifted", StringComparison.Ordinal), $"a 4 px move lifted the icon: {Tail(log)}");

        // 2. Esc mid-drag: the icon springs back, nothing saved, nothing opened
        cs = Cells(app);
        at = LogLength(profile);
        DragTo(app, Centre(Pin("Alpha").Rect), Centre(Pin("Beta").Rect), () => {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(150);
            Keyboard.Release(VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
        });
        Host.LogContains(() => LogSince(profile, at), "bar tasks: drag: lifted a pinned app at 1 of 2", "the drag never lifted the icon", 4000);
        Host.LogContains(() => LogSince(profile, at), "bar tasks: drag: cancelled (Esc): the icon went back to 1 of 2, nothing saved", "Esc did not cancel the drag", 4000);
        Thread.Sleep(800);
        log = LogSince(profile, at);
        Assert.IsFalse(log.Contains("bar tasks: order:", StringComparison.Ordinal), $"a cancelled drag saved an order: {Tail(log)}");
        Assert.IsFalse(log.Contains("launch dry run", StringComparison.Ordinal), $"a cancelled drag opened the app: {Tail(log)}");
        cs = WaitPins(app, new[] { "Alpha", "Beta" }, 3000, "after Esc");

        // 3. a pin dragged far past the divider, onto the running-only app and beyond: it stops at the last pin's slot
        at = LogLength(profile);
        var past = Centre(Named(cs, run.Title)!.Rect);
        DragTo(app, Centre(Pin("Alpha").Rect), new Point(past.X + 60, past.Y));
        Host.LogContains(() => LogSince(profile, at), "bar tasks: order: a pinned app moved 1 -> 2 of 2, saved (in this test profile)", "the drag past the divider did not end on the last pin's slot", 6000);
        cs = WaitPins(app, new[] { "Beta", "Alpha" }, 4000, "after the drag past the divider");
        var r = Named(cs, run.Title);
        var firstRun = cs.FirstOrDefault(c => !c.Pinned);
        Assert.IsTrue(r is { Pinned: false } && firstRun is { AfterDivider: true, Index: 2 } && cs.Take(2).All(c => c.Pinned), $"the pins and the running-only apps are no longer split at the divider: {Describe(cs)}");
        Assert.IsFalse(LogSince(profile, at).Contains("launch dry run", StringComparison.Ordinal), "the drop opened the app");
        Assert.IsFalse(app.Proc.HasExited, "the app exited during the test");
    }
}
