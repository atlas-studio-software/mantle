using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Mantle.Core.Features.PowerMode;
using Mantle.Core.Features.QuickSettings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Mantle.App.Surfaces.Bar.QuickSettings;

/// <summary>
/// Lane r15-quick-settings: quick settings in the islands look, to the approved prototype (apps-r3/mantle-bar
/// panels/quick.css and quick.js; light: apps-r3/light-mantle, LIGHT.md). The pop-up is levitated (the host draws the
/// island: PopupKind Levitate, BarWindow.QuickSettings.cs); this part gives the panel the prototype's sizes (Theme/
/// LvQuick.xaml through QsLook) and inks (the LvBar* twins of the Lg* names the rest of the panel uses, QsLook.Ink), a
/// 360 wide box as tall as the main view's own content (at least 240; the host caps it at the screen less 80) that every
/// view shares, and the views' sink and lift (look.css .view / .sunk / .below; quick fades under reduced motion).
/// The Electron look (MANTLE_TEST_GLASS=electron: the parity pairs) never runs any of it.
/// </summary>
internal sealed partial class QsPanel
{
    readonly bool _isl = QsLook.Islands;
    Grid? _mainView;
    FrameworkElement? _shown;
    Storyboard? _swapSb;
    FrameworkElement? _swapFrom;

    /// The main view: in the islands look its scroller over the footer, else the stack itself.
    FrameworkElement MainView => (FrameworkElement?)_mainView ?? _stack;

    double L(string key, double fallback) => QsLook.N(key, fallback, _log);
    Thickness LT(string key, Thickness fallback) => QsLook.T(key, fallback, _log);

    static string FaintText() => QsLook.Islands ? QsLook.N("LvQsFaint", 0.32).ToString("0.###", CultureInfo.InvariantCulture) : "0.28";

    bool _forcedOff;
    /// The bar's MANTLE_TEST_NET=off (BarWindow.Network.cs): the bar's network button is drawn offline whatever the
    /// network is, so the Wi-Fi tile says "Not connected" too (one bar lit, the rest faint) and the two never disagree.
    public bool ForcedOffline
    {
        get => _forcedOff;
        set { if (value && !_forcedOff) _log("qs: MANTLE_TEST_NET=off: the Wi-Fi tile is drawn on and not connected, as the bar's network button"); _forcedOff = value; }
    }

    /// The Wi-Fi part as the tile draws it: under the offline test switch a connected part reads not connected.
    JsonObject? WifiPart()
    {
        var w = Part("wifi");
        if (!_forcedOff || w == null || !QsParts.IsTrue(w["connected"])) return w;
        var c = (JsonObject)w.DeepClone();
        c["connected"] = false;
        return c;
    }

    // ------------------------------------------------------------------ build (sizes only; inks are painted)

