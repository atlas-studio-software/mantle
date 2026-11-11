using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.GameMode;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Bar.GameMode;

/// <summary>
/// The Game mode row in the quick-settings panel (ui/bar/gamemode.js + gamemode.css .gmrow): the header ("Game mode" and
/// ONE status on the right), a switch that says what a click does ("Turn on" / "Turn off", the track shows the state)
/// with "Details" beside it, one line under it only when something needs reading (warning ink for a failure), and,
/// opened, what game mode changed or would change plus its three options. It reads <c>gamemode.state</c> (and its push)
/// and calls <c>gamemode.set</c> / <c>gamemode.auto</c> / <c>gamemode.liftSaver</c> / <c>gamemode.boost</c>. Words come
/// from <see cref="GameModeView"/>. Ids: qs.gamemode, qs.gamemode.sub, .switch, .more, .why, .list, .row.&lt;key&gt;,
/// .last, .opt.&lt;key&gt;. Log: <c>qs: game mode row: switched on|off (...)</c>, <c>qs: game mode row: details open|closed</c>.
/// </summary>
internal sealed class GameModeRow
{
    readonly Func<string, Brush> _b;
    readonly Action<string> _log;
    readonly Action _refit;
    readonly Func<(bool Ok, string Words)>? _takeKeys;
    readonly FontFamily _font;
    SurfaceLink? _link;
    IDisposable? _sub;
    JsonObject? _st;
    bool _open, _inflight, _dark;
    long _lastClick = long.MinValue / 2;
    string? _note;

