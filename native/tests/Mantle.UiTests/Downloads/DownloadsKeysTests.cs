using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Downloads;

/// <summary>
/// The shelf's keyboard, Esc, one open per double click, and the resting words (BAR-888/889/890/882/897), over a temp
/// folder (MANTLE_DOWNLOADS_DIR), never the user's Downloads. MANTLE_TEST_DL_KEYS=1 lets the test shelf take the
/// keyboard (a CI runner only); every open is a dry run that is logged, not done.
/// </summary>
[TestClass]
public class DownloadsKeysTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why, int ms = 8000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);
    static int Count(string s, string what) { int n = 0, i = 0; while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; } return n; }
    static AutomationElement El(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, $"no {id} in the Downloads shelf");
        return e!;
    }

    static AutomationElement Shelf(BarApp app, string dir)
    {
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Downloads")), 3000), "the shelf did not open");
        return Host.Uia.FromHandle(h);
    }

    /// A second start of the same test profile (the host's test hooks), as RestTests does.
    static void Second(string profile, string arg)
    {
        var psi = new ProcessStartInfo(Host.ExePath) { UseShellExecute = false };
        foreach (var a in new[] { "--test", $"--profile-name={profile}", "--user-data=" + Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile), arg }) psi.ArgumentList.Add(a);
        psi.Environment["MANTLE_TEST_VISIBLE"] = "1";
        using var p = Process.Start(psi)!;
        Assert.IsTrue(p.WaitForExit(15000), "the second start (" + arg + ") did not exit");
    }

    [TestMethod]
    public void Arrows_walk_the_rows_Enter_opens_a_double_click_opens_once_Esc_closes_and_resting_is_said()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mantle-dl-keys-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        foreach (var (n, age) in new[] { ("old.txt", 3), ("mid.txt", 2), ("new.txt", 1) })
        {
            var f = Path.Combine(folder, n);
            File.WriteAllText(f, n);
            File.SetLastWriteTime(f, DateTime.Now.AddMinutes(-age)); File.SetCreationTime(f, DateTime.Now.AddMinutes(-age));
        }
        try
        {
            const string profile = "downloads-keys";
            string dir = FrameProbe.NewDir(profile);
            var env = new Dictionary<string, string> { ["MANTLE_DOWNLOADS_DIR"] = folder, ["MANTLE_TEST_DL_KEYS"] = "1" };
            using var app = BarApp.Launch(profile, "light", env: env, extra: new[] { "--bench-out=" + dir });
            Has(dir, "downloads: 3 items", "the first read did not happen", 15000);
            var btn = app.Find("bar.downloads");
            ClickEl(btn);
            var w = Shelf(app, dir);
            Has(dir, "downloads: the shelf has the keyboard", "the test shelf did not take the keyboard");
            Assert.IsTrue(Host.Until(() => El(w, "dl.row.2").Name.StartsWith("old.txt", StringComparison.Ordinal), 5000), "the rows are not newest first");

            // BAR-888: the first Down is the newest; Down walks; End / Home; clamped, never wraps; the first Up from none is the oldest
            Keyboard.Type(VirtualKeyShort.DOWN);
            Has(dir, "downloads: key Down: row 1 of 3", "the first Down did not reach the newest row");
            Keyboard.Type(VirtualKeyShort.DOWN);
            Has(dir, "downloads: key Down: row 2 of 3", "Down did not walk");
            Keyboard.Type(VirtualKeyShort.END);
            Has(dir, "downloads: key End: row 3 of 3", "End did not reach the last row");
            Keyboard.Type(VirtualKeyShort.DOWN);
            Assert.IsTrue(Host.Until(() => Count(FrameProbe.Log(dir), "downloads: key Down: row 3 of 3") == 1, 3000), "Down on the last row did not stay there");
            Assert.IsTrue(Host.Until(() => El(w, "dl.row.2").Properties.HasKeyboardFocus.ValueOrDefault, 3000), "the last row does not have the focus");
            Keyboard.Type(VirtualKeyShort.HOME);
            Has(dir, "downloads: key Home: row 1 of 3", "Home did not reach the first row");

            // BAR-889: Enter on a row opens it (a dry run here, said in the panel)
            Keyboard.Type(VirtualKeyShort.RETURN);
            Has(dir, "downloads: open was a dry run", "Enter did not open the row as a dry run");
            Thread.Sleep(600);

            // BAR-882: a double click opens once
            int before = Count(FrameProbe.Log(dir), "downloads: open was a dry run");
            var row = El(w, "dl.row.1");
            var r = row.BoundingRectangle;
            Mouse.DoubleClick(new System.Drawing.Point(r.Left + r.Width / 2, r.Top + r.Height / 2));
            Has(dir, "downloads: a second open on the same row was ignored (one decision)", "the double click's second press was not held back");
            Thread.Sleep(600);
            Assert.AreEqual(before + 1, Count(FrameProbe.Log(dir), "downloads: open was a dry run"), "a double click opened more than once");

            // BAR-890: Esc closes the shelf and hands the keyboard back
            Keyboard.Type(VirtualKeyShort.ESCAPE);
            Has(dir, "downloads: the shelf closed", "Esc did not close the shelf");
            Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Downloads")), 3000), "the shelf is still on screen after Esc");

            // BAR-897: resting (game mode's host.rest): the shelf says "Paused while you play"
            Second(profile, "--test-rest=on:uitest-game runs");
            Assert.IsTrue(Host.Until(() => Status(btn) == "resting", 10000), $"the button did not go resting ({Status(btn)})");
            ClickEl(btn);
            w = Shelf(app, dir);
            Assert.IsTrue(Host.Until(() => El(w, "dl.empty.big").Name == "Paused while you play", 5000), "the shelf does not say it is paused");
            Assert.AreEqual("resting", Status(El(w, "dl.empty")));
            Host.Screenshot("downloads-resting-light");
            string log = FrameProbe.Log(dir);
            Assert.IsFalse(log.Contains("old.txt") || log.Contains(folder, StringComparison.OrdinalIgnoreCase), "a file name or the folder reached the log");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
