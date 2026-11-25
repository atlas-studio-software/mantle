using System;
using System.Collections.Generic;

namespace Mantle.Core.Features.Snap;

// lane r9-snap. THE NEVER-STRAND RULE for every window Mantle moves (snap, un-snap, a restore of a remembered
// frame): its title bar must end on a LIVE monitor's work area, so a person can always grab it again.
//
// The geometry itself is the linked sidecar/Snap.cs (PLAN A4: SnapGeom, tested in-process by
// Mantle.Sidecar.Tests/Snap/SnapPlanTests.cs). Two holes in it, found while porting, are why this file exists:
//   1. snap.move's un-snap looks the remembered monitor up by id; when that monitor is GONE (unplugged, a dock
//      undocked) preMon is null and SnapGeom.Translate returns the old frame untouched, so the window is put
//      back on a monitor that no longer exists.
//   2. Translate returns the frame untouched when the monitor is the SAME one, even if its work area shrank
//      or its scale changed since (a resolution change, the taskbar moved), so the frame can hang off it.
// sidecar/*.cs is shared with the Electron build and was not that lane's to change, so the native move path
// (SnapCore) checks every landed frame with Clamp and puts a stranded window back with ONE layouts.place.
// Lane r11-snap-strand closed both holes in Snap.cs itself (for Electron too): Translate now fits a frame whose
// monitor is gone, and one on the same monitor whose title bar is off its work area, by SnapGeom.TitleBarOn, the
// twin of TitleBarOn below (keep the two the same rule). The Clamp check stays as the second guard, for what the
// plan cannot see (an app that refuses the size it was given); on a plan the sidecar made safe, its fast path
// (TitleBarOn on the move's own monitor) holds and it makes no extra call.
//
// All rects are VISIBLE frames (DWMWA_EXTENDED_FRAME_BOUNDS) in physical screen pixels, the sidecar's units.

/// <summary>A rectangle in physical screen pixels: left, top, right, bottom (Win32 RECT, the sidecar's SnapRect).</summary>
public readonly record struct SnapBox(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Empty => Right <= Left || Bottom <= Top;
    public static SnapBox Xywh(int x, int y, int w, int h) => new(x, y, x + w, y + h);
    public override string ToString() => FormattableString.Invariant($"{Left},{Top} {Width}x{Height}");
}

/// <summary>One monitor as the sidecar reports it (snap.monitors): bounds, work area (minus app bars), DPI (96 = 100 %).</summary>
public sealed record SnapScreen(string Id, SnapBox Bounds, SnapBox Work, int Dpi = 96, bool Primary = false);

/// <summary>A remembered frame (an un-snap's pre-snap rect, a restore rect) with the monitor it was on WHEN it was saved.</summary>
/// <param name="Work">That monitor's work area at the time; empty when unknown.</param>
/// <param name="Dpi">That monitor's DPI at the time; 0 when unknown (taken as 96).</param>
public sealed record SnapSaved(SnapBox Frame, string? MonitorId, SnapBox Work, int Dpi);

/// <summary>Where a window should be. <see cref="Changed"/> false = leave it exactly where it is.</summary>
public sealed record SnapLanding(SnapBox Frame, SnapScreen? Screen, bool Changed, string Why);

public static class SnapSafety
{
    /// The strip at the top of a visible frame a person drags it by (DIP).
    public const int TitleBarDip = 32;
    /// How much of that strip, across, must be on the work area (DIP; all of it when the window is narrower).
    public const int GrabDip = 64;
    /// DWM rounds fractional DIP edges either way by a pixel on scaled displays (SnapGeom.Tolerance).
    public const int Tolerance = 2;

    public const string NoMonitor = "no live monitor is known, so the window was left where it is";

    /// <summary>DIP to physical pixels at <paramref name="dpi"/>, half up.</summary>
    public static int Px(int dip, int dpi) => (int)(((long)dip * Dpi(dpi) + 48) / 96);

    static int Dpi(int dpi) => dpi > 0 ? dpi : 96;

    /// <summary>The monitors a window can be put on: an empty work area falls back to the bounds; a monitor with neither is skipped.</summary>
    public static List<SnapScreen> Usable(IReadOnlyList<SnapScreen>? live)
    {
        var list = new List<SnapScreen>();
        if (live == null) return list;
        foreach (var s in live) {
            if (s == null) continue;
            if (!s.Work.Empty) list.Add(s);
            else if (!s.Bounds.Empty) list.Add(s with { Work = s.Bounds });
        }
        return list;
    }

