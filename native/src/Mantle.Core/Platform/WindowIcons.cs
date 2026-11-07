using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mantle.Core.Platform;

/// <summary>
/// The window list's icons, put back (lib/winlist-icons.js). The sidecar's <c>ev windows</c> carries
/// <c>iconDiff: true</c> and leaves the "icon" key OUT of an entry whose icon is the one it last sent for
/// that hwnd (sidecar/Windows.cs WithoutSentIcons). This puts it back, so every listener receives exactly
/// the list it did before: same entries, same fields in the same order, same icons. The cache is updated
/// ONLY from iconDiff lists and holds exactly the last one's hwnds. An entry with no icon key and nothing
/// kept gets icon null and is counted in Missed, so the platform reads the whole list again and says so.
/// </summary>
public sealed class WindowIconCache
{
    Dictionary<string, string> _icons = new(StringComparer.Ordinal);   // hwnd -> the icon's raw JSON ("null" or a string)

    /// A new sidecar process has sent nothing yet.
    public void Reset() => _icons = new Dictionary<string, string>(StringComparer.Ordinal);

    public int Count => _icons.Count;

    static string Hwnd(JsonElement w) => w.TryGetProperty("hwnd", out var h) ? (h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : h.GetRawText()) : "";

    /// <summary>An iconDiff list back to the full list; Missed are the hwnds it had no icon for.</summary>
    public (JsonArray List, List<string> Missed) Take(JsonElement list)
    {
        var next = new Dictionary<string, string>(StringComparer.Ordinal);
        var missed = new List<string>();
        var outList = new JsonArray();
        if (list.ValueKind != JsonValueKind.Array) { _icons = next; return (outList, missed); }
        foreach (var w in list.EnumerateArray()) {
            if (w.ValueKind != JsonValueKind.Object) { outList.Add(JsonNode.Parse(w.GetRawText())); continue; }
            string h = Hwnd(w);
            if (w.TryGetProperty("icon", out var icon)) {
                next[h] = icon.GetRawText();
                outList.Add(JsonNode.Parse(w.GetRawText()));
                continue;
            }
            string raw = "null";
            if (_icons.TryGetValue(h, out var kept)) raw = kept; else missed.Add(h);
            next[h] = raw;
            outList.Add(WithIcon(w, raw));
        }
        _icons = next;
        return (outList, missed);
    }

    /// <summary>Fill hwnds this has no icon for from a full list (the repair after a miss).</summary>
    public void Fill(JsonElement full, IEnumerable<string> hwnds)
    {
        if (full.ValueKind != JsonValueKind.Array) return;
        var want = new HashSet<string>(hwnds, StringComparer.Ordinal);
        foreach (var w in full.EnumerateArray()) {
            if (w.ValueKind != JsonValueKind.Object) continue;
            string h = Hwnd(w);
            if (want.Contains(h) && _icons.TryGetValue(h, out var k) && k == "null")
                _icons[h] = w.TryGetProperty("icon", out var i) ? i.GetRawText() : "null";
        }
    }

    /// A copy of <paramref name="w"/> with the icon where the sidecar puts it: just before iconFrom (else last).
    public static JsonObject WithIcon(JsonElement w, string iconRaw)
    {
        var o = new JsonObject();
        bool placed = false;
        foreach (var p in w.EnumerateObject()) {
            if (!placed && p.Name == "iconFrom") { o["icon"] = JsonNode.Parse(iconRaw); placed = true; }
            o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        }
        if (!placed) o["icon"] = JsonNode.Parse(iconRaw);
        return o;
    }

    /// <summary>The whole <c>ev windows</c> message without the iconDiff flag, with the full list.</summary>
    public (JsonElement Message, List<string> Missed) Full(JsonElement msg)
    {
        var o = new JsonObject();
        List<string> missed = new();
        foreach (var p in msg.EnumerateObject()) {
            if (p.Name == "iconDiff") continue;
            if (p.Name == "list") { var (l, m) = Take(p.Value); missed = m; o["list"] = l; }
            else o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        }
        using var d = JsonDocument.Parse(o.ToJsonString());
        return (d.RootElement.Clone(), missed);
    }
}