    void BuildIslands()
    {
        _shown = _stack;
        if (!_isl) return;
        double pad = L("LvQsPad", 8), gap = L("LvQsGap", 12), rc = L("LvQsRCtl", 8), rs = L("LvQsRSmall", 4);
        _width = L("LvQsWidth", 360);
        Root.Width = _width;

        // the main view (quick.js mainHTML): .qs-scroll > .qs-in (padding 8, gap 12), then .qs-foot under it
        _stack.Padding = new Thickness(pad);
        _stack.Spacing = gap;
        _stack.Children.Remove(_foot);
        Root.Children.Remove(_stack);
        var scroll = new ScrollViewer { Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _mainView = new Grid();
        _mainView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _mainView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _mainView.Children.Add(scroll);
        Grid.SetRow(_foot, 1);
        _mainView.Children.Add(_foot);
        Root.Children.Insert(0, _mainView);
        _shown = _mainView;

        // tiles (.qs-grid / .qs-tile / .qs-split / .qs-chev)
        double tg = L("LvQsTileGap", 8), tileH = L("LvQsTileH", 48);
        _grid.ColumnSpacing = tg; _grid.RowSpacing = tg; _grid.Margin = new Thickness(0);
        foreach (var t in _tiles.Values) {
            t.B.Height = tileH;
            t.B.VerticalAlignment = VerticalAlignment.Top;
            t.B.Padding = LT("LvQsTilePad", new Thickness(12, 0, 8, 0));
            t.B.CornerRadius = new CornerRadius(rc);
            if (t.B.Content is Grid g) {
                g.ColumnDefinitions[0].Width = new GridLength(L("LvQsTileIco", 20));
                g.ColumnSpacing = L("LvQsTileIcoGap", 8);
                g.VerticalAlignment = VerticalAlignment.Center;
            }
            t.Lbl.FontWeight = FontWeights.Medium;
            t.Lbl.TextTrimming = TextTrimming.CharacterEllipsis;
            if (t.Chev == null) continue;
            t.B.CornerRadius = new CornerRadius(rc, rs, rs, rc);
            t.Chev.Width = L("LvQsChevW", 24);
            t.Chev.Height = tileH;
            t.Chev.VerticalAlignment = VerticalAlignment.Top;
            t.Chev.BorderThickness = new Thickness(0);
            t.Chev.CornerRadius = new CornerRadius(rs, rc, rc, rs);
            if (t.Chev.Parent is Grid split) split.ColumnSpacing = L("LvQsSplitGap", 2);
        }
        _xNotes.Margin = new Thickness(0);

        // sliders (.qs-row / .qs-head / .qs-line / .qs-range), mute and the sound "›" (.qs-ibtn)
        double lineH = L("LvQsLineH", 28), trackH = L("LvQsTrackH", 4), ibtn = L("LvQsIBtn", 28), lgap = L("LvQsLineGap", 4);
        foreach (var s in new[] { _bright, _vol }) {
            s.Wrap.Margin = new Thickness(0);
            s.Head.Padding = LT("LvQsHeadPad", new Thickness(4, 0, 4, 0));
            s.Name.FontWeight = FontWeights.Medium;
            s.Line.Height = lineH;
            s.Track.Height = trackH;
            s.Track.CornerRadius = new CornerRadius(trackH / 2);
            s.Track.BorderThickness = new Thickness(0);
            s.Track.Margin = new Thickness(0);
            s.Range.Style = IslRangeStyle();
            s.Range.Height = lineH;
            s.Range.Margin = new Thickness(0);
        }
        foreach (var b in new[] { _mute, _soundMore }) {
            b.Width = ibtn; b.Height = ibtn;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(lgap, 0, 0, 0);
            b.CornerRadius = new CornerRadius(rc);
        }

        // power mode (.qs-row / .qs-seg / .qs-segb)
        _pmHeadRow.Padding = LT("LvQsHeadPad", new Thickness(4, 0, 4, 0));
        _pmHead.FontWeight = FontWeights.Medium;
        _pmSeg.Height = L("LvQsSegH", 32);
        _pmSeg.Padding = new Thickness(L("LvQsSegPad", 2));
        _pmSeg.ColumnSpacing = 0;
        _pmSeg.CornerRadius = new CornerRadius(rc);
        foreach (var b in _pmBtns.Values) {
            b.Height = L("LvQsSegBtnH", 28);
            b.CornerRadius = new CornerRadius(L("LvQsRSeg", 6));
        }

        // the footer (.qs-foot: 40 tall, the hair on top, 16 left 8 right; .qs-power; the .link; the gear .qs-ibtn)
        _foot.Height = L("LvQsFootH", 40);
        _foot.Margin = new Thickness(0);
        _foot.Padding = LT("LvQsFootPad", new Thickness(16, 0, 8, 0));
        _foot.BorderThickness = new Thickness(0, 1, 0, 0);
        if (_foot.Child is Grid fg) {
            fg.VerticalAlignment = VerticalAlignment.Center;
            fg.ColumnSpacing = L("LvQsFootGap", 4);
            if (fg.Children.Count > 1 && fg.Children[1] is StackPanel acts) acts.Spacing = L("LvQsFootGap", 4);
        }
        _toWin.Height = 28;
        _toWin.Padding = new Thickness(12, 0, 12, 0);   // look.css .link: 28 tall, padding 0 12, r-m
        _toWin.CornerRadius = new CornerRadius(rc);
        _gear.Width = ibtn; _gear.Height = ibtn;
        _gear.CornerRadius = new CornerRadius(rc);

        // one level down (.qs-shead: 40 tall, padding 0 8; the back button 32; .qs-stitle 14 / 600), sharing the box
        _sub.Height = double.NaN;
        _sub.Margin = new Thickness(pad, 0, pad, pad);
        if (_sub.Children[0] is Grid head) {
            head.Height = L("LvQsHeadH", 40);
            head.Margin = new Thickness(0);
            head.ColumnSpacing = 8;
            if (head.Children[0] is Button back) { back.Width = L("LvQsBackSize", 32); back.Height = back.Width; }
        }
        _vTitle.FontSize = L("LvQsTitleFs", 14);
        BuildMoreSettings(pad, gap);
    }

    /// The more-settings view (sparse layout); nothing until the layout commit.
    partial void BuildMoreSettings(double pad, double gap);

    double IslandsNeed()
    {
        _width = L("LvQsWidth", 360);
        Root.Width = _width;
        // quick.js fit(): the main view's own height (measured alone: a view sinking beside it does not count), at
        // least 240; PopupLevitate.Place caps it at the monitor less 80
        MainView.Measure(new Windows.Foundation.Size(_width, double.PositiveInfinity));
        double h = Math.Ceiling(MainView.DesiredSize.Height);
        return h < 120 ? 460 : Math.Max(L("LvQsMinH", 240), h);
    }

    // ------------------------------------------------------------------ paint (after the Electron look's paint)

    static void Line(TextBlock t, double fs, double lh)
    {
        t.FontSize = fs;
        t.LineHeight = lh;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    void PaintIslands()
    {
        if (!_isl) return;
        double dead = L("LvQsDeadOpacity", 0.45);
        double lblFs = L("LvQsLblFs", 13), lblLh = L("LvQsLblLh", 18), subFs = L("LvQsSubFs", 11), subLh = L("LvQsSubLh", 15);
        double headFs = L("LvQsHeadFs", 13), valFs = L("LvQsValFs", 12), footFs = L("LvQsFootFs", 12);
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // tiles: fill-1 at rest, the accent when on (its second line accent-ink-2), never the old card / sunk faces
        foreach (var t in _tiles.Values) {
            if (t.View is not QsTileView v) continue;
            Line(t.Lbl, lblFs, lblLh); Line(t.Sub, subFs, subLh);
            t.B.Background = v.On ? B("LgAcc") : B("LgTile");
            t.Lbl.Foreground = v.On ? B("LgAccOn") : B("LgInk");
            t.Sub.Foreground = v.On ? B("LvQsOnSubInk") : B("LgInk2");
            t.Sub.Opacity = 1;
            t.Ico.Opacity = v.Dead ? dead : 1;
            t.Lbl.Opacity = v.Dead ? dead : 1;
            if (t.Chev != null) { t.Chev.Background = t.B.Background; t.Chev.BorderThickness = new Thickness(0); }
        }

        // sliders: the track fill-3, the part before the thumb the accent (text-3 while muted), the thumb the text ink
        var vol = Part("volume");
        bool muted = QsParts.IsTrue(vol?["muted"]);
        foreach (var s in new[] { _bright, _vol }) {
            Line(s.Name, headFs, 18); Line(s.Val, valFs, 18);
            s.Name.Foreground = B("LgInk"); s.Val.Foreground = B("LgInk2");
            s.Head.Margin = new Thickness(0, 0, 0, L("LvQsRowGap", 4));
            s.Track.Background = B("LvBarFillStrong");
            s.Range.Foreground = s == _vol && muted ? B("LvBarInk3") : B("LgAcc");
            s.Range.Background = B("LgInk");
            if (s.Wrap.Opacity < 1) s.Wrap.Opacity = dead;
        }
        double lvl = muted ? 0 : QsParts.Num(vol?["level"]) ?? 0;
        int waves = muted ? 0 : lvl <= 0.005 ? 0 : lvl <= 0.33 ? 1 : lvl <= 0.66 ? 2 : 3;
        Fill(_muteGlyph, VolumePaths(waves, muted), "LgInk");   // a .btn: its glyph in the text ink, pressed = fill-2
        _mute.Background = muted ? B("LvBarFillOn") : clear;
        _soundMore.Background = clear;

        // power mode: the seg fill-1, the chosen one fill-2 in the text ink, the others text-2
        var pv = PowerModeView.View(_pm, _pmPending, _pmErr);
        Line(_pmHead, headFs, 18); Line(_pmSub, valFs, 18);
        _pmHead.Foreground = B("LgInk"); _pmSub.Foreground = B("LgInk2");
        _pmHeadRow.Margin = new Thickness(0, 0, 0, L("LvQsRowGap", 4));
        _pmRow.Margin = new Thickness(0);
        _pmSeg.Background = B("LgTile");
        _pmSeg.BorderThickness = new Thickness(0);
        _pmSeg.Padding = new Thickness(L("LvQsSegPad", 2));
        _pmSeg.Opacity = pv.Dead ? dead : 1;
        foreach (var g in pv.Segs) {
            var b = _pmBtns[g.Mode];
            var tb = (TextBlock)b.Content;
            b.CornerRadius = new CornerRadius(L("LvQsRSeg", 6));
            b.BorderThickness = new Thickness(0);
            tb.FontWeight = FontWeights.Medium;
            Line(tb, valFs, 16);
            b.Background = g.On ? B("LvBarFillOn") : clear;
            var ink = g.On ? B("LgInk") : B("LgInk2");
            b.Foreground = ink; tb.Foreground = ink;
        }

        // the footer
        _foot.BorderBrush = B("LvBarHair");
        Line(_power, footFs, 16);
        _power.Margin = new Thickness(0);
        _power.Foreground = B("LgInk2");
        var link = (TextBlock)_toWin.Content;
        Line(link, footFs, 16);
        link.FontWeight = FontWeights.Medium;
        link.Foreground = B("LgInk2");
        _toWin.Background = clear; _gear.Background = clear;
        PaintMoreSettings();
    }

    /// The more-settings row and view (sparse layout); nothing until the layout commit.
    partial void PaintMoreSettings();

    // ------------------------------------------------------------------ views: sink and lift

    /// One view out, another in. The islands look: the leaving view sinks (opacity 0 in 200 ms, translateY 12 and
    /// scale .955 in 300 ms) and the coming one lifts from below (translateY 28, scale .965: opacity in 240 ms after 40,
    /// the transform in 460 ms after 20); reduced motion: two quick fades (120 ms). The leaving view stops taking the
    /// pointer at once and is collapsed when its sink ends. <paramref name="jump"/> (and the Electron look): at once.
    void Swap(FrameworkElement from, FrameworkElement to, bool jump = false)
    {
        if (_swapSb != null) {
            _swapSb.Stop();
            _swapSb = null;
            if (_swapFrom != null && _swapFrom != from && _swapFrom != to) _swapFrom.Visibility = Visibility.Collapsed;
        }
        _shown = to;
        to.Visibility = Visibility.Visible;
        to.IsHitTestVisible = true;
        from.IsHitTestVisible = false;
        if (!_isl || jump || !Open) {
            from.Visibility = Visibility.Collapsed;
            from.IsHitTestVisible = true;
            Rest(from); Rest(to);
            return;
        }
        bool reduced = _room.Reduced?.Invoke() ?? false;
        var sb = new Storyboard();
        var fo = Xf(from); var tt = Xf(to);
        Rest(from);
        to.Opacity = 0;
        if (!reduced) { tt.TranslateY = 28; tt.ScaleX = tt.ScaleY = 0.965; }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        void Anim(DependencyObject target, string prop, double value, double ms, double after = 0, bool linear = false)
        {
            var a = new DoubleAnimation { To = value, Duration = new Duration(TimeSpan.FromMilliseconds(ms)), BeginTime = TimeSpan.FromMilliseconds(after) };
            if (!linear) a.EasingFunction = ease;
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        if (reduced) {
            Anim(from, "Opacity", 0, 120, 0, true);
            Anim(to, "Opacity", 1, 120, 0, true);
        } else {
            Anim(from, "Opacity", 0, 200);
            Anim(fo, "TranslateY", 12, 300); Anim(fo, "ScaleX", 0.955, 300); Anim(fo, "ScaleY", 0.955, 300);
            Anim(to, "Opacity", 1, 240, 40);
            Anim(tt, "TranslateY", 0, 460, 20); Anim(tt, "ScaleX", 1, 460, 20); Anim(tt, "ScaleY", 1, 460, 20);
        }
        sb.Completed += (_, _) => {
            if (_swapSb != sb) return;
            _swapSb = null; _swapFrom = null;
            if (_shown != from) { from.Visibility = Visibility.Collapsed; from.IsHitTestVisible = true; }
            sb.Stop();
            Rest(from); Rest(to);
        };
        _swapSb = sb; _swapFrom = from;
        sb.Begin();
    }

    static CompositeTransform Xf(FrameworkElement e)
    {
        if (e.RenderTransform is CompositeTransform c) return c;
        c = new CompositeTransform();
        e.RenderTransform = c;
        e.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        return c;
    }

    static void Rest(FrameworkElement e)
    {
        e.Opacity = 1;
        if (e.RenderTransform is CompositeTransform c) { c.TranslateY = 0; c.ScaleX = 1; c.ScaleY = 1; }
    }

    // ------------------------------------------------------------------ styles

    static Style? _islBtnStyle;
    /// A control in the islands look: its own Background at rest, the hover overlay that turns fill-1 into fill-2
    /// (LvQsHoverOver), and a press that scales to .985 in 160 ms (transform only); no ring of its own.
    static Style IslBtnStyle() => _islBtnStyle ??= (Style)XamlReader.Load(
        $"<Style {Ns} xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/>" +
        "<Setter Property='MinWidth' Value='0'/><Setter Property='MinHeight' Value='0'/><Setter Property='CornerRadius' Value='8'/>" +
        "<Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='{ThemeResource LvBarInk}'/>" +
        "<Setter Property='IsTabStop' Value='False'/><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
        "<Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='VerticalContentAlignment' Value='Center'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<Grid x:Name='Face' RenderTransformOrigin='0.5,0.5' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='{TemplateBinding CornerRadius}'>" +
        "<Grid.RenderTransform><ScaleTransform x:Name='Sc'/></Grid.RenderTransform>" +
        "<Border x:Name='Over' CornerRadius='{TemplateBinding CornerRadius}'/>" +
        "<ContentPresenter Padding='{TemplateBinding Padding}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}' Foreground='{TemplateBinding Foreground}'/>" +
        "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'>" +
        "<VisualState x:Name='Normal'><Storyboard><DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:0.16'/><DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:0.16'/></Storyboard></VisualState>" +
        "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LvQsHoverOver}'/></VisualState.Setters>" +
        "<Storyboard><DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='1' Duration='0:0:0.16'/><DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='1' Duration='0:0:0.16'/></Storyboard></VisualState>" +
        "<VisualState x:Name='Pressed'><Storyboard><DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleX' To='0.985' Duration='0:0:0.16'/><DoubleAnimation Storyboard.TargetName='Sc' Storyboard.TargetProperty='ScaleY' To='0.985' Duration='0:0:0.16'/></Storyboard></VisualState>" +
        "<VisualState x:Name='Disabled'/></VisualStateGroup></VisualStateManager.VisualStateGroups>" +
        "</Grid></ControlTemplate></Setter.Value></Setter></Style>");

    static Style? _islRangeStyle;
    /// The range in the islands look (quick.css .qs-range): a 4 px track (SliderRow.Track, fill-3, behind), the part
    /// before the thumb in the Slider's Foreground (the accent; text-3 while muted), a 16 px thumb in its Background
    /// (the text ink). The named parts are the Slider's own (it sizes HorizontalDecreaseRect and puts the thumb after it).
    static Style IslRangeStyle() => _islRangeStyle ??= (Style)XamlReader.Load(
        $"<Style {Ns} xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>" +
        "<Grid x:Name='SliderContainer' Background='Transparent'><Grid x:Name='HorizontalTemplate'>" +
        "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>" +
        "<Rectangle x:Name='HorizontalTrackRect' Grid.ColumnSpan='3' Height='4' Fill='Transparent'/>" +
        "<Rectangle x:Name='HorizontalDecreaseRect' Height='4' Fill='Transparent'/>" +
        "<Border Grid.ColumnSpan='2' Height='4' Margin='0,0,8,0' CornerRadius='2' Background='{TemplateBinding Foreground}'/>" +
        "<Thumb x:Name='HorizontalThumb' Grid.Column='1' Width='16' Height='16' Background='{TemplateBinding Background}'><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
        "<Ellipse Fill='{TemplateBinding Background}'/></ControlTemplate></Thumb.Template></Thumb>" +
        "</Grid></Grid></ControlTemplate></Setter.Value></Setter></Style>");
}
