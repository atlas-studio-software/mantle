using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Desktop;

/// <summary>
/// The desktop layer's rules, pure: where it goes (main.js attachDesktop's words for each sidecar mode), when a layer
/// that closed is made again (desktopRemakeVerdict, shellBackAction, desktopAttachNext), and its geometry
/// (lib/windows.js createDesktop: the PRIMARY display's bounds live, an ordinary 960x540 window in --test; the cluster
/// of ui/desktop/desktop.css).
/// </summary>
public static class DesktopLayer
{
    public const int RecheckMs = 30000;
    public const int RemakeMax = 5;
    public const int RemakeSpanMs = 10 * 60 * 1000;
    public static readonly IReadOnlyList<int> AttachBackoffMs = new[] { 1500, 3000, 5000, 8000, 12000, 20000 };
    public const int TestWidth = 960, TestHeight = 540;

    public const string NoDesktop = "--no-desktop: the desktop layer was not created";
    public const string TestNeverAttached = "a test run never puts the desktop layer under the icons, so it is an ordinary window";

    /// The state reason once the sidecar put it in place.
    public static string Attached(string? mode) => mode == "progman-front"
        ? "the desktop layer is behind the icons and in front of the wallpaper, including a video wallpaper (progman-front)"
        : $"the desktop layer is under the wallpaper ({mode ?? "mode not reported"})";

    /// The state reason when the attach failed (held = a re-made layer waiting hidden).
    public static string AttachFailed(string error, bool held) => held
        ? $"the new desktop layer is waiting, hidden, to go under the icons (trying again): {error}"
        : $"the desktop layer could not go under the wallpaper, so it is floating as an ordinary window: {error}";

    public static string RecheckFailed(string error) => $"the desktop layer may be covered by the wallpaper: its position check failed: {error}";
    public const string RecheckBack = "the desktop layer is behind the icons and in front of the wallpaper again";

    public sealed record Remake(bool Again, IReadOnlyList<long> At, string? Reason);

    /// Should a desktop layer that just closed be made again? At most 5 remakes in ten minutes.
    public static Remake Verdict(bool shuttingDown, bool paused, IEnumerable<long>? at, long now)
    {
        var keep = (at ?? Array.Empty<long>()).Where(t => now - t < RemakeSpanMs).ToArray();
        if (shuttingDown) return new(false, keep, null);
        if (paused) return new(false, keep, "the desktop layer closed while Windows was put back; restart Mantle to bring it back");
        if (keep.Length >= RemakeMax)
            return new(false, keep, $"the desktop layer closed {keep.Length + 1} times in ten minutes, so Mantle stopped making it again; restart Mantle to bring it back");
        return new(true, keep.Append(now).ToArray(), "the desktop layer closed (Explorer restarted?); Mantle is making a new one");
    }

    /// Explorer came back: 'none' | 'try-now' | 'make' | 'wait' | 'recheck'.
    public static string ShellBack(bool test, bool shuttingDown, bool paused, bool pending, bool layer, bool held)
    {
        if (test || shuttingDown || paused) return "none";
        if (pending) return "try-now";
        if (!layer) return "make";
        if (held) return "wait";
        return "recheck";
    }

    /// After attach attempt n (0-based) on a layer made again: 'done', 'retry' or 'give-up'. Never "show it anyway".
    public static string AttachNext(bool ok, int n) => ok ? "done" : n + 1 < AttachBackoffMs.Count ? "retry" : "give-up";

    public const string TestRemade = "test run: the desktop layer was made again after it closed (not attached: a test run never touches the shell)";
    public const string ShellBackMaking = "Explorer is back; Mantle is making the desktop layer again";

    /// What Explorer coming back (TaskbarCreated) is said as in the log (main.js shellRestarted).
    public static string ShellBackLine(string how, int count, bool test) => $"Explorer is back ({how}, {count} this run): " + (test
        ? "a test run never touches the shell, so nothing is re-attached"
        : "putting the desktop layer back under the icons");

    /// Out of tries: the layer is closed, never shown over the apps (main.js desktopGiveUp).
    public static string GaveUp(int tries, string? why, string? when) => when != null
        ? $"the desktop layer could not go under the icons {when} ({tries} tries: {why ?? "no reason given"}), so Windows' own wallpaper shows; restart Mantle to bring it back"
        : $"the desktop layer could not go back under the icons after Explorer restarted ({tries} tries: {why ?? "no reason given"}), so Windows' own wallpaper shows; restart Mantle to bring it back";

    // ---- the display changed (DESK-016, lib/display-fit.js) ----

    /// Bursts of display changes become one more fit after this much quiet (main.js displayBurst).
    public const int DisplayQuietMs = 400;

