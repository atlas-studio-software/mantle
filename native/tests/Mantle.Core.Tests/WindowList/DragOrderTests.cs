using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Pinned;
using PinOverlay = Mantle.Core.Features.Pinned.PinOverlay;
using Mantle.Core.Features.WindowList;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

// Ported from lib/features/jumplist-order.test.js (mergeOrder, setOrder, orderRunning, the 'order' command) and the pure
// parts of ui/bar/bar.page.test.js "drag" (DRAG_PX, the group's bounds, the slots, orderKept): BAR-239..254.
public class DragOrderTests
{
    static TaskItem P(string id) => new(id, "App " + id, "pinned", null, null, null, "none", Array.Empty<TaskWindow>());
    static TaskItem R(string id) => new("running:" + id, id, "running", null, null, null, "x", new[] { new TaskWindow("1", 1, id, false, false) });
    static string[] Ids(TaskList l) => l.Items.Select(i => i.Id).ToArray();

    [Fact]
    public void A_press_lifts_at_5_px_on_either_axis_and_a_4_px_wobble_stays_a_click()
    {
        Assert.False(DragOrder.Lifts(4, 4));
        Assert.False(DragOrder.Lifts(-4.9, 0));
        Assert.True(DragOrder.Lifts(5, 0));
        Assert.True(DragOrder.Lifts(0, -5));
        // DRAG_OUT: 24 DIP past any edge of the bar
        Assert.False(DragOrder.Outside(-24, 10, 0, 0, 100, 48));
        Assert.True(DragOrder.Outside(-24.5, 10, 0, 0, 100, 48));
        Assert.True(DragOrder.Outside(50, 72.5, 0, 0, 100, 48));
        Assert.False(DragOrder.Outside(124, 72, 0, 0, 100, 48));
    }

    [Fact]
    public void The_lifted_icon_stays_in_its_group_and_the_icons_it_passes_slide_one_slot()
    {
        // three pins, 42 DIP apart (36 + the 6 DIP gap), dragged from the first
        var plan = new DragPlan(new double[] { 100, 142, 184 }, 0);
        Assert.Equal(0, plan.Min);
        Assert.Equal(84, plan.Max);
        Assert.Equal(84, plan.Clamp(400));     // never past the last pin, whatever the pointer does
        Assert.Equal(0, plan.Clamp(-50));
        Assert.False(plan.Track(20));          // nearer its own slot
        Assert.True(plan.Track(22));           // past half a slot: the next one
        Assert.Equal(1, plan.To);
        Assert.Equal(-42, plan.ShiftOf(1));
        Assert.Equal(0, plan.ShiftOf(2));
        Assert.True(plan.Track(1000));
        Assert.Equal(2, plan.To);
        Assert.Equal(-42, plan.ShiftOf(1));
        Assert.Equal(-42, plan.ShiftOf(2));
        Assert.Equal(0, plan.ShiftOf(0));      // the lifted one is moved by the pointer, not by a shift
        Assert.Equal(84, plan.DropOffset);
        // Esc: back where it was, nobody shifted
        Assert.True(plan.Cancel());
        Assert.Equal(0, plan.To);
        Assert.Equal(0, plan.ShiftOf(1));
        Assert.Equal(0, plan.DropOffset);

        // dragged left from the last: the ones it passes move right
        var back = new DragPlan(new double[] { 100, 142, 184 }, 2);
        Assert.True(back.Track(-84));
        Assert.Equal(0, back.To);
        Assert.Equal(42, back.ShiftOf(0));
        Assert.Equal(42, back.ShiftOf(1));
        Assert.Equal(-84, back.DropOffset);
        // uneven slots (a group that starts after the hairline): the nearest slot, not one pitch
        var uneven = new DragPlan(new double[] { 300, 355, 397 }, 0);
        Assert.True(uneven.Track(70));
        Assert.Equal(1, uneven.To);
        Assert.Equal(-55, uneven.ShiftOf(1));
    }

    [Fact]
    public void Move_puts_one_id_in_its_new_place()
    {
        Assert.Equal(new[] { "b", "c", "a" }, DragOrder.Move(new[] { "a", "b", "c" }, 0, 2));
        Assert.Equal(new[] { "c", "a", "b" }, DragOrder.Move(new[] { "a", "b", "c" }, 2, 0));
        Assert.Equal(new[] { "a", "b", "c" }, DragOrder.Move(new[] { "a", "b", "c" }, 1, 1));
        Assert.Equal("1 -> 3 of 3", DragOrder.Where(0, 2, 3));
        Assert.Equal("? -> 1 of 2", DragOrder.Where(-1, 0, 2));
    }

