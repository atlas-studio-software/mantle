// Shape.cs - the JSON "shape" of a sidecar answer (every path and the type found there), and a diff
// of two shapes. Values are not compared here (a pid or an hwnd differs between any two runs).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Mantle.Sidecar.Tests {
  internal static class Shape {
    public static SortedDictionary<string, string> Of(JsonElement e) {
      var into = new SortedDictionary<string, string>(StringComparer.Ordinal);
      Walk(e, "$", into);
      return into;
    }

    public static SortedDictionary<string, string> Of(string json) {
      using var doc = JsonDocument.Parse(json);
      return Of(doc.RootElement);
    }

    static void Put(SortedDictionary<string, string> into, string path, string type) {
      if (!into.TryGetValue(path, out var had) || had == type) { into[path] = type; return; }
      if (had == "null") { into[path] = type; return; }
      if (type == "null") return;
      if (!had.StartsWith("mixed(", StringComparison.Ordinal)) into[path] = "mixed(" + had + "|" + type + ")";
    }

    static void Walk(JsonElement e, string path, SortedDictionary<string, string> into) {
      switch (e.ValueKind) {
        case JsonValueKind.Object:
          Put(into, path, "object");
          foreach (var p in e.EnumerateObject()) Walk(p.Value, path + "." + p.Name, into);
          break;
        case JsonValueKind.Array:
          Put(into, path, "array");
          foreach (var el in e.EnumerateArray()) Walk(el, path + "[]", into);
          break;
        case JsonValueKind.String: Put(into, path, "string"); break;
        case JsonValueKind.Number: Put(into, path, "number"); break;
        case JsonValueKind.True:
        case JsonValueKind.False: Put(into, path, "bool"); break;
        default: Put(into, path, "null"); break;
      }
    }

    /// Plain-words differences between two shapes ("" when they match). A path that is null on one
    /// side and typed on the other is a VALUE difference, not a shape one, and is not listed.
    /// `ignorePrefixes` skips subtrees whose keys are data, not schema (e.g. a per-lane map).
    public static List<string> Diff(SortedDictionary<string, string> a, SortedDictionary<string, string> b,
                                    string aName, string bName, params string[] ignorePrefixes) {
      var d = new List<string>();
      bool Ignored(string p) => ignorePrefixes.Any(x => p.StartsWith(x, StringComparison.Ordinal));
      foreach (var kv in a) {
        if (Ignored(kv.Key)) continue;
        if (!b.TryGetValue(kv.Key, out var tb)) {
          // An array that was EMPTY on the other side has no element paths: not a schema difference.
          if (ParentIsEmptyArray(kv.Key, b)) continue;
          d.Add("only in " + aName + ": " + kv.Key + " (" + kv.Value + ")");
        } else if (tb != kv.Value && tb != "null" && kv.Value != "null") d.Add("type differs at " + kv.Key + ": " + aName + " " + kv.Value + ", " + bName + " " + tb);
      }
      foreach (var kv in b) {
        if (Ignored(kv.Key)) continue;
        if (!a.ContainsKey(kv.Key) && !ParentIsEmptyArray(kv.Key, a)) d.Add("only in " + bName + ": " + kv.Key + " (" + kv.Value + ")");
      }
      return d;
    }

    static bool ParentIsEmptyArray(string path, SortedDictionary<string, string> other) {
      // "$.list[].x" is absent from `other` because $.list had no elements (or was null) there.
      int at = path.LastIndexOf("[]", StringComparison.Ordinal);
      while (at > 0) {
        string arr = path.Substring(0, at);
        if (other.TryGetValue(arr, out var t) && (t == "array" || t == "null") && !other.ContainsKey(arr + "[]")) return true;
        at = arr.LastIndexOf("[]", StringComparison.Ordinal);
      }
      // A whole object that was null on the other side.
      int dot = path.LastIndexOf('.');
      while (dot > 0) {
        string parent = path.Substring(0, dot);
        if (other.TryGetValue(parent, out var t) && t == "null") return true;
        dot = parent.LastIndexOf('.');
      }
      return false;
    }

    public static string OutDir() {
      string d = Environment.GetEnvironmentVariable("MANTLE_SIDECAR_OUT");
      if (string.IsNullOrEmpty(d)) d = Path.Combine(Path.GetTempPath(), "mantle-sidecar-tests");
      Directory.CreateDirectory(d);
      return d;
    }

    static readonly object _gate = new object();
    public static void Note(string file, string line) {
      lock (_gate) File.AppendAllText(Path.Combine(OutDir(), file), line + Environment.NewLine);
    }
  }
}
