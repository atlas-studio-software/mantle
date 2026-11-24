using System;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Snap;

/// <summary>What a key on a snap page does. None = not the page's key (it goes on); Stay = taken, nothing changes.</summary>
public enum SnapKeyAct { None, Stay, Select, Pick, Dismiss }

/// <summary>A key's verdict: the act, and for Select the picker's layout and zone or Snap Assist's tile.</summary>
public readonly record struct SnapKeyed(SnapKeyAct Act, int Layout = 0, int Zone = 0, int Sel = 0);

/// <summary>Snap Assist's tile grid (ui/snap/snap.js#layoutAssist): tile size, columns, rows, rows on view, and the grid's top-left.</summary>
public readonly record struct SnapTiles(int Tw, int Th, int Cols, int Rows, int Vis, int X0, int Y0);

/// <summary>
/// The snap page's model (ui/snap/snap.js, ui/snap/index.html), lane r9-snap: the three faces' words, the picker's and
/// Snap Assist's keys, and Assist's tile grid and paging, as pure functions. The surface (on the shared style layer)
/// only draws what these say; nothing here knows a colour, a font or a box. CSS px = DIP.
/// </summary>
public static class SnapPages
{
    const string Dot = " · ";                                       // " · ", the page's separator

    public const string PickerTitle = "Snap layouts";
    public const string AssistTitle = "Snap Assist";
    public const string PickerHint = "Arrows choose a zone" + Dot + "Enter snaps" + Dot + "Esc closes";   // index.html, before a zone is selected
    public const string AssistFoot = "Arrows choose a window" + Dot + "Enter puts it here" + Dot + "Esc closes";
    public const string EscCloses = "Esc closes";
    public const string NoOthers = "No other windows to put here";
    public const string NoticeFallback = "Could not snap";
    public const string BrokenTitle = "The snap page cannot work";
    public const string NoBridge = "window.mantle is missing (preload.js did not run).";
    public const string NoLayouts = "layouts.js did not load.";
    public const string Untitled = "Untitled window";
    public const string MinimizedTag = "· minimized";
    public const string NoPicture = "no picture";
    public const string KeysElsewhere = "The keyboard is on another window: click instead.";

    public const int TileMax = 300, TileMin = 150, CapH = 28, TilePad = 6, Gap = 8;

    // ------------------------------------------------------------ words (SNAP-060, 061, 069, 082, 083, 088, 089)

    /// <summary>SNAP-060: the notice's title, "Could not snap" when it has none.</summary>
    public static string NoticeTitle(string? title) => string.IsNullOrEmpty(title) ? NoticeFallback : title;

    /// <summary>SNAP-061: the page could not start: no bridge (preload did not run), or the layouts table did not load.</summary>
    public static (string Title, string Text) Broken(bool bridge) => (BrokenTitle, bridge ? NoLayouts : NoBridge);

    /// <summary>SNAP-069: "for &lt;exe or title&gt;", empty when neither.</summary>
    public static string PickerFor(string? exe, string? title)
    {
        string? who = !string.IsNullOrEmpty(exe) ? exe : !string.IsNullOrEmpty(title) ? title : null;
        return who == null ? "" : "for " + who;
    }

    /// <summary>SNAP-069: the footer for the selected zone.</summary>
    public static string PickerFoot(int layout, int zone)
    {
        SnapLayout l = SnapFaces.Layouts[layout];
        return l.Label + ": " + l.Names[zone] + Dot + "Enter snaps" + Dot + "Esc closes";
    }

    /// <summary>SNAP-069: a layout's group label "N: &lt;label&gt;".</summary>
    public static string GroupLabel(int layout) => (layout + 1) + ": " + SnapFaces.Layouts[layout].Label;

    /// <summary>SNAP-069: a zone's label "&lt;layout&gt;, &lt;zone&gt;" (its tooltip is the zone name alone).</summary>
    public static string ZoneLabel(int layout, int zone) => SnapFaces.Layouts[layout].Label + ", " + SnapFaces.Layouts[layout].Names[zone];

    /// <summary>SNAP-068: the page opens on the first zone of the first layout; it ignores main's start (see the lane notes).</summary>
    public static (int Layout, int Zone) PickerOpensOn => (0, 0);

    /// <summary>SNAP-082: "choose a window for the &lt;zone&gt;" and " (N more after this)" while more zones follow.</summary>
    public static string AssistZoneLine(string? zoneName, int left) =>
        "choose a window for the " + (string.IsNullOrEmpty(zoneName) ? "this space" : zoneName) + (left > 0 ? " (" + left + " more after this)" : "");

