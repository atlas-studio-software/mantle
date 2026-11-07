using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Look;

/// One field look.json refused, and why (look-sync.js parse's <c>skipped</c>).
public sealed record LookSkip(string Field, string? By, string Reason, string? Text = null);

/// <summary>look-sync.js <c>parse</c>: <c>State</c> ok | corrupt | newer, the validated doc (ok only), what was skipped.</summary>
public sealed record LookParse(string State, JsonObject? Doc, IReadOnlyList<LookSkip> Skipped, string? Reason, double? Schema = null);

/// <summary>look-sync.js <c>read</c>: a parse plus the bytes' SHA-1 and text, or missing | error.</summary>
public sealed record LookRead(string State, JsonObject? Doc, IReadOnlyList<LookSkip> Skipped, string? Reason, string? Hash, string? Text, string? Code = null);

/// <summary>
/// The pure half of look-sync.js (vendored from the look-sync project, CONTRACT.md sections 2, 3 and 5):
/// the schema, the seed, the ranges, validation, migration and parsing. Values are <see cref="JsonNode"/>s with
/// JavaScript's semantics (numbers are doubles; <see cref="JsJson"/> writes them), so what Mantle publishes is
/// byte-for-byte what the JavaScript module would write for the same state. Helm and Quartz read this file.
/// </summary>
public static class LookSyncCore
{
    public const int Schema = 1;
    public const string FileName = "look.json";
    public static readonly string[] Fields = { "theme", "accent", "glass" };
    static readonly Dictionary<string, string> AppNames = new() { ["helm"] = "Helm", ["quartz"] = "Quartz", ["mantle"] = "Mantle" };

    /// The ranges of the glass numbers (RANGES), in the module's key order.
    public static readonly (string Key, double Lo, double Hi)[] Ranges = { ("blurPx", 0, 64), ("dim", 0.20, 0.90), ("panel", -0.30, 0.15) };

    /// <summary>The first seed (SEED): Match Windows, Mantle's own accent, the matched glass. A fresh copy each call.</summary>
    public static JsonObject Seed() => (JsonObject)JsJson.Parse(
        "{\"theme\":\"system\",\"accent\":{\"hue\":null},\"glass\":{\"dark\":{\"blurPx\":5,\"dim\":0.5,\"panel\":0},\"light\":{\"blurPx\":12,\"dim\":0.45,\"panel\":0},\"render\":\"picture\"}}")!;

    /// RANGES as the view answers it: <c>{blurPx: [0, 64], dim: [.2, .9], panel: [-.3, .15]}</c>.
    public static JsonObject RangesJson()
    {
        var o = new JsonObject();
        foreach (var (k, lo, hi) in Ranges) o[k] = new JsonArray(JsonValue.Create(lo), JsonValue.Create(hi));
        return o;
    }

    /// The default folder: <c>LOOK_SYNC_DIR</c>, else <c>%APPDATA%\look-sync</c> (DataPaths decides for a run).
    public static string DefaultDir(Func<string, string?> env)
    {
        if (env("LOOK_SYNC_DIR") is { Length: > 0 } d) return d;
        string bas = env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        return Path.Combine(bas, "look-sync");
    }

    public static string AppName(string? a) => a != null && AppNames.TryGetValue(a, out var n) ? n : string.IsNullOrEmpty(a) ? "another app" : a;

    // ---- JavaScript value helpers ---------------------------------------------------------------

    internal static bool IsObj(JsonNode? v) => v is JsonObject;

    internal static bool TryNum(JsonNode? v, out double d)
    {
        d = 0;
        if (v is not JsonValue jv) return false;
        if (jv.TryGetValue(out double x)) { d = x; return true; }
        if (jv.TryGetValue(out int i)) { d = i; return true; }
        if (jv.TryGetValue(out long l)) { d = l; return true; }
        if (jv.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number) { d = e.GetDouble(); return true; }
        return false;
    }

    /// typeof v === 'number' (NaN and infinities included).
    internal static bool IsNumber(JsonNode? v) => TryNum(v, out _);

    internal static bool IsFinite(JsonNode? v, out double d) => TryNum(v, out d) && double.IsFinite(d);

    /// Number.isInteger.
    internal static bool IsInt(JsonNode? v, out double d) => IsFinite(v, out d) && Math.Floor(d) == d;

    internal static string? Str(JsonNode? v) => v is JsonValue jv && jv.TryGetValue(out string? s) ? s : null;

    internal static bool IsBool(JsonNode? v, out bool b)
    {
        b = false;
        return v is JsonValue jv && jv.TryGetValue(out b);
    }

