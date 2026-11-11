using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.TopBar;

// The stat dropdowns (TOP-100..110): ui/topbar/topbar.js's openStat / askDetail / renewDetail / renderStatMenu /
// openTaskMgr. The rows are Mantle.Core's TopBarStatMenu.Rows; this half draws them once and, while the dropdown stays
// open, sets only the words that changed on each 1 s sample (Electron rewrote the dropdown's HTML every second). Closed,
// it costs nothing: no element, no timer.
internal sealed partial class TopBarSurface
{
    /// The drawn stat dropdown: one entry per row, so a sample of the same shape only sets changed words.
    sealed class StatView
    {
        public TopBarMenu Owner = null!; public string Shape = ""; public Dd Dd = null!;
        public readonly List<(TextBlock? A, TextBlock? B, StatRow Row, List<(Border Fill, Border Bar)>? Cores)> Rows = new();
    }
    StatView? _statView;
    DispatcherQueueTimer? _renewT;

    /// TOP-100: a press on a cell opens its dropdown; on the open cell it closes it and gives the app the keyboard back.
    void OnStat(string key)
    {
        if (_open?.Stat == key) { CloseAll(true, "the open stat pressed again"); _ctx.Log.Line($"stat pressed: {key} (closed; refocus)"); return; }
        _open = TopBarMenu.ForStat(key);
        _openKey = "stat:" + key;
        PaintTitles();
        RenderMenus();
        _ctx.Log.Line($"stat pressed: {key} (open; {_statView?.Rows.Count ?? 0} rows)");
        AskDetail(_open);
    }

