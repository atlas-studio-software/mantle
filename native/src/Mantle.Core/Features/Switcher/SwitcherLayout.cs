using System;

namespace Mantle.Core.Features.Switcher;

/// <summary>
/// The Alt+Tab switcher's geometry: a port of Electron's <c>ui/switcher/layout.js</c> (one pure function
/// shared by the overlay window, the page and the arrow keys). Everything is DIP. Tiles go in rows, left to
/// right (index i is in row i / cols, exactly what the sidecar's SwitchMath.Move assumes for Up/Down), so a
/// row is never centred. Biggest tiles first, columns balanced over the rows; too tall, the tiles shrink in
/// steps; still too tall at the smallest tile, the grid shows whole rows and scrolls by whole rows (a DWM
/// thumbnail cannot be clipped). Never throws: nonsense in gives a sane answer out.
/// Ported with lib/switcher.test.js layout() and liveLayout() (tests/Mantle.Core.Tests/Features/Switcher).
/// Two guards JS does not have: a count is capped at 1e6 and an infinite work area reads as the default
/// (JS would loop on Infinity); every finite input gives JS's answer.
/// </summary>
public static class SwitcherLayout
{
    public const int Margin = 18, Pad = 14, Gap = 8, TileMax = 240, TileMin = 136, Step = 4, Inset = 6, CapH = 30;
    public const double Aspect = 0.625, MaxW = 0.92, MaxH = 0.84;
    public const int EmptyW = 380, EmptyH = 116, NoticeW = 460, NoticeH = 104;

    /// <summary>JS Math.round: halves go up (C#'s Math.Round would round 142.5 to 142).</summary>
    internal static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    sealed class T
    {
        public int TileW, PreviewW, PreviewH, TileH, Cols, Rows, GridW, GridH;
    }

    static T TileOf(int tw)
    {
        int pw = tw - 2 * Inset;
        int ph = JsRound(pw * Aspect);
        return new T { TileW = tw, PreviewW = pw, PreviewH = ph, TileH = Inset + ph + CapH };
    }

    static T Grid(int n, int tw, double availW)
    {
        var t = TileOf(tw);
        int maxCols = Math.Max(1, (int)Math.Floor((availW - 2 * Pad + Gap) / (tw + Gap)));
        int cols = Math.Min(n, maxCols);
        int rows = (n + cols - 1) / cols;
        cols = (n + rows - 1) / rows;                       // balance the rows: 7 over 6 columns = 4 + 3
        t.Cols = cols;
        t.Rows = rows;
        t.GridW = cols * tw + (cols - 1) * Gap;
        t.GridH = rows * t.TileH + (rows - 1) * Gap;
        return t;
    }

    static SwitcherGeometry Finish(T t, int n, int visibleRows)
    {
        int vr = Math.Min(t.Rows, visibleRows);
        int gridH = vr * t.TileH + (vr - 1) * Gap;
        int panelW = 2 * Pad + t.GridW, panelH = 2 * Pad + gridH;
        return new SwitcherGeometry(n, t.Cols, t.Rows, vr, vr < t.Rows, t.TileW, t.TileH, t.PreviewW, t.PreviewH,
            t.GridW, gridH, panelW, panelH, panelW + 2 * Margin, panelH + 2 * Margin);
    }

    static int Count(double n) => double.IsNaN(n) || n <= 0 ? 0 : (int)Math.Min(1e6, Math.Floor(n));
    static double Dim(double v, double dflt) => v > 0 && !double.IsInfinity(v) ? v : dflt;

    /// <summary>n windows in a work area of workW x workH DIP -> the whole geometry.</summary>
    public static SwitcherGeometry Layout(double n, double workW, double workH)
    {
        int count = Count(n);
        workW = Dim(workW, 1280);
        workH = Dim(workH, 720);
        double availW = workW * MaxW - 2 * Margin, availH = workH * MaxH - 2 * Margin;
        if (count == 0) return SwitcherGeometry.Empty;
        for (int tw = TileMax; tw >= TileMin; tw -= Step)
        {
            var t = Grid(count, tw, availW);
            if (2 * Pad + t.GridH <= availH) return Finish(t, count, t.Rows);
        }
        var small = Grid(count, TileMin, availW);
        int fit = Math.Max(1, (int)Math.Floor((availH - 2 * Pad + Gap) / (small.TileH + Gap)));
        return Finish(small, count, fit);
    }

