using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Corners;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Mantle.App.Surfaces.Desktop;

/// <summary>
/// Quick settings' Hot corners row (ui/bar/corners.js + corners.css .hcrow, CORN-061..085): the header ("Hot corners"
/// and a sub-caption), a 2x2 grid of corner tiles laid out like the screen, one corner's choices open below it as a wrap
/// of chips, and the lines under it. Words come from <see cref="CornerRow"/>; it reads <c>corners.state</c> on each open,
/// hears its push only while the panel is open, and saves through <c>corners.set {corner, action, via:"quick settings"}</c>.
/// Esc with a corner's choices open closes the choices only and puts focus back on its tile.
/// Ids: qs.corners, qs.corners.sub, qs.corners.grid, qs.corners.tile.&lt;c&gt;, qs.corners.pick, qs.corners.chip.&lt;id&gt;,
/// qs.corners.why, qs.corners.line.&lt;i&gt;. Log: <c>qs: hot corners row: ...</c>.
/// </summary>
internal sealed class CornersRow
{
    readonly Func<string, Brush> _b;
    readonly Action<string> _log;
    readonly Action _refit;
    readonly FontFamily _font;
    SurfaceLink? _link;
    IDisposable? _sub;
    JsonElement? _st;
    CornerRowView _v = CornerRow.View(null);
    string? _open, _busy, _note;
    bool _dark, _alive;

