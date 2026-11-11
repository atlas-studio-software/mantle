using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Mantle.Core.Features.Pinned;

/// <summary>
/// The write side of Mantle's own pin list (lib/features/_jumplist-pins.js pin, unpin, undoUnpin): the right-click
/// menu's "Pin to taskbar" / "Unpin from taskbar" (BAR-216, BAR-280, BAR-281, BAR-285, BAR-324). Windows' own pins are
/// never touched: a Mantle pin is an <c>add</c>, unpinning a Windows pin is a <c>hide</c>. Pure: overlay in, overlay out.
/// </summary>
public static class PinOverlayEdit
{
    /// The app a menu acts on (a bar item: id, name, exe, AUMID; the shortcut Windows would start it from, if known).
    public sealed record App(string Id, string Name, string? Exe, string? Aumid, string? Shortcut = null);

    public sealed record Result(PinOverlay Overlay, string Did);

    static string Low(string? v) => string.IsNullOrEmpty(v) ? "" : v.ToLowerInvariant();

    static bool Match(PinOverlay.Entry x, App a) => x.Id == a.Id || PinOverlay.SameApp(x.Exe, x.Aumid, a.Exe, a.Aumid);

    /// Pin: did = "unhid" (a hidden Windows pin shown again), "added" or "already".
    public static Result Pin(PinOverlay o, App item)
    {
        var hide = o.Hide.ToList();
        var add = o.Add.ToList();
        int h = hide.FindIndex(x => Match(x, item));
        if (h >= 0) { hide.RemoveAt(h); return new Result(new PinOverlay { Hide = hide, Add = add, Order = o.Order.ToList() }, "unhid"); }
        if (add.Any(x => Match(x, item))) return new Result(o, "already");
        if (item.Exe == null && item.Aumid == null && item.Shortcut == null)
            throw new InvalidOperationException($"{(item.Name.Length > 0 ? item.Name : "this app")} cannot be pinned: Mantle does not know what program it is");
        string key = Low(item.Aumid) is { Length: > 0 } k1 ? k1 : Low(item.Exe) is { Length: > 0 } k2 ? k2 : Low(item.Shortcut);
        add.Add(new PinOverlay.Entry("mantle:" + key, item.Name.Length > 0 ? item.Name : key, item.Exe, item.Aumid, item.Shortcut));
        return new Result(new PinOverlay { Hide = hide, Add = add, Order = o.Order.ToList() }, "added");
    }

    /// Unpin: a Mantle pin is dropped ("removed"), a Windows pin is hidden ("hid"), else "already". The app leaves the
    /// dragged order too: pinned again later, it goes at the end (as on Windows).
    public static Result Unpin(PinOverlay o, App item)
    {
        var order = o.Order.Where(k => k != item.Id).ToList();
        var add = o.Add.ToList();
        var hide = o.Hide.ToList();
        int a = add.FindIndex(x => Match(x, item));
        if (a >= 0) {
            string aid = add[a].Id;
            order.RemoveAll(k => k == aid);
            add.RemoveAt(a);
            return new Result(new PinOverlay { Hide = hide, Add = add, Order = order }, "removed");
        }
        if (hide.Any(x => x.Id == item.Id)) return new Result(new PinOverlay { Hide = hide, Add = add, Order = order }, "already");
        hide.Add(new PinOverlay.Entry(item.Id, item.Name.Length > 0 ? item.Name : item.Id, item.Exe, item.Aumid, null));
        return new Result(new PinOverlay { Hide = hide, Add = add, Order = order }, "hid");
    }

    /// Undo an unpin: <paramref name="before"/> is the overlay just before it. The app comes back exactly where it was
    /// (a Mantle pin at its old place among Mantle's pins, a hidden Windows pin shown again, its id back in the dragged
    /// order right after the id it followed); anything changed since is kept. did = "restored".
    public static Result UndoUnpin(PinOverlay current, PinOverlay before, App item)
    {
        var add = current.Add.ToList();
        var hide = current.Hide.ToList();
        var order = current.Order.ToList();
        var ids = new HashSet<string>(StringComparer.Ordinal) { item.Id };
        int ba = before.Add.ToList().FindIndex(x => Match(x, item));
        if (ba >= 0) {
            ids.Add(before.Add[ba].Id);
            if (!add.Any(x => Match(x, item))) add.Insert(Math.Min(ba, add.Count), before.Add[ba]);
        }
        // a Windows pin the unpin hid is shown again (one that was hidden BEFORE it stays hidden)
        if (!before.Hide.Any(x => x.Id == item.Id)) hide.RemoveAll(x => x.Id == item.Id);
        string? prev = null;
        foreach (var k in before.Order) {
            if (order.Contains(k)) { prev = k; continue; }
            if (!ids.Contains(k)) continue;
            order.Insert(prev == null ? 0 : order.IndexOf(prev) + 1, k);
            prev = k;
        }
        if (order.Count > PinOverlay.OrderMax) order = order.Take(PinOverlay.OrderMax).ToList();
        return new Result(new PinOverlay { Hide = hide, Add = add, Order = order }, "restored");
    }

    /// The overlay as jumplist.json's "pins" value. JsonNode casts (JsonArray.Add&lt;T&gt; is not trim-safe).
    public static JsonObject ToJson(PinOverlay o)
    {
        static JsonNode? S(string? v) => v == null ? null : JsonValue.Create(v);
        JsonArray Entries(IEnumerable<PinOverlay.Entry> l) => new(l.Select(e => (JsonNode?)new JsonObject {
            ["id"] = e.Id, ["name"] = e.Name, ["exe"] = S(e.Exe), ["aumid"] = S(e.Aumid), ["shortcut"] = S(e.Shortcut),
        }).ToArray());
        return new JsonObject {
            ["hide"] = Entries(o.Hide),
            ["add"] = Entries(o.Add),
            ["order"] = new JsonArray(o.Order.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
        };
    }
}