    /// <summary>The top <see cref="TitleBarDip"/> of <paramref name="frame"/> (all of it when it is shorter).</summary>
    public static SnapBox TitleBarOf(SnapBox frame, int dpi) =>
        new(frame.Left, frame.Top, frame.Right, frame.Top + Math.Min(Math.Max(frame.Height, 0), Px(TitleBarDip, dpi)));

    /// <summary>
    /// True when the title bar is on <paramref name="s"/>'s work area: the strip's whole height inside it (within
    /// <see cref="Tolerance"/>) and at least <see cref="GrabDip"/> of its width (all of it when narrower).
    /// </summary>
    public static bool TitleBarOn(SnapBox frame, SnapScreen s)
    {
        if (frame.Empty || s.Work.Empty) return false;
        SnapBox bar = TitleBarOf(frame, s.Dpi);
        if (bar.Top < s.Work.Top - Tolerance || bar.Bottom > s.Work.Bottom + Tolerance) return false;
        int across = Math.Min(bar.Right, s.Work.Right) - Math.Max(bar.Left, s.Work.Left);
        return across >= Math.Min(frame.Width, Px(GrabDip, s.Dpi));
    }

    /// <summary>True when the title bar is on at least one live monitor's work area.</summary>
    public static bool TitleBarOnLive(SnapBox frame, IReadOnlyList<SnapScreen>? live)
    {
        foreach (var s in Usable(live)) if (TitleBarOn(frame, s)) return true;
        return false;
    }

