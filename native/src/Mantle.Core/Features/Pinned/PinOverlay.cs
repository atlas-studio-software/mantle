using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mantle.Core.Features.WindowList;

namespace Mantle.Core.Features.Pinned;

/// <summary>
/// Mantle's own Pin/Unpin overlay, the read side (lib/features/_jumplist-pins.js read/norm/apply/byOrder and the
/// session's running order, BAR-280..284, BAR-289). It lives in &lt;userData&gt;\features\jumplist.json under
/// "pins": {hide:[...], add:[...], order:[ids]}; Windows' pins are never touched. A dragged order is written by
/// <see cref="Mantle.Core.Features.WindowList.OrderSave"/> (round 5); pin, unpin and undo are still to come. This side
/// reads it unchanged, so Electron's file works as it is.
/// </summary>
public sealed class PinOverlay
{
    public sealed record Entry(string Id, string Name, string? Exe, string? Aumid, string? Shortcut);

    public const int OrderMax = 400;
    public IReadOnlyList<Entry> Hide { get; init; } = Array.Empty<Entry>();
    public IReadOnlyList<Entry> Add { get; init; } = Array.Empty<Entry>();
    public IReadOnlyList<string> Order { get; init; } = Array.Empty<string>();

    public static readonly PinOverlay Empty = new();

    public static string FileFor(string userData) => Path.Combine(userData, "features", "jumplist.json");

