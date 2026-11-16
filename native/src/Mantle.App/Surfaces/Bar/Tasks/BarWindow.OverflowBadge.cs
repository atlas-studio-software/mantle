using System.Collections.Generic;
using Mantle.Core.Features.Badges;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Mantle.App;

/// <summary>
/// The counts behind "+N" (ui/bar/helmbadge.js paintMore / paintTile; BAR-257, BAR-1030; lane native-r7-strip-3). The
/// "+N" button carries the sum of the hidden apps' counts in the same disc as a strip icon's (accent, warn when any
/// stopped on an error; none while "+N" is hidden), and its tooltip and accessible name get "Needs you: &lt;app&gt; &lt;n&gt;, ...";
/// each tile of the open panel carries its own app's disc, tooltip and status. The rule is Mantle.Core's OverflowBadge;
/// the counts are the Helm badge's (Surfaces/Bar/Badges, CellVm.Badge.cs). Painted only when the strip's fit or a count
/// changes (a count change is coalesced into one paint): no timer, nothing at rest.
/// UI Automation: the "+N" disc's text <c>bar.apps.more.count</c>, a tile's <c>bar.ovf.count</c>.
/// </summary>
public sealed partial class BarWindow
{
    Grid? _moreBox;
    TextBlock? _moreLabel;
    Border? _moreAcc, _moreWarn;
    string _moreSaid = "";
    bool _morePaintOwed;
    readonly List<(CellVm Vm, Button Tile, Border Acc, Border Warn)> _ovfTiles = new();

    /// The same disc as a strip cell's (BarWindow.xaml, media4), at the 24 DIP icon box's top-right corner.
    static string Disc(string bg, string id, string margin) =>
        "<Border MinWidth='15' Height='15' CornerRadius='7.5' Margin='" + margin + "' HorizontalAlignment='Right' VerticalAlignment='Top' IsHitTestVisible='False' Visibility='Collapsed'" +
        " Background='{ThemeResource " + bg + "}' BorderBrush='{ThemeResource LgFillStrong}' BorderThickness='1.5'>" +
        "<TextBlock AutomationProperties.AutomationId='" + id + "' FontSize='10' FontWeight='SemiBold' Foreground='{ThemeResource LgAccOn}' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>";

    /// helmbadge.css .ovf-app .hb-wrap .hb-badge { top: -5px; right: -7px; }
    static string TileDisc(string bg) => Disc(bg, "bar.ovf.count", "0,-5,-7,0");

    /// "+N"'s face: its label and the count disc over a 24 DIP box, like a strip icon's.
    void SetMoreLabel(string label)
    {
        if (_moreBox == null) {
            _moreBox = (Grid)XamlReader.Load("<Grid " + JlNs + " Width='24' Height='24'><TextBlock HorizontalAlignment='Center' VerticalAlignment='Center'/>" +
                Disc("LgAcc", "bar.apps.more.count", "0,-2,-8,0") + Disc("LgWarn", "bar.apps.more.count", "0,-2,-8,0") + "</Grid>");
            _moreLabel = (TextBlock)_moreBox.Children[0];
            _moreAcc = (Border)_moreBox.Children[1];
            _moreWarn = (Border)_moreBox.Children[2];
            DockMore.Content = _moreBox;
        }
        if (_moreLabel!.Text != label) _moreLabel.Text = label;
    }

    static void ShowDisc(Border acc, Border warn, int n, bool isWarn)
    {
        string text = HelmBadge.DiscText(n);
        foreach (var (d, on) in new[] { (acc, n > 0 && !isWarn), (warn, n > 0 && isWarn) }) {
            var vis = on ? Visibility.Visible : Visibility.Collapsed;
            if (d.Visibility != vis) d.Visibility = vis;
            if (!on) continue;
            var t = (TextBlock)d.Child;
            if (t.Text != text) t.Text = text;
            var pad = text.Length > 1 ? new Thickness(4, 0, 4, 0) : new Thickness(0);
            if (!d.Padding.Equals(pad)) d.Padding = pad;
        }
    }

    /// A cell's count changed (CellVm.BadgeMoved): one paint once the push has set every cell and its status.
    void MoreBadgeChanged()
    {
        if (_morePaintOwed) return;
        _morePaintOwed = true;
        DispatcherQueue.TryEnqueue(() => { _morePaintOwed = false; PaintMore(); });
    }

    /// paintMore: the sum, the warn, the tooltip line and the name; then the open panel's tiles.
    void PaintMore()
    {
        int keep = OnStripCount, hidden = _cells.Count - keep;
        bool shown = hidden > 0 && DockMore.Visibility == Visibility.Visible;
        var behind = new List<OverflowBadge.Hidden>();
        for (int i = keep; i < _cells.Count; i++) behind.Add(new(_cells[i].Item.Name, _cells[i].BadgeCount, _cells[i].BadgeWarn));
        var (n, warn, line) = OverflowBadge.Sum(behind, shown);
        if (_moreAcc != null) ShowDisc(_moreAcc, _moreWarn!, n, warn);
        if (shown) {
            string tip = StripFit.Tip(hidden) + line, name = OverflowBadge.Spoken(tip);
            if (AutomationProperties.GetName(DockMore) != name) { AutomationProperties.SetName(DockMore, name); ToolTipService.SetToolTip(DockMore, tip); }
        }
        string said = !shown ? "" : n > 0 ? $"\"+{hidden}\" carries the count {n}{(warn ? " (warn)" : "")}" : $"\"+{hidden}\" carries no count";
        if (said != _moreSaid) { _moreSaid = said; if (said.Length > 0) Host.Log.Line("bar tasks: overflow: " + said); }
        PaintTiles();
    }

    /// paintTile: each open tile gets its app's disc, tooltip and status.
    void PaintTiles()
    {
        foreach (var (vm, tile, acc, warn) in _ovfTiles) {
            ShowDisc(acc, warn, vm.BadgeCount, vm.BadgeWarn);
            string tip = vm.Tip ?? vm.Name;
            if (ToolTipService.GetToolTip(tile) as string != tip) ToolTipService.SetToolTip(tile, tip);
            if (AutomationProperties.GetItemStatus(tile) != vm.Status) AutomationProperties.SetItemStatus(tile, vm.Status);
        }
    }
}