    /// TOP-101: a meter's details are asked for on open and renewed while it stays open; never for ping.
    async void AskDetail(TopBarMenu o)
    {
        if (!TopBarStatMenu.AsksDetail(o.Stat)) return;
        o.DetailAt = NowMs;
        var r = await _ctx.CallAsync("topbar.statsDetail", new JsonObject { ["kind"] = o.Stat });
        bool ok = r.Ok && !(r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var k) && k.ValueKind == JsonValueKind.False);
        string? why = r.Ok ? (r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var rv) ? rv.GetString() : null) : r.Error;
        _ctx.Log.Line($"stats detail asked: {o.Stat} ({(ok ? "ok" : "refused: " + why)})");
    }

    /// renewDetail: 5 s after the last ask, 450 ms after this sample. One one-shot timer, made on first use, stopped on close.
    void RenewDetail(TopBarMenu o)
    {
        if (!TopBarStatMenu.RenewDue(o.Stat, NowMs, o.DetailAt, _renewT is { IsRunning: true })) return;
        if (_renewT == null)
        {
            _renewT = _win.DispatcherQueue.CreateTimer();
            _renewT.IsRepeating = false;
            _renewT.Tick += (t, _) => { t.Stop(); if (_open is { Stat: not null } cur) AskDetail(cur); };
        }
        _renewT.Interval = TimeSpan.FromMilliseconds(TopBarStatMenu.RenewAfterSampleMs);
        _renewT.Start();
    }

    /// TOP-102..108: the dropdown for the open stat. The same shape as the drawn one = the same elements, words set only
    /// where they changed (no re-measure, no re-place, no window resize); a new shape is drawn anew with its scroll kept.
    void RenderStatMenu(TopBarMenu o)
    {
        var rows = TopBarStatMenu.Rows(o.Stat!, _stats);
        var (ptRows, ptSig) = PingRows(o);   // TOP-139..159: the ping test below PING's rows (TopBarSurface.PingTest.cs)
        string shape = TopBarStatMenu.Shape(rows) + "|" + ptSig;
        if (_statView is { } v && v.Owner == o && v.Shape == shape)
        {
            int changed = 0;
            for (int i = 0; i < rows.Count; i++) changed += Update(v.Rows[i], rows[i]);
            for (int i = 0; i < rows.Count; i++) v.Rows[i] = (v.Rows[i].A, v.Rows[i].B, rows[i], v.Rows[i].Cores);
            if (changed > 0 && _test) _ctx.Log.Line($"stat dropdown: {o.Stat} in place ({changed} words changed)");
            return;
        }
        double scroll = _statView?.Owner == o ? _statView.Dd.Sv.VerticalOffset : 0;
        bool boxFocused = _ptBox != null && _ptBox.FocusState != FocusState.Unfocused;
        _menus.Children.Clear(); _dds.Clear(); _editBox = null; _ptBox = null;
        _menus.Visibility = Visibility.Visible; _away.Visibility = Visibility.Visible;
        var view = new StatView { Owner = o, Shape = shape };
        var panel = new StackPanel();
        for (int i = 0; i < rows.Count; i++) { var (el, entry) = StatRowView(rows[i], i); panel.Children.Add(el); view.Rows.Add(entry); }
        for (int i = 0; i < ptRows.Count; i++) panel.Children.Add(PingRowView(ptRows[i], i));
        var dd = new Dd { Lv = o.Stack[0], Rows = panel };
        dd.Sv = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = false };
        dd.Box = new Border
        {
            Child = dd.Sv, Padding = new Thickness(5), MinWidth = TopBarStatMenu.MinW, MaxWidth = _dark ? TopBarStatMenu.MaxWDark : TopBarStatMenu.MaxWLight,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Dbl("LgR", 8)), Background = MenuFace(), BorderBrush = Br("LgEdgeOut"),
        };
        AutomationProperties.SetAutomationId(dd.Sv, "topbar.menu.0");
        AutomationProperties.SetName(dd.Sv, "stat:" + o.Stat);
        view.Dd = dd;
        _statView = view;
        _menus.Children.Add(dd.Box);
        dd.Box.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = dd.Box.DesiredSize.Width, h = dd.Box.DesiredSize.Height;
        double right = 8 + w;
        if (_cells.TryGetValue(o.Stat!, out var cell))
            try { right = cell.B.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(cell.B.ActualWidth, 0)).X; } catch (ArgumentException) { }
        var (top, maxH) = TopBarMenu.FitDown(0, TopBarMenu.DdTop, h, _workBottom);
        if (maxH is double m) dd.Box.MaxHeight = m;
        Canvas.SetLeft(dd.Box, TopBarStatMenu.Left(right, w)); Canvas.SetTop(dd.Box, top);
        dd.Bottom = top + Math.Min(h, maxH ?? h);
        _dds.Add(dd);
        _menus.UpdateLayout();
        if (scroll > 0) dd.Sv.ChangeView(null, scroll, null, true);
        AfterPingRender(boxFocused);
        NotifyMain();
        Reveal();
    }

    static string Upper(string s) => s.ToUpperInvariant();

    /// One row's element and the parts a later sample may change.
    (FrameworkElement, (TextBlock?, TextBlock?, StatRow, List<(Border, Border)>?)) StatRowView(StatRow r, int i)
    {
        string id = $"topbar.menu.0.{i}";
        switch (r.Kind)
        {
            case StatRowKind.Head:
            {
                // .dd.stat h4: 11 px 600 ink-3 uppercase (dark: 12 px, 28 px tall, 10 px sides)
                var t = new TextBlock
                {
                    Text = Upper(r.Text), FontFamily = TextFont, FontSize = _dark ? 12 : 11, FontWeight = FontWeights.SemiBold, Foreground = Br("LgInk3"),
                    CharacterSpacing = _dark ? 40 : 30, Margin = _dark ? new Thickness(10, 0, 10, 0) : new Thickness(8, 4, 8, 6), TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (_dark) { t.Height = 28; t.LineHeight = 28; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; }
                AutomationProperties.SetAutomationId(t, id); AutomationProperties.SetName(t, r.Text);
                return (t, (t, null, r, null));
            }
            case StatRowKind.Row:
            {
                // .row2: name (cut with an ellipsis) and the value right-aligned in ink-2, tabular figures
                var g = new Grid { ColumnSpacing = _dark ? 10 : 16, Padding = _dark ? new Thickness(10, 2, 10, 2) : new Thickness(8, 2, 8, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                double size = _dark ? 13 : 12.5;
                var n = new TextBlock { Text = r.Text, FontFamily = TextFont, FontSize = size, Foreground = Br("LgInk"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
                var x = new TextBlock { FontFamily = TextFont, FontSize = size, Foreground = Br("LgInk2"), TextAlignment = TextAlignment.Right };
                x.Inlines.Add(Tabular(r.Value ?? ""));
                Grid.SetColumn(x, 1);
                g.Children.Add(n); g.Children.Add(x);
                AutomationProperties.SetAutomationId(g, id); AutomationProperties.SetName(g, r.Text + " " + r.Value);
                return (g, (n, x, r, null));
            }
            case StatRowKind.Cores:
            {
                // .cores: 8 columns, 3 px gaps; each bar 26 px on --lg-sunk (dark: --lg-field, control radius), filled from
                // the bottom to its percent in the accent at .75
                var g = new Grid { ColumnSpacing = 3, RowSpacing = 3, Padding = new Thickness(8, 4, 8, 6) };
                for (int c = 0; c < 8; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var cores = new List<(Border, Border)>();
                var list = r.Cores ?? Array.Empty<double>();
                for (int k = 0; k < list.Count; k++)
                {
                    if (k % 8 == 0) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var fill = new Border { VerticalAlignment = VerticalAlignment.Bottom, Background = Br("LgAcc"), Opacity = 0.75, Height = CoreH(list[k]) };
                    var bar = new Border { Height = 26, CornerRadius = new CornerRadius(_dark ? Dbl("LgRCtl", 6) : 3), Background = Br(_dark ? "LgField" : "LgSunk"), Child = fill };
                    ToolTipService.SetToolTip(bar, TopBarModel.Pct(list[k]));
                    AutomationProperties.SetName(bar, TopBarModel.Pct(list[k]));
                    Grid.SetColumn(bar, k % 8); Grid.SetRow(bar, k / 8);
                    g.Children.Add(bar); cores.Add((fill, bar));
                }
                AutomationProperties.SetAutomationId(g, id); AutomationProperties.SetName(g, $"{list.Count} cores");
                return (g, (null, null, r, cores));
            }
            case StatRowKind.Info:
            {
                var t = Info(r.Text, r.Bad, id);
                return (t, (t, null, r, null));
            }
            case StatRowKind.Sep:
                return (new Border { Height = 1, Margin = _dark ? new Thickness(6, 4, 6, 4) : new Thickness(8, 4, 8, 4), Background = Br(_dark ? "LgDivider" : "LgSunk") }, (null, null, r, null));
            default:
            {
                // "Open Task Manager": a menu row with its key text; it closes the menu and asks for the Task Manager (TOP-108)
                var b = (Button)XamlReader.Load($"<Button {Ns} Height='24' Padding='6,0,8,0' HorizontalAlignment='Stretch' HorizontalContentAlignment='Stretch' ClickMode='Release' IsTabStop='False' UseSystemFocusVisuals='False'>"
                    + "<Button.Template><ControlTemplate TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>"
                    + "<ContentPresenter Content='{TemplateBinding Content}' Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center' HorizontalAlignment='Stretch'/></Border></ControlTemplate></Button.Template></Button>");
                var g = new Grid { ColumnSpacing = 6 };
                foreach (var wd in new[] { new GridLength(16), new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(12) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = wd });
                var l = new TextBlock { Text = r.Text, FontFamily = TextFont, FontSize = _dark ? 13 : 12.5, Foreground = Br("LgInk"), VerticalAlignment = VerticalAlignment.Center };
                var k = new TextBlock { Text = r.Value, FontFamily = TextFont, FontSize = 11.5, Foreground = Br("LgInk3"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
                Grid.SetColumn(l, 1); Grid.SetColumn(k, 2);
                g.Children.Add(l); g.Children.Add(k);
                b.Content = g; b.Background = ClearBrush(); b.Foreground = Br("LgInk");
                b.PointerEntered += (_, _) => b.Background = Br("LgHover");
                b.PointerExited += (_, _) => b.Background = ClearBrush();
                b.Click += (_, _) => { CloseAll(false, "Open Task Manager"); OpenTaskMgr(); };
                DragPick(b, () => { CloseAll(false, "Open Task Manager"); OpenTaskMgr(); });
                AutomationProperties.SetAutomationId(b, id); AutomationProperties.SetName(b, r.Text);
                return (b, (null, null, r, null));
            }
        }
    }

    static double CoreH(double pct) => Math.Round(26 * Math.Max(0, Math.Min(100, pct)) / 100, 1);

    /// One kept row against this sample's: only what changed is set. Returns how many words / bars changed.
    int Update((TextBlock? A, TextBlock? B, StatRow Row, List<(Border Fill, Border Bar)>? Cores) e, StatRow r)
    {
        int n = 0;
        if (r.Kind == StatRowKind.Cores && e.Cores != null && r.Cores != null)
        {
            for (int k = 0; k < e.Cores.Count && k < r.Cores.Count; k++)
            {
                if (e.Row.Cores![k] == r.Cores[k]) continue;
                e.Cores[k].Fill.Height = CoreH(r.Cores[k]);
                string p = TopBarModel.Pct(r.Cores[k]);
                ToolTipService.SetToolTip(e.Cores[k].Bar, p); AutomationProperties.SetName(e.Cores[k].Bar, p);
                n++;
            }
            return n;
        }
        if (e.A != null && e.Row.Text != r.Text)
        {
            e.A.Text = r.Kind == StatRowKind.Head ? Upper(r.Text) : r.Text;
            if (r.Kind is StatRowKind.Head or StatRowKind.Info) AutomationProperties.SetName(e.A, r.Text);
            n++;
        }
        if (r.Kind == StatRowKind.Info && e.A != null && e.Row.Bad != r.Bad) { e.A.Foreground = Br(r.Bad ? "LgWarn" : "LgInk3"); n++; }
        if (e.B != null && e.Row.Value != r.Value && e.B.Inlines.FirstOrDefault() is Microsoft.UI.Xaml.Documents.Run run) { run.Text = r.Value ?? ""; n++; }
        if (r.Kind == StatRowKind.Row && (e.Row.Text != r.Text || e.Row.Value != r.Value) && e.A?.Parent is Grid g) AutomationProperties.SetName(g, r.Text + " " + r.Value);
        return n;
    }

    /// TOP-108/109 openTaskMgr: Mantle's own Task Manager first; when it does not open, Windows' (a dry run in --test),
    /// and the strip says why.
    async void OpenTaskMgr()
    {
        var r = await _ctx.CallAsync("taskmgr.open", new JsonObject { ["view"] = "apps" });
        bool ok = r.Ok && !(r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var k) && k.ValueKind == JsonValueKind.False);
        if (ok) { _ctx.Log.Line("task manager: Mantle's opened"); return; }
        string why = (r.Ok && r.Result.TryGetProperty("reason", out var rv) ? rv.GetString() : r.Error) ?? "it did not say why";
        var x = await _ctx.CallAsync("topbar.taskmgr");
        bool xok = x.Ok && !(x.Result.ValueKind == JsonValueKind.Object && x.Result.TryGetProperty("ok", out var xk) && xk.ValueKind == JsonValueKind.False);
        string? reason = x.Ok ? (x.Result.ValueKind == JsonValueKind.Object && x.Result.TryGetProperty("reason", out var xr) ? xr.GetString() : null) : x.Error;
        bool dry = x.Ok && x.Result.ValueKind == JsonValueKind.Object && x.Result.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
        string note = TopBarStatMenu.TaskMgrNote(why, xok, reason, dry);
        _ctx.Log.Line("task manager: " + note);
        SetNote(note);
    }
}
