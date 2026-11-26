using System;
using Mantle.App.Shell.Levitate;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.App.Surfaces.Osd;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App;

/// <summary>
/// Lane r13-bar-islands: the bar as THREE floating islands (apps-r3/mantle-bar look.css "the bar: three islands", shell.js
/// fitDock; the numbers are Mantle.Core <see cref="BarIslands"/>): left (the M), the dock, right (the status cluster), each
/// 44 DIP tall at radius 12, the band 12 DIP from the screen's edges. Every run but MANTLE_TEST_GLASS=electron
/// (<see cref="BarGeometry.Islands"/>); there the pill stays exactly as before and nothing here runs.
/// <list type="bullet">
/// <item>The pill's own layers (Glass, Tint, Lip, Edge) are collapsed: no hairline, lip, tint or sheen at rest. Each
/// island wears a <see cref="LevitatePanel"/> (frost by size, dim, the v2.1 dark film or, in light, the lift toward #F4F7F8; the sheen on hover
/// only), placed from the geometry on every layout, geometry or theme change.</item>
/// <item>One float shadow bitmap (<see cref="LevitateShadow"/>) round the three boxes, in ShadowHost, in both themes.</item>
/// <item>The dock is centred on the screen; when its right edge + 12 would pass the right island, it sits 12 after the M
/// (<see cref="BarIslands.Layout"/>). The island is placed by its layout margin (the frost lines up from the layout).</item>
/// <item>Hover / pressed fills: the Levitate inks in dark (white .07 / .05), today's Lg tokens in light, given to the
/// bar's buttons through Root's resources (the LgBtn template's LgHover / LgPress).</item>
/// </list>
/// </summary>
public sealed partial class BarWindow
{
    LevitatePanel? _lvLeft, _lvDock, _lvRight;
    readonly SolidColorBrush _isHover = new(), _isPress = new(), _isAccent = new();
    string _islandShadowKey = "", _islandsSaid = "";
    Image? _islandShadow;   // the float shadow bitmap's one Image in ShadowHost (IslandShadow)

