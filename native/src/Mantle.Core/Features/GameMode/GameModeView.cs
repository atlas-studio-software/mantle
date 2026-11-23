using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using static Mantle.Core.Features.GameMode.GameModeJson;

namespace Mantle.Core.Features.GameMode;

/// <summary>One Details row: its key, label, status (done, same, dry, blocked, failed, skipped, info, plan), badge and words.</summary>
public sealed record GameModeRowView(string Key, string Label, string Status, string Badge, string Text);

/// <summary>What the quick-settings Game mode row shows (ui/bar/gamemode.js view(), pure).</summary>
public sealed class GameModeTileView
{
    public bool Dead, On, Pending, Warn, Auto = true, LiftSaver, Boost = true;
    public string Label = "Turn on", Tip = "", Sub = "", Why = "", Last = "", Crash = "", Rest = "";
    public List<GameModeRowView> Rows = new();
    public List<GameModeRowView>? Plan;
}

/// <summary>ui/bar/gamemode.js's pure half: the state -> the row's words. "Minimal and obvious": one status, one switch
/// that says what a click does, one line under it only when something needs reading, and Details.</summary>
public static class GameModeView
{
    /// info rows are notes: no badge at all
    public static readonly IReadOnlyDictionary<string, string> Badge = new Dictionary<string, string>
    {
        ["done"] = "Done", ["same"] = "No change", ["dry"] = "Test run", ["blocked"] = "Not done", ["failed"] = "Failed", ["skipped"] = "Skipped",
    };

    public static readonly (string Label, string Text)[] PlanRows =
    {
        ("Power mode", "Best performance while you play; your own choice comes back after"),
        ("Background apps", "Efficiency mode for your other apps. Never the game, anti-cheat, Discord, music, recording, mouse or keyboard software, or anything playing sound"),
        ("Mantle itself", "its own windows slowed down the same way"),
        ("Boost", "a minute in, idle background apps hand their unused memory back to Windows, once. Nothing is closed; Helm, Claude, calls and music are left alone"),
        ("Wallpaper", "paused, even for a windowed game"),
        ("Mantle resting", "while a game runs, Mantle closes its hidden windows and hands their shortcuts back to Windows; all back when the game closes"),
    };

    public static string ExeName(string? f) => System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(f ?? "", @"\.exe$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "-Win64-Shipping$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    /// <summary>BAR-808: a second click on the switch this soon after the last one is the same decision (a double click).</summary>
    public const long SwitchGapMs = 500;

    /// <summary>gamemode.js switch click: not while dead, turning on/off or a call is on its way, nor within <see cref="SwitchGapMs"/> of the last click.</summary>
    public static bool SwitchTakes(bool dead, bool pending, bool inflight, long lastClickMs, long nowMs) =>
        !dead && !pending && !inflight && nowMs - lastClickMs >= SwitchGapMs;

    public static string Mins(double? m) => !(m >= 1) ? "under a minute" : m == 1 ? "1 min" : $"{m} min";
    public static string Cap(string? s) { s ??= ""; return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..]; }

    public static GameModeTileView Of(JsonObject? st)
    {
        var v = new GameModeTileView();
        if (st == null || st["error"] != null)
        {
            v.Dead = true; v.Sub = "Unavailable"; v.Warn = true;
            v.Why = "Game mode didn't answer. Close and reopen quick settings to try again";
            v.Tip = "Game mode is unavailable right now";
            return v;
        }
        v.Auto = !False(st["auto"]); v.LiftSaver = True(st["liftSaver"]); v.Boost = !False(st["boost"]);
        if (False(st["enabled"]))
        {
            v.Dead = true; v.Sub = "Switched off";
            v.Why = "Game mode is switched off in Mantle's tray menu. Turn it back on there to use it";
            v.Tip = "Switched off in Mantle's tray menu";
            return v;
        }
        v.On = True(st["on"]);
        string? busy = Str(st["busy"]);
        v.Pending = busy != null;
        string how = Str(st["how"]) ?? "";
        string game = Str(st["game"]?["file"]) is { Length: > 0 } gf && how != "manual" ? ExeName(gf) : "";
        var rest = st["rest"] as JsonObject ?? new JsonObject();
        bool restOn = True(rest["on"]);
        if (busy == "on") { v.Sub = "Turning on…"; v.Label = "Turning on…"; }
        else if (busy == "off") { v.Sub = "Putting things back…"; v.Label = "Turning off…"; }
        else if (v.On) { v.Sub = game.Length > 0 ? "On for " + game : "On"; v.Label = "Turn off"; }
        else if (restOn) v.Sub = "Off · Mantle is resting";
        else if (!v.Auto) v.Sub = "Off · starts only by hand";
        else if (False(st["watching"])) v.Sub = "Off · not watching";
        else v.Sub = "Off · starts with your games";
        v.Tip = v.On ? "Turn game mode off and put everything back" : "Turn game mode on now, for whatever you play next";

        string? restText = Str(rest["text"]);
        if (restOn) v.Rest = restText ?? ("Mantle is resting while " + Str(rest["why"]));
        else if (restText != null) v.Rest = restText;

        var lo = st["leftover"] as JsonObject;
        var bn = st["boostNow"] as JsonObject;
        var fc = st["fromCrash"] as JsonObject;
        if (!restOn && restText != null) { v.Why = Cap(restText); v.Warn = true; }
        else if (Str(lo?["text"]) is { Length: > 0 } lt) { v.Why = Cap(lt); v.Warn = true; }
        else if (!v.On && False(st["watching"]) && Str(st["reason"]) is { Length: > 0 } rs) { v.Why = Cap(rs); v.Warn = true; }
        else if (v.On && Str(bn?["text"]) is { Length: > 0 } bt) { v.Why = Cap(bt); v.Warn = True(bn!["warn"]); }
        else if (Str(fc?["text"]) is { Length: > 0 } ft) { v.Why = "After Mantle restarted: " + ft; v.Warn = False(fc!["ok"]); }
        else if (True(st["dry"])) v.Why = "Test run: everything is planned and shown, nothing on this PC is changed";
        if (Str(fc?["text"]) is { Length: > 0 } ct) v.Crash = "After Mantle restarted: " + ct;

        if (st["actions"] is JsonArray acts)
            foreach (var a in acts)
            {
                if (a is not JsonObject ao) continue;
                string status = Str(ao["status"]) ?? "";
                v.Rows.Add(new(Str(ao["key"]) ?? "", Str(ao["label"]) ?? "", status, Badge.TryGetValue(status, out var b) ? b : "", Cap(Str(ao["text"]))));
            }
        if (v.On && Str(st["game"]?["why"]) is { Length: > 0 } gw && how != "manual") v.Rows.Insert(0, new("why", "Why it is on", "info", "", Cap(gw)));
        if (restOn) v.Rows.Insert(0, new("rest", "Mantle resting", "done", Badge["done"], Cap(v.Rest)));
        if (!v.On && busy == null)
        {
            v.Plan = new();
            foreach (var (l, t) in PlanRows) v.Plan.Add(new("plan", l, "plan", "", Cap(t)));
        }
        if (st["last"] is JsonObject last)
            v.Last = "Last game: " + (Str(last["game"]) is { Length: > 0 } lg ? ExeName(lg) : "turned on by hand") + ", " + Mins(Num(last["minutes"])) + ". " + Cap(Str(last["text"])) + ".";
        if (!restOn && Str(rest["last"]?["text"]) is { Length: > 0 } rl) v.Last = (v.Last.Length > 0 ? v.Last + " " : "") + Cap(rl) + ".";
        return v;
    }
}
