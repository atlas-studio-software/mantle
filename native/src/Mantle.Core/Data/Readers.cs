using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Mantle.Core.Data;

/// <summary>
/// The parsers, one per file. Each takes the file's text and returns the value Electron would use,
/// with the same validation as the Electron code that reads it (named on each), tolerant of keys it
/// does not know. JSON is read the way Node's JSON.parse reads it (no comments, no trailing commas),
/// through JsonDocument, so no reflection is involved (trim / AOT safe).
/// </summary>
public static class Readers
{
    public const string ThemeFile = "theme.json";
    public const string LocalLookFile = "look-sync.json";
    public const string FeaturesFile = "features.json";
    public const string PinsFile = @"features\jumplist.json";
    public const string SharedLookFile = "look.json";

    /// lib/theme.js FILE_MAX
    public const long ThemeMaxBytes = 64 * 1024;
    /// a ceiling for the others: Electron writes a few KB; anything this big is not its file
    public const long OtherMaxBytes = 1024 * 1024;

    static readonly JsonDocumentOptions NodeJson = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };

    static JsonDocument? Parse(string text, out string? why)
    {
        try { why = null; return JsonDocument.Parse(text, NodeJson); }
        catch (JsonException e) { why = "it is not valid JSON (" + e.Message.Split('.')[0] + ")"; return null; }
    }

    internal static ReadResult<T> Result<T>(T v, ReadState s, string? why, List<string>? skipped, string name, string stamp) => new(v, s, why, skipped, name, stamp);

    // ------------------------------------------------------------------ theme.json (lib/theme.js)
    public static ReadResult<ThemeSettings> ParseTheme(string text, string name = ThemeFile, string stamp = "")
    {
        using var doc = Parse(text, out var why);
        if (doc == null) return Result(ThemeSettings.Default, ReadState.Corrupt, why, null, name, stamp);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Result(ThemeSettings.Default, ReadState.Corrupt, "it is not a JSON object", null, name, stamp);
        if (!root.TryGetProperty("mode", out var m)) return Result(ThemeSettings.Default, ReadState.Corrupt, "it has no \"mode\"", null, name, stamp);
        ThemeMode? mode = m.ValueKind == JsonValueKind.String ? ModeOf(m.GetString()) : null;
        if (mode == null) return Result(ThemeSettings.Default, ReadState.Corrupt, "\"mode\" can be \"light\", \"dark\" or \"system\"; it is " + Shown(m), null, name, stamp);
        return Result(new ThemeSettings(mode.Value), ReadState.Ok, null, null, name, stamp);
    }

    static ThemeMode? ModeOf(string? s) => s switch { "system" => ThemeMode.System, "light" => ThemeMode.Light, "dark" => ThemeMode.Dark, _ => null };

    // ------------------------------------------------------------------ look-sync.json (lib/look-host.js load)
    public static ReadResult<LocalLook> ParseLocalLook(string text, string name = LocalLookFile, string stamp = "")
    {
        using var doc = Parse(text, out var why);
        if (doc == null) return Result(LocalLook.Default, ReadState.Corrupt, why, null, name, stamp);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Result(LocalLook.Default, ReadState.Corrupt, "it is not a JSON object", null, name, stamp);
        var skipped = new List<string>();
        bool sync = LocalLook.Default.LookSync;
        if (root.TryGetProperty("lookSync", out var ls)) {
            if (ls.ValueKind is JsonValueKind.True or JsonValueKind.False) sync = ls.GetBoolean();
            else skipped.Add("lookSync must be true or false");
        }
        int? hue = LocalLook.Default.AccentHue;
        if (root.TryGetProperty("accent", out var acc)) {
            string? bad = CheckAccent(acc, out var h);
            if (bad == null) hue = h; else skipped.Add(bad);
        }
        GlassSettings glass = LocalLook.Default.Glass;
        if (root.TryGetProperty("glass", out var gl)) {
            string? bad = CheckGlass(gl, out var g);
            if (bad == null) glass = g!; else skipped.Add(bad);
        }
        return Result(new LocalLook(sync, hue, glass), ReadState.Ok, null, skipped, name, stamp);
    }

    // ------------------------------------------------------------------ look.json (lib/look-sync.js parse)
    public static ReadResult<SharedLook> ParseSharedLook(string text, string name = SharedLookFile, string stamp = "")
    {
        using var doc = Parse(text, out var why);
        if (doc == null) return Result(SharedLook.None, ReadState.Corrupt, why, null, name, stamp);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Result(SharedLook.None, ReadState.Corrupt, "top level is not an object", null, name, stamp);
        if (!root.TryGetProperty("schema", out var sc) || !Whole(sc, out long schema) || schema < 1)
            return Result(SharedLook.None, ReadState.Corrupt, "schema is missing", null, name, stamp);
        if (schema > 1)
            return Result(SharedLook.None, ReadState.Newer, $"Shared look is from a newer version (schema {schema}); update this app", null, name, stamp);
        if (!root.TryGetProperty("rev", out var rv) || !Whole(rv, out long rev) || rev < 0)
            return Result(SharedLook.None, ReadState.Corrupt, "rev is missing", null, name, stamp);

        string? wApp = null, wAt = null;
        if (root.TryGetProperty("writer", out var w) && w.ValueKind == JsonValueKind.Object) {
            if (w.TryGetProperty("app", out var a) && a.ValueKind == JsonValueKind.String) wApp = a.GetString();
            if (w.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String) wAt = at.GetString();
        }
        var skipped = new List<string>();
        ThemeMode? theme = null; bool hasAccent = false; int? hue = null; GlassSettings? glass = null;
        if (root.TryGetProperty("look", out var look) && look.ValueKind == JsonValueKind.Object) {
            if (Field(look, "theme", skipped, out var tv)) {
                var m = tv.ValueKind == JsonValueKind.String ? ModeOf(tv.GetString()) : null;
                if (m == null) skipped.Add("theme: theme must be system, light or dark"); else theme = m;
            }
            if (Field(look, "accent", skipped, out var av)) {
                string? bad = CheckAccent(av, out var h);
                if (bad == null) { hasAccent = true; hue = h; } else skipped.Add(bad);
            }
            if (Field(look, "glass", skipped, out var gv)) {
                string? bad = CheckGlass(gv, out var g);
                if (bad == null) glass = g; else skipped.Add(bad);
            }
        }
        DesktopRecord? desk = null;
        if (root.TryGetProperty("desktop", out var d) && d.ValueKind != JsonValueKind.Null) {
            string? bad = CheckDesktop(d, out desk);
            if (bad != null) skipped.Add(bad);
        }
        return Result(new SharedLook((int)schema, rev, wApp, wAt, theme, hasAccent, hue, glass, desk), ReadState.Ok, null, skipped, name, stamp);
    }

    /// look.look[field] = { v, rev, by }: true with v when the entry is usable as an entry.
    static bool Field(JsonElement look, string field, List<string> skipped, out JsonElement v)
    {
        v = default;
        if (!look.TryGetProperty(field, out var e)) return false;
        if (e.ValueKind != JsonValueKind.Object) { skipped.Add(field + ": not an object"); return false; }
        if (!e.TryGetProperty("rev", out var r) || !Whole(r, out long rev) || rev < 0) { skipped.Add(field + ": rev must be a whole number"); return false; }
        if (!e.TryGetProperty("v", out v)) { skipped.Add(field + ": it has no value"); return false; }
        return true;
    }

    // look-sync.js checkValue('accent')
    static string? CheckAccent(JsonElement v, out int? hue)
    {
        hue = null;
        if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("hue", out var h)) return "accent must be { hue }";
        if (h.ValueKind == JsonValueKind.Null) return null;
        if (!Whole(h, out long n) || n < 0 || n > 360) return "accent hue must be null or an integer 0..360";
        hue = (int)n;
        return null;
    }

    // look-sync.js checkValue('glass')
    static string? CheckGlass(JsonElement v, out GlassSettings? glass)
    {
        glass = null;
        if (v.ValueKind != JsonValueKind.Object) return "glass must be an object";
        var parts = new GlassLook[2];
        string[] themes = { "dark", "light" };
        for (int i = 0; i < 2; i++) {
            if (!v.TryGetProperty(themes[i], out var g) || g.ValueKind != JsonValueKind.Object) return "glass." + themes[i] + " missing";
            double[] vals = new double[3];
            (string k, double lo, double hi)[] ranges = { ("blurPx", 0, 64), ("dim", 0.20, 0.90), ("panel", -0.30, 0.15) };
            for (int j = 0; j < 3; j++) {
                var (k, lo, hi) = ranges[j];
                if (!g.TryGetProperty(k, out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetDouble(out double dv) || !double.IsFinite(dv) || dv < lo || dv > hi)
                    return $"glass.{themes[i]}.{k} must be {lo.ToString(System.Globalization.CultureInfo.InvariantCulture)}..{hi.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                vals[j] = dv;
            }
            parts[i] = new GlassLook(vals[0], vals[1], vals[2]);
        }
        if (!v.TryGetProperty("render", out var r) || r.ValueKind != JsonValueKind.String || (r.GetString() != "picture" && r.GetString() != "seeThrough"))
            return "glass.render must be picture or seeThrough";
        glass = new GlassSettings(parts[0], parts[1], r.GetString()!);
        return null;
    }

    // look-sync.js checkDesktop
    static string? CheckDesktop(JsonElement d, out DesktopRecord? rec)
    {
        rec = null;
        if (d.ValueKind != JsonValueKind.Object) return "desktop must be an object";
        if (!d.TryGetProperty("rev", out var r) || !Whole(r, out long rev) || rev < 0) return "desktop.rev must be a whole number";
        if (!d.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return "desktop.ok must be true or false";
        var s = new string?[6];
        string[] keys = { "id", "type", "title", "file", "still", "reason" };
        for (int i = 0; i < keys.Length; i++) {
            if (!d.TryGetProperty(keys[i], out var x) || x.ValueKind == JsonValueKind.Null) continue;
            if (x.ValueKind != JsonValueKind.String) return "desktop." + keys[i] + " must be text or null";
            s[i] = x.GetString();
        }
        rec = new DesktopRecord(rev, ok.GetBoolean(), s[0], s[1], s[2], s[3], s[4], s[5]);
        return null;
    }

    // ------------------------------------------------------------------ features.json (lib/features.js)
    public static ReadResult<FeatureSwitches> ParseFeatures(string text, string name = FeaturesFile, string stamp = "")
    {
        using var doc = Parse(text, out var why);
        if (doc == null) return Result(FeatureSwitches.Default, ReadState.Corrupt, why, null, name, stamp);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Result(FeatureSwitches.Default, ReadState.Corrupt, "it does not hold switches (not a JSON object)", null, name, stamp);
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var p in root.EnumerateObject()) {
            if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) map[p.Name] = p.Value.GetBoolean();
            else skipped.Add(Clip(p.Name) + ": a switch is true or false");
        }
        return Result(new FeatureSwitches(map), ReadState.Ok, null, skipped, name, stamp);
    }

    // ------------------------------------------------------------------ features\jumplist.json (lib/features/_jumplist-pins.js)
    public static ReadResult<PinOverlay> ParsePins(string text, string name = PinsFile, string stamp = "")
    {
        using var doc = Parse(text, out var why);
        if (doc == null) return Result(PinOverlay.Empty, ReadState.Corrupt, why, null, name, stamp);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Result(PinOverlay.Empty, ReadState.Corrupt, "it is not a JSON object", null, name, stamp);
        // the feature's settings file holds other keys too; only "pins" is the overlay (norm(): anything else = empty)
        if (!root.TryGetProperty("pins", out var pins) || pins.ValueKind != JsonValueKind.Object)
            return Result(PinOverlay.Empty, ReadState.Ok, null, null, name, stamp);
        var skipped = new List<string>();
        var hide = Entries(pins, "hide", skipped);
        var add = Entries(pins, "add", skipped);
        var order = new List<string>();
        if (pins.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array) {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in o.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } id && seen.Add(id)) order.Add(id);
        }
        return Result(new PinOverlay(hide, add, order), ReadState.Ok, null, skipped, name, stamp);
    }

    static List<PinEntry> Entries(JsonElement pins, string key, List<string> skipped)
    {
        var list = new List<PinEntry>();
        if (!pins.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        int i = 0;
        foreach (var e in arr.EnumerateArray()) {
            i++;
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } idText) {
                skipped.Add($"pins.{key} #{i}: an entry needs an id");
                continue;
            }
            list.Add(new PinEntry(idText, Str(e, "name"), Str(e, "exe"), Str(e, "aumid"), Str(e, "shortcut")));
        }
        return list;
    }

    // ------------------------------------------------------------------ helpers
    static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// JavaScript's Number.isInteger: 5 and 5.0 are whole numbers.
    static bool Whole(JsonElement e, out long n)
    {
        n = 0;
        if (e.ValueKind != JsonValueKind.Number) return false;
        if (e.TryGetInt64(out n)) return true;
        if (e.TryGetDouble(out double d) && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < 9e15) { n = (long)d; return true; }
        return false;
    }

    static string Shown(JsonElement e) => e.ValueKind switch {
        JsonValueKind.String => "\"" + Clip(e.GetString() ?? "") + "\"",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "a list",
        JsonValueKind.Object => "an object",
        _ => "a " + e.ValueKind.ToString().ToLowerInvariant(),
    };

    static string Clip(string s) => s.Length > 40 ? s[..40] : s;
}
