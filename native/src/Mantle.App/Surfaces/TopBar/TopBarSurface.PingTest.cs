using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.TopBar;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.TopBar;

// The ping test in the PING dropdown (TOP-139..159): ui/topbar/pingtest.js's drawing. What it says is Mantle.Core's
// PingTestPanel.Rows; this half draws those rows at the end of the PING dropdown and hands row presses and the name
// box's keys back to the panel. A push while the dropdown is open on PING redraws it (Electron rewrote its HTML); closed,
// nothing is drawn and no timer runs. AutomationIds: topbar.pt.tab.test / .history, topbar.pt.<act verb> for each action
// row, topbar.pt.name (the name box), topbar.pt.table / topbar.pt.compare, topbar.pt.h.<n> (history rows),
// topbar.pt.chip.<n>, topbar.pt.info.<n>; an amber cell or line has the ItemStatus "amber".
internal sealed partial class TopBarSurface
{
    PingTestPanel? _pt;
    TextBox? _ptBox;

    void InstallPingTest()
    {
        _pt = new PingTestPanel(PtCall, PtRerender, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _subs.Add(_ctx.On<JsonElement>("topbar.pingtest", s => _pt.OnRun(JsJson.FromElement(s) as JsonObject)));
        _subs.Add(_ctx.On<JsonElement>("topbar.pingtests", v => _pt.OnHistory(JsJson.FromElement(v) as JsonObject)));
        _ = _pt.Init();   // once at load: the last run and the history (no timer)
    }

    async Task<JsonObject> PtCall(string verb, JsonObject args)
    {
        var r = await _ctx.CallAsync(verb, args);
        if (_test) _ctx.Log.Line($"ping test: {verb} {args["action"]} ({(r.Ok ? "answered" : "failed: " + r.Error)})");
        if (!r.Ok) return new JsonObject { ["ok"] = false, ["reason"] = r.Error ?? "Mantle did not answer" };
        return JsJson.FromElement(r.Result) as JsonObject ?? new JsonObject();
    }

    void PtRerender()
    {
        if (_open?.Stat == "ping") RenderMenus();
    }

    /// The ping test's rows for the open PING dropdown, and their signature (a changed one redraws the dropdown).
    (IReadOnlyList<PtRow> Rows, string Sig) PingRows(TopBarMenu o)
    {
        if (o.Stat != "ping" || _pt == null) return (Array.Empty<PtRow>(), "");
        var rows = _pt.Rows();
        return (rows, PingTestPanel.Sig(rows));
    }

    /// TOP-091: keys typed into the ping test's name box are the box's; Enter saves and Escape leaves it (the dropdown stays).
    bool PingKey(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e, string? name)
    {
        if (_pt?.Edit == null || _ptBox == null || e.OriginalSource != _ptBox) return false;
        if (name is "Enter" or "Escape")
        {
            _pt.Typed(_ptBox.Text, _ptBox.SelectionStart, _ptBox.SelectionStart + _ptBox.SelectionLength);
            _pt.Key(name, out _);
            _ctx.Log.Line($"ping test name box: {name}");
            e.Handled = true;
        }
        return true;
    }

    void AfterPingRender()
    {
        if (_ptBox == null || _pt?.Edit is not { } ed) return;
        _ptBox.Focus(FocusState.Programmatic);
        int a = Math.Clamp(ed.A ?? _ptBox.Text.Length, 0, _ptBox.Text.Length), b = Math.Clamp(ed.B ?? _ptBox.Text.Length, a, _ptBox.Text.Length);
        _ptBox.Select(a, b - a);
    }

    static void Amber(FrameworkElement el, bool on) { if (on) AutomationProperties.SetItemStatus(el, "amber"); }

    string InkFor(string cls)
    {
        var c = cls.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (c.Contains("bad") || c.Contains("worse") || c.Contains("hi")) return "LgWarn";
        if (c.Contains("hid") || c.Contains("wait") || c.Contains("h")) return "LgInk3";
        if (c.Contains("better") || c.Contains("rt")) return "LgInk";
        if (c.Contains("step") || c.Contains("verdict") || c.Contains("net") || c.Contains("link")) return "LgInk2";
        return "LgInk3";
    }

    Button PtButton(string act, double height, Thickness pad, CornerRadius radius)
    {
        var b = (Button)XamlReader.Load($"<Button {Ns} Padding='0' HorizontalAlignment='Stretch' HorizontalContentAlignment='Stretch' ClickMode='Release' IsTabStop='False' UseSystemFocusVisuals='False'>"
            + "<Button.Template><ControlTemplate TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='{TemplateBinding CornerRadius}' Padding='{TemplateBinding Padding}'>"
            + "<ContentPresenter Content='{TemplateBinding Content}' Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center' HorizontalAlignment='Stretch'/></Border></ControlTemplate></Button.Template></Button>");
        if (height > 0) b.Height = height;
        b.Padding = pad; b.CornerRadius = radius; b.Background = ClearBrush(); b.Foreground = Br("LgInk");
        b.Click += (_, _) => { _ctx.Log.Line($"ping test row: {act}"); _ = _pt!.Act(act); };
        DragPick(b, () => { _ctx.Log.Line($"ping test row: {act}"); _ = _pt!.Act(act); });
        return b;
    }

    TextBlock PtText(string text, string ink, double size, bool bold = false, bool right = false, bool italic = false) => new()
    {
        Text = text, FontFamily = TextFont, FontSize = size, Foreground = Br(ink), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
    };

    Border Badge(string text)
    {
        var t = new TextBlock { Text = text, FontFamily = TextFont, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = Br("LgAcc") };
        return new Border { Child = t, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(4, 0, 0, 0), CornerRadius = new CornerRadius(99), Background = Br("LgAccSoft"), VerticalAlignment = VerticalAlignment.Center };
    }

    /// One ping test row's element.
    FrameworkElement PingRowView(PtRow r, int i)
    {
        double size = _dark ? 13 : 12.5;
        switch (r.Kind)
        {
            case PtKind.Sep:
                return new Border { Height = 1, Margin = _dark ? new Thickness(6, 4, 6, 4) : new Thickness(8, 4, 8, 4), Background = Br(_dark ? "LgDivider" : "LgSunk") };
            case PtKind.Head:
            {
                var (el, _) = StatRowView(new StatRow(StatRowKind.Head, r.Text), 1000 + i);
                AutomationProperties.SetAutomationId(el, "topbar.pt.head." + i);
                return el;
            }
            case PtKind.Info:
            {
                bool bad = r.Cls.Split(' ').Contains("bad");
                var t = Info(r.Text, bad, "topbar.pt.info." + i);
                if (!bad) t.Foreground = Br(InkFor(r.Cls));
                if (r.Cls.Split(' ').Contains("hid")) t.FontStyle = Windows.UI.Text.FontStyle.Italic;
                if (r.Tip is { Length: > 0 }) ToolTipService.SetToolTip(t, r.Tip);
                Amber(t, bad || r.Cls.Split(' ').Contains("worse"));
                return t;
            }
            case PtKind.Progress:
            {
                // .pt-prog: 4 px on --lg-sunk, the done part in the accent
                var g = new Grid { Height = 4, Margin = new Thickness(8, 2, 8, 4), CornerRadius = new CornerRadius(2), Background = Br("LgSunk") };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(r.Progress, 0.0001), GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - r.Progress, 0.0001), GridUnitType.Star) });
                g.Children.Add(new Border { Background = Br("LgAcc"), CornerRadius = new CornerRadius(2) });
                AutomationProperties.SetAutomationId(g, "topbar.pt.progress");
                AutomationProperties.SetName(g, $"{Math.Round(r.Progress * 100)}%");
                return g;
            }
            case PtKind.Table:
            {
                // .pt-t / .pt-c: the place, then four right-aligned figures; tabular numbers
                var g = new Grid { ColumnSpacing = r.Text == "compare" ? 10 : 12, RowSpacing = 1, Padding = new Thickness(8, 2, 8, 4) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int c = 0; c < 4; c++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                int row = -1, col = 5;
                var cells = r.Cells ?? Array.Empty<PtCell>();
                for (int k = 0; k < cells.Count; k++)
                {
                    var cell = cells[k];
                    if (col >= 5) { row++; col = 0; g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); }
                    bool head = cell.Cls == "h";
                    FrameworkElement el;
                    var t = PtText(cell.Text, head ? "LgInk3" : col == 0 && cell.Cls != "rt" ? "LgInk" : InkFor(cell.Cls == "" ? "step" : cell.Cls), head ? 11 : size,
                        bold: cell.Cls == "rt", right: col > 0 && !(cell.Span > 1));
                    if (col > 0 && cell.Span > 1) t.TextAlignment = TextAlignment.Left;
                    if (head && col == 0) t.TextAlignment = TextAlignment.Left;
                    if (!head && col > 0) t.Inlines.Clear();
                    if (!head && col > 0) t.Inlines.Add(Tabular(cell.Text));
                    if (cell.Badge != null)
                    {
                        var sp = new StackPanel { Orientation = Orientation.Horizontal };
                        sp.Children.Add(t); sp.Children.Add(Badge(cell.Badge));
                        el = sp;
                    }
                    else el = t;
                    if (cell.Tip is { Length: > 0 }) ToolTipService.SetToolTip(el, cell.Tip);
                    AutomationProperties.SetAutomationId(el, $"topbar.pt.c{row}.{col}");
                    AutomationProperties.SetName(el, cell.Text + (cell.Badge != null ? " " + cell.Badge : ""));
                    Amber(el, cell.Cls is "hi" or "worse");
                    Grid.SetRow(el, row); Grid.SetColumn(el, col);
                    if (cell.Span > 1) Grid.SetColumnSpan(el, cell.Span);
                    g.Children.Add(el);
                    col += Math.Max(1, cell.Span);
                }
                AutomationProperties.SetAutomationId(g, r.Text == "compare" ? "topbar.pt.compare" : "topbar.pt.table");
                return g;
            }
            case PtKind.Row2:
            {
                var (el, entry) = StatRowView(new StatRow(StatRowKind.Row, r.Text, r.Value), 1000 + i);
                if (r.Cls == "hi" && entry.Item2 is { } x) { x.Foreground = Br("LgWarn"); Amber(el, true); }
                AutomationProperties.SetAutomationId(el, "topbar.pt.speed." + r.Text.ToLowerInvariant());
                return el;
            }
            case PtKind.Go:
            {
                // .pt-go: the label in the accent (quiet: ink-2, armed: warn), its key text on the right
                var b = PtButton(r.Act!, 24, new Thickness(6, 0, 8, 0), new CornerRadius(6));
                var g = new Grid { ColumnSpacing = 6 };
                foreach (var wd in new[] { new GridLength(16), new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(12) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = wd });
                bool quiet = r.Cls == "quiet", armed = r.Cls == "armed";
                var l = PtText(r.Text, armed ? "LgWarn" : quiet ? "LgInk2" : "LgAcc", size, bold: !quiet && !armed);
                var k = PtText(r.Value ?? "", "LgInk3", 11.5); k.Margin = new Thickness(18, 0, 0, 0);
                Grid.SetColumn(l, 1); Grid.SetColumn(k, 2);
                g.Children.Add(l); g.Children.Add(k);
                b.Content = g;
                b.PointerEntered += (_, _) => b.Background = Br("LgHover");
                b.PointerExited += (_, _) => b.Background = ClearBrush();
                string verb = r.Act!.Split(':')[0];
                AutomationProperties.SetAutomationId(b, "topbar.pt." + verb);
                AutomationProperties.SetName(b, r.Text);
                Amber(b, armed);
                return b;
            }
            case PtKind.NameBox:
            {
                // .pt-name: 22 px, accent border, max 60; its text, caret and focus survive every redraw (TOP-154)
                var box = new TextBox
                {
                    Text = r.Value ?? "", PlaceholderText = r.Text, MaxLength = PingTestHistory.NameMax, Height = 22, MinHeight = 22, Padding = new Thickness(6, 1, 6, 0),
                    FontFamily = TextFont, FontSize = size, BorderThickness = new Thickness(1), BorderBrush = Br("LgAcc"), Background = Br("LgFillStrong"),
                    Foreground = Br("LgInk"), IsSpellCheckEnabled = false, Margin = new Thickness(8, 2, 8, 4),
                };
                box.TextChanged += (_, _) => _pt?.Typed(box.Text, box.SelectionStart, box.SelectionStart + box.SelectionLength);
                box.SelectionChanged += (_, _) => _pt?.Typed(box.Text, box.SelectionStart, box.SelectionStart + box.SelectionLength);
                AutomationProperties.SetAutomationId(box, "topbar.pt.name");
                AutomationProperties.SetName(box, "Test name");
                _ptBox = box;
                return box;
            }
            case PtKind.Tabs:
            case PtKind.Chips:
            {
                // .pt-tabs: a pill on --lg-sunk holding two pills (the chosen one --lg-fill-strong); .pt-chips: tiles (chosen: accent soft)
                bool tabs = r.Kind == PtKind.Tabs;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = tabs ? 2 : 4 };
                var cells = r.Cells ?? Array.Empty<PtCell>();
                for (int k = 0; k < cells.Count; k++)
                {
                    var c = cells[k];
                    bool on = c.Cls == "on";
                    var b = PtButton(c.Act!, 20, new Thickness(10, 0, 10, 0), new CornerRadius(10));
                    b.HorizontalAlignment = HorizontalAlignment.Left;
                    var t = PtText(c.Text, on ? (tabs ? "LgInk" : "LgAcc") : "LgInk2", 11.5, bold: on);
                    if (!tabs) t.MaxWidth = 130;
                    b.Content = t;
                    var rest = on ? Br(tabs ? "LgFillStrong" : "LgAccSoft") : tabs ? ClearBrush() : Br("LgTile");
                    b.Background = rest;
                    if (!on) { b.PointerEntered += (_, _) => t.Foreground = Br("LgInk"); b.PointerExited += (_, _) => t.Foreground = Br("LgInk2"); }
                    if (c.Tip is { Length: > 0 }) ToolTipService.SetToolTip(b, c.Tip);
                    AutomationProperties.SetAutomationId(b, tabs ? "topbar.pt.tab." + c.Act!.Split(':')[1] : "topbar.pt.chip." + k);
                    AutomationProperties.SetName(b, c.Text);
                    if (on) AutomationProperties.SetItemStatus(b, "on");
                    sp.Children.Add(b);
                }
                if (!tabs) return new Border { Child = sp, Padding = new Thickness(8, 0, 8, 6) };
                return new Border { Child = sp, Padding = new Thickness(2), Margin = new Thickness(8, 2, 8, 6), CornerRadius = new CornerRadius(12), Background = Br("LgSunk"), HorizontalAlignment = HorizontalAlignment.Left };
            }
            default:
            {
                // .pt-hrow: name (bold + "saved" when saved) or date, the network and short link, the best average, the speeds
                var cells = r.Cells!;
                var b = PtButton(r.Act!, 0, new Thickness(8, 2, 8, 2), new CornerRadius(6));
                var g = new Grid { ColumnSpacing = 10 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 170 });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = PtText(cells[0].Text, "LgInk", size, bold: cells[0].Cls == "b");
                FrameworkElement name = n;
                if (cells[0].Badge != null) { var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(n); sp.Children.Add(Badge(cells[0].Badge!)); name = sp; }
                var w = PtText(cells[1].Text, cells[1].Cls == "hid" ? "LgInk3" : "LgInk2", size, italic: cells[1].Cls == "hid");
                if (cells[1].Tip is { Length: > 0 }) ToolTipService.SetToolTip(w, cells[1].Tip);
                var avg = PtText("", cells[2].Cls == "hi" ? "LgWarn" : "LgInk2", size, right: true); avg.Inlines.Add(Tabular(cells[2].Text));
                var s = PtText("", "LgInk3", 11.5, right: true); s.Inlines.Add(Tabular(cells[3].Text));
                Grid.SetColumn(w, 1); Grid.SetColumn(avg, 2); Grid.SetColumn(s, 3);
                g.Children.Add(name); g.Children.Add(w); g.Children.Add(avg); g.Children.Add(s);
                b.Content = g;
                b.PointerEntered += (_, _) => b.Background = Br("LgHover");
                b.PointerExited += (_, _) => b.Background = ClearBrush();
                if (r.Tip is { Length: > 0 }) ToolTipService.SetToolTip(b, r.Tip);
                AutomationProperties.SetAutomationId(b, "topbar.pt.h." + i);
                AutomationProperties.SetName(b, string.Join(" | ", cells.Select(c => c.Text + (c.Badge != null ? " " + c.Badge : ""))));
                Amber(b, cells[2].Cls == "hi");
                return b;
            }
        }
    }
}