    /// <summary>The monitor holding most of <paramref name="r"/>, else the nearest to its centre (SnapGeom.MonitorFor's rule); null for none.</summary>
    public static SnapScreen? Nearest(SnapBox r, IReadOnlyList<SnapScreen>? live)
    {
        List<SnapScreen> mons = Usable(live);
        SnapScreen? best = null;
        long bestArea = 0;
        foreach (var m in mons) {
            long w = (long)Math.Min(r.Right, m.Bounds.Right) - Math.Max(r.Left, m.Bounds.Left);
            long h = (long)Math.Min(r.Bottom, m.Bounds.Bottom) - Math.Max(r.Top, m.Bounds.Top);
            long a = w > 0 && h > 0 ? w * h : 0;
            if (a > bestArea) { bestArea = a; best = m; }
        }
        if (best != null) return best;
        long bestD = long.MaxValue;
        long cx = ((long)r.Left + r.Right) / 2, cy = ((long)r.Top + r.Bottom) / 2;
        foreach (var m in mons) {
            SnapBox b = m.Bounds.Empty ? m.Work : m.Bounds;
            long dx = Math.Max(0, Math.Max(b.Left - cx, cx - b.Right));
            long dy = Math.Max(0, Math.Max(b.Top - cy, cy - b.Bottom));
            long d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = m; }
        }
        return best;
    }

    /// <summary>Shrinks <paramref name="f"/> to at most the work area and slides it fully onto it (SnapGeom.Fit). An empty frame becomes the centred two thirds.</summary>
    public static SnapBox Fit(SnapBox f, SnapBox work)
    {
        if (f.Empty) return Centred(work);
        int w = Math.Min(f.Width, work.Width), h = Math.Min(f.Height, work.Height);
        int l = Math.Max(work.Left, Math.Min(f.Left, work.Right - w));
        int t = Math.Max(work.Top, Math.Min(f.Top, work.Bottom - h));
        return new SnapBox(l, t, l + w, t + h);
    }

    /// <summary>Two thirds of the work area, centred: SnapGeom's "nothing to go back to" frame.</summary>
    public static SnapBox Centred(SnapBox work)
    {
        int w = work.Width * 2 / 3, h = work.Height * 2 / 3;
        int l = work.Left + (work.Width - w) / 2, t = work.Top + (work.Height - h) / 2;
        return new SnapBox(l, t, l + w, t + h);
    }

    static int Sc(int v, int from, int to) => (int)Math.Round((double)v * to / from, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <paramref name="f"/> (on a work area <paramref name="fromWork"/> at <paramref name="fromDpi"/>) carried onto
    /// <paramref name="to"/>: the same offset into the work area and the same size, both scaled for DPI, then fitted
    /// onto it. Unlike SnapGeom.Translate this ALWAYS fits, the same monitor included.
    /// </summary>
    public static SnapBox Carry(SnapBox f, SnapBox fromWork, int fromDpi, SnapScreen to)
    {
        if (f.Empty) return Centred(to.Work);
        int a = Dpi(fromDpi), b = Dpi(to.Dpi);
        SnapBox origin = fromWork.Empty ? to.Work : fromWork;
        int w = Sc(f.Width, a, b), h = Sc(f.Height, a, b);
        int dx = Sc(f.Left - origin.Left, a, b), dy = Sc(f.Top - origin.Top, a, b);
        return Fit(new SnapBox(to.Work.Left + dx, to.Work.Top + dy, to.Work.Left + dx + w, to.Work.Top + dy + h), to.Work);
    }

    static SnapScreen? Find(List<SnapScreen> mons, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var m in mons) if (m.Id == id) return m;
        return null;
    }

    static string Onto(SnapScreen s) => s.Primary ? "the main monitor" : "the nearest monitor";

    /// <summary>
    /// The check after every move: a frame whose title bar is on a live work area is left EXACTLY where it is;
    /// any other (its monitor gone, off every screen, under an app bar, bigger than the screen after a DPI change)
    /// is fitted onto the monitor holding most of it, else the nearest one. With no live monitor nothing moves.
    /// </summary>
    public static SnapLanding Clamp(SnapBox frame, IReadOnlyList<SnapScreen>? live)
    {
        List<SnapScreen> mons = Usable(live);
        if (mons.Count == 0) return new SnapLanding(frame, null, false, NoMonitor);
        if (!frame.Empty) {
            foreach (var s in mons)
                if (TitleBarOn(frame, s)) return new SnapLanding(frame, s, false, "its title bar is on a live monitor");
        }
        SnapScreen to = Nearest(frame, mons)!;
        SnapBox f = Fit(frame, to.Work);
        string why = frame.Empty
            ? "it had no size, so it was given two thirds of " + Onto(to)
            : "its title bar was off every live monitor's work area, so it was brought back onto " + Onto(to);
        return new SnapLanding(f, to, f != frame, why);
    }

    /// <summary>
    /// A remembered frame put back (restore rect bookkeeping). Its monitor still there with the same work area and
    /// scale, and the title bar on a live work area: exactly where it was. Its monitor still there but changed:
    /// carried onto it again (DPI-scaled, fitted). Its monitor gone: carried onto the live monitor nearest to where
    /// it was. The answer always passes <see cref="TitleBarOnLive"/> when any monitor is live.
    /// </summary>
    public static SnapLanding Restore(SnapSaved saved, IReadOnlyList<SnapScreen>? live)
    {
        List<SnapScreen> mons = Usable(live);
        if (mons.Count == 0) return new SnapLanding(saved.Frame, null, false, NoMonitor);
        SnapScreen? same = Find(mons, saved.MonitorId);
        if (same != null) {
            bool scaleSame = same.Dpi == Dpi(saved.Dpi);
            bool workSame = saved.Work.Empty || same.Work == saved.Work;
            if (scaleSame && workSame && !saved.Frame.Empty && TitleBarOnLive(saved.Frame, mons))
                return new SnapLanding(saved.Frame, same, false, "back exactly where it was");
            SnapBox f = Carry(saved.Frame, saved.Work, saved.Dpi, same);
            string why = !scaleSame ? "its monitor's scale changed since, so it was resized for it and kept on it"
                       : !workSame ? "its monitor's work area changed since, so it was kept on it"
                       : "its title bar would have been off the screen, so it was kept on its monitor";
            return new SnapLanding(f, same, f != saved.Frame, why);
        }
        SnapScreen to = Nearest(saved.Frame, mons)!;
        SnapBox g = saved.Work.Empty ? Fit(saved.Frame, to.Work) : Carry(saved.Frame, saved.Work, saved.Dpi, to);
        return new SnapLanding(g, to, g != saved.Frame, "the monitor it was on is gone, so it was brought onto " + Onto(to));
    }
}
