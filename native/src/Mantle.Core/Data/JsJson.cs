using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mantle.Core.Data;

/// <summary>
/// JSON exactly as Electron Mantle reads and writes it: <c>JSON.parse</c> and
/// <c>JSON.stringify(value, null, indent)</c>. A file native writes is byte-for-byte the file Electron would
/// have written for the same value, so a rollback to Electron reads it and a diff shows only real changes.
///
/// What that means in detail (each one is a difference from System.Text.Json's defaults):
///   - numbers are doubles, printed the way JavaScript prints them (<c>1</c> not <c>1.0</c>, <c>1e+21</c>,
///     <c>1e-7</c>, <c>-0</c> as <c>0</c>);
///   - strings escape only <c>"</c>, <c>\</c> and control characters (<c>\n</c> style or <c>\u001b</c>,
///     lower-case hex) and lone surrogates; every other character, emoji included, is written as itself;
///   - object keys keep their order, except that array-index keys (<c>"0"</c>, <c>"42"</c>) come first in
///     ascending order, as in every JavaScript object;
///   - a key that appears twice keeps its first position and its last value (JSON.parse);
///   - a leading byte-order mark is dropped; invalid UTF-8 becomes U+FFFD (Node's utf8 decoding).
/// </summary>
public static class JsJson
{
    static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 1024, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    /// Decode a file's bytes the way <c>fs.readFileSync(file, 'utf8')</c> does, minus a BOM.
    public static string DecodeUtf8(byte[] bytes)
    {
        string s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == '﻿' ? s[1..] : s;
    }

    /// <summary>JSON.parse. Throws <see cref="JsonException"/> when the text is not JSON.</summary>
    public static JsonNode? Parse(string text)
    {
        using var doc = JsonDocument.Parse(text, ParseOptions);
        return FromElement(doc.RootElement);
    }

    /// A JsonElement as a mutable node with JavaScript's value semantics (numbers become doubles).
    public static JsonNode? FromElement(JsonElement e)
    {
        switch (e.ValueKind) {
            case JsonValueKind.Object: {
                var o = new JsonObject();
                foreach (var p in e.EnumerateObject()) o[p.Name] = FromElement(p.Value);   // last value wins, first position kept
                return o;
            }
            case JsonValueKind.Array: {
                var a = new JsonArray();
                foreach (var v in e.EnumerateArray()) a.Add(FromElement(v));
                return a;
            }
            case JsonValueKind.String: return JsonValue.Create(e.GetString()!);
            case JsonValueKind.Number: return JsonValue.Create(double.Parse(e.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture));
            case JsonValueKind.True: return JsonValue.Create(true);
            case JsonValueKind.False: return JsonValue.Create(false);
            default: return null;
        }
    }

    /// A deep copy with JavaScript's value semantics (<c>JSON.parse(JSON.stringify(v))</c>).
    public static JsonNode? Clone(JsonNode? n) => n == null ? null : Parse(Stringify(n, 0));

    /// <summary>JSON.stringify(value, null, indent). A non-finite number is written as null, as in JavaScript.</summary>
    public static string Stringify(JsonNode? node, int indent)
    {
        if (indent < 0 || indent > 10) throw new ArgumentOutOfRangeException(nameof(indent), "JSON.stringify indents 0 to 10 spaces");
        var sb = new StringBuilder();
        Write(sb, node, indent, 0);
        return sb.ToString();
    }

    /// The bytes Electron writes: UTF-8 without a BOM, with or without the trailing newline some writers add.
    public static byte[] ToFileBytes(JsonNode? node, int indent, bool trailingNewline)
        => new UTF8Encoding(false).GetBytes(Stringify(node, indent) + (trailingNewline ? "\n" : ""));

    static void Write(StringBuilder sb, JsonNode? n, int indent, int level)
    {
        switch (n) {
            case null: sb.Append("null"); return;
            case JsonObject o: WriteObject(sb, o, indent, level); return;
            case JsonArray a: WriteArray(sb, a, indent, level); return;
            case JsonValue v: WriteValue(sb, v); return;
        }
    }

    static void NewLine(StringBuilder sb, int indent, int level)
    {
        if (indent == 0) return;
        sb.Append('\n').Append(' ', indent * level);
    }

