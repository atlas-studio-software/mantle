using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.Notifications;

/// <summary>ui/bar/notifications.js's words and rules, pure (the bar's panel draws them; unit tests pin them).</summary>
public static class NotificationWords
{
    public sealed record Group(string? Aumid, string? App, JsonObject First, List<JsonObject> Items);

    /// "now", "N min ago", "N h ago" under 6 h, then the clock time today, "Yesterday <time>", else "<Mon d>, <time>".
    public static string Ago(long ms, DateTimeOffset now, CultureInfo? culture = null)
    {
        if (ms <= 0) return "";
        var c = culture ?? CultureInfo.CurrentCulture;
        long d = now.ToUnixTimeMilliseconds() - ms;
        if (d < 60000) return "now";
        if (d < 3600000) return d / 60000 + " min ago";
        if (d < 6 * 3600000L) return d / 3600000 + " h ago";
        var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToOffset(now.Offset);
        string time = t.ToString("t", c);
        if (t.Date == now.Date) return time;
        if (t.Date == now.Date.AddDays(-1)) return "Yesterday " + time;
        return t.ToString("MMM d", c) + ", " + time;
    }

    /// Grouped by app (AUMID, else app name, case-insensitive), in the order of each app's newest toast.
    public static List<Group> Groups(IEnumerable<JsonObject> list)
    {
        var o = new List<Group>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in list) {
            string k = (NotificationCentre.Str(t["aumid"]) ?? NotificationCentre.Str(t["app"]) ?? "?").ToLowerInvariant();
            if (!at.TryGetValue(k, out var i)) { at[k] = i = o.Count; o.Add(new Group(NotificationCentre.Str(t["aumid"]), NotificationCentre.Str(t["app"]), t, new List<JsonObject>())); }
            o[i].Items.Add(t);
        }
        return o;
    }

    public static string Badge(int unread) => unread > 9 ? "9+" : unread.ToString(CultureInfo.InvariantCulture);

    /// The bell's tooltip and accessible name.
    public static string BellTip(JsonObject? p)
    {
        if (p == null) return "Notifications — reading…";
        if (NotificationCentre.IsFalse(p["ok"])) return "Notifications unavailable — " + (NotificationCentre.Str(p["reason"]) ?? "no reason was given");
        int n = (p["toasts"] as JsonArray)?.Count ?? 0, u = (int?)p["unread"] ?? 0;
        return n == 0 ? "Notifications — none" : "Notifications — " + n + (u > 0 ? " (" + u + " new)" : "");
    }

    /// What a click on a card does, or why it does nothing; a picture that could not be shown is named.
    public static string CardTip(JsonObject t)
    {
        string app = NotificationCentre.Str(t["app"]) ?? "the app";
        string opens = NotificationCentre.Str(t["opens"]) ?? "app";
        string tip = opens == "none" ? "Mantle cannot open this one: " + (NotificationCentre.Str(t["opensWhy"]) ?? "no reason was given")
            : opens == "protocol" ? "Open — goes where " + app + " pointed this notification"
            : "Open " + app + " (Windows would also tell it which notification this was; Mantle cannot pass that on yet)";
        if (t["imageNotes"] is JsonArray notes && notes.Count > 0) tip += "\nPicture not shown — " + string.Join("; ", notes.Select(n => NotificationCentre.Str(n) ?? ""));
        return tip;
    }

    public static string Title(JsonObject t) => NotificationCentre.Str(t["title"]) is { Length: > 0 } s ? s : NotificationCentre.Str(t["app"]) is { Length: > 0 } a ? a : "Notification";

    public static string Meta(JsonObject t, DateTimeOffset now) =>
        string.Join(" · ", new[] { NotificationCentre.Str(t["attribution"]), Ago((long?)t["timeMs"] ?? 0, now) }.Where(x => !string.IsNullOrEmpty(x)));

    public static string Letter(string? name) => (name ?? "?").Trim() is { Length: > 0 } s ? char.ToUpperInvariant(s[0]).ToString() : "?";

    /// bar.js on('bar.winkey'): "qs" (Win+A), "close", "open", "windows" (no panel: press Windows' own) or "none".
    public static string WinKey(string? key, bool onlyOpen, string? openKey, bool hasPanel)
    {
        if (key == "a") return "qs";
        if (key != "n") return "none";
        if (openKey == "notifs") return onlyOpen ? "none" : "close";
        return hasPanel ? "open" : "windows";
    }

    /// The panel's note: its own failure first, then a stale list's reason, then the seen file's.
    public static string Note(string note, JsonObject? p)
    {
        if (note.Length > 0) return note;
        bool bad = p != null && NotificationCentre.IsFalse(p["ok"]);
        int n = (p?["toasts"] as JsonArray)?.Count ?? 0;
        if (bad && n > 0) return NotificationCentre.Str(p!["reason"]) ?? "";
        return NotificationCentre.Str(p?["seenNote"]) ?? "";
    }
}
