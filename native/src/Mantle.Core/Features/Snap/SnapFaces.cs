using System;
using System.Collections.Generic;

namespace Mantle.Core.Features.Snap;

/// <summary>One Win+Z layout (ui/snap/layouts.js): a grid and its zones as cells [x, y, w, h], with their spoken names.</summary>
public sealed record SnapLayout(string Id, string Label, int Cols, int Rows, IReadOnlyList<int[]> Zones, IReadOnlyList<string> Names);

/// <summary>A zone left over after a pick: what Snap Assist fills next (layouts.js#rest).</summary>
public sealed record SnapRestZone(string Layout, int Index, int Cols, int Rows, int[] Cell, string Name);

/// <summary>
/// The snap window's three faces as pure numbers (lib/features/snap.js#notice, #begin, #picker, PICKER, NOTICE;
/// ui/snap/layouts.js; ui/snap/snap.js#stepZone, #stepRow), lane r9-snap, so the surface stays thin. All rects here are
/// DIP (the surface converts a monitor's physical work area first, snap.js#toDip). Rounding is JavaScript's Math.round.
/// </summary>
public static class SnapFaces
{
    public const int PickerW = 400, PickerH = 268;          // the picker face plus its 16 px shadow margin
    public const int NoticeW = 440, NoticeH = 132, NoticeMax = 260, NoticeAbove = 72;
    public const int NoticeMinMs = 4500, NoticeMaxMs = 9000;
    public const int LayoutCols = 3;                         // tiles per row in the picker

    public static readonly IReadOnlyList<SnapLayout> Layouts = new[] {
        new SnapLayout("halves", "Halves", 2, 1, new[] { new[] { 0, 0, 1, 1 }, new[] { 1, 0, 1, 1 } }, new[] { "left half", "right half" }),
        new SnapLayout("thirds", "Thirds", 3, 1, new[] { new[] { 0, 0, 1, 1 }, new[] { 1, 0, 1, 1 }, new[] { 2, 0, 1, 1 } },
                       new[] { "left third", "middle third", "right third" }),
        new SnapLayout("wide", "Two thirds and one third", 3, 1, new[] { new[] { 0, 0, 2, 1 }, new[] { 2, 0, 1, 1 } },
                       new[] { "left two thirds", "right third" }),
        new SnapLayout("quarters", "Quarters", 2, 2, new[] { new[] { 0, 0, 1, 1 }, new[] { 1, 0, 1, 1 }, new[] { 0, 1, 1, 1 }, new[] { 1, 1, 1, 1 } },
                       new[] { "top-left quarter", "top-right quarter", "bottom-left quarter", "bottom-right quarter" }),
        new SnapLayout("columns", "Four columns", 4, 1, new[] { new[] { 0, 0, 1, 1 }, new[] { 1, 0, 1, 1 }, new[] { 2, 0, 1, 1 }, new[] { 3, 0, 1, 1 } },
                       new[] { "first column", "second column", "third column", "fourth column" }),
    };

    /// JavaScript's Math.round: half up, towards +infinity (-2.5 -> -2).
    public static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    public static SnapLayout? ById(string? id)
    {
        foreach (var l in Layouts) if (l.Id == id) return l;
        return null;
    }

    /// <summary>The zones of <paramref name="layoutId"/> other than <paramref name="zoneIndex"/>, in reading order.</summary>
    public static List<SnapRestZone> Rest(string? layoutId, int zoneIndex)
    {
        var out_ = new List<SnapRestZone>();
        SnapLayout? l = ById(layoutId);
        if (l == null) return out_;
        for (int i = 0; i < l.Zones.Count; i++)
            if (i != zoneIndex) out_.Add(new SnapRestZone(l.Id, i, l.Cols, l.Rows, (int[])l.Zones[i].Clone(), l.Names[i]));
        return out_;
    }

    /// <summary>SNAP-058: 440x132, centred on the work area, 72 above its bottom.</summary>
    public static SnapBox NoticeRect(SnapBox work) =>
        SnapBox.Xywh(JsRound(work.Left + (work.Width - NoticeW) / 2.0), JsRound(work.Top + work.Height - NoticeH - (double)NoticeAbove), NoticeW, NoticeH);

    /// <summary>SNAP-058: the page measured <paramref name="need"/> DIP of text: only ever taller, upward (the bottom stays), never past 260.</summary>
    public static SnapBox NoticeGrow(SnapBox rect, double need)
    {
        if (!(need > rect.Height && need <= 2000)) return rect;
        int h = (int)Math.Min(NoticeMax, need);
        return SnapBox.Xywh(rect.Left, rect.Top - (h - rect.Height), rect.Width, h);
    }

    /// <summary>SNAP-059: 4.5 s, or 2 s + 55 ms per character of title and text, at most 9 s.</summary>
    public static int NoticeMs(string? title, string? text)
    {
        long chars = (title ?? "").Length + (long)(text ?? "").Length;
        return (int)Math.Min(NoticeMaxMs, Math.Max(NoticeMinMs, 2000 + chars * 55));
    }

    /// <summary>SNAP-067: Windows 11's flyout spot, under the maximize button, kept inside the work area.</summary>
    public static SnapBox PickerRect(SnapBox frame, SnapBox work)
    {
        int x = Math.Max(work.Left, Math.Min(frame.Left + frame.Width - PickerW - 24, work.Left + work.Width - PickerW));
        int y = Math.Max(work.Top, Math.Min(frame.Top + 28, work.Top + work.Height - PickerH));
        return SnapBox.Xywh(x, y, PickerW, PickerH);
    }

    /// <summary>SNAP-071: Right/Tab (+1) or Left/Shift+Tab (-1) through every zone of every layout in reading order, wrapping.</summary>
    public static (int Layout, int Zone) StepZone(int layout, int zone, int d)
    {
        var flat = new List<(int, int)>();
        for (int li = 0; li < Layouts.Count; li++)
            for (int zi = 0; zi < Layouts[li].Zones.Count; zi++) flat.Add((li, zi));
        int i = 0;
        for (int k = 0; k < flat.Count; k++) if (flat[k].Item1 == layout && flat[k].Item2 == zone) i = k;
        return flat[((i + d) % flat.Count + flat.Count) % flat.Count];
    }

    /// <summary>SNAP-071: Down (+1) / Up (-1) a row of three layouts, the zone index clamped; null = no row there (nothing moves).</summary>
    public static (int Layout, int Zone)? StepRow(int layout, int zone, int d)
    {
        int li = layout + d * LayoutCols;
        if (li < 0 || li >= Layouts.Count) return null;
        return (li, Math.Min(zone, Layouts[li].Zones.Count - 1));
    }
}
