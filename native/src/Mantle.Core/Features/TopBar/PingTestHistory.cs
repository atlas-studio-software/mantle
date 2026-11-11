using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mantle.Core.Data;

namespace Mantle.Core.Features.TopBar;

/// <summary>
/// lib/features/_pingtest-history.js (TOP-153..159): the PING dropdown's list of past ping tests, kept in
/// <c>&lt;data root&gt;\ping-tests.json</c> as <c>{version: 1, tests: [...]}</c> (indent 2, F-024).
///
/// Every finished run (phase "done") becomes one small entry. The newest <see cref="MaxUnsaved"/> unsaved tests are
/// kept; a saved test has a name and is never pruned; only a saved test can be renamed; any test can be removed.
/// Every write is a .tmp file and a rename through <see cref="SafeFile"/> (the ONE write gate): a --test run writes its
/// own profile, and the user's real profile only in a --live run that is the only shell. A refused or failed write keeps
/// the tests in memory and says "history could not be saved: why" until a later write works.
/// A file that cannot be read or makes no sense is moved aside to ping-tests.bad.json (never lost: when that name is
/// taken, ping-tests.bad-&lt;ms&gt;.json) and the history starts fresh with the reason in view().problem for the session.
/// If it cannot be moved aside, new tests are kept in memory only and the file is left alone.
/// Log lines carry counts and reasons only: never a Wi-Fi name, never a test's name.
/// </summary>
public sealed class PingTestHistory
{
    public const int MaxUnsaved = 50;
    public const int NameMax = 60;
    public const string FileName = "ping-tests.json";
    static readonly string[] RowFields = { "kind", "name", "host", "via", "method", "sent", "got", "avg", "min", "max", "jitter", "loss", "why", "gaveUp", "best", "iface", "wifi" };

    readonly string _file, _badName, _dir;
    readonly SafeFile _files;
    readonly Action<string> _log;
    readonly Func<double> _now;
    bool _loaded, _memoryOnly;
    List<JsonObject> _tests = new();
    string? _readProblem, _saveProblem;