    /// The islands' sizes and layers, once, from the constructor (before the first layout).
    void SetUpIslands()
    {
        if (!BarGeometry.Islands) return;
        foreach (var layer in new UIElement[] { Glass, Tint, Lip, Edge }) layer.Visibility = Visibility.Collapsed;
        // the pill's 0 8 padding is the electron face's: the band itself is 12 DIP from the screen's edges, so the left and
        // right islands sit flush on its ends (named, not found through LeftIsland.Parent: the first cloud run still drew
        // them 8 DIP in, the band 1880 wide on a 1920 screen)
        PillContent.Padding = new Thickness(0);
        Pill.Height = BarIslands.BarH;
        // the LgBtn template's hover / pressed fills resolve from here first (lightweight styling)
        Root.Resources["LgHover"] = _isHover;
        Root.Resources["LgPress"] = _isPress;

        // left: 44 x 44 round a 36 start button (its 22 mark is the XAML's Viewbox)
        LeftIsland.Width = BarIslands.LeftW;
        LeftIsland.Height = BarIslands.BarH;
        StartBtn.Width = BarIslands.StartSize;
        StartBtn.Height = BarIslands.StartSize;
        StartBtn.HorizontalAlignment = HorizontalAlignment.Center;
        StartBtn.CornerRadius = new CornerRadius(BarIslands.CellRadius);

        // the dock: padding 0 4, gap 2; placed by its left margin (LayoutIslands)
        DockIsland.HorizontalAlignment = HorizontalAlignment.Left;
        DockIsland.Height = BarIslands.BarH;
        DockRow.HorizontalAlignment = HorizontalAlignment.Left;
        DockRow.Margin = new Thickness(BarIslands.DockPadX, 0, BarIslands.DockPadX, 0);
        DockRow.Spacing = BarIslands.DockGap;
        DockMore.Width = BarIslands.CellW;
        DockMore.Height = BarIslands.CellH;
        DockMore.CornerRadius = new CornerRadius(BarIslands.CellRadius);

        // right: padding 0 4, gap 2, glyphs 32 x 36 at radius 8, the separator 1 x 20 margin 0 4, the clock padding 0 8
        RightIsland.Height = BarIslands.BarH;
        RightIsland.HorizontalAlignment = HorizontalAlignment.Right;
        RightCluster.Margin = new Thickness(BarIslands.RightPadX, 0, BarIslands.RightPadX, 0);
        RightCluster.Spacing = BarIslands.RightGap;
        RightSep.Height = BarIslands.SepH;
        RightSep.Margin = new Thickness(BarIslands.SepMarginX, 0, BarIslands.SepMarginX, 0);
        IslandSep();
        foreach (var b in new[] { DownloadsBtn, VolumeBtn, NetworkBtn, NotifsBtn, MoreBtn }) {
            b.Width = BarIslands.GlyphW;
            b.CornerRadius = new CornerRadius(BarIslands.GlyphRadius);
        }
        if (MoreBtn.Content is Grid more) more.Width = BarIslands.GlyphW;
        // the pip on More (barhide.js #more .bh-pip): a 6 px dot at top 8 / right 8, no ring; its ink is IslandPipInk
        MorePip.Width = MorePip.Height = 6;
        MorePip.Margin = new Thickness(0, 8, 8, 0);
        MorePipRing.Visibility = Visibility.Collapsed;
        IslandBattery();
        // the clock (look.css .clock): padding 0 8, as wide as its text (no min-width), time over date, both on the right
        // (the XAML's StackPanel and TextBlocks are right-aligned; the button sizes to them)
        ClockBtn.Padding = new Thickness(BarIslands.ClockPadX, 0, BarIslands.ClockPadX, 0);
        ClockBtn.MinWidth = 0;
        ClockT.MinWidth = 0;
        ClockBtn.CornerRadius = new CornerRadius(BarIslands.GlyphRadius);
        ClockT.FontSize = BarIslands.ClockTime;
        ClockT.FontWeight = FontWeights.Medium;
        ClockD.FontSize = BarIslands.ClockDate;

        _lvLeft = IslandPanel("bar left island", LeftIsland);
        _lvDock = IslandPanel("bar dock island", DockIsland);
        _lvRight = IslandPanel("bar right island", RightIsland);
        Pill.SizeChanged += (_, _) => LayoutIslands("the band was sized");
        DockRow.SizeChanged += (_, _) => LayoutIslands("the dock changed size");
        RightCluster.SizeChanged += (_, _) => LayoutIslands("the status island changed size");
        PaintIslandInks();
        Host.Log.Line("bar look: three islands (lane r13-bar-islands); MANTLE_TEST_GLASS=electron keeps the pill");
    }

    /// The battery button in the status island: a 32 x 36 glyph button between network and clock (shell.js #battery,
    /// look.css .glyph), opening quick settings like volume and network. With no battery it is Collapsed: it takes no room
    /// and is not in UIA (lane r14-bar-review: the empty box left a gap between network and the clock). Electron's
    /// hidden-but-flex 14 px slot is the electron face's alone (BarWindow.xaml). The glyph is the prototype's (data.js
    /// `battery`, its 24 box drawn in 16). The level is not read yet.
    void IslandBattery()
    {
        BatteryBtn.Width = BarIslands.GlyphW;
        BatteryBtn.CornerRadius = new CornerRadius(BarIslands.GlyphRadius);
        var (has, why) = IslandPower.HasBattery();
        BatteryBtn.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (has) {
            var host = new Canvas { Width = BarIslands.GlyphIcon, Height = BarIslands.GlyphIcon };
            host.Children.Add(PathEl(RoundRect(2, 5, 10.67, 6, 1.47), false, 1.2));
            foreach (var d in new[] { "M14 7v2", "M4 7v2", "M6 7v2", "M8 7v2" }) host.Children.Add(PathEl(d, false, 1.2));
            BatteryBtn.Content = host;
        } else BatteryBtn.Content = null;
        Host.Log.Line($"bar battery: {(has ? "the glyph is drawn" : "none (" + why + "); the button is collapsed and takes no room")}");
    }

