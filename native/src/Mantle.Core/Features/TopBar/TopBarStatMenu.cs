using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.TopBar;

/// <summary>One row of a stat dropdown: a header, a name / value row, the CPU's core bars, an info line (Bad = warn), a
/// separator or "Open Task Manager".</summary>
public enum StatRowKind { Head, Row, Cores, Info, Sep, TaskMgr }

public sealed record StatRow(StatRowKind Kind, string Text = "", string? Value = null, bool Bad = false, IReadOnlyList<double>? Cores = null);

/// <summary>
/// The stat dropdowns (TOP-100..110, ui/topbar/topbar.js#openStat / askDetail / renewDetail / renderStatMenu /
/// openTaskMgr): what each meter's dropdown says, from one topbarstats sample, and when its details are asked for.
/// The surface draws the rows and keeps the drawn ones across samples (only changed words are set).
/// </summary>
public static class TopBarStatMenu
{
    public static readonly string[] Kinds = { "cpu", "mem", "gpu", "net", "ping" };
    /// The sidecar sends a meter's details for 10 s after each ask: the ask is renewed every 5 s while the dropdown is
    /// open, 450 ms after a sample (half-way between two, so the renewal's own sample is not a few ms after the last).
    public const int DetailRenewMs = 5000, RenewAfterSampleMs = 450;
    public const double MinW = 280, MaxWLight = 420, MaxWDark = 440, EdgeGap = 4;
    public const string TopArrive = "Top processes arrive with the next sample.";
    public const string TaskMgrLabel = "Open Task Manager", TaskMgrChord = "Ctrl+Shift+Esc";

    public static bool IsKind(string? k) => k != null && Array.IndexOf(Kinds, k) >= 0;
    /// TOP-101: a meter's details are asked for; never for ping (its sample always carries them).
    public static bool AsksDetail(string? stat) => IsKind(stat) && stat != "ping";
    /// TOP-101 renewDetail: due once 5 s have passed since the last ask and no renewal is already waiting.
    public static bool RenewDue(string? stat, long now, long detailAt, bool waiting) => AsksDetail(stat) && !waiting && now - detailAt >= DetailRenewMs;

    /// TOP-100: the dropdown hangs from the cell's right edge, never closer than 4 px to the window's left.
    public static double Left(double cellRight, double width) => Math.Max(EdgeGap, Math.Round(cellRight - width));

    /// statsDetail's refusal while the bar has stepped aside (and is not peeking); null = ask.
    public static string? Refusal(bool stepAside, bool peekShown, string? kind)
        => stepAside && !peekShown ? (kind == "hidden" ? "the stats are paused while the top bar is hidden by hand" : "the stats are paused while a game has the screen") : null;

    /// TOP-109: the strip's words when Mantle's own Task Manager did not open (why) and Windows' was asked for.
    /// A --test dry run of Windows' says so (Electron's mock never got that far).
    public static string TaskMgrNote(string why, bool ok, string? reason, bool dryRun)
        => !ok ? $"Task Manager: Mantle's did not open ({why}) and Windows' did not either: {reason ?? "no reason came back"}"
         : dryRun ? $"Task Manager: Mantle's did not open ({why}); {reason ?? "dry run (--test)"}"
         : $"Opened Windows' Task Manager: Mantle's did not open ({why})";