    public PingTestHistory(string dataRoot, SafeFile files, Action<string>? log = null, Func<double>? now = null)
    {
        _dir = dataRoot;
        _file = Path.Combine(dataRoot, FileName);
        _badName = Path.GetFileNameWithoutExtension(FileName) + ".bad.json";
        _files = files;
        _log = log ?? (_ => { });
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public string FilePath => _file;

    /// checkName: 1 to 60 characters after trimming, no control characters.
    public static (bool Ok, string? Name, string? Reason) CheckName(JsonNode? v)
    {
        if (v is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String) return (false, null, "a name needs 1 to 60 characters");
        var name = jv.GetValue<string>().Trim();
        if (name.Length < 1 || name.Length > NameMax) return (false, null, "a name needs 1 to 60 characters");
        if (Regex.IsMatch(name, "[\\x00-\\x1f\\x7f]")) return (false, null, "a name cannot hold control characters");
        return (true, name, null);
    }

    static JsonNode? Copy(JsonNode? v) => v == null ? null : JsJson.Clone(v);
    static JsonNode? Val(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) ? Copy(v) : null;
    static bool IsStr(JsonNode? v) => v is JsonValue x && x.GetValueKind() == JsonValueKind.String;
    static bool Truthy(JsonNode? v) => v switch
    {
        null => false,
        JsonValue x when x.GetValueKind() == JsonValueKind.String => x.GetValue<string>().Length > 0,
        JsonValue x when x.GetValueKind() == JsonValueKind.False => false,
        JsonValue x when x.GetValueKind() == JsonValueKind.Number => x.GetValue<double>() != 0,
        _ => true,
    };
    static JsonNode? OrNull(JsonObject o, string k) => o.TryGetPropertyValue(k, out var v) && Truthy(v) ? Copy(v) : null;
    static double Num(JsonNode? v) => v is JsonValue x && x.GetValueKind() == JsonValueKind.Number ? x.GetValue<double>() : 0;

    static JsonObject SpeedOf(JsonNode? s)
    {
        var o = s as JsonObject ?? new JsonObject();
        var r = new JsonObject();
        foreach (var k in new[] { "mbps", "bytes", "why", "state", "server", "note" }) r[k] = Val(o, k);
        return r;
    }

    /// entryOf: one history entry from a finished run, or null (not done, or no start time).
    public static JsonObject? EntryOf(JsonObject? run)
    {
        if (run == null || run["phase"] is not JsonValue ph || ph.GetValueKind() != JsonValueKind.String || ph.GetValue<string>() != "done" || !Truthy(run["startedAt"])) return null;
        var rows = new JsonArray();
        if (run["rows"] is JsonArray ra)
            foreach (var r in ra.OfType<JsonObject>())
            {
                var o = new JsonObject();
                foreach (var k in RowFields) o[k] = Val(r, k);
                rows.Add((JsonNode)o);
            }
        var b = rows.OfType<JsonObject>().FirstOrDefault(r => r["best"] is JsonValue bv && bv.GetValueKind() == JsonValueKind.True);
        var rt = rows.OfType<JsonObject>().FirstOrDefault(r => IsStr(r["kind"]) && r["kind"]!.GetValue<string>() == "router");
        return new JsonObject
        {
            ["id"] = Id(run["startedAt"], run["run"]),
            ["at"] = Copy(run["startedAt"]),
            ["endedAt"] = Val(run, "endedAt"),
            ["name"] = null,
            ["saved"] = false,
            ["wifi"] = OrNull(run, "wifi"),
            ["wifiChanged"] = OrNull(run, "wifiChanged"),
            ["linkEnd"] = OrNull(run, "linkEnd"),
            ["linkChanged"] = OrNull(run, "linkChanged"),
            ["label"] = Val(run, "label"),
            ["host"] = Val(run, "host"),
            ["best"] = b == null ? null : new JsonObject { ["name"] = Copy(b["name"]), ["avg"] = Copy(b["avg"]), ["jitter"] = Copy(b["jitter"]), ["loss"] = Copy(b["loss"]) },
            ["router"] = rt == null ? null : new JsonObject { ["host"] = Copy(rt["host"]), ["avg"] = Copy(rt["avg"]), ["jitter"] = Copy(rt["jitter"]), ["loss"] = Copy(rt["loss"]), ["iface"] = Copy(rt["iface"]) },
            ["routerWhy"] = OrNull(run, "routerWhy"),
            ["listWhy"] = OrNull(run, "listWhy"),
            ["down"] = SpeedOf(run["down"]),
            ["up"] = SpeedOf(run["up"]),
            ["rows"] = rows,
        };
    }

    /// `${startedAt}-${run}` as JS prints the two numbers.
    public static string Id(JsonNode? startedAt, JsonNode? run)
    {
        static string S(JsonNode? v) => v is JsonValue x && x.GetValueKind() == JsonValueKind.Number ? JsJson.Number(x.GetValue<double>()) : v is JsonValue s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : "undefined";
        return S(startedAt) + "-" + S(run);
    }

    void Say(string line) { try { _log("ping test history: " + line); } catch { /* a broken logger never breaks the history */ } }

    void MoveAside(string reason)
    {
        string bad = Path.Combine(_dir, _badName);
        string? r2 = _files.MoveAside(_file, bad);
        if (r2 == "EEXIST" || (r2 != null && File.Exists(bad)))
        {
            // an older bad file is there already: keep both (Electron's rename would replace the older one)
            bad = Path.Combine(_dir, Path.GetFileNameWithoutExtension(FileName) + ".bad-" + ((long)_now()).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            r2 = _files.MoveAside(_file, bad);
        }
        if (r2 == null)
        {
            _readProblem = $"history could not be read: {reason}; starting fresh, old file kept as {Path.GetFileName(bad)}";
            Say($"could not read the file ({reason}); moved it aside, starting fresh");
            return;
        }
        _memoryOnly = true;
        _readProblem = $"history could not be read: {reason}; the old file could not be moved aside ({r2}), so new tests are kept only until Mantle restarts";
        Say($"could not read the file ({reason}) nor move it aside ({r2}); keeping tests in memory only");
    }

    static readonly Regex AtPos = new(@"(?:at position|BytePositionInLine:)\s*(\d+)", RegexOptions.Compiled);

    void Load()
    {
        if (_loaded) return;
        _loaded = true;
        string raw;
        try { raw = JsJson.DecodeUtf8(File.ReadAllBytes(_file)); }
        catch (Exception e)
        {
            if (SafeFile.Code(e) == "ENOENT") return;
            MoveAside(SafeFile.Code(e) == "EPERM" ? "permission denied" : "it could not be opened (" + SafeFile.Code(e) + ")");
            return;
        }
        JsonNode? data;
        try { data = JsJson.Parse(raw); }
        catch (JsonException e)
        {
            // the parser's own message can quote the file (a Wi-Fi or test name): only where it broke is kept
            var m = AtPos.Match(e.Message);
            MoveAside("the file is damaged" + (m.Success ? " near character " + m.Groups[1].Value : ""));
            return;
        }
        if (data is not JsonObject o || o["tests"] is not JsonArray arr) { MoveAside("the file does not hold a list of tests"); return; }
        var seen = new HashSet<string>();
        int dropped = 0;
        foreach (var t in arr)
        {
            if (t is not JsonObject to || !IsStr(to["id"]) || !seen.Add(to["id"]!.GetValue<string>())) { dropped++; continue; }
            _tests.Add((JsonObject)JsJson.Clone(to)!);
        }
        Sort();
        if (dropped > 0) Say($"loaded {_tests.Count} tests; dropped {dropped} entries that made no sense");
    }

    void Sort() => _tests = _tests.OrderByDescending(t => Num(t["at"])).ToList();   // stable, newest first

    void Persist()
    {
        if (_memoryOnly) return;
        var doc = new JsonObject { ["version"] = 1, ["tests"] = new JsonArray(_tests.Select(t => (JsonNode)JsJson.Clone(t)!).ToArray()) };
        string? why = _files.WriteAtomic(_file, JsJson.ToFileBytes(doc, 2, false), _file + ".tmp");
        if (why == null)
        {
            if (_saveProblem != null) Say("saving works again");
            _saveProblem = null;
            return;
        }
        string next = "history could not be saved: " + why;
        if (next != _saveProblem) Say($"could not save {_tests.Count} tests ({why}); kept in memory");
        _saveProblem = next;
    }

    void Prune()
    {
        Sort();
        int unsaved = 0;
        _tests = _tests.Where(t => Saved(t) || ++unsaved <= MaxUnsaved).ToList();
    }

    static bool Saved(JsonObject t) => t["saved"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
    JsonObject? Find(string id) => _tests.FirstOrDefault(t => t["id"]?.GetValue<string>() == id);
    static JsonObject Gone() => new() { ["ok"] = false, ["reason"] = "that test is no longer in the history" };
    static JsonObject No(string why) => new() { ["ok"] = false, ["reason"] = why };

    /// view(): {tests (copies, newest first), problem (read and save problems joined by " · ") | null, max}.
    public JsonObject View()
    {
        Load();
        var p = new[] { _readProblem, _saveProblem }.Where(x => x != null).ToArray();
        return new JsonObject
        {
            ["tests"] = new JsonArray(_tests.Select(t => (JsonNode)JsJson.Clone(t)!).ToArray()),
            ["problem"] = p.Length > 0 ? string.Join(" · ", p) : null,
            ["max"] = MaxUnsaved,
        };
    }

    /// add(run): the entry (a copy) when it joined, null when the run is not a finished one or is already kept.
    public JsonObject? Add(JsonObject? run)
    {
        Load();
        var e = EntryOf(run);
        if (e == null || Find(e["id"]!.GetValue<string>()) != null) return null;
        _tests.Add(e);
        Prune();
        Persist();
        return (JsonObject)JsJson.Clone(e)!;
    }

    public JsonObject Save(string id, JsonNode? name)
    {
        Load();
        var t = Find(id);
        if (t == null) return Gone();
        var c = CheckName(name);
        if (!c.Ok) return No(c.Reason!);
        t["name"] = c.Name; t["saved"] = true;
        Persist();
        return new JsonObject { ["ok"] = true, ["entry"] = JsJson.Clone(t) };
    }

    public JsonObject Rename(string id, JsonNode? name)
    {
        Load();
        var t = Find(id);
        if (t == null) return Gone();
        if (!Saved(t)) return No("only a saved test can be renamed");
        var c = CheckName(name);
        if (!c.Ok) return No(c.Reason!);
        t["name"] = c.Name;
        Persist();
        return new JsonObject { ["ok"] = true, ["entry"] = JsJson.Clone(t) };
    }

    public JsonObject Remove(string id)
    {
        Load();
        if (Find(id) == null) return Gone();
        _tests = _tests.Where(t => t["id"]?.GetValue<string>() != id).ToList();
        Persist();
        return new JsonObject { ["ok"] = true };
    }
}
