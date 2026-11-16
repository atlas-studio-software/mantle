using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// A look.json change must not freeze the shell (look.md, "the look.json timing"): Quartz and Helm rewrite look.json on
/// every theme or wallpaper change, and every native window that is up re-applies the look. The UI thread is timed from
/// OUTSIDE the app: a probe thread in this test sends WM_NULL to the bar's window over and over (SendMessageTimeout); the
/// answer comes only when the app's UI thread is back in its message loop, so the longest wait is the longest single job
/// that held the UI thread (to within the probe's own rest, one Sleep(1), at most one 15.6 ms timer tick).
public sealed partial class GlassTests
{
    /// The longest the UI thread may be held by one job of a look.json change (look.md, "the look.json timing"). Measured
    /// (run 43864153617, at c517c29f): at most 1.6 ms (the glass Dim), 1.0 ms (accent + theme) and 0.4 ms (a new still),
    /// 0.2 ms at rest; the app's own timing says 0.8 to 1.1 ms for 8 window layers and 1.9 ms for the copy of a decoded
    /// still (the decode itself, 214 to 371 ms, and the guard stay on the pool thread). 50 ms is the slice budget: far over
    /// the runner's jitter, far under a decode or a guard (100 ms and more) that came back onto the UI thread.
    const double LookStallLimitMs = 50;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeoutW(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// WM_NULL round trips to one window of the app, timed; answers "the longest wait for probes sent in [from, to)".
    sealed class UiProbe : IDisposable
    {
        readonly IntPtr _h;
        readonly Thread _t;
        readonly List<(long At, double Ms)> _s = new();
        readonly long _base = Stopwatch.GetTimestamp();
        readonly DateTime _baseAt = DateTime.Now;
        volatile bool _stop;
        public int Failed;

        public UiProbe(IntPtr h) { _h = h; _t = new Thread(Run) { IsBackground = true, Name = "ui probe" }; _t.Start(); }

        void Run()
        {
            while (!_stop) {
                long t0 = Stopwatch.GetTimestamp();
                if (SendMessageTimeoutW(_h, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero, 0 /* SMTO_NORMAL */, 10000, out _) == IntPtr.Zero) Interlocked.Increment(ref Failed);
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                lock (_s) _s.Add((t0, ms));
                Thread.Sleep(1);
            }
        }

        public static long Now => Stopwatch.GetTimestamp();

        /// The local time a probe was sent (the app's log uses local HH:mm:ss.fff), to line a stall up with the log.
        public string When(long at) => _baseAt.AddSeconds((at - _base) / (double)Stopwatch.Frequency).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        public (double Ms, string At, int N) Worst(long from, long to)
        {
            double worst = 0; long at = from; int n = 0;
            lock (_s)
                foreach (var (t, ms) in _s)
                    if (t >= from && t < to) { n++; if (ms > worst) { worst = ms; at = t; } }
            return (Math.Round(worst, 1), When(at), n);
        }

        public void Dispose() { _stop = true; _t.Join(15000); }
    }

    /// look.json as another app writes it (Quartz: temp file + rename, the way look-sync writes), with every field this
    /// test moves: the glass Dim per theme, the accent, the theme, and the desktop record's still.
    static void WriteLook(string lookDir, int rev, double darkDim, double lightDim, int? hue, string theme, string still, int deskRev)
    {
        string esc = still.Replace("\\", "\\\\");
        string I(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string doc = "{\"schema\":1,\"rev\":" + rev + ",\"writer\":{\"app\":\"quartz\",\"pid\":1,\"at\":\"2026-11-15T00:00:0" + (rev % 10) + ".000Z\"},\"look\":{" +
            "\"theme\":{\"v\":\"" + theme + "\",\"rev\":" + rev + ",\"by\":\"quartz\"}," +
            "\"accent\":{\"v\":{\"hue\":" + (hue?.ToString(CultureInfo.InvariantCulture) ?? "null") + "},\"rev\":" + rev + ",\"by\":\"quartz\"}," +
            "\"glass\":{\"v\":{\"dark\":{\"blurPx\":5,\"dim\":" + I(darkDim) + ",\"panel\":0},\"light\":{\"blurPx\":12,\"dim\":" + I(lightDim) + ",\"panel\":0},\"render\":\"picture\"},\"rev\":" + rev + ",\"by\":\"quartz\"}}," +
            "\"desktop\":{\"rev\":" + deskRev + ",\"ok\":true,\"id\":\"test:blocks" + deskRev + "\",\"type\":\"picture\",\"title\":\"blocks\",\"still\":\"" + esc + "\",\"stillSize\":[" + StillW + "," + StillH + "],\"by\":\"mantle\"}}";
        string tmp = Path.Combine(lookDir, "look.json." + rev + ".tmp");
        File.WriteAllText(tmp, doc);
        File.Move(tmp, Path.Combine(lookDir, "look.json"), overwrite: true);
    }

    [TestMethod]
    public void A_look_json_change_from_another_app_never_holds_the_UI_thread_long()
    {
        FrameProbe.CursorAway();
        string dir = FrameProbe.NewDir("glass-stall");
        string still = Path.Combine(dir, "still.png"), still2 = Path.Combine(dir, "still2.png");
        WritePng(still);
        WritePng(still2);
        string lookDir = Fixture("stall", still);
        var env = new Dictionary<string, string> {
            ["MANTLE_TEST_POPUPS"] = "1", ["LOOK_SYNC_DIR"] = lookDir, ["MANTLE_TEST_GLASS"] = "", ["MANTLE_TEST_TOPBAR"] = "app=npp&stats=ok",
        };
        using var app = BarApp.Launch("glass-stall-" + Guid.NewGuid().ToString("N")[..4], theme: "dark", env: env, extra: new[] { "--bench-out=" + dir });
        int pid = app.Proc.Id;
        string Log() => FrameProbe.Log(dir);
        // the probe runs from the bar's first frame: what it sees over the start-up is the control (it does see a long hold
        // when there is one: the start-up lane's freeze, not judged here), the look changes below are what is judged
        using var probe = new UiProbe(app.Hwnd);
        long launched = UiProbe.Now;
        // the windows a look change re-applies are up and painting the still: the top bar and a pop-up (More)
        Host.LogContains(Log, "glass top bar: painted the desktop still", "the top bar painted the still", 15000);
        ClickEl(app, "bar.more");
        IntPtr more = Host.Until(() => FrameProbe.Find(pid, "Mantle pop-up: More") is var h && FrameProbe.Showing(h) ? (object)h : null, 3000) is IntPtr m ? m : IntPtr.Zero;
        Assert.AreNotEqual(IntPtr.Zero, more, "More did not open:\n" + Tail(dir));
        Host.LogContains(Log, "glass pop-up More: painted the desktop still", "More painted the still", 8000);
        IntPtr bar = Host.Until(() => FrameProbe.Find(pid, "Mantle top bar") is var t && t != IntPtr.Zero ? (object)t : null, 3000) is IntPtr tb ? tb : IntPtr.Zero;
        uint uiThread = GetWindowThreadProcessId(app.Hwnd, out _);
        string threads = $"bar thread {uiThread}, top bar {GetWindowThreadProcessId(bar, out _)}, More {GetWindowThreadProcessId(more, out _)}";
        Thread.Sleep(2500);   // past the start-up work (the data files' first look at the look-sync folder comes at 2 s)

        var lines = new List<string> { "LOOK-STALL windows: " + threads };
        var worst = new Dictionary<string, double>();
        {
            var start = probe.Worst(launched, UiProbe.Now);
            lines.Add($"LOOK-STALL start-up and the first More open (the control, not judged here): the UI thread was held at most {start.Ms} ms ({start.N} probes, worst at {start.At})");
            long r0 = UiProbe.Now; Thread.Sleep(1500);
            var rest = probe.Worst(r0, UiProbe.Now);
            lines.Add($"LOOK-STALL at rest: the UI thread answered within {rest.Ms} ms ({rest.N} probes, worst at {rest.At})");

            int took = Count(Log(), "glass: the look change took");
            (double Ms, string At, int N) Case(string name, Action write, Func<bool> settled)
            {
                long from = UiProbe.Now;
                string at = probe.When(from);
                write();
                bool done = Host.Until(settled, 8000, 50);
                Thread.Sleep(1200);   // the surfaces' theme payloads and any decode land after the first answer
                var w = probe.Worst(from, UiProbe.Now);
                worst[name] = w.Ms;
                lines.Add($"LOOK-STALL {name}: written at {at}; settled {done}; the UI thread was held at most {w.Ms} ms ({w.N} probes, worst at {w.At})");
                return w;
            }

            // 1. another app moves the glass Dim (the tint moves in every window that is up; every surface hears the look)
            Case("glass dim", () => WriteLook(lookDir, 4, 0.55, 0.45, null, "dark", still, 3),
                () => Count(Log(), "glass: the look change took") > took);
            took = Count(Log(), "glass: the look change took");
            // 2. another app moves the accent and the theme (the run is pinned dark, so the theme is said, not shown)
            Case("accent + theme", () => WriteLook(lookDir, 5, 0.55, 0.45, 200, "light", still, 3),
                () => Log().Contains("hue 200", StringComparison.Ordinal));
            // 3. the wallpaper changes: a new still (decoded on the pool thread; the windows that are up take it again)
            int painted = Count(Log(), "glass top bar: painted the desktop still");
            Case("new still", () => WriteLook(lookDir, 6, 0.55, 0.45, 200, "light", still2, 4),
                () => Count(Log(), "glass top bar: painted the desktop still") > painted && Count(Log(), "glass: the desktop still 1600x900 cover-fit") >= 2);
            lines.Add($"LOOK-STALL probes that failed: {probe.Failed}");
        }
        var slow = Regex.Matches(Log(), @"[^\r\n]*(held the UI thread \d+ ms|the look change took [0-9.]+ ms|the copy into the shared bitmap: [0-9.]+ ms)[^\r\n]*").Select(x => "  app: " + x.Value.Trim());
        lines.AddRange(slow);
        Console.WriteLine(string.Join("\n", lines));
        File.WriteAllLines(Path.Combine(Host.OutDir, "look-stall.txt"), lines);
        try { File.Copy(Path.Combine(dir, "native-log.txt"), Path.Combine(Host.OutDir, "look-stall-app-log.txt"), true); } catch (IOException) { }

        Assert.IsTrue(Count(Log(), "glass: the desktop still 1600x900 cover-fit") >= 2, "the new still was never decoded:\n" + Tail(dir));
        foreach (var (name, ms) in worst)
            Assert.IsTrue(ms < LookStallLimitMs, $"a look.json change ({name}) held the UI thread {ms} ms (limit {LookStallLimitMs} ms):\n" + string.Join("\n", lines));
    }
}
