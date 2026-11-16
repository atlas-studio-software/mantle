using System;
using System.Collections.Generic;
using System.Linq;
using Mantle.App.Shell.Popups;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Windows.Foundation;
using Windows.System;

namespace Mantle.App;

/// <summary>
/// The strip's fit and its "+N" button (ui/bar/bar.js measure / decide / applyOverflow / buildOvfPop / ovfTile;
/// BAR-056, BAR-235..238, the "+N" part of BAR-242). The rule is <see cref="StripFit"/> in Mantle.Core: centred while the
/// icons fit the free span (8 DIP inside the Start button and the right cluster), slid by the minimum that keeps them in
/// it, and only when they cannot fit the whole span do the last of them (the order's tail) wait behind "+N". Clicking it
/// opens a panel of exactly those apps (up to 4 columns of 76 DIP); a tile acts like its icon (click, middle-click, right-
/// click: the app menu), and an open panel is rebuilt in place only when what it lists changes.
/// Native, better than Electron: the judgement reads layout sizes, which the bar's reveal (a composition transform) never
/// changes, so it is made once per real change (the bar's width, the right cluster's, the app list) with no 50 ms settle
/// timer and no per-frame "follow"; nothing runs at rest.
/// UI Automation: the button <c>bar.apps.more</c> (Name = "N more apps that do not fit"), the panel <c>bar.ovf</c>, its
/// tiles <c>bar.ovf.&lt;n&gt;</c> (n = the app's place in the strip's order; Name = the app, ItemStatus = the icon's status).
/// </summary>
public sealed partial class BarWindow
{
    const string OvfKey = "ovf";
    int _keep = int.MaxValue;     // icons on the strip; MaxValue: all of them
    string _ovfSig = "", _fitSaid = "";
    bool _ovfDirty, _ovfHooked;
    UiaGrid? _ovfRoot;            // the open panel's body, rebuilt in place

    void HookOverflow()
    {
        Pill.SizeChanged += (_, _) => Judge();
        RightCluster.SizeChanged += (_, _) => Judge();
        StartBtn.SizeChanged += (_, _) => Judge();
        // a container made after the judgement (an app just added) takes its place on the next layout pass; only while
        // one is owed, so nothing runs at rest
        Dock.LayoutUpdated += (_, _) => { if (_ovfDirty) ApplyOverflow(); };
        DockMore.Click += (_, _) => OpenOverflow();
    }

    /// The icons on the strip now (all of them unless some wait behind "+N").
    internal int OnStripCount => Math.Min(_keep, _cells.Count);

    /// bar.js layoutDock + decide: placed in the room the bar has now, and the tail behind "+N" when it cannot fit.
    void Judge()
    {
        double barW = Pill.ActualWidth;
        if (barW <= 0 || StartBtn.ActualWidth <= 0) return;
        double leftEnd = StartBtn.TransformToVisual(Pill).TransformPoint(new Point(StartBtn.ActualWidth, 0)).X;
        double wall = RightCluster.ActualWidth > 0 ? RightCluster.TransformToVisual(Pill).TransformPoint(new Point(0, 0)).X : barW - 8;
        var room = StripFit.Room.Between(barW, leftEnd, wall);
        var div = _cells.Select(c => c.HasDivider).ToList();
        var fit = StripFit.Decide(div, room);
        double shift = Math.Round(fit.Shift);
        if (DockShift.X != shift) DockShift.X = shift;
        int keep = fit.Hidden > 0 ? fit.Keep : int.MaxValue;
        bool changed = keep != _keep;
        _keep = keep;
        string said = fit.Hidden > 0
            ? $"{fit.Keep} of {div.Count} on the strip, {fit.Hidden} behind \"+{fit.Hidden}\" (span {room.Span:0} DIP)"
            : $"all {div.Count} on the strip (span {room.Span:0} DIP, {(shift == 0 ? "centred" : $"slid {shift:+0;-0} DIP")})";
        if (said != _fitSaid) { _fitSaid = said; Host.Log.Line("bar tasks: fit: " + said); }
        if (changed || fit.Hidden > 0 || _ovfDirty || _ovfRoot != null) ApplyOverflow();
    }

