using System;
using System.Collections.Generic;

namespace Mantle.Core.Features.TaskView;

/// <summary>The space under the desktop strip and the filter row where the tiles go (DIP).</summary>
public readonly record struct TaskViewArea(double X, double Y, double W, double H);

/// <summary>layout.js layout(): the grid for n windows. Scrolls: whole rows only (a DWM picture cannot be clipped).</summary>
public sealed record TaskViewGrid(int N, int Cols, int Rows, int VisibleRows, bool Scrolls, int TileW, int TileH, int PvW, int PvH, TaskViewArea Area)
{
    public int Gap => TaskViewLayout.Gap;
    public int Inset => TaskViewLayout.Inset;
    public int CapH => TaskViewLayout.CapH;
}

/// <summary>Where tile <c>I</c> goes; a tile scrolled out of view has Y = -10000.</summary>
public readonly record struct TaskViewSpot(int I, int Row, int Col, bool Visible, double X, double Y);

/// <summary>
/// Task View's grid geometry, arrow keys and type-to-filter: a port of Electron's <c>ui/taskview/layout.js</c>
/// (DIP, pure). Unlike the switcher, rows ARE centred and the arrow keys stop at the edges (no wrap); Up/Down go
/// to the tile whose centre is nearest in x. Ported with ui/taskview/layout.test.js.
/// </summary>
public static class TaskViewLayout
{
    public const int StripH = 148, SearchH = 48, PadX = 48, PadTop = 12, PadBottom = 36, Gap = 20;
    public const int TileMax = 420, TileMin = 176, Step = 8, Inset = 8, CapH = 36;
    public const double Aspect = 0.625;

    static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    static (int TileW, int TileH, int PvW, int PvH) TileOf(int tw)
    {
        int pvW = tw - 2 * Inset;
        int pvH = JsRound(pvW * Aspect);
        return (tw, Inset + pvH + CapH, pvW, pvH);
    }

