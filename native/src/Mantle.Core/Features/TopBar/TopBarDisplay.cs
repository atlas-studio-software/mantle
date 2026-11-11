using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.TopBar;

// lib/features/_topbar-display.js, ported: the top bar's display-change handling, pure. A 125 % -> 150 % change fires
// several display events, and every re-reserve of the app bar moves the work area, which fires another. So: the first
// change of a burst runs at once and the rest become ONE trailing run after a quiet gap (Burst), and a work-area-only
// change Mantle caused itself, or one that leaves the work area at the bar's bottom, needs nothing (Verdict).

/// <summary>One display event as the surface saw it (WM_DISPLAYCHANGE, WM_DPICHANGED, SPI_SETWORKAREA).</summary>
/// <param name="Kind">"metrics" | "added" | "removed".</param>
/// <param name="Changed">The changed metrics: "bounds", "workArea", "scaleFactor", "rotation".</param>
/// <param name="Primary">The event's display is the primary one (where the bar lives).</param>
/// <param name="WorkOk">The primary's work area starts at the bar's bottom.</param>
public sealed record TopBarDisplayEvent(string Kind, IReadOnlyList<string>? Changed, bool Primary, bool WorkOk, long OwnAt, long Now);

public static class TopBarDisplay
{
    public const int SelfMs = 2000;   // a work-area change this soon after Mantle's own reserve/release is Mantle's
    public const int QuietMs = 400;

    static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        ["bounds"] = "the resolution changed", ["workArea"] = "the work area moved", ["scaleFactor"] = "the scale changed", ["rotation"] = "the screen turned",
    };

    /// TOP-236..238: run or not, and why (plain words, no names).
    public static (bool Run, string Why) Verdict(TopBarDisplayEvent x)
    {
        if (x.Kind == "added") return (true, "a display was connected");
        if (x.Kind == "removed") return (true, "a display was disconnected");
        var ch = x.Changed ?? Array.Empty<string>();
        if (ch.Count == 0) return (true, "the display changed");
        var words = string.Join(", ", ch.Select(m => Words.TryGetValue(m, out var w) ? w : $"the display's {m} changed").Distinct());
        if (!ch.All(m => m == "workArea")) return (true, words);
        if (!x.Primary) return (false, "the work area moved on another screen (the bar is on the main one)");
        long d = x.Now - x.OwnAt;
        if (x.OwnAt > 0 && d >= 0 && d < SelfMs) return (false, "Mantle moved the work area itself (its own app bar)");
        if (x.WorkOk) return (false, "the work area moved, but still starts at the bar's bottom");
        return (true, "the work area moved off the bar's bottom");
    }
}

/// <summary>
/// makeBurst (TOP-234/235): the first change runs at once (the bar never waits); the rest of the burst become ONE more run
/// after <see cref="TopBarDisplay.QuietMs"/> with nothing new, its reason "N changes: a, b" with each reason once.
/// </summary>
public sealed class TopBarBurst
{
    readonly Action<string> _run;
    readonly Func<int, Action, IDisposable> _later;
    readonly int _quiet;
    readonly List<string> _pending = new();
    IDisposable? _timer;
    bool _hot;

    public TopBarBurst(Action<string> run, Func<int, Action, IDisposable> later, int quietMs = TopBarDisplay.QuietMs) { _run = run; _later = later; _quiet = quietMs; }

    /// "now" or "later".
    public string Fire(string why)
    {
        if (!_hot)
        {
            _hot = true;
            Safe(why);
            _timer = _later(_quiet, Settle);
            return "now";
        }
        _pending.Add(why);
        _timer?.Dispose();
        _timer = _later(_quiet, Settle);
        return "later";
    }

    void Settle()
    {
        _timer = null;
        if (_pending.Count > 0)
        {
            var parts = _pending.SelectMany(p => p.Split(", ")).Distinct().ToArray();
            var why = _pending.Count == 1 ? _pending[0] : $"{_pending.Count} changes: {string.Join(", ", parts)}";
            _pending.Clear();
            Safe(why);
            _timer = _later(_quiet, Settle);   // hot until a full quiet gap with nothing new
            return;
        }
        _hot = false;
    }

    void Safe(string why) { try { _run(why); } catch (Exception) { /* the next event tries again */ } }

    public void Stop() { _timer?.Dispose(); _timer = null; _pending.Clear(); _hot = false; }
}