    public readonly StackPanel Root = new() { Margin = new Thickness(0, 2, 0, 9) };
    readonly TextBlock _head, _sub2, _swText, _moreText, _why;
    readonly Button _sw, _more;
    readonly Border _track, _knob;
    readonly Canvas _chev = new() { Width = 8, Height = 8 };
    readonly Microsoft.UI.Xaml.Shapes.Polyline _chevLine = new() { StrokeThickness = 1.5, Points = { new(1, 2), new(4, 5), new(7, 2) } };
    readonly Border _listBox;
    readonly StackPanel _list = new() { Spacing = 2 }, _opts = new() { Margin = new Thickness(0, 4, 0, 0) };
    readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.Ordinal);

    /// <param name="takeKeys">A --test panel only (MANTLE_TEST_QS_KEYS=1): Details opened by a click takes the keyboard, so
    /// a test can press Esc in it (a live panel has the keyboard already).</param>
    public GameModeRow(Func<string, Brush> brush, FontFamily font, Action<string> log, Action refit, Func<(bool Ok, string Words)>? takeKeys = null)
    {
        _b = brush; _font = font; _log = log; _refit = refit; _takeKeys = takeKeys;
        AutomationProperties.SetAutomationId(Root, "qs.gamemode");
        AutomationProperties.SetName(Root, "Game mode");
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _head = Text("Game mode", 12.5);
        _sub2 = Text("", 12.5); _sub2.HorizontalAlignment = HorizontalAlignment.Right; _sub2.TextTrimming = TextTrimming.CharacterEllipsis; _sub2.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_sub2, 1);
        AutomationProperties.SetAutomationId(_sub2, "qs.gamemode.sub");
        head.Children.Add(_head); head.Children.Add(_sub2);
        Root.Children.Add(head);

        var line = new Grid { ColumnSpacing = 4 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _knob = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3, 3, 0, 0) };
        _track = new Border { Width = 32, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Child = _knob };
        _swText = Text("Turn on", 12); _swText.VerticalAlignment = VerticalAlignment.Center; _swText.TextTrimming = TextTrimming.CharacterEllipsis;
        var swFace = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        swFace.Children.Add(_track); swFace.Children.Add(_swText);
        _sw = MakeButton("qs.gamemode.switch", "Game mode: Turn on", swFace);
        _sw.HorizontalAlignment = HorizontalAlignment.Stretch; _sw.HorizontalContentAlignment = HorizontalAlignment.Left;
        _sw.Click += (_, _) => _ = Switch();
        _moreText = Text("Details", 12); _moreText.VerticalAlignment = VerticalAlignment.Center;
        _chev.Children.Add(_chevLine);
        var moreFace = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        moreFace.Children.Add(_moreText); moreFace.Children.Add(_chev);
        _more = MakeButton("qs.gamemode.more", "Details", moreFace);
        ToolTipService.SetToolTip(_more, "What game mode changes, and its two options");
        _more.Click += (_, _) => { _open = !_open; _log($"qs: game mode row: details {(_open ? "open" : "closed")}"); Paint(); if (_open) TestKeys(); };
        // BAR-815: Esc inside the open Details closes it, gives the focus back to Details, and leaves quick settings open
        // (handled here, so the pop-up's own Esc, on its root, never hears it)
        Root.KeyDown += (_, e) =>
        {
            if (e.Key != global::Windows.System.VirtualKey.Escape || !_open) return;
            e.Handled = true;
            _open = false;
            Paint(); _refit();
            bool back = _more.Focus(FocusState.Keyboard);
            _log($"qs: game mode row: details closed (Esc); the focus is back on Details: {(back ? "yes" : "no")}");
        };
        Grid.SetColumn(_more, 1);
        line.Children.Add(_sw); line.Children.Add(_more);
        Root.Children.Add(line);

        _why = Text("", 12); _why.Margin = new Thickness(0, 5, 0, 0); _why.TextWrapping = TextWrapping.Wrap; _why.LineHeight = 16.8;
        AutomationProperties.SetAutomationId(_why, "qs.gamemode.why");
        Root.Children.Add(_why);
        _listBox = new Border { Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(4), CornerRadius = new CornerRadius(6), Child = new ScrollViewer { MaxHeight = 212, Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        AutomationProperties.SetAutomationId(_listBox, "qs.gamemode.list");
        Root.Children.Add(_listBox);
        Opt("auto", "Turn on by itself for games", "gamemode.auto", "Game mode starts when a known game starts and ends when it closes");
        Opt("liftSaver", "Lift Energy saver while gaming on battery", "gamemode.liftSaver", "Energy saver forces Best power efficiency; this lifts it for the game and puts it back after");
        Opt("boost", "Boost: free memory and power for the game", "gamemode.boost", "Background apps get Efficiency mode, and a minute in, idle ones hand their unused memory back to Windows once. Nothing is ever closed; everything goes back when the game ends");
        AutomationProperties.SetAutomationId(_opts, "qs.gamemode.opts");
        Root.Children.Add(_opts);
        Paint();
    }

    public void Attach(SurfaceLink link)
    {
        _link = link;
        _sub = link.On<JsonElement>(GameModeFeature.StateEvent, e => { _st = e.ValueKind == JsonValueKind.Object ? JsonNode.Parse(e.GetRawText()) as JsonObject : _st; Paint(); _refit(); });
    }

    /// The panel opens: a fresh read (the push covers changes after it).
    public async Task Opened(bool dark)
    {
        _dark = dark; _note = null;
        Paint();
        if (_link == null) return;
        var r = await _link.CallAsync("gamemode.state");
        _st = r.Ok && r.Result.ValueKind == JsonValueKind.Object ? JsonNode.Parse(r.Result.GetRawText()) as JsonObject : new JsonObject { ["error"] = r.Error ?? "no answer" };
        Paint(); _refit();
    }

    public void Theme(bool dark) { _dark = dark; Paint(); }

    async Task Switch()
    {
        long now = Environment.TickCount64;
        var gv = GameModeView.Of(_st);
        if (_link == null || !GameModeView.SwitchTakes(gv.Dead, gv.Pending, _inflight, _lastClick, now)) { _log("qs: game mode row: a click on the switch was held back (one decision)"); return; }
        _lastClick = now;
        bool want = !(_st != null && GameModeJson.True(_st["on"]));
        _inflight = true; _note = null; Paint();
        var r = await _link.CallAsync("gamemode.set", new JsonObject { ["on"] = want });
        _inflight = false;
        if (r.Ok && r.Result.ValueKind == JsonValueKind.Object && JsonNode.Parse(r.Result.GetRawText()) is JsonObject st)
        {
            _st = st;
            if (GameModeJson.False(st["ok"])) _note = "Game mode didn't switch: " + (GameModeJson.Str(st["reason"]) ?? "no reason was given");
            _log($"qs: game mode row: switched {(want ? "on" : "off")} ({(GameModeJson.False(st["ok"]) ? "refused: " + GameModeJson.Str(st["reason"]) : "on=" + (GameModeJson.True(st["on"]) ? "true" : "false"))})");
        }
        else { _note = "Game mode didn't switch: " + (r.Error ?? "no answer"); _log($"qs: game mode row: the switch failed: {r.Error}"); }
        Paint(); _refit();
    }

    void TestKeys()
    {
        if (_takeKeys == null) return;
        var (ok, words) = _takeKeys();
        if (ok) _more.Focus(FocusState.Keyboard);
        _log($"qs: game mode row: details {(ok ? "took" : "did not take")} the keyboard (test): {words}");
    }

    void Opt(string key, string label, string cmd, string help)
    {
        var box = new CheckBox { Content = Text(label, 12), MinHeight = 28, Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(8, 0, 8, 0), FontFamily = _font };
        AutomationProperties.SetAutomationId(box, "qs.gamemode.opt." + key);
        AutomationProperties.SetName(box, label);
        AutomationProperties.SetHelpText(box, help);
        ToolTipService.SetToolTip(box, help);
        box.Click += async (_, _) => {
            if (_link == null) return;
            bool want = box.IsChecked == true;
            box.IsEnabled = false;
            var r = await _link.CallAsync(cmd, new JsonObject { ["on"] = want });
            box.IsEnabled = true;
            if (r.Ok && r.Result.ValueKind == JsonValueKind.Object && JsonNode.Parse(r.Result.GetRawText()) is JsonObject st) { _st = st; _log($"qs: game mode row: {key} {(want ? "on" : "off")}"); }
            else { box.IsChecked = !want; _note = "That setting didn't change: " + (r.Error ?? "no answer"); }
            Paint();
        };
        _boxes[key] = box;
        _opts.Children.Add(box);
    }

    void Paint()
    {
        var v = GameModeView.Of(_st);
        _head.Foreground = _b("LgInk");
        _sub2.Text = v.Sub; _sub2.Foreground = _b("LgInk2");
        ToolTipService.SetToolTip(_sub2, v.Sub.Length > 0 ? v.Sub : null);
        AutomationProperties.SetName(_sub2, v.Sub);
        _swText.Text = v.Label;
        _swText.Foreground = v.Dead ? _b("LgInk3") : _b("LgInk");
        AutomationProperties.SetName(_sw, "Game mode: " + v.Label);
        AutomationProperties.SetItemStatus(_sw, (v.On ? "on" : "off") + (v.Pending || _inflight ? "; pending" : "") + (v.Dead ? "; dead" : ""));
        AutomationProperties.SetHelpText(_sw, v.Tip);
        ToolTipService.SetToolTip(_sw, v.Tip);
        _sw.IsEnabled = !v.Dead;
        _sw.Opacity = v.Pending || _inflight ? .6 : 1;
        double h = _dark ? 30 : 28;
        _sw.Height = h; _more.Height = h;
        _sw.Padding = _more.Padding = new Thickness(_dark ? 12 : 10, 0, _dark ? 12 : 10, 0);
        _sw.Background = v.Dead ? _b("LgSunk") : _dark ? _b("LgCard") : _b("LgTile");
        _more.Background = _dark ? _b("LgCard") : _b("LgTile");
        _swText.FontSize = _moreText.FontSize = _dark ? 13 : 12;
        _track.Background = v.On ? _b("LgAcc") : new SolidColorBrush();
        _track.BorderBrush = v.On ? new SolidColorBrush() : v.Dead ? _b("LgEdgeIn") : _b("LgInk3");
        _knob.Background = v.Dead ? _b("LgInk3") : v.On ? _b("LgAccOn") : _b("LgInk2");
        _knob.Margin = new Thickness(v.On ? 17 : 2, 2, 0, 0);
        _moreText.Text = _open ? "Hide details" : "Details";
        _moreText.Foreground = _b("LgInk2");
        _chevLine.Stroke = _b("LgInk2");
        _chev.RenderTransform = new RotateTransform { Angle = _open ? 180 : 0, CenterX = 4, CenterY = 4 };
        AutomationProperties.SetName(_more, _open ? "Hide details" : "Details");
        AutomationProperties.SetItemStatus(_more, _open ? "expanded" : "collapsed");
        _more.IsEnabled = !(v.Dead && !(_st != null && _st["error"] == null));
        string words = _note ?? v.Why;
        _why.Text = words; _why.Visibility = words.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _why.Foreground = _note != null || v.Warn ? _b("LgWarn") : _b("LgInk2");
        AutomationProperties.SetName(_why, words);
        AutomationProperties.SetItemStatus(_why, _note != null || v.Warn ? "warn" : "");
        _listBox.Visibility = _opts.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
        _listBox.Background = _dark ? _b("LgCard") : _b("LgTile");
        foreach (var (k, box) in _boxes) box.IsChecked = k switch { "auto" => v.Auto, "liftSaver" => v.LiftSaver, _ => v.Boost };
        foreach (var (_, box) in _boxes) if (box.Content is TextBlock t) t.Foreground = _b("LgInk");
        _list.Children.Clear();
        if (!_open) return;
        var rows = v.Rows.Count > 0 ? v.Rows : v.Plan ?? new List<GameModeRowView>();
        if (v.Rows.Count == 0 && v.Plan != null) { var lh = Text("When it is on", 11); lh.Foreground = _b("LgInk3"); lh.Margin = new Thickness(8, 4, 8, 0); _list.Children.Add(lh); }
        foreach (var r in rows)
        {
            var a = new StackPanel { Padding = new Thickness(8, 4, 8, 4) };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bool bad = r.Status is "blocked" or "failed";
            var name = Text((bad ? "⚠ " : "") + r.Label, 12); name.Foreground = _b("LgInk");
            top.Children.Add(name);
            if (r.Badge.Length > 0) { var bd = Text(r.Badge, 11); bd.Foreground = r.Status == "done" ? _b("LgInk") : bad ? _b("LgWarn") : _b("LgInk3"); Grid.SetColumn(bd, 1); top.Children.Add(bd); }
            a.Children.Add(top);
            var tx = Text(r.Text, 11.5); tx.TextWrapping = TextWrapping.Wrap;
            tx.Foreground = bad ? _b("LgWarn") : r.Status is "plan" or "info" ? _b("LgInk3") : _b("LgInk2");
            a.Children.Add(tx);
            AutomationProperties.SetAutomationId(a, "qs.gamemode.row." + r.Key + (r.Key == "plan" ? "." + r.Label.Replace(' ', '-').ToLowerInvariant() : ""));
            AutomationProperties.SetName(a, r.Label);
            AutomationProperties.SetHelpText(a, r.Text);
            AutomationProperties.SetItemStatus(a, r.Status);
            _list.Children.Add(a);
        }
        if (v.Last.Length > 0 && !v.On)
        {
            var l = Text(v.Last, 11.5); l.TextWrapping = TextWrapping.Wrap; l.Foreground = _b("LgInk2"); l.Margin = new Thickness(8, 6, 8, 4);
            AutomationProperties.SetAutomationId(l, "qs.gamemode.last");
            AutomationProperties.SetName(l, v.Last);
            _list.Children.Add(l);
        }
    }

    TextBlock Text(string s, double size) => new() { Text = s, FontSize = size, FontFamily = _font };

    static Style? _style;
    /// .gmswitch / .gmmore: a flat face in the element's own Background, the hover and press tokens over it.
    static Button MakeButton(string id, string name, UIElement face)
    {
        _style ??= (Style)XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
            "<Setter Property='BorderThickness' Value='0'/><Setter Property='CornerRadius' Value='6'/><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
            "<Setter Property='VerticalContentAlignment' Value='Center'/>" +
            "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
            "<Grid Background='{TemplateBinding Background}' CornerRadius='{TemplateBinding CornerRadius}'>" +
            "<Border x:Name='Over' CornerRadius='{TemplateBinding CornerRadius}'/>" +
            "<ContentPresenter Padding='{TemplateBinding Padding}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}'/>" +
            "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'><VisualState x:Name='Normal'/>" +
            "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgHover}'/></VisualState.Setters></VisualState>" +
            "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgPress}'/></VisualState.Setters></VisualState>" +
            "<VisualState x:Name='Disabled'/></VisualStateGroup></VisualStateManager.VisualStateGroups>" +
            "</Grid></ControlTemplate></Setter.Value></Setter></Style>");
        var b = new Button { Style = _style, Content = face };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, name);
        return b;
    }

    public void Dispose() { _sub?.Dispose(); _sub = null; }
}
