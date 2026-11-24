using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Switcher;

/// <summary>A monitor rectangle in physical px (x, y, w, h as lib/switcher.js displaysPhys()).</summary>
public readonly record struct PhysRect(int X, int Y, int W, int H);

/// <summary>Where the parked overlay goes, and which side of its monitor that is (for the log).</summary>
public readonly record struct ParkPlace(int X, int Y, string Side);

/// <summary>The "could not switch" notice: one plain sentence, and the small what-to-do line (or none).</summary>
public sealed record NoticeText(string Title, string? Text);

/// <summary>A preview box the page reports for one tile (DIP inside the overlay).</summary>
public sealed record PreviewBox(string? Hwnd, double X, double Y, double W, double H);

/// <summary>The sidecar's <c>activated</c> event, the fields the log line reads.</summary>
public sealed record ActivatedEvent(long Seq, bool Ok, string? Via, string? How = null, double? Ms = null, string? Cause = null, string? Reason = null);

/// <summary>
/// The pure helpers of lib/switcher.js: where the overlay parks (parkSpot), the failure notice's words
/// (noticeWords, nameFor, when a notice is due), the preview boxes a page may ask DWM for (cleanBoxes) and
/// the switch's log line (activatedLine). PRIVACY: no window title and no handle in a log line, ever.
/// </summary>
public static class SwitcherWords
{
    public const int ParkGap = 64;
    public const int MaxBoxes = 400;
    public const int NameMax = 48;

    /// <summary>
    /// A w x h spot on NO display that Windows still counts as on <paramref name="mon"/> (the nearest monitor
    /// decides a window's DPI): just above it, else below, left, right; boxed in, -32000,-32000.
    /// </summary>
    public static ParkPlace ParkSpot(PhysRect mon, int w, int h, IReadOnlyList<PhysRect> displays)
    {
        bool Hit(int x, int y)
        {
            foreach (var d in displays)
                if (x < d.X + d.W && x + w > d.X && y < d.Y + d.H && y + h > d.Y) return true;
            return false;
        }
        var cands = new[]
        {
            new ParkPlace(mon.X, mon.Y - h - ParkGap, "above"),
            new ParkPlace(mon.X, mon.Y + mon.H + ParkGap, "below"),
            new ParkPlace(mon.X - w - ParkGap, mon.Y, "left of"),
            new ParkPlace(mon.X + mon.W + ParkGap, mon.Y, "right of"),
        };
        foreach (var c in cands) if (!Hit(c.X, c.Y)) return c;
        return new ParkPlace(-32000, -32000, "far from");
    }

    /// <summary>
    /// The notice leads with what the user would say himself; the small line says what to do next. The machinery
    /// (<paramref name="detail"/>: SetForegroundWindow, the focus ladder, exceptions) is for the log, never shown.
    /// </summary>
    public static NoticeText NoticeWords(string? cause, string? name, string? detail)
    {
        _ = detail;
        string who = string.IsNullOrEmpty(name) ? "That window" : name;
        string whom = string.IsNullOrEmpty(name) ? "that window" : name;
        return cause switch
        {
            "hung" => new NoticeText($"{who} isn't responding, so Windows wouldn't bring it to the front.",
                "Give it a moment and try again, or end it in Task Manager (Ctrl+Shift+Esc)."),
            "closed" => new NoticeText($"{who} closed before Mantle could switch to it.", null),
            "refused" => new NoticeText($"Windows wouldn't bring {whom} to the front.", "Click it on the taskbar to switch to it."),
            _ => new NoticeText($"Mantle couldn't switch to {whom}.", "Try Alt+Tab again, or click it on the taskbar."),
        };
    }

    /// <summary>What the user calls that window: the bar's pinned-app name, else its title, 47 characters and an ellipsis past 48.</summary>
    public static string NameFor(string? pinnedName, string? title)
    {
        if (!string.IsNullOrEmpty(pinnedName)) return pinnedName;
        string t = (title ?? "").Trim();
        return t.Length > NameMax ? t.Substring(0, NameMax - 1) + "…" : t;
    }

    /// <summary>onActivated: a failed switch gets a notice, unless it said "none" or there was nothing to switch to.</summary>
    public static bool NoticeDue(bool ok, string? cause, int? listCount) => !ok && cause != "none" && listCount != 0;

    static readonly Regex Digits = new("^[0-9]{1,20}$", RegexOptions.CultureInvariant);
    static readonly Regex WindowNumber = new("window [0-9]+", RegexOptions.CultureInvariant);

    static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;

    /// <summary>
    /// The boxes kept before any reaches DWM: only windows IN this Alt+Tab, digit handles, finite sane numbers,
    /// one per window, at most the first 400 looked at. Without this a page could ask for a live picture of any
    /// window on the desktop. A box not kept is simply not drawn.
    /// </summary>
    public static IReadOnlyList<PreviewBox> CleanBoxes(IReadOnlyList<PreviewBox?>? boxes, IEnumerable<string?>? list)
    {
        var out_ = new List<PreviewBox>();
        if (boxes is null || list is null) return out_;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in list) if (h is not null) allowed.Add(h);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < boxes.Count && i < MaxBoxes; i++)
        {
            var b = boxes[i];
            if (b is null || b.Hwnd is null) continue;
            string hwnd = b.Hwnd;
            if (!Digits.IsMatch(hwnd) || !allowed.Contains(hwnd) || seen.Contains(hwnd)) continue;
            if (!In(b.X, -1e4, 1e5) || !In(b.Y, -1e4, 1e5) || !In(b.W, 0, 1e5) || !In(b.H, 0, 1e5)) continue;
            seen.Add(hwnd);
            out_.Add(b);
        }
        return out_;
    }

    static string Js(string? s) => s ?? "undefined";

    /// <summary>The log line for <c>activated</c>: the way it went and how long, never the title or the handle.</summary>
    public static string ActivatedLine(ActivatedEvent m) =>
        m.Ok
            ? $"#{m.Seq}: switched ({Js(m.Via)}, {Js(m.How)}, {(m.Ms is double ms ? ms.ToString(CultureInfo.InvariantCulture) : "undefined")} ms)"
            : $"#{m.Seq}: could NOT switch ({Js(m.Via)}, {(string.IsNullOrEmpty(m.Cause) ? "no cause given" : m.Cause)}): " +
              WindowNumber.Replace(string.IsNullOrEmpty(m.Reason) ? "no reason given" : m.Reason, "the window");
}
