using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Desktop;

// The desktop widgets' words and timing, pure (ui/desktop/desktop.js paintClock / tickStats / the Mission Control
// probe; lane native-r7-desktop). The surface (Mantle.App/Surfaces/Desktop) only draws what these say.

/// <summary>The clock: 12-hour with seconds and AM/PM, Windows style ("4:15:32 PM"), whatever the locale.</summary>
public static class DeskClock
{
    /// The boot placeholder, until the first paint.
    public const string BootHm = "--:--", BootSs = "--";

    public readonly record struct Parts(string Hm, string Ss, string Mer)
    {
        public string Text => $"{Hm}:{Ss} {Mer}";
    }

    /// Hour unpadded (0 -> 12), minutes and seconds two digits; AM/PM computed by hand so a 24-hour locale cannot turn
    /// it into military time.
    public static Parts Of(DateTime t)
    {
        int h = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return new Parts($"{h}:{t.Minute:00}", $"{t.Second:00}", t.Hour < 12 ? "AM" : "PM");
    }

    /// A chained timer aimed at the next second boundary (floor 60 ms), never a fixed interval.
    public static int NextDelayMs(DateTime now) => Math.Max(60, 1000 - now.Millisecond);

    /// The date line in the locale's order: long weekday, day and long month, no year ("Friday, November 6").
    public static string DateLine(DateTime t, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        string p = c.DateTimeFormat.LongDatePattern;
        // the year goes, with whatever separates it from the rest
        p = Regex.Replace(p, @"[\s,.\-/]*y+[\s.\-/]*(?:г\.|年)?", " ").Trim().TrimEnd(',', ' ', '.');
        if (!p.Contains("dddd", StringComparison.Ordinal)) p = "dddd " + p;
        p = Regex.Replace(p, @"(?<!d)dd(?!d)", "d");
        string s = t.ToString(p, c);
        return Regex.Replace(s, @"\s{2,}", " ").Trim();
    }
}

/// <summary>The System tile's words (sys.stats every 5 s).</summary>
public static class DeskStats
{
    public const int EveryMs = 5000;
    public const string Dash = "—";

    public sealed record View(string Cpu, string Mem, string? Battery, string? Note);

    public static string Gib(double bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    /// One read. <paramref name="batteryPercent"/> null = no battery row (and <paramref name="batteryReason"/> may say why).
    public static View Of(double cpu, double memUsed, double memTotal, double? batteryPercent, bool charging, string? batteryReason)
    {
        string? bat = batteryPercent is { } p ? $"{Math.Round(p).ToString(CultureInfo.InvariantCulture)}%" + (charging ? " · charging" : "") : null;
        string? note = bat == null && !string.IsNullOrEmpty(batteryReason) && batteryReason != "this machine reports no battery" && batteryReason != "ok"
            ? "Battery: " + batteryReason : null;
        return new View($"{Math.Round(cpu).ToString(CultureInfo.InvariantCulture)}%", Gib(memUsed) + " / " + Gib(memTotal), bat, note);
    }

    public static View Failed(string error) => new(Dash, Dash, null, "System numbers unavailable: " + error);

    /// Before the first read.
    public static View Boot { get; } = new(Dash, Dash, null, null);
}

/// <summary>The Mission Control tile: the probe's cadence and its words.</summary>
public static class McProbe
{
    public const string Origin = "http://127.0.0.1:7373";
    public const int EveryMs = 30000, MaxMs = 10 * 60 * 1000;
    public const string BootNote = "Checking 127.0.0.1:7373…";
    public const string NoReason = "it did not answer and did not say why";

    /// 30 s while it answers; while down 30 s, then doubling, to 10 min at most.
    public static int NextDelayMs(int fails)
    {
        if (!(fails > 1)) return EveryMs;
        return (int)Math.Min(EveryMs * Math.Pow(2, Math.Min(fails, 20) - 1), MaxMs);
    }

    /// "in 30 s" / "in 4 min, at 4:19 PM".
    public static string When(int ms, DateTime now)
    {
        int s = (int)Math.Round(ms / 1000.0, MidpointRounding.AwayFromZero);
        if (s < 60) return $"in {s} s";
        var at = now.AddMilliseconds(ms);
        int h = at.Hour % 12 == 0 ? 12 : at.Hour % 12;
        return $"in {(int)Math.Round(s / 60.0, MidpointRounding.AwayFromZero)} min, at {h}:{at.Minute:00} {(at.Hour < 12 ? "AM" : "PM")}";
    }

    /// The note while it is down.
    public static string Offline(string reason, int nextMs, DateTime now) =>
        "Mission Control offline: " + reason + "." +
        (nextMs > 0 ? " Next check " + When(nextMs, now) + "." : "") +
        (nextMs > EveryMs ? " Checks slow down while it stays down, to one every 10 min at most." : "");

    public static string Resting(string why) => $"Mission Control closed while Mantle rests ({why}); back when the game exits.";
}
