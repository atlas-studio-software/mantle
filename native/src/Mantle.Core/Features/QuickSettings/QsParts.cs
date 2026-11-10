using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.QuickSettings;

// The pure half of the quick-settings panel's data (main.js QS_PARTS, qsNormalize, qsCompose, QS_WRITE, QS_LEVEL),
// with no platform and no clock, so the unit tests drive every rule. QsService is the host-thread half.

/// <summary>One part of the panel and the sidecar verb that reads it (main.js QS_PARTS).</summary>
public sealed record QsPart(string Key, string Verb, string Label);

/// <summary>One switch or level qs.set can change (main.js QS_WRITE).</summary>
/// <param name="Name">The qs.set part name (<c>volume</c>, <c>mute</c>, <c>wifi</c>, ...).</param>
/// <param name="Key">The panel part its read-back lands in (<c>mute</c> lands in <c>volume</c>).</param>
/// <param name="Verb">The sidecar verb it is sent as.</param>
public sealed record QsWrite(string Name, string Key, string Verb, string Label, Func<JsonElement, string?> Check, Func<JsonElement, JsonObject> Args, Func<JsonObject, JsonNode?> Read);

public static class QsParts
{
    /// The six parts, in main.js order.
    public static readonly IReadOnlyList<QsPart> All = new[]
    {
        new QsPart("volume", "volume", "the volume"),
        new QsPart("wifi", "wifi", "Wi-Fi"),
        new QsPart("bluetooth", "bluetooth", "Bluetooth"),
        new QsPart("brightness", "brightness", "the screen brightness"),
        new QsPart("nightlight", "nightlight", "Night light"),
        new QsPart("power", "power", "the battery"),
    };

    public static QsPart? Find(string key)
    {
        foreach (var p in All) if (p.Key == key) return p;
        return null;
    }

    /// <summary>
    /// qsNormalize: one sidecar answer as a part. The sidecar's OWN ok/reason win whenever it gave them (a part it is
    /// still reading for the first time is ok:false, pending:true with a reason, never drawn as a reading); a bare
    /// answer with no ok (volume's {level, muted}) is a reading, so it is ok. <c>read</c> is dropped.
    /// </summary>
    public static JsonObject Normalize(JsonNode? r, string label)
    {
        if (r is not JsonObject src) return Fail($"{label} cannot be read: the sidecar answered with nothing");
        var part = new JsonObject { ["ok"] = true, ["reason"] = null };
        foreach (var kv in src) {
            if (kv.Key == "read") continue;
            part[kv.Key] = kv.Value?.DeepClone();
        }
        if (IsFalse(part["ok"]) && string.IsNullOrEmpty(Str(part["reason"])))
            part["reason"] = $"{label} cannot be read, and the sidecar gave no reason";
        return part;
    }

    /// A part that could not be read, with its reason.
    public static JsonObject Fail(string reason) => new() { ["ok"] = false, ["reason"] = reason };

    /// A part not read yet (qsCompose's placeholder).
    public static JsonObject Pending(string label) => new() { ["ok"] = false, ["pending"] = true, ["reason"] = $"{label} has not been read yet" };

    /// <summary>
    /// qsCompose: the whole panel from its parts. <c>ok</c> when the two things it is mostly for answered (volume and
    /// power); <c>reason</c> joins every dead part's reason with "; ", null when none is dead.
    /// </summary>
    public static JsonObject Compose(IReadOnlyDictionary<string, JsonObject>? parts)
    {
        var o = new JsonObject { ["ok"] = false, ["reason"] = null };
        var dead = new List<string>();
        foreach (var p in All) {
            JsonObject part = parts != null && parts.TryGetValue(p.Key, out var got) ? (JsonObject)got.DeepClone() : Pending(p.Label);
            o[p.Key] = part;
            if (!IsOk(part)) dead.Add(Str(part["reason"]) ?? $"{p.Label} cannot be read");
        }
        o["ok"] = IsOk(o["volume"] as JsonObject) && IsOk(o["power"] as JsonObject);
        o["reason"] = dead.Count > 0 ? string.Join("; ", dead) : null;
        return o;
    }

    /// <summary>
    /// QS_LEVEL: whole percentages 0-100, the sidecar's own scale. A 0-1 fraction is refused WITH that reason (main.js:
    /// the panel once sent 0.42, the sidecar rounded it to 0, and a drag set Windows to 0 %).
    /// </summary>
    public static string? CheckLevel(string what, JsonElement v)
    {
        double n = Number(v);
        if (!double.IsNaN(n) && Math.Floor(n) == n && n >= 0 && n <= 100) return null;
        return $"{what} must be a whole number from 0 to 100" + (n > 0 && n < 1 ? $" ({Fmt(v)} looks like a 0-1 fraction)" : "");
    }

