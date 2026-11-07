using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Tray;

// main.js <mainrel:report> and lib/tray-icon.js, PURE: "Copy diagnostic report" as text, and the
// code-drawn icon. The report is paste-safe: no window titles, no file or device names, and the user
// name, PC name and home folder removed by the log's own scrubber (passed in as scrub).

public sealed record ReportDisplay(int W, int H, double Scale, bool Primary);

/// <param name="Runtime">What runs Mantle ("Electron 33.2.0" there; ".NET 10.0.1, Windows App SDK 1.7" here).</param>
public sealed record ReportSnapshot(
    string? When, string? Version, string? Commit, string? Runtime, string? Windows, bool Test, double UptimeS,
    IReadOnlyList<ReportDisplay> Displays, (int All, int Processes, int Main)? MemMB, TrayHealth Health, string? Recovery,
    string LinkStatus, int LinkRestarts, bool LinkGaveUp, int FeaturesOn, int FeaturesOff, IReadOnlyList<string> FeaturesDown,
    IReadOnlyList<string> Recent);

public static class TrayReport
{
    public const string CopiedTitle = "Diagnostic report copied";
    public const string CopiedText = "Paste it wherever you need it. Your user name, PC name and home folder were left out.";

    public static string Build(ReportSnapshot s, Func<string, string>? scrub = null)
    {
        static string Dur(double sec)
        {
            var n = Math.Max(0, (long)Math.Round(sec));
            var h = n / 3600; var m = n % 3600 / 60;
            return h > 0 ? $"{h} h {m} min" : $"{m} min";
        }
        var o = new List<string>
        {
            "Mantle diagnostic report",
            $"Made {s.When ?? "at an unknown time"}. User name, PC name and home folder are left out; read it before you share it.",
            "",
            $"Mantle {s.Version ?? "?"}{(s.Commit is { Length: > 0 } c ? $" ({c})" : "")}, {(s.Test ? "a test run" : "running live")}, up {Dur(s.UptimeS)}",
            $"Windows {s.Windows ?? "?"}, {s.Runtime ?? "?"}",
            "Displays: " + (s.Displays.Count > 0
                ? string.Join(", ", s.Displays.Select(d => $"{d.W}x{d.H} at {Math.Round(d.Scale * 100)}%{(d.Primary ? " (main)" : "")}"))
                : "none reported"),
        };
        if (s.MemMB is { } mem) o.Add($"Memory: {mem.All} MB in {mem.Processes} processes (Mantle's main process {mem.Main} MB)");
        o.Add("");
        o.Add($"Health: {s.Health.Head}");
        foreach (var l in s.Health.Lines) o.Add($"  - {l}");
        if (s.Recovery != null) o.Add($"  - {s.Recovery}");
        o.Add($"Link to Windows: {s.LinkStatus}{(s.LinkGaveUp ? ", gave up restarting" : "")}{(s.LinkRestarts > 0 ? $", {s.LinkRestarts} restart(s) in a row" : "")}");
        o.Add($"Features: {s.FeaturesOn} on, {s.FeaturesOff} off{(s.FeaturesDown.Count > 0 ? "; not working: " + string.Join(", ", s.FeaturesDown) : "")}");
        o.Add("");
        o.Add(s.Recent.Count > 0 ? $"Last {s.Recent.Count} warnings and errors this run, oldest first:" : "No warnings or errors this run.");
        foreach (var r in s.Recent) o.Add($"  {r}");
        var text = string.Join("\n", o) + "\n";
        return scrub != null ? scrub(text) : text;
    }

    /// <summary>The last <paramref name="max"/> warning lines, each stamped HH:MM:SS and cut to 240 characters.</summary>
    public sealed class RecentLines
    {
        readonly int _max;
        readonly Func<DateTime> _now;
        readonly Queue<string> _lines = new();
        public RecentLines(int max = 15, Func<DateTime>? now = null) { _max = max; _now = now ?? (() => DateTime.Now); }

        public void Add(string chunk)
        {
            var t = _now().ToString("HH:mm:ss");
            foreach (var raw in (chunk ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                _lines.Enqueue($"{t} {(line.Length > 240 ? line[..239] + "…" : line)}");
                while (_lines.Count > _max) _lines.Dequeue();
            }
        }

        public IReadOnlyList<string> List() => _lines.ToArray();
    }

    /// <summary>lib/tray-icon.js squareBitmap: a rounded square in the accent --ion (#72ADCA), 3x3-sampled
    /// coverage, premultiplied BGRA, top-down rows. No shipped image file.</summary>
    public static byte[] IconBitmap(int size)
    {
        const byte R = 0x72, G = 0xAD, B = 0xCA;
        var inset = Math.Max(1, (int)Math.Round(size / 16.0, MidpointRounding.AwayFromZero));
        var radius = Math.Max(2, (int)Math.Round(size / 4.0, MidpointRounding.AwayFromZero));
        double x0 = inset, y0 = inset, x1 = size - inset, y1 = size - inset;
        var buf = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var hits = 0;
                for (var sy = 0; sy < 3; sy++)
                    for (var sx = 0; sx < 3; sx++)
                    {
                        var px = x + (sx + 0.5) / 3; var py = y + (sy + 0.5) / 3;
                        if (px < x0 || px > x1 || py < y0 || py > y1) continue;
                        var cx = px < x0 + radius ? x0 + radius : px > x1 - radius ? x1 - radius : px;
                        var cy = py < y0 + radius ? y0 + radius : py > y1 - radius ? y1 - radius : py;
                        var dx = px - cx; var dy = py - cy;
                        if (dx * dx + dy * dy <= radius * radius) hits++;
                    }
                var a = hits / 9.0;
                var i = (y * size + x) * 4;
                buf[i] = (byte)Math.Round(B * a, MidpointRounding.AwayFromZero);
                buf[i + 1] = (byte)Math.Round(G * a, MidpointRounding.AwayFromZero);
                buf[i + 2] = (byte)Math.Round(R * a, MidpointRounding.AwayFromZero);
                buf[i + 3] = (byte)Math.Round(255 * a, MidpointRounding.AwayFromZero);
            }
        return buf;
    }
}