    internal static JsonValue N(double d) => JsonValue.Create(d);

    /// JSON.stringify of a value (the module's sameValue compares these).
    internal static string J(JsonNode? v) => v == null ? "null" : JsJson.Stringify(v, 0);

    internal static JsonNode? Clone(JsonNode? v) => JsJson.Clone(v);

    static string NumText(double d) => JsJson.Number(d);

    // ---- validation -----------------------------------------------------------------------------

    /// <summary>checkValue: null when <paramref name="v"/> is a valid value of <paramref name="field"/>, else a short reason.</summary>
    public static string? CheckValue(string field, JsonNode? v)
    {
        if (field == "theme") return Str(v) is "system" or "light" or "dark" ? null : "theme must be system, light or dark";
        if (field == "accent") {
            if (v is not JsonObject o || !o.ContainsKey("hue")) return "accent must be { hue }";
            var h = o["hue"];
            if (h == null) return null;
            return IsInt(h, out var hue) && hue >= 0 && hue <= 360 ? null : "accent hue must be null or an integer 0..360";
        }
        if (field == "glass") {
            if (v is not JsonObject g) return "glass must be an object";
            foreach (var t in new[] { "dark", "light" }) {
                if (g[t] is not JsonObject gt) return "glass." + t + " missing";
                foreach (var (k, lo, hi) in Ranges) {
                    if (!(IsFinite(gt[k], out var n) && n >= lo && n <= hi)) return "glass." + t + "." + k + " must be " + NumText(lo) + ".." + NumText(hi);
                }
            }
            return Str(g["render"]) is "picture" or "seeThrough" ? null : "glass.render must be picture or seeThrough";
        }
        return "unknown field " + field;
    }

    /// <summary>checkDesktop: the desktop record's shape (only Mantle writes it).</summary>
    public static string? CheckDesktop(JsonNode? d)
    {
        if (d is not JsonObject o) return "desktop must be an object";
        if (!(IsInt(o["rev"], out var rev) && rev >= 0)) return "desktop.rev must be a whole number";
        if (!IsBool(o["ok"], out _)) return "desktop.ok must be true or false";
        foreach (var k in new[] { "id", "type", "title", "file", "still", "reason" }) {
            if (o.TryGetPropertyValue(k, out var x) && x != null && Str(x) == null) return "desktop." + k + " must be text or null";
        }
        if (o.TryGetPropertyValue("stillSize", out var ss) && ss != null) {
            bool good = ss is JsonArray a && a.Count == 2;
            if (good) foreach (var n in (JsonArray)ss) if (!(IsInt(n, out var x) && x > 0)) good = false;
            if (!good) return "desktop.stillSize must be [width, height]";
        }
        return null;
    }

    /// <summary>migrate: a v1 doc that still carries the retired <c>wallpaperRequest</c> loses it (a copy).</summary>
    public static JsonObject Migrate(JsonObject doc)
    {
        if (IsInt(doc["schema"], out var s) && s == Schema && doc.ContainsKey("wallpaperRequest")) {
            var c = (JsonObject)Clone(doc)!;
            c.Remove("wallpaperRequest");
            return c;
        }
        return doc;
    }

    static LookParse Corrupt(string why) => new("corrupt", null, Array.Empty<LookSkip>(), why);

