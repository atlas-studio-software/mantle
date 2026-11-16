using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.GameMode;

/// <summary>
/// The JSON half of game mode's service: the helper's gamemode.scan reply read into <see cref="GameModePlan"/>'s
/// records, and _gamemode-plan.js applyItem / restoreItem / recordEntry (what is sent to gamemode.apply and what the
/// restore record keeps). Pure: the tests drive it with made-up replies.
/// </summary>
public static class GameModeJson
{
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static bool True(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static bool False(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && !b;
    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }
    public static int Int(JsonNode? n) => Num(n) is double d ? (int)d : 0;
    public static long Long(JsonNode? n) => Num(n) is double d ? (long)d : 0;

    /// fg + its verdict (sidecar GameMode.cs Scan: fg.verdict = {gameStore, quns, ...}).
    public static GameFg? Fg(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        var v = o["verdict"] as JsonObject;
        int? quns = Num(v?["quns"]) is double q ? (int)q : null;
        return new GameFg(Int(o["pid"]), Str(o["exe"]), Str(o["title"]), True(o["self"]), True(o["fullscreen"]), True(v?["gameStore"]), quns);
    }

    public static GameProc Proc(JsonObject o) => new(
        Int(o["pid"]), Str(o["file"]), Int(o["ppid"]), Num(o["session"]) is double s ? (int)s : null, Str(o["path"]),
        o["created"] is JsonValue c ? (c.TryGetValue<string>(out var cs) ? cs : Num(c)?.ToString(System.Globalization.CultureInfo.InvariantCulture)) : null,
        Str(o["prio"]), True(o["pending"]), True(o["elevated"]), Str(o["why"]), Num(o["cpuMs"]), Long(o["ws"]));

    public static List<GameProc> Procs(JsonNode? n) => n is JsonArray a ? a.OfType<JsonObject>().Select(Proc).ToList() : new List<GameProc>();

    /// _gamemode-plan.js applyItem: Efficiency mode on, and below-normal when the process runs at normal.
    public static JsonObject ApplyItem(GameTarget t) => new()
    {
        ["pid"] = t.Pid, ["created"] = t.Created, ["file"] = t.File,
        ["eco"] = new JsonObject { ["control"] = 1, ["state"] = 1 },
        ["prio"] = t.Prio == "normal" ? "belownormal" : null, ["prioIf"] = "normal",
    };

    /// _gamemode-plan.js restoreItem: the record entry back as it was, only if it still is what game mode set.
    public static JsonObject RestoreItem(JsonObject r)
    {
        var it = new JsonObject { ["pid"] = r["pid"]?.DeepClone(), ["created"] = r["created"]?.DeepClone(), ["file"] = r["file"]?.DeepClone() };
        if (r["eco"] is JsonObject eco)
        {
            it["eco"] = eco["was"] is JsonObject was ? was.DeepClone() : new JsonObject { ["control"] = 0, ["state"] = 0 };
            it["ecoIf"] = eco["set"]?.DeepClone();
        }
        if (r["prio"] is JsonObject prio) { it["prio"] = prio["was"]?.DeepClone(); it["prioIf"] = prio["set"]?.DeepClone(); }
        return it;
    }

    /// _gamemode-plan.js recordEntry: what to remember from an apply result so a restart can put it back (null = nothing changed).
    public static JsonObject? RecordEntry(GameTarget t, JsonObject? res)
    {
        if (res == null) return null;
        var e = new JsonObject { ["pid"] = t.Pid, ["created"] = Str(res["created"]) ?? t.Created, ["file"] = Str(res["file"]) ?? t.File, ["group"] = t.Group };
        if (res["eco"] is JsonObject eco && True(eco["wrote"])) e["eco"] = new JsonObject { ["was"] = eco["before"]?.DeepClone(), ["set"] = new JsonObject { ["control"] = 1, ["state"] = 1 } };
        if (res["prio"] is JsonObject prio && True(prio["wrote"])) e["prio"] = new JsonObject { ["was"] = prio["before"]?.DeepClone(), ["set"] = "belownormal" };
        return e["eco"] != null || e["prio"] != null ? e : null;
    }

    public static GameEased Eased(JsonObject e) => new(Int(e["pid"]), Str(e["created"]), Str(e["file"]), Str(e["group"]) ?? "background");

    /// "Discord.exe ×2, claude.exe": image names only (a path is cut to its file name), counts.
    public static string ExeNames(IEnumerable<string?> files)
    {
        var m = new List<(string K, int N)>();
        foreach (var f in files)
        {
            var k = GameModePlan.BaseName(f); if (k.Length == 0) k = "?";
            int i = m.FindIndex(x => x.K == k);
            if (i < 0) m.Add((k, 1)); else m[i] = (k, m[i].N + 1);
        }
        return string.Join(", ", m.Select(x => x.N > 1 ? $"{x.K} ×{x.N}" : x.K));
    }

    /// One action row of the tile's Details.
    public static JsonObject Act(string key, string label, string status, string text) => new() { ["key"] = key, ["label"] = label, ["status"] = status, ["text"] = text };

    public static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
    public static JsonNode? Node(JsonElement e) => e.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());

    /// The sidecar's error text in words for the tile: the exception type names and hex codes are cut, the sentence kept.
    public static string Plain(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "no reason was given";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\b[A-Z][A-Za-z]+Exception:?\s*", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\(0x[0-9A-Fa-f]{8}\)", "");
        s = s.Trim();
        return s.Length == 0 ? "Windows refused without a reason" : s;
    }
}