    static string? CheckBool(string what, JsonElement v) =>
        v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : $"{what} must be true or false";

    /// JS Number(v) for the values a page sends: a number, a numeric string, a bool; anything else NaN.
    public static double Number(JsonElement v) => v.ValueKind switch {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? 0
            : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
        JsonValueKind.Null => 0,
        _ => double.NaN,
    };

    /// The parts qs.set can change, by name (main.js QS_WRITE; the same names, checks and read-backs).
    public static readonly IReadOnlyDictionary<string, QsWrite> Writes = new Dictionary<string, QsWrite>(StringComparer.Ordinal)
    {
        ["volume"] = new("volume", "volume", "volume", "the volume", v => CheckLevel("volume", v),
            v => new JsonObject { ["level"] = Number(v) }, p => p["level"]?.DeepClone()),
        ["mute"] = new("mute", "volume", "volume", "mute", v => CheckBool("mute", v),
            v => new JsonObject { ["muted"] = v.ValueKind == JsonValueKind.True }, p => IsTrue(p["muted"])),
        ["wifi"] = new("wifi", "wifi", "wifi", "Wi-Fi", v => CheckBool("wifi", v),
            v => new JsonObject { ["on"] = v.ValueKind == JsonValueKind.True }, p => IsTrue(p["on"])),
        ["bluetooth"] = new("bluetooth", "bluetooth", "bluetooth", "Bluetooth", v => CheckBool("bluetooth", v),
            v => new JsonObject { ["on"] = v.ValueKind == JsonValueKind.True }, p => IsTrue(p["on"])),
        ["brightness"] = new("brightness", "brightness", "brightness", "the screen brightness", v => CheckLevel("brightness", v),
            v => new JsonObject { ["level"] = Number(v) }, p => p["level"]?.DeepClone()),
        ["nightlight"] = new("nightlight", "nightlight", "nightlight", "Night light", v => CheckBool("nightlight", v),
            v => new JsonObject { ["on"] = v.ValueKind == JsonValueKind.True }, p => IsTrue(p["on"])),
    };

    /// "qs.set does not know the part ..." lists them in this order (main.js Object.keys(QS_WRITE)).
    public static readonly string KnownWrites = "volume, mute, wifi, bluetooth, brightness, nightlight";

    /// <summary>
    /// What a dry run (a --test run) would leave the part reading: the last reading with the asked value written into
    /// it, marked <c>dryRun</c>. Null with a reason when the part cannot be switched at all (not read, not ok, no
    /// adapter): the live sidecar would refuse too, so a dry run refuses with the same kind of words.
    /// </summary>
    public static JsonObject? DryRun(QsWrite w, JsonObject? before, JsonElement value, out string? refused)
    {
        refused = null;
        if (before == null || IsTrue(before["pending"])) { refused = $"{Cap(w.Label)} is still being read, so it cannot be switched yet"; return null; }
        if (!IsOk(before)) { refused = $"{Cap(w.Label)} could not be changed: {Str(before["reason"]) ?? "it cannot be read"}"; return null; }
        if (IsFalse(before["present"])) { refused = $"{Cap(w.Label)} could not be changed: this PC has none"; return null; }
        var after = (JsonObject)before.DeepClone();
        foreach (var kv in w.Args(value)) after[kv.Key] = kv.Value?.DeepClone();
        if (w.Name == "wifi" && value.ValueKind == JsonValueKind.False) after["connected"] = false;
        after["dryRun"] = true;
        return after;
    }

    // ---- small readers, shared with the service and the tests

    public static bool IsOk(JsonObject? p) => p != null && !IsFalse(p["ok"]);
    public static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static bool IsFalse(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && !b;
    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    public static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// A value as JS would print it in the set log (strings quoted, numbers as they are).
    public static string Fmt(JsonElement v) => v.ValueKind switch {
        JsonValueKind.String => "\"" + v.GetString() + "\"",
        JsonValueKind.Undefined => "undefined",
        _ => v.GetRawText(),
    };

    public static string Fmt(JsonNode? n) => n == null ? "null" : n is JsonValue v && v.TryGetValue<string>(out var s) ? "\"" + s + "\"" : n.ToJsonString();
}
