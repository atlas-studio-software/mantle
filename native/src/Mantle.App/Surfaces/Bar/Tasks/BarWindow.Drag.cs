using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mantle.App;

/// <summary>
/// Drag to reorder on the strip (ui/bar/bar.js "drag to reorder", BAR-239..254). Press an icon and move it DRAG_PX: it
/// lifts (lighter surface, a hairline, the glyph at 1.1) and follows the pointer along its own group while the icons
/// it passes slide one slot aside; letting go glides it into its slot (SETTLE_MS) and the group's new order goes to the
/// host (jumplist.order: pins saved in Mantle's own pin list, running-only apps for this session). Esc, the pointer
/// more than DRAG_OUT outside the bar, a lost capture, or the button coming up unseen: it springs back, nothing saved.
/// The slots are read ONCE, at the lift; every move after is arithmetic and a composition offset (UIElement.Translation
/// with its transition), so a frame is never a layout. The lift takes the pointer capture from the cell's Button, so
/// the Button never sees a click from a drag. While lifted the bar stays up wherever the pointer is (HoldsBar), and no
/// tooltip, flyout or pop-up opens until the pointer leaves the icon it was dropped on.
/// </summary>
public sealed partial class BarWindow
{
    sealed class DragState
    {
        public required CellVm Vm;
        public required Button Btn;
        public uint PointerId;
        public Point P0, P;
        public bool Lifted, Ended;
        public string Kind = "pinned";
        public List<CellVm> Group = new();
        public List<Button> Btns = new();
        public double[] Shift = Array.Empty<double>();
        public DragPlan Plan = null!;
        public Rect Bar;
        public Border? Hairline;
        public Brush? WasBackground;
    }

    DragState? _drag;
    DispatcherQueueTimer? _dragSettle, _dragBeat, _orderFailClear;
    bool _settling, _dragPending, _dragQuiet, _dragHoldWrapped, _dragCaptureSaid;
    CellVm? _dragQuietCell;
    long _dragSwallowUntil, _dragHoldUntil;
    readonly OrderHold _orderHold = new();

    /// Lifted and not let go yet, or gliding into its slot: strip pushes wait, nothing opens (bar.js dragBusy).
    bool DragBusy => _drag is { Lifted: true, Ended: false } || _settling;

