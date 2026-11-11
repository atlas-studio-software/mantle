using System;
using System.Collections.Generic;
using System.Text;

namespace Mantle.Core.Features.Desktop;

// HIDING THE WIDGETS UNDER WINDOWS (DESK-091..103; lib/features/widgethide.js + sidecar/WidgetCover.cs + desktop.js
// applyCover, lane native-r7-desktop-4). The pure part: which windows cover a widget, the answer the panes draw from, the
// fade timings, and the throttle the watch runs on. The watch itself (Mantle.App/Surfaces/Desktop/CoverWatch.cs) reads
// the live window list on WinEvents only: nothing runs while no window changes.

/// <summary>A widget's place in PHYSICAL screen pixels (l, t inclusive; r, b exclusive), keyed by pane.</summary>
public sealed record CoverArea(string Key, int L, int T, int R, int B);

/// <summary>One top-level window as the cover check reads it (top of the z-order first).</summary>
public sealed class CoverWin
{
    public long Hwnd;
    public string Cls = "", Title = "";
    public bool Visible = true, Iconic, Cloaked;
    public long ExStyle;
    public int Pid;
    /// <summary>-1: not a layered window with an alpha; else its alpha 0..255.</summary>
    public int Alpha = -1;
    public int L, T, R, B;
    /// <summary>Explorer's desktop window (Progman): everything after it is behind the desktop.</summary>
    public bool Desktop;
}

/// <summary>What the panes draw from: unknown is "nothing covered" (fail open).</summary>
public sealed record CoverAnswer(bool On, IReadOnlyDictionary<string, bool> Covered, IReadOnlyDictionary<string, string?> By, string? Reason)
{
    public static CoverAnswer Open(bool on, string? reason) =>
        new(on, new Dictionary<string, bool>(), new Dictionary<string, string?>(), reason);

    public bool Is(string key) => On && Covered.TryGetValue(key, out bool c) && c;

    /// "clock=1,mc=0,stats=0,strip=0": the watch says something only when this changes.
    public string Sig()
    {
        var keys = new List<string>(Covered.Keys);
        keys.Sort(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var k in keys) { if (sb.Length > 0) sb.Append(','); sb.Append(k).Append('=').Append(Covered[k] ? '1' : '0'); }
        return sb.ToString();
    }

    /// For the log: "clock (by Notepad), stats (by Notepad)" or "nothing".
    public string Words()
    {
        var keys = new List<string>(Covered.Keys);
        keys.Sort(StringComparer.Ordinal);
        var parts = new List<string>();
        foreach (var k in keys) if (Is(k)) parts.Add(By.TryGetValue(k, out var b) && !string.IsNullOrEmpty(b) ? $"{k} (by {b})" : k);
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}

public static class WidgetCover
{
    /// <summary>A window covers a widget when the two overlap by at least this much each way (physical px).</summary>
    public const int MinOverlap = 2;
    /// <summary>desktop.css: out on the bar's exit timing (--lg-dur-out), in on its entrance (--lg-dur), --lg-ease.</summary>
    public const int FadeOutMs = 260, FadeInMs = 560;
    public static readonly (double X1, double Y1, double X2, double Y2) Ease = (0.22, 0.68, 0.3, 1);
    /// <summary>The watch's throttle: the first window event of a burst decides 100 ms later (and again every 100 ms while
    /// events keep coming, so a dragged window fades a widget while it moves); one settle check 1 s after the last decision
    /// catches what has no event of its own (a window fading its alpha in). No event, no work.</summary>
    public const int ThrottleMs = 100, SettleMs = 1000;
    /// <summary>The panes the layer reports (DeskPanes' Tags), in the page's order.</summary>
    public static readonly string[] Keys = { "clock", "stats", "mc", "strip" };

    public const string Off = "switched off, so the widgets always show";
    public const string WidgetsOff = "the widgets are switched off on the desktop menu, so there is nothing to hide";
    public const string NotShown = "the desktop layer is not shown, so nothing is watched";
    public const string NoLayer = "the desktop layer is not open, so there are no widgets to hide";
    public const string NoAreas = "waiting for the desktop layer to say where its widgets are";
    public static string WatchFailed(string err) => $"the desktop widgets stay up under windows because Mantle could not watch the windows: {err}";
    public static string CheckFailed(string err) => $"the covered-widget check failed, so every widget shows: {err}";

    /// Shell furniture and transient system UI that never means "an app is over the desktop" (sidecar/WidgetCover.cs).
    static readonly HashSet<string> Furniture = new(StringComparer.Ordinal) {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "#32768" /* a menu */, "tooltips_class32", "SysShadow", "MSCTFIME UI", "IME", "Default IME",
        "ForegroundStaging", "Shell_InputSwitchTopLevelWindow", "EdgeUiInputTopWndClass",
    };

    /// Mantle's APP windows cover (sidecar/AltTab.cs MantleAppTitles); its shell UI (the bar, the top bar, pop-ups, the
    /// desktop layer and its menu) never does.
    static readonly HashSet<string> MantleApps = new(StringComparer.Ordinal) { "Mantle Settings", "Mantle Task Manager", "Mantle Files", "Mantle Player" };

    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;