    /// bar.js applyOverflow: the tail collapsed, "+N" says how many, an open panel follows what it lists.
    void ApplyOverflow()
    {
        _ovfDirty = false;
        int keep = OnStripCount, hidden = _cells.Count - keep;
        for (int i = 0; i < _cells.Count; i++) {
            if (Dock.ContainerFromIndex(i) is not UIElement cont) { _ovfDirty = true; continue; }
            var vis = i < keep ? Visibility.Visible : Visibility.Collapsed;
            if (cont.Visibility != vis) cont.Visibility = vis;
        }
        var btnVis = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DockMore.Visibility != btnVis) DockMore.Visibility = btnVis;
        if (hidden > 0) {
            SetMoreLabel(StripFit.Label(hidden));   // its tooltip, name and count disc: PaintMore below (BarWindow.OverflowBadge.cs)
        }
        string sig = StripFit.Sig(_cells.Select(c => c.Item).ToList(), keep);
        if (Popups?.OpenKey == OvfKey) {
            if (hidden == 0) Popups.Close("nothing waits behind +N any more");
            else if (sig != _ovfSig && _ovfRoot != null) {
                FillOverflow(_ovfRoot);
                var (w, h) = StripFit.PanelSize(hidden);
                Popups.Resize(OvfKey, h, w);
                Host.Log.Line($"bar tasks: overflow: the panel was rebuilt ({hidden} app(s))");
            }
        }
        _ovfSig = sig;
        PaintMore();
    }

    /// The "+N" button: opens the panel, or closes it on a second click (bar.js openPop('ovf')).
    void OpenOverflow()
    {
        if (Popups == null) return;
        if (!_ovfHooked) {
            _ovfHooked = true;
            Popups.Anchor(DockMore);
            Popups.Closed += (key, _) => { if (key != OvfKey) return; _ovfRoot = null; _ovfTiles.Clear(); Popups.GiveBackKeyboard("the panel closed"); };
        }
        int hidden = _cells.Count - OnStripCount;
        if (hidden <= 0) return;
        var (w, h) = StripFit.PanelSize(hidden);
        _popAnchor = DockMore;
        Popups.Toggle(new PopupKind(OvfKey, "Apps that do not fit", w, h, Focusable: false, Instant: false, Away: true, () => {
            var root = new UiaGrid { Padding = new Thickness(StripFit.PanelPad) };
            AutomationProperties.SetAutomationId(root, "bar.ovf");
            AutomationProperties.SetName(root, "Apps that do not fit");
            // Esc closes it whenever the panel has the keyboard (BAR-238)
            root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => {
                if (e.Key == VirtualKey.Escape) { e.Handled = true; Popups?.Close("Esc"); }
            }), true);
            FillOverflow(root);
            _ovfRoot = root;
            return root;
        }), DockMore);
        if (Popups.OpenKey == OvfKey) {
            Host.Log.Line($"bar tasks: overflow: the panel opened with {hidden} app(s)");
            OvfKeyboard(0);
        }
    }

    /// bar.js buildOvfPop: one tile per app behind "+N", in the strip's order, up to 4 across.
    void FillOverflow(UiaGrid root)
    {
        root.Children.Clear();
        _ovfTiles.Clear();
        int keep = OnStripCount, hidden = _cells.Count - keep, cols = StripFit.Columns(hidden);
        var grid = new Grid { ColumnSpacing = StripFit.TileGap, RowSpacing = StripFit.TileGap };
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(StripFit.TileW) });
        for (int r = 0; r < (hidden + cols - 1) / cols; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(StripFit.TileH) });
        for (int i = keep; i < _cells.Count; i++) {
            var t = OvfTile(_cells[i]);
            Grid.SetColumn(t, (i - keep) % cols);
            Grid.SetRow(t, (i - keep) / cols);
            grid.Children.Add(t);
        }
        root.Children.Add(grid);
        PaintTiles();
    }

    PointerEventHandler? _ovfMiddle;

    /// bar.js ovfTile: the icon (or its letter tile), the name, the running line; it acts like the icon on the strip.
    Button OvfTile(CellVm vm)
    {
        var b = (Button)XamlReader.Load(
            "<Button " + JlNs + " HorizontalAlignment='Stretch' VerticalAlignment='Stretch' HorizontalContentAlignment='Center' Padding='4,8,4,5' IsTabStop='True' UseSystemFocusVisuals='False'" +
            " Foreground='{ThemeResource LgInk2}'><Button.Template>" + JlRowTemplate + "</Button.Template>" +
            "<StackPanel Spacing='4' HorizontalAlignment='Center'>" +
            "<Grid HorizontalAlignment='Center'><Grid Width='24' Height='24' CornerRadius='6'>" +
            "<Border Background='{ThemeResource LgTile}'><TextBlock FontSize='12.5' FontWeight='SemiBold' Foreground='{ThemeResource LgInk}' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<Image Width='24' Height='24'/></Grid>" + TileDisc("LgAcc") + TileDisc("LgWarn") + "</Grid>" +
            "<TextBlock FontSize='11.5' MaxWidth='68' TextTrimming='CharacterEllipsis' HorizontalAlignment='Center' FontFamily='{ThemeResource LgFont}'/>" +
            "<Grid Height='3' Width='16'>" +
            "<Border Width='6' Height='3' CornerRadius='1.5' Background='{ThemeResource LgInk2}' HorizontalAlignment='Center'/>" +
            "<Border Width='16' Height='3' CornerRadius='1.5' Background='{ThemeResource LgAcc}' HorizontalAlignment='Center'/></Grid>" +
            "</StackPanel></Button>");
        var sp = (StackPanel)b.Content;
        var wrap = (Grid)sp.Children[0];   // the icon and its count disc (helmbadge.js paintTile's .hb-wrap)
        var icoBox = (Grid)wrap.Children[0];
        _ovfTiles.Add((vm, b, (Border)wrap.Children[1], (Border)wrap.Children[2]));
        var tile = (Border)icoBox.Children[0];
        var img = (Image)icoBox.Children[1];
        ((TextBlock)tile.Child).Text = vm.Letter;
        // the same decoded icon as the strip's (bar.js clones the strip's)
        if (vm.Icon != null) { img.Source = vm.Icon; tile.Visibility = Visibility.Collapsed; } else img.Visibility = Visibility.Collapsed;
        ((TextBlock)sp.Children[1]).Text = vm.Name;
        var ind = (Grid)sp.Children[2];
        ind.Children[0].Visibility = vm.RunVisibility;
        ind.Children[1].Visibility = vm.ActVisibility;
        AutomationProperties.SetAutomationId(b, "bar.ovf." + vm.Index);
        AutomationProperties.SetName(b, vm.Name);
        AutomationProperties.SetItemStatus(b, vm.Status);
        ToolTipService.SetToolTip(b, vm.Tip ?? vm.Name);
        b.Tag = vm;
        b.Click += (_, _) => {
            if (Host.Args.Test) Host.Log.Line($"bar tasks: test: click on {"bar.ovf." + vm.Index} ({vm.Title})");
            Popups?.Close("an app in the panel was opened");
            OpenCell(vm, fresh: false, DockMore);   // 2+ windows: the previews over "+N" (BAR-237)
        };
        _ovfMiddle ??= new PointerEventHandler((o, e) => {
            if (o is not Button t || t.Tag is not CellVm v) return;
            if (e.GetCurrentPoint(t).Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased) return;
            e.Handled = true;
            if (Host.Args.Test) Host.Log.Line($"bar tasks: test: middle-click on {"bar.ovf." + v.Index} ({v.Title})");
            Popups?.Close("a middle-click on an app in the panel");
            OpenCell(v, fresh: true);
        });
        b.AddHandler(UIElement.PointerReleasedEvent, _ovfMiddle, true);
        b.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler((_, e) => {
            e.Handled = true;
            if (Host.Args.Test) Host.Log.Line($"bar tasks: test: right-click on {"bar.ovf." + vm.Index} ({vm.Title})");
            try { OpenJump(vm, DockMore); }   // the app menu, over "+N", in place of the panel
            catch (Exception ex) { Host.Log.Line($"bar tasks: menu: the app menu could not open ({ex.GetType().Name}: {ex.Message})"); }
        }), true);
        return b;
    }

    /// The panel asks for the keyboard like the app menu (Esc, Tab between tiles, Enter / Space opens one): never in a
    /// test instance unless MANTLE_TEST_JL_KEYS=1. A refusal is said in the log and the panel's help text.
    void OvfKeyboard(int tries)
    {
        if (Popups == null || Popups.OpenKey != OvfKey || _ovfRoot == null) return;
        bool allowed = !Host.Args.Test || (Host.Env(JlKeysEnv) == "1" && !Host.Quiet);
        var (ok, words) = allowed ? Popups.TakeKeyboard(OvfKey)
            : (false, "a test instance never takes the keyboard from whoever is at the machine");
        if (!ok && words == "the pop-up is not open" && tries < 20) {
            var t = DispatcherQueue.CreateTimer(); t.Interval = TimeSpan.FromMilliseconds(50); t.IsRepeating = false;
            t.Tick += (_, _) => OvfKeyboard(tries + 1); t.Start();
            return;
        }
        if (ok) {
            if (FirstTile(_ovfRoot) is Button first) first.Focus(FocusState.Keyboard);
            Host.Log.Line("bar tasks: overflow: the panel has the keyboard");
        } else {
            AutomationProperties.SetHelpText(_ovfRoot, "Keys do not reach this panel: " + words.Split(", so ")[0]);
            Host.Log.Line("bar tasks: overflow: keys do not reach the panel: " + words);
        }
    }

    static Button? FirstTile(UiaGrid root) =>
        root.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<Button>().FirstOrDefault();
}