    public readonly StackPanel Root = new() { Margin = new Thickness(0, 2, 0, 9) };
    readonly TextBlock _head, _subText;
    readonly Grid _grid = new() { ColumnSpacing = 4, RowSpacing = 4 };
    readonly Dictionary<string, (Button B, TextBlock Cap, TextBlock Act)> _tiles = new(StringComparer.Ordinal);
    readonly ChipWrap _pick = new() { Gap = 4, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _why = new() { Margin = new Thickness(0, 6, 0, 0), Spacing = 2 };

    public CornersRow(Func<string, Brush> brush, FontFamily font, Action<string> log, Action refit)
    {
        _b = brush; _font = font; _log = log; _refit = refit;
        AutomationProperties.SetAutomationId(Root, "qs.corners");
        AutomationProperties.SetName(Root, CornerRow.Head);
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _head = Text(CornerRow.Head, 12.5);
        _subText = Text("", 12.5);
        _subText.HorizontalAlignment = HorizontalAlignment.Right; _subText.TextTrimming = TextTrimming.CharacterEllipsis; _subText.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetAutomationId(_subText, "qs.corners.sub");
        Grid.SetColumn(_subText, 1);
        head.Children.Add(_head); head.Children.Add(_subText);
        Root.Children.Add(head);

        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AutomationProperties.SetAutomationId(_grid, "qs.corners.grid");
        AutomationProperties.SetName(_grid, CornerRow.GroupName);
        int i = 0;
        foreach (var c in CornerRules.Corners) {
            bool right = c[1] == 'r';
            var cap = Text(CornerRow.Label(c), 10); cap.TextTrimming = TextTrimming.CharacterEllipsis;
            var act = Text("", 12); act.TextTrimming = TextTrimming.CharacterEllipsis;
            var face = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            face.Children.Add(cap); face.Children.Add(act);
            if (right) { cap.HorizontalAlignment = act.HorizontalAlignment = HorizontalAlignment.Right; face.HorizontalAlignment = HorizontalAlignment.Right; }
            var b = MakeButton("qs.corners.tile." + c, CornerRow.Label(c), face);
            b.Height = 36; b.Padding = new Thickness(10, 0, 10, 0);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            string corner = c;
            b.Click += (_, _) => {
                if (_v.Dead) return;
                _open = _open == corner ? null : corner; _note = null;
                _log($"qs: hot corners row: {corner} {(_open == corner ? "open" : "closed")}");
                Paint(); _refit();
            };
            Grid.SetColumn(b, i % 2); Grid.SetRow(b, i / 2);
            _grid.Children.Add(b);
            _tiles[c] = (b, cap, act);
            i++;
        }
        Root.Children.Add(_grid);
        AutomationProperties.SetAutomationId(_pick, "qs.corners.pick");
        Root.Children.Add(_pick);
        AutomationProperties.SetAutomationId(_why, "qs.corners.why");
        Root.Children.Add(_why);
        // Esc closes the open corner's choices first (the panel on the next Esc) and puts focus back on its corner
        Root.KeyDown += (_, e) => {
            if (e.Key != Windows.System.VirtualKey.Escape || _open == null) return;
            e.Handled = true;
            var back = _tiles[_open].B;
            _log($"qs: hot corners row: Esc closed the {_open} choices");
            _open = null; Paint(); _refit();
            back.Focus(FocusState.Keyboard);
        };
        Paint();
    }

    public void Attach(SurfaceLink link) => _link = link;

    /// The panel opens: hear the pushes while it is open, and a fresh read now.
    public async Task Opened(bool dark)
    {
        _dark = dark; _alive = true; _note = null; _open = null; _busy = null;
        _sub?.Dispose();
        if (_link != null) _sub = _link.On<JsonElement>("corners.state", e => Take(e));
        Paint();
        if (_link == null) { Take(Err("this bar has no road to Mantle's commands")); return; }
        var r = await _link.CallAsync("corners.state");
        Take(r.Ok && r.Result.ValueKind == JsonValueKind.Object ? r.Result : Err(r.Error ?? "no answer"));
    }

    /// The panel closed: the pushes stop (CORN-079); the next open reads afresh.
    public void Closed() { _alive = false; _sub?.Dispose(); _sub = null; _open = null; }

    public void Theme(bool dark) { _dark = dark; Paint(); }

    static JsonElement Err(string why) => JsonDocument.Parse(new JsonObject { ["error"] = why }.ToJsonString()).RootElement.Clone();

    void Take(JsonElement st)
    {
        if (!_alive || st.ValueKind != JsonValueKind.Object) return;
        _st = st.Clone();
        Paint(); _refit();
    }

    async Task Choose(string corner, string action)
    {
        if (_v.Dead || _busy != null || _link == null) return;
        _busy = corner; _note = null; Paint();
        var r = await _link.CallAsync("corners.set", new JsonObject { ["corner"] = corner, ["action"] = action, ["via"] = "quick settings" });
        _busy = null;
        if (!r.Ok) _note = CornerRow.SaveNote(corner, r.Error, answered: false);
        else if (r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            _note = CornerRow.SaveNote(corner, r.Result.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString() : null, answered: true);
        else _open = null;
        _log($"qs: hot corners row: set {corner} to {action}: {(_note == null ? "saved" : "not saved: " + _note)}");
        if (r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object) Take(st);
        else { Paint(); _refit(); }
    }

    void Paint()
    {
        var v = _v = CornerRow.View(_st);
        _head.Foreground = _b("LgInk");
        _subText.Text = v.Sub;
        _subText.Foreground = _b("LgInk2");
        ToolTipService.SetToolTip(_subText, v.Sub);
        if (v.Dead) _open = null;
        foreach (var c in CornerRules.Corners) {
            var (b, cap, act) = _tiles[c];
            var cell = v.Cells.Count == 4 ? v.Cells[CornerIndex(c)] : null;
            bool open = _open == c;
            act.Text = v.Dead ? (v.Loading ? "" : "—") : _busy == c ? "Saving…" : cell!.ActionLabel;
            b.IsEnabled = !v.Dead;
            cap.FontSize = _dark ? 11 : 10;
            act.FontSize = _dark ? 13 : 12;
            cap.Foreground = _b("LgInk3");
            act.Foreground = v.Dead ? _b("LgInk3") : cell!.Failed ? _b("LgWarn") : cell.Off ? _b("LgInk2") : _b("LgInk");
            b.Background = v.Dead || (cell?.Off ?? true) ? _b("LgSunk") : open && _dark ? _b("LgPress") : _b("LgTile");
            b.BorderThickness = new Thickness(open ? 1 : 0);
            b.BorderBrush = open ? (_dark ? _b("LgEdgeStrong") : _b("LgAcc")) : null;
            string tip = cell?.Tip ?? $"{CornerRow.Label(c)} corner. Click to change.";
            ToolTipService.SetToolTip(b, tip);
            AutomationProperties.SetName(b, $"{CornerRow.Label(c)} corner: {act.Text}");
            AutomationProperties.SetHelpText(b, tip);
            AutomationProperties.SetItemStatus(b, (open ? "expanded" : "collapsed") + (cell?.Failed == true ? " failed" : "") + (cell?.Off == true ? " off" : ""));
        }
        // the choices for the open corner
        _pick.Children.Clear();
        var cur = _open != null && v.Cells.Count == 4 ? v.Cells[CornerIndex(_open)] : null;
        _pick.Visibility = cur != null ? Visibility.Visible : Visibility.Collapsed;
        if (cur != null)
            foreach (var (id, label) in v.Choices) {
                bool now = id == cur.Action;
                var t = Text(label, _dark ? 12 : 11);
                t.Foreground = now ? (_dark ? _b("LgInk") : _b("LgAccOn")) : _b("LgInk");
                var chip = MakeButton("qs.corners.chip." + id, label, t);
                chip.Height = 24; chip.Padding = new Thickness(10, 0, 10, 0); chip.CornerRadius = new CornerRadius(12);
                chip.Background = now ? (_dark ? _b("LgAccSoft") : _b("LgAcc")) : _b("LgTile");
                chip.BorderThickness = new Thickness(_dark ? 1 : 0);
                chip.BorderBrush = now ? _b("LgAccLine") : _b("LgEdgeIn");
                chip.IsEnabled = _busy == null;
                string tip = CornerRow.ChipTip(cur, label, now);
                ToolTipService.SetToolTip(chip, tip);
                AutomationProperties.SetHelpText(chip, tip);
                AutomationProperties.SetItemStatus(chip, now ? "pressed" : "");
                string corner = cur.Corner, choice = id;
                chip.Click += (_, _) => {
                    if (now) { _open = null; _log($"qs: hot corners row: {corner} closed (its own choice)"); Paint(); _refit(); }
                    else _ = Choose(corner, choice);
                };
                _pick.Children.Add(chip);
            }
        // the lines under it
        _why.Children.Clear();
        var lines = new List<CornerLine>();
        if (_note != null) lines.Add(new CornerLine(_note, true));
        lines.AddRange(v.Lines);
        _why.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < lines.Count; i++) {
            var t = Text(lines[i].Text, 12); t.TextWrapping = TextWrapping.Wrap; t.LineHeight = 16.8;
            t.Foreground = lines[i].Warn ? _b("LgWarn") : _b("LgInk2");
            AutomationProperties.SetAutomationId(t, "qs.corners.line." + i);
            AutomationProperties.SetName(t, lines[i].Text);
            AutomationProperties.SetItemStatus(t, lines[i].Warn ? "warn" : "plain");
            _why.Children.Add(t);
        }
        AutomationProperties.SetItemStatus(Root, v.Loading ? "loading" : v.Dead ? "dead" : v.Warn ? "warn" : "ok");
    }

    static int CornerIndex(string c) => c switch { "tl" => 0, "tr" => 1, "bl" => 2, _ => 3 };

    TextBlock Text(string s, double size) => new() { Text = s, FontSize = size, FontFamily = _font };

    static Style? _style;
    /// .hccell / .hcchip: a flat face in the element's own Background, the hover and press tokens over it.
    static Button MakeButton(string id, string name, UIElement face)
    {
        _style ??= (Style)XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Setter Property='CornerRadius' Value='6'/><Setter Property='UseSystemFocusVisuals' Value='True'/>" +
            "<Setter Property='VerticalContentAlignment' Value='Center'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
            "<Grid Background='{TemplateBinding Background}' CornerRadius='{TemplateBinding CornerRadius}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>" +
            "<Border x:Name='Over' CornerRadius='{TemplateBinding CornerRadius}'/>" +
            "<ContentPresenter x:Name='Face' Padding='{TemplateBinding Padding}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}'/>" +
            "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'><VisualState x:Name='Normal'/>" +
            "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgHover}'/></VisualState.Setters></VisualState>" +
            "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgPress}'/></VisualState.Setters></VisualState>" +
            "<VisualState x:Name='Disabled'><VisualState.Setters><Setter Target='Face.Opacity' Value='{ThemeResource LgDisabled}'/></VisualState.Setters></VisualState></VisualStateGroup></VisualStateManager.VisualStateGroups>" +
            "</Grid></ControlTemplate></Setter.Value></Setter></Style>");
        var b = new Button { Style = _style, Content = face };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, name);
        return b;
    }

    public void Dispose() { _sub?.Dispose(); _sub = null; }
}

/// <summary>The chips' wrap (.hcpick: flex-wrap, gap 4): left to right, a new line when the next chip does not fit.</summary>
internal sealed partial class ChipWrap : Panel
{
    public double Gap { get; set; } = 4;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, line = 0, w = 0;
        foreach (var c in Children) {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > available.Width) { y += line + Gap; x = 0; line = 0; }
            x += d.Width + Gap; line = Math.Max(line, d.Height); w = Math.Max(w, x - Gap);
        }
        return new Size(double.IsInfinity(available.Width) ? w : available.Width, Children.Count == 0 ? 0 : y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var c in Children) {
            var d = c.DesiredSize;
            if (x > 0 && x + d.Width > final.Width) { y += line + Gap; x = 0; line = 0; }
            c.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width + Gap; line = Math.Max(line, d.Height);
        }
        return final;
    }
}