    /// <summary>parse: the text of look.json validated. Never throws.</summary>
    /// <remarks>The doc is rebuilt as the module rebuilds it: <c>{schema, rev, writer, look{theme?, accent?, glass?}, desktop?}</c>,
    /// unknown fields dropped, each look entry <c>{v, rev, by}</c>. A field that fails is skipped and named; the rest stays.</remarks>
    public static LookParse Parse(string text)
    {
        JsonNode? raw;
        try { raw = JsJson.Parse(text); }
        catch (Exception e) { return Corrupt("not valid JSON (" + ParseWords(e) + ")"); }
        if (raw is not JsonObject r) return Corrupt("top level is not an object");
        if (!(IsInt(r["schema"], out var schema) && schema >= 1)) return Corrupt("schema is missing");
        if (schema > Schema)
            return new LookParse("newer", null, Array.Empty<LookSkip>(), "Shared look is from a newer version (schema " + NumText(schema) + "); update this app", schema);
        if (!(IsInt(r["rev"], out var rev) && rev >= 0)) return Corrupt("rev is missing");
        r = Migrate(r);
        var doc = new JsonObject {
            ["schema"] = N(Schema),
            ["rev"] = N(rev),
            ["writer"] = r["writer"] is JsonObject w ? Clone(w) : new JsonObject(),
            ["look"] = new JsonObject(),
        };
        var skipped = new List<LookSkip>();
        var look = r["look"] as JsonObject;
        var outLook = (JsonObject)doc["look"]!;
        foreach (var f in Fields) {
            if (look == null || !look.TryGetPropertyValue(f, out var e)) continue;
            string? why = e is not JsonObject eo ? "not an object"
                : !(IsInt(eo["rev"], out var er) && er >= 0) ? "rev must be a whole number"
                : CheckValue(f, eo["v"]);
            if (why != null) { skipped.Add(new LookSkip(f, e is JsonObject x ? Str(x["by"]) : null, why)); continue; }
            var ent = (JsonObject)e!;
            IsInt(ent["rev"], out var frev);
            outLook[f] = new JsonObject { ["v"] = Clone(ent["v"]), ["rev"] = N(frev), ["by"] = Str(ent["by"]) is string by ? JsonValue.Create(by) : null };
        }
        if (r.TryGetPropertyValue("desktop", out var desk) && desk != null) {
            string? why = CheckDesktop(desk);
            if (why != null) skipped.Add(new LookSkip("desktop", desk is JsonObject dk ? Str(dk["by"]) : null, why));
            else doc["desktop"] = Clone(desk);
        }
        return new LookParse("ok", doc, skipped, null);
    }

    /// The JSON error in short words. JavaScript's own message (V8's) cannot be reproduced exactly; the
    /// parenthesis is the only text that differs from Electron's, and only the log and a damaged-file status show it.
    static string ParseWords(Exception e)
    {
        string m = e.Message;
        int cut = m.IndexOf(" LineNumber", StringComparison.Ordinal);
        if (cut > 0) m = m[..cut].TrimEnd('.', ' ');
        return m.Length > 120 ? m[..120] : m;
    }

    public static string Sha1(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));

    /// <summary>read: <c>&lt;dir&gt;\look.json</c> read and parsed. Never throws, never writes.</summary>
    public static LookRead Read(string dir)
    {
        string file = Path.Combine(dir, FileName);
        byte[] buf;
        // Node's readFileSync opens with FILE_SHARE_READ | WRITE | DELETE (libuv). File.ReadAllBytes shares READ only, so a
        // native read held look.json against every other app's replace-rename (their EPERM) and was itself refused while
        // one was in flight (EBUSY: a lost publish, since Mutate re-reads first). Open it the way Node does.
        try {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            buf = new byte[fs.Length];
            int got = 0;
            while (got < buf.Length) { int n = fs.Read(buf, got, buf.Length - got); if (n == 0) break; got += n; }
            if (got < buf.Length) Array.Resize(ref buf, got);
        }
        catch (Exception e) {
            string code = SafeFile.Code(e);
            if (code == "ENOENT") return new LookRead("missing", null, Array.Empty<LookSkip>(), "no shared look file yet", null, null);
            return new LookRead("error", null, Array.Empty<LookSkip>(), "Can't read " + file + ": " + code, null, null, code);
        }
        string text = JsJson.DecodeUtf8(buf);
        var p = Parse(text);
        return new LookRead(p.State, p.Doc, p.Skipped, p.Reason, Sha1(buf), text);
    }

    /// validLocal: the app's own values, each kept only when valid.
    internal static JsonObject ValidLocal(JsonObject? local)
    {
        var o = new JsonObject();
        if (local == null) return o;
        foreach (var f in Fields)
            if (local.TryGetPropertyValue(f, out var v) && CheckValue(f, v) == null) o[f] = Clone(v);
        return o;
    }

    /// Object.assign(target, src): existing keys keep their place, new ones go last. Values are copied.
    internal static JsonObject Assign(JsonObject target, JsonObject? src)
    {
        if (src == null) return target;
        foreach (var (k, v) in JsJson.OrderedLikeJs(src)) target[k] = Clone(v);
        return target;
    }

    /// describeChange: "theme changed to Dark", "accent hue 200", "glass changed".
    public static string DescribeChange(string field, JsonNode? v)
    {
        if (field == "theme") return "theme changed to " + (Str(v) switch { "system" => "Match Windows", "light" => "Light", "dark" => "Dark", _ => "undefined" });
        if (field == "accent") {
            var h = (v as JsonObject)?["hue"];
            return h == null ? "accent back to default" : "accent hue " + J(h);
        }
        return "glass changed";
    }

    /// Date.prototype.toISOString.
    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
