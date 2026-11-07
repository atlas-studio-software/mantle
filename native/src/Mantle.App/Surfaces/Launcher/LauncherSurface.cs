using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Native;
using Mantle.App.Shell.Windowing;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Search;
using Mantle.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The launcher (ui/launcher): one frameless, see-through, always-on-top, non-resizable, no-taskbar window,
/// 680x460 DIP, centred on the primary display at every open, the pane inset 10 px (LAU-001/002). Created at
/// start and parked, so an open is a show (LAU-017). It renders only: rows come from <c>apps.search</c> on the
/// host, Enter is <c>apps.launch</c>; the grouping is Electron's groups.js (LauncherGroups), run here as the
/// page runs it. UI Automation ids are Electron's DOM ids: panel, q, results, hint, counts, notice.
/// Part 1: apps mode only (search/run/quick modes, the fold, the backdrop picture, file rows, calc: part 2).
/// </summary>
[Mantle.App.Shell.Surface("launcher", AtStart = true)]
internal sealed class LauncherSurface : Mantle.App.Shell.ISurface
{
    public const double WidthDip = 680, HeightDip = 460, InsetDip = 10;
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    Mantle.App.Shell.ISurfaceContext _ctx = null!;
    Window _win = null!;
    IntPtr _hwnd;
    Grid _root = null!;
    Border _panel = null!;
    TextBox _q = null!;
    StackPanel _list = null!;
    ScrollViewer _scroll = null!;
    TextBlock _hint = null!, _counts = null!, _notice = null!;
    bool _shown, _activated, _dark;
    int _seq;
    long _openedAt;
    DispatcherQueueTimerLike? _debounce;
    LauncherGroups.Result _view = new([], []);
    int _sel;
    readonly List<Grid> _rows = [];

    public bool Shown => _shown;

    /// For the bench and the UI tests: ms from Show() to the first rows painted, and from a keystroke to its rows.
    public static double LastOpenMs { get; private set; } = -1;
    public static double LastTypedToRowsMs { get; private set; } = -1;

