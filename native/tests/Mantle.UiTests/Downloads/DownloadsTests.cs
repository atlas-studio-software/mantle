using FlaUI.Core.AutomationElements;
using Mantle.UiTests.BarFrame;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Downloads;

/// <summary>
/// The Downloads button and its shelf (Surfaces/Bar/Downloads, ui/bar/downloads.js) over the real [Feature("downloads")].
/// MANTLE_DOWNLOADS_DIR points it at a temp folder (a test never reads the real Downloads folder); open / Show in folder /
/// the folder button are DRY RUNS in --test, so nothing opens on the runner.
/// </summary>
[TestClass]
public class DownloadsTests
{
    static void ClickEl(AutomationElement e) { var r = e.BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(350); }
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static string Help(AutomationElement e) => e.Properties.HelpText.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why, int ms = 8000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);
    static AutomationElement El(AutomationElement w, string id, int ms = 3000)
    {
        var e = Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId(id)), ms);
        Assert.IsNotNull(e, $"no {id} in the Downloads shelf");
        return e!;
    }

    static (BarApp App, string Dir) Launch(string profile, string folder)
    {
        string dir = FrameProbe.NewDir(profile);
        var env = new Dictionary<string, string> { ["MANTLE_DOWNLOADS_DIR"] = folder };
        return (BarApp.Launch(profile, "light", env: env, extra: new[] { "--bench-out=" + dir }), dir);
    }

    static AutomationElement Shelf(BarApp app, string dir)
    {
        IntPtr h = IntPtr.Zero;
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(h = FrameProbe.Find(app.Proc.Id, "Mantle pop-up: Downloads")), 3000), "the shelf did not open. Log:\n" + FrameProbe.Log(dir));
        Has(dir, "downloads: the shelf opened", "no opened line");
        var w = Host.Uia.FromHandle(h);
        Assert.IsNotNull(Host.Until(() => w.FindFirstDescendant(cf => cf.ByAutomationId("dl.panel")), 3000), "the pop-up has no dl.panel");
        return w;
    }

    [TestMethod]
    public void The_shelf_lists_the_folder_newest_first_flags_what_is_new_and_opens_and_reveals_as_dry_runs()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mantle-dl-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try {
            var (app, dir) = Launch("downloads", folder);
            using var _ = app;
            Has(dir, "downloads: 0 items, 0 new [start]", "the first read did not happen", 15000);
            var btn = app.Find("bar.downloads");
            Assert.IsTrue(Host.Until(() => btn.Name == "Downloads", 3000), $"the button says \"{btn.Name}\"");

            // empty: the panel says so; the second click closes it
            ClickEl(btn);
            var w = Shelf(app, dir);
            Assert.IsTrue(Host.Until(() => El(w, "dl.empty.big").Name == "Nothing downloaded yet", 5000), "no empty state");
            Assert.AreEqual("Files you download show up here, newest first.", El(w, "dl.empty.sub").Name);
            StringAssert.StartsWith(Help(El(w, "dl.list")), "Arrow keys do not reach this panel: a test instance never takes the keyboard");
            ClickEl(btn);
            Has(dir, "downloads: the shelf closed", "the second click did not close the shelf");

            // a file arrives: the watch reads it, the button gets its dot; a partial is counted, never a row
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "a");
            Thread.Sleep(50);
            File.WriteAllText(Path.Combine(folder, "fresh.zip"), new string('x', 2048));
            File.WriteAllText(Path.Combine(folder, "movie.mp4.crdownload"), "x");
            Has(dir, "downloads: 2 items, 2 new, 1 downloading [watch]", "the watch did not pick the new files up", 10000);
            Assert.IsTrue(Host.Until(() => Status(btn) == "new" && btn.Name == "Downloads — 2 new", 3000), $"the button says \"{btn.Name}\" ({Status(btn)})");
            Host.Screenshot("downloads-button-new-light");

            ClickEl(btn);
            w = Shelf(app, dir);
            var row = El(w, "dl.row.0");
            StringAssert.StartsWith(row.Name, "fresh.zip, new, Archive, 2 KB · ");
            StringAssert.StartsWith(El(w, "dl.row.1").Name, "notes.txt, new, Document, ");
            Assert.AreEqual("1 still downloading…", El(w, "dl.sub").Name);
            Assert.AreEqual("fresh", Status(row));
            Has(dir, "downloads: the shelf shows 2 rows (ok, 2 new)", "the shelf did not read the folder on open");
            // opening marks them seen: the dot goes, the rows keep theirs while open
            Assert.IsTrue(Host.Until(() => btn.Name == "Downloads" && Status(btn) == "ok", 5000), $"the button still says \"{btn.Name}\" ({Status(btn)})");
            Host.Screenshot("downloads-shelf-light");

            ClickEl(El(w, "dl.reveal.1"));
            Has(dir, "downloads: reveal was a dry run", "Show in folder was not a dry run");
            Assert.IsTrue(Host.Until(() => El(w, "dl.note").Name == "Test run: would show it selected in Explorer.", 3000), $"the note says \"{El(w, "dl.note").Name}\"");
            ClickEl(El(w, "dl.row.0"));
            Has(dir, "downloads: open was a dry run", "the row's open was not a dry run");
            Assert.IsTrue(Host.Until(() => El(w, "dl.note").Name == "Test run: would open it with its default app.", 3000), $"the note says \"{El(w, "dl.note").Name}\"");
            ClickEl(El(w, "dl.folder"));
            Has(dir, "downloads: folder was a dry run", "the folder button was not a dry run");
            string log = FrameProbe.Log(dir);
            Assert.IsFalse(log.Contains("fresh.zip") || log.Contains(folder, StringComparison.OrdinalIgnoreCase), "a file name or the folder reached the log");
        } finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void A_missing_folder_mutes_the_button_raises_a_chip_and_the_shelf_says_when_it_looks_again()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mantle-dl-ui-missing-" + Guid.NewGuid().ToString("N")[..8]);
        var (app, dir) = Launch("downloads-missing", folder);
        using var _ = app;
        Has(dir, "downloads: missing: the Downloads folder does not exist", "the missing folder was not read as missing", 15000);
        var btn = app.Find("bar.downloads");
        Assert.IsTrue(Host.Until(() => Status(btn) == "off", 3000), $"the button is {Status(btn)}");
        StringAssert.StartsWith(btn.Name, "Downloads unavailable — the Downloads folder does not exist");
        var pip = app.TryFind("bar.more.pip", 8000);
        Assert.IsNotNull(pip, "no pip on more");
        Assert.IsTrue(Host.Until(() => Help(pip!).Contains("downloads off"), 3000), "the pip does not name it: " + Help(pip!));
        ClickEl(btn);
        var w = Shelf(app, dir);
        Assert.IsTrue(Host.Until(() => El(w, "dl.empty.big").Name == "Your Downloads folder is missing", 5000), "no missing heading");
        Assert.AreEqual("It was moved, renamed or deleted.", El(w, "dl.empty.sub").Name);
        Assert.AreEqual("Mantle looks again every 2 minutes and each time you open this panel.", El(w, "dl.next").Name);
        Assert.IsFalse(El(w, "dl.folder").IsEnabled, "the folder button is on with no folder to open");
        Assert.AreEqual("There is no Downloads folder to open right now", Help(El(w, "dl.folder")));
        Host.Screenshot("downloads-missing-light");
    }
}
