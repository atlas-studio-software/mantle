using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.App.Native;
using Mantle.App.Shell;
using Mantle.App.Shell.Windowing;
using Mantle.App.Surfaces.Bar.Frame;
using Mantle.App.Surfaces.Osd;
using Mantle.Core.Features.TopBar;
using Mantle.Core.Look;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App.Surfaces.TopBar;

// The top bar: the native form of ui/topbar (index.html, topbar.css, topbar.js). ONE window titled exactly "Mantle top
// bar" (sidecar/MenuBar.cs checks the title before it ever removes a stale app bar), made at start and parked hidden,
// placed at the PRIMARY display's top-left, its full width, 28 DIP tall (physical px = round(28 x scale)). Frameless,
// not resizable (no sizing bands: the hit test is CLIENT everywhere), no taskbar button, shown without activating.
// Live it is topmost; a --test window is never topmost.
//
// It only draws. The feature (Mantle.Core/Features/TopBar) decides: "topbar.visible" shows or hides it, "topbar.place"
// re-places it at a height (an open menu, a display change), "topbar.focus" / "topbar.stats" are what the strip says.
// It reports where it is ("topbar.placed", with its hwnd, which the feature's app bar needs) and what the display did
// ("topbar.display": WM_DISPLAYCHANGE, WM_DPICHANGED, SPI_SETWORKAREA), and re-places itself at once on each.
//
// The strip (TOP-002, TOP-018..027): the Mantle glyph, the focused app's bold name, its own menus (or "Window"), fitted
// with TopBarModel.FitTitles (the app name gives way first, then "»"); on the right the stat cells (CPU, RAM, GPU,
// NET, PING) and the two tool buttons (timer, notes). Light: --lg-fill-fb over what is behind with the inset
// --lg-edge-out line at its bottom; dark: --lg-fill-pop, no line (the dark glass and colour under are later parts).
// The dropdowns and their keys are TopBarSurface.Menus.cs. Not drawn yet (docs/lanes/topbar.md "Left"): the stats
// dropdowns, the "stats paused" item, the menus' motion and glass.
[Surface("topbar", AtStart = true)]
internal sealed partial class TopBarSurface : ISurface
{
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
    const double Pad = 8, Gap = 10;
    static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");
    const string GlyphPath = "M4.5,2.5 H11.5 A2.5,2.5 0 0 1 14,5 V11 A2.5,2.5 0 0 1 11.5,13.5 H4.5 A2.5,2.5 0 0 1 2,11 V5 A2.5,2.5 0 0 1 4.5,2.5 Z M2,6 H14";
    const string TimerPath = "M13.25,9.25 A5.25,5.25 0 1 1 2.75,9.25 A5.25,5.25 0 1 1 13.25,9.25 Z M8,9.25 V6.5 M6.25,1.75 H9.75 M12.3,4.45 L13.25,3.5";
    const string NotePath = "M3.25,2.25 H9.75 L12.75,5.25 V13.75 H3.25 Z M9.5,2.5 V5.5 H12.5 M5.75,8.25 H10.25 M5.75,11 H8.75";

    ISurfaceContext _ctx = null!;
    Window _win = null!;
    IntPtr _hwnd;
    Grid _root = null!, _bar = null!;
    Border _face = null!, _line = null!;
    StackPanel _left = null!, _right = null!;
    readonly List<IDisposable> _subs = new();
    readonly HashSet<string> _missing = new();
    bool _dark, _shown, _activated, _test;
    int _height = TopBarRules.BarDip;
    string? _openKey;
    JsonObject? _focus, _menu; JsonObject? _stats; string? _paused;
    IReadOnlyList<TopBarTitle> _titles = Array.Empty<TopBarTitle>();
    FrameNative.SubclassProc? _proc;
    string _lastPlaced = "";

    public bool Shown => _shown;

    // ------------------------------------------------------------------ create

    public void Create(ISurfaceContext ctx)
    {
        try { CreateCore(ctx); }
        catch (Exception e)
        {
            // the host logs only the message; the frames say WHERE (a bare "Specified cast is not valid" said nothing)
            var at = (e.StackTrace ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4);
            ctx.Log.Line($"made: failed: {e.GetType().Name}: {e.Message} at {string.Join(" < ", at)}");
            throw;
        }
    }

