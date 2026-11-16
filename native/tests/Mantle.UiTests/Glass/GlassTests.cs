using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Mantle.UiTests;

/// The see-through look (docs/parity/look.md SEE-005 .. SEE-009): the pop-ups paint look.json's desktop.still sharp,
/// cover-fit to the monitor and lined up with the screen, under Wash.Alpha's tint. Fixture only: LOOK_SYNC_DIR points the
/// run at a scratch look.json whose desktop.still is a PNG this test writes; the user's real look.json and wallpaper are
/// never read. The check is on real screen pixels: at known screen points inside the pop-up, the pixel must be the still's
/// pixel at that screen point composited with the tint, within a tolerance.
[TestClass]
public sealed class GlassTests
{
    [TestCleanup]
    public void Cleanup() { FrameProbe.CursorAway(); Host.KillAll(); }

    const int StillW = 1600, StillH = 900, Block = 100, Tol = 6;
    const double DarkDim = 0.40, LightDim = 0.35;

    /// The fixture still: 16 x 9 blocks of 100 px, each its own colour, so a screen point's expected colour does not
    /// depend on the scaler's filter (points are taken 20 px or more inside a block).
    static (byte R, byte G, byte B) BlockColour(int i, int j) => ((byte)(20 + i * 47 % 216), (byte)(20 + j * 71 % 216), (byte)(20 + (i + j) * 29 % 216));

