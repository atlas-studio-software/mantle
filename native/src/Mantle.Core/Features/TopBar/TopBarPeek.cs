using System;

namespace Mantle.Core.Features.TopBar;

// lib/features/_topbar-peek.js, ported: the PEEK at a top bar hidden by hand. Rest the cursor on the top edge for
// DwellMs and the bar comes out OVER the screen (the strip stays let go, `hidden` untouched); GraceMs after the cursor
// leaves it (no menu open) it goes again. A button held on the edge (a tab click, a window dragged up to snap) disarms
// it until the cursor leaves the edge. Pure: TopBarFeature owns the poll, the window and the log; the tests drive
// Step with fake time, cursor and buttons. Every rect and point here is in DIP of the bar's display.

public readonly record struct PeekPoint(double X, double Y);
public readonly record struct PeekRect(double X, double Y, double W, double H)
{
    public bool Has(PeekPoint p) => p.X >= X && p.X < X + W && p.Y >= Y && p.Y < Y + H;
}
/// <summary>One read of the mouse buttons (GetAsyncKeyState: held now, pressed since the last read).</summary>
public readonly record struct PeekButtons(bool Ok, bool Held = false, bool Pressed = false, string? Reason = null);
/// <summary>What else may forbid a peek (blocker()): names, or null.</summary>
public sealed record PeekGuard(bool Hidden, bool Enabled = true, bool Stopped = false, string? Run = null, string? Lock = null, string? Game = null, string? Fullscreen = null);

public sealed record PeekState(string Phase = "idle", long? Since = null, long? LeftAt = null, string? Why = null);

/// <summary>One poll's inputs (step()'s i).</summary>
public sealed record PeekInput(long Now, PeekPoint? Pt, string? CursorWhy, PeekRect Bounds, PeekRect Bar, bool MenuOpen, string? Guard, bool Claimed, Func<PeekButtons> Buttons);

/// <summary>One poll's outcome: the next state, "show" / "hide" / null, why, and what blocks a peek now.</summary>
public sealed record PeekResult(PeekState St, string? Act, string? Why, string? Blocked);

public static class TopBarPeek
{
    public const double BandDip = 2;
    public const int DwellMs = 500;
    public const int GraceMs = 600;
    public const int PollMs = 100;
    /// VK_LBUTTON, VK_RBUTTON, VK_MBUTTON, VK_XBUTTON1, VK_XBUTTON2: the PHYSICAL buttons (a swapped mouse too).
    public static readonly int[] VkMouse = { 0x01, 0x02, 0x04, 0x05, 0x06 };

    /// TOP-290: the top BandDip of the display, within its width (a monitor beside or above does not count).
    public static bool InBand(PeekPoint? pt, PeekRect b, double band = BandDip)
        => pt is PeekPoint p && p.X >= b.X && p.X < b.X + b.W && p.Y >= b.Y && p.Y < b.Y + band;

    public static string? Blocker(PeekGuard? g)
    {
        if (g == null || !g.Hidden) return "the top bar is not hidden by hand";
        if (g.Stopped) return "the top bar is stopped (Mantle is resting while a game runs, or putting Windows back)";
        if (!g.Enabled) return "the top bar is switched off in the tray";
        if (g.Run != null) return $"{g.Run} is running (a known game)";
        if (g.Lock != null) return $"{g.Lock} is in front (the lock or sign-in screen)";
        if (g.Game != null) return $"a full-screen game is in front ({g.Game})";
        if (g.Fullscreen != null) return $"{g.Fullscreen} is full screen";
        return null;
    }

    public static PeekState Initial() => new();
    /// TOP-293: a new watch starts DISARMED, so Win+Alt+T with the cursor on the edge does not peek straight back.
    public static PeekState StartState() => new("disarmed", null, null, "the bar was just hidden (or Mantle started) with the cursor on the edge; it has to leave the edge first");

    /// One poll (step()). Never mutates its inputs.
    public static PeekResult Step(PeekState? st, PeekInput i)
    {
        var s = st ?? Initial();
        PeekResult Out(PeekState n, string? act = null, string? why = null, string? blocked = null) => new(n, act, why, blocked);
        PeekState Reset(string phase = "idle", string? why = null) => new(phase, null, null, why);

        if (s.Phase == "shown")
        {
            if (i.Guard != null) return Out(Reset(), "hide", i.Guard);
            if (i.Pt is not PeekPoint pt) return Out(Reset(), "hide", $"the cursor could not be read ({i.CursorWhy ?? "no reason"})");
            if (i.MenuOpen || i.Bar.Has(pt) || InBand(pt, i.Bounds)) return Out(s with { LeftAt = null });
            if (s.LeftAt == null) return Out(s with { LeftAt = i.Now });
            long away = i.Now - s.LeftAt.Value;
            if (away >= GraceMs) return Out(Reset(), "hide", $"the cursor left the bar {away} ms ago");
            return Out(s);
        }

        if (i.Pt == null) return Out(Reset(), null, null, $"the cursor could not be read ({i.CursorWhy ?? "no reason"})");
        if (!InBand(i.Pt, i.Bounds) || i.Claimed) return Out(Reset());
        if (s.Phase == "disarmed") return Out(s, null, null, s.Why);
        if (i.Guard != null) return Out(Reset(), null, null, i.Guard);
        PeekButtons b;
        try { b = i.Buttons(); } catch (Exception e) { b = new(false, Reason: $"the reader threw: {e.Message}"); }
        if (!b.Ok) return Out(Reset(), null, null, $"the mouse buttons cannot be read ({b.Reason ?? "no reason"}), so a peek could land in the middle of a drag");
        if (b.Held || (s.Phase == "dwell" && b.Pressed))
        {
            var d = Reset("disarmed", "a mouse button went down on the top edge (a tab click, or a window dragged up to snap or maximise); the cursor has to leave the edge first");
            return Out(d, null, null, d.Why);
        }
        if (s.Phase != "dwell") return Out(new("dwell", i.Now, null, null));
        long rested = i.Now - (s.Since ?? i.Now);
        if (rested >= DwellMs) return Out(new("shown", i.Now, null, null), "show", $"the cursor rested on the top edge for {rested} ms");
        return Out(s);
    }
}
