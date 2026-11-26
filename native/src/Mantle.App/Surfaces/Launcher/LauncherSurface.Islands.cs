using System;
using System.Numerics;
using Mantle.App.Native;
using Mantle.App.Shell.Levitate;
using Mantle.App.Surfaces.Osd;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The launcher in the islands look (lane r15-quick-launcher; on when <c>Surfaces.Bar.Frame.BarGeometry.Islands</c>, else the
/// old glass look in LauncherSurface.cs is untouched). The prototype is apps-r3/mantle-bar panels/launcher.css in both
/// themes (apps-r3/light-mantle copies it unchanged; the inks follow LIGHT.md section 4):
/// <list type="bullet">
/// <item>One island, a pop-over: <see cref="LevitatePanel"/> (its part of the frosted still, the dim, the v2.1 film in dark or
/// the lift in light, the sheen on hover only) at radius 14, the float-hi shadow round it (<see cref="LevitateShadow"/>); no
/// hairline edge, no ring, no tint. 600 x 520 (capped at the monitor less 24 / 80), centred on the primary display; the
/// window round it is only the shadow's room.</item>
/// <item>The field: no fill and no ring, a 1 px hair under it and the accent line over that while the query has the
/// keyboard (UIA "field-line", ItemStatus "focused"); an 18 search glyph in text-2.</item>
/// <item>Rows 36 (Quick links 32, no icon), padding 0 12 0 8, radius 8; the selected row is fill-2 plus a 3 px accent tick
/// (UIA "row-tick" in each row, ItemStatus "on" on the selected one); an armed Quick links row is in the warn ink.</item>
/// <item>The foot: 40 tall with a hair on top, the key hints at 12 in text-2 with keycaps at 11 on fill-1. Readable text is
/// the text ink or text-2, never text-3 (text-3 is only the field's placeholder, launcher.css L13).</item>
/// <item>Motion, transform and opacity only: the pop-over in (opacity 160, translateY from -6 and scale from 0.97 over 260 on
/// ease-lift, about the island's centre) and out (120 / 160 on ease); reduced motion: quick fades (LevitateMotion).
/// Nothing follows the pointer; nothing runs at rest.</item>
/// </list>
/// Numbers: Theme/LvLauncher.xaml (<see cref="LauncherSpec"/>); inks: Theme/Levitate.xaml LvBar* and the islands accent.
/// </summary>
internal sealed partial class LauncherSurface
{
    bool _lv;                                          // the islands look, decided once at Create
    LauncherSpec _spec = null!;
    LevitatePanel? _lvPanel;
    Grid? _slotGlyph;
    SolidColorBrush _lvAccent = new();
    double _paneW = WidthDip - 2 * InsetDip, _paneH = HeightDip - 2 * InsetDip;
    int _lvSeq;
    string _lvShadowKey = "";

    /// An islands ink of the theme in effect.
    Brush Lv(string key) => Mantle.App.Shell.Levitate.IslandsInk.Token(_dark, key, _ctx.Log.Line);

    /// Create(), after Build(): the island's layers under the content; the old glass layers stay in the tree, collapsed.
    void LvCreate()
    {
        var r = new CornerRadius(_spec.Radius);
        var lev = new LevitatePanel("launcher", r);
        _lvPanel = lev;
        _panelGrid.Children.Insert(0, lev.Sheen);
        _panelGrid.Children.Insert(0, lev.Film);
        _panelGrid.Children.Insert(0, lev.Dim);
        _panelGrid.Children.Insert(0, lev.Picture);       // Picture, Dim, Film, Sheen, then the launcher's own content
        foreach (var b in new[] { _bdrop, _fill, _edge, _lip, _dim }) b.Visibility = Visibility.Collapsed;
        _panelGrid.CornerRadius = r;
        _panelGrid.Background = new SolidColorBrush();      // clear but hit-testable, so the sheen follows the whole island
        _panelGrid.PointerEntered += (_, _) => lev.Hover(true);
        _panelGrid.PointerExited += (_, _) => lev.Hover(false);
        _panel.HorizontalAlignment = HorizontalAlignment.Left; _panel.VerticalAlignment = VerticalAlignment.Top;
        _shadow.HorizontalAlignment = HorizontalAlignment.Left; _shadow.VerticalAlignment = VerticalAlignment.Top;
        if (LvPlace() is { } at) LvShadow(at.Win.Right - at.Win.Left, at.Win.Bottom - at.Win.Top, at.Scale);   // made once now (off the UI thread), not on the first open
        _ctx.Log.Line(FormattableString.Invariant($"islands look: one island {_paneW:0}x{_paneH:0} DIP at radius {_spec.Radius:0}, rows {_spec.RowHeight:0} (Quick links {_spec.RowHeightBare:0}), the float-hi shadow round it"));
    }

    /// The pane for the primary display as it is NOW, centred on it (LAU-002), the window round it the shadow's room.
    (Win32.RECT Win, Win32.RECT Screen, double Scale)? LvPlace()
    {
        var area = DisplayArea.Primary;
        double scale = Math.Max(1, Win32.GetDpiForWindow(_hwnd)) / 96.0;
        if (area == null || area.WorkArea.Width <= 0) { _ctx.Log.Line("the primary display reports no size; opening where it was"); return null; }
        var o = area.OuterBounds;
        var (pw, ph) = _spec.PaneFor(o.Width / scale, o.Height / scale);
        var room = _spec.ShadowRoom;
        _paneW = pw; _paneH = ph;
        _panel.Width = pw; _panel.Height = ph;
        _panel.Margin = new Thickness(room.Left, room.Top, 0, 0);
        int pwPx = (int)Math.Round(pw * scale), phPx = (int)Math.Round(ph * scale);
        int w = (int)Math.Round((pw + room.Left + room.Right) * scale), h = (int)Math.Round((ph + room.Top + room.Bottom) * scale);
        int x = o.X + (o.Width - pwPx) / 2 - (int)Math.Round(room.Left * scale), y = o.Y + (o.Height - phPx) / 2 - (int)Math.Round(room.Top * scale);
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Win32.SWP_NOACTIVATE | 0x0004);
        return (new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, new Win32.RECT { Left = o.X, Top = o.Y, Right = o.X + o.Width, Bottom = o.Y + o.Height }, scale);
    }

    /// Before the window shows: the shadow for this size and the island's frost lined up with the screen.
    void LvGlass((Win32.RECT Win, Win32.RECT Screen, double Scale)? at)
    {
        if (at is not { } p || _lvPanel == null) return;
        double s = p.Scale > 0 ? p.Scale : 1;
        LvShadow(p.Win.Right - p.Win.Left, p.Win.Bottom - p.Win.Top, s);
        var room = _spec.ShadowRoom;
        _lvPanel.Place((p.Screen.Right - p.Screen.Left) / s, (p.Screen.Bottom - p.Screen.Top) / s,
            (p.Win.Left - p.Screen.Left) / s + room.Left, (p.Win.Top - p.Screen.Top) / s + room.Top, _paneW, _paneH, _dark);
    }

    /// The float-hi shadow round the island (look.css .panel > .island), one bitmap per window size and scale, both themes
    /// (LIGHT.md section 4: the shadows fall on the picture, which is theme-free).
    void LvShadow(int wp, int hp, double s)
    {
        if (wp <= 0 || hp <= 0) return;
        var room = _spec.ShadowRoom;
        string key = FormattableString.Invariant($"{wp}x{hp}@{s}:{_paneW}x{_paneH}");
        if (key == _lvShadowKey) return;
        _lvShadowKey = key;
        var box = new CssShadow.Box(room.Left, room.Top, _paneW, _paneH, _spec.Radius);
        // the blur runs off the UI thread (CssShadow.Into, as the OSD's shadow does since lane native-r8-startup): made on
        // the UI thread at start it held it ~280 ms (StartupStallTests' limit is 250); the bitmap is one copy on the UI thread
        var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        var boxes = new[] { box };
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            var px = new byte[Math.Max(1, wp) * Math.Max(1, hp) * 4];
            CssShadow.Into(px, wp, hp, s, boxes, LevitateShadow.FloatHi);
            dq?.TryEnqueue(() =>
            {
                if (key != _lvShadowKey) return;   // a newer size asked for its own
                _shadow.Source = CssShadow.FromPixels(px, wp, hp);
                _shadow.Width = wp / s; _shadow.Height = hp / s;
            });
        });
    }

    /// The pop-over in (look.css .panel.center / .panel.open), about the island's centre; a pending out is cancelled.
    void LvIn()
    {
        ++_lvSeq;
        var s = _spec;
        LvPlay(new Track[] {
            new("opacity", 0, 1, s.InFadeMs, 0, LevitateTokens.Ease),
            new("translateY", -s.Rise, 0, s.InMoveMs, 0, LevitateTokens.EaseLift),
            new("scale", s.ScaleFrom, 1, s.InMoveMs, 0, LevitateTokens.EaseLift),
        }, null);
    }

    /// The pop-over out; done() once when it has landed (or 250 ms after it should have, if no frame came). A new open
    /// first cancels it.
    void LvOut(Action done)
    {
        int seq = ++_lvSeq;
        var s = _spec;
        bool ran = false;
        void End() { if (ran || seq != _lvSeq) return; ran = true; done(); }
        LvPlay(new Track[] {
            new("opacity", 1, 0, s.OutFadeMs, 0, LevitateTokens.Ease),
            new("translateY", 0, -s.Rise, s.OutMoveMs, 0, LevitateTokens.Ease),
            new("scale", 1, s.ScaleFrom, s.OutMoveMs, 0, LevitateTokens.Ease),
        }, End);
        DispatcherQueueTimerLike.After(_ctx.Dispatcher, (int)Math.Max(s.OutFadeMs, s.OutMoveMs) + 250, End);
    }

    void LvPlay(Track[] tracks, Action? done)
    {
        LevitateMotion.Play(_root, tracks, done);
        // about the island's centre, from the sizes it was given (a window shown for the first time may not be laid out yet)
        var room = _spec.ShadowRoom;
        ElementCompositionPreview.GetElementVisual(_root).CenterPoint = new Vector3((float)(room.Left + _paneW / 2), (float)(room.Top + _paneH / 2), 0);
    }

    // ------------------------------------------------------------------ the tree's parts

    /// Build(): the field's row (the search glyph, then the query), its sizes, the list's and the foot's.
    void LvBuild()
    {
        var s = _spec;
        _slotGlyph = new Grid { Width = 16, Height = 16 };
        var icon = new Viewbox { Width = s.FieldIcon, Height = s.FieldIcon, VerticalAlignment = VerticalAlignment.Center, Child = _slotGlyph, IsHitTestVisible = false };
        var row = new Grid { ColumnSpacing = s.FieldGap };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _slot.Child = null;
        Grid.SetColumn(_q, 1);
        row.Children.Add(icon); row.Children.Add(_q);
        _slot.Child = row;
        _slot.Height = s.FieldHeight; _slot.Padding = s.FieldPadding; _slot.BorderThickness = new Thickness(0, 0, 0, s.FieldLine);
        _focusLine.Height = s.FieldLine; _focusLine.Margin = s.FieldLineInset;
        _focusLine.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(s.FieldLineFadeMs) };
        AutomationProperties.SetAutomationId(_focusLine, "field-line");
        AutomationProperties.SetName(_focusLine, "Search line underline");
        _q.FontSize = s.FieldFont;
        _scroll.Padding = s.BodyPadding;
        _hint.Spacing = s.HintGap;
        _hintRow.MinHeight = s.FootHeight - s.FootLine;
        _foot.Padding = s.FootPadding; _foot.BorderThickness = new Thickness(0, s.FootLine, 0, 0); _foot.MinHeight = s.FootHeight;
        _notice.FontSize = s.HintFont;
        _noticeRow.Margin = new Thickness(0, 8, 0, 0);
    }

    /// ApplyTheme() in the islands look: every ink from LvBar* and the islands accent.
    void LvTheme(bool dark)
    {
        var r = new CornerRadius(_spec.Radius);
        _panel.CornerRadius = r; _panelGrid.CornerRadius = r;
        _lvPanel?.Theme(dark);
        _shadow.Visibility = Visibility.Visible;
        var acc = Mantle.App.Shell.Levitate.IslandsInk.Accent(dark, _ctx.Log.Line);
        _lvAccent = new SolidColorBrush(acc.Acc);
        var hair = Lv("LvBarHair");
        _slot.BorderBrush = hair;                                            // launcher.css L8: the hair under the field
        _foot.BorderBrush = hair;                                            // L60: the hair over the foot
        _focusLine.Background = _lvAccent;                                   // L9: the accent line
        foreach (var (k, b) in _qBrushes)
            b.Color = k.Contains("Placeholder") ? Mantle.App.Shell.Levitate.IslandsInk.Color(dark, "LvBarInk3", _ctx.Log.Line)
                : k.Contains("Foreground") ? Mantle.App.Shell.Levitate.IslandsInk.Color(dark, "LvBarInk", _ctx.Log.Line) : LauncherTokens.None;
        _q.Foreground = Lv("LvBarInk");
        _q.SelectionHighlightColor = new SolidColorBrush(acc.Acc);
        _notice.Foreground = Lv("LvBarWarn");
        LauncherGlyphs.Warn(_noticeGlyph, Lv("LvBarWarn"));
        if (_slotGlyph != null) LauncherGlyphs.Fill(_slotGlyph, "web", Lv("LvBarInk2"), dark);   // L7: the field's glyph in text-2
    }

    /// A group heading (L23): 12 / 600 in the text ink.
    TextBlock LvHead() => new() { FontSize = _spec.GroupFont, FontWeight = FontWeights.SemiBold, Foreground = Lv("LvBarInk") };

    Thickness LvHeadMargin(bool first) => first ? _spec.GroupPaddingFirst : _spec.GroupPadding;

    /// The longest a row's kind or folder may be (L42: 44 % of the row).
    double LvSubMax() => Math.Max(0, (_paneW - _spec.BodyPadding.Left - _spec.BodyPadding.Right) * _spec.SubShare);

    /// One row's elements in the islands look (L30 to L42): the wash is the selected fill (fill-2), the tick the accent.
    Row LvRow()
    {
        var s = _spec;
        var wash = new Border { CornerRadius = new CornerRadius(s.RowRadius), Opacity = 0, Background = Lv("LvBarFillOn") };
        var tick = new LauncherGrid(AutomationControlType.Group)
        {
            Width = s.TickWidth, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, s.TickInset, 0, s.TickInset),
            CornerRadius = new CornerRadius(Math.Min(s.TickRadius, s.TickWidth / 2)), Background = _lvAccent, Opacity = 0, IsHitTestVisible = false,
        };
        AutomationProperties.SetAutomationId(tick, "row-tick");
        AutomationProperties.SetName(tick, "Selected row tick");
        var letter = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Lv("LvBarInk"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var glyphs = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var pic = new Image { Width = s.Icon, Height = s.Icon, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        var cell = new Grid();
        cell.Children.Add(letter); cell.Children.Add(glyphs); cell.Children.Add(pic);
        var tile = new Border { Width = s.Icon, Height = s.Icon, CornerRadius = s.IconCorner, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, s.RowGap, 0), Child = cell };
        var name = new TextBlock { FontSize = s.NameFont, Foreground = Lv("LvBarInk"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { MaxWidth = LvSubMax(), FontSize = s.SubFont, Foreground = Lv("LvBarInk2"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); Grid.SetColumn(sub, 2);
        var main = new Grid { Padding = s.RowPadding };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.Children.Add(tile); main.Children.Add(name); main.Children.Add(sub);
        var g = new LauncherGrid(AutomationControlType.ListItem) { Height = s.RowHeight, Background = new SolidColorBrush(LauncherTokens.None) };   // a fill so the whole row takes the pointer
        g.Children.Add(wash); g.Children.Add(tick); g.Children.Add(main);
        return new Row { Root = g, Wash = wash, Tick = tick, Tile = tile, Letter = letter, Name = name, Sub = sub, GlyphBox = glyphs, Pic = pic, Main = main };
    }

    /// FillRow() in the islands look: a Quick links row is bare (L37, L38: 32 tall, padding 0 12, no icon); an armed row says
    /// what the next press does in the warn ink, its tick too (L51 to L53); a note under it is ok or warn.
    void LvFill(Row r, LauncherItem it)
    {
        var s = _spec;
        bool bare = it.Kind == "quick";
        r.Root.Height = bare ? s.RowHeightBare : s.RowHeight;
        if (r.Main != null) r.Main.Padding = bare ? s.RowPaddingBare : s.RowPadding;
        double inset = bare ? s.TickInsetBare : s.TickInset;
        r.Tick.Margin = new Thickness(0, inset, 0, inset);
        r.Tile.Visibility = bare ? Visibility.Collapsed : Visibility.Visible;
        r.Sub.Margin = new Thickness(it.Subtitle.Length > 0 ? s.RowGap : 0, 0, 0, 0);
        r.Sub.MaxWidth = LvSubMax();
        bool armed = _mode == QuickMode && _quick.ArmedId == it.Id;
        string subInk = "LvBarInk2";
        if (_mode == QuickMode && _quick.Note is { } n && n.Id == it.Id) subInk = n.Ok ? "LvBarOk" : "LvBarWarn";
        if (armed) subInk = "LvBarWarn";
        r.Name.Foreground = Lv(armed ? "LvBarWarn" : "LvBarInk");
        r.Name.FontWeight = armed ? FontWeights.SemiBold : FontWeights.Normal;
        r.Sub.Foreground = Lv(subInk);
        r.Tick.Background = armed ? Lv("LvBarWarn") : _lvAccent;
    }

    /// Icon() in the islands look: a picture stands on no tile, a glyph on fill-1 in text-2 (L39), a letter on fill-2 in the
    /// text ink (look.css .app-ic).
    Brush? LvTile(bool pic, bool glyph) => pic ? null : glyph ? Lv("LvBarFillHover") : Lv("LvBarFillOn");

    /// Select() in the islands look: the selected row's fill and its tick.
    void LvSelect(Row r, bool on)
    {
        r.Wash.Opacity = on ? 1 : 0;
        r.Tick.Opacity = on ? 1 : 0;
        string st = on ? "on" : "";
        if (AutomationProperties.GetItemStatus(r.Tick) != st) AutomationProperties.SetItemStatus(r.Tick, st);
    }

    /// A key hint (L61 to L65, look.css .kbd): the keycap at 11 on fill-1 in text-2, 8 to its word at 12 in text-2.
    StackPanel LvKeycap(string key, string word)
    {
        var s = _spec;
        var kbd = new Border
        {
            Padding = s.KbdPadding, CornerRadius = s.KbdCorner, Background = Lv("LvBarFillHover"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = key, FontSize = s.KbdFont, Foreground = Lv("LvBarInk2"), TextAlignment = TextAlignment.Center },
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = s.KeyGap, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(kbd);
        sp.Children.Add(new TextBlock { Text = word, FontSize = s.HintFont, Foreground = Lv("LvBarInk2"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    /// The no-match and problem lines (L26): 13 in text-2, or the warn ink.
    TextBlock LvLine(string text, bool warn) => new()
    {
        Text = text, FontSize = _spec.NoneFont, TextWrapping = TextWrapping.Wrap, Margin = _spec.NonePadding, Foreground = Lv(warn ? "LvBarWarn" : "LvBarInk2"),
    };
}
