using System.Drawing;
using FlaUI.Core.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// Lane r15-bar-light: the islands' inks per theme (Theme/Levitate.xaml's LvBar* set). Light is apps-r3/LIGHT.md section 4
/// (text #0F1E21, text-2 .76, fills black .05 / pressed .04, hair black .08, the run dot text-3 .56, the accent #083F49 with
/// #F4F7F8 on it, the hover sheen white .16); dark is what the islands showed before lane r15, with the accent Core's
/// "islands" profile (#A9E7ED on #052A2F). The app says the inks it laid once per theme ("bar islands inks (...)");
/// the clock's time is then read in the pixels: dark ink on the lifted light island, light ink on the dark one.
[TestClass]
public sealed class IslandsInkTests
{
    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    const string Light = "bar islands inks (light): text #FF0F1E21, text-2 #C20F1E21, hover #0D000000, pressed #0A000000, hair #14000000, "
        + "run #8F0F1E21, accent #FF083F49 on #FFF4F7F8, sheen #29FFFFFF";
    const string Dark = "bar islands inks (dark): text #FFEEF6F7, text-2 #C2E6F2F4, hover #12FFFFFF, pressed #0DFFFFFF, hair #17FFFFFF, "
        + "run #FFA9AAA7, accent #FFA9E7ED on #FF052A2F, sheen #12FFFFFF";

    [TestMethod]
    public void Light_islands_wear_the_LIGHT_md_inks_and_the_islands_accent() => Inks("light", Light);

    [TestMethod]
    public void Dark_islands_keep_their_inks_and_take_Cores_islands_accent() => Inks("dark", Dark);

    static void Inks(string theme, string want)
    {
        string profile = "uitest-islands-ink-" + theme;
        using var app = BarApp.Launch(profile, theme);
        string log = Path.Combine(Path.GetTempPath(), "mantle-native-uitest-" + profile, "mantle-native.log");
        Host.LogContains(() => Read(log), want, $"the {theme} islands did not lay the expected inks", 8000);

        // the clock's time in the pixels: its darkest (light) or brightest (dark) pixels stand clear of the island's median
        var r = app.Find("bar.clock.time").BoundingRectangle;
        Mouse.Position = new Point(r.Right + 400, Math.Max(0, r.Y - 300));   // no hover fill or sheen
        Thread.Sleep(1500);   // first frame, and the dim and the lift laid
        var px = Win32.Grab(r.X, r.Y, Math.Max(4, r.Width), Math.Max(4, r.Height));
        var lum = new double[px.Length / 4];
        for (int i = 0; i < lum.Length; i++) lum[i] = 0.114 * px[i * 4] + 0.587 * px[i * 4 + 1] + 0.299 * px[i * 4 + 2];
        var sorted = lum.OrderBy(v => v).ToArray();
        double median = sorted[sorted.Length / 2], ink = theme == "light" ? sorted[0] : sorted[^1];
        Console.WriteLine($"{theme} clock time: median {median:0.0}, ink {ink:0.0}");
        if (theme == "light") Assert.IsTrue(ink < median - 60, $"the light clock's ink ({ink:0.0}) is not clearly darker than the island ({median:0.0}): text #0F1E21 on the lifted island?");
        else Assert.IsTrue(ink > median + 60, $"the dark clock's ink ({ink:0.0}) is not clearly lighter than the island ({median:0.0})");
    }

    static string Read(string path)
    {
        try {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(f).ReadToEnd();
        } catch (IOException) { return ""; }
    }
}
