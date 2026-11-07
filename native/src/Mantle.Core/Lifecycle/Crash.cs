using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mantle.Core.Lifecycle;

/// <summary>
/// main.js's crash road (SAFE-018, SAFE-019, CORE-038): crashPlan (3 restarts in 10 minutes, the 4th
/// stays down), crashWhat (the first line, at most 160 chars, no stack), recoveryLine (the tray line for
/// an hour), the crash.json record, and the once-per-message unhandled-rejection log.
/// </summary>
public static class Crash
{
    public const string FileName = "crash.json";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MaxRestarts = 3;

    public sealed record Plan(bool Restart, IReadOnlyList<long> History, int Recent, string Why);

    /// crashPlan(history, now): times are Unix milliseconds.
    public static Plan Decide(IEnumerable<long>? history, long now)
    {
        var recent = (history ?? Array.Empty<long>()).Where(t => t <= now && now - t < Window.TotalMilliseconds).ToList();
        recent.Add(now);
        var keep = recent.Skip(Math.Max(0, recent.Count - 10)).ToList();
        if (recent.Count > MaxRestarts)
            return new Plan(false, keep, recent.Count, $"stopped with an error {recent.Count} times in 10 minutes, so it was not started again");
        return new Plan(true, keep, recent.Count, recent.Count == 1 ? "stopped with an error, so it is starting again" : $"stopped with an error ({recent.Count} times in 10 minutes), so it is starting again");
    }

    /// crashWhat(err): the message's first line, whitespace folded, at most 160 chars.
    public static string What(string? message)
    {
        string line = Regex.Replace((message ?? "").Split('\n')[0], @"\s+", " ").Trim();
        return line.Length > 160 ? line[..159] + "…" : (line.Length > 0 ? line : "no message");
    }

    /// recoveryLine(rec, now): null unless the last crash is under an hour old.
    public static string? RecoveryLine(long? at, bool restarted, long now, Func<long, string> fmtTime)
    {
        if (at is not { } t || t > now || now - t > 60 * 60 * 1000) return null;
        return restarted ? $"Restarted by itself after an error at {fmtTime(t)}" : $"Stopped after repeated errors at {fmtTime(t)}";
    }

    /// The record as main.js writes it: {history:[...], last:{at, what, restarted}}.
    public static string Json(Plan plan, long now, string what)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf)) {
            w.WriteStartObject();
            w.WriteStartArray("history");
            foreach (var t in plan.History) w.WriteNumberValue(t);
            w.WriteEndArray();
            w.WriteStartObject("last");
            w.WriteNumber("at", now);
            w.WriteString("what", what);
            w.WriteBoolean("restarted", plan.Restart);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    /// Read crash.json: (history, last at, last restarted); anything unreadable is an empty history.
    public static (List<long> History, long? At, bool Restarted) Read(string file)
    {
        try {
            using var d = JsonDocument.Parse(File.ReadAllText(file));
            var h = new List<long>();
            if (d.RootElement.TryGetProperty("history", out var ha) && ha.ValueKind == JsonValueKind.Array)
                foreach (var x in ha.EnumerateArray()) if (x.TryGetInt64(out long v)) h.Add(v);
            long? at = null; bool restarted = false;
            if (d.RootElement.TryGetProperty("last", out var l) && l.ValueKind == JsonValueKind.Object) {
                if (l.TryGetProperty("at", out var a) && a.TryGetInt64(out long av)) at = av;
                restarted = l.TryGetProperty("restarted", out var r) && r.ValueKind == JsonValueKind.True;
            }
            return (h, at, restarted);
        } catch (Exception) { return (new List<long>(), null, false); }
    }

    /// <summary>The whole uncaught-exception step: read, plan, write (only where this run may write).</summary>
    /// <returns>The plan; a failed write (or a refused one) never restarts, as in main.js.</returns>
    public static Plan Record(string dataRoot, bool mayWrite, string? message, long now)
    {
        string file = Path.Combine(dataRoot, FileName);
        try {
            var prev = Read(file);
            var plan = Decide(prev.History, now);
            if (!mayWrite) return plan with { Restart = false, Why = "stopped with an error, and the crash could not be written down (this run may not write its profile), so it was not started again" };
            Directory.CreateDirectory(dataRoot);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, Json(plan, now, What(message)));
            File.Move(tmp, file, overwrite: true);
            return plan;
        } catch (Exception e) {
            return new Plan(false, Array.Empty<long>(), 0, $"stopped with an error, and the crash could not be written down ({e.Message}), so it was not started again");
        }
    }
}

/// <summary>main.js makeRejectionLog: once per distinct message, then at the 10th, 100th, 1000th repeat; never fatal.</summary>
public sealed class RejectionLog
{
    readonly Action<string> _write;
    readonly int _maxKeys;
    readonly LinkedList<string> _order = new();
    readonly Dictionary<string, (int N, LinkedListNode<string> Node)> _seen = new(StringComparer.Ordinal);

    public RejectionLog(Action<string> write, int maxKeys = 100) { _write = write; _maxKeys = maxKeys; }

    public int Note(Exception e) => Note(e.Message, e.ToString());

    public int Note(string message, string full)
    {
        string key = Crash.What(message);
        int n = 1;
        if (_seen.Remove(key, out var prev)) { n = prev.N + 1; _order.Remove(prev.Node); }
        _seen[key] = (n, _order.AddLast(key));
        while (_seen.Count > _maxKeys) { var f = _order.First!; _order.RemoveFirst(); _seen.Remove(f.Value); }
        if (n == 1) _write("mantle: unhandled promise rejection: " + full);
        else if (Regex.IsMatch(n.ToString(System.Globalization.CultureInfo.InvariantCulture), "^10+$")) _write($"mantle: the same unhandled promise rejection has now happened {n} times: {key}");
        return n;
    }
}
