// GlassBox.cs - the see-through look (DesktopGlass.cs) inside a rounded box that layout places in a bigger window: a
// top bar dropdown (the bar's window grows to hold it), a desktop pane, the desktop menu's panel.
//
// The picture and the tint go INSIDE the box, under its body, reaching out under its hairline (a negative margin of the
// border's thickness), and the hairline is drawn again over them. Inside, so whatever the owner does to the box carries
// them: its opacity (a pane fading under a window), its motion (a dropdown sliding in), its size. The box's own fill is
// kept for the plain case (no still) and swapped for a clear brush while the picture is up, so the box still takes its
// clicks (a dropdown's padding must not fall through to the click-away layer under it).
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Shell.Glass;

internal sealed class GlassBox
{
    readonly Border _box, _pad, _edge;
    readonly Brush _clear = new SolidColorBrush();   // all-zero ARGB: draws nothing, still hit-tests
    Brush? _face;
    bool _picture;

    public GlassLayer Layer { get; }

    /// A box with a layer of its own (a desktop pane, the desktop menu): <paramref name="origin"/> null = the picture's own
    /// place in the window (GlassLayer.At).
    public static GlassBox Make(string name, IntPtr hwnd, Border box, Func<double, (double X, double Y)>? origin = null)
    {
        GlassBox? gb = null;
        GlassLayer? layer = null;
        layer = new GlassLayer(name, hwnd, box.CornerRadius, origin ?? GlassLayer.At(hwnd, () => layer!.Picture), plain => gb?.Plain(plain));
        gb = new GlassBox(box, layer);
        return gb;
    }

    /// Dress <paramref name="box"/> with <paramref name="layer"/> (taken out of the box it dressed before, if any). The box's
    /// child moves into an inner border that takes over the box's padding, so the layout is the same to the pixel.
    public GlassBox(Border box, GlassLayer layer)
    {
        _box = box; Layer = layer;
        var body = box.Child;
        box.Child = null;
        _pad = new Border { Child = body };
        _edge = new Border { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        foreach (var e in new FrameworkElement[] { layer.Picture, layer.Tint })
            if (e.Parent is Panel old) old.Children.Remove(e);
        var grid = new Grid();
        grid.Children.Add(layer.Picture);
        grid.Children.Add(layer.Tint);
        grid.Children.Add(_edge);
        grid.Children.Add(_pad);
        box.Child = grid;
        Sync();
        Plain(layer.State != "picture");   // a kept layer (a redrawn dropdown) is up already: no plain frame first
    }

    /// After the owner (re)set the box's padding, fill, hairline or corners (its theme paint): the padding moves to the
    /// inner border, the fill is kept for the plain case, the hairline and the corners are copied to the layers.
    public void Sync()
    {
        var p = _box.Padding;
        if (p.Left != 0 || p.Top != 0 || p.Right != 0 || p.Bottom != 0) { _pad.Padding = p; _box.Padding = new Thickness(0); }
        if (_box.Background is { } bg && !ReferenceEquals(bg, _clear)) { _face = bg; if (_picture) _box.Background = _clear; }
        var t = _box.BorderThickness;
        var under = new Thickness(-t.Left, -t.Top, -t.Right, -t.Bottom);
        Layer.Picture.Margin = Layer.Tint.Margin = _edge.Margin = under;
        _edge.BorderThickness = t;
        _edge.BorderBrush = _box.BorderBrush;
        _edge.CornerRadius = _box.CornerRadius;
        Layer.Radius(_box.CornerRadius);
    }

    /// The layer's picture went (true: the box's own fill and hairline again) or came up (false).
    public void Plain(bool plain)
    {
        _picture = !plain;
        _box.Background = plain ? _face : _clear;
        _edge.Visibility = plain ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// The top bar's dropdowns (drawn in its one window, which grows to hold them): each level's box gets the look. One
/// layer per level, kept while the menu stays open and handed to each new box of that level (a stat dropdown is drawn
/// again with each reading), so a redraw neither decodes nor logs anything; closing the menu puts them all down.
internal sealed class MenuGlass
{
    sealed class Slot { public GlassLayer Layer = null!; public GlassBox? Box; public bool Up, Used; }

    readonly string _name;
    readonly IntPtr _hwnd;
    readonly List<Slot> _slots = new();
    bool _dark;

    public MenuGlass(string name, IntPtr hwnd) { _name = name; _hwnd = hwnd; }

    /// A new dropdown box for <paramref name="level"/>, before it is laid out.
    public void Dress(int level, Border box)
    {
        while (_slots.Count <= level) {
            var s = new Slot();
            s.Layer = new GlassLayer(_name + " " + _slots.Count, _hwnd, box.CornerRadius, GlassLayer.At(_hwnd, () => s.Layer.Picture), plain => s.Box?.Plain(plain));
            _slots.Add(s);
        }
        var slot = _slots[level];
        slot.Box = new GlassBox(box, slot.Layer);
        slot.Used = true;
    }

    /// The owner's theme paint redid a box's fill or hairline: copied again.
    public void Sync() { foreach (var s in _slots) if (s.Used || s.Up) s.Box?.Sync(); }

    /// After the boxes are laid out: a new level comes up lined up where it rests, a kept one re-reads its place (said only
    /// when it moved), a level no longer drawn goes down.
    public void Placed(bool dark)
    {
        bool themed = dark != _dark;
        _dark = dark;
        foreach (var s in _slots) {
            if (s.Used && !s.Up) { s.Up = true; s.Layer.Up(dark); }
            else if (s.Used) { if (themed) s.Layer.Theme(dark); s.Layer.Moved("placed"); }
            else if (s.Up) { s.Up = false; s.Layer.Down(); s.Box = null; }
            s.Used = false;
        }
    }

    /// The window moved: each open level's picture follows.
    public void Moved() { foreach (var s in _slots) if (s.Up) s.Layer.Moved(); }

    /// The menu closed: nothing more until the next one opens.
    public void Down() { foreach (var s in _slots) { if (s.Up) s.Layer.Down(); s.Up = s.Used = false; s.Box = null; } }
}