    /// <summary>
    /// The layout for a list that CHANGED while the switcher is up: tiles keep their size and column count, so
    /// a window that comes or goes fills or empties a slot. A fresh <see cref="Layout"/> when the old grid
    /// would not fit the screen, or a grid that did not scroll gets too tall; n = 0 is the empty panel.
    /// </summary>
    public static SwitcherGeometry Reflow(double n, double workW, double workH, SwitcherGeometry? prev)
    {
        int count = Count(n);
        if (prev is null || !(prev.Cols > 0) || !(prev.TileW > 0) || count == 0) return Layout(count, workW, workH);
        workW = Dim(workW, 1280);
        workH = Dim(workH, 720);
        double availW = workW * MaxW - 2 * Margin, availH = workH * MaxH - 2 * Margin;
        var t = TileOf(prev.TileW);
        int cols = Math.Min(count, prev.Cols);
        int rows = (count + cols - 1) / cols;
        t.Cols = cols;
        t.Rows = rows;
        t.GridW = cols * t.TileW + (cols - 1) * Gap;
        t.GridH = rows * t.TileH + (rows - 1) * Gap;
        if (2 * Pad + t.GridW > availW) return Layout(count, workW, workH);
        if (2 * Pad + t.GridH <= availH) return Finish(t, count, rows);
        if (!prev.Scroll) return Layout(count, workW, workH);
        return Finish(t, count, Math.Max(1, (int)Math.Floor((availH - 2 * Pad + Gap) / (t.TileH + Gap))));
    }

    /// <summary>The "could not switch" notice: a fixed 460 x 104 DIP panel plus the shadow margin.</summary>
    public static (int PanelW, int PanelH, int Margin, int W, int H) Notice() =>
        (NoticeW, NoticeH, Margin, NoticeW + 2 * Margin, NoticeH + 2 * Margin);

    /// <summary>The row the grid starts at so <paramref name="sel"/> is visible, moving as little as possible.</summary>
    public static int FirstRow(SwitcherGeometry? lay, int sel, int current)
    {
        if (lay is null || !lay.Scroll || sel < 0) return 0;
        int row = sel / lay.Cols;
        int first = Math.Max(0, Math.Min(current, lay.Rows - lay.VisibleRows));
        if (row < first) return row;
        if (row >= first + lay.VisibleRows) return row - lay.VisibleRows + 1;
        return first;
    }
}

/// <summary>layout.js finish(): the geometry main sizes the window with and the page draws (DIP).</summary>
public sealed record SwitcherGeometry(int N, int Cols, int Rows, int VisibleRows, bool Scroll, int TileW, int TileH,
    int PreviewW, int PreviewH, int GridW, int GridH, int PanelW, int PanelH, int W, int H)
{
    public int Inset => SwitcherLayout.Inset;
    public int CapH => SwitcherLayout.CapH;
    public int Pad => SwitcherLayout.Pad;
    public int Gap => SwitcherLayout.Gap;
    public int Margin => SwitcherLayout.Margin;

    /// <summary>No windows: the 380 x 116 panel with "No open windows".</summary>
    public static readonly SwitcherGeometry Empty = new(0, 0, 0, 0, false, 0, 0, 0, 0, 0, 0,
        SwitcherLayout.EmptyW, SwitcherLayout.EmptyH, SwitcherLayout.EmptyW + 2 * SwitcherLayout.Margin, SwitcherLayout.EmptyH + 2 * SwitcherLayout.Margin);

    /// <summary>The same fields as layout.js, in its order, for the golden comparison and the log.</summary>
    public string Key() => $"{N},{Cols},{Rows},{VisibleRows},{(Scroll ? 1 : 0)},{TileW},{TileH},{PreviewW},{PreviewH},{GridW},{GridH},{PanelW},{PanelH},{W},{H}";
}
