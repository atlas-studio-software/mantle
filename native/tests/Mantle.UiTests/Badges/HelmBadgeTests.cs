using System.Diagnostics;
using System.Text;
using FlaUI.Core.AutomationElements;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Badges;

/// <summary>
/// The Helm badge's disc on a strip cell (Surfaces/Bar/Badges, ui/bar/helmbadge.js) over the real [Feature("helmbadge")].
/// The test profile's own <c>sources</c> setting points a pinned Character Map at a temp badge file, so neither Helm nor
/// its real badge file is involved; the file is written by tmp + rename, as Helm writes it.
/// </summary>
[TestClass]
public class HelmBadgeTests
{
    static string Status(AutomationElement e) => e.Properties.ItemStatus.ValueOrDefault ?? "";
    static void Has(string dir, string needle, string why, int ms = 8000) => Host.LogContains(() => FrameProbe.Log(dir), needle, why, ms);

    static string Pins(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-pins-" + tag);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string lnk = Path.Combine(dir, "Char Map.lnk"), exe = Path.Combine(Environment.SystemDirectory, "charmap.exe");
        string ps = $"$sh = New-Object -ComObject WScript.Shell; $l = $sh.CreateShortcut('{lnk}'); $l.TargetPath = '{exe}'; $l.Save()";
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(ps)) }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(30000)) { Host.Kill(p); Assert.Fail("making the scratch pin took over 30 s"); }
        Assert.IsTrue(File.Exists(lnk), "the scratch pin was not made");
        return dir;
    }

    static void Write(string file, string json) { File.WriteAllText(file + ".tmp", json); File.Move(file + ".tmp", file, true); }

    [TestMethod]
    public void A_matching_cell_shows_the_count_turns_warn_on_an_error_and_hides_it_when_the_file_cannot_be_read()
    {
        string tag = Guid.NewGuid().ToString("N")[..6];
        const string profile = "helmbadge";
        string ud = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile);
        string bdir = Path.Combine(Path.GetTempPath(), "mantle-hb-ui-" + tag);
        Directory.CreateDirectory(bdir);
        string badge = Path.Combine(bdir, "badge.json");
        Write(badge, "{\"v\":1,\"waiting\":2}");
        Directory.CreateDirectory(Path.Combine(ud, "features"));
        File.WriteAllText(Path.Combine(ud, "features", "helmbadge.json"),
            "{\"sources\":[{\"exe\":\"charmap.exe\",\"file\":" + System.Text.Json.JsonSerializer.Serialize(badge) + ",\"label\":\"Char Map\"}]}");
        string dir = FrameProbe.NewDir(profile);
        try {
            using var app = BarApp.Launch(profile, env: new Dictionary<string, string> { ["MANTLE_TEST_PINS_DIR"] = Pins("hb-" + tag) }, extra: new[] { "--bench-out=" + dir });
            Has(dir, "helmbadge: source 1: count 2", "the badge file was not read", 15000);
            Has(dir, "helmbadge: bar.app.0 shows 2", "the cell did not get its disc", 15000);
            var cell = app.Find("bar.app.0");
            Assert.AreEqual("charmap.exe", cell.Properties.HelpText.ValueOrDefault, "bar.app.0 is not the pinned Character Map");
            Assert.IsTrue(Host.Until(() => Status(cell).EndsWith("; badge 2", StringComparison.Ordinal), 3000), $"the cell's status is \"{Status(cell)}\"");
            Host.Screenshot("helmbadge-2-light");

            Write(badge, "{\"v\":1,\"waiting\":11,\"errors\":1}");
            Has(dir, "helmbadge: bar.app.0 shows 9+ (warn)", "the disc did not follow the file to 9+ in warn");
            Assert.IsTrue(Host.Until(() => Status(cell).EndsWith("; badge 12 warn", StringComparison.Ordinal), 3000), $"the cell's status is \"{Status(cell)}\"");
            Host.Screenshot("helmbadge-9plus-warn-light");

            Write(badge, "{not json");
            Has(dir, "helmbadge: bar.app.0 shows no count (hidden)", "an unreadable file did not hide the count");
            Assert.IsFalse(Status(cell).Contains("badge", StringComparison.Ordinal), $"the cell's status still has a badge: \"{Status(cell)}\"");
            Assert.IsFalse(FrameProbe.Log(dir).Contains("Char Map: count", StringComparison.Ordinal), "the log named the app");
        } finally { try { Directory.Delete(bdir, true); } catch (IOException) { } }
    }
}