    /// Null when the window can cover a widget, else the plain-words reason it is ignored.
    public static string? Ignored(CoverWin w, int ownPid)
    {
        if (!w.Visible) return "not visible";
        if (w.Iconic) return "minimised";
        if (w.Cloaked) return "cloaked (another desktop, or not shown by Windows)";
        if ((w.ExStyle & WS_EX_TOOLWINDOW) != 0) return "a tool window (tooltip, palette, helper)";
        if ((w.ExStyle & WS_EX_TRANSPARENT) != 0) return "click-through overlay";
        if (w.Alpha == 0) return "fully transparent";
        if (Furniture.Contains(w.Cls)) return "Windows shell furniture (" + w.Cls + ")";
        if (w.Pid != 0 && w.Pid == ownPid && !MantleApps.Contains(w.Title)) return "Mantle's own shell UI";
        if (w.R - w.L <= 1 || w.B - w.T <= 1) return "zero size";
        return null;
    }

    /// The decision: every area starts uncovered; walking the z-order top down to the desktop, the first window that
    /// covers an area names it. <paramref name="why"/>, when given, gets one line per window read.
    public static CoverAnswer Decide(IReadOnlyList<CoverArea> areas, IEnumerable<CoverWin> wins, int ownPid, List<string>? why = null)
    {
        var covered = new Dictionary<string, bool>(StringComparer.Ordinal);
        var by = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var a in areas) { covered[a.Key] = false; by[a.Key] = null; }
        bool behind = false;
        foreach (var w in wins) {
            if (w.Desktop) { behind = true; why?.Add($"{w.Hwnd} {w.Cls}: the desktop"); continue; }
            string? skip = behind ? "behind the desktop" : Ignored(w, ownPid);
            var hits = new List<string>();
            if (skip == null) {
                foreach (var a in areas) {
                    int ow = Math.Min(w.R, a.R) - Math.Max(w.L, a.L), oh = Math.Min(w.B, a.B) - Math.Max(w.T, a.T);
                    if (ow < MinOverlap || oh < MinOverlap) continue;
                    hits.Add(a.Key);
                    if (!covered[a.Key]) { covered[a.Key] = true; by[a.Key] = w.Title.Length > 0 ? w.Title : w.Cls; }
                }
            }
            why?.Add($"{w.Hwnd} {w.Cls} \"{w.Title}\" {w.L},{w.T},{w.R},{w.B}: {skip ?? (hits.Count == 0 ? "covers nothing" : "covers " + string.Join(",", hits))}");
        }
        return new CoverAnswer(true, covered, by, null);
    }

    /// The pane rects the layer reports (physical px, x/y/w/h) as areas; a pane with no size (a hidden strip, a pane the
    /// widgets switch hid) is left out: nothing can cover what is not there.
    public static IReadOnlyList<CoverArea> Areas(IEnumerable<(string Key, int X, int Y, int W, int H)> panes)
    {
        var o = new List<CoverArea>();
        foreach (var p in panes) if (p.W >= 1 && p.H >= 1) o.Add(new CoverArea(p.Key, p.X, p.Y, p.X + p.W, p.Y + p.H));
        return o;
    }

    /// Why nothing is watched right now (every widget shows), or null when the watch should run.
    public static string? Idle(bool featureOn, bool layerOpen, bool shown, bool widgetsOn, int areaCount)
    {
        if (!featureOn) return Off;
        if (!layerOpen) return NoLayer;
        if (!shown) return NotShown;
        if (!widgetsOn) return WidgetsOff;
        if (areaCount == 0) return NoAreas;
        return null;
    }

    /// DESK-103: a covered clock or System tile stops once its fade-out has FINISHED (never before, so digits that can
    /// still be seen never freeze); uncovered, it paints / reads first, in the same step that starts the fade in.
    public static bool Parked(bool covered, bool fadedOut) => covered && fadedOut;
}

/// <summary>
/// The watch's throttle as a pure state machine (the hook thread drives it with real timers; the unit tests with
/// made-up times). An event arms one decision ThrottleMs later; events while it waits ride along; after a decision with
/// more events behind it, another ThrottleMs; after a quiet decision, one settle check SettleMs later; then nothing.
/// </summary>
public sealed class CoverThrottle
{
    public enum Next { None, Throttle, Settle }
    bool _waiting, _dirty;

    /// A window event: arm a decision if none is waiting (the timer to start), else ride along.
    public Next Event()
    {
        if (_waiting) { _dirty = true; return Next.None; }
        _waiting = true; _dirty = false;
        return Next.Throttle;
    }

    /// The throttle timer fired (the caller decides now): the next timer, if any.
    public Next Fired()
    {
        _waiting = false;
        if (_dirty) { _dirty = false; _waiting = true; return Next.Throttle; }
        return Next.Settle;
    }

    /// The settle timer fired (the caller decides once more): nothing follows.
    public Next Settled() => Next.None;

    public bool Waiting => _waiting;
}