    /// <summary>SNAP-082, 089: the footer: the key hints, or only "Esc closes" with nothing to offer.</summary>
    public static string AssistFootFor(int count) => count > 0 ? AssistFoot : EscCloses;

    /// <summary>SNAP-083: a tile's caption: the title, else the exe, else "Untitled window".</summary>
    public static string Caption(string? title, string? exe) =>
        !string.IsNullOrEmpty(title) ? title : !string.IsNullOrEmpty(exe) ? exe : Untitled;

    /// <summary>SNAP-083: the first-letter badge when there is no icon: the exe without ".exe", else the title, upper case; "?" for none.</summary>
    public static string Letter(string? exe, string? title)
    {
        string s = !string.IsNullOrEmpty(exe) ? exe : !string.IsNullOrEmpty(title) ? title : "?";
        s = Regex.Replace(s, @"\.exe$", "", RegexOptions.IgnoreCase);
        return s.Length == 0 ? "?" : char.ToUpperInvariant(s[0]).ToString();
    }

    /// <summary>SNAP-083: only a data:image/ URL is drawn as the icon.</summary>
    public static bool IsIcon(string? icon) => icon != null && icon.StartsWith("data:image/", StringComparison.Ordinal);

    /// <summary>SNAP-088: what a tile's preview says when its picture could not be drawn ("" when it was).</summary>
    public static string TileWhy(bool ok, string? reason) => ok ? "" : string.IsNullOrEmpty(reason) ? NoPicture : reason;

    /// <summary>SNAP-088: the footer warning when the pictures failed.</summary>
    public static string NoPictures(string? reason) => "No live pictures: " + (string.IsNullOrEmpty(reason) ? "unknown reason" : reason);

    /// <summary>SNAP-077: the footer warning main sent, or the page's own when it sent none.</summary>
    public static string NoKeysFoot(string? text) => string.IsNullOrEmpty(text) ? KeysElsewhere : text;

    // ------------------------------------------------------------ keys (SNAP-060, 071, 085)

    /// <summary>SNAP-060: Esc, Enter or Space dismiss a notice (as Esc); any other key is not the notice's.</summary>
    public static SnapKeyed NoticeKey(string key) =>
        key is "Escape" or "Enter" or " " ? new SnapKeyed(SnapKeyAct.Dismiss) : new SnapKeyed(SnapKeyAct.None);

    /// <summary>
    /// SNAP-071: Right/Tab and Left/Shift+Tab walk every zone (wrapping), Down/Up jump a row of three layouts (the zone
    /// clamped; no row there = nothing moves), digits 1-5 pick that layout's first zone, Enter or Space snaps, Esc closes.
    /// </summary>
    public static SnapKeyed PickerKey(string key, bool shift, int layout, int zone)
    {
        if (key == "Escape") return new SnapKeyed(SnapKeyAct.Dismiss);
        if (key == "ArrowRight" || (key == "Tab" && !shift)) return Select(SnapFaces.StepZone(layout, zone, 1));
        if (key == "ArrowLeft" || (key == "Tab" && shift)) return Select(SnapFaces.StepZone(layout, zone, -1));
        if (key == "ArrowDown" || key == "ArrowUp") {
            var to = SnapFaces.StepRow(layout, zone, key == "ArrowDown" ? 1 : -1);
            return to is { } t ? Select(t) : new SnapKeyed(SnapKeyAct.Stay, layout, zone);
        }
        if (key == "Enter" || key == " ") return new SnapKeyed(SnapKeyAct.Pick, layout, zone);
        if (key.Length == 1 && key[0] >= '1' && key[0] <= '9' && key[0] - '0' <= SnapFaces.Layouts.Count)
            return new SnapKeyed(SnapKeyAct.Select, key[0] - '1', 0);
        return new SnapKeyed(SnapKeyAct.None, layout, zone);
    }

    static SnapKeyed Select((int Layout, int Zone) t) => new(SnapKeyAct.Select, t.Layout, t.Zone);

