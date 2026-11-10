using System;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>
/// The words of quick settings' second tile row (lib/features/_qstiles-core.js + _project-core.js tile()), pure, so a
/// tile and its tooltip are unit tested without a window: Airplane mode, Do not disturb, Focus, Project, Cast.
/// </summary>
/// <remarks>
/// The state is <see cref="QsService"/>'s <c>qs.tiles.state</c>: <c>{ on, dnd: {ok, on, writable, reason, rule},
/// airplane: {ok, on, canSet, reason}, focus: {on, endAt, minutes, dndByFocus} | null, timer: {ok, reason}, project:
/// {on, read: {current, available, unavailable, connected}, readError}, readAt, readError, busy, test }</c>, the shape
/// lib/features/qstiles.js and project.js replay (project folded in, one state for the row).
/// </remarks>
public static class QsTiles
{
    public const int FocusMin = 30;
    public static readonly (string Key, string Label)[] Order =
        { ("airplane", "Airplane mode"), ("dnd", "Do not disturb"), ("focus", "Focus"), ("project", "Project"), ("cast", "Cast") };

    public static string Name(string key)
    {
        foreach (var (k, l) in Order) if (k == key) return l;
        return key;
    }

    static string Cap(string? s) { s ??= ""; return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..]; }
    static string Mins(double ms) => Math.Max(1, (int)Math.Ceiling(ms / 60000)) + " min";
    static JsonObject? O(JsonObject? st, string k) => st?[k] as JsonObject;
    static bool Off(JsonObject? st) => st != null && QsParts.IsFalse(st["on"]);
    static bool Has(JsonObject? o, string k) => o != null && o[k] != null;

    static QsTileView Switched(string key) => new(key, Name(key), "Switched off",
        Name(key) + ": this tile is switched off in Mantle’s tray menu (Quick settings tiles)", false, true);

    static QsTileView ReadingTile(string key, JsonObject? st) => new(key, Name(key),
        st != null && QsParts.Str(st["readError"]) != null ? "Unknown" : "Reading…", Name(key) + " is still being read", false, false, Reading: true);

    public static QsTileView View(string key, JsonObject? st, double nowMs) => key switch {
        "dnd" => Dnd(st),
        "airplane" => Airplane(st),
        "focus" => Focus(st, nowMs),
        "cast" => Cast(),
        _ => Project(st),
    };

    /// _qstiles-core.js dnd(): never a fake Off while it is being read.
    public static QsTileView Dnd(JsonObject? st)
    {
        if (Off(st)) return Switched("dnd");
        var d = O(st, "dnd");
        if (d == null) return ReadingTile("dnd", st);
        if (QsParts.IsFalse(d["ok"]) || QsParts.IsFalse(d["writable"]))
            return new("dnd", "Do not disturb", "Unavailable", "Do not disturb: " + Cap(QsParts.Str(d["reason"]) ?? "Windows did not say whether it is on"), false, true);
        var f = O(st, "focus");
        bool byFocus = f != null && QsParts.IsTrue(f["on"]) && QsParts.IsTrue(f["dndByFocus"]);
        if (QsParts.IsTrue(d["on"]))
            return new("dnd", "Do not disturb", byFocus ? "On · Focus" : "On",
                "Do not disturb is on: banners are silenced (Mantle’s too).\nClick to turn it off" + (byFocus ? " (Focus keeps running)" : ""), true, false);
        if (QsParts.Str(d["rule"]) is { Length: > 0 } rule)
            return new("dnd", "Do not disturb", "Off · a rule is on",
                "One of Windows’ automatic rules is silencing banners right now (" + rule + ").\nClick to turn Do not disturb on yourself", false, false);
        return new("dnd", "Do not disturb", "Off", "Turn Do not disturb on: banners are silenced, Mantle’s too", false, false);
    }

    /// _qstiles-core.js airplane(): dead with its reason when Mantle cannot switch it.
    public static QsTileView Airplane(JsonObject? st)
    {
        if (Off(st)) return Switched("airplane");
        var a = O(st, "airplane");
        if (a == null) return ReadingTile("airplane", st);
        bool known = Has(a, "on"), on = QsParts.IsTrue(a["on"]);
        if (QsParts.IsFalse(a["ok"]) || !QsParts.IsTrue(a["canSet"]) || !known)
            return new("airplane", "Airplane mode", !known ? "Unavailable" : on ? "On" : "Off",
                "Airplane mode: " + Cap(QsParts.Str(a["reason"]) ?? "Mantle cannot switch airplane mode on this PC"), on, true);
        return on
            ? new("airplane", "Airplane mode", "On", "Airplane mode is on: Wi-Fi, Bluetooth and mobile data are off.\nClick to turn it off", true, false)
            : new("airplane", "Airplane mode", "Off", "Turn airplane mode on: Wi-Fi, Bluetooth and mobile data go off", false, false);
    }

    /// _qstiles-core.js focus(): Mantle's 30 minute timer plus Do not disturb. <paramref name="nowMs"/> is wall-clock
    /// (Unix ms), as the record's endAt is.
    public static QsTileView Focus(JsonObject? st, double nowMs)
    {
        if (Off(st)) return Switched("focus");
        var f = O(st, "focus");
        var t = O(st, "timer");
        if (f != null && QsParts.IsTrue(f["on"])) {
            double left = Math.Max(0, (QsParts.Num(f["endAt"]) ?? 0) - nowMs);
            bool byFocus = QsParts.IsTrue(f["dndByFocus"]);
            return new("focus", "Focus", Mins(left) + " left",
                "Focus: " + Mins(left) + " left on Mantle’s timer" + (byFocus ? ", with Do not disturb on" : "") +
                ".\nClick to end it" + (byFocus ? " (Do not disturb goes back off)" : ""), true, false);
        }
        if (t != null && QsParts.IsFalse(t["ok"]))
            return new("focus", "Focus", "Unavailable", "Focus: " + Cap(QsParts.Str(t["reason"]) ?? "Mantle’s timer is not running"), false, true);
        var d = O(st, "dnd");
        bool quiet = d != null && !QsParts.IsFalse(d["ok"]) && !QsParts.IsFalse(d["writable"]);
        return new("focus", "Focus", FocusMin + " min",
            "Start " + FocusMin + " minutes of focus: Mantle’s timer" + (quiet ? " and Do not disturb" : "") +
            (quiet ? "" : "\n(Do not disturb cannot be switched here: " + (QsParts.Str(d?["reason"]) ?? "it is still being read") + ")"), false, false);
    }

    /// _qstiles-core.js cast(): Windows' own Cast panel (Win+K); never lit.
    public static QsTileView Cast() => new("cast", "Cast", "Windows’ panel",
        "Connect to a wireless display: opens Windows’ own Cast panel (Win+K).\nWindows gives other apps no way to start screen casting, so Mantle hands this one over", false, false);

    static readonly (string Id, string Label, string Short)[] Modes =
        { ("internal", "PC screen only", "PC screen only"), ("clone", "Duplicate", "Duplicate"), ("extend", "Extend", "Extend"), ("external", "Second screen only", "Second screen") };
    public const string OneDisplay = "Only one display is connected. Connect another display to duplicate or extend.";

    static bool Single(JsonObject rd)
    {
        if (QsParts.Num(rd["connected"]) is double n) return n <= 1;
        if (rd["available"] is not JsonArray av) return false;
        bool clone = false, extend = false;
        foreach (var x in av) { var s = QsParts.Str(x); if (s == "clone") clone = true; if (s == "extend") extend = true; }
        return !clone && !extend;
    }

    /// _project-core.js tile() (+ qs-tiles.js's switched-off words): never lit, like Windows' own Project tile, since it
    /// opens a choice; it is not a switch.
    public static QsTileView Project(JsonObject? st)
    {
        var p = O(st, "project");
        if (p != null && QsParts.IsFalse(p["on"]))
            return new("project", "Project", "Switched off", "Project: Mantle’s pop-up is switched off in the tray menu. Windows’ own is Win+P", false, true);
        var rd = O(p, "read");
        string? err = QsParts.Str(p?["readError"]);
        if (rd == null)
            return new("project", "Project", err != null ? "Unknown" : "Choose…",
                err != null ? "Project: your displays could not be read (" + err + "). Click to choose anyway" : "Choose how your screens are used (Win+P)", false, false);
        string? cur = QsParts.Str(rd["current"]);
        (string Id, string Label, string Short)? m = null;
        foreach (var x in Modes) if (x.Id == cur) m = x;
        string sub = m is { } mm ? mm.Short : cur == "custom" ? "Custom layout" : "Choose…";
        if (Single(rd)) return new("project", "Project", sub, OneDisplay + "\nClick to see the choices (Win+P)", false, false);
        return new("project", "Project", sub, "Now: " + (m is { } m2 ? m2.Label : "a custom layout") + ". Click to choose how your screens are used (Win+P)", false, false);
    }

    /// _qstiles-core.js guess(): the state drawn the moment a tile is clicked (the reply has the last word). A new object.
    public static JsonObject Guess(JsonObject? st, string tile, bool want, double nowMs)
    {
        var s = (JsonObject?)st?.DeepClone() ?? new JsonObject();
        if (tile == "dnd" && s["dnd"] is JsonObject d) d["on"] = want;
        if (tile == "airplane" && s["airplane"] is JsonObject a) a["on"] = want;
        if (tile == "focus") {
            var dd = s["dnd"] as JsonObject;
            s["focus"] = want ? new JsonObject { ["on"] = true, ["endAt"] = nowMs + FocusMin * 60000.0, ["minutes"] = FocusMin, ["dndByFocus"] = !(dd != null && QsParts.IsTrue(dd["on"])) } : null;
            if (want && dd != null && !QsParts.IsFalse(dd["writable"])) dd["on"] = true;
        }
        return s;
    }

    /// The note a reply leaves under the row (qs-tiles.js click()): a refusal's reason, or a dry run's / a partial
    /// success's words with the first letter up.
    public static string? Note(string tile, bool ok, string? reason)
    {
        if (!ok) return reason is { Length: > 0 } ? reason : Name(tile) + " did not change, and Mantle gave no reason";
        return reason is { Length: > 0 } ? Cap(reason) : null;
    }
}