    /// The status island's divider (shell.js: .b-sep after the now-playing chip group) shows only when something sits
    /// before it: now playing, or the tray strip (Electron's #tray, then .sep). With neither, no lone divider. Called
    /// from the start and whenever either one shows or hides; the electron face keeps its divider always.
    void IslandSep()
    {
        if (!BarGeometry.Islands) return;
        bool before = _media?.Visibility == Visibility.Visible || _tray?.Visibility == Visibility.Visible;
        var v = before ? Visibility.Visible : Visibility.Collapsed;
        if (RightSep.Visibility != v) RightSep.Visibility = v;
    }

    /// The islands look's ink for a bar glyph drawn from an Lg key: the design's icon ink (LvBarInk, look.css
    /// .btn { color: var(--text) }) in the islands look, the key itself on the electron face.
    static string IslandIconKey(string lg) => BarGeometry.Islands ? "LvBarInk" : lg;

    /// The bell's ink: the icon ink in the islands look, also when notifications cannot be read (the More pip and the
    /// bell's tooltip say that; LgInk3 put the bell at 1.89:1); the electron face keeps LgInk3 / LgInk2.
    static string BellInk(bool bad) => BarGeometry.Islands ? "LvBarInk" : bad ? "LgInk3" : "LgInk2";

    /// The More pip's dot in the islands look: text-2 for a plain problem, the design's warn only for a "warn" one.
    Brush IslandPipInk(bool warn) => (Brush)ThemeToken(warn ? "LvBarWarn" : "LvBarInk2");

    /// One island's levitated layers under its content, the hover sheen from the pointer.
    static LevitatePanel IslandPanel(string name, Grid island)
    {
        var r = new CornerRadius(BarIslands.Radius);
        var lev = new LevitatePanel(name, r);
        island.Children.Insert(0, lev.Sheen);
        island.Children.Insert(0, lev.Film);
        island.Children.Insert(0, lev.Dim);
        island.Children.Insert(0, lev.Picture);   // Picture, Dim, Film, Sheen, then the island's own content
        island.CornerRadius = r;
        // clear but hit-testable (a brush with no colour), so the sheen follows the whole island
        island.Background = new SolidColorBrush();
        island.PointerEntered += (_, _) => lev.Hover(true);
        island.PointerExited += (_, _) => lev.Hover(false);
        return lev;
    }

    /// The islands placed for the screen and the dock's and status island's widths now (shell.js fitDock), their frost
    /// lined up and the shadow drawn. From the geometry (ApplyGeometry) and every size change; cheap when nothing moved.
    void LayoutIslands(string why)
    {
        if (!BarGeometry.Islands || _lvLeft == null || _lvDock == null || _lvRight == null) return;
        double s = _scale > 0 ? _scale : 1;
        double screenW = (_monitor.Right - _monitor.Left) / s, screenH = (_monitor.Bottom - _monitor.Top) / s;
        if (screenW <= 0 || screenH <= 0) return;
        var l = BarIslands.Layout(screenW, DockRow.ActualWidth, RightCluster.ActualWidth);
        if (Math.Abs(DockIsland.Margin.Left - l.Dock.X) > 0.01) DockIsland.Margin = new Thickness(l.Dock.X, 0, 0, 0);
        // the band's top-left on the monitor (DIP): the frost is lined up from the geometry, not from a render transform
        double bx = (_pillPx.Left - _monitor.Left) / s, by = (_pillPx.Top - _monitor.Top) / s;
        _lvLeft.Place(screenW, screenH, bx + l.Left.X, by + l.Left.Y, l.Left.W, l.Left.H, _dark);
        _lvDock.Place(screenW, screenH, bx + l.Dock.X, by + l.Dock.Y, l.Dock.W, l.Dock.H, _dark);
        _lvRight.Place(screenW, screenH, bx + l.Right.X, by + l.Right.Y, l.Right.W, l.Right.H, _dark);
        IslandShadow(l);
        string said = FormattableString.Invariant($"left {l.Left.X:0.#}+{l.Left.W:0.#}, dock {l.Dock.X:0.#}+{l.Dock.W:0.#}{(l.DockLeft ? " (after the M: centred it would run into the status island)" : " (centred)")}, right {l.Right.X:0.#}+{l.Right.W:0.#} in a {_pillW:0.#} DIP band");
        if (said != _islandsSaid) { _islandsSaid = said; Host.Log.Line($"bar islands ({why}): {said}"); }
    }

