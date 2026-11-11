using System;
using System.Collections.Generic;
using Mantle.Core.Features.Desktop;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App.Surfaces.Desktop;

/// <summary>
/// The desktop layer's panes (ui/desktop/index.html + desktop.css): one top-left column of Clock, System, Mission
/// Control and Needs attention, each the taskbar's island glass (a 1 px hairline over the tint; the fallback tint
/// --lg-fill-fb until a wallpaper picture arrives, which the native layer does not take yet). Code-built (no XamlReader:
/// AOT), every brush read from Theme/Tokens.xaml on each paint so a theme flip repaints all of it.
/// </summary>
internal sealed class DeskPanes
{
    static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");
    static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

    readonly Func<string, Brush> _b;
    public readonly StackPanel Cluster = new() { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    public readonly Border Clock, Stats, Mc, Strip;
    readonly TextBlock _clock = new(), _date = new();
    readonly Run _hm = new() { Text = DeskClock.BootHm }, _colon = new() { Text = ":" }, _ss = new() { Text = DeskClock.BootSs }, _mer = new();
    readonly TextBlock _statsHead, _mcHead, _stripHead, _statsNote, _mcNote;
    readonly Run _mcLive = new(), _stripCount = new();
    readonly Grid _rows = new();
    readonly TextBlock _cpu = new(), _mem = new(), _bat = new();
    readonly TextBlock _cpuL, _memL, _batL;
    readonly Border _div1 = new(), _div2 = new();
    readonly StackPanel _stripList = new() { Spacing = 8 };
    readonly ScrollViewer _stripScroll;
    bool _dark, _short;
    IReadOnlyList<DeskRow> _stripRows = Array.Empty<DeskRow>();
    bool _batShown;

    public DeskPanes(Func<string, Brush> brush)
    {
        _b = brush;
        _clock.Inlines.Add(_hm); _clock.Inlines.Add(_colon); _clock.Inlines.Add(_ss); _clock.Inlines.Add(_mer);
        _clock.CharacterSpacing = -20;   // letter-spacing -0.02em
        Typography.SetNumeralAlignment(_clock, FontNumeralAlignment.Tabular);
        AutomationProperties.SetAutomationId(_clock, "desktop.clock");
        AutomationProperties.SetAutomationId(_date, "desktop.date");
        var clockBody = new StackPanel(); clockBody.Children.Add(_clock); clockBody.Children.Add(_date);
        Clock = Pane("clock", "Clock", clockBody);

        _statsHead = Head("System");
        _cpuL = Dt("CPU"); _memL = Dt("Memory"); _batL = Dt("Battery");
        foreach (var (t, id) in new[] { (_cpu, "desktop.cpu"), (_mem, "desktop.mem"), (_bat, "desktop.battery") }) {
            t.FontSize = 13; t.HorizontalAlignment = HorizontalAlignment.Right; t.Text = DeskStats.Dash;
            Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(t, id);
        }
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statsNote = Note("desktop.stats.note");
        var statsBody = new StackPanel(); statsBody.Children.Add(_statsHead); statsBody.Children.Add(_rows); statsBody.Children.Add(_statsNote);
        Stats = Pane("stats", "System", statsBody);

        _mcHead = Head("Mission Control", _mcLive);
        _mcNote = Note("desktop.mc.note"); _mcNote.Text = McProbe.BootNote; _mcNote.Visibility = Visibility.Visible; _mcNote.Margin = new Thickness(0);
        var mcBody = new StackPanel(); mcBody.Children.Add(_mcHead); mcBody.Children.Add(_mcNote);
        Mc = Pane("mc", "Mission Control", mcBody);

        _stripHead = Head("Needs attention", _stripCount);
        AutomationProperties.SetAutomationId(_stripList, "desktop.strip.list");
        _stripScroll = new ScrollViewer { Content = _stripList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var stripBody = new Grid();
        stripBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stripBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stripBody.Children.Add(_stripHead);
        Grid.SetRow(_stripScroll, 1); stripBody.Children.Add(_stripScroll);
        Strip = Pane("strip", "Needs attention", stripBody);
        Strip.Visibility = Visibility.Collapsed;

        Cluster.Children.Add(Clock); Cluster.Children.Add(Stats); Cluster.Children.Add(Mc); Cluster.Children.Add(Strip);
        LayoutRows();
    }

    static Border Pane(string key, string name, UIElement body)
    {
        var b = new Border { Child = body, BorderThickness = new Thickness(1), Tag = key };
        AutomationProperties.SetAutomationId(b, "desktop.pane." + key);
        AutomationProperties.SetName(b, name);
        return b;
    }

    TextBlock Head(string text, Run? note = null)
    {
        var t = new TextBlock { FontSize = 11, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.NoWrap };
        t.Inlines.Add(new Run { Text = text.ToUpperInvariant() });
        if (note != null) { t.Inlines.Add(new Run { Text = "  " }); t.Inlines.Add(note); }
        AutomationProperties.SetName(t, text);
        return t;
    }

    static TextBlock Dt(string text) => new() { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    static TextBlock Note(string id)
    {
        var t = new TextBlock { FontSize = 11.5, LineHeight = 17.25, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(t, id);
        return t;
    }

    void LayoutRows()
    {
        _rows.Children.Clear(); _rows.RowDefinitions.Clear();
        var list = new List<(TextBlock L, TextBlock V)> { (_cpuL, _cpu), (_memL, _mem) };
        if (_batShown) list.Add((_batL, _bat));
        for (int i = 0; i < list.Count; i++) {
            _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var (l, v) = list[i];
            // desktop.css .row: 4 px above and below, a 1 px border-top from the second row on (in the row, above the
            // padding: the divider drawn at the row's top); dt and dd sit on one baseline (12 px dt 1 DIP lower than the
            // 13 px dd); the 13 px line is Chromium's 17, not WinUI's 18 (LineTrim)
            double top = 4 + (i > 0 ? 1 : 0);
            v.Margin = new Thickness(0, top, 0, 4 - DesktopLayer.LineTrim(13));
            l.Margin = new Thickness(0, top + 1, 0, 0);
            l.VerticalAlignment = VerticalAlignment.Top; v.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetRow(l, i); Grid.SetColumn(l, 0); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            _rows.Children.Add(l); _rows.Children.Add(v);
            if (i > 0) {
                var d = i == 1 ? _div1 : _div2;
                d.Height = 1; d.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(d, i); Grid.SetColumnSpan(d, 2);
                _rows.Children.Add(d);
            }
        }
    }

    // ---- what they show ----

    public void SetClock(DeskClock.Parts p, string date)
    {
        if (_hm.Text != p.Hm) _hm.Text = p.Hm;
        if (_ss.Text != p.Ss) _ss.Text = p.Ss;
        string mer = " " + p.Mer;
        if (_mer.Text != mer) _mer.Text = mer;
        if (_date.Text != date) _date.Text = date;
        AutomationProperties.SetName(_clock, p.Text);
    }

    public void SetStats(DeskStats.View v)
    {
        _cpu.Text = v.Cpu; _mem.Text = v.Mem;
        bool bat = v.Battery != null;
        if (bat) _bat.Text = v.Battery!;
        if (bat != _batShown) { _batShown = bat; LayoutRows(); Paint(_dark, _short); }
        _statsNote.Text = v.Note ?? "";
        _statsNote.Visibility = v.Note != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetMc(bool live, string? note)
    {
        _mcLive.Text = live ? "· live" : "";
        _mcNote.Text = note ?? "";
        _mcNote.Visibility = note != null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetItemStatus(Mc, live ? "live" : "offline");
    }

    public void SetStrip(IReadOnlyList<DeskRow> rows, bool test)
    {
        _stripRows = rows;
        _stripList.Children.Clear();
        Strip.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _stripCount.Text = rows.Count == 0 ? "" : DeskStrip.Count(rows.Count, test);
        AutomationProperties.SetItemStatus(Strip, rows.Count + (rows.Count == 1 ? " thing" : " things"));
        int n = 0;
        foreach (var r in rows) {
            var g = new Grid { ColumnSpacing = 9 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(7) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var pip = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Tag = "pip" };
            var t = new TextBlock { FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Tag = "row" };
            t.Inlines.Add(new Run { Text = r.Title, FontWeight = FontWeights.Medium });
            t.Inlines.Add(new Run { Text = " — " + r.Reason });
            AutomationProperties.SetAutomationId(t, "desktop.strip.row." + n++);
            AutomationProperties.SetName(t, r.Title + " — " + r.Reason);
            Grid.SetColumn(t, 1);
            g.Children.Add(pip); g.Children.Add(t);
            _stripList.Children.Add(g);
        }
        Paint(_dark, _short);
    }

    /// Widgets off: every pane hidden in place (visibility:hidden: nothing moves).
    public void SetWidgets(bool on)
    {
        foreach (var p in new[] { Clock, Stats, Mc, Strip }) { p.Opacity = on ? 1 : 0; p.IsHitTestVisible = on; AutomationProperties.SetItemType(p, on ? "widget" : "widget hidden"); }
        AutomationProperties.SetItemStatus(Cluster, on ? "widgets on" : "widgets off");
    }

    /// The strip may scroll inside what is left of the window height (the heading stays put).
    public void FitHeight(double windowHeight)
    {
        var c = DesktopLayer.ClusterFor(windowHeight);
        Strip.MaxHeight = Math.Max(60, c.MaxHeight - Clock.ActualHeight - Stats.ActualHeight - Mc.ActualHeight - 3 * c.Gap);
    }

    // ---- the look ----

    public void Paint(bool dark, bool shortWindow)
    {
        _dark = dark; _short = shortWindow;
        var c = DesktopLayer.ClusterFor(shortWindow ? 700 : 900);
        Cluster.Margin = new Thickness(c.Left, c.Top, 0, 0);
        Cluster.Width = c.Width;
        Cluster.Spacing = c.Gap;
        foreach (var p in new[] { Clock, Stats, Mc, Strip }) {
            // the hairline is drawn INSIDE the pane in Electron (::after, inset 0): here it is the Border's 1 DIP, so the
            // padding gives that DIP back and every pane is exactly as tall as Electron's
            p.Padding = new Thickness(c.Pad.L - 1, c.Pad.T - 1, c.Pad.R - 1, c.Pad.B - 1);
            p.CornerRadius = new CornerRadius(16);   // light: the tile radius; dark: --lg-r-pop (16 too)
            p.Background = _b(dark ? "LgFillPop" : "LgFillFb");   // no wallpaper picture yet: the fallback tint
            p.BorderBrush = _b("LgEdgeOut");
        }
        Strip.BorderBrush = dark ? _b("LgEdgeOut") : _b("LgWarnSoft");
        var body = dark ? new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI") : TextFont;
        _clock.FontFamily = DisplayFont;
        _clock.FontSize = c.ClockPx; _clock.LineHeight = c.ClockPx; _clock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _clock.FontWeight = FontWeights.Light; _clock.Foreground = _b("LgInk");
        _mer.FontSize = Math.Round(c.ClockPx * 0.30, 1); _mer.FontWeight = FontWeights.Medium; _mer.CharacterSpacing = 60; _mer.Foreground = _b("LgInk2");
        _date.FontFamily = body; _date.FontSize = c.DatePx; _date.Margin = new Thickness(0, c.DateTop, 0, -DesktopLayer.LineTrim(c.DatePx)); _date.Foreground = _b("LgInk2");
        foreach (var h in new[] { _statsHead, _mcHead, _stripHead }) {
            h.FontFamily = body; h.FontWeight = dark ? FontWeights.SemiBold : FontWeights.Medium;
            h.CharacterSpacing = dark ? 40 : 90; h.Foreground = _b("LgInk3");
        }
        _stripHead.Foreground = _b("LgWarn");
        _mcLive.Foreground = _b("LgAcc");
        _stripCount.Foreground = _b("LgInk3");
        foreach (var t in new[] { _cpuL, _memL, _batL }) { t.FontFamily = body; t.Foreground = _b("LgInk2"); }
        foreach (var t in new[] { _cpu, _mem, _bat }) { t.FontFamily = body; t.Foreground = _b("LgInk"); }
        foreach (var d in new[] { _div1, _div2 }) d.Background = _b(dark ? "LgDivider" : "LgEdgeOut");
        foreach (var n in new[] { _statsNote, _mcNote }) { n.FontFamily = body; n.FontSize = dark ? 12 : 11.5; n.LineHeight = n.FontSize * 1.5; n.Foreground = _b("LgInk2"); }
        foreach (var child in _stripList.Children)
            if (child is Grid g) foreach (var e in g.Children) {
                if (e is Ellipse pip) pip.Fill = _b("LgWarn");
                else if (e is TextBlock t) { t.FontFamily = body; t.Foreground = _b("LgInk2"); if (t.Inlines.Count > 0) t.Inlines[0].Foreground = _b("LgInk"); }
            }
    }
}