    static string? S(JsonObject? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double? N(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        double? d = v.TryGetValue<double>(out var x) ? x : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null;
        return d is double r && double.IsFinite(r) ? r : null;
    }
    static string Gb(double? b) => TopBarModel.Gb(b);

    /// TOP-103..108, TOP-138 (renderStatMenu): the rows of stat's dropdown for one sample (the ping test's own rows
    /// would follow these).
    public static IReadOnlyList<StatRow> Rows(string stat, JsonObject? stats)
    {
        var d = stats?[stat] as JsonObject ?? new JsonObject();
        var rows = new List<StatRow>();
        void Row(string n, string x) => rows.Add(new StatRow(StatRowKind.Row, n, x));
        var top = d["top"] as JsonArray;
        void Top(Func<JsonObject, string> val)
        {
            if (top == null) return;
            rows.Add(new StatRow(StatRowKind.Head, "Top processes"));
            foreach (var p in top.OfType<JsonObject>()) Row(S(p, "name") ?? "", val(p));
        }
        bool ping = stat == "ping";
        switch (stat)
        {
            case "cpu":
                rows.Add(new StatRow(StatRowKind.Head, "CPU · " + TopBarModel.Pct(N(d["pct"]))));
                if (d["cores"] is JsonArray cores && cores.Count > 0) rows.Add(new StatRow(StatRowKind.Cores, Cores: cores.Select(c => N(c) ?? 0).ToArray()));
                Top(p => TopBarModel.Pct(N(p["pct"])));
                break;
            case "mem":
                rows.Add(new StatRow(StatRowKind.Head, $"Memory · {Gb(N(d["used"]))} of {Gb(N(d["total"]))} GB"));
                if (d["commit"] != null) Row("Committed", $"{Gb(N(d["commit"]))} of {Gb(N(d["commitLimit"]))} GB");
                Top(p => Gb(N(p["bytes"])) + " GB");
                break;
            case "gpu":
                rows.Add(new StatRow(StatRowKind.Head, "GPU · " + TopBarModel.Pct(N(d["pct"])) + (S(d, "name") is { Length: > 0 } nm ? " · " + nm : "")));
                foreach (var e in (d["engines"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Row(S(e, "type") ?? "", TopBarModel.Pct(N(e["pct"])));
                if (d["dedicated"] != null) Row("Dedicated memory", Gb(N(d["dedicated"])) + " GB");
                if (d["shared"] != null) Row("Shared memory", Gb(N(d["shared"])) + " GB");
                Top(p => TopBarModel.Pct(N(p["pct"])));
                break;
            case "net":
                rows.Add(new StatRow(StatRowKind.Head, $"Network · ↓{TopBarModel.Rate(N(d["down"]))} ↑{TopBarModel.Rate(N(d["up"]))}"));
                foreach (var a in (d["adapters"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()) Row(S(a, "name") ?? "", $"↓{TopBarModel.Rate(N(a["down"]))} ↑{TopBarModel.Rate(N(a["up"]))}");
                break;
            case "ping":
            {
                bool ok = S(d, "state") == "ok";
                string server = S(d, "server") ?? "";
                string name = ((S(d, "label") is { Length: > 0 } l ? l + " " : "") + server).Trim();
                rows.Add(new StatRow(StatRowKind.Head, "Ping · " + (ok ? TopBarModel.PingMs(N(d["ms"])) : "--") + (server.Length > 0 ? " · " + name : "")));
                if (server.Length > 0)
                {
                    Row("Measured to", (S(d, "host") ?? "") + (S(d, "via") is { Length: > 0 } via ? " (" + via + ")" : ""));
                    Row("Every", Whole((N(d["everyMs"]) ?? 2000) / 1000) + " s, " + (S(d, "method") == "tcp" ? "by connection time" : "by ping"));
                }
                if (d["picked"] is JsonArray picked && picked.Count > 0)
                {
                    rows.Add(new StatRow(StatRowKind.Head, $"Closest server, picked every {Whole((N(d["pickEveryMs"]) ?? 600000) / 60000)} min"));
                    foreach (var p in picked.OfType<JsonObject>())
                        Row(S(p, "name") ?? "", N(p["ms"]) is double ms ? TopBarModel.PingMs(ms) : Regex.IsMatch(S(p, "why") ?? "", "look up") ? "name not found" : "no reply");
                }
                break;
            }
        }
        if (S(d, "reason") is { Length: > 0 } why)
        {
            bool bad = ping ? S(d, "state") is not ("ok" or "measuring") : N(d["pct"]) == null && N(d["used"]) == null && N(d["down"]) == null;
            rows.Add(new StatRow(StatRowKind.Info, why, Bad: bad));
        }
        if (ping && S(d, "test") is { Length: > 0 } test) rows.Add(new StatRow(StatRowKind.Info, test));
        if (top == null && stat != "net" && !ping) rows.Add(new StatRow(StatRowKind.Info, TopArrive));
        if (!ping) { rows.Add(new StatRow(StatRowKind.Sep)); rows.Add(new StatRow(StatRowKind.TaskMgr, TaskMgrLabel, TaskMgrChord)); }
        return rows;
    }

    static string Whole(double v) => TopBarModel.JsRound(v).ToString(CultureInfo.InvariantCulture);

    /// The rows' shape: two samples of the same shape are drawn in place (TOP-102: same element, scroll kept).
    public static string Shape(IReadOnlyList<StatRow> rows)
        => string.Join(",", rows.Select(r => r.Kind == StatRowKind.Cores ? "C" + r.Cores!.Count.ToString(CultureInfo.InvariantCulture) : ((int)r.Kind).ToString(CultureInfo.InvariantCulture)));
}
