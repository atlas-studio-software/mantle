using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Clipboard;

/// <summary>One history item as the sidecar's <c>clip.list</c> gives it (sidecar/ClipHistory.cs#ItemObj), the fields the
/// panel reads. Text is trimmed to 2,000 characters by the sidecar; the full text is pasted from the sidecar.</summary>
public sealed record ClipItem
{
    public string Id { get; init; } = "";
    /// text | image | files
    public string Kind { get; init; } = "text";
    public string? Text { get; init; }
    public int Chars { get; init; }
    public int Lines { get; init; }
    public bool Rich { get; init; }
    public bool Pinned { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public int FileCount { get; init; }
    public IReadOnlyList<ClipFile> Files { get; init; } = Array.Empty<ClipFile>();
    /// Unix ms of the copy.
    public double Ts { get; init; }
}

public sealed record ClipFile(string Name, string? Path, bool Dir = false);

/// <summary>What the panel's filter box holds when a key arrives (ui/clipboard/model.js keyAction's <c>st</c>).</summary>
public readonly record struct ClipKeyState(string? Query, bool FilterArmed, int? SelStart = null, int? SelEnd = null);

/// <summary>A key as the page sees it (KeyboardEvent.key and the modifiers).</summary>
public readonly record struct ClipKey(string Key, bool Ctrl = false, bool Alt = false, bool Meta = false);

public readonly record struct DipRect(double X, double Y, double Width, double Height);

/// <summary>Where the window goes and where the panel sits inside its shadow margin (DIP).</summary>
public readonly record struct ClipPlacement(DipRect Window, DipRect Panel);

public readonly record struct ClipPreview(IReadOnlyList<string> Lines, bool More, bool Blank);

/// <summary>The page's dark-glass report made safe (model.js glassOf).</summary>
public readonly record struct ClipGlass(bool Ok, double? Ms, double? Capture, double? Bake, double? SinceOpen, string? Reason);

/// <summary>
/// THE CLIPBOARD PANEL'S PURE LOGIC: a line-for-line port of ui/clipboard/model.js (filtering, previews, labels, keys,
/// placement, the dark-glass log line). The spec is Electron's own output: ClipEmojiGoldenTests replays the answers
/// native/tools/parity/clipemoji/make-golden.cjs took from model.js. Nothing here logs item text.
/// </summary>
public static class ClipModel
{
    // Windows' own Win+V panel is ~360 x 460 at 100%; the margin is transparent room for the shadow.
    public const double PanelW = 360, PanelH = 460, Margin = 18;
    const double Gap = 6, Edge = 8;
    public const double GlassSlowMs = 150;

    static readonly Regex Ws = new(@"\s+", RegexOptions.CultureInvariant);

    /// JavaScript's Math.round (halves go up, also for negatives).
    internal static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>Items matching every word of <paramref name="query"/> (case-insensitive), in their original order.</summary>
    public static IReadOnlyList<ClipItem> Filter(IReadOnlyList<ClipItem> items, string? query)
    {
        var words = Ws.Split((query ?? "").ToLowerInvariant()).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0) return items.ToList();
        return items.Where(it => { string hay = Haystack(it); return words.All(w => hay.Contains(w, StringComparison.Ordinal)); }).ToList();
    }

