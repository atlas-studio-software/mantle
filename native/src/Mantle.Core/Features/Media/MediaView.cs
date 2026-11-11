using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.Media;

/// <summary>One row of the card's source list (bar.js mediaPaint's rows).</summary>
public sealed record MediaSourceRow(string Aumid, string Name, string? Icon, string Text, string Tip, bool On, bool Gone);

/// <summary>The card's source list, drawn only with two or more apps.</summary>
public sealed record MediaSources(bool Shown, IReadOnlyList<MediaSourceRow> Rows, bool AutoOn, string AutoTip, string Why);

/// <summary>
/// The words and rules the bar's now-playing chip and card draw from a <c>"media"</c> payload (ui/bar/bar.js
/// mediaApp / mediaLine / mediaStatus / mediaGuarded / mediaCan / fmtMs / mediaPos and the source list of mediaPaint).
/// Pure, so the UI only lays it out.
/// </summary>
public static class MediaView
{
    public static readonly IReadOnlyDictionary<string, string> Verb = new Dictionary<string, string> {
        ["toggle"] = "play/pause", ["play"] = "play", ["pause"] = "pause", ["next"] = "next track", ["previous"] = "previous track",
    };

    static string? S(JsonNode? n) => MediaService.Str(n);
    static bool B(JsonNode? n) => MediaService.Bool(n);

