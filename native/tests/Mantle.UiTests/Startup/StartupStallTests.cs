using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests.Startup;

/// <summary>
/// The start-up freeze (lane native-r8-startup): the UI thread is never held long in Mantle's first 10 s, so a user
/// who uses the bar or right-clicks the desktop while Mantle starts (the sidecar coming up, the surfaces' first
/// builds) meets an app that answers. The app's own stall meter (Shell/Diagnostics/StallMeter.cs, MANTLE_TEST_STALL=1)
/// queues one empty job on the UI thread every 50 ms from the bar's first frame and logs how late each one ran; this
/// test reads its last line. It never touches the app through UI Automation while the meter runs (a UIA read is served
/// on the UI thread too), and it does not read the bar's test-only 250 ms beat.
/// </summary>
[TestClass]
public class StartupStallTests
{
    /// The limit (ms) for the longest stall in the first 10 s. See native/docs/lanes/startup.md for the measured numbers.
    public const int LimitMs = 250;

    static readonly Regex Longest = new(@"stall meter: the longest UI-thread stall in the first 10 s was (\d+) ms", RegexOptions.CultureInvariant);

    [TestCleanup]
    public void Cleanup() => Host.KillAll();

    [TestMethod]
    public void The_UI_thread_is_never_held_over_250_ms_in_the_first_10_s()
    {
        string dir = FrameProbe.NewDir("startup-stall");
        var env = new Dictionary<string, string> { ["MANTLE_TEST_STALL"] = "1" };
        using var app = BarApp.Launch("startup-stall", "light", wait: false, env: env, extra: new[] { "--bench-out=" + dir });
        string log = "";
        try {
            Host.LogContains(() => FrameProbe.Log(dir), "stall meter: on (MANTLE_TEST_STALL=1)", "the stall meter never started", 30000);
            Host.LogContains(() => log = FrameProbe.Log(dir), "stall meter: the longest UI-thread stall in the first 10 s was ", "the stall meter never said its longest stall", 30000);
        } finally {
            if (log.Length == 0) log = FrameProbe.Log(dir);
            try { File.WriteAllText(Path.Combine(Host.OutDir, "startup-stall-native-log.txt"), log); } catch (IOException) { }
        }
        var stalls = log.Split('\n').Where(l => l.Contains("stall meter: ", StringComparison.Ordinal) || l.Contains("ui slice: ", StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')).ToList();
        foreach (var l in stalls) Console.WriteLine(l);
        Assert.IsFalse(log.Contains("stall meter: it failed", StringComparison.Ordinal), "the stall meter failed:\n" + string.Join("\n", stalls));
        var m = Longest.Match(log);
        Assert.IsTrue(m.Success, "no longest-stall line");
        int ms = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.IsTrue(ms < LimitMs, $"the UI thread was held {ms} ms in Mantle's first 10 s (limit {LimitMs} ms):\n" + string.Join("\n", stalls));
    }
}
