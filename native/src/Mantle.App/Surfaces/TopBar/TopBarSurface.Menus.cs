using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Features.TopBar;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace Mantle.App.Surfaces.TopBar;

// The dropdowns (TOP-026..082) and their keys (TOP-083..088): ui/topbar/topbar.js's openMenu / renderMenus / activate /
// keydown, drawn in the same window as Electron does (the window grows to the menu through topbar.menuState and
// shrinks back when it closes). The rules are Mantle.Core's TopBarMenu; this half only draws its state and does what
// each step says. Closed, it costs nothing: no element, no timer, no event beyond the titles' own.
internal sealed partial class TopBarSurface
{
    TopBarMenu? _open;
    Canvas _menus = null!;
    Border _away = null!;
    int _fitShown = int.MaxValue;
    int _asked = -1;
    double _workBottom = 10000;   // the primary's work-area bottom in the window, DIP (TOP-073)
    bool _overGames, _peeking, _cropped, _focusDirty;
    DispatcherQueueTimer? _waitT, _hoverT;
    TextBox? _editBox;
    sealed class Dd { public Border Box = null!; public ScrollViewer Sv = null!; public StackPanel Rows = null!; public MenuLevel Lv = null!; public double Bottom; public List<Button?> Btns = new(); }
    readonly List<Dd> _dds = new();

    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

    void BuildMenus()
    {
        // click-away (TOP-078): a clear layer under the dropdowns, below the bar only, shown while a menu is open
        _away = new Border { Background = ClearBrush(), Margin = new Thickness(0, TopBarRules.BarDip, 0, 0), Visibility = Visibility.Collapsed };
        _away.PointerPressed += (_, _) => CloseAll(false, "a click outside the menu");
        _menus = new Canvas { Visibility = Visibility.Collapsed };   // no background: the bar under it keeps its clicks
        _root.Children.Add(_away);
        _root.Children.Add(_menus);
        _root.PreviewKeyDown += OnKey;
    }