    [Fact]
    public void A_merged_order_keeps_a_pin_the_bar_did_not_show_after_the_one_it_followed()
    {
        // jumplist-order.test.js: shown first, every stored id not shown goes back right after the id it followed
        Assert.Equal(new[] { "b", "a", "c" }, PinOverlay.MergeOrder(new[] { "a", "b", "c" }, new[] { "b", "a", "c" }));
        Assert.Equal(new[] { "c", "a", "x", "b" }, PinOverlay.MergeOrder(new[] { "a", "x", "b", "c" }, new[] { "c", "a", "b" }));
        Assert.Equal(new[] { "x", "b", "a" }, PinOverlay.MergeOrder(new[] { "x", "a", "b" }, new[] { "b", "a" }));
        Assert.Equal(new[] { "a", "b" }, PinOverlay.MergeOrder(Array.Empty<string>(), new string?[] { "a", "b", "a", "", null }));
        // a runaway stored list is cut at 400, never the ids on the bar
        var stored = Enumerable.Range(0, 500).Select(i => "s" + i).ToArray();
        var merged = PinOverlay.MergeOrder(stored, new[] { "a", "b" });
        Assert.Equal(PinOverlay.OrderMax, merged.Count);
        Assert.Contains("a", merged);
        Assert.Contains("b", merged);
        // pins the stored order does not name go at the end, in Windows' order (byOrder)
        var pins = new[] { new Pin { Id = "a" }, new Pin { Id = "b" }, new Pin { Id = "new" }, new Pin { Id = "c" } };
        var ov = new PinOverlay { Order = new[] { "c", "a", "b" } };
        Assert.Equal(new[] { "c", "a", "b", "new" }, ov.Apply(pins).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void A_dropped_order_outranks_older_lists_for_4_s_and_never_touches_the_other_group()
    {
        var hold = new OrderHold();
        var old = new TaskList(true, null, new[] { P("a"), P("b"), P("c"), R("x"), R("y") });
        Assert.Same(old, hold.Apply(old, 0));
        hold.Want("pinned", new[] { "b", "c", "a" }, 1000);
        Assert.Equal(new[] { "b", "c", "a", "running:x", "running:y" }, Ids(hold.Apply(old, 1000 + 3999)));
        // a list that already carries it comes back as it is
        var fresh = new TaskList(true, null, new[] { P("b"), P("c"), P("a"), R("x"), R("y") });
        Assert.Same(fresh, hold.Apply(fresh, 2000));
        // an item the dropped order does not name keeps its place after it
        var more = new TaskList(true, null, new[] { P("n"), P("a"), P("b"), P("c"), R("y") });
        Assert.Equal(new[] { "b", "c", "a", "n", "running:y" }, Ids(hold.Apply(more, 2000)));
        // after ORDER_HOLD_MS the list wins again
        Assert.Same(old, hold.Apply(old, 1000 + DragOrder.OrderHoldMs + 1));
        Assert.False(hold.Holding(1000 + DragOrder.OrderHoldMs + 2));
        // a running-only drop sorts the running group only; a failed save gives the hold up at once
        hold.Want("running", new[] { "running:y", "running:x" }, 0);
        Assert.Equal(new[] { "a", "b", "c", "running:y", "running:x" }, Ids(hold.Apply(old, 10)));
        hold.Drop();
        Assert.Same(old, hold.Apply(old, 20));
    }

    static Stores ScratchStores(TestDir d) => new(d.Path, new SafeFile(WriteGate.Scratch(d.Path)), new ListLog());

    [Fact]
    public async Task A_pinned_order_is_saved_in_Mantles_own_pin_list_and_keeps_its_hide_add_and_other_keys()
    {
        using var d = new TestDir("drag-order");
        d.Write(@"features\jumplist.json", "{\"other\":7,\"pins\":{\"hide\":[{\"id\":\"h\",\"name\":\"H\"}],\"add\":[],\"order\":[\"a\",\"gone\",\"b\",\"c\"]}}");
        using var stores = ScratchStores(d);
        var log = new List<string>();
        var run = new RunningOrder();
        var r = await OrderSave.SaveAsync("pinned", new[] { "c", "a", "b" }, 2, 0, stores.Open(@"features\jumplist.json"), run, log.Add, test: true);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal("pins", r.Saved);
        Assert.Contains(log, l => l == "bar tasks: order: a pinned app moved 3 -> 1 of 3, saved (in this test profile)");
        using var doc = JsonDocument.Parse(File.ReadAllText(d.File(@"features\jumplist.json")));
        Assert.Equal(7, doc.RootElement.GetProperty("other").GetInt32());
        var pins = doc.RootElement.GetProperty("pins");
        Assert.Equal(new[] { "c", "a", "gone", "b" }, pins.GetProperty("order").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("h", pins.GetProperty("hide")[0].GetProperty("id").GetString());
        // what the next build reads (PinOverlay.Read): the new order
        Assert.Equal(new[] { "c", "a", "gone", "b" }, PinOverlay.Read(d.Path).Order);
        Assert.Empty(run.Current);
    }

    [Fact]
    public async Task A_running_only_order_lasts_for_the_session_and_a_stopped_app_is_forgotten_only_on_a_clean_read()
    {
        var log = new List<string>();
        var run = new RunningOrder();
        var r = await OrderSave.SaveAsync("running", new[] { "running:y", "running:x" }, 1, 0, null, run, log.Add);
        Assert.True(r.Ok);
        Assert.Equal("session", r.Saved);
        Assert.Contains(log, l => l == "bar tasks: order: a running app moved 2 -> 1 of 2 (kept for this session)");
        var items = new List<TaskItem> { P("a"), R("x"), R("y"), R("z") };
        Assert.Equal(new[] { "a", "running:y", "running:x", "running:z" }, run.Apply(items, i => i.Kind == "running", i => i.Id, prune: false).Select(i => i.Id).ToArray());
        // y stopped, but the running list was not read cleanly: it keeps its place in the order
        run.Apply(new List<TaskItem> { P("a"), R("x") }, i => i.Kind == "running", i => i.Id, prune: false);
        Assert.Equal(new[] { "running:y", "running:x" }, run.Current);
        // a clean read forgets it, so it comes back at the end when it starts again
        run.Apply(new List<TaskItem> { P("a"), R("x") }, i => i.Kind == "running", i => i.Id, prune: true);
        Assert.Equal(new[] { "running:x" }, run.Current);
        Assert.Equal(new[] { "running:x", "running:z", "running:y" }, run.Apply(new List<TaskItem> { R("z"), R("y"), R("x") }, i => i.Kind == "running", i => i.Id, prune: true).Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task A_save_the_gate_refuses_says_so_and_leaves_the_file_as_it_was()
    {
        using var d = new TestDir("drag-order-no");
        string before = "{\"pins\":{\"hide\":[],\"add\":[],\"order\":[\"a\",\"b\"]}}";
        d.Write(@"features\jumplist.json", before);
        using var stores = new Stores(d.Path, new SafeFile(WriteGate.Never("a test says no")), new ListLog());
        var log = new List<string>();
        var r = await OrderSave.SaveAsync("pinned", new[] { "b", "a" }, 1, 0, stores.Open(@"features\jumplist.json"), new RunningOrder(), log.Add);
        Assert.False(r.Ok);
        Assert.StartsWith("the new order was not saved: ", r.Reason);
        Assert.Contains(log, l => l.StartsWith("bar tasks: order: a pinned app moved 2 -> 1 of 2 and the order was NOT saved: ", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(d.File(@"features\jumplist.json")));
        Assert.Equal(new[] { "a", "b" }, PinOverlay.Read(d.Path).Order);
        // no store, no ids, the test hook: each says why
        Assert.Equal("the new order was not saved: Mantle's own pin list is not open in this run",
            (await OrderSave.SaveAsync("pinned", new[] { "a" }, 0, 0, null, new RunningOrder(), _ => { })).Reason);
        Assert.Equal("the bar sent no order to save", (await OrderSave.SaveAsync("pinned", new string?[] { null, "" }, 0, 0, null, new RunningOrder(), _ => { })).Reason);
        Assert.Equal("the new order was not saved: test words",
            (await OrderSave.SaveAsync("pinned", new[] { "a" }, 0, 0, stores.Open(@"features\jumplist.json"), new RunningOrder(), _ => { }, failForTest: "test words")).Reason);
    }
}