    /// Ids: strings only, non-empty, at most 1024 long, no repeats, capped at 400 (cleanIds).
    public static List<string> CleanIds(JsonElement list)
    {
        var outList = new List<string>();
        if (list.ValueKind != JsonValueKind.Array) return outList;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in list.EnumerateArray()) {
            if (v.ValueKind != JsonValueKind.String) continue;
            string s = v.GetString() ?? "";
            if (s.Length == 0 || s.Length > 1024 || !seen.Add(s)) continue;
            outList.Add(s);
            if (outList.Count >= OrderMax) break;
        }
        return outList;
    }

    public static List<string> CleanIds(IEnumerable<string?> list)
    {
        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in list) {
            if (string.IsNullOrEmpty(s) || s.Length > 1024 || !seen.Add(s)) continue;
            outList.Add(s);
            if (outList.Count >= OrderMax) break;
        }
        return outList;
    }

    static Entry? Clean(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? id = TaskJson.Str(e, "id");
        if (string.IsNullOrEmpty(id)) return null;
        static string? S(JsonElement o, string k) => TaskJson.Get(o, k) is { ValueKind: JsonValueKind.String } v && v.GetString() is string s && s.Length > 0 ? s : null;
        return new Entry(id, S(e, "name") ?? id, S(e, "exe"), S(e, "aumid"), S(e, "shortcut"));
    }

    /// A hand-edited or malformed overlay never breaks the bar: bad entries are dropped (norm).
    public static PinOverlay Norm(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return Empty;
        List<Entry> Take(string k)
        {
            var l = new List<Entry>();
            if (TaskJson.Get(o, k) is { ValueKind: JsonValueKind.Array } arr)
                foreach (var e in arr.EnumerateArray()) if (Clean(e) is Entry c) l.Add(c);
            return l;
        }
        return new PinOverlay { Hide = Take("hide"), Add = Take("add"), Order = CleanIds(TaskJson.Get(o, "order")) };
    }

    /// The overlay from jumplist.json (read): missing = empty; unreadable or not JSON throws with plain words.
    public static PinOverlay Read(string userData)
    {
        string text;
        try { text = File.ReadAllText(FileFor(userData)); }
        catch (FileNotFoundException) { return Empty; }
        catch (DirectoryNotFoundException) { return Empty; }
        catch (Exception e) { throw new InvalidOperationException($"Mantle's own pin list could not be read ({e.GetType().Name.Replace("Exception", "")})"); }
        try {
            using var d = JsonDocument.Parse(text);
            return Norm(TaskJson.Get(d.RootElement, "pins"));
        } catch (JsonException) {
            throw new InvalidOperationException(@"Mantle's own pin list is not valid JSON (features\jumplist.json)");
        }
    }

    /// Two different AUMIDs are two apps even on one exe; the exe decides only when an AUMID is missing (sameApp).
    public static bool SameApp(string? exeA, string? aumidA, string? exeB, string? aumidB)
    {
        if (!string.IsNullOrEmpty(aumidA) && !string.IsNullOrEmpty(aumidB)) return string.Equals(aumidA, aumidB, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrEmpty(exeA) && !string.IsNullOrEmpty(exeB) && string.Equals(exeA, exeB, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPackaged(string? aumid) => !string.IsNullOrEmpty(aumid) && aumid.IndexOf('!') > 0;

    /// Stable sort by the rank in <paramref name="order"/>; unranked keep their place after the ranked (byOrder).
    public static List<T> ByOrder<T>(IReadOnlyList<T> list, IReadOnlyList<string> order, Func<T, string> keyOf)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) rank.TryAdd(order[i], i);
        int n = rank.Count;
        return list.Select((v, i) => (v, r: rank.TryGetValue(keyOf(v), out int r) ? r : n + i, i))
            .OrderBy(x => x.r).ThenBy(x => x.i).Select(x => x.v).ToList();
    }

    /// <summary>
    /// The order to keep after a drag (_jumplist-pins.js#mergeOrder, BAR-252): <paramref name="shown"/> (the bar's pinned
    /// ids in their new order) plus every id the old list had that is not on the bar right now (a pin a partial read left
    /// out), each put back right after the id it followed, so a drag made while one pin was missing keeps its place.
    /// </summary>
    public static List<string> MergeOrder(IEnumerable<string?> stored, IEnumerable<string?> shown)
    {
        var s = CleanIds(shown);
        var inShown = new HashSet<string>(s, StringComparer.Ordinal);
        var outList = new List<string>(s);
        string? prev = null;
        foreach (var k in CleanIds(stored)) {
            if (inShown.Contains(k)) { prev = k; continue; }
            outList.Insert(prev == null ? 0 : outList.IndexOf(prev) + 1, k);
            prev = k;
        }
        for (int i = outList.Count - 1; outList.Count > OrderMax && i >= 0; i--) if (!inShown.Contains(outList[i])) outList.RemoveAt(i);
        return outList.Count > OrderMax ? outList.GetRange(0, OrderMax) : outList;
    }

    static string KeyOf(Pin p) => !string.IsNullOrEmpty(p.Id) ? p.Id : (p.LnkPath ?? p.Name);

    /// Windows' pins with Mantle's overlay applied: hidden ones out, Mantle's adds in (one per app), then the order.
    public List<Pin> Apply(IReadOnlyList<Pin> pins)
    {
        var hidden = new HashSet<string>(Hide.Select(x => x.Id), StringComparer.Ordinal);
        var outList = pins.Where(p => p != null && !hidden.Contains(KeyOf(p))).ToList();
        int order = outList.Aggregate(-1, (m, p) => Math.Max(m, p.Order)) + 1;
        foreach (var a in Add) {
            if (outList.Any(p => SameApp(p.Exe, p.Aumid, a.Exe, a.Aumid))) continue;
            bool packaged = IsPackaged(a.Aumid);
            outList.Add(new Pin {
                Id = a.Id, Name = a.Name, Kind = "mantle",
                LnkPath = packaged ? null : a.Shortcut,
                Target = packaged ? null : (a.Shortcut ?? a.Exe),
                Exe = TaskJson.Low(a.Exe), Aumid = a.Aumid,
                IconPath = packaged ? null : (a.Exe ?? a.Shortcut), IconIndex = 0, Order = order++,
            });
        }
        return Order.Count > 0 ? ByOrder(outList, Order, KeyOf) : outList;
    }
}

/// <summary>The running-only apps' dragged order for this session (setRunning / orderRunning, BAR-253, BAR-289).</summary>
public sealed class RunningOrder
{
    List<string> _order = new();

    public IReadOnlyList<string> Current => _order;

    /// A drag of a running-only app (part 2 wires the drag); returns what is kept.
    public IReadOnlyList<string> Set(IEnumerable<string> ids) { _order = PinOverlay.CleanIds(ids); return _order; }

    /// Running-only items put in the dragged order, in their own slots; pins stay where they are. With
    /// <paramref name="prune"/> (the running list was read cleanly) apps that stopped are forgotten.
    public List<T> Apply<T>(IReadOnlyList<T> items, Func<T, bool> isRunning, Func<T, string> idOf, bool prune)
    {
        var run = items.Where(isRunning).ToList();
        if (prune) {
            var here = new HashSet<string>(run.Select(idOf), StringComparer.Ordinal);
            _order = _order.Where(here.Contains).ToList();
        }
        if (run.Count < 2 || _order.Count == 0) return items.ToList();
        var sorted = PinOverlay.ByOrder(run, _order, idOf);
        int k = 0;
        return items.Select(it => isRunning(it) ? sorted[k++] : it).ToList();
    }
}