    void InstallMenus()
    {
        _win.Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) CloseAll(false, "the window lost focus"); };
        _subs.Add(_ctx.On<bool>("topbar.peek", on => { _peeking = on; RefreshTitles(); }));
        _subs.Add(_ctx.On<JsonElement>("topbar.state", st =>
        {
            if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("overGames", out var og) && og.ValueKind is JsonValueKind.True or JsonValueKind.False)
            { _overGames = og.ValueKind == JsonValueKind.True; RefreshTitles(); }
        }));
    }

    /// The titles' items follow the check and peek states without redrawing the bar (their labels do not change).
    void RefreshTitles() { if (_titles.Count > 0) _titles = TopBarModel.Titles(_focus, _menu, _overGames, _peeking); }

    JsonObject? MenuSource() => _menu;

    // ------------------------------------------------------------------ open / close
    int BarCount() => _fitShown < _titles.Count ? _fitShown + 1 : _titles.Count;

    double TitleX(int ti)
    {
        var b = _left.Children.OfType<Button>().FirstOrDefault(x => ti == _fitShown && _fitShown < _titles.Count ? (x.Tag as string) == "more" : (x.Tag as string) == (ti < _titles.Count ? _titles[ti].Key : null));
        if (b == null) return Pad;
        try { return Math.Round(b.TransformToVisual(_root).TransformPoint(default).X); } catch (ArgumentException) { return Pad; }
    }

    /// TOP-029: a press on a title opens its menu; on the open title it closes it and gives the app the keyboard back.
    void OnTitlePress(int ti)
    {
        if (_open != null && _open.Ti == ti) { CloseAll(true, "the open title pressed again"); _ctx.Log.Line($"title pressed: {KeyOf(ti)} (closed; refocus)"); return; }
        OpenMenu(ti);
        _ctx.Log.Line($"title pressed: {KeyOf(ti)} ({(_open != null ? "open; " + _open.Top.Items.Count + " rows" : "closed; nothing to open")})");
    }
    string KeyOf(int ti) => ti == _fitShown && _fitShown < _titles.Count ? "more" : ti < _titles.Count ? _titles[ti].Key : "?";

    void OpenMenu(int ti)
    {
        if (ti == _fitShown && _fitShown < _titles.Count) { OpenMore(); return; }
        if (ti < 0 || ti >= _titles.Count) return;
        var t = _titles[ti];
        if (t.Source == "uia" && t.Lazy)   // TOP-066: the app opens its own menu
        {
            CloseAll(false, "a lazy UI Automation title");
            _ = Run(new MenuCall("topbar.uiaInvoke", new JsonObject { ["hwnd"] = (MenuSource()?["hwnd"])?.DeepClone(), ["path"] = new JsonArray((JsonNode)JsonValue.Create(t.Pos ?? 0)),
                ["names"] = new JsonArray((JsonNode)JsonValue.Create(t.Label)!), ["expand"] = true }, t.Label), "the app did not open its menu");
            return;
        }
        bool was = _open != null;
        _open = TopBarMenu.ForTitle(ti, t, TitleX(ti));
        _openKey = t.Key;
        PaintTitles();
        RenderMenus();
        if (!was) _ctx.Log.Line($"menu opened: {t.Key} ({_open.Top.Items.Count} rows)");
        if (t.Source == "win32") RefreshLevel(0);   // TOP-061: fresh greyed / checked states
    }

    void OpenMore()
    {
        _open = TopBarMenu.ForMore(_titles, _fitShown, TitleX(_fitShown));
        _openKey = "more";
        PaintTitles();
        RenderMenus();
        _ctx.Log.Line($"menu opened: more ({_open.Top.Items.Count} rows: {string.Join(", ", _open.Top.Items.Select(i => i.Label))})");
    }

    async void RefreshLevel(int level)
    {
        var o = _open;
        var src = MenuSource();
        if (o == null || src == null || level >= o.Stack.Count) return;
        var lv = o.Stack[level];
        var args = new JsonObject { ["hwnd"] = src["hwnd"]?.DeepClone(), ["init"] = new JsonArray(lv.Path.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()) };
        var r = await _ctx.CallAsync("topbar.menus", args);
        if (_open != o || level >= o.Stack.Count || o.Stack[level] != lv) return;
        var reply = r.Ok ? JsonNode.Parse(r.Result.GetRawText()) as JsonObject : new JsonObject { ["ok"] = false, ["reason"] = r.Error };
        TopBarMenu.Refreshed(lv, reply);
        RenderMenus();
    }

    void OpenSub(int level, int i, bool selectFirst)
    {
        if (_open == null || level >= _dds.Count) return;
        var dd = _dds[level];
        double l = dd.Lv.X + 220, rr = l, top = 40;
        if (i < dd.Btns.Count && dd.Btns[i] is { } row)
        {
            try
            {
                var p = row.TransformToVisual(_root).TransformPoint(default);
                l = p.X; rr = p.X + row.ActualWidth; top = p.Y;
            }
            catch (ArgumentException) { }
        }
        var (pushed, title, refresh) = _open.PushSub(level, i, l, rr, top, MenuSource()?["source"]?.GetValue<string>());
        if (title is int ti) { OpenMenu(ti); return; }
        if (!pushed) return;
        if (selectFirst) _open.Move(level + 1, 1);
        RenderMenus();
        _ctx.Log.Line($"submenu opened: {_open.Stack[level].Items[i].Label} (level {level + 1}, {_open.Top.Items.Count} rows)");
        if (refresh) RefreshLevel(level + 1);
    }

    /// TOP-077: the dropdowns go first, then the feature is told (the window shrinks back to the 28 DIP bar).
    void CloseAll(bool refocus, string why)
    {
        if (_open == null) return;
        _open = null; _openKey = null;
        _hoverT?.Stop(); _waitT?.Stop(); _editBox = null;
        _menus.Children.Clear(); _dds.Clear();
        _menus.Visibility = Visibility.Collapsed; _away.Visibility = Visibility.Collapsed;
        _asked = -1;
        PaintTitles();
        _ = _ctx.CallAsync("topbar.menuState", new JsonObject { ["open"] = false, ["refocus"] = refocus });
        _ctx.Log.Line($"menu closed ({why}; refocus {(refocus ? "yes" : "no")})");
        if (_focusDirty) { _focusDirty = false; Render(); } else Fit();
    }

    void PaintTitles()
    {
        foreach (var b in _left.Children.OfType<Button>()) b.Background = IsOpen(b) ? Br("LgPress") : ClearBrush();
    }

    // ------------------------------------------------------------------ steps
    long NowMs => Environment.TickCount64;

    void Do(MenuStep s)
    {
        var o = _open;
        if (o == null) return;
        switch (s.Do)
        {
            case MenuDo.Render: RenderMenus(); break;
            case MenuDo.OpenSub: OpenSub(s.Level, s.Index, s.SelectFirst); break;
            case MenuDo.OpenTitle:
            {
                int n = BarCount();
                int next = TopBarMenu.StepTitle(o.Ti, s.Index, n);
                if (next < 0) return;
                OpenMenu(next);
                if (_open != null && s.SelectFirst && _open.Move(0, 1)) RenderMenus();
                break;
            }
            case MenuDo.Armed:
            {
                RenderMenus();
                var it = o.Stack[s.Level].Items[s.Index];
                _ctx.Log.Line($"armed: {it.Label} (\"{it.Confirm}\")");
                var t = _win.DispatcherQueue.CreateTimer();
                t.Interval = TimeSpan.FromMilliseconds(TopBarMenu.ArmMs); t.IsRepeating = false;
                t.Tick += (_, _) => { if (_open == o && o.Disarm(it)) { RenderMenus(); _ctx.Log.Line($"disarmed: {it.Label}"); } };
                t.Start();
                break;
            }
            case MenuDo.Call:
                CloseAll(s.Refocus, "a pick");
                _ = Run(s.Call!, null);
                break;
            case MenuDo.Close:
                CloseAll(s.Refocus, s.Refocus ? "Escape" : "closed");
                if (s.Note != null) { _ctx.Log.Line("menu note: " + s.Note); SetNote(s.Note); }
                break;
            case MenuDo.Commit:
                var c = o.Commit();
                CloseAll(true, "the name box's Enter");
                if (c != null) _ = Run(c, null);
                break;
        }
    }

    /// Runs one pick's command; a refusal shows on the strip (TOP-081).
    async System.Threading.Tasks.Task Run(MenuCall c, string? dflt)
    {
        var r = await _ctx.CallAsync(c.Verb, c.Args);
        bool ok = r.Ok; string? reason = r.Error; bool dry = false;
        if (r.Ok && r.Result.ValueKind == JsonValueKind.Object)
        {
            var x = r.Result;
            if (x.TryGetProperty("ok", out var k) && k.ValueKind == JsonValueKind.False) ok = false;
            reason = x.TryGetProperty("reason", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() : x.TryGetProperty("error", out var ev) && ev.ValueKind == JsonValueKind.String ? ev.GetString() : null;
            dry = x.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
            if (c.Verb == "topbar.overGames" && x.TryGetProperty("overGames", out var og)) { _overGames = og.ValueKind == JsonValueKind.True; RefreshTitles(); }
        }
        if (!ok && reason == null && dflt != null) reason = dflt;
        var note = TopBarMenu.ResultNote(c.Label, ok, reason, dry);
        _ctx.Log.Line($"menu pick: {c.Label} -> {c.Verb}: {(note ?? (ok ? "ok" : "refused"))}");
        if (note != null) SetNote(note);
    }

    // ------------------------------------------------------------------ keys (TOP-082..088)
    static string? KeyName(VirtualKey k) => k switch
    {
        VirtualKey.Escape => "Escape", VirtualKey.Down => "ArrowDown", VirtualKey.Up => "ArrowUp", VirtualKey.Right => "ArrowRight",
        VirtualKey.Left => "ArrowLeft", VirtualKey.Enter => "Enter", VirtualKey.Space => " ",
        >= VirtualKey.A and <= VirtualKey.Z => ((char)('a' + (k - VirtualKey.A))).ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (k - VirtualKey.Number0))).ToString(),
        _ => null,
    };

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (_open == null) return;
        var name = KeyName(e.Key);
        if (_open.Edit != null && _editBox != null && e.OriginalSource == _editBox && name is not ("Enter" or "Escape")) return;   // typing into the box
        if (name == null) return;
        _ctx.Log.Line($"key: {(name == " " ? "Space" : name)}");
        Do(_open.Key(name, NowMs, _focus, MenuSource()));
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drawing
    void RenderMenus()
    {
        var o = _open;
        foreach (var d in _dds) d.Lv.Scroll = d.Sv.VerticalOffset;   // TOP-079: each level keeps its scroll
        _menus.Children.Clear(); _dds.Clear(); _editBox = null;
        if (o == null) return;
        _menus.Visibility = Visibility.Visible; _away.Visibility = Visibility.Visible;
        double vis = _root.ActualWidth > 0 ? _root.ActualWidth : 1280;
        var inf = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        for (int level = 0; level < o.Stack.Count; level++)
        {
            var lv = o.Stack[level];
            var dd = MakeDropdown(o, lv, level);
            _menus.Children.Add(dd.Box);
            dd.Box.Measure(inf);
            double w = dd.Box.DesiredSize.Width, h = dd.Box.DesiredSize.Height;
            double left = TopBarMenu.Left(level, lv.X, w, vis, lv.FlipX);
            var (top, maxH) = TopBarMenu.FitDown(level, lv.Y ?? TopBarMenu.DdTop, h, _workBottom);
            if (maxH is double m) dd.Box.MaxHeight = m;
            Canvas.SetLeft(dd.Box, left); Canvas.SetTop(dd.Box, top);
            dd.Bottom = top + Math.Min(h, maxH ?? h);
            _dds.Add(dd);
        }
        _menus.UpdateLayout();
        foreach (var dd in _dds)
        {
            if (dd.Lv.Scroll > 0) dd.Sv.ChangeView(null, dd.Lv.Scroll, null, true);
            KeepSelVisible(dd);
        }
        if (_editBox != null) { _editBox.Focus(FocusState.Programmatic); _editBox.SelectionStart = _editBox.Text.Length; }
        NotifyMain();
        Reveal();
    }

    void KeepSelVisible(Dd dd)
    {
        if (dd.Lv.Sel < 0 || dd.Lv.Sel >= dd.Btns.Count || dd.Btns[dd.Lv.Sel] is not { } row) return;
        double rowTop;
        try { rowTop = row.TransformToVisual(dd.Rows).TransformPoint(default).Y; } catch (ArgumentException) { return; }
        if (TopBarMenu.KeepVisible(rowTop, row.ActualHeight, dd.Sv.VerticalOffset, dd.Sv.ViewportHeight, dd.Sv.ExtentHeight) is double y)
            dd.Sv.ChangeView(null, y, null, true);
    }

    Dd MakeDropdown(TopBarMenu o, MenuLevel lv, int level)
    {
        var dd = new Dd { Lv = lv, Rows = new StackPanel() };
        for (int i = 0; i < lv.Items.Count; i++) { var (el, btn) = MakeRow(o, lv, level, i); dd.Rows.Children.Add(el); dd.Btns.Add(btn); }
        if (lv.Info != null) dd.Rows.Children.Add(Info(lv.Info, lv.Bad, $"topbar.menu.{level}.info"));
        if (lv.Items.Count == 0 && lv.Info == null) dd.Rows.Children.Add(Info(TopBarMenu.Empty, false, $"topbar.menu.{level}.info"));
        dd.Sv = new ScrollViewer { Content = dd.Rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, IsTabStop = false };
        dd.Box = new Border
        {
            Child = dd.Sv, Padding = new Thickness(5), MinWidth = 220, MaxWidth = 420, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Dbl("LgR", 8)), Background = Br("LgFillPop"), BorderBrush = Br("LgEdgeOut"),
        };
        AutomationProperties.SetAutomationId(dd.Sv, $"topbar.menu.{level}");   // a Border has no automation peer; the ScrollViewer has
        AutomationProperties.SetName(dd.Sv, level == 0 ? (o.IsMore ? "More menus" : KeyOf(o.Ti)) : "Submenu");
        lv.Shown = true;
        return dd;
    }

    TextBlock Info(string text, bool bad, string id)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 11.5, FontFamily = TextFont, Padding = new Thickness(8, 5, 8, 4), Foreground = Br(bad ? "LgWarn" : "LgInk3") };
        AutomationProperties.SetAutomationId(t, id);
        AutomationProperties.SetName(t, text);
        return t;
    }

    (FrameworkElement El, Button? Btn) MakeRow(TopBarMenu o, MenuLevel lv, int level, int i)
    {
        var it = lv.Items[i];
        if (it.T == "sep") return (new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4), Background = Br("LgSunk") }, null);
        if (it.T == "info") return (Info(it.Label ?? "", false, $"topbar.menu.{level}.{i}"), null);
        bool armed = o.Armed is { } a && ReferenceEquals(a.Item, it);
        var (label, check, key, arrow, off) = TopBarMenu.Row(it, armed);
        var b = (Button)XamlReader.Load($"<Button {Ns} Height='24' Padding='6,0,8,0' HorizontalAlignment='Stretch' HorizontalContentAlignment='Stretch' ClickMode='Release' IsTabStop='False' UseSystemFocusVisuals='False'>"
            + "<Button.Template><ControlTemplate TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='6' Padding='{TemplateBinding Padding}'>"
            + "<ContentPresenter Content='{TemplateBinding Content}' Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center' HorizontalAlignment='Stretch'/></Border></ControlTemplate></Button.Template></Button>");
        var g = new Grid { ColumnSpacing = 6 };
        foreach (var wd in new[] { new GridLength(16), new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(12) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = wd });
        string ink = armed ? "LgWarn" : off ? "LgInk3" : "LgInk";
        TextBlock T(string s, int col, string br, double size)
        {
            var t = new TextBlock { Text = s, FontFamily = TextFont, FontSize = size, Foreground = Br(br), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, col); g.Children.Add(t); return t;
        }
        bool editing = o.Edit is { } ed && ReferenceEquals(ed.Item, it);
        T(check, 0, off ? "LgInk3" : "LgInk2", 11).HorizontalAlignment = HorizontalAlignment.Center;
        if (editing)
        {
            // TOP-042/082: the name box, typed in the menu itself
            var box = new TextBox { Text = o.Edit!.Value.Value, MaxLength = 40, PlaceholderText = "Name it, e.g. Work", MinWidth = 150, Height = 20, MinHeight = 20, Padding = new Thickness(6, 0, 6, 0), FontSize = 12, IsSpellCheckEnabled = false };
            AutomationProperties.SetName(box, "Layout name");
            AutomationProperties.SetAutomationId(box, "topbar.menu.namebox");
            box.TextChanged += (_, _) => { if (_open?.Edit is { } e2) _open.Edit = (e2.Item, box.Text); };
            Grid.SetColumn(box, 1); g.Children.Add(box);
            _editBox = box;
        }
        else T(label, 1, ink, _dark ? 13 : 12.5);
        T(key, 2, "LgInk3", 11.5).Margin = new Thickness(18, 0, 0, 0);
        T(arrow, 3, "LgInk3", 10).HorizontalAlignment = HorizontalAlignment.Right;
        b.Content = g;
        b.Background = i == lv.Sel || editing ? Br("LgHover") : ClearBrush();
        b.Foreground = Br(ink);
        AutomationProperties.SetAutomationId(b, $"topbar.menu.{level}.{i}");
        AutomationProperties.SetName(b, label);
        if (off) b.IsEnabled = false;
        if (it.Checked) AutomationProperties.SetItemStatus(b, it.Radio ? "radio checked" : "checked");
        int li = level, ri = i;
        b.Click += (_, _) => { if (_open == o) Do(o.Activate(li, ri, NowMs, _focus, MenuSource())); };
        b.PointerEntered += (_, _) => Hover(o, li, ri);
        return (b, b);
    }

    /// TOP-071: hovering selects the row and closes deeper levels; a row with a submenu opens it after 110 ms.
    void Hover(TopBarMenu o, int level, int i)
    {
        if (_open != o || level >= o.Stack.Count) return;
        var lv = o.Stack[level];
        if (lv.Sel == i) return;
        lv.Sel = i;
        bool deeper = o.Stack.Count > level + 1;
        if (deeper) o.Stack.RemoveRange(level + 1, o.Stack.Count - level - 1);
        if (deeper || o.Edit != null) RenderMenus();
        else if (level < _dds.Count) for (int k = 0; k < _dds[level].Btns.Count; k++) if (_dds[level].Btns[k] is { } b) b.Background = k == i ? Br("LgHover") : ClearBrush();   // in place: no rebuild per hover
        _hoverT?.Stop();
        if (lv.Items[i].Items == null) return;
        _hoverT ??= _win.DispatcherQueue.CreateTimer();
        _hoverT.Interval = TimeSpan.FromMilliseconds(TopBarMenu.HoverSubMs); _hoverT.IsRepeating = false;
        _hoverT.Tick -= HoverTick; _hoverT.Tick += HoverTick;
        _hoverAt = (o, lv, i);
        _hoverT.Start();
    }
    (TopBarMenu O, MenuLevel Lv, int I)? _hoverAt;
    void HoverTick(DispatcherQueueTimer t, object _)
    {
        if (_hoverAt is not { } h || _open != h.O || h.Lv.Sel != h.I) return;
        int level = h.O.Stack.IndexOf(h.Lv);
        if (level >= 0) OpenSub(level, h.I, false);
    }

    // ------------------------------------------------------------------ the window follows the menu (TOP-073..076)
    async void NotifyMain()
    {
        int want = TopBarMenu.WindowHeight(_dds.Select(d => d.Bottom));
        if (want == _asked) return;   // hover and re-renders at the same height
        _asked = want;
        var o = _open;
        var r = await _ctx.CallAsync("topbar.menuState", new JsonObject { ["open"] = true, ["height"] = want });
        bool ok = r.Ok && !(r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var k) && k.ValueKind == JsonValueKind.False);
        if (!ok && _open == o)
        {
            _asked = -1;
            string? why = r.Ok && r.Result.TryGetProperty("reason", out var rv) ? rv.GetString() : r.Error;
            SetNote(TopBarMenu.CannotShow(why));
        }
    }

    /// TOP-075: a dropdown reaching past the window's bottom waits unseen until the window has grown; after 1.5 s it
    /// shows anyway and the strip says why (TOP-076).
    void Reveal()
    {
        if (_open == null) return;
        int waiting = 0;
        foreach (var d in _dds)
        {
            bool fits = d.Bottom <= _height + 0.5 || _open.GaveUp;
            d.Box.Opacity = fits ? 1 : 0;
            d.Box.IsHitTestVisible = fits;
            if (!fits) waiting++;
        }
        if (waiting == 0) { _waitT?.Stop(); return; }
        if (_waitT is { IsRunning: true }) return;
        _waitT ??= _win.DispatcherQueue.CreateTimer();
        _waitT.Interval = TimeSpan.FromMilliseconds(TopBarMenu.WaitMs); _waitT.IsRepeating = false;
        _waitT.Tick -= WaitedTooLong; _waitT.Tick += WaitedTooLong;
        _waitT.Start();
    }
    void WaitedTooLong(DispatcherQueueTimer t, object _)
    {
        if (_open == null || _dds.All(d => d.Bottom <= _height + 0.5)) return;
        double need = _dds.Max(d => d.Bottom);
        _open.GaveUp = true;
        Reveal();
        SetNote(TopBarMenu.CutOff(need, _height));
    }

    /// The window region from an earlier crop (TOP-218) would clip a grown window: it goes once the window is as tall as asked.
    void Uncrop(int gotH, int wantH)
    {
        if (gotH > wantH) { _cropped = true; return; }
        if (_cropped) { SetWindowRgn(_hwnd, IntPtr.Zero, true); _cropped = false; }
    }
}
