using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Bar.QuickSettings;

/// <summary>
/// Lane r15-quick-settings: the sparse layout of the prototype (apps-r3/mantle-bar panels/quick.js, the default since
/// 2026-11-24; its ?qs=full is the Electron look here). The main view keeps the tiles, the two sliders and the footer;
/// under a hair, ONE row "More settings" goes one level down to a view with a back arrow and the title "More settings"
/// that holds power mode, game mode and hot corners (no footer there). Native has no top bar row in quick settings, so
/// the row's sub line names the three it holds. The islands look only: the Electron look keeps every row in the main
/// view. Ids: qs.more.row (Name "More settings", HelpText its sub line), qs.view.more-settings (the view), qs.more.back,
/// qs.more.title; the rows keep their own ids (qs.powermode*, qs.gamemode*, qs.corners*).
/// </summary>
internal sealed partial class QsPanel
{
    const string MoreName = "More settings", MoreSub = "Power mode, game mode, hot corners";
    Grid? _sys;
    StackPanel? _sysStack;
    Button? _moreRow, _sysBack;
    TextBlock? _moreName, _moreSubT, _sysTitle;
    Canvas? _moreChev;
    Border? _moreHair;

    partial void BuildMoreSettings(double pad, double gap)
    {
        // the rows go one level down, in quick.js sysHTML's order: power mode, game mode, (top bar,) hot corners
        _sysStack = new StackPanel { Padding = new Thickness(pad), Spacing = gap };
        foreach (var row in new FrameworkElement[] { _pmRow, _gm.Root, _corners.Root }) {
            if (!_stack.Children.Contains(row)) continue;   // MANTLE_TEST_QS_NO_GAMEMODE leaves two out (the Electron pair only)
            _stack.Children.Remove(row);
            row.Margin = new Thickness(0);   // the view's 12 gap stands in for the rows' own 2 / 9
            _sysStack.Children.Add(row);
        }

        // the main view: a hair, then the row (.qs-hair, .qs-gorow: 40 tall, 12 left 8 right, r-m; its name over what
        // is in there, the "›" on the right in text-2)
        _moreHair = new Border { Height = 1 };
        _stack.Children.Add(_moreHair);
        _moreRow = Btn("qs.more.row", MoreName, double.NaN, L("LvQsGoRowH", 40));
        _moreRow.HorizontalAlignment = HorizontalAlignment.Stretch;
        _moreRow.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _moreRow.Padding = LT("LvQsGoRowPad", new Thickness(12, 0, 8, 0));
        _moreRow.CornerRadius = new CornerRadius(L("LvQsRCtl", 8));
        var g = new Grid { ColumnSpacing = L("LvQsGoRowGap", 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _moreName = Text(MoreName, 13);
        _moreSubT = Text(MoreSub, 11);
        _moreName.TextTrimming = TextTrimming.CharacterEllipsis; _moreSubT.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(_moreName); words.Children.Add(_moreSubT);
        _moreChev = new Canvas { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_moreChev, 1);
        g.Children.Add(words); g.Children.Add(_moreChev);
        _moreRow.Content = g;
        AutomationProperties.SetHelpText(_moreRow, MoreSub);
        ToolTipService.SetToolTip(_moreRow, MoreSub);
        _moreRow.Click += (_, _) => Enter("more");
        _stack.Children.Add(_moreRow);

        // the view one level down (.qs-shead: 40 tall, padding 0 8, the back button 32; .qs-stitle 14 / 600)
        _sys = new Grid { Visibility = Visibility.Collapsed };
        _sys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _sys.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Uia(_sys, "qs.view.more-settings", MoreName);
        var head = new Grid { Height = L("LvQsHeadH", 40), Padding = new Thickness(L("LvQsHeadPadX", 8), 0, L("LvQsHeadPadX", 8), 0), ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        double bs = L("LvQsBackSize", 32);
        _sysBack = Btn("qs.more.back", "Back to quick settings", bs, bs);
        _sysBack.Content = new Canvas { Width = 16, Height = 16 };
        ToolTipService.SetToolTip(_sysBack, "Back to quick settings");
        _sysBack.Click += (_, _) => Back("the back arrow");
        _sysTitle = Text(MoreName, L("LvQsTitleFs", 14));
        _sysTitle.FontWeight = FontWeights.SemiBold;
        _sysTitle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_sysTitle, "qs.more.title");
        Grid.SetColumn(_sysTitle, 1);
        head.Children.Add(_sysBack); head.Children.Add(_sysTitle);
        _sys.Children.Add(head);
        var scroll = new ScrollViewer { Content = _sysStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        _sys.Children.Add(scroll);
        Root.Children.Add(_sys);
    }

    /// Enter("more"): the main view sinks, the more-settings view lifts; the box keeps its size.
    void EnterMore()
    {
        if (_sys == null) { _view = null; _log("qs: the More settings view did not open: this look keeps every row in the main view"); return; }
        Swap(MainView, _sys);
        Paint();
        _log("qs: the More settings view opened");
    }

    /// Back from the more-settings view (the back arrow, or the pop-up closing).
    void BackMore(string why, bool closing)
    {
        _log($"qs: the More settings view closed ({why})");
        _view = null;
        if (_sys != null) Swap(_sys, MainView, closing);
        if (closing) return;
        Paint();
        Fit();
    }

    partial void PaintMoreSettings()
    {
        if (_moreRow == null || _moreName == null || _moreSubT == null || _moreChev == null) return;
        if (_moreHair != null) _moreHair.Background = B("LvBarHair");
        _moreRow.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Line(_moreName, L("LvQsLblFs", 13), L("LvQsLblLh", 18));
        Line(_moreSubT, L("LvQsSubFs", 11), L("LvQsSubLh", 15));
        _moreName.Foreground = B("LgInk");
        _moreSubT.Foreground = B("LgInk2");
        Fill(_moreChev, ChevR, "LgInk2");
        if (_sysTitle != null) _sysTitle.Foreground = B("LgInk");
        if (_sysBack?.Content is Canvas c) Fill(c, ChevL, "LgInk");
    }
}