    static void WriteObject(StringBuilder sb, JsonObject o, int indent, int level)
    {
        if (o.Count == 0) { sb.Append("{}"); return; }
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in OrderedLikeJs(o)) {
            if (!first) sb.Append(',');
            first = false;
            NewLine(sb, indent, level + 1);
            WriteString(sb, k);
            sb.Append(indent == 0 ? ":" : ": ");
            Write(sb, v, indent, level + 1);
        }
        NewLine(sb, indent, level);
        sb.Append('}');
    }

    static void WriteArray(StringBuilder sb, JsonArray a, int indent, int level)
    {
        if (a.Count == 0) { sb.Append("[]"); return; }
        sb.Append('[');
        for (int i = 0; i < a.Count; i++) {
            if (i > 0) sb.Append(',');
            NewLine(sb, indent, level + 1);
            Write(sb, a[i], indent, level + 1);
        }
        NewLine(sb, indent, level);
        sb.Append(']');
    }

    /// JavaScript's own key order: array indexes (canonical integers below 2^32 - 1) ascending, then the rest as inserted.
    public static IEnumerable<KeyValuePair<string, JsonNode?>> OrderedLikeJs(JsonObject o)
    {
        List<(uint Index, KeyValuePair<string, JsonNode?> Pair)>? idx = null;
        var rest = new List<KeyValuePair<string, JsonNode?>>(o.Count);
        foreach (var p in o) {
            if (IsArrayIndex(p.Key, out uint i)) (idx ??= new()).Add((i, p));
            else rest.Add(p);
        }
        if (idx != null) {
            idx.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var x in idx) yield return x.Pair;
        }
        foreach (var p in rest) yield return p;
    }

    static bool IsArrayIndex(string k, out uint value)
    {
        value = 0;
        if (k.Length == 0 || k.Length > 10) return false;
        if (k.Length > 1 && k[0] == '0') return false;
        ulong acc = 0;
        foreach (char c in k) {
            if (c < '0' || c > '9') return false;
            acc = acc * 10 + (ulong)(c - '0');
        }
        if (acc >= 4294967295UL) return false;
        value = (uint)acc;
        return true;
    }

    static void WriteValue(StringBuilder sb, JsonValue v)
    {
        if (v.TryGetValue(out JsonElement el)) { Write(sb, FromElement(el), 0, 0); return; }
        if (v.TryGetValue(out string? s)) { WriteString(sb, s!); return; }
        if (v.TryGetValue(out bool b)) { sb.Append(b ? "true" : "false"); return; }
        if (v.TryGetValue(out double d)) { sb.Append(Number(d)); return; }
        if (v.TryGetValue(out float f)) { sb.Append(Number(f)); return; }
        if (v.TryGetValue(out long l)) { sb.Append(Number(l)); return; }
        if (v.TryGetValue(out int i)) { sb.Append(Number(i)); return; }
        if (v.TryGetValue(out uint ui)) { sb.Append(Number(ui)); return; }
        if (v.TryGetValue(out short sh)) { sb.Append(Number(sh)); return; }
        if (v.TryGetValue(out byte by)) { sb.Append(Number(by)); return; }
        if (v.TryGetValue(out ulong ul)) { sb.Append(Number(ul)); return; }
        if (v.TryGetValue(out decimal m)) { sb.Append(Number((double)m)); return; }
        if (v.TryGetValue(out char ch)) { WriteString(sb, ch.ToString()); return; }
        throw new NotSupportedException("JsJson writes JSON values only (string, number, bool, null); it was given " + v.GetType().Name);
    }

    /// <summary>Number.prototype.toString for a double (ECMA-262 Number::toString, radix 10).</summary>
    public static string Number(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        if (d == 0) return "0";
        string r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);   // shortest digits that round-trip
        // split r into its digits and the exponent n, so that |d| = 0.d1d2..dk x 10^n
        int e = r.IndexOfAny(new[] { 'E', 'e' });
        int exp10 = 0;
        string mant = r;
        if (e >= 0) { exp10 = int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); mant = r[..e]; }
        int dot = mant.IndexOf('.');
        string intPart = dot >= 0 ? mant[..dot] : mant;
        string frac = dot >= 0 ? mant[(dot + 1)..] : "";
        string digits = (intPart + frac).TrimStart('0');
        int lead = (intPart + frac).Length - digits.Length;   // leading zeros dropped
        int n = intPart.Length + exp10 - lead;
        digits = digits.TrimEnd('0');
        int k = digits.Length;
        var sb = new StringBuilder();
        if (d < 0) sb.Append('-');
        if (k <= n && n <= 21) sb.Append(digits).Append('0', n - k);
        else if (0 < n && n <= 21) sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        else if (-6 < n && n <= 0) sb.Append("0.").Append('0', -n).Append(digits);
        else {
            int x = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(x >= 0 ? '+' : '-').Append(Math.Abs(x).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// JSON.stringify of a string (ES2019 well-formed: lone surrogates are escaped, pairs are kept).
    public static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++) {
            char c = s[i];
            switch (c) {
                case '"': sb.Append("\\\""); continue;
                case '\\': sb.Append("\\\\"); continue;
                case '\b': sb.Append("\\b"); continue;
                case '\f': sb.Append("\\f"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
                case '\t': sb.Append("\\t"); continue;
            }
            if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); continue; }
            if (char.IsHighSurrogate(c)) {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; continue; }
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); continue;
            }
            if (char.IsLowSurrogate(c)) { sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); continue; }
            sb.Append(c);
        }
        sb.Append('"');
    }
}