    /// The click that ends a drag (and any click while one is lifted) opens nothing.
    bool DragSwallows()
    {
        if (DragBusy) return true;
        if (Environment.TickCount64 < _dragSwallowUntil) { _dragSwallowUntil = 0; return true; }
        return false;
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    void HookDrag()
    {
        // handledEventsToo: the cell's Button marks its own press and release handled
        Dock.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(DragPressed), true);
        Dock.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(DragMoved), true);
        Dock.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => DragUp(e, cancel: null)), true);
        Dock.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => DragUp(e, cancel: "the pointer was taken away")), true);
        Dock.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) => {
            if (_drag is not { Lifted: true, Ended: false } d || e.Pointer.PointerId != d.PointerId) return;
            // a release takes the capture away first (the Button's own handling runs before the strip hears the
            // button-up): decide once this input has been handled, so only a capture lost WITHOUT a release cancels
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => {
                if (_drag == d && !d.Ended) DragDrop("the pointer capture was lost");
            });
        }), true);
        // the quiet ends when the pointer leaves the strip (bar.js mouseleave on the bar); PointerExited bubbles up from
        // the cells, so only a pointer outside the strip's own box counts
        Dock.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, e) => {
            if (!_dragQuiet || DragBusy) return;
            var p = e.GetCurrentPoint(Dock).Position;
            if (p.X < 0 || p.Y < 0 || p.X >= Dock.ActualWidth || p.Y >= Dock.ActualHeight) DragQuietOff();
        }), true);
        // Esc when the bar has the keyboard (it rarely does; the beat below also reads the key itself)
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => {
            if (e.Key != global::Windows.System.VirtualKey.Escape || _drag is not { Lifted: true, Ended: false }) return;
            e.Handled = true;
            DragDrop("Esc");
        }), true);
    }

    static Button? CellButtonOf(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d)) if (d is Button b && b.Tag is CellVm) return b;
        return null;
    }

    Button? ButtonOf(CellVm vm) => Dock.ContainerFromItem(vm) is DependencyObject c ? FindButton(c) as Button : null;

    void DragPressed(object sender, PointerRoutedEventArgs e)
    {
        // a press, or a finished drag whose button-up never came, is over now; a stuck lift springs back
        if (_drag != null && (_drag.Ended || !_drag.Lifted)) _drag = null;
        if (_drag != null) { DragDrop("a new press came before the button-up"); _drag = null; }
        var pt = e.GetCurrentPoint(Root);
        if (_settling || pt.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed || !pt.Properties.IsPrimary) return;
        if (CellButtonOf(e.OriginalSource as DependencyObject) is not { Tag: CellVm vm } b) return;
        _drag = new DragState { Vm = vm, Btn = b, PointerId = e.Pointer.PointerId, P0 = pt.Position, P = pt.Position };
    }

    void DragMoved(object sender, PointerRoutedEventArgs e)
    {
        var d = _drag;
        if (d == null || d.Ended || e.Pointer.PointerId != d.PointerId) return;
        var pt = e.GetCurrentPoint(Root);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed) {
            // the button came up where the bar never heard it
            if (d.Lifted) DragDrop("the button was released where the bar did not see it");
            _drag = null;
            return;
        }
        d.P = pt.Position;
        if (!d.Lifted) {
            if (!DragOrder.Lifts(d.P.X - d.P0.X, d.P.Y - d.P0.Y)) return;
            if (!DragLift(d, e.Pointer)) { _drag = null; return; }
            e.Handled = true;
        }
        if (DragOrder.Outside(d.P.X, d.P.Y, d.Bar.Left, d.Bar.Top, d.Bar.Right, d.Bar.Bottom)) { DragDrop("the pointer left the bar"); return; }
        DragFrame(d);
    }

    void DragUp(PointerRoutedEventArgs e, string? cancel)
    {
        var d = _drag;
        if (d == null || e.Pointer.PointerId != d.PointerId) return;
        if (d.Lifted) {
            if (!d.Ended) DragDrop(cancel);
            _dragSwallowUntil = Environment.TickCount64 + DragOrder.SwallowMs;
            e.Handled = true;
        }
        _drag = null;
    }

    bool DragLift(DragState d, Pointer ptr)
    {
        d.Kind = DragOrder.KindOf(d.Vm.Item);
        d.Group = _cells.Where(c => DragOrder.KindOf(c.Item) == d.Kind).ToList();
        int from = d.Group.IndexOf(d.Vm);
        if (from < 0) return false;
        // never into the "+N" tail (bar.js d.list, BAR-242): only the group's icons on the strip are slots; the hidden
        // ones are the order's tail, so the save below still moves within the whole group by the same indexes
        int onStrip = d.Group.Count == 0 ? 0 : StripFit.OnStrip(_cells.IndexOf(d.Group[0]), d.Group.Count, OnStripCount);
        if (from >= onStrip) return false;
        var slots = new List<double>();
        foreach (var c in d.Group.Take(onStrip)) {
            if (ButtonOf(c) is not Button b) { Host.Log.Line("bar tasks: drag: an icon of the group is not laid out yet, so the press stays a click"); return false; }
            b.TranslationTransition = null;
            b.Translation = Vector3.Zero;
            d.Btns.Add(b);
            // the one layout read of the whole drag
            slots.Add(b.TransformToVisual(Root).TransformPoint(new Point(0, 0)).X);
        }
        d.Btn = d.Btns[from];
        d.Plan = new DragPlan(slots, from);
        d.Shift = new double[slots.Count];
        d.Bar = Pill.TransformToVisual(Root).TransformBounds(new Rect(0, 0, Pill.ActualWidth, Pill.ActualHeight));
        // the pointer is the strip's now: the cell's Button lets it go, so it is no longer pressed and a drag never ends in
        // its click (before the lift is marked, so the Button's own capture loss is not taken for a cancel). If the strip
        // cannot take it, the Button keeps it: its moves still bubble here, its click is swallowed, and the capture loss
        // its release brings is told apart from a real one (below)
        try { d.Btn.ReleasePointerCapture(ptr); } catch (ArgumentException) { }
        if (!Dock.CapturePointer(ptr) && !_dragCaptureSaid) {
            _dragCaptureSaid = true;
            Host.Log.Line("bar tasks: drag: the strip could not take the pointer from the icon, so the icon keeps it (its click is swallowed)");
        }
        d.Lifted = true;
        // nothing opens on top of a drag, and what was open closes
        StopTimer(_flyDwell);
        if (Popups?.OpenKey != null) Popups?.Close("an icon was lifted to be dragged");
        DragQuietOn();
        HoldBarWhileDragging();
        var tr = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(DragOrder.ShiftMs), Components = Vector3TransitionComponents.X };
        foreach (var b in d.Btns) if (b != d.Btn) b.TranslationTransition = tr;
        LiftLook(d, true);
        if (Dock.ContainerFromItem(d.Vm) is UIElement cont) Canvas.SetZIndex(cont, 1);
        // Esc and the pointer's real place, read every 30 ms: the bar is no-activate (rarely has the keyboard), and a
        // pointer that drifts out of the window may send it no moves
        Timer(ref _dragBeat, 30, DragBeat);
        Host.Log.Line($"bar tasks: drag: lifted a {d.Kind} app at {from + 1} of {slots.Count}");
        return true;
    }

    void DragBeat()
    {
        var d = _drag;
        if (d is not { Lifted: true, Ended: false }) return;
        if ((GetAsyncKeyState(0x1B) & 0x8000) != 0) { DragDrop("Esc"); return; }
        if (FlyCursor(out var p)) {
            var g = Geometry; double sc = g.Scale > 0 ? g.Scale : 1;
            double x = (p.X - g.Window.Left) / sc, y = (p.Y - g.Window.Top) / sc;
            if (DragOrder.Outside(x, y, d.Bar.Left, d.Bar.Top, d.Bar.Right, d.Bar.Bottom)) { DragDrop("the pointer left the bar"); return; }
        }
        Timer(ref _dragBeat, 30, DragBeat);
    }

    /// The lifted look (bar.css .lifted): a lighter surface, a hairline round it, the glyph at 1.1; on top of its neighbours.
    void LiftLook(DragState d, bool on)
    {
        var b = d.Btn;
        if (on) {
            d.WasBackground = b.Background;
            if (TryToken("LgHover") is Brush hover) b.Background = hover;
            if (VisualTreeHelper.GetChildrenCount(b) > 0 && VisualTreeHelper.GetChild(b, 0) is Grid face) {
                d.Hairline = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), IsHitTestVisible = false, BorderBrush = TryToken("LgEdgeOut") as Brush };
                face.Children.Add(d.Hairline);
            }
            if (b.Content is FrameworkElement glyph) {
                glyph.CenterPoint = new Vector3((float)(glyph.ActualWidth / 2), (float)(glyph.ActualHeight / 2), 0);
                glyph.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(DragOrder.ShiftMs) };
                glyph.Scale = new Vector3(1.1f, 1.1f, 1);
            }
            return;
        }
        b.Background = d.WasBackground;
        if (d.Hairline != null && VisualTreeHelper.GetParent(d.Hairline) is Panel p) p.Children.Remove(d.Hairline);
        d.Hairline = null;
        if (b.Content is FrameworkElement g) { g.ScaleTransition = null; g.Scale = Vector3.One; }
    }

    object? TryToken(string key) { try { return Res(key); } catch (InvalidOperationException) { return null; } }

    /// The lifted icon follows the pointer (no transition: it is under the finger); its neighbours slide (160 ms).
    void DragFrame(DragState d)
    {
        double dx = d.Plan.Clamp(d.P.X - d.P0.X);
        d.Btn.Translation = new Vector3((float)dx, 0, 0);
        if (d.Plan.Track(d.P.X - d.P0.X)) DragShift(d);
    }

    void DragShift(DragState d)
    {
        for (int i = 0; i < d.Btns.Count; i++) {
            if (i == d.Plan.From) continue;
            double s = d.Plan.ShiftOf(i);
            if (d.Shift[i] == s) continue;
            d.Shift[i] = s;
            d.Btns[i].Translation = new Vector3((float)s, 0, 0);
        }
    }

    /// Let go (cancel = null) or spring back (cancel = why): glide into the slot, then commit with nothing left moving.
    void DragDrop(string? cancel)
    {
        var d = _drag;
        if (d == null || d.Ended) return;
        d.Ended = true;
        StopTimer(_dragBeat);
        if (!d.Lifted) return;
        // the drag's hold on the bar ends here: the usual grace from now on
        _dragHoldUntil = Environment.TickCount64 + DragOrder.GraceMs;
        try { Dock.ReleasePointerCaptures(); } catch (ArgumentException) { /* already released */ }
        if (cancel != null) {
            if (d.Plan.Cancel()) DragShift(d);
            Host.Log.Line($"bar tasks: drag: cancelled ({cancel}): the icon went back to {d.Plan.From + 1} of {d.Plan.Count}, nothing saved");
        } else {
            Host.Log.Line($"bar tasks: drag: dropped at {d.Plan.To + 1} of {d.Plan.Count}");
        }
        _settling = true;
        d.Btn.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(DragOrder.SettleMs), Components = Vector3TransitionComponents.X };
        d.Btn.Translation = new Vector3((float)d.Plan.DropOffset, 0, 0);
        Timer(ref _dragSettle, DragOrder.SettleMs, () => DragCommit(d));
    }

    /// The glide is over: every offset comes off in the same tick the cells move, so nothing on screen moves; the
    /// group's order goes to the host. A push that came mid-drag is applied now (bar.js dragCommit).
    void DragCommit(DragState d)
    {
        _settling = false;
        foreach (var b in d.Btns) { b.TranslationTransition = null; b.Translation = Vector3.Zero; }
        LiftLook(d, false);
        if (Dock.ContainerFromItem(d.Vm) is UIElement cont) Canvas.SetZIndex(cont, 0);
        int from = d.Plan.From, to = d.Plan.To;
        // quiet until the pointer leaves the icon it is on now; that icon opens no flyout until then
        _dragQuietCell = d.Vm;
        if (to != from) {
            var ids = DragOrder.Move(d.Group.Select(c => c.Item.Id).ToList(), from, to);
            _orderHold.Want(d.Kind, ids, Environment.TickCount64);
            SyncCells();
            OrderSaveFromBar(d.Kind, ids, from, to);
        } else if (_dragPending) SyncCells();
    }

    void OrderSaveFromBar(string kind, IReadOnlyList<string> ids, int from, int to)
    {
        if (_tasks == null) return;
        _tasks.Order(kind, ids, from, to, r => {
            if (r.Ok) { if (_chips.Clear("order")) RenderPip(force: true); return; }
            // spring back to the order the host has, and say why (bar.js orderSave; no app name: this reaches the log)
            _orderHold.Drop();
            string words = $"The {(kind == "running" ? "running" : "pinned")} apps' new order was not saved, so they went back: {r.Reason}";
            Host.Log.Line("bar tasks: drag: " + words);
            if (_chips.Fail("order", "order not saved", words, "warn")) RenderPip(force: true);
            Timer(ref _orderFailClear, 20000, () => { if (_chips.Clear("order")) RenderPip(force: true); });
            SyncCells();
        });
    }

    // ------------------------------------------------------------------ the quiet (bar.js dragQuietOn / Off)
    void DragQuietOn()
    {
        _dragQuiet = true;
        _dragQuietCell = null;
        foreach (var c in _cells) c.Quiet = true;
    }

    void DragQuietOff()
    {
        if (!_dragQuiet) return;
        _dragQuiet = false;
        _dragQuietCell = null;
        foreach (var c in _cells) c.Quiet = false;
    }

    /// From the preview dwell (FlyCellEnter): true = open nothing. During a drag nothing opens; after it, the icon it
    /// was dropped on stays quiet until the pointer reaches another one.
    bool DragQuietEnter(CellVm vm)
    {
        if (DragBusy) return true;
        if (!_dragQuiet) return false;
        if (vm == _dragQuietCell) return true;
        DragQuietOff();
        return false;
    }

    /// While lifted, and for the usual 180 ms after the drop, the bar stays up wherever the pointer is (main.js holds
    /// the bar per `bar.hover {drag:true}` beat; here the frame's own poll asks this directly, no beat in between).
    void HoldBarWhileDragging()
    {
        if (_dragHoldWrapped) return;
        _dragHoldWrapped = true;
        var before = HoldsBar;
        HoldsBar = () => DragBusy || Environment.TickCount64 < _dragHoldUntil || before?.Invoke() == true;
    }
}