    /// The float shadow round the three islands, one bitmap the size of the window's content, redrawn only when a box,
    /// the size or the scale changed.
    void IslandShadow(BarIslandsLayout l)
    {
        double s = _scale > 0 ? _scale : 1;
        double w = Root.Width, h = Root.Height;
        if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;
        double x0 = BarGeometry.SideNow, y0 = BarGeometry.ShadowTop, r = BarIslands.Radius;
        var boxes = new[] {
            new CssShadow.Box(x0 + l.Left.X, y0 + l.Left.Y, l.Left.W, l.Left.H, r),
            new CssShadow.Box(x0 + l.Dock.X, y0 + l.Dock.Y, l.Dock.W, l.Dock.H, r),
            new CssShadow.Box(x0 + l.Right.X, y0 + l.Right.Y, l.Right.W, l.Right.H, r),
        };
        int wp = (int)Math.Ceiling(w * s), hp = (int)Math.Ceiling(h * s);
        string key = FormattableString.Invariant($"{wp}x{hp}@{s}:{boxes[0]}:{boxes[1]}:{boxes[2]}");
        if (key == _islandShadowKey) return;
        _islandShadowKey = key;
        // One Image, made once and given each new bitmap in place. A new Image on every dock-width change (several
        // times while the dock fills at start-up) took a live automation peer out of the tree each time, under any
        // UI Automation client that had just listed it (its next read: UIA_E_ELEMENTNOTAVAILABLE, 0x80040201).
        if (_islandShadow == null) {
            _islandShadow = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            ShadowHost.Children.Clear();
            ShadowHost.Children.Add(_islandShadow);
        }
        _islandShadow.Source = LevitateShadow.Bitmap(wp, hp, s, boxes);
        _islandShadow.Width = wp / s;
        _islandShadow.Height = hp / s;
    }

    /// The theme changed: the inks, the frost's dim and film.
    void IslandsTheme()
    {
        PaintIslandInks();
        _lvLeft?.Theme(_dark);
        _lvDock?.Theme(_dark);
        _lvRight?.Theme(_dark);
        LayoutIslands("the theme changed");
    }

    /// Dark: the Levitate inks (Theme/Levitate.xaml); light: today's Lg tokens (as the levitated Downloads shelf).
    void PaintIslandInks()
    {
        _isHover.Color = IslandInk("LvCtlFillHover", "LgHover") is SolidColorBrush hb ? hb.Color : default;
        _isPress.Color = IslandInk("LvCtlFillPressed", "LgPress") is SolidColorBrush pb ? pb.Color : default;
        // the clock's time is the text ink, its date text-2 (look.css .clock .t / .d; light from LIGHT.md 4)
        ClockT.Foreground = (Brush)ThemeToken("LvBarInk");
        ClockD.Foreground = (Brush)ThemeToken("LvBarInk2");
        RightSep.Fill = IslandInk("LvHair", "LgEdgeOut");
        DockMore.Foreground = (Brush)ThemeToken("LvBarInk");
        // the dock's focus underline (look.css .dock-hl .u: var(--accent)), shared by every cell (IslandCell). A local
        // brush until Core has the islands accent profile (look-sync accent.js 'islands', base hue 185)
        _isAccent.Color = ThemeToken("LvBarAccent") is SolidColorBrush ab ? ab.Color : default;
    }