    static int ColsFor(int n, int tw, double availW)
    {
        int cols = Math.Max(1, (int)Math.Floor((availW + Gap) / (tw + Gap)));
        cols = Math.Min(cols, Math.Max(1, n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        return Math.Max(1, (int)Math.Ceiling(n / (double)Math.Max(1, rows)));
    }

    static double HeightOf(int rows, int tileH) => rows * tileH + Math.Max(0, rows - 1) * Gap;

    public static TaskViewGrid Layout(int n, double w, double h)
    {
        if (n < 0) n = 0;
        double ay = StripH + SearchH + PadTop;
        double aw = Math.Max(0, w - 2 * PadX);
        var area = new TaskViewArea(PadX, ay, aw, Math.Max(0, h - ay - PadBottom));
        (int TileW, int TileH, int PvW, int PvH) t = default;
        int cols = 1, rows = 0;
        bool fitted = false, any = false;
        int maxW = (int)Math.Min(TileMax, Math.Max(TileMin, area.W));
        for (int tw = maxW; tw >= TileMin; tw -= Step)
        {
            any = true;
            t = TileOf(tw);
            cols = ColsFor(n, tw, area.W);
            rows = n > 0 ? (int)Math.Ceiling(n / (double)cols) : 0;
            if (HeightOf(rows, t.TileH) <= area.H) { fitted = true; break; }
        }
        if (!any || !fitted)
        {
            t = TileOf(TileMin);
            cols = ColsFor(n, TileMin, area.W);
            rows = n > 0 ? (int)Math.Ceiling(n / (double)cols) : 0;
        }
        int visibleRows = fitted ? rows : Math.Max(1, (int)Math.Floor((area.H + Gap) / (t.TileH + Gap)));
        return new TaskViewGrid(n, cols, rows, Math.Min(rows, visibleRows), !fitted && rows > visibleRows, t.TileW, t.TileH, t.PvW, t.PvH, area);
    }

    /// <summary>How many tiles row <paramref name="row"/> holds (0 outside the grid).</summary>
    public static int RowLen(TaskViewGrid lay, int row) =>
        row < 0 || row >= lay.Rows ? 0 : Math.Min(lay.Cols, lay.N - row * lay.Cols);

    /// <summary>Every tile's spot with the grid scrolled to <paramref name="firstRow"/>: rows centred, the block centred in the area.</summary>
    public static IReadOnlyList<TaskViewSpot> Place(TaskViewGrid lay, int firstRow)
    {
        int first = Math.Max(0, Math.Min(firstRow, Math.Max(0, lay.Rows - lay.VisibleRows)));
        int shownRows = Math.Min(lay.VisibleRows, lay.Rows - first);
        double blockH = HeightOf(shownRows, lay.TileH);
        double y0 = lay.Area.Y + Math.Max(0, Math.Floor((lay.Area.H - blockH) / 2));
        var out_ = new List<TaskViewSpot>(lay.N);
        for (int i = 0; i < lay.N; i++)
        {
            int row = i / lay.Cols, col = i % lay.Cols;
            int k = RowLen(lay, row);
            double rowW = k * lay.TileW + (k - 1) * Gap;
            double x0 = lay.Area.X + Math.Floor((lay.Area.W - rowW) / 2);
            bool visible = row >= first && row < first + shownRows;
            out_.Add(new TaskViewSpot(i, row, col, visible, x0 + col * (lay.TileW + Gap),
                visible ? y0 + (row - first) * (lay.TileH + Gap) : -10000));
        }
        return out_;
    }

    /// <summary>
    /// The arrow keys (DOM key names): Left/Right step and stop at the ends, Home/End, Up/Down to the nearest
    /// tile in x on the next row (staying put at the top or bottom). -1 with no tiles; a stale selection is clamped.
    /// </summary>
    public static int Move(TaskViewGrid lay, int sel, string? key)
    {
        int n = lay.N;
        if (n <= 0) return -1;
        int s = Math.Max(0, Math.Min(n - 1, sel < 0 ? 0 : sel));
        switch (key)
        {
            case "ArrowLeft": return Math.Max(0, s - 1);
            case "ArrowRight": return Math.Min(n - 1, s + 1);
            case "Home": return 0;
            case "End": return n - 1;
            case "ArrowUp": case "ArrowDown": break;
            default: return s;
        }
        int row = s / lay.Cols;
        int to = key == "ArrowUp" ? row - 1 : row + 1;
        if (to < 0 || to >= lay.Rows) return s;
        var pos = Place(lay, 0);
        double cx = pos[s].X + lay.TileW / 2.0;
        int best = -1;
        double bestD = double.PositiveInfinity;
        for (int i = to * lay.Cols; i < Math.Min(n, (to + 1) * lay.Cols); i++)
        {
            double d = Math.Abs(pos[i].X + lay.TileW / 2.0 - cx);
            if (d < bestD - 0.5) { bestD = d; best = i; }
        }
        return best < 0 ? s : best;
    }

    /// <summary>The first row shown so <paramref name="sel"/> is on screen, moving as little as possible.</summary>
    public static int FirstRowFor(TaskViewGrid lay, int sel, int cur)
    {
        int first = Math.Max(0, cur);
        if (!lay.Scrolls || sel < 0) return 0;
        int row = sel / lay.Cols;
        if (row < first) first = row;
        if (row >= first + lay.VisibleRows) first = row - lay.VisibleRows + 1;
        return Math.Max(0, Math.Min(first, lay.Rows - lay.VisibleRows));
    }

    /// <summary>Type to filter: the indexes whose "title app" holds every word of the query, any case, list order kept.</summary>
    public static IReadOnlyList<int> Filter(IReadOnlyList<(string? Title, string? App)> items, string? query)
    {
        var words = (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var out_ = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            string hay = ((items[i].Title ?? "") + " " + (items[i].App ?? "")).ToLowerInvariant();
            bool ok = true;
            for (int w = 0; w < words.Length && ok; w++) ok = hay.Contains(words[w], StringComparison.Ordinal);
            if (ok) out_.Add(i);
        }
        return out_;
    }
}
