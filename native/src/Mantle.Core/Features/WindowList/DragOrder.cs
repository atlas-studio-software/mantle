using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Pinned;

namespace Mantle.Core.Features.WindowList;

/// <summary>
/// Drag to reorder on the bar's strip (ui/bar/bar.js "drag to reorder", BAR-239..254): the pure parts, so the unit tests
/// prove them without a window. The UI side is Mantle.App's Tasks/BarWindow.Drag.cs; the save is <see cref="OrderSave"/>.
/// </summary>
public static class DragOrder
{
    /// A press that moves less (in DIP, on either axis) is a click (DRAG_PX).
    public const double DragPx = 5;
    /// DIP past the bar's edge that cancels (DRAG_OUT).
    public const double DragOut = 24;
    /// The dropped icon's glide into its slot (SETTLE_MS, bar.css .settle).
    public const int SettleMs = 150;
    /// The neighbours' slide aside (bar.css .dock.dragging .cell transition).
    public const int ShiftMs = 160;
    /// How long a dropped order outranks lists built before its save (ORDER_HOLD_MS).
    public const int OrderHoldMs = 4000;
    /// The click that ends a drag is swallowed this long (bar.js dragSwallow).
    public const int SwallowMs = 400;
    /// After the drop the bar keeps its usual grace before it may hide (main.js, 180 ms after the last beat).
    public const int GraceMs = 180;

    /// bar.js pointermove: lifts once the pointer has moved DRAG_PX on either axis.
    public static bool Lifts(double dx, double dy) => Math.Abs(dx) >= DragPx || Math.Abs(dy) >= DragPx;

    /// More than DRAG_OUT outside the bar's rectangle (left, top, right, bottom): the drag is cancelled.
    public static bool Outside(double x, double y, double left, double top, double right, double bottom)
        => x < left - DragOut || x > right + DragOut || y < top - DragOut || y > bottom + DragOut;

    /// The group an item drags within (bar.js kindOf): running-only among the running, everything else among the pins.
    public static string KindOf(TaskItem it) => it.Kind == "running" ? "running" : "pinned";

    /// <paramref name="ids"/> with the one at <paramref name="from"/> moved to <paramref name="to"/>.
    public static List<string> Move(IReadOnlyList<string> ids, int from, int to)
    {
        var l = ids.ToList();
        if (from < 0 || from >= l.Count) return l;
        to = Math.Clamp(to, 0, l.Count - 1);
        var v = l[from];
        l.RemoveAt(from);
        l.Insert(to, v);
        return l;
    }

    /// "1 -> 3 of 4" (jumplist.js order's log words: positions only, never an app's name or id).
    public static string Where(int from, int to, int count)
    {
        static string At(int v) => v >= 0 ? (v + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
        return $"{At(from)} -> {At(to)} of {count}";
    }
}

/// <summary>
/// One lift (bar.js dragLift / dragFrame / dragShift): the group's slots are read ONCE, at the lift (the left edge of each
/// icon, in DIP), and everything after is arithmetic over them. Electron rounds dx by a single pitch (the first two
/// slots); this picks the nearest slot, which is the same for an even strip and stays right when the slots are uneven.
/// </summary>
public sealed class DragPlan
{
    readonly double[] _slots;

    public DragPlan(IReadOnlyList<double> slots, int from)
    {
        if (slots.Count == 0) throw new ArgumentException("a drag needs at least its own slot", nameof(slots));
        _slots = slots.ToArray();
        From = To = Math.Clamp(from, 0, _slots.Length - 1);
        // within its group (bar.js d.min / d.max)
        Min = _slots.Min() - _slots[From];
        Max = _slots.Max() - _slots[From];
    }

    public int From { get; }
    public int To { get; private set; }
    public int Count => _slots.Length;
    public double Min { get; }
    public double Max { get; }

    /// The lifted icon's offset for a pointer <paramref name="dx"/> from the press: never past the group's ends.
    public double Clamp(double dx) => Math.Clamp(dx, Min, Max);

    /// Follow the pointer; true when the slot it would land in changed (the neighbours then slide).
    public bool Track(double dx)
    {
        double x = _slots[From] + Clamp(dx);
        int best = 0;
        for (int k = 1; k < _slots.Length; k++) if (Math.Abs(_slots[k] - x) < Math.Abs(_slots[best] - x)) best = k;
        if (best == To) return false;
        To = best;
        return true;
    }

    /// Esc, out of the bar, capture lost: it goes back where it was.
    public bool Cancel() { bool moved = To != From; To = From; return moved; }

    /// How far neighbour <paramref name="i"/> stands aside (one slot left or right), 0 when it stays.
    public double ShiftOf(int i)
    {
        if (i == From || i < 0 || i >= _slots.Length) return 0;
        if (To > From && i > From && i <= To) return _slots[i - 1] - _slots[i];
        if (To < From && i >= To && i < From) return _slots[i + 1] - _slots[i];
        return 0;
    }