    public void Create(Mantle.App.Shell.ISurfaceContext ctx)
    {
        _ctx = ctx;
        _root = (Grid)XamlReader.Load(
            "<Grid " + Ns + ">" +
            "<Border x:Name='panel' Margin='10' CornerRadius='20' Background='{ThemeResource LgFillPop}' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='1'>" +
            "<Grid><Grid.RowDefinitions><RowDefinition Height='58'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>" +
            "<Border Grid.Row='0' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='0,0,0,1' Padding='16,0'>" +
            "<TextBox x:Name='q' VerticalAlignment='Center' FontSize='15' BorderThickness='0' Background='Transparent' Padding='0' " +
            "Foreground='{ThemeResource LgInk}' PlaceholderForeground='{ThemeResource LgInk3}' PlaceholderText='Apps, files, settings, commands, or a URL'/></Border>" +
            "<ScrollViewer Grid.Row='1' x:Name='scroll' Padding='6' VerticalScrollBarVisibility='Auto'><StackPanel x:Name='results'/></ScrollViewer>" +
            "<Border Grid.Row='2' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='0,1,0,0' Padding='14,7,14,8'><StackPanel>" +
            "<TextBlock x:Name='notice' Visibility='Collapsed' Foreground='{ThemeResource LgWarn}' FontSize='12' TextWrapping='Wrap' Margin='0,0,0,6'/>" +
            "<Grid><TextBlock x:Name='hint' FontSize='12' Foreground='{ThemeResource LgInk3}'/>" +
            "<TextBlock x:Name='counts' FontSize='12' HorizontalAlignment='Right' Foreground='{ThemeResource LgInk3}' Text='Esc Close'/></Grid>" +
            "</StackPanel></Border></Grid></Border></Grid>");
        _panel = (Border)_root.FindName("panel");
        _q = (TextBox)_root.FindName("q");
        _scroll = (ScrollViewer)_root.FindName("scroll");
        _list = (StackPanel)_root.FindName("results");
        _hint = (TextBlock)_root.FindName("hint");
        _counts = (TextBlock)_root.FindName("counts");
        _notice = (TextBlock)_root.FindName("notice");
        foreach (var (el, id) in new (FrameworkElement, string)[] { (_panel, "panel"), (_q, "q"), (_list, "results"), (_hint, "hint"), (_counts, "counts"), (_notice, "notice") })
            AutomationProperties.SetAutomationId(el, id);
        AutomationProperties.SetName(_q, "Search apps, files, settings, commands, power actions, or a URL");
        AutomationProperties.SetName(_list, "Results");

        _win = new Window { Title = "Mantle launcher", Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        _win.SystemBackdrop = new TransparentBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        int round = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        WindowKinds.Frameless(_hwnd);
        WindowKinds.StripFrame(_hwnd);
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW;
        // LAU-020 / WIN-028: a --test launcher is shown inactive, draws nothing and lets clicks through
        if (ctx.Test) ex |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (ctx.Test && Environment.GetEnvironmentVariable("MANTLE_TEST_VISIBLE") != "1") Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(_hwnd);

        _q.TextChanged += (_, _) => QueueSearch();
        _q.PreviewKeyDown += OnKey;
        _win.Activated += (_, e) =>
        {
            // LAU-025: focus lost to another window closes it (live only; a --test launcher is never active)
            if (e.WindowActivationState == WindowActivationState.Deactivated && _shown && !_ctx.Test) Hide("blur");
        };
        ctx.On<ThemePayload>("theme", t => SetTheme(t.View.Theme == "dark"));
        ctx.Log.Line("launcher: created (parked, " + (ctx.Test ? "test: inactive, invisible" : "live") + ")");
    }

    void SetTheme(bool dark)
    {
        _dark = dark;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _panel.CornerRadius = new CornerRadius(dark ? 18 : 20);           // LAU-037: dark uses --lg-r-xl
        _q.FontSize = dark ? 16 : 15;
        Render();
    }

    /// LAU-002: centred on the primary display as it is NOW, at every open.
    void Place()
    {
        var area = DisplayArea.Primary;
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)Math.Round(WidthDip * scale), h = (int)Math.Round(HeightDip * scale);
        if (area == null || area.WorkArea.Width <= 0) { _ctx.Log.Line("launcher: the primary display reports no size; opening where it was"); return; }
        var o = area.OuterBounds;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, o.X + (o.Width - w) / 2, o.Y + (o.Height - h) / 2, w, h, Win32.SWP_NOACTIVATE | 0x0004);
    }

    public void Show(string why)
    {
        if (_shown) return;                                                 // LAU-009/011: an open over an open launcher does nothing
        _openedAt = Environment.TickCount64;
        LastOpenMs = -1;
        Place();
        if (!_activated) { _activated = true; if (_ctx.Test) Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE); else _win.Activate(); }
        else if (_ctx.Test) Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        else { Win32.ShowWindow(_hwnd, 5 /* SW_SHOW */); Win32.SetForegroundWindow(_hwnd); }
        WindowKinds.StripFrame(_hwnd);
        _shown = true;
        if (!_ctx.Test) _q.Focus(FocusState.Programmatic);                   // LAU-046
        _q.SelectAll();
        _ctx.Log.Line("launcher: shown (" + why + ")");
        _ = _ctx.CallAsync("apps.index");                                    // LAU-018: re-checked on every open
        RunSearch(_q.Text);
    }

    public void Hide(string why)
    {
        if (!_shown) return;
        _shown = false;
        Win32.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        _ctx.Log.Line("launcher: hidden (" + why + ")");
    }

    public void Destroy(string why)
    {
        _shown = false;
        try { _win.Close(); } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _ctx.Log.Line("launcher: destroyed (" + why + ")");
    }

    // ------------------------------------------------------------------ search (LAU-016: 12 ms debounce)

    void QueueSearch()
    {
        long typedAt = Environment.TickCount64;
        _debounce?.Stop();
        _debounce = DispatcherQueueTimerLike.After(_ctx.Dispatcher, 12, () => RunSearch(_q.Text, typedAt));
    }

    async void RunSearch(string q, long typedAt = 0)
    {
        int seq = ++_seq;
        var r = await _ctx.CallAsync("apps.search", new JsonObject { ["q"] = q });
        if (seq != _seq) return;                                             // a superseded query's answer is dropped (LAU-077)
        if (!r.Ok)
        {
            ShowProblem("The search did not answer (" + r.Error + "). Type again to retry; files and the web may still work.");
            return;
        }
        var hits = new List<SearchHit>();
        foreach (var e in r.Result.GetProperty("items").EnumerateArray()) hits.Add(new SearchHit(ItemOf(e), e.TryGetProperty("score", out var s) ? s.GetInt32() : null));
        string? keepId = _view.Flat.Count > _sel ? _view.Flat[_sel].Item.Id : null;
        _view = LauncherGroups.Build(q, hits);
        _sel = 0;
        if (keepId != null && typedAt == 0) { int at = _view.Flat.ToList().FindIndex(x => x.Item.Id == keepId); if (at >= 0) _sel = at; }
        Footer(r.Result.GetProperty("sources"));
        Render();
        if (typedAt != 0) LastTypedToRowsMs = Environment.TickCount64 - typedAt;
        if (LastOpenMs < 0 && _shown) { LastOpenMs = Environment.TickCount64 - _openedAt; _ctx.Log.Line("launcher: first rows " + LastOpenMs + " ms after the open"); }
    }

    static LauncherItem ItemOf(JsonElement e)
    {
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new LauncherItem { Id = S("id") ?? "", Kind = S("kind") ?? "", Name = S("name") ?? "", Subtitle = S("subtitle") ?? "", Path = S("path") ?? "", Glyph = S("glyph"), IconKey = S("iconKey"), Confirm = S("confirm") };
    }

    static readonly Dictionary<string, string> SourceWords = new()
    {
        ["apps"] = "Your apps could not be listed, so only settings, files and the web can be searched.",
        ["commands"] = "Your PowerShell commands could not be read.",
        ["settings"] = "Settings pages could not be listed.",
        ["power"] = "Lock, Sleep and the other power options could not be listed.",
    };

    void Footer(JsonElement sources)
    {
        var bad = sources.EnumerateArray().Where(s => s.GetProperty("status").GetString() != "ok").ToList();
        if (bad.Count == 0) { _notice.Visibility = Visibility.Collapsed; return; }
        var s0 = bad[0];
        string name = s0.GetProperty("name").GetString() ?? "";
        _notice.Text = SourceWords.TryGetValue(name, out var w) ? w : "Part of the search is unavailable right now.";
        ToolTipService.SetToolTip(_notice, name + ": " + s0.GetProperty("reason").GetString());
        _notice.Visibility = Visibility.Visible;
    }

    void ShowProblem(string text)
    {
        _list.Children.Clear(); _rows.Clear();
        ShowProblemLine(text, warn: true);
    }

    // ------------------------------------------------------------------ rows (LAU-063/067/068/071)

    // Parsed once: rows, headers and lines are made from these so {ThemeResource} resolves in the window's own theme.
    static DataTemplate? _rowT, _headT, _lineT;
    static DataTemplate T(string body) => (DataTemplate)XamlReader.Load("<DataTemplate " + Ns + ">" + body + "</DataTemplate>");
    static void Templates()
    {
        _rowT ??= T("<Grid Height='44'>" +
            "<Border x:Name='wash' CornerRadius='8' Background='{ThemeResource LgTile}' Opacity='0'/>" +
            "<Border x:Name='washDark' CornerRadius='8' Background='{ThemeResource LgPress}' BorderBrush='{ThemeResource LgEdgeOut}' BorderThickness='1' Opacity='0'/>" +
            "<Border x:Name='tick' Width='3' HorizontalAlignment='Left' Margin='0,13' CornerRadius='2' Background='{ThemeResource LgAcc}' Opacity='0'/>" +
            "<Grid Padding='10,0,12,0' ColumnSpacing='12'><Grid.ColumnDefinitions><ColumnDefinition Width='24'/><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
            "<Border Width='24' Height='24' CornerRadius='6' Background='{ThemeResource LgSunk}' VerticalAlignment='Center'>" +
            "<TextBlock x:Name='letter' FontSize='12' FontWeight='SemiBold' Foreground='{ThemeResource LgInk2}' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
            "<TextBlock x:Name='name' Grid.Column='1' FontSize='14' Foreground='{ThemeResource LgInk}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>" +
            "<TextBlock x:Name='sub' Grid.Column='2' MaxWidth='290' FontSize='12.5' Foreground='{ThemeResource LgInk3}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>" +
            "</Grid></Grid>");
        _headT ??= T("<TextBlock FontSize='13' FontWeight='SemiBold' Foreground='{ThemeResource LgInk}' Margin='12,10,12,3'/>");
        _lineT ??= T("<TextBlock FontSize='12.5' TextWrapping='Wrap' Foreground='{ThemeResource LgInk2}' Margin='16,10,12,2'/>");
    }

    void ShowProblemLine(string text, bool warn)
    {
        Templates();
        var t = (TextBlock)_lineT!.LoadContent();
        t.Text = text;
        if (warn) t.Foreground = _notice.Foreground;             // --lg-warn, as the notice line
        _list.Children.Add(t);
    }

    void Render()
    {
        _list.Children.Clear(); _rows.Clear();
        int i = 0;
        foreach (var g in _view.Groups)
        {
            Templates();
            var head = (TextBlock)_headT!.LoadContent();
            head.Text = g.Label;
            if (i == 0) head.Margin = new Thickness(12, 4, 12, 3);
            _list.Children.Add(head);
            foreach (var row in g.Rows) _list.Children.Add(RowEl(row, i++));
        }
        if (_view.Flat.Count == 0)
            ShowProblemLine(_q.Text.Trim().Length == 0 ? "No apps to show yet. Type to search files, settings and the web." : "Nothing matches ‘" + _q.Text.Trim() + "’", warn: false);
        Select(_sel, false);
        string enter = _view.Flat.Count > _sel ? EnterVerb(_view.Flat[_sel].Item) : "";
        _hint.Text = (enter.Length > 0 ? "Enter " + enter + "     " : "") + (_view.Groups.Count(x => x.Rows.Count > 0) > 1 ? "Tab Next group" : "");
        AutomationProperties.SetName(_counts, _view.Flat.Count == 0 ? "No results" : _view.Flat.Count == 1 ? "1 result" : _view.Flat.Count + " results");
    }

    static string EnterVerb(LauncherItem it) => it.Kind switch
    {
        "command" => "Run", "web" => "Search the web", "power" => it.Name, _ => "Open",
    };

    Grid RowEl(LauncherRow row, int index)
    {
        Templates();
        var it = row.Item;
        var g = (Grid)_rowT!.LoadContent();
        ((TextBlock)g.FindName("letter")).Text = it.Name.Length > 0 ? it.Name.Substring(0, 1).ToUpperInvariant() : "?";
        var name = (TextBlock)g.FindName("name");
        name.Text = it.Name.Length > 0 ? it.Name : "(unnamed)";
        var sub = (TextBlock)g.FindName("sub");
        sub.Text = it.Subtitle;
        if (_dark) { name.FontSize = 13; sub.FontSize = 12; }                    // LAU-037: 16/13/12 in dark
        if (index > 0) g.Margin = new Thickness(0, 1, 0, 0);
        AutomationProperties.SetAutomationId(g, "row-" + it.Id);
        AutomationProperties.SetName(g, it.Name);
        ToolTipService.SetToolTip(g, it.Name + (it.Subtitle.Length > 0 ? " · " + it.Subtitle : ""));
        g.PointerMoved += (_, _) => { if (_sel != index) Select(index, false); };   // LAU-056: mousemove, not mouseover
        g.Tapped += (_, _) => { Select(index, false); Activate(); };
        _rows.Add(g);
        return g;
    }

    void Select(int index, bool scroll = true)
    {
        if (_rows.Count == 0) { _sel = 0; return; }
        _sel = Math.Clamp(index, 0, _rows.Count - 1);
        for (int i = 0; i < _rows.Count; i++)
        {
            bool on = i == _sel;
            // light: --lg-tile wash + 3 px accent tick (LAU-038); dark: --lg-press wash + hairline ring, no tick (LAU-037)
            ((UIElement)_rows[i].FindName("wash")).Opacity = on && !_dark ? 1 : 0;
            ((UIElement)_rows[i].FindName("tick")).Opacity = on && !_dark ? 1 : 0;
            ((UIElement)_rows[i].FindName("washDark")).Opacity = on && _dark ? 1 : 0;
            if (on) AutomationProperties.SetItemStatus(_rows[i], "selected");
        }
        if (scroll) _rows[_sel].StartBringIntoView();
        string enter = _view.Flat.Count > _sel ? EnterVerb(_view.Flat[_sel].Item) : "";
        _hint.Text = (enter.Length > 0 ? "Enter " + enter + "     " : "") + (_view.Groups.Count(x => x.Rows.Count > 0) > 1 ? "Tab Next group" : "");
    }

    // ------------------------------------------------------------------ keys (LAU-048..054)

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        int n = _rows.Count;
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        int page = Math.Max(1, (int)(_scroll.ViewportHeight / 45) - 1);
        switch (e.Key)
        {
            case VirtualKey.Down: if (n > 0) Select((_sel + 1) % n); break;                       // wraps (LAU-048)
            case VirtualKey.Up: if (n > 0) Select((_sel - 1 + n) % n); break;
            case VirtualKey.PageDown: if (n > 0) Select(Math.Min(n - 1, _sel + page)); break;     // clamped, never wraps (LAU-049)
            case VirtualKey.PageUp: if (n > 0) Select(Math.Max(0, _sel - page)); break;
            case VirtualKey.Home: if (n > 0) Select(0); break;                                     // LAU-050
            case VirtualKey.End: if (n > 0) Select(n - 1); break;
            case VirtualKey.Tab: if (n > 0) Select(LauncherGroups.JumpGroup(_view.Groups, _sel, shift ? -1 : 1)); break;   // LAU-051
            case VirtualKey.Enter: if (!e.KeyStatus.WasKeyDown) Activate(); break;               // a held Enter is ignored (LAU-052)
            case VirtualKey.Escape: Hide("Esc"); break;                                          // LAU-054
            default: return;
        }
        e.Handled = true;
    }

    async void Activate()
    {
        if (_view.Flat.Count <= _sel) return;
        var it = _view.Flat[_sel].Item;
        if (it.Kind == "web") { _ctx.Log.Line("launcher: web search is part 2"); return; }
        var r = await _ctx.CallAsync("apps.launch", new JsonObject { ["id"] = it.Id });
        _ctx.Log.Line("launcher: Enter on \"" + it.Name + "\": " + (r.Ok ? "ok" : "FAILED " + r.Error) + (r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var why) ? " (" + why.GetString() + ")" : ""));
        if (r.Ok) Hide("launched");
        else ShowProblem("That did not open: " + r.Error);
    }
}

/// <summary>A one-shot timer on a DispatcherQueue (the 12 ms search debounce).</summary>
internal sealed class DispatcherQueueTimerLike
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _t;
    DispatcherQueueTimerLike(Microsoft.UI.Dispatching.DispatcherQueueTimer t) { _t = t; }
    public static DispatcherQueueTimerLike After(Microsoft.UI.Dispatching.DispatcherQueue q, int ms, Action run)
    {
        var t = q.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.IsRepeating = false;
        t.Tick += (_, _) => run();
        t.Start();
        return new DispatcherQueueTimerLike(t);
    }
    public void Stop() => _t.Stop();
}