    /// The session the chip shows, or null (the payload failed or shows nothing).
    public static JsonObject? Shown(JsonObject? p) => p != null && !(p["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok) ? p["shown"] as JsonObject : null;

    public static string App(JsonObject? s) => S(s?["app"]?["name"]) is { Length: > 0 } n ? n : S(s?["aumid"]) is { Length: > 0 } a ? a : "this app";
    public static string Line(JsonObject s) { var t = S(s["title"]) is { Length: > 0 } x ? x : App(s); return S(s["artist"]) is { Length: > 0 } ar ? t + " · " + ar : t; }
    public static string Status(JsonObject? s) { var st = S(s?["status"]) is { Length: > 0 } x ? x : "unknown"; return char.ToUpperInvariant(st[0]) + st[1..]; }
    public static bool Guarded(JsonObject? s) => s?["controls"]?["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok;

    /// mediaCan(): may this button be pressed for this session, and if not, why not.
    public static (bool Ok, string? Why) Can(JsonObject? s, string action)
    {
        if (s == null) return (false, "nothing is playing");
        if (Guarded(s)) return (false, S(s["controls"]?["reason"]) ?? "controls are off for this session");
        if (s["can"] is not JsonObject c) return (false, App(s) + " did not say which buttons it accepts");
        bool yes = action == "toggle" ? B(c["toggle"]) || (B(s["playing"]) ? B(c["pause"]) : B(c["play"])) : B(c[action]);
        return yes ? (true, null) : (false, $"{App(s)} does not accept {Verb[action]} right now");
    }

    /// The chip's tooltip: "title · artist / app · Status / [guard reason] / Click for the media card".
    public static string ChipTip(JsonObject s) =>
        Line(s) + "\n" + App(s) + " · " + Status(s) + (Guarded(s) ? "\n" + S(s["controls"]?["reason"]) : "") + "\nClick for the media card";

    /// The chip's play/pause tooltip: "Pause <app>" / "Play <app>", or why not.
    public static string ToggleTip(JsonObject s) { var c = Can(s, "toggle"); return c.Ok ? (B(s["playing"]) ? "Pause " : "Play ") + App(s) : c.Why!; }

    /// The card's artist line: "artist · album", else "no track details: why".
    public static (string Text, bool Why) ArtistLine(JsonObject s)
    {
        if (S(s["artist"]) is { Length: > 0 } a) return (S(s["album"]) is { Length: > 0 } al ? a + " · " + al : a, false);
        if (S(s["detailsReason"]) is { Length: > 0 } r) return ("no track details: " + r, true);
        return (S(s["title"]) is { Length: > 0 } ? "" : "no track details", false);
    }

    /// fmtMs(): m:ss or h:mm:ss.
    public static string FmtMs(double ms)
    {
        long t = Math.Max(0, (long)Math.Floor(ms / 1000)), h = t / 3600, m = t % 3600 / 60, x = t % 60;
        return (h > 0 ? h + ":" + m.ToString("00") : m.ToString()) + ":" + x.ToString("00");
    }

    /// mediaPos(): where the track is now (the read's estimate moved on by the local clock while playing, rate-adjusted),
    /// or null when the app publishes no position.
    public static (double Pos, double Dur)? Pos(JsonObject? s, double sinceReadMs)
    {
        var t = s?["timeline"] as JsonObject;
        double dur = Num(t?["durationMs"]);
        if (t == null || !B(t["available"]) || !(dur > 0)) return null;
        double pos = t["positionNowMs"] != null ? Num(t["positionNowMs"]) : Num(t["positionMs"]);
        if (double.IsNaN(pos)) pos = 0;
        if (B(s!["playing"])) pos += sinceReadMs * (Num(s["rate"]) is double r && r > 0 ? r : 1);
        return (Math.Max(0, Math.Min(dur, pos)), dur);
    }

    /// The "no position" words when <see cref="Pos"/> is null.
    public static string NoPosition(JsonObject s) => S(s["timeline"]?["reason"]) ?? "no position: " + App(s) + " does not publish one";

    static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

    /// The source list (BAR-928..932, 949..953): one row per app by AUMID (two sessions of one app share a row) in name
    /// order, "Automatic" always last, exactly one row lit; a picked app whose session is away keeps its row.
    public static MediaSources Sources(JsonObject? p, JsonObject s)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<JsonObject>();
        int dup = 0;
        foreach (var x in (p?["sessions"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) {
            var k = (S(x["aumid"]) ?? "").ToLowerInvariant();
            if (!seen.Add(k)) { dup++; continue; }
            rows.Add(x);
        }
        var pk = p?["pick"] as JsonObject;
        string? by = S(pk?["by"]);
        var wait = by == "waiting" ? pk?["waiting"] as JsonObject : null;
        string held = by == "you" ? (S(s["aumid"]) ?? "").ToLowerInvariant() : wait != null ? (S(wait["aumid"]) ?? "").ToLowerInvariant() : "";
        if (wait != null && !seen.Contains(held))
            rows.Add(new JsonObject { ["aumid"] = S(wait["aumid"]), ["app"] = new JsonObject { ["name"] = S(wait["name"]) ?? S(wait["aumid"]) }, ["gone"] = true });
        string NameOf(JsonObject x) => S(x["app"]?["name"]) is { Length: > 0 } n ? n : S(x["aumid"]) ?? "";
        rows.Sort((a, b) => {
            int c = string.CompareOrdinal(NameOf(a).ToLowerInvariant(), NameOf(b).ToLowerInvariant());
            return c != 0 ? c : string.CompareOrdinal((S(a["aumid"]) ?? "").ToLowerInvariant(), (S(b["aumid"]) ?? "").ToLowerInvariant());
        });
        bool guardOn = B(p?["guard"]?["on"]);
        string autoTip = "Go back to the session Windows calls current" + (guardOn ? " (under the protected media apps rule, anything but a protected app first)" : "");
        double grace = Num(wait?["graceMs"]) is double g && g > 0 ? g : MediaPick.GraceMs;
        var outRows = rows.Select(x => {
            string name = S(x["app"]?["name"]) is { Length: > 0 } n ? n : S(x["aumid"]) is { Length: > 0 } a ? a : "an app";
            bool gone = B(x["gone"]);
            string text = name + (gone ? " · session gone, waiting for it" : S(x["label"]) is { Length: > 0 } l ? " · " + l : S(x["title"]) is { Length: > 0 } t ? " · " + t : "");
            string tip = gone
                ? $"{name}'s media session is missing right now. Your pick is kept for up to {Math.Round(grace / 1000)} s and applies again the moment it is back"
                : name + (S(x["title"]) is { Length: > 0 } t2 ? ": " + t2 : "") + " (" + Status(x) + ")";
            return new MediaSourceRow(S(x["aumid"]) ?? "", name, S(x["app"]?["icon"]), text, tip, (S(x["aumid"]) ?? "").ToLowerInvariant() == held, gone);
        }).ToList();
        string autoWhy = by == "waiting" ? S(pk?["auto"]) ?? "" : by ?? "";
        var apps = (p?["guard"]?["apps"] as JsonArray)?.Select(a => S(a)).Where(a => a != null).ToList();
        string why = by == "you" ? "Showing the source you picked"
            : wait != null ? S(pk?["reason"]) ?? S(wait["reason"]) ?? (S(wait["name"]) ?? "The picked app") + "'s session is missing; showing automatic until it's back"
            : autoWhy == "protected-rule" ? $"Automatic: showing {App(s)} rather than {(apps is { Count: > 0 } ? string.Join(", ", apps) : "a protected app")} (the protected media apps rule)"
            : "Automatic: showing the session Windows calls current";
        if (held.Length == 0 && S(pk?["dropped"]?["reason"]) is { Length: > 0 } dr) why += " · " + dr;
        if (dup > 0) why += $" · {dup} more from the same app{(dup > 1 ? "s" : "")} (Windows gives them one address, so they share a row)";
        return new MediaSources(outRows.Count >= 2, outRows, held.Length == 0, autoTip, why);
    }

    /// The card's foot: "Updates every 10 s instead of live: why" when change events could not be hooked, else "".
    public static string Foot(JsonObject? p) => p?["events"] is JsonValue v && v.TryGetValue<bool>(out var e) && !e
        ? "Updates every 10 s instead of live: " + (S(p["eventsReason"]) ?? "the change events could not be hooked") : "";

    /// The lock box's "how" split at its --flags, so a flag is never broken at its hyphens: (text, isFlag) parts.
    public static IEnumerable<(string Text, bool Flag)> HowParts(string? how) =>
        System.Text.RegularExpressions.Regex.Split(how ?? "", "(--[a-z][a-z-]*)").Select((part, i) => (part, i % 2 == 1)).Where(x => x.part.Length > 0);
}
