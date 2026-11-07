using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.Toasts;

// The record of which apps' Windows banners Mantle turned off, and what each one was before
// (<userData>\notif-banners.json, lib/features/_toasts-owner.js). Byte-shape compatible with Electron's,
// because the sidecar's exit, the crash supervisor and tools/restore-banners.ps1 read the same file:
//   { "v": 1, "owned": { "<app id>": 1 | null }, "taken": { "<app id, keyed exactly as in owned>": <stamp> },
//     "released": ["<app id>", ...], "pid": <main's pid>, "at": <epoch ms> }   (pid/at added by the file store)

/// <summary>A string-keyed map that keeps insertion order the way a JS object does (delete then add = at the end).</summary>
public sealed class OrderedMap<T>
{
    readonly List<string> _keys = new();
    readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Keys => _keys;
    public int Count => _keys.Count;
    public bool ContainsKey(string k) => _values.ContainsKey(k);
    public bool TryGetValue(string k, out T v) => _values.TryGetValue(k, out v!);
    public T this[string k]
    {
        get => _values[k];
        set { if (!_values.ContainsKey(k)) _keys.Add(k); _values[k] = value; }
    }
    public bool Remove(string k) { if (!_values.Remove(k)) return false; _keys.Remove(k); return true; }

    /// The key as it is stored, matched without case (Windows matches app ids without case), or null.
    public string? KeyOf(string aumid)
    {
        foreach (var k in _keys) if (string.Equals(k, aumid, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }
}

public sealed class BannerRecord
{
    /// The ShowBanner value before Mantle: 1, or null (no value; restored by deleting the value).
    public OrderedMap<int?> Owned { get; } = new();
    /// One stamp per owned app, keyed exactly as in Owned (finite numbers only).
    public OrderedMap<double> Taken { get; } = new();
    public List<string> Released { get; } = new();

    public static BannerRecord Empty() => new();

    /// Electron's load(): owned keeps only 1/null values; taken keeps finite numbers for apps still owned
    /// (keyed exactly); released keeps strings. Anything else in the file is dropped, as Electron does.
    public static BannerRecord FromJson(JsonNode? root)
    {
        var r = new BannerRecord();
        if (root is not JsonObject o) return r;
        if (o["owned"] is JsonObject owned)
            foreach (var (k, v) in owned)
            {
                if (v is null) r.Owned[k] = null;
                else if (v is JsonValue jv && jv.TryGetValue(out double d) && d == 1) r.Owned[k] = 1;
            }
        if (o["taken"] is JsonObject taken)
            foreach (var (k, v) in taken)
                if (r.Owned.ContainsKey(k) && v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number
                    && jv.TryGetValue(out double d) && double.IsFinite(d)) r.Taken[k] = d;
        if (o["released"] is JsonArray rel)
            foreach (var x in rel)
                if (x is JsonValue jv && jv.GetValueKind() == JsonValueKind.String) r.Released.Add(jv.GetValue<string>());
        return r;
    }

    /// The record as JSON, in Electron's key order (v, owned, taken, released), plus extra trailing fields (pid, at).
    public JsonObject ToJson(params (string Key, JsonNode? Value)[] extra)
    {
        var owned = new JsonObject();
        foreach (var k in Owned.Keys) owned[k] = Owned[k] is int i ? JsonValue.Create(i) : null;
        var taken = new JsonObject();
        foreach (var k in Taken.Keys) taken[k] = Number(Taken[k]);
        var o = new JsonObject
        {
            ["v"] = 1,
            ["owned"] = owned,
            ["taken"] = taken,
            ["released"] = new JsonArray(Released.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
        };
        foreach (var (k, v) in extra) o[k] = v;
        return o;
    }

    /// An integral stamp is written as an integer (JSON.stringify(1730000000000) has no ".0").
    public static JsonNode Number(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 9e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);

    public BannerRecord Copy() => FromJson(JsonNode.Parse(ToJson().ToJsonString()));
}

/// <summary>A stamp as a put-back marker names it: none (null or missing), a number, or something else (never matches).</summary>
public readonly record struct Stamp(StampKind Kind, double Value)
{
    public static readonly Stamp None = new(StampKind.None, 0);
    public static readonly Stamp Other = new(StampKind.Other, 0);
    public static Stamp Of(double v) => new(StampKind.Number, v);
    public static Stamp Of(double? v) => v is double d ? Of(d) : None;

    /// JS `===` between a marker's took and the record's stamp (missing = null on both sides).
    public bool Same(Stamp o) => Kind == o.Kind && Kind != StampKind.Other && (Kind == StampKind.None || Value == o.Value);

    public static Stamp FromJson(JsonNode? n)
    {
        if (n is null) return None;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out double d)) return Of(d);
        return Other;
    }
}

public enum StampKind { None, Number, Other }

/// <summary>One app a put-back writer (sidecar, supervisor, script) listed in its marker file.</summary>
public sealed record BackEntry(string Aumid, Stamp Took, double? At = null, string From = "");

/// <summary>Every marker entry, plus the marker files that were there but could not be read (for the log).</summary>
public sealed record BackFiles(IReadOnlyList<BackEntry> Entries, IReadOnlyList<string> Unreadable)
{
    public static readonly BackFiles None = new(Array.Empty<BackEntry>(), Array.Empty<string>());

    /// The three marker files, one per writer, next to the record (main never writes them).
    public static readonly string[] FileNames =
        { "notif-banners-back.sidecar.json", "notif-banners-back.supervisor.json", "notif-banners-back.script.json" };

    /// readBackFiles for one file's text: a v1 { back: { id: { at, took } } } list, else why not.
    public static (List<BackEntry> Entries, string? Bad) Parse(string name, string text)
    {
        var list = new List<BackEntry>();
        try
        {
            if (text.Length > 0 && text[0] == '﻿') text = text[1..];
            var j = JsonNode.Parse(text);
            if (j is not JsonObject o || o["v"] is not JsonValue v || v.GetValueKind() != JsonValueKind.Number
                || !v.TryGetValue(out double ver) || ver != 1 || o["back"] is not JsonObject back)
                return (list, $"{name}: not a v1 put-back list");
            foreach (var (k, e) in back)
            {
                if (string.IsNullOrEmpty(k) || e is not JsonObject eo) continue;
                double? at = eo["at"] is JsonValue a && a.GetValueKind() == JsonValueKind.Number && a.TryGetValue(out double ad) ? ad : null;
                list.Add(new BackEntry(k, eo.ContainsKey("took") ? Stamp.FromJson(eo["took"]) : Stamp.None, at, name));
            }
            return (list, null);
        }
        catch (Exception ex) { return (new List<BackEntry>(), $"{name}: {Words.First(ex.Message)}"); }
    }
}

/// <summary>Plain-words helpers shared by the toasts code.</summary>
public static class Words
{
    /// plainFirst: the first line, at most 300 characters.
    public static string First(string? s)
    {
        var t = (s ?? "").Split('\n')[0];
        return t.Length > 300 ? t[..300] : t;
    }

    public static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