    void CreateCore(ISurfaceContext ctx)
    {
        _ctx = ctx;
        _test = ctx.Test;
        Build();
        _win = new Window { Title = TopBarRules.Title, Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = !_test;   // TOP-212/213, TOP-222
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        _win.SystemBackdrop = new SeeThroughBackdrop(ctx.Log.Line);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        WindowKinds.Frameless(_hwnd);
        WindowKinds.StripFrame(_hwnd);
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW;   // no taskbar button
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;   // WIN-028: a quiet test window draws nothing
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(_hwnd);
        // display changes: re-place at once, and tell the feature (its verdict and burst decide the app bar's part)
        _proc = Proc;
        FrameNative.SetWindowSubclass(_hwnd, _proc, new UIntPtr(0x5442), IntPtr.Zero);

        SetTheme(false);   // the theme event (replayed at once) repaints in dark
        _subs.Add(ctx.On<ThemePayload>("theme", t => SetTheme(t.View.Theme == "dark")));
        _subs.Add(ctx.On<TopBarVisible>("topbar.visible", OnVisible));
        _subs.Add(ctx.On<TopBarPlace>("topbar.place", pl => { _height = pl.Height; Place(pl.Why); if (_open != null) Reveal(); }));
        _subs.Add(ctx.On<TopBarFocus>("topbar.focus", OnFocus));
        _subs.Add(ctx.On<TopBarStats>("topbar.stats", OnStats));
        _subs.Add(ctx.On<TopBarClose>("topbar.close", c => CloseAll(false, c.Why)));
        _subs.Add(ctx.On<TopBarNote>("topbar.note", n => SetNote(n.Text)));
        InstallMenus();
        Place("made");
        ctx.Log.Line("made hidden and parked (" + (Host.Quiet ? "test: quiet" : _test ? "test: visible" : "live") + ")");
    }

    void Build()
    {
        _root = new Grid { Background = ClearBrush(), VerticalAlignment = VerticalAlignment.Top };
        _bar = new Grid { Height = TopBarRules.BarDip, VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(Pad, 0, Pad, 0), ColumnSpacing = Gap };
        AutomationProperties.SetName(_bar, "Menu bar");
        AutomationProperties.SetAutomationId(_bar, "topbar.bar");
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _face = new Border { Height = TopBarRules.BarDip, VerticalAlignment = VerticalAlignment.Top };
        _line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };   // inset 0 -1px 0 --lg-edge-out (light)
        var faceGrid = new Grid { Height = TopBarRules.BarDip, VerticalAlignment = VerticalAlignment.Top };
        faceGrid.Children.Add(_face); faceGrid.Children.Add(_line);
        _left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        Grid.SetColumn(_right, 1);
        _bar.Children.Add(_left); _bar.Children.Add(_right);
        _root.Children.Add(faceGrid);
        _root.Children.Add(_bar);
        BuildMenus();
        _root.SizeChanged += (_, _) => Fit();
        _root.RequestedTheme = ElementTheme.Light;
    }

    // ------------------------------------------------------------------ the theme tokens (Theme/Tokens.xaml)
    object Tok(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is { } v) return v;
        if (_missing.Add(key)) _ctx?.Log.Line($"the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }
    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }
    Brush Br(string key) => Tok(key) as Brush ?? new SolidColorBrush();
    /// A clear brush that still takes clicks (a null Background would not hit-test). A new SolidColorBrush's colour is
    /// all-zero ARGB, so no colour literal is needed.
    static Brush ClearBrush() => new SolidColorBrush();
    double Dbl(string key, double dflt) => Tok(key) is double v ? v : dflt;

    void SetTheme(bool dark)
    {
        if (_dark == dark && _face.Background != null) return;
        _dark = dark;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        // TOP-004: 12.5 px light, 13 px dark
        _face.Background = Br(dark ? "LgFillPop" : "LgFillFb");
        _line.Background = dark ? null : Br("LgEdgeOut");
        Render();
        _ctx.Log.Line("theme " + (dark ? "dark" : "light"));
    }

