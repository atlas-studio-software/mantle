using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.Toasts;

// Port of lib/features/_toasts-core.js (the pure part): which toast becomes a banner, what a banner carries,
// when Windows would be quiet, what a click does, and where the banner column goes. cornerSpots (the glide
// with the bar) waits for lib/bar-follow.js's port and is part 2.

/// <summary>One toast from the notification list (the payload the bell uses), the fields a banner can carry.</summary>
public sealed record IncomingToast(long Id, string? Aumid = null, string? App = null, string? Title = null, string? Body = null,
    double? TimeMs = null, string? Icon = null, string? Logo = null, bool LogoCircle = false, string? Attribution = null,
    string? Image = null, string? Hero = null, string? Scenario = null, string? Opens = null)
{
    /// One entry of the sidecar's list; null when it has no integer id.
    public static IncomingToast? FromJson(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long n)) return null;
        string? S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? time = t.TryGetProperty("timeMs", out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetDouble() : null;
        bool circle = t.TryGetProperty("logoCircle", out var lc) && lc.ValueKind == JsonValueKind.True;
        return new IncomingToast(n, S("aumid"), S("app"), S("title"), S("body"), time, S("icon"), S("logo"), circle, S("attribution"),
            S("image"), S("hero"), S("scenario"), S("opens"));
    }
}

/// <summary>What a banner carries (TST-038), nothing more.</summary>
public sealed record Banner(long Id, string? Aumid, string App, string? Icon, string? Logo, bool LogoCircle, string? Title, string? Body,
    string? Attribution, string? Image, bool Sticky, string Opens, double At);

/// <summary>The first list after a start is only remembered; after that an id not in the previous list is new.</summary>
public sealed class BannerPicker
{
    HashSet<long>? _known;

    public IReadOnlyList<IncomingToast> Next(bool ok, IReadOnlyList<IncomingToast>? toasts, double nowMs)
    {
        if (toasts == null || !ok) return Array.Empty<IncomingToast>();   // a failed read changes nothing: no burst after it
        var ids = new HashSet<long>(toasts.Select(t => t.Id));
        bool first = _known == null;
        var prev = _known;
        _known = ids;
        if (first) return Array.Empty<IncomingToast>();
        return toasts.Where(t => !prev!.Contains(t.Id) && !(t.TimeMs is double ms && ms > 0 && nowMs - ms > ToastsCore.FreshMs)).ToArray();
    }

    public IReadOnlyList<long> Ids() => _known?.ToArray() ?? Array.Empty<long>();
    public void Reset() => _known = null;
}

public sealed record SpotRect(double X, double Y, double Width, double Height, string? Key = null);
public sealed record BannerSpot(int X, int Y, int Width, int Height, int Full, IReadOnlyList<string> Under, bool Hide, string? Why);

public static class ToastsCore
{
    /// A toast whose own time is older than this is never bannered (a re-listed old toast after a hiccup).
    public const int FreshMs = 120000;
    /// Less room than this above a tall pop-up: banners are held back.
    public const int MinRoom = 150;

    static readonly Regex StickyRe = new("^(alarm|reminder|incomingCall|urgent)$", RegexOptions.IgnoreCase);
    static readonly Regex Spaces = new(@"\s+");
    static readonly Regex Picture = new("^(data:image/|file:)", RegexOptions.IgnoreCase);

    static readonly Dictionary<int, string> Quns = new()
    {
        [1] = "the screen is locked or the screen saver is on",
        [2] = "a full-screen app is in front",
        [3] = "a full-screen game is running",
        [4] = "presentation mode is on",
        [6] = "Windows is in its quiet first hour after setup",
        [7] = "a full-screen app is in front",
    };

    public static bool IsSticky(string? scenario) => StickyRe.IsMatch(scenario ?? "");

    static string? Text(string? v, int max)
    {
        if (v == null || v.Trim().Length == 0) return null;
        var s = Spaces.Replace(v, " ").Trim();
        return s.Length > max ? s[..max] : s;
    }

    static string? Pic(string? v) => v != null && Picture.IsMatch(v) ? v : null;

    public static Banner BannerOf(IncomingToast t, double nowMs) => new(
        t.Id, Text(t.Aumid, 512), Text(t.App, 120) ?? "An app", Pic(t.Icon), Pic(t.Logo), t.LogoCircle,
        Text(t.Title, 300), Text(t.Body, 1200), Text(t.Attribution, 200), Pic(t.Image) ?? Pic(t.Hero),
        IsSticky(t.Scenario), t.Opens == "protocol" ? "protocol" : t.Opens == "none" ? "none" : "app", nowMs);

    /// No banner while Windows would be quiet: the reason, or null (an unreadable state is NOT quiet).
    public static string? QuietWhy(bool ok, int? dnd, int? quns)
    {
        if (!ok) return null;
        if (dnd == 1) return "Do not disturb is on";
        if (dnd == 2) return "Do not disturb is on (alarms only)";
        if (quns is int q && q != 5 && Quns.TryGetValue(q, out var why)) return why;
        return null;
    }

    /// A click: a link toast opens its link; else the app's window when it has one; else the notification list.
    public static (string Act, string? Hwnd) ClickPlan(Banner? b, string? hwnd)
    {
        if (b != null && b.Opens == "protocol") return ("link", null);
        if (!string.IsNullOrEmpty(hwnd)) return ("window", hwnd);
        return ("list", null);
    }

    /// JS Math.round (halves go up).
    public static int Round(double v) => (int)Math.Floor(v + 0.5);

    /// The banner column at the right of the work area, down to whatever in the lower half reaches under it.
    public static BannerSpot Spot(SpotRect work, int w, double top, SpotRect? bar, IReadOnlyList<SpotRect>? others)
    {
        double right = work.X + work.Width, left = right - w, half = work.Y + work.Height / 2;
        var things = new List<SpotRect>();
        if (bar != null) things.Add(bar with { Key = "the bar" });
        foreach (var r in others ?? Array.Empty<SpotRect>()) if (r != null) things.Add(r);
        double bottom = work.Y + work.Height;
        var under = new List<string>();
        for (bool moved = true; moved;)
        {
            moved = false;
            foreach (var r in things)
            {
                var key = r.Key ?? "something";
                if (!(r.Width > 0 && r.Height > 0) || under.Contains(key)) continue;
                bool across = r.X < right && r.X + r.Width > left;
                if (across && r.Y + r.Height / 2 >= half && r.Y < bottom && r.Y + r.Height > bottom - MinRoom)
                {
                    bottom = r.Y; under.Add(key); moved = true;
                }
            }
        }
        int y = Round(work.Y + top);
        int height = Round(bottom - y);
        int full = Math.Max(1, Round(work.Y + work.Height - y));
        bool hide = height < MinRoom;
        return new BannerSpot(Round(left), y, w, Math.Max(1, height), full, under, hide,
            hide ? $"only {Math.Max(0, height)} DIP left above {(under.Count > 0 ? string.Join(" and ", under) : "the corner")}" : null);
    }
}