    /// <summary>
    /// SNAP-085: Right/Tab next, Left/Shift+Tab previous (no wrap), Down a row (clamped to the last), Up a row (stops at
    /// the top), Home first, End last, Enter or Space picks, Esc closes. With no window to offer the keys are taken and do nothing.
    /// </summary>
    public static SnapKeyed AssistKey(string key, bool shift, int sel, int count, int cols)
    {
        if (key == "Escape") return new SnapKeyed(SnapKeyAct.Dismiss, Sel: sel);
        int c = Math.Max(1, cols);
        int? to = key switch {
            "ArrowRight" => (int?)(sel + 1),
            "Tab" when !shift => (int?)(sel + 1),
            "ArrowLeft" => (int?)(sel - 1),
            "Tab" => (int?)(sel - 1),
            "ArrowDown" => (int?)Math.Min(sel + c, count - 1),
            "ArrowUp" => sel - c >= 0 ? (int?)(sel - c) : (int?)null,
            "Home" => (int?)0,
            "End" => (int?)(count - 1),
            _ => (int?)null,
        };
        bool known = key is "ArrowRight" or "Tab" or "ArrowLeft" or "ArrowDown" or "ArrowUp" or "Home" or "End" or "Enter" or " ";
        if (!known) return new SnapKeyed(SnapKeyAct.None, Sel: sel);
        if (count <= 0) return new SnapKeyed(SnapKeyAct.Stay, Sel: sel);
        if (key is "Enter" or " ") return new SnapKeyed(SnapKeyAct.Pick, Sel: sel);
        if (to == null) return new SnapKeyed(SnapKeyAct.Stay, Sel: sel);
        return new SnapKeyed(SnapKeyAct.Select, Sel: Math.Max(0, Math.Min(to.Value, count - 1)));
    }

    /// <summary>SNAP-083: Assist's first selection: main's sel kept inside the list (0 for an empty one).</summary>
    public static int StartSel(int sel, int count) => Math.Max(0, Math.Min(sel, count - 1));

    // ------------------------------------------------------------ the tiles (SNAP-083, 084)

    /// <summary>
    /// SNAP-083: tiles as large as fit, 300 wide shrinking by 10 down to 150, previews 16:10 plus a caption, whole rows
    /// only, 8 px apart, centred in a <paramref name="w"/> by <paramref name="h"/> box.
    /// </summary>
    public static SnapTiles Layout(int n, double w, double h)
    {
        int tw = TileMax, cols, rows, th;
        for (; ; tw -= 10) {
            cols = Math.Max(1, (int)Math.Floor((w + Gap) / (tw + Gap)));
            rows = (int)Math.Ceiling(n / (double)cols);
            th = PreviewH(tw) + 2 * TilePad + CapH;
            if (rows * th + (rows - 1) * Gap <= h || tw <= TileMin) break;
        }
        int vis = Math.Max(1, Math.Min(rows, (int)Math.Floor((h + Gap) / (th + Gap))));
        int usedW = cols * tw + (cols - 1) * Gap;
        int usedH = vis * th + (vis - 1) * Gap;
        return new SnapTiles(tw, th, cols, rows, vis, Math.Max(0, (int)Math.Floor((w - usedW) / 2)), Math.Max(0, (int)Math.Floor((h - usedH) / 2)));
    }

    /// <summary>SNAP-083: the 16:10 preview's height inside a tile <paramref name="tw"/> wide.</summary>
    public static int PreviewH(int tw) => SnapFaces.JsRound((tw - 2 * TilePad) * 10 / 16.0);

    /// <summary>SNAP-084: tile <paramref name="i"/>'s box in the grid with <paramref name="first"/> the top row on view; null when its row is off view.</summary>
    public static SnapBox? Tile(int i, int first, SnapTiles t)
    {
        int row = i / t.Cols, col = i % t.Cols, vr = row - first;
        if (vr < 0 || vr >= t.Vis) return null;
        return SnapBox.Xywh(t.X0 + col * (t.Tw + Gap), t.Y0 + vr * (t.Th + Gap), t.Tw, t.Th);
    }

    /// <summary>
    /// SNAP-084, 085: select tile <paramref name="i"/> (clamped); with <paramref name="scroll"/> the rows page as whole
    /// units to bring it on view. Moved = other windows are on view now, so the pictures are measured again.
    /// </summary>
    public static (int Sel, int First, bool Moved) SelectTile(int i, int sel, int count, int first, SnapTiles t, bool scroll)
    {
        if (count <= 0) return (sel, first, false);
        int s = Math.Max(0, Math.Min(i, count - 1));
        int row = s / Math.Max(1, t.Cols);
        bool moved = false;
        if (scroll && row < first) { first = row; moved = true; }
        if (scroll && row >= first + t.Vis) { first = row - t.Vis + 1; moved = true; }
        return (s, first, moved);
    }

    /// <summary>SNAP-084: "more above · N more below" while rows are off view; null when every tile is on view.</summary>
    public static string? MoreHint(int count, int first, SnapTiles t)
    {
        int hidden = count - Math.Min(count, (first + t.Vis) * t.Cols);
        if (hidden <= 0 && first <= 0) return null;
        return (first > 0 ? "more above" + Dot : "") + (hidden > 0 ? hidden + " more below" : "");   // snap.js keeps the " · " even with nothing below
    }
}
