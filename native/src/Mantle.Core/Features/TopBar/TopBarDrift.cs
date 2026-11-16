using System;
using System.Linq;

namespace Mantle.Core.Features.TopBar;

// lib/features/topbar.js's PUT BACK, NEVER GIVE UP (TOP-227..232), the pure half. Something outside Mantle can still
// move or resize the bar's window. Every move / resize is checked 250 ms later (the surface hears WM_WINDOWPOSCHANGED:
// no poll); a window whose x, y or width is not the primary display's top-left and width is OFF (the height is not
// compared). The first DriftFree put-backs within DriftWindowMs are immediate; past that the window is put back on a
// backoff (1, 2, 4, 8, 16, then every 30 s) until a re-check finds it in place, never giving up, and the fight is a
// health state while it lasts. Hidden, nothing is put back and a fight pauses; it is put back BEFORE it shows again.

/// <summary>The running record (driftStep's d). Null = none.</summary>
public sealed record DriftState(int N, int Fixes, long Since, string? Last, bool Stuck, int Tries, int RetryMs, long? NextAt);

/// <summary>One step: the next record, put it back now?, the re-check to queue (0 = none), the line to log, and whether
/// a fight just ended (the queued re-check is dropped).</summary>
public sealed record DriftStepResult(DriftState? D, bool Fix, int RetryMs, string? Log, bool Settled = false);

public static class TopBarDrift
{
    public const int CheckMs = 250;
    public const int DriftWindowMs = 10000;
    public const int DriftFree = 3;
    public static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 16000, 30000 };

    /// driftStep: <paramref name="off"/> describes where the window is when it is NOT in place (null = in place),
    /// <paramref name="to"/> where it goes back to; <paramref name="retry"/> is true for the queued re-check.
    public static DriftStepResult Step(DriftState? d, string? off, string to, long now, bool retry)
    {
        if (d is { Stuck: true })
        {
            // a re-check is queued: events in between wait for it (that is the backoff)
            if (!retry) return new(d with { Last = off ?? d.Last }, false, 0, null);
            if (off == null)
                return new(null, false, 0, $"the window is back in place and stayed there, after {d.Fixes} put-backs in {Math.Round((now - d.Since) / 1000.0, MidpointRounding.AwayFromZero)} s", Settled: true);
            int tries = d.Tries + 1;
            int retryMs = BackoffMs[Math.Min(tries, BackoffMs.Length - 1)];
            // one line per step of the backoff; at the 30 s cap the health state carries it (no line every 30 s)
            return new(d with { Last = off, Fixes = d.Fixes + 1, Tries = tries, RetryMs = retryMs, NextAt = now + retryMs }, true, retryMs,
                retryMs != d.RetryMs ? $"the window was out of place again ({off}); put back to {to} (put-back {d.Fixes + 1}), next check in {retryMs / 1000} s" : null);
        }
        if (off == null) return new(d, false, 0, null);
        if (d == null || now - d.Since > DriftWindowMs) d = new DriftState(0, 0, now, null, false, 0, 0, null);
        int n = d.N + 1;
        if (n <= DriftFree) return new(d with { N = n, Last = off, Fixes = d.Fixes + 1 }, true, 0, $"the window drifted to {off}; put back to {to}");
        int first = BackoffMs[0];
        return new(d with { N = n, Last = off, Fixes = d.Fixes + 1, Stuck = true, Tries = 0, RetryMs = first, NextAt = now + first }, true, first,
            $"the window keeps leaving its place ({off}) after {DriftFree} put-backs in {DriftWindowMs / 1000} s; put back to {to} again, then checked after " +
            $"{string.Join(", ", BackoffMs.Select(ms => ms / 1000))} s until it stays");
    }

    /// driftReason: the health reason while the window is fought over (null = no fight, or the bar is not showing).
    /// <paramref name="clock"/> words a time (the user's local time of day).
    public static string? Reason(DriftState? d, bool visible, Func<long, string> clock)
    {
        if (d is not { Stuck: true } || !visible) return null;
        return $"something keeps moving or resizing the top bar's window (last seen at {d.Last}); Mantle put it back {d.Fixes} times since " +
            $"{clock(d.Since)} and tries again every {Math.Round(d.RetryMs / 1000.0, MidpointRounding.AwayFromZero)} s until it stays";
    }

    /// The window's place against where it belongs (physical px): null when x, y and width match (the height is not compared).
    public static string? Off(int x, int y, int w, int h, int wantX, int wantY, int wantW, double scale)
        => x == wantX && y == wantY && w == wantW ? null
         : $"{x},{y} {w}x{h} px on a {wantW} px wide display at {Math.Round(scale * 100, MidpointRounding.AwayFromZero)} %";
}