    Brush IslandInk(string lv, string lg)
    {
        if (_dark && Application.Current.Resources.TryGetValue(lv, out var v) && v is Brush b) return b;
        return (Brush)TokenOr(lg, "LgInk2");
    }

    /// A dock cell in the islands look (from Cell_Loaded): 40 x 36 at radius 8, a 20 icon, the run dot 4 x 3 two up,
    /// the separator 1 x 20 with margin 0 4 (plus the 2 gap after it), the dock's gap 2.
    void IslandCell(Button b)
    {
        if (!BarGeometry.Islands) return;
        b.Width = BarIslands.CellW;
        b.CornerRadius = new CornerRadius(BarIslands.CellRadius);
        if (Dock.ItemsPanelRoot is StackPanel panel && panel.Spacing != BarIslands.DockGap) panel.Spacing = BarIslands.DockGap;
        if (VisualTreeHelper.GetParent(b) is StackPanel row && row.Children.Count > 0 && row.Children[0] is Rectangle sep) {
            sep.Height = BarIslands.SepH;
            sep.Margin = new Thickness(BarIslands.SepMarginX, 0, BarIslands.SepMarginX + BarIslands.DockGap, 0);
        }
        if (b.Content is Grid face && face.Children.Count > 0 && face.Children[0] is StackPanel stack) {
            face.Height = BarIslands.CellH;
            stack.VerticalAlignment = VerticalAlignment.Bottom;
            stack.Margin = new Thickness(0, 0, 0, BarIslands.RunBottom);
            if (stack.Children.Count > 0 && stack.Children[0] is Grid icon) {
                icon.Width = BarIslands.CellIcon;
                icon.Height = BarIslands.CellIcon;
                foreach (var c in icon.Children)
                    if (c is Image im) { im.Width = BarIslands.CellIcon; im.Height = BarIslands.CellIcon; }
            }
            if (stack.Children.Count > 1 && stack.Children[1] is Grid ind && ind.Children.Count > 0 && ind.Children[0] is Border run)
                run.Width = BarIslands.RunW;
            if (stack.Children.Count > 1 && stack.Children[1] is Grid ind2 && ind2.Children.Count > 1 && ind2.Children[1] is Border act)
                act.Background = _isAccent;   // the focus underline in the islands accent (PaintIslandInks)
        }
    }

    /// The span the dock's cells may fill (DIP) in the islands look: from 12 after the M to 12 before the status island,
    /// less the dock's own padding, and less 8 because StripFit counts today's 36 + 6 cells (the islands' 40 + 2 is the
    /// same pitch, but the first cell, "+N" and the separator differ by up to 8).
    double IslandDockSpan()
    {
        double s = _scale > 0 ? _scale : 1;
        double screenW = (_monitor.Right - _monitor.Left) / s;
        return Math.Max(0, BarIslands.DockRoom(screenW, RightCluster.ActualWidth) - 2 * BarIslands.DockPadX - 8);
    }
}

/// Whether Windows reports a system battery (GetSystemPowerStatus; BatteryFlag 128 = none, 255 = unknown), for the
/// islands' battery glyph. Read once at start; never throws.
internal static class IslandPower
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public uint BatteryLifeTime, BatteryFullLifeTime; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    public static (bool Has, string Why) HasBattery()
    {
        try {
            if (!GetSystemPowerStatus(out var p)) return (false, "Windows did not say whether there is a battery");
            if ((p.BatteryFlag & 128) != 0) return (false, "this machine reports no battery");
            if (p.BatteryFlag == 255) return (false, "Windows does not know whether there is a battery");
            return (true, "ok");
        } catch (Exception e) { return (false, "the battery could not be read: " + e.GetType().Name); }
    }
}