    // ------------------------------------------------------------------ the strip
    /// A strip button: the fixed parts through XAML (a template of a Border and a ContentPresenter, no focus visuals, press
    /// to open), the themed parts in code. Each code step is guarded: a step that throws is logged once with what it was
    /// (the first runner build threw a bare "Specified cast is not valid" from this method) and the button still comes.
    Button MakeButton(string id, string name, bool bold = false)
    {
        var b = (Button)XamlReader.Load($"<Button {Ns} Height='22' Padding='9,0,9,0' VerticalAlignment='Center' ClickMode='Press' IsTabStop='True' UseSystemFocusVisuals='False'>"
            + "<Button.Template><ControlTemplate TargetType='Button'><Border Background='{TemplateBinding Background}' CornerRadius='{TemplateBinding CornerRadius}' Padding='{TemplateBinding Padding}'>"
            + "<ContentPresenter Content='{TemplateBinding Content}' Foreground='{TemplateBinding Foreground}' VerticalAlignment='Center' HorizontalAlignment='Left'/></Border></ControlTemplate></Button.Template></Button>");
        Step("corner", () => b.CornerRadius = new CornerRadius(Dbl("LgRCtl", 6)));
        Step("fill", () => b.Background = ClearBrush());
        Step("ink", () => b.Foreground = Br("LgInk"));
        Step("font", () => b.FontFamily = TextFont);
        Step("font size", () => b.FontSize = _dark ? 13 : 12.5);
        Step("weight", () => b.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal);
        Step("automation id", () => AutomationProperties.SetAutomationId(b, id));
        Step("automation name", () => AutomationProperties.SetName(b, name));
        Step("hover", () =>
        {
            b.PointerEntered += (_, _) => { if (!IsOpen(b)) b.Background = Br("LgHover"); };
            b.PointerExited += (_, _) => { if (!IsOpen(b)) b.Background = ClearBrush(); };
        });
        return b;
    }
    readonly HashSet<string> _stepFailed = new();
    void Step(string what, Action a)
    {
        try { a(); }
        catch (Exception e) when (e is InvalidCastException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (_stepFailed.Add(what)) _ctx.Log.Line($"a strip button's {what} could not be set: {e.GetType().Name}: {e.Message}");
        }
    }
    bool IsOpen(Button b) => _openKey != null && (b.Tag as string) == _openKey;