    /// May the layer be sized now? 'fit' | 'later' (held hidden on its way under the icons: sized once there) | 'skip'.
    public static (string Act, string Reason) FitVerdict(bool alive, bool test, bool paused, bool held)
    {
        if (!alive) return ("skip", "there is no desktop layer");
        if (test) return ("skip", "a test run: its desktop layer is an ordinary small window, not one that covers the display");
        if (paused) return ("skip", "Windows is put back, so the desktop layer is left alone");
        if (held) return ("later", "the desktop layer is waiting, hidden, to go under the icons; it is sized to the display as soon as it is there");
        return ("fit", "the desktop layer is sized to the main display as it is now");
    }

    /// Does the layer's content fill the display at its scale? One DIP of rounding either way is a fill. null = unknown.
    public static (bool? Ok, string Reason) PageFill(double w, double h, double scale, int displayW, int displayH, double displayScale)
    {
        if (!(w > 0) || !(h > 0)) return (null, "the desktop layer did not say how big it is");
        if (!(displayW > 0) || !(displayH > 0)) return (null, "the main display reported no size");
        double sf = displayScale > 0 ? displayScale : 1;
        double bw = displayW / sf, bh = displayH / sf;
        bool fills = Math.Abs(w - bw) <= 1 && Math.Abs(h - bh) <= 1;
        bool scaled = !(scale > 0) || Math.Abs(scale - sf) < 0.01;
        string S(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        if (fills && scaled) return (true, $"the desktop layer fills the main display ({S(bw)}x{S(bh)} at scale {S(sf)})");
        return (false, $"the desktop layer is {S(w)}x{S(h)} at scale {(scale > 0 ? S(scale) : "?")} but the main display is {S(bw)}x{S(bh)} at scale {S(sf)}{(fills ? " (it fills the screen but draws at the old scale)" : "")}");
    }

    // ---- type: Chromium's line-height:normal vs WinUI's ----

    /// How many DIP a WinUI TextBlock's natural line is taller than Chromium's line-height:normal for the same Segoe UI
    /// size: Chromium rounds the ascent and the descent each (13 px: 14 + 3 = 17), DirectWrite rounds their sum up (18).
    /// The panes take it off below the line so every pane is as tall as Electron's (the glyphs stay where they were).
    public static double LineTrim(double px)
    {
        const double asc = 2210.0 / 2048, desc = 514.0 / 2048;   // Segoe UI's hhea ascender / descender per em
        double chrome = Math.Round(px * asc, MidpointRounding.AwayFromZero) + Math.Round(px * desc, MidpointRounding.AwayFromZero);
        double winui = Math.Ceiling(px * (asc + desc));
        return Math.Max(0, winui - chrome);
    }

    // ---- geometry ----

    /// The cluster (desktop.css .cluster): one top-left column, 420 DIP wide. A short window (max-height 760) packs it.
    public readonly record struct Cluster(double Top, double Left, double Width, double Gap, double MaxHeight, bool Short,
        double ClockPx, double DatePx, double DateTop, (double T, double R, double B, double L) Pad, double McHeight);

    public static Cluster ClusterFor(double heightDip)
    {
        bool shortWin = heightDip <= 760;
        return shortWin
            ? new Cluster(22, 30, 420, 11, heightDip - 76, true, 48, 13, 3, (11, 13, 12, 13), 132)
            : new Cluster(38, 44, 420, 16, heightDip - 76, false, 76, 14, 6, (14, 16, 15, 16), 240);
    }

    /// A pane's rect (DIP, relative to the layer's client area) on the screen in physical pixels: the layer's origin
    /// plus the DIP-to-physical conversion at the layer's scale (125%: 420 DIP = 525 px).
    public static PxRect ToScreen(double x, double y, double w, double h, int originX, int originY, double scale) =>
        new(originX + (int)Math.Round(x * scale, MidpointRounding.AwayFromZero), originY + (int)Math.Round(y * scale, MidpointRounding.AwayFromZero),
            (int)Math.Round(w * scale, MidpointRounding.AwayFromZero), (int)Math.Round(h * scale, MidpointRounding.AwayFromZero));

    /// The pane rects a page may report: at most 16, keys a-z0-9- up to 40 chars, numeric positions (null = fine).
    public static string? CheckRects(IReadOnlyDictionary<string, (double X, double Y, double W, double H)> rects)
    {
        if (rects.Count > 16) return $"{rects.Count} panes is more than the 16 a page may report";
        foreach (var (k, r) in rects) {
            if (k.Length == 0 || k.Length > 40 || k.Any(ch => !(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))) return $"\"{(k.Length > 40 ? k[..40] : k)}\" is not a pane key (a-z, 0-9 and -, up to 40)";
            if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.W) || !double.IsFinite(r.H)) return $"the {k} pane's position is not a number";
        }
        return null;
    }
}