    static string Haystack(ClipItem it)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(it.Text)) parts.Add(it.Text);
        foreach (var f in it.Files) { parts.Add(f.Name); parts.Add(f.Path ?? ""); }
        if (it.Kind == "image") parts.Add("image picture screenshot");
        if (it.Kind == "files") parts.Add("file files");
        if (it.Pinned) parts.Add("pinned");
        return string.Join("\n", parts).ToLowerInvariant();
    }

    /// <summary>The first <paramref name="max"/> lines worth showing: leading blank lines dropped, tabs opened up, each
    /// line cut at 240 characters. More = something was left out; Blank = only spaces or line breaks.</summary>
    public static ClipPreview PreviewLines(string? text, int max = 3)
    {
        if (max <= 0) max = 3;
        var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        var output = new List<string>();
        int i = 0;
        for (; i < lines.Count && output.Count < max; i++) {
            string l = lines[i].Replace("\t", "    ").TrimEnd();
            output.Add(l.Length > 240 ? l.Substring(0, 240) + "…" : l);
        }
        bool rest = lines.Skip(i).Any(l => l.Trim().Length > 0);
        return new ClipPreview(output, rest, output.All(l => l.Trim().Length == 0));
    }

    /// <summary>"just now", "4 min ago", "2 h ago", "Yesterday", "3 days ago", then a short date.</summary>
    public static string Ago(double ts, double now)
    {
        double s = Math.Max(0, JsRound((now - ts) / 1000));
        if (s < 45) return "just now";
        double m = JsRound(s / 60);
        if (m < 60) return m + " min ago";
        double h = JsRound(m / 60);
        if (h < 24) return h + " h ago";
        double d = JsRound(h / 24);
        if (d == 1) return "Yesterday";
        if (d < 7) return d + " days ago";
        // toLocaleDateString(undefined, { month: 'short', day: 'numeric' }): "Nov 3" on an English Windows
        var dt = DateTimeOffset.FromUnixTimeMilliseconds((long)ts).ToLocalTime();
        return dt.ToString("MMM d", CultureInfo.CurrentCulture);
    }

    static string Plural(int n, string one, string? many = null) => n + " " + (n == 1 ? one : (many ?? one + "s"));

    /// <summary>The quiet line under an item: what it is and when.</summary>
    public static string Meta(ClipItem it, double now)
    {
        string what;
        if (it.Kind == "image") what = it.W != 0 && it.H != 0 ? it.W + " × " + it.H + " image" : "Image";
        else if (it.Kind == "files") what = Plural(it.FileCount != 0 ? it.FileCount : it.Files.Count, "file");
        else if (it.Lines > 3) what = Plural(it.Lines, "line");
        else what = Plural(it.Chars, "character");
        if (it.Rich && it.Kind == "text") what += ", formatted";
        if (it.Kind == "image" && it.FileCount > 0) what += " and its file";
        return what + " · " + Ago(it.Ts, now);
    }

    /// <summary>What a key does: close, down, up, pagedown, pageup, paste, delete, pin, arm-filter, home, end; or null
    /// to let the filter box have it. P pins while the filter is EMPTY and not armed; once typing, P is a letter and
    /// Ctrl+P pins; "/" arms an empty filter so a filter can start with p. Del deletes the item unless it would edit
    /// the filter's text (a selection, or text after the caret).</summary>
    public static string? KeyAction(ClipKey e, ClipKeyState st)
    {
        string q = st.Query ?? "";
        bool armed = st.FilterArmed;
        string k = e.Key;
        switch (k) {
            case "Escape": return "close";
            case "ArrowDown": return "down";
            case "ArrowUp": return "up";
            case "PageDown": return "pagedown";
            case "PageUp": return "pageup";
            case "Enter": return "paste";
        }
        if (k == "Delete" && !e.Ctrl && !e.Alt) {
            if (q.Length > 0 && st.SelStart != null && (st.SelStart != st.SelEnd || (st.SelEnd ?? 0) < q.Length)) return null;
            return "delete";
        }
        bool isP = k == "p" || k == "P";
        if (isP && e.Ctrl && !e.Alt) return "pin";
        if (isP && !e.Ctrl && !e.Alt && !e.Meta && q.Length == 0 && !armed) return "pin";
        if (k == "/" && q.Length == 0 && !armed && !e.Ctrl && !e.Alt) return "arm-filter";
        if (k == "Home" && q.Length == 0) return "home";
        if (k == "End" && q.Length == 0) return "end";
        return null;
    }

    /// <summary>The selection after <paramref name="action"/> over <paramref name="n"/> visible items (page = rows per page).</summary>
    public static int MoveSel(int sel, string action, int n, int page = 4)
    {
        if (n <= 0) return -1;
        if (page <= 0) page = 4;
        int s = sel < 0 ? 0 : sel;
        switch (action) {
            case "down": s = Math.Min(n - 1, s + 1); break;
            case "up": s = Math.Max(0, s - 1); break;
            case "pagedown": s = Math.Min(n - 1, s + page); break;
            case "pageup": s = Math.Max(0, s - page); break;
            case "home": s = 0; break;
            case "end": s = n - 1; break;
        }
        return Math.Max(0, Math.Min(n - 1, s));
    }

    /// <summary>Keep the same item selected across a list refresh; else the same index; else the top.</summary>
    public static int Reselect(string? prevId, int prevIndex, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return -1;
        for (int i = 0; i < ids.Count; i++) if (ids[i] == prevId) return i;
        return Math.Max(0, Math.Min(ids.Count - 1, prevIndex));
    }

    /// <summary>Where the WINDOW goes (DIP, top-left) so the PANEL inside its shadow margin sits just below the caret (or
    /// the pointer), flipped above it when there is no room below and above is roomier, always wholly inside the work
    /// area, 8 DIP from every edge. <paramref name="ah"/> = caret height (0 for the pointer).</summary>
    public static ClipPlacement Place(double ax, double ay, double ah, DipRect work, double pw = PanelW, double ph = PanelH, double m = Margin)
    {
        double px = ax, py = ay + Gap;
        double roomBelow = work.Y + work.Height - Edge - py;
        double roomAbove = ay - ah - Gap - (work.Y + Edge);
        if (roomBelow < ph && roomAbove > roomBelow) py = ay - ah - Gap - ph;
        double minX = work.X + Edge, maxX = work.X + work.Width - Edge - pw;
        double minY = work.Y + Edge, maxY = work.Y + work.Height - Edge - ph;
        px = Math.Max(minX, Math.Min(maxX, px));
        py = Math.Max(minY, Math.Min(maxY, py));
        return new ClipPlacement(new DipRect(JsRound(px - m), JsRound(py - m), pw + 2 * m, ph + 2 * m),
            new DipRect(JsRound(px), JsRound(py), pw, ph));
    }

    static double? Num(double? v) => v is double d && double.IsFinite(d) && d >= 0 ? JsRound(d) : null;

    public static ClipGlass GlassOf(bool ok, double? ms, double? capture, double? bake, double? sinceOpen, string? reason) =>
        new(ok, Num(ms), Num(capture), Num(bake), Num(sinceOpen), ok ? null : Cut(string.IsNullOrEmpty(reason) ? "no reason given" : reason, 200));

    static string Cut(string s, int n) => s.Length > n ? s.Substring(0, n) : s;

    /// <summary>One log line, or null: the first of a run, a slow one, and every missing one are said.</summary>
    public static string? GlassLine(ClipGlass? glass, bool first)
    {
        if (glass is not ClipGlass g) return null;
        string parts = (g.Capture != null ? "capture " + g.Capture + " ms" : "capture ?") + ", bake " + (g.Bake != null ? g.Bake + " ms" : "?");
        if (!g.Ok) return "dark glass: none this open, the panel kept its own dark face (" + g.Reason + ")";
        bool slow = g.Ms != null && g.Ms > GlassSlowMs;
        if (!first && !slow) return null;
        return "dark glass " + (g.Ms != null ? g.Ms.ToString() : "?") + " ms after the panel was drawn" + (first ? " (first this run)" : "") +
            (slow ? ", slow: the panel showed its own dark face until then" : "") + " (" + parts + "); the open never waited for it";
    }
}