    /// Where the lifted icon glides on the drop: its new slot, relative to where it was laid out.
    public double DropOffset => _slots[To] - _slots[From];
}

/// <summary>
/// bar.js orderKept (BAR-248): a list built before the drop's save landed still has the old order, so the dropped one is
/// kept over it for ORDER_HOLD_MS (or until a failed save gives it up) and an icon never hops back. Items the dropped
/// order does not name keep their own places, after it; the other group is never touched.
/// </summary>
public sealed class OrderHold
{
    string? _kind;
    List<string>? _ids;
    double _until;

    public bool Holding(double nowMs) => _ids != null && nowMs <= _until;
    public string? Kind => _kind;

    public void Want(string kind, IReadOnlyList<string> ids, double nowMs)
    {
        _kind = kind;
        _ids = ids.ToList();
        _until = nowMs + DragOrder.OrderHoldMs;
    }

    public void Drop() { _kind = null; _ids = null; }

    public TaskList Apply(TaskList list, double nowMs)
    {
        if (_ids == null || _kind == null) return list;
        if (nowMs > _until) { Drop(); return list; }
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < _ids.Count; i++) rank.TryAdd(_ids[i], i);
        string kind = _kind;
        var mine = list.Items.Where(it => DragOrder.KindOf(it) == kind).ToList();
        var sorted = mine.Select((it, i) => (it, r: rank.TryGetValue(it.Id, out int r) ? r : _ids.Count + i))
            .OrderBy(x => x.r).Select(x => x.it).ToList();
        bool same = true;
        for (int i = 0; i < sorted.Count && same; i++) if (!ReferenceEquals(sorted[i], mine[i])) same = false;
        if (same) return list;
        int k = 0;
        return list with { Items = list.Items.Select(it => DragOrder.KindOf(it) == kind ? sorted[k++] : it).ToList() };
    }
}

/// The jumplist.order reply: saved "pins" (Mantle's own pin list), "session" (running-only), or not, with the reason.
public sealed record OrderSaved(bool Ok, string? Reason, string? Saved = null);

/// <summary>
/// lib/features/jumplist.js 'order' (BAR-243, BAR-252..254): the bar sends one group's ids in their new order.
/// pinned: saved in Mantle's own pin list (features\jumplist.json "pins".order, merged with what is stored, so a pin a
/// partial read left out keeps its place); hide and add, and every other key of the file, are kept as they are.
/// running: this session only (an app that is merely running has no pin to remember it by).
/// The log gets counts and positions only, never an app's name or id.
/// </summary>
public static class OrderSave
{
    public static async ValueTask<OrderSaved> SaveAsync(string? group, IReadOnlyList<string?>? rawIds, int from, int to,
        IStore? store, RunningOrder running, Action<string> log, bool test = false, string? failForTest = null)
    {
        string g = group == "running" ? "running" : "pinned";
        var ids = PinOverlay.CleanIds(rawIds ?? Array.Empty<string?>());
        if (ids.Count == 0) return new OrderSaved(false, "the bar sent no order to save");
        string where = DragOrder.Where(from, to, ids.Count);
        if (g == "running") {
            running.Set(ids);
            log($"bar tasks: order: a running app moved {where} (kept for this session)");
            return new OrderSaved(true, null, "session");
        }
        string? why = failForTest;
        if (why == null && store == null) why = "Mantle's own pin list is not open in this run";
        if (why == null) {
            try {
                // a file changed on disk since it was read (Electron, or a hand edit): merge with what is there now
                if (store is JsonStore js && js.DiskStamp != js.LoadedStamp) js.Load();
                JsonObject pins = store!.TryGet("pins", out var cur) && cur.ValueKind == JsonValueKind.Object
                    ? (JsonNode.Parse(cur.GetRawText()) as JsonObject ?? new JsonObject()) : new JsonObject();
                var stored = PinOverlay.CleanIds(TaskJson.Get(cur, "order"));
                var merged = PinOverlay.MergeOrder(stored, ids);
                if (pins["hide"] is not JsonArray) pins["hide"] = new JsonArray();
                if (pins["add"] is not JsonArray) pins["add"] = new JsonArray();
                pins["order"] = new JsonArray(merged.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                var w = await store.SetAsync("pins", pins).ConfigureAwait(true);
                if (!w.Ok) why = w.Reason ?? "the store did not say why";
            } catch (Exception e) { why = e.Message; }
        }
        if (why != null) {
            log($"bar tasks: order: a pinned app moved {where} and the order was NOT saved: {why}");
            return new OrderSaved(false, "the new order was not saved: " + why);
        }
        log($"bar tasks: order: a pinned app moved {where}, saved{(test ? " (in this test profile)" : "")}");
        return new OrderSaved(true, null, "pins");
    }
}