    static Viewbox Glyph(string data, double size, Brush ink, double stroke = 1.5)
    {
        // the path data through XAML (as QsPanel does): XamlBindingHelper.ConvertValue to Geometry threw "Specified cast is
        // not valid" on the runner, so the surface was never made
        var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load($"<Path {Ns} Data='{data}'/>");
        path.Stroke = ink; path.StrokeThickness = stroke;
        path.StrokeStartLineCap = PenLineCap.Round; path.StrokeEndLineCap = PenLineCap.Round; path.StrokeLineJoin = PenLineJoin.Round;
        path.Width = 16; path.Height = 16;
        var c = new Canvas { Width = 16, Height = 16 }; c.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = c };
    }

    void Render()
    {
        if (_ctx == null) return;
        _titles = TopBarModel.Titles(_focus, _menu, _overGames, _peeking);
        _left.Children.Clear();
        _fitShown = int.MaxValue;
        for (int ti = 0; ti < _titles.Count; ti++)
        {
            var t = _titles[ti]; int idx = ti;
            var b = MakeButton("topbar.title." + t.Key, t.Glyph ? "Mantle" : t.Label, t.Bold);
            b.Tag = t.Key;
            if (t.Glyph) { b.Padding = new Thickness(8, 0, 8, 0); b.Content = Glyph(GlyphPath, 14, Br("LgInk")); ToolTipService.SetToolTip(b, "Mantle"); }
            else b.Content = new TextBlock { Text = t.Label, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, FontWeight = b.FontWeight };
            if (!t.Enabled) b.Foreground = Br("LgInk3");   // TOP-021: a disabled title is dimmed
            if (IsOpen(b)) b.Background = Br("LgPress");
            b.Click += (_, _) => OnTitlePress(idx);
            b.PointerEntered += (_, _) => Slide(idx);
            _left.Children.Add(b);
        }
        RenderRight();
        Fit();
    }

    /// One drawn stat cell, kept so a 1 s sample only changes its words (rebuilding the right side every second cost
    /// the idle bar its CPU budget in the UI tests' idle-CPU check).
    sealed class Cell { public Button B = null!; public Microsoft.UI.Xaml.Documents.Run V = null!; public TextBlock T = null!; public string Ink = "", Name = "", Tip = ""; }
    readonly Dictionary<string, Cell> _cells = new();

    void RenderRight()
    {
        _right.Children.Clear();
        _cells.Clear();
        if (_note != null) _right.Children.Add(NoteView(_note));
        if (_stats == null && _paused != null)
            _right.Children.Add(new TextBlock { Text = "Stats paused", Foreground = Br("LgInk2"), FontFamily = TextFont, FontSize = 11.5, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        else
        {
            double ch = Ch();
            foreach (var c in TopBarModel.StatCells(_stats))
            {
                var b = MakeButton("topbar.stat." + c.Key, c.Label + " " + c.Value);
                b.Tag = "stat:" + c.Key;
                b.Padding = new Thickness(8, 0, 8, 0);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                row.Children.Add(new TextBlock { Text = c.Label, FontSize = 11, FontWeight = FontWeights.SemiBold, CharacterSpacing = 20, Foreground = Br("LgInk2"), VerticalAlignment = VerticalAlignment.Center });
                double min = c.Key switch { "cpu" or "gpu" => 4.6, "mem" => 11.5, "net" => 20.5, "ping" => 6.1, _ => 0 } * ch;
                string ink = c.Why != null ? "LgInk2" : c.Warn ? "LgWarn" : "LgInk";
                var v = new TextBlock { MinWidth = min, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Br(ink) };
                var run = Tabular(c.Value);
                v.Inlines.Add(run);
                row.Children.Add(v);
                b.Content = row;
                var tip = c.Title ?? c.Why ?? "";
                if (tip.Length > 0) ToolTipService.SetToolTip(b, tip);
                _cells[c.Key] = new Cell { B = b, V = run, T = v, Ink = ink, Name = c.Label + " " + c.Value, Tip = tip };
                b.Click += (_, _) => OnStat(c.Key);
                _right.Children.Add(b);
            }
        }
        var timer = MakeButton("topbar.tool.timer", "Timer and stopwatch");
        timer.Tag = "tool:timer"; timer.Padding = new Thickness(7, 0, 7, 0); timer.Margin = new Thickness(4, 0, 0, 0);
        timer.Content = Glyph(TimerPath, 14, Br("LgInk"));
        ToolTipService.SetToolTip(timer, "Timer and stopwatch");
        timer.Click += (_, _) => OnTool("timer");
        var nb = TopBarModel.NotesButton(null);
        var notes = MakeButton("topbar.tool.notes", "Quick notes");
        notes.Tag = "tool:notes"; notes.Padding = new Thickness(7, 0, 7, 0);
        notes.Content = Glyph(NotePath, 14, Br("LgInk"));
        ToolTipService.SetToolTip(notes, nb.Title);
        notes.Click += (_, _) => OnTool("notes");
        _right.Children.Add(timer); _right.Children.Add(notes);
    }

    bool _tabularSaid;
    /// font-variant-numeric: tabular-nums. Typography.NumeralAlignment set on a TextBlock threw "Specified cast is not
    /// valid" on the runner (the attached property wants a text element), so it goes on a Run; a refusal is logged once
    /// and the cell keeps its fixed MinWidth either way.
    Microsoft.UI.Xaml.Documents.Run Tabular(string text)
    {
        var run = new Microsoft.UI.Xaml.Documents.Run { Text = text };
        try { Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(run, FontNumeralAlignment.Tabular); }
        catch (Exception e) when (e is InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            if (!_tabularSaid) { _tabularSaid = true; _ctx.Log.Line($"stat figures: tabular numerals could not be set ({e.Message}); the cells keep their fixed widths"); }
        }
        return run;
    }

    double _ch;
    /// CSS's 1ch at the strip's font: the advance of "0".
    double Ch()
    {
        if (_ch > 0) return _ch;
        var t = new TextBlock { Text = "0000000000", FontFamily = TextFont, FontSize = _dark ? 13 : 12.5 };
        t.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return _ch = Math.Max(1, t.DesiredSize.Width / 10);
    }

    /// TOP-024/025: the app name gives way first, then menus go into "»" from the right. Frozen while a menu is open (TOP-026).
    void Fit()
    {
        if (_openKey != null || _left.Children.Count < 2) return;
        var inf = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        var btns = _left.Children.OfType<Button>().Where(b => (b.Tag as string) != "more").ToList();
        foreach (var b in btns) { b.Visibility = Visibility.Visible; b.MaxWidth = double.PositiveInfinity; }
        _left.Children.OfType<Button>().Where(b => (b.Tag as string) == "more").ToList().ForEach(b => _left.Children.Remove(b));
        var w = btns.Select(b => { b.Measure(inf); return b.DesiredSize.Width; }).ToList();
        _right.Measure(inf);
        double avail = _root.ActualWidth - 2 * Pad - Gap - _right.DesiredSize.Width;
        if (avail <= 0) return;
        var more = MakeButton("topbar.more", "More menus"); more.Tag = "more"; more.Padding = new Thickness(8, 0, 8, 0);
        more.Content = new TextBlock { Text = "»", FontSize = 14, Foreground = Br("LgInk2") };
        ToolTipService.SetToolTip(more, "More menus");
        more.Measure(inf);
        var fit = TopBarModel.FitTitles(w, more.DesiredSize.Width, avail);
        if (fit.AppW is double aw && btns.Count > 1) btns[1].MaxWidth = aw;
        for (int i = fit.Shown; i < btns.Count; i++) btns[i].Visibility = Visibility.Collapsed;
        _fitShown = fit.Shown;
        if (fit.Shown < fit.N) { int mi = fit.Shown; more.Click += (_, _) => OnTitlePress(mi); more.PointerEntered += (_, _) => Slide(mi); _left.Children.Insert(fit.Shown, more); }
        if (_test) Note($"fit: {fit.Shown} of {fit.N} titles shown{(fit.AppW is double a ? $", app name at most {a:0} DIP" : "")}{(fit.Shown < fit.N ? ", the rest in »" : "")} ({avail:0} DIP of room)");
    }
    string _lastFit = "";
    void Note(string line) { if (line == _lastFit) return; _lastFit = line; _ctx.Log.Line(line); }

    // ------------------------------------------------------------------ the controls (the dropdowns are a later part)
    /// TOP-030: with a menu open, hovering another title opens that one (not from a stat or tool dropdown).
    void Slide(int ti) { if (_open != null && _open.Tool == null && _open.Ti != ti) OpenMenu(ti); }
    async void OnStat(string key)
    {
        var r = await _ctx.CallAsync("topbar.statsDetail", new JsonObject { ["kind"] = key });
        _ctx.Log.Line($"stat pressed: {key}" + (r.Ok ? "" : $" (its dropdown is not drawn yet: {r.Error})"));
    }
    async void OnTool(string tool)
    {
        var r = await _ctx.CallAsync(tool == "notes" ? "notes.toggle" : "timer.state");
        _ctx.Log.Line($"tool pressed: {tool}" + (r.Ok ? "" : $" ({r.Error})"));
    }

    // ------------------------------------------------------------------ the strip note (TOP-175)
    const string WarnPath = "M8,2.2 L14.2,13.1 H1.8 Z M8,6.4 V9.4 M8,11.2 V11.21";
    string? _note; DispatcherQueueTimer? _noteT;

    /// A note on the right before the stats (ui/topbar/topbar.js#setNote): it clears itself after 8 s if unchanged.
    void SetNote(string? text)
    {
        if (string.IsNullOrEmpty(text) && _note == null) return;   // the replay of "no note" at subscribe
        _note = string.IsNullOrEmpty(text) ? null : text;
        _noteT?.Stop();
        RenderRight(); Fit();
        _ctx.Log.Line(_note == null ? "note cleared" : $"note: {_note}");
        if (_note == null) return;
        string shown = _note;
        _noteT = _ctx.Dispatcher.CreateTimer();
        _noteT.Interval = TimeSpan.FromSeconds(8); _noteT.IsRepeating = false;
        _noteT.Tick += (t, _) => { t.Stop(); if (_note == shown) { _note = null; RenderRight(); Fit(); _ctx.Log.Line("note cleared after 8 s"); } };
        _noteT.Start();
    }

    /// The warn glyph and the words in ink, one line, cut at 60 characters with "…", the whole text as the tooltip
    /// (.warn: max-width min(360px, 28vw)).
    FrameworkElement NoteView(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(7, 2, 7, 2), VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Glyph(WarnPath, 12, Br("LgWarn"), 1.3));
        double vw = _root.ActualWidth > 0 ? _root.ActualWidth : 1280;
        row.Children.Add(new TextBlock
        {
            Text = text.Length > 60 ? text[..57] + "…" : text, FontFamily = TextFont, FontSize = 11.5, Foreground = Br("LgInk"),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Min(360, vw * 0.28) - 30,
            VerticalAlignment = VerticalAlignment.Center,
        });
        ToolTipService.SetToolTip(row, text);
        AutomationProperties.SetAutomationId(row, "topbar.note");
        AutomationProperties.SetName(row, text);
        return row;
    }

    // ------------------------------------------------------------------ the feature's pushes
    void OnFocus(TopBarFocus f)
    {
        if (!_reported) Report();
        var was = _focus?["hwnd"]?.ToJsonString();
        _focus = f.Json != null ? JsonNode.Parse(f.Json) as JsonObject : null;
        _menu = f.MenuJson != null ? JsonNode.Parse(f.MenuJson) as JsonObject : null;
        if (_open != null && was != _focus?["hwnd"]?.ToJsonString()) CloseAll(false, "another app came to the front");
        if (_open != null) { _focusDirty = true; RefreshTitles(); return; }   // the open menu and its title stay put; redrawn on close
        Render();
        _ctx.Log.Line($"focus: {(_focus == null ? "none" : TopBarModel.AppName(_focus))}, {_titles.Count} titles{(f.Fixture ? " (test fixture)" : "")}");
    }
    void OnStats(TopBarStats s)
    {
        _stats = s.Json != null ? JsonNode.Parse(s.Json) as JsonObject : null;
        _paused = s.Paused;
        if (_cells.Count > 0 && !(_stats == null && _paused != null)) { UpdateCells(); return; }   // cells stay cells: words only
        RenderRight();
        Fit();
    }

    /// A sample on cells already drawn: only what changed is set (fixed decimals and fixed minimum widths, so no re-fit).
    void UpdateCells()
    {
        foreach (var c in TopBarModel.StatCells(_stats))
        {
            if (!_cells.TryGetValue(c.Key, out var cell)) continue;
            if (cell.V.Text != c.Value) cell.V.Text = c.Value;
            string ink = c.Why != null ? "LgInk2" : c.Warn ? "LgWarn" : "LgInk";
            if (cell.Ink != ink) { cell.Ink = ink; cell.T.Foreground = Br(ink); }
            string name = c.Label + " " + c.Value;
            if (cell.Name != name) { cell.Name = name; AutomationProperties.SetName(cell.B, name); }
            string tip = c.Title ?? c.Why ?? "";
            if (cell.Tip != tip) { cell.Tip = tip; ToolTipService.SetToolTip(cell.B, tip.Length > 0 ? tip : null); }
        }
    }
    void OnVisible(TopBarVisible v)
    {
        if (!_reported) Report();
        if (v.Show) ShowWin(v.Why); else HideWin(v.Why);
    }

    void ShowWin(string why)
    {
        if (_shown && Win32.IsWindowVisible(_hwnd)) return;
        Place("before show");   // never drawn out of place (TOP-232)
        if (!_activated)
        {
            // WinUI starts drawing at the first Activate; the bar never keeps the keyboard (TOP-221 showInactive)
            _activated = true;
            IntPtr prev = Win32.GetForegroundWindow();
            _win.Activate();
            if (prev != IntPtr.Zero && prev != _hwnd && Win32.GetForegroundWindow() == _hwnd) Win32.SetForegroundWindow(prev);
        }
        else Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.StripFrame(_hwnd);
        if (!_test) Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        else if (Host.Quiet) Win32.SetWindowPos(_hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);   // TOP-222
        // a VISIBLE --test strip (UI tests, pairs): handing the foreground back above raised that window over the strip
        // (a full-screen backdrop hid it), so the strip goes to the top of the plain z-order, still not topmost or active
        else Win32.SetWindowPos(_hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        _shown = true;
        _ = _ctx.CallAsync("topbar.shown", new JsonObject { ["on"] = true });
        _ctx.Log.Line("shown (" + why + ")");
    }
    void HideWin(string why)
    {
        if (!_shown && !Win32.IsWindowVisible(_hwnd)) return;
        Win32.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        _shown = false;
        CloseAll(false, "the bar was hidden");
        _ = _ctx.CallAsync("topbar.shown", new JsonObject { ["on"] = false });
        _ctx.Log.Line("hidden (" + why + ")");
    }

    // ------------------------------------------------------------------ placement (TOP-215, TOP-240, TOP-218)
    void Place(string why)
    {
        IntPtr mon = Win32.MonitorFromPoint(default, 1 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (mon == IntPtr.Zero || !Win32.GetMonitorInfoW(mon, ref mi)) { _ctx.Log.Line("the primary display reports no size; the bar is not placed (" + why + ")"); return; }
        uint dpi = Win32.GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0 ? dx : 96;
        double s = dpi / 96.0;
        var r = mi.rcMonitor;
        int w = r.Right - r.Left, h = (int)Math.Round(_height * s, MidpointRounding.AwayFromZero);
        _bar.Height = TopBarRules.BarDip;
        _root.Height = _height;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        FrameNative.GetWindowRect(_hwnd, out var got);
        int gh = got.Bottom - got.Top;
        // TOP-218: a window Windows keeps taller than the bar is cropped to it, so nothing below draws or takes clicks
        string shape = "";
        if (gh > h) { WindowKinds.ClipToShape(_hwnd, 0, 0, got.Right - got.Left, h, 0); shape = $"; the window is {gh} px tall, cropped to {h}"; }
        Uncrop(gh, h);
        _workBottom = (mi.rcWork.Bottom - got.Top) / s;
        string line = $"placed at {got.Left},{got.Top} {got.Right - got.Left}x{h} px at {Math.Round(s * 100)} % (dpi {dpi}){shape}";
        if (line != _lastPlaced || why.StartsWith("display", StringComparison.Ordinal)) { _lastPlaced = line; _ctx.Log.Line(line + " (" + why + ")"); }
        _report = new JsonObject { ["hwnd"] = _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), ["scale"] = s, ["x"] = got.Left, ["y"] = got.Top, ["w"] = got.Right - got.Left, ["h"] = h };
        Report();
    }

    JsonObject? _report; bool _reported;
    /// Tell the feature where the window is (its hwnd for the app bar). The feature may install after the surface is
    /// made: until one report is answered, every push from the feature sends it again.
    async void Report()
    {
        if (_report == null) return;
        var r = await _ctx.CallAsync("topbar.placed", (JsonObject)_report.DeepClone());
        if (r.Ok && !_reported) { _reported = true; _ctx.Log.Line("the feature knows the window"); }
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        var res = FrameNative.DefSubclassProc(hwnd, msg, wp, lp);
        string? kind = null; string[]? changed = null;
        switch (msg)
        {
            case 0x007E: kind = "metrics"; changed = new[] { "bounds" }; break;                               // WM_DISPLAYCHANGE
            case 0x02E0: kind = "metrics"; changed = new[] { "scaleFactor" }; break;                          // WM_DPICHANGED (after WinUI's own handler)
            case 0x001A when wp == new IntPtr(0x2F): kind = "metrics"; changed = new[] { "workArea" }; break;  // WM_SETTINGCHANGE SPI_SETWORKAREA
        }
        if (kind != null)
        {
            var ch = changed!;
            _ctx.Dispatcher.TryEnqueue(() =>
            {
                Place("display: " + string.Join(", ", ch));
                _ = _ctx.CallAsync("topbar.display", new JsonObject { ["kind"] = kind, ["changed"] = new JsonArray(ch.Select(c => (JsonNode)c).ToArray()), ["primary"] = true });
            });
        }
        return res;
    }

    // ------------------------------------------------------------------ ISurface
    /// The test road (--test-surface=show:topbar) and the host: the window itself; the feature's rules decide otherwise.
    public void Show(string why) => ShowWin(why);
    public void Hide(string why) => HideWin(why);

    public void Destroy(string why)
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        _noteT?.Stop(); _noteT = null;
        try { _win?.Close(); } catch (Exception) { /* already gone */ }
        _shown = false;
        _ctx?.Log.Line("destroyed (" + why + ")");
    }
}
