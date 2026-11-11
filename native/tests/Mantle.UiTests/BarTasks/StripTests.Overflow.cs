using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// The strip's fit and "+N" (ui/bar/bar.page.test.js "fit"; BAR-056, BAR-235..238, the "+N" part of BAR-242): 30 scratch
/// pins (charmap, never launched: a click on a pin is a dry run) cannot fit any bar, so the tail waits behind "+N".
/// </summary>
public partial class StripTests
{
    static AutomationElement? Find(AutomationElement w, string id) => w.FindFirstDescendant(cf => cf.ByAutomationId(id));

    static Point Mid(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    const string OvfPanel = "Mantle pop-up: Apps that do not fit";

    [TestMethod]
    public void Apps_that_do_not_fit_wait_behind_plus_N_and_its_panel_acts_like_the_strip()
    {
        const string profile = "uitest-strip-overflow";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        const int total = 30;
        // one pin per app: 30 pins of the same exe are one app on the strip, so each pin gets its own copy of charmap.exe
        string exes = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-ovf-exes-" + tag);
        Directory.CreateDirectory(exes);
        var targets = Enumerable.Range(1, total).Select(i => { string t = Path.Combine(exes, $"ovf{i:00}.exe"); File.Copy(CharmapExe, t, true); return t; }).ToArray();
        var pins = PinFolder("ovf-" + tag, Enumerable.Range(1, total).Select(i => ($"Ovf {i:00}", targets[i - 1])).ToArray());
        string fx = Path.Combine(Path.GetTempPath(), "mantle-jl-fixture-ovf-" + tag + ".json");
        File.WriteAllText(fx, "{\"apps\":{\"*\":{\"sections\":[],\"none\":\"this app doesn't publish one\"}}}");
        using var app = BarApp.Launch(profile, env: new Dictionary<string, string> {
            ["MANTLE_TEST_PINS_DIR"] = pins, ["MANTLE_TEST_JUMPLIST"] = fx, ["MANTLE_TEST_JL_KEYS"] = "1" });
        Func<string> log = () => LogSince(profile, 0);

        // BAR-235: the "+N" button, the tail of the order behind it, no icon cut off
        AutomationElement? more = null;
        var cs = new List<Cell>();
        Assert.IsTrue(Host.Until(() => {
            cs = Cells(app);
            more = Find(app.Window, "bar.apps.more");
            return cs.Count > 0 && more != null && !more.IsOffscreen && (more.Name ?? "").EndsWith("that do not fit", StringComparison.Ordinal);
        }, 20000, 250), $"in 20 s no \"+N\" button showed for {total} pins; cells: {Describe(cs)}; log: {Tail(log())}");
        Thread.Sleep(500);
        cs = Cells(app);
        more = Find(app.Window, "bar.apps.more")!;
        int hidden = int.Parse(more.Name.Split(' ')[0]);
        int keep = cs.Count, all = keep + hidden;   // the 30 pins, then any running-only app the runner has (after them)
        Assert.IsTrue(all >= total && keep < total, $"\"{more.Name}\" and {keep} icons on the strip: not the {total} pins with the tail behind \"+N\"; cells: {Describe(cs)}");
        Assert.AreEqual(hidden == 1 ? "1 more app that do not fit" : $"{hidden} more apps that do not fit", more.Name);
        CollectionAssert.AreEqual(Enumerable.Range(0, keep).ToArray(), cs.Select(c => c.Index).ToArray(), $"the icons on the strip are not the order's head: {Describe(cs)}");
        CollectionAssert.AreEqual(Enumerable.Range(1, keep).Select(i => $"Ovf {i:00}").ToArray(), cs.Select(c => c.Name).ToArray(), "the strip's order");
        Host.LogContains(log, $"bar tasks: fit: {keep} of {all} on the strip, {hidden} behind \"+{hidden}\"", "the fit was not logged");
        var start = Find(app.Window, "bar.start")!.BoundingRectangle;
        var wall = Find(app.Window, "bar.downloads")!.BoundingRectangle;
        var mr = more.BoundingRectangle;
        double s = cs[0].Rect.Width / 36.0;
        foreach (var c in cs)
            Assert.IsTrue(c.Rect.Left >= start.Right && c.Rect.Right <= wall.Left, $"{c.Name} at {c.Rect} is not inside the free span ({start.Right}..{wall.Left})");
        Assert.IsTrue(mr.Left > cs.Last().Rect.Right && mr.Right <= wall.Left - (int)(8 * s), $"\"+N\" at {mr} is not after the last icon ({cs.Last().Rect}) and inside the span (wall {wall.Left})");
        // the collapsed tail takes no room: "+N" sits one gap (6 DIP) after the last icon on the strip
        Assert.IsTrue(Math.Abs(mr.Left - cs.Last().Rect.Right - 6 * s) <= 2, $"\"+N\" is {mr.Left - cs.Last().Rect.Right} px after the last icon, not 6 DIP ({6 * s:0.#} px)");
        // filled from the span's left edge: the first icon 8 DIP after the Start button
        Assert.IsTrue(Math.Abs(cs[0].Rect.Left - start.Right - 8 * s) <= 2, $"the first icon is {cs[0].Rect.Left - start.Right} px after Start, not 8 DIP");

        // BAR-242 ("+N" part): a drag never reaches the tail; its slots are only the icons on the strip
        var last = cs.Last();
        DragTo(app, Mid(last.Rect), new Point(mr.Right + 150, Mid(last.Rect).Y));
        Host.LogContains(log, $"bar tasks: drag: lifted a pinned app at {keep} of {keep}", "the drag's group counted the apps behind \"+N\"");

        // BAR-236: the panel lists exactly the hidden apps, in order, up to 4 across
        Invoke(Find(app.Window, "bar.apps.more")!);
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, OvfPanel)), 4000), $"the panel did not open; log: {Tail(log())}");
        var pw = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => Find(pw, "bar.ovf"), 3000), "the panel has no bar.ovf");
        var tiles = Host.Until(() => { var t = pw.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)).Where(e => (e.AutomationId ?? "").StartsWith("bar.ovf.", StringComparison.Ordinal)).ToList(); return t.Count == hidden ? t : null; }, 3000);
        Assert.IsNotNull(tiles, $"the panel does not hold {hidden} tiles");
        CollectionAssert.AreEqual(Enumerable.Range(keep, hidden).Select(i => "bar.ovf." + i).ToArray(), tiles!.Select(t => t.AutomationId).ToArray(), "the tiles are not the hidden apps in order");
        CollectionAssert.AreEqual(Enumerable.Range(keep + 1, total - keep).Select(i => $"Ovf {i:00}").ToArray(), tiles.Take(total - keep).Select(t => t.Name).ToArray(), "every tile names its app, in order");
        Assert.IsTrue(tiles.All(t => !string.IsNullOrEmpty(t.Name)), "a tile has no name");
        int cols = Math.Min(4, hidden);
        var tr = tiles.Select(t => t.BoundingRectangle).ToList();
        Assert.AreEqual(cols, tr.Select(r => r.X).Distinct().Count(), $"the panel is not {cols} column(s) wide: {string.Join(" ", tr)}");
        Assert.IsTrue(Math.Abs(tr[0].Width - 76 * s) <= 2, $"a tile is {tr[0].Width} px wide, not 76 DIP");

        // BAR-238: Esc closes it (the panel has the keyboard with MANTLE_TEST_JL_KEYS=1)
        Host.LogContains(log, "bar tasks: overflow: the panel has the keyboard", "the panel never took the keyboard");
        Keyboard.Type(VirtualKeyShort.ESCAPE);
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, OvfPanel)), 3000), "Esc did not close the panel");

        // BAR-237: a tile acts like its icon: a click opens (a dry run here), middle-click asks for another, right-click the menu
        string lastName = $"Ovf {total:00}";
        AutomationElement Tile(string id)
        {
            var w0 = FrameProbe.Find(app.Proc.Id, OvfPanel);
            if (!FrameProbe.Showing(w0)) { Invoke(Find(app.Window, "bar.apps.more")!); Assert.IsTrue(Host.Until(() => FrameProbe.Showing(w0 = FrameProbe.Find(app.Proc.Id, OvfPanel)), 4000), "the panel did not reopen"); }
            var e = Host.Until(() => Find(Host.Uia.FromHandle(w0), id), 3000);
            Assert.IsNotNull(e, "the panel has no " + id);
            return e!;
        }
        long at = LogLength(profile);
        Invoke(Tile("bar.ovf." + (total - 1)));
        Assert.IsTrue(Host.Until(() => LogSince(profile, at).Contains("launch dry run", StringComparison.Ordinal), 6000, 200), $"a tile's click did not open its app; log: {Tail(LogSince(profile, at))}");
        StringAssert.Contains(LogSince(profile, at), lastName + ".lnk", "the tile opened another app");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, OvfPanel)), 3000), "the panel stayed open after its app opened");

        at = LogLength(profile);
        var tile = Tile("bar.ovf." + (total - 1));
        Thread.Sleep(300);
        Mouse.Click(Mid(tile.BoundingRectangle), MouseButton.Middle);
        Assert.IsTrue(Host.Until(() => LogSince(profile, at).Contains("middle-click on bar.ovf." + (total - 1), StringComparison.Ordinal) && LogSince(profile, at).Contains("launch dry run", StringComparison.Ordinal), 6000, 200),
            $"a tile's middle-click did not ask for its app; log: {Tail(LogSince(profile, at))}");

        tile = Tile("bar.ovf." + (total - 1));
        Thread.Sleep(300);
        RightClick(tile);
        Menu(app, lastName, profile);
        Assert.IsFalse(FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, OvfPanel)), "the panel stayed open under the app menu");
    }
}
