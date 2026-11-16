using System;
using System.Collections.Generic;
using System.Text;

namespace Mantle.Core.Features.WindowList;

/// <summary>
/// The strip's fit rule (ui/bar/bar.js measure / keepFor / placeStrip / decide / applyOverflow; BAR-056, BAR-235..238):
/// the icons stay centred on the bar while they fit the free span, slide by the minimum that keeps them inside it, and
/// only when they cannot fit the whole span do the last of them (the TAIL of the order) wait behind a "+N" button at the
/// end of the strip. The pure parts, so the unit tests prove them without a window; the UI side is Mantle.App's
/// Tasks/BarWindow.Overflow.cs. Every width is in DIP.
/// </summary>
public static class StripFit
{
    /// bar.css --cell, --cell-gap. The divider is 13 DIP in the native cell (a 1 DIP hairline, 3 before, 9 after:
    /// Electron's 7 DIP .div plus the extra 6 DIP gap it sits between, so the strips measure the same).
    public const double Cell = 36, Gap = 6, Div = 13, Wall = 8;

    /// The room the strip is placed in: <paramref name="SpanL"/> / <paramref name="SpanR"/> are the free span's edges
    /// (8 DIP inside the left cluster and the first right control), <paramref name="Centre"/> the bar's centre, all in
    /// the bar's own coordinates.
    public readonly record struct Room(double Centre, double SpanL, double SpanR)
    {
        public double Span => Math.Max(0, SpanR - SpanL);

        /// bar.js measure(): the span between the left cluster's right edge and the wall's left edge, 8 DIP inside each.
        public static Room Between(double barWidth, double leftEnd, double wallLeft) =>
            new(barWidth / 2, leftEnd + Wall, wallLeft - Wall);
    }

    /// The answer: how many icons stay on the strip (<paramref name="Keep"/>), how many wait behind "+N"
    /// (<paramref name="Hidden"/>), the strip's width with the button, and how far its centre sits off the bar's.
    public readonly record struct Fit(int Keep, int Hidden, double Width, double Shift);

    /// One cell's width with what goes before it: the gap, and the hairline before the first running-only app.
    public static double Add(IReadOnlyList<bool> divider, int i) => (i > 0 ? Gap : 0) + Cell + (divider[i] ? Div : 0);

    /// bar.js needWidth(): every icon on the strip.
    public static double Need(IReadOnlyList<bool> divider)
    {
        double w = 0;
        for (int i = 0; i < divider.Count; i++) w += Add(divider, i);
        return w;
    }

    /// bar.js keepFor(): all of them while they fit the span, else as many as fit with room kept for "+N".
    public static int Keep(IReadOnlyList<bool> divider, double span)
    {
        if (divider.Count == 0 || Need(divider) <= span) return divider.Count;
        double room = span - Cell - Gap, used = 0;
        int n = 0;
        for (int i = 0; i < divider.Count; i++) {
            double add = Add(divider, i);
            if (used + add > room) break;
            used += add; n++;
        }
        return n;
    }

    /// bar.js stripWidth(): the first <paramref name="keep"/> icons, and "+N" when any wait behind it.
    public static double Width(IReadOnlyList<bool> divider, int keep)
    {
        if (keep >= divider.Count) return Need(divider);
        double used = 0;
        for (int i = 0; i < keep; i++) used += Add(divider, i);
        return keep > 0 ? used + Gap + Cell : Cell;
    }

    /// bar.js decide() + placeStrip(): centred while it fits, slid by the minimum that keeps it inside the span; with
    /// apps behind "+N", filled from the span's left edge.
    public static Fit Decide(IReadOnlyList<bool> divider, Room room)
    {
        if (divider.Count == 0) return new Fit(0, 0, 0, 0);
        int keep = Keep(divider, room.Span);
        bool behind = keep < divider.Count;
        double w = Width(divider, keep);
        double l = room.Centre - w / 2, r = room.Centre + w / 2, shift = 0;
        if (behind) shift = room.SpanL - l;
        else if (l < room.SpanL) shift = room.SpanL - l;   // pushed right, off the launcher
        else if (r > room.SpanR) shift = room.SpanR - r;   // pushed left, off the clock (the usual one)
        return new Fit(keep, divider.Count - keep, w, shift);
    }

    /// The "+N" button's words: its label and its tooltip / accessible name (bar.js applyOverflow).
    public static string Label(int hidden) => "+" + hidden;
    public static string Tip(int hidden) => hidden + (hidden == 1 ? " more app" : " more apps") + " that do not fit";

    /// bar.js ovfSig: which apps wait behind "+N" and their running / active marks. The open panel is rebuilt only when
    /// this changes (an unrelated update leaves it alone, BAR-238).
    public static string Sig(IReadOnlyList<TaskItem> items, int keep)
    {
        var sb = new StringBuilder();
        for (int i = Math.Max(0, keep); i < items.Count; i++) {
            var it = items[i];
            sb.Append(it.Id);
            if (it.Windows.Count > 0) sb.Append('*');
            foreach (var w in it.Windows) if (w.Active) { sb.Append('!'); break; }
            sb.Append('|');
        }
        return sb.ToString();
    }

    /// The panel's columns: up to 4 of 76 DIP; fewer apps make a narrower panel, never an empty column (bar.js buildOvfPop).
    public const double TileW = 76, TileGap = 2, TileH = 64, PanelPad = 12, MaxCols = 4;
    public static int Columns(int hidden) => (int)Math.Max(1, Math.Min(MaxCols, hidden));
    public static (double W, double H) PanelSize(int hidden)
    {
        int cols = Columns(hidden), rows = (Math.Max(1, hidden) + cols - 1) / cols;
        return (cols * TileW + (cols - 1) * TileGap + 2 * PanelPad, rows * TileH + (rows - 1) * TileGap + 2 * PanelPad);
    }

    /// The "+N" tail rule for drag to reorder (bar.js dragLift: d.list leaves out the ones behind +N, BAR-242): how many of
    /// a group that starts at <paramref name="groupStart"/> in the strip's order are on the strip with <paramref name="keep"/>
    /// kept. The hidden ones are always the order's tail, so the group's visible ones are its first ones.
    public static int OnStrip(int groupStart, int groupCount, int keep) => Math.Clamp(keep - groupStart, 0, groupCount);
}
