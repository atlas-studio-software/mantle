using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.BarTasks;

/// <summary>
/// The counts behind "+N" (ui/bar/helmbadge.js paintMore / paintTile; BAR-257, BAR-1030; lane native-r7-strip-3): 30
/// scratch pins (copies of charmap, never launched) cannot fit, so the tail waits behind "+N". This test profile's own
/// helmbadge setting points the LAST pin and the FIRST pin at temp badge files (never Helm's real one): "+N" carries only
/// the hidden app's count, says who needs the user, turns warn on an error, and the panel's tile carries the same disc.
/// </summary>
public partial class StripTests
{
    [TestMethod]
    public void Plus_N_carries_the_hidden_apps_counts_and_its_panel_tile_carries_its_own()
    {
        const string profile = "uitest-strip-ovfbadge";
        _profile = profile;
        string tag = Guid.NewGuid().ToString("N")[..6];
        const int total = 30;
        string exes = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-ovfb-exes-" + tag);
        Directory.CreateDirectory(exes);
        var targets = Enumerable.Range(1, total).Select(i => { string t = Path.Combine(exes, $"ovb{i:00}.exe"); File.Copy(CharmapExe, t, true); return t; }).ToArray();
        var pins = PinFolder("ovfb-" + tag, Enumerable.Range(1, total).Select(i => ($"Ovb {i:00}", targets[i - 1])).ToArray());
        string ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
        string bdir = Path.Combine(Path.GetTempPath(), "mantle-hb-ovf-" + tag);
        Directory.CreateDirectory(bdir);
        string last = Path.Combine(bdir, "last.json"), first = Path.Combine(bdir, "first.json");
        WriteBadge(last, "{\"v\":1,\"waiting\":2}");
        WriteBadge(first, "{\"v\":1,\"waiting\":5}");
        Directory.CreateDirectory(Path.Combine(ud, "features"));
        File.WriteAllText(Path.Combine(ud, "features", "helmbadge.json"), "{\"sources\":[" +
            "{\"exe\":\"ovb30.exe\",\"file\":" + JsonSerializer.Serialize(last) + ",\"label\":\"Last\"}," +
            "{\"exe\":\"ovb01.exe\",\"file\":" + JsonSerializer.Serialize(first) + ",\"label\":\"First\"}]}");
        try {
            using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = pins });
            Func<string> log = () => LogSince(profile, 0);
            AutomationElement? more = null;
            Assert.IsTrue(Host.Until(() => (more = Find(app.Window, "bar.apps.more")) != null && !more.IsOffscreen && (more.Name ?? "").Contains("that do not fit", StringComparison.Ordinal), 20000, 250),
                $"in 20 s no \"+N\" button showed for {total} pins; log: {Tail(log())}");
            int hidden = int.Parse(more!.Name.Split(' ')[0]);
            var cs = Cells(app);
            Assert.AreEqual("Ovb 01", cs[0].Name, "the first pin is not first on the strip");
            Assert.IsTrue(Host.Until(() => cs.Count > 0 && Cells(app)[0].Status.EndsWith("; badge 5", StringComparison.Ordinal), 15000), "the first pin on the strip did not get its own count: " + Describe(Cells(app)));

            // only the hidden app's count rides on "+N" (the strip's own icons show theirs)
            Host.LogContains(log, $"bar tasks: overflow: \"+{hidden}\" carries the count 2", "\"+N\" did not carry the hidden app's count", 15000);
            Assert.IsTrue(Host.Until(() => Find(app.Window, "bar.apps.more")?.Name == $"{hidden} more apps that do not fit. Needs you: Ovb 30 2", 3000),
                $"\"+N\"'s name does not say who needs the user: \"{Find(app.Window, "bar.apps.more")?.Name}\"");
            var disc = Host.Until(() => Find(app.Window, "bar.apps.more.count"), 3000);
            Assert.AreEqual("2", disc?.Name, "\"+N\"'s disc does not show 2");
            var mr = Find(app.Window, "bar.apps.more")!.BoundingRectangle;
            var dr = disc!.BoundingRectangle;
            Assert.IsTrue(dr.Left + dr.Width / 2.0 > mr.Left + mr.Width / 2.0 && dr.Top + dr.Height / 2.0 < mr.Top + mr.Height / 2.0, $"the disc at {dr} is not at \"+N\"'s top-right ({mr})");
            Host.Screenshot("strip-plus-n-count");

            // an error behind "+N": the disc turns warn (same count)
            WriteBadge(last, "{\"v\":1,\"waiting\":1,\"errors\":1}");
            Host.LogContains(log, $"bar tasks: overflow: \"+{hidden}\" carries the count 2 (warn)", "an error behind \"+N\" did not turn its disc warn", 15000);

            // the panel: the hidden app's tile carries its own disc, status and tooltip line
            more = Find(app.Window, "bar.apps.more")!;
            more.Patterns.Invoke.Pattern.Invoke();
            IntPtr h = IntPtr.Zero;
            Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, OvfPanel)), 4000), "the panel did not open");
            var w = Host.Uia.FromHandle(h);
            // Ovb 30 is the last pin; a running-only app the runner has would come after it, so it is found by name
            var tile = Host.Until(() => w.FindAllDescendants(cf => cf.ByName("Ovb 30"))
                .FirstOrDefault(e => (e.Properties.AutomationId.ValueOrDefault ?? "").StartsWith("bar.ovf.", StringComparison.Ordinal)), 3000);
            Assert.IsNotNull(tile, "the panel has no tile for Ovb 30");
            Assert.IsTrue(Host.Until(() => (tile!.Properties.ItemStatus.ValueOrDefault ?? "").EndsWith("; badge 2 warn", StringComparison.Ordinal), 3000), $"the tile's status is \"{tile!.Properties.ItemStatus.ValueOrDefault}\"");
            var td = tile!.FindFirstDescendant(cf => cf.ByAutomationId("bar.ovf.count"));
            Assert.AreEqual("2", td?.Name, "the tile has no disc showing 2");
            var other = Find(w, "bar.ovf." + cs.Count);   // the first tile: not Ovb 30 when two or more wait behind "+N"
            if (other != null && other.Name != "Ovb 30")
                Assert.IsNull(other.FindFirstDescendant(cf => cf.ByAutomationId("bar.ovf.count")), $"{other.Name}'s tile has a disc with no count");
            Host.Screenshot("strip-plus-n-tile-count");

            // the count goes to 0 while the panel is open: the tile's disc and "+N"'s go, and its name loses the line
            WriteBadge(last, "{\"v\":1,\"waiting\":0}");
            Host.LogContains(log, $"bar tasks: overflow: \"+{hidden}\" carries no count", "\"+N\" kept a count that went to 0", 15000);
            Assert.IsTrue(Host.Until(() => tile!.FindFirstDescendant(cf => cf.ByAutomationId("bar.ovf.count")) == null, 3000), "the tile kept its disc");
            Assert.IsTrue(Host.Until(() => Find(app.Window, "bar.apps.more")?.Name == $"{hidden} more apps that do not fit", 3000), "\"+N\"'s name kept the line");
            Assert.IsNull(Find(app.Window, "bar.apps.more.count"), "\"+N\" kept its disc");
        } finally { try { Directory.Delete(bdir, true); } catch (IOException) { } }
    }
}