    static void WritePng(string path)
    {
        var raw = new byte[StillH * (1 + StillW * 3)];
        for (int y = 0, o = 0; y < StillH; y++) {
            raw[o++] = 0;   // filter: none
            for (int x = 0; x < StillW; x++) { var c = BlockColour(x / Block, y / Block); raw[o++] = c.R; raw[o++] = c.G; raw[o++] = c.B; }
        }
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
        using var f = new MemoryStream();
        f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, StillW); BE(ihdr, 4, StillH); ihdr[8] = 8; ihdr[9] = 2;   // 8-bit RGB
        Chunk(f, "IHDR", ihdr); Chunk(f, "IDAT", z.ToArray()); Chunk(f, "IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, f.ToArray());
    }
    static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(td);
        uint c = 0xFFFFFFFF;
        foreach (byte b in td) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        var crc = new byte[4]; BE(crc, 0, (int)~c); s.Write(crc);
    }

    /// A scratch look-sync folder: look.json with the glass (dimAuto absent: automatic; no guard, so the tint is Dim) and
    /// a desktop record whose still is <paramref name="still"/>.
    static string Fixture(string tag, string still)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mantle-glass-" + tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        string esc = still.Replace("\\", "\\\\");
        string doc = "{\"schema\":1,\"rev\":3,\"writer\":{\"app\":\"quartz\",\"pid\":1,\"at\":\"2026-11-15T00:00:00.000Z\"},\"look\":{" +
            "\"glass\":{\"v\":{\"dark\":{\"blurPx\":5,\"dim\":" + DarkDim + ",\"panel\":0},\"light\":{\"blurPx\":12,\"dim\":" + LightDim + ",\"panel\":0},\"render\":\"picture\"},\"rev\":2,\"by\":\"quartz\"}}," +
            "\"desktop\":{\"rev\":3,\"ok\":true,\"id\":\"test:blocks\",\"type\":\"picture\",\"title\":\"blocks\",\"still\":\"" + esc + "\",\"stillSize\":[" + StillW + "," + StillH + "],\"by\":\"mantle\"}}";
        File.WriteAllText(Path.Combine(dir, "look.json"), doc);
        return dir;
    }

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public Win32.RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    static Win32.RECT Monitor(IntPtr h)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        Assert.IsTrue(GetMonitorInfoW(MonitorFromWindow(h, 2), ref mi), "the pop-up's monitor could not be read");
        return mi.rcMonitor;
    }

    /// The expected colour at a screen point: the still cover-fit and centred on the monitor (the desktop's fit), then the
    /// wash composited in 8 bits. Null when the point is within 20 still px of a block edge (left to the scaler).
    static (int R, int G, int B)? Expected(int sx, int sy, Win32.RECT mon, bool dark, double alpha)
    {
        int mw = mon.Right - mon.Left, mh = mon.Bottom - mon.Top;
        double k = Math.Max(mw / (double)StillW, mh / (double)StillH);
        int tw = Math.Max(mw, (int)Math.Ceiling(StillW * k)), th = Math.Max(mh, (int)Math.Ceiling(StillH * k));
        double u = (sx - mon.Left + (tw - mw) / 2 + 0.5) * StillW / (double)tw, v = (sy - mon.Top + (th - mh) / 2 + 0.5) * StillH / (double)th;
        if (u % Block < 20 || u % Block > Block - 20 || v % Block < 20 || v % Block > Block - 20) return null;
        var c = BlockColour((int)(u / Block), (int)(v / Block));
        double a = Math.Round(alpha * 255) / 255;
        int w0 = dark ? 9 : 255, w1 = dark ? 10 : 255, w2 = dark ? 11 : 255;
        return ((int)Math.Round(c.R * (1 - a) + w0 * a), (int)Math.Round(c.G * (1 - a) + w1 * a), (int)Math.Round(c.B * (1 - a) + w2 * a));
    }

    /// Sample the lower part of the pop-up (the stand-in's words sit at its top-left): every point that is not near a block
    /// edge. -> (points, worst channel error, the failures' words).
    static (int N, int Worst, string Bad) Check(IntPtr h, bool dark, double alpha, string what)
    {
        Assert.IsTrue(FrameProbe.GetWindowRect(h, out var r), what + ": no window rect");
        var mon = Monitor(h);
        int x0 = r.Left + 24, x1 = r.Right - 24, y0 = r.Top + (r.Bottom - r.Top) * 45 / 100, y1 = r.Top + (r.Bottom - r.Top) * 78 / 100;
        var px = Win32.Grab(x0, y0, x1 - x0, y1 - y0);
        int n = 0, worst = 0; var bad = new StringBuilder();
        for (int y = y0; y < y1; y += 7)
            for (int x = x0; x < x1; x += 7) {
                if (Expected(x, y, mon, dark, alpha) is not { } e) continue;
                int i = ((y - y0) * (x1 - x0) + (x - x0)) * 4;
                int b = px[i], g = px[i + 1], rr = px[i + 2];
                int err = Math.Max(Math.Abs(rr - e.R), Math.Max(Math.Abs(g - e.G), Math.Abs(b - e.B)));
                n++; worst = Math.Max(worst, err);
                if (err > Tol && bad.Length < 600) bad.Append($" ({x},{y}) got {rr},{g},{b} want {e.R},{e.G},{e.B};");
            }
        Console.WriteLine($"glass check {what}: {n} points, worst channel error {worst} (tolerance {Tol}), window {r.Left},{r.Top},{r.Right},{r.Bottom}, monitor {mon.Left},{mon.Top},{mon.Right},{mon.Bottom}");
        Host.Screenshot("glass-" + what);
        return (n, worst, bad.ToString());
    }

    static void ClickEl(BarApp app, string id) { var r = app.Find(id).BoundingRectangle; ZOrder.Click(r.Left + r.Width / 2, r.Top + r.Height / 2); Thread.Sleep(450); }
    static string Tail(string dir) { var l = FrameProbe.Log(dir).Split('\n'); return string.Join("\n", l.Skip(Math.Max(0, l.Length - 30))); }
    static int Count(string log, string needle) { int c = 0, i = 0; while ((i = log.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; } return c; }

    static void Lined(string dir, IntPtr h, bool dark, string what)
    {
        double a = dark ? DarkDim : LightDim;
        (int N, int Worst, string Bad) c = (0, 0, "");
        // the picture lands once decoded (first open) and the compositor shows it a frame later: a few looks, no more
        for (int i = 0; i < 12; i++) { c = Check(h, dark, a, what); if (c.N >= 12 && c.Worst <= Tol) break; Thread.Sleep(250); }
        Assert.IsTrue(c.N >= 12, $"{what}: only {c.N} sample points inside blocks");
        Assert.IsTrue(c.Worst <= Tol, $"{what}: the pixels are not the still at those screen points under the {a:0.00} tint (worst error {c.Worst} over {c.N} points):{c.Bad}\n{Tail(dir)}");
    }

    [TestMethod]
    [DataRow("dark")]
    [DataRow("light")]
    public void Popups_paint_the_desktop_still_sharp_lined_up_with_the_screen_after_a_move_a_park_and_in_each_kind(string theme)
    {
        bool dark = theme == "dark";
        string dir = FrameProbe.NewDir("glass-" + theme);
        string still = Path.Combine(dir, "still.png");
        WritePng(still);
        var env = new Dictionary<string, string> { ["MANTLE_TEST_POPUPS"] = "1", ["LOOK_SYNC_DIR"] = Fixture(theme, still), ["MANTLE_TEST_GLASS"] = "" };
        using var app = BarApp.Launch("glass-" + theme + "-" + Guid.NewGuid().ToString("N")[..4], theme: theme, env: env, extra: new[] { "--bench-out=" + dir });
        int pid = app.Proc.Id;
        Host.LogContains(() => FrameProbe.Log(dir), "glass: the desktop still is " + still, "the look's still reached the backdrop", 10000);

        // the instant kind, open
        ClickEl(app, "bar.more");
        IntPtr more = Host.Until(() => FrameProbe.Find(pid, "Mantle pop-up: More") is var h && FrameProbe.Showing(h) ? (object)h : null, 3000) is IntPtr m ? m : IntPtr.Zero;
        Assert.AreNotEqual(IntPtr.Zero, more, "More did not open:\n" + Tail(dir));
        Host.LogContains(() => FrameProbe.Log(dir), "glass pop-up More: painted the desktop still", "More painted the still", 8000);
        Lined(dir, more, dark, theme + "-more-open");

        // moved from outside: only the offset changes, and the picture stays put on the screen
        Assert.IsTrue(FrameProbe.GetWindowRect(more, out var r0));
        int moves = Count(FrameProbe.Log(dir), "glass pop-up More: moved");
        Assert.IsTrue(SetWindowPos(more, IntPtr.Zero, r0.Left - 37, r0.Top - 23, 0, 0, 0x0001 | 0x0004 | 0x0010), "the pop-up could not be moved");
        Assert.IsTrue(Host.Until(() => Count(FrameProbe.Log(dir), "glass pop-up More: moved") > moves, 3000), "the move was not heard:\n" + Tail(dir));
        Lined(dir, more, dark, theme + "-more-moved");
        Assert.AreEqual(1, Count(FrameProbe.Log(dir), "glass: the desktop still 1600x900 cover-fit"), "a move or a second window decoded the still again:\n" + Tail(dir));

        // parked (its button again) and opened again: placed where it belongs, still lined up
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => !FrameProbe.Showing(more), 2000), "More did not close");
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(more), 2000), "More did not open again");
        Thread.Sleep(400);   // the entrance (320 ms) has landed
        Lined(dir, more, dark, theme + "-more-reopened");

        // the other kind (made on demand, takes the keyboard) replaces it in place
        ClickEl(app, "bar.clock");
        IntPtr cal = Host.Until(() => FrameProbe.Find(pid, "Mantle pop-up: Calendar") is var h && FrameProbe.Showing(h) ? (object)h : null, 3000) is IntPtr c ? c : IntPtr.Zero;
        Assert.AreNotEqual(IntPtr.Zero, cal, "Calendar did not open:\n" + Tail(dir));
        Thread.Sleep(400);
        Lined(dir, cal, dark, theme + "-cal-open");
        Assert.AreEqual(1, Count(FrameProbe.Log(dir), "glass: the desktop still 1600x900 cover-fit"), "the second window decoded the still again (it is shared):\n" + Tail(dir));
        StringAssert.Contains(FrameProbe.Log(dir), $"tint {theme} {(dark ? DarkDim : LightDim):0.00} (Automatic: {(dark ? DarkDim : LightDim):0.00}, not raised: Mantle has not published a top band for this picture)");
    }

    [TestMethod]
    public void A_missing_or_unreadable_still_gives_a_plain_fill_and_says_why_and_a_fixed_file_is_taken_from_the_watch()
    {
        string dir = FrameProbe.NewDir("glass-missing");
        string still = Path.Combine(dir, "still.png");   // not there yet
        var env = new Dictionary<string, string> { ["MANTLE_TEST_POPUPS"] = "1", ["LOOK_SYNC_DIR"] = Fixture("missing", still), ["MANTLE_TEST_GLASS"] = "" };
        using var app = BarApp.Launch("glass-missing-" + Guid.NewGuid().ToString("N")[..4], theme: "dark", env: env, extra: new[] { "--bench-out=" + dir });
        int pid = app.Proc.Id;
        ClickEl(app, "bar.more");
        Host.LogContains(() => FrameProbe.Log(dir), "glass pop-up More: no picture, a plain fill: the desktop still is missing: " + still, "a missing still says so", 8000);
        ClickEl(app, "bar.more");

        File.WriteAllBytes(still, Encoding.ASCII.GetBytes("this is not a picture"));
        Host.LogContains(() => FrameProbe.Log(dir), "glass: the desktop still's file changed", "the file watch heard the still", 5000);
        ClickEl(app, "bar.more");
        Host.LogContains(() => FrameProbe.Log(dir), "glass pop-up More: no picture, a plain fill: the desktop still could not be decoded", "an unreadable still says so", 8000);
        ClickEl(app, "bar.more");

        int heard = Count(FrameProbe.Log(dir), "glass: the desktop still's file changed");
        WritePng(still);
        Assert.IsTrue(Host.Until(() => Count(FrameProbe.Log(dir), "glass: the desktop still's file changed") > heard, 5000), "the fixed file was not heard:\n" + Tail(dir));
        ClickEl(app, "bar.more");
        IntPtr more = FrameProbe.Find(pid, "Mantle pop-up: More");
        Host.LogContains(() => FrameProbe.Log(dir), "glass pop-up More: painted the desktop still", "the fixed still is painted", 8000);
        Lined(dir, more, true, "dark-more-after-fix");
    }

    [TestMethod]
    public void The_electron_switch_keeps_the_earlier_look_for_the_screenshot_pairs()
    {
        string dir = FrameProbe.NewDir("glass-electron");
        string still = Path.Combine(dir, "still.png");
        WritePng(still);
        var env = new Dictionary<string, string> { ["MANTLE_TEST_POPUPS"] = "1", ["LOOK_SYNC_DIR"] = Fixture("electron", still), ["MANTLE_TEST_GLASS"] = "electron" };
        using var app = BarApp.Launch("glass-electron-" + Guid.NewGuid().ToString("N")[..4], theme: "dark", env: env, extra: new[] { "--bench-out=" + dir });
        Host.LogContains(() => FrameProbe.Log(dir), "MANTLE_TEST_GLASS=electron: this test run keeps native's earlier look", "the switch is said", 8000);
        ClickEl(app, "bar.more");
        Assert.IsTrue(Host.Until(() => FrameProbe.Showing(FrameProbe.Find(app.Proc.Id, "Mantle pop-up: More")), 3000), "More did not open");
        Thread.Sleep(500);
        Assert.IsFalse(FrameProbe.Log(dir).Contains("glass pop-up", StringComparison.Ordinal), "a pop-up took the new look under the electron switch:\n" + Tail(dir));
    }
}
