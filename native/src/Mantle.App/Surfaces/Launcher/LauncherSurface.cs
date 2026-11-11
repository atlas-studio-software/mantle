using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Native;
using Mantle.App.Shell.Windowing;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Search;
using Mantle.Core.Look;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The launcher (ui/launcher): one frameless, see-through, always-on-top, non-resizable, no-taskbar window,
/// 680x460 DIP, centred on the primary display at every open, the pane inset 10 px (LAU-001/002). Created at
/// start and parked, so an open is a show (LAU-017). It renders only: rows come from <c>apps.search</c> on the
/// host, Enter is <c>apps.launch</c>; the grouping is Electron's groups.js (LauncherGroups), run here as the
/// page runs it. UI Automation ids are Electron's DOM ids: panel, q, results, hint, counts, notice.
/// Every element is made in code (never XamlReader + FindName: in the AOT build an element that comes back as a
/// plain object may be a bare COM object, and the cast to its class throws "Specified cast is not valid").
/// In --test it follows TestQuiet: never activated, alpha 0 and click-through unless MANTLE_TEST_VISIBLE=1;
/// MANTLE_TEST_LAUNCHER_KEYS=1 (with MANTLE_TEST_VISIBLE=1, a CI runner only) lets a UI test give it the keyboard.
/// Part 1/2: apps mode only (search/run/quick modes, the fold, the backdrop picture, file rows, calc: later).
/// </summary>
[Mantle.App.Shell.Surface("launcher", AtStart = true)]
internal sealed partial class LauncherSurface : Mantle.App.Shell.ISurface
{
    public const double WidthDip = 680, HeightDip = 460, InsetDip = 10;
    public const string KeysEnv = "MANTLE_TEST_LAUNCHER_KEYS";

    /// A pooled row: made once, then refilled in place on every search (no new elements per keystroke).
    sealed class Row
    {
        public required Grid Root;
        public required Border Wash, Tick, Tile;
        public required TextBlock Letter, Name, Sub;
        public required Grid GlyphBox;
        public required Image Pic;
        public int Index;
        public string? Id, Tip, GlyphName, IconUrl;
    }

    /// The host's index, searched on this thread (set by the host's launcher block before the surfaces start). Null: apps.search on the bus.
    public static Mantle.Core.Features.Run.LauncherService? Service { get; set; }

    Mantle.App.Shell.ISurfaceContext _ctx = null!;
    LauncherTokens _tok = null!;
    Window _win = null!;
    IntPtr _hwnd;
    Grid _root = null!;
    Border _panel = null!, _edge = null!, _lip = null!, _slot = null!, _focusLine = null!, _foot = null!;
    TextBox _q = null!;
    StackPanel _list = null!;
    ScrollViewer _scroll = null!;
    StackPanel _hint = null!;
    Grid _panelGrid = null!;
    Grid _hintRow = null!;
    TextBlock _counts = null!, _notice = null!;
    StackPanel _noticeRow = null!;
    Grid _noticeGlyph = null!;
    Border _bdrop = null!, _fill = null!, _dim = null!;
    Image _shadow = null!;
    readonly Dictionary<string, SolidColorBrush> _qBrushes = new();
    IDisposable? _themeSub;
    bool _shown, _activated, _dark, _keys, _clearHidden;
    int _seq;
    long _openTs, _typedTs, _lastKeyTs;
    DispatcherQueueTimerLike? _debounce;
    LauncherGroups.Result _view = new([], []);
    int _sel;
    readonly List<Row> _rows = [];                                        // the rows on screen now, in order
    readonly List<Row> _pool = [];                                        // every row made in this theme (reused in order)
    readonly List<TextBlock> _heads = [];                                 // group headings, pooled the same way
    readonly List<UIElement> _want = [];
    readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> _pics = new(StringComparer.Ordinal);
    LauncherLook? _look;

    public bool Shown => _shown;

    /// For the bench and the UI tests: ms from Show() to the first frame with rows, and from a keystroke to its rows on screen.
    public static double LastOpenMs { get; private set; } = -1;
    public static double LastTypedToRowsMs { get; private set; } = -1;

    static double Ms(long from) => (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;

    public void Create(Mantle.App.Shell.ISurfaceContext ctx)
    {
        _ctx = ctx;
        _tok = new LauncherTokens(ctx.Log);
        _keys = ctx.Test && !Mantle.App.Host.Quiet && Environment.GetEnvironmentVariable(KeysEnv) == "1";
        Build();
        _look = new LauncherLook(ctx.Dispatcher, ctx.Log, _panel, _bdrop, _shadow, _dim);
        _look.NoPictureChanged += Footer;

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
        // LAU-020 / WIN-028 (TestQuiet): a --test launcher is never activated; quiet, it is alpha 0 and click-through
        if (ctx.Test && !_keys) ex |= Win32.WS_EX_NOACTIVATE;
        if (Mantle.App.Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Mantle.App.Host.Quiet) Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(_hwnd);

        // TextChanging, not TextChanged: it comes synchronously with the edit, so the rows are made before the frame that
        // draws the new text (TextChanged is posted after it, which put some keystrokes' rows one frame late: LAU-016)
        _q.TextChanging += (_, e) => { if (e.IsContentChanging) QueueSearch(); };
        _q.PreviewKeyDown += OnKey;
        _q.GotFocus += (_, _) => FocusLine();                             // .slot:focus-within::after
        _q.LostFocus += (_, _) => FocusLine();
        _q.Loaded += (_, _) => HideClearButton();
        _win.Activated += (_, e) =>
        {
            // LAU-025: focus lost to another window closes it (live only; a --test launcher is never active)
            if (e.WindowActivationState == WindowActivationState.Deactivated && _shown && !_ctx.Test) Hide("blur");
        };
        ApplyTheme(dark: false);
        _themeSub = ctx.On<ThemePayload>("theme", t =>
        {
            bool d = t.View.Theme == "dark";
            if (d != _dark) ApplyTheme(d);
            if (ctx.Test) ctx.Log.Line("theme " + t.View.Theme + " reached it");
        });
        QuickCreate();                                                     // Win+X (LauncherSurface.Quick.cs)
        ctx.Log.Line("created, hidden and parked (" + (!ctx.Test ? "live" : Mantle.App.Host.Quiet ? "test: never activated, alpha 0, click-through"
            : _keys ? "test, visible; " + KeysEnv + "=1: it takes the keyboard when shown" : "test, visible: never activated") + ")");
    }

    StackPanel? _hintEnd;

    /// The accent line under the query (.slot:focus-within::after): while the query has focus, whether or not the window is
    /// active (Chromium keeps :focus-within in an inactive window; the parity pairs show Electron's --test launcher with it).
    void FocusLine() => _focusLine.Opacity = _q.FocusState != FocusState.Unfocused ? 1 : 0;

    // ------------------------------------------------------------------ the tree (ui/launcher/index.html + launcher.css)

    void Build()
    {
        _q = new TextBox
        {
            VerticalAlignment = VerticalAlignment.Center, FontSize = 15, BorderThickness = new Thickness(0), Padding = new Thickness(0),
            MinHeight = 0, Background = new SolidColorBrush(LauncherTokens.None), IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
            PlaceholderText = "Apps, files, settings, commands, or a URL",
        };
        // lightweight styling: the TextBox template's own states (hover, focus) keep the line plain, as #q (border 0, transparent)
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled",
                     "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused",
                     "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
        {
            var b = new SolidColorBrush(LauncherTokens.None);
            _qBrushes[k] = b;
            _q.Resources[k] = b;
        }
        try { _q.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0); }
        catch (Exception e) { _ctx.Log.Line("the search line's focus border could not be turned off (" + e.Message + "); it may draw a line under the text"); }

        // bottom: -1px of the slot's padding box = ON the divider's own row (the pane does not change height), not below it
        _focusLine = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, IsHitTestVisible = false };
        var slotGrid = new Grid();
        _slot = new Border { Height = 58, Padding = new Thickness(16, 0, 16, 0), BorderThickness = new Thickness(0, 0, 0, 1), Child = _q };
        slotGrid.Children.Add(_slot);
        slotGrid.Children.Add(_focusLine);

        _list = new LauncherStack(Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.List);
        _scroll = new ScrollViewer { Padding = new Thickness(6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        Grid.SetRow(_scroll, 1);

        _notice = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 600 };
        _hint = new LauncherStack(Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.Group) { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        _counts = new TextBlock { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };   // .sr-only
        _hintRow = new Grid { MinHeight = 20 };
        _hintRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _hintRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hintRow.Children.Add(_hint);
        _hintRow.Children.Add(_counts);
        var footStack = new StackPanel();
        // .src-error: the warning triangle (launcher.css ::before mask, 12 px) then the words
        _noticeGlyph = new Grid { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 6, 0) };
        _notice.Margin = new Thickness(0);
        _noticeRow = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
        _noticeRow.Children.Add(_noticeGlyph);
        _noticeRow.Children.Add(_notice);
        footStack.Children.Add(_noticeRow);
        footStack.Children.Add(_hintRow);
        _foot = new Border { Padding = new Thickness(14, 7, 14, 8), BorderThickness = new Thickness(0, 1, 0, 0), Child = footStack };
        Grid.SetRow(_foot, 2);

        var inner = new Grid();
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        inner.Children.Add(slotGrid);
        inner.Children.Add(_scroll);
        inner.Children.Add(_foot);

        // .panel: the fill (the pane's layers as one colour), the 1 px edge and the lip (inset 0 1px 0) drawn over it
        _edge = new Border { BorderThickness = new Thickness(1), IsHitTestVisible = false };
        _lip = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(1), IsHitTestVisible = false };
        // .bdrop (the blurred picture of what is behind), then the pane's fill over it, the content, the lip and the edge;
        // the unfold's dim on the moving leaf (the right half) above everything, off at rest
        _bdrop = new Border { IsHitTestVisible = false };
        _fill = new Border { IsHitTestVisible = false };
        _dim = new Border
        {
            IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Width = (WidthDip - 2 * InsetDip) / 2,
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
                GradientStops = { new GradientStop { Color = LauncherTokens.Dim(26), Offset = 0 }, new GradientStop { Color = LauncherTokens.Dim(51), Offset = .5 }, new GradientStop { Color = LauncherTokens.Dim(145), Offset = 1 } },
            },
        };
        _panelGrid = new LauncherGrid(Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.Group); var panelGrid = _panelGrid;
        panelGrid.Children.Add(_bdrop);
        panelGrid.Children.Add(_fill);
        panelGrid.Children.Add(inner);
        panelGrid.Children.Add(_lip);
        panelGrid.Children.Add(_edge);
        panelGrid.Children.Add(_dim);
        _panel = new Border { Margin = new Thickness(InsetDip), Child = panelGrid };
        // the light drop shadow fills the window round the pane (nothing under the pane itself)
        _shadow = new Image { Width = WidthDip, Height = HeightDip, Stretch = Stretch.Fill, IsHitTestVisible = false };
        _root = new Grid();
        _root.Children.Add(_shadow);
        _root.Children.Add(_panel);

        foreach (var (el, id) in new (FrameworkElement, string)[] { (_panelGrid, "panel"), (_q, "q"), (_list, "results"), (_hint, "hint"), (_counts, "counts"), (_notice, "notice") })
            AutomationProperties.SetAutomationId(el, id);
        AutomationProperties.SetName(_q, "Search apps, files, settings, commands, power actions, or a URL");
        AutomationProperties.SetName(_list, "Results");
        AutomationProperties.SetLiveSetting(_counts, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
    }

    /// The TextBox template's clear (x) button: #q is a plain text input with none.
    void HideClearButton()
    {
        if (_clearHidden) return;
        if (Named(_q, "DeleteButton", 0) is FrameworkElement b)
        {
            b.Width = 0; b.MinWidth = 0; b.Margin = new Thickness(0); b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false;
            _clearHidden = true;
        }
        else _ctx.Log.Line("the search line's clear button was not found in its template, so it may show while you type");
    }

    static DependencyObject? Named(DependencyObject d, string name, int depth)
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is FrameworkElement fe && fe.Name == name) return c;
            if (depth < 16 && Named(c, name, depth + 1) is { } f) return f;
        }
        return null;
    }

    // ------------------------------------------------------------------ theme (LAU-037/038)

    void ApplyTheme(bool dark)
    {
        _dark = dark;
        var t = _tok;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        double r = dark ? 18 : 20;                                           // --lg-r-xl in dark, --panel-r in light
        _panel.CornerRadius = _edge.CornerRadius = _bdrop.CornerRadius = _fill.CornerRadius = new CornerRadius(r);
        _dim.CornerRadius = new CornerRadius(0, r, r, 0);
        // the pooled rows and headings carry this theme's sizes and brushes: made again on the next render
        _pool.Clear(); _heads.Clear(); _empty = null; _list?.Children.Clear(); _rows.Clear(); _hintKey = "";
        if (!_shown && _look?.Folding == true) _win?.AppWindow.Hide();        // a fold cut short by the theme still ends hidden
        _look?.Rest();
        _look?.ClearPicture();
        if (_shadow != null) _shadow.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;   // no outer shadow in dark
        _lip.CornerRadius = new CornerRadius(r - 1);
        // .panel::after over what is behind (the window is see-through; the blurred backdrop picture is part 2):
        // light: --pane-fill, --lg-fill, --pane-fill, the --lg-sunk recess, bottom to top; dark: one --lg-fill
        _fill.Background = new SolidColorBrush(dark ? t.Of("LgFill", true)
            : LauncherTokens.Over(t.Of("LgFillStrong", false), t.Of("LgFill", false), t.Of("LgFillStrong", false), t.Of("LgSunk", false)));
        _edge.BorderBrush = t.Brush("LgEdgeOut", dark);
        _lip.BorderBrush = t.Brush("LgLip", dark);                          // --lg-lip-up: inset 0 1px 0
        var line = t.Brush(dark ? "LgDivider" : "LgEdgeOut", dark);
        _slot.BorderBrush = line;
        _foot.BorderBrush = line;
        _focusLine.Background = t.Brush("LgAcc", dark);
        _q.FontSize = dark ? 16 : 15;
        foreach (var (k, b) in _qBrushes)
            b.Color = k.Contains("Placeholder") ? t.Of("LgInk3", dark) : k.Contains("Foreground") ? t.Of("LgInk", dark) : LauncherTokens.None;
        _q.Foreground = t.Brush("LgInk", dark);
        _q.SelectionHighlightColor = new SolidColorBrush(t.Of("LgAcc", dark));
        _notice.Foreground = t.Brush("LgWarn", dark);
        LauncherGlyphs.Warn(_noticeGlyph, t.Brush("LgWarn", dark));
        Render();
        RenderHint();
    }

    /// LAU-002: centred on the primary display as it is NOW, at every open.
    /// Returns the window's physical rectangle, the display's, and the scale (null when the display reports no size).
    (Win32.RECT Win, Win32.RECT Screen, double Scale)? Place()
    {
        var area = DisplayArea.Primary;
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        int w = (int)Math.Round(WidthDip * scale), h = (int)Math.Round(HeightDip * scale);
        if (area == null || area.WorkArea.Width <= 0) { _ctx.Log.Line("the primary display reports no size; opening where it was"); return null; }
        var o = area.OuterBounds;
        int x = o.X + (o.Width - w) / 2, y = o.Y + (o.Height - h) / 2;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Win32.SWP_NOACTIVATE | 0x0004);
        return (new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h }, new Win32.RECT { Left = o.X, Top = o.Y, Right = o.X + o.Width, Bottom = o.Y + o.Height }, scale);
    }

    /// Before the window shows: the light shadow for this size and the picture of what is behind the pane.
    void Glass((Win32.RECT Win, Win32.RECT Screen, double Scale)? at)
    {
        if (at is not { } p || _look == null) return;
        int ins = (int)Math.Round(InsetDip * p.Scale);
        _look.Shadow(_dark, p.Win.Right - p.Win.Left, p.Win.Bottom - p.Win.Top, p.Scale, InsetDip, _dark ? 18 : 20);
        if (_look.Folding) return;                                          // still on screen from a fold: keep the picture it has
        var pane = new Win32.RECT { Left = p.Win.Left + ins, Top = p.Win.Top + ins, Right = p.Win.Right - ins, Bottom = p.Win.Bottom - ins };
        _look.Picture(pane, p.Screen, p.Scale, _dark);
    }

    public void Show(string why)
    {
        if (_shown) return;                                                 // LAU-009/011: an open over an open launcher does nothing
        _openTs = Stopwatch.GetTimestamp();
        LastOpenMs = -1;
        Glass(Place());
        bool takeKeys = !_ctx.Test || _keys;
        if (!takeKeys) _win.AppWindow.Show(false);                          // TestQuiet: never activated
        else if (!_activated) _win.Activate();
        else { Win32.ShowWindow(_hwnd, 5 /* SW_SHOW */); Win32.SetForegroundWindow(_hwnd); }
        _activated = true;
        WindowKinds.StripFrame(_hwnd);
        _shown = true;
        if (takeKeys) _q.Focus(FocusState.Programmatic);                   // LAU-046
        _q.SelectAll();
        _ctx.Log.Line("shown (" + why + ")");
        _look?.Unfold(WidthDip - 2 * InsetDip, InsetDip);                  // ui/shared/unfold-live.js open, size 'pop'
        _ = _ctx.CallAsync("apps.index");                                    // LAU-018: re-checked on every open
        if (!QuickOpening()) RunSearch(_q.Text, 0, opening: true);
    }

    public void Hide(string why)
    {
        if (!_shown) return;
        _shown = false;
        FlushSearch();
        QuickHidden();
        _ctx.Log.Line("hidden (" + why + ")");
        if (_look == null) { _win.AppWindow.Hide(); return; }
        // main.js: MantleLauncherFold() before the hide; the window goes when the fold has landed (an open first cancels it)
        _look.Fold(WidthDip - 2 * InsetDip, InsetDip, () =>
        {
            if (_shown) return;
            _win.AppWindow.Hide();
            _look.Rest();
            _look.ClearPicture();                                            // panelDown: clearBackdrop()
        });
    }

    public void Destroy(string why)
    {
        _shown = false;
        _debounce?.Stop();
        QuickDestroy();
        _themeSub?.Dispose(); _themeSub = null;
        try { _win?.Close(); } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _ctx?.Log.Line("destroyed (" + why + ")");
    }

    // ------------------------------------------------------------------ search (LAU-016: 12 ms debounce)

    string? _viewQ;                                                       // the query the rows on screen answer
    string? _queuedQ;                                                     // the last query searched or waiting on the debounce

    void QueueSearch()
    {
        long typedAt = Stopwatch.GetTimestamp();
        string text = _q.Text;
        // the same text again (a second change event for one edit) is not a new search: a duplicate answer that landed
        // after an arrow key put the selection back on row 1 (the intermittent "Down did not move to row 2")
        if (text == _queuedQ) return;
        _queuedQ = text;
        if (_typedTs == 0) _typedTs = typedAt;                               // the first keystroke of a burst the debounce folds
        // A lone key searches at once; a key within 12 ms of the last one (auto-repeat, a paste) is folded into one
        // search 12 ms later, as Electron's queueSearch does. (Electron waits 12 ms on every key; a WinUI timer rounds
        // 12 ms up to the next 15.6 ms tick, so waiting here would put native behind it.)
        bool burst = _lastKeyTs != 0 && Ms(_lastKeyTs) < 12;
        _lastKeyTs = typedAt;
        _debounce?.Stop(); _debounce = null;
        if (!burst) { RunSearch(text, _typedTs); return; }
        _debounce = DispatcherQueueTimerLike.After(_ctx.Dispatcher, 12, FlushSearch);
    }

    /// The search the debounce is holding, now (its timer, or a key that must act on the rows for what is typed).
    void FlushSearch()
    {
        if (_debounce == null) return;
        _debounce.Stop(); _debounce = null;
        RunSearch(_q.Text, _typedTs);
    }

    void RunSearch(string q, long typedAt, bool opening = false)
    {
        if (_mode == QuickMode) return;                                    // Quick links' rows are not a search
        int seq = ++_seq;
        if (typedAt != 0) _typedTs = 0;
        _queuedQ = q;
        // the host's index searched right here (pure, read-only, well under 1 ms for 12 rows): no thread hop per keystroke
        var svc = Service;
        if (svc != null && svc.Current.Items.Count > 0)
        {
            long t0 = Stopwatch.GetTimestamp();
            List<SearchHit> hits;
            try { hits = svc.Search(q); }
            catch (Exception e) { ShowProblem("The search failed (" + e.Message + "). Type again to retry; files and the web may still work."); return; }
            Apply(q, hits, svc.Current.Sources, typedAt, opening, Ms(t0));
            return;
        }
        _ = RunSearchOnBus(q, typedAt, opening, seq);
    }

    async System.Threading.Tasks.Task RunSearchOnBus(string q, long typedAt, bool opening, int seq)
    {
        long t0 = Stopwatch.GetTimestamp();
        var r = await _ctx.CallAsync("apps.search", new JsonObject { ["q"] = q });
        if (seq != _seq) return;                                             // a superseded query's answer is dropped (LAU-077)
        if (!r.Ok)
        {
            ShowProblem("The search did not answer (" + r.Error + "). Type again to retry; files and the web may still work.");
            return;
        }
        var hits = new List<SearchHit>();
        foreach (var e in r.Result.GetProperty("items").EnumerateArray()) hits.Add(new SearchHit(ItemOf(e), e.TryGetProperty("score", out var s) ? s.GetInt32() : null));
        var sources = new List<IndexSource>();
        foreach (var s in r.Result.GetProperty("sources").EnumerateArray())
            sources.Add(new IndexSource(s.GetProperty("name").GetString() ?? "", s.TryGetProperty("count", out var c) ? c.GetInt32() : 0, s.GetProperty("status").GetString() ?? "", s.GetProperty("reason").GetString() ?? ""));
        Apply(q, hits, sources, typedAt, opening, Ms(t0));
    }

    void Apply(string q, List<SearchHit> hits, IReadOnlyList<IndexSource> sources, long typedAt, bool opening, double searchMs)
    {
        if (_mode == QuickMode) return;                                    // a late apps answer never replaces Quick links' rows
        long t0 = Stopwatch.GetTimestamp();
        string? keepId = _view.Flat.Count > _sel ? _view.Flat[_sel].Item.Id : null;
        // the selection stays on its row when the rows answer the same query (an open, an index refresh, a repeat);
        // a new query starts at the top (launcher.js render: sel = 0)
        bool keep = typedAt == 0 || q == _viewQ;
        _view = LauncherGroups.Build(q, hits);
        _viewQ = q;
        _sel = 0;
        if (keepId != null && keep) { int at = _view.Flat.ToList().FindIndex(x => x.Item.Id == keepId); if (at >= 0) _sel = at; }
        _sources = sources;
        Footer();
        Render();
        double renderMs = Ms(t0);
        int n = _view.Flat.Count;
        if (typedAt != 0)
            NextFrame(() =>
            {
                LastTypedToRowsMs = Ms(typedAt);
                if (_ctx.Test) _ctx.Log.Line($"typed \"{q}\": {n} row(s) on screen {LastTypedToRowsMs:0.0} ms after the keystroke (search {searchMs:0.0} ms, rows {renderMs:0.0} ms)");
            });
        if (opening && _shown)
        {
            double rowsMs = Ms(_openTs);
            NextFrame(() =>
            {
                LastOpenMs = Ms(_openTs);
                _ctx.Log.Line($"first frame with rows {LastOpenMs:0.0} ms after the open (rows ready at {rowsMs:0.0} ms; {n} row(s))");
            });
        }
    }

    /// The next frame the UI thread draws (CompositionTarget.Rendering), once.
    static void NextFrame(Action done)
    {
        EventHandler<object>? h = null;
        h = (_, _) => { CompositionTarget.Rendering -= h; done(); };
        CompositionTarget.Rendering += h;
    }

    static LauncherItem ItemOf(JsonElement e)
    {
        string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new LauncherItem { Id = S("id") ?? "", Kind = S("kind") ?? "", Name = S("name") ?? "", Subtitle = S("subtitle") ?? "", Path = S("path") ?? "", Glyph = S("glyph"), IconKey = S("iconKey"), Icon = S("icon"), Confirm = S("confirm") };
    }

    static readonly Dictionary<string, string> SourceWords = new()
    {
        ["apps"] = "Your apps could not be listed, so only settings, files and the web can be searched.",
        ["commands"] = "Your PowerShell commands could not be read.",
        ["settings"] = "Settings pages could not be listed.",
        ["power"] = "Lock, Sleep and the other power options could not be listed.",
    };

    IReadOnlyList<IndexSource> _sources = [];

    /// launcher.js renderFooter: the first source problem, else the backdrop's ("The see-through effect is off"), else nothing.
    void Footer()
    {
        var bad = _sources.FirstOrDefault(s => s.Status != "ok");
        string? text = null, tip = null;
        if (bad != null) { text = SourceWords.TryGetValue(bad.Name, out var w) ? w : "Part of the search is unavailable right now."; tip = bad.Name + ": " + bad.Reason; }
        else if (_look?.NoPicture is string why) { text = "The see-through effect is off: Mantle could not picture the screen behind this window."; tip = why; }
        if (text == null) { _noticeRow.Visibility = Visibility.Collapsed; return; }
        if (_notice.Text != text) _notice.Text = text;
        ToolTipService.SetToolTip(_notice, tip);
        _noticeRow.Visibility = Visibility.Visible;
    }

    void ShowProblem(string text)
    {
        _list.Children.Clear(); _rows.Clear();
        ShowLine(text, warn: true);
    }

    // ------------------------------------------------------------------ rows (LAU-063/067/068/071)

    void ShowLine(string text, bool warn)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = warn ? 13 : 12.5, TextWrapping = TextWrapping.Wrap,
            Margin = warn ? new Thickness(12, 16, 12, 16) : new Thickness(16, 10, 12, 2),                  // .empty.is-bad / .empty.is-none
            Foreground = _tok.Brush(warn ? "LgWarn" : "LgInk2", _dark),
        };
        _list.Children.Add(t);
    }

    /// The rows for _view, refilled into the pooled elements in order; only the children that changed are swapped.
    void Render()
    {
        if (_list == null) return;
        _rows.Clear(); _want.Clear();
        int i = 0, gi = 0;
        foreach (var g in _view.Groups)
        {
            if (gi == _heads.Count)
                _heads.Add(new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = _tok.Brush("LgInk", _dark) });
            var head = _heads[gi];
            if (head.Text != g.Label) head.Text = g.Label;
            head.Margin = gi == 0 ? new Thickness(12, 4, 12, 3) : new Thickness(12, 12, 12, 3);           // .grp (+2 after a row)
            gi++;
            _want.Add(head);
            foreach (var row in g.Rows)
            {
                var r = i < _pool.Count ? _pool[i] : NewRow();
                FillRow(r, row.Item, i);
                _rows.Add(r);
                _want.Add(r.Root);
                i++;
            }
        }
        if (_view.Flat.Count == 0)
            _want.Add(EmptyLine(_q.Text.Trim().Length == 0 ? "No apps to show yet. Type to search files, settings and the web." : "Nothing matches ‘" + _q.Text.Trim() + "’"));
        // keep the common head of the list; swap only the tail that differs
        var ch = _list.Children;
        int k = 0;
        while (k < ch.Count && k < _want.Count && ReferenceEquals(ch[k], _want[k])) k++;
        while (ch.Count > k) ch.RemoveAt(ch.Count - 1);
        for (int j = k; j < _want.Count; j++) ch.Add(_want[j]);
        Select(_sel, false);
        string counts = _view.Flat.Count == 0 ? "No results" : _view.Flat.Count == 1 ? "1 result" : _view.Flat.Count + " results";
        if (_counts.Text != counts) { AutomationProperties.SetName(_counts, counts); _counts.Text = counts; }
    }

    TextBlock? _empty;
    TextBlock EmptyLine(string text)
    {
        _empty ??= new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 12, 2) };   // .empty.is-none
        _empty.Foreground = _tok.Brush("LgInk2", _dark);
        if (_empty.Text != text) _empty.Text = text;
        return _empty;
    }

    /// One row's elements, made once per theme (LAU-063: icon cell 24, name, subtitle; the wash and the light tick).
    Row NewRow()
    {
        var t = _tok;
        double radius = _dark ? 10 : 8;                                      // --lg-r
        var wash = new Border { CornerRadius = new CornerRadius(radius), Opacity = 0 };
        var tick = new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 13, 0, 13), CornerRadius = new CornerRadius(2), Background = t.Brush("LgAcc", _dark), Opacity = 0 };
        var letter = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = t.Brush("LgInk2", _dark),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var glyphs = new Grid { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var pic = new Image { Width = 24, Height = 24, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
        var cell = new Grid();
        cell.Children.Add(letter); cell.Children.Add(glyphs); cell.Children.Add(pic);
        var tile = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center, Child = cell };
        var name = new TextBlock { FontSize = _dark ? 13 : 14, Foreground = t.Brush("LgInk", _dark), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { MaxWidth = 290, FontSize = _dark ? 12 : 12.5, Foreground = t.Brush("LgInk3", _dark), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1); Grid.SetColumn(sub, 2);
        var main = new Grid { Padding = new Thickness(10, 0, 12, 0), ColumnSpacing = 12 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.Children.Add(tile); main.Children.Add(name); main.Children.Add(sub);
        var g = new LauncherGrid(Microsoft.UI.Xaml.Automation.Peers.AutomationControlType.ListItem) { Height = 44, Background = new SolidColorBrush(LauncherTokens.None) };   // a fill so the whole row takes the pointer
        g.Children.Add(wash); g.Children.Add(tick); g.Children.Add(main);
        var r = new Row { Root = g, Wash = wash, Tick = tick, Tile = tile, Letter = letter, Name = name, Sub = sub, GlyphBox = glyphs, Pic = pic };
        g.PointerMoved += (_, _) => { if (_sel != r.Index) Select(r.Index, false); };   // LAU-056: mousemove, not mouseover
        g.Tapped += (_, _) => { Select(r.Index, false); Activate(); };
        _pool.Add(r);
        return r;
    }

    void FillRow(Row r, LauncherItem it, int index)
    {
        r.Index = index;
        r.Root.Margin = index > 0 ? new Thickness(0, 1, 0, 0) : new Thickness(0);
        string name = it.Name.Length > 0 ? it.Name : "(unnamed)";
        if (!QuickName(r, it, name) && r.Name.Text != name) r.Name.Text = name;
        if (r.Sub.Text != it.Subtitle) r.Sub.Text = it.Subtitle;
        if (r.Id != it.Id)
        {
            r.Id = it.Id;
            AutomationProperties.SetAutomationId(r.Root, "row-" + it.Id);
        }
        if (AutomationProperties.GetName(r.Root) != it.Name) AutomationProperties.SetName(r.Root, it.Name);
        string tip = it.Name + (it.Subtitle.Length > 0 ? " · " + it.Subtitle : "");
        if (r.Tip != tip) { r.Tip = tip; ToolTipService.SetToolTip(r.Root, tip); }
        Icon(r, it);
    }

    /// launcher.js iconCell: the app's own picture, else the row's glyph, else the letter tile. A picture or a glyph
    /// stands on no tile (.ico:not(.is-letter): transparent); a picture that does not decode falls back to the letter.
    void Icon(Row r, LauncherItem it)
    {
        string? url = it.Icon != null && it.Icon.StartsWith("data:image/", StringComparison.Ordinal) ? it.Icon : null;
        BitmapImage? bmp = null;
        if (url != null && !_pics.TryGetValue(url, out bmp)) Decode(url);   // a letter tile until it is decoded
        bool showPic = bmp != null;
        string? glyph = url == null ? LauncherGlyphs.NameFor(it.Glyph, it.Kind) : null;
        bool showGlyph = !showPic && glyph != null;
        if (showPic) { if (!ReferenceEquals(r.Pic.Source, bmp)) r.Pic.Source = bmp; }
        else if (r.Pic.Source != null) r.Pic.Source = null;
        r.IconUrl = url;
        if (showGlyph && r.GlyphName != glyph) { LauncherGlyphs.Fill(r.GlyphBox, glyph!, _tok.Brush("LgInk2", _dark), _dark); r.GlyphName = glyph; }
        string letter = it.Name.Trim() is { Length: > 0 } nm ? nm.Substring(0, 1).ToUpperInvariant() : "?";
        if (r.Letter.Text != letter) r.Letter.Text = letter;
        r.Pic.Visibility = showPic ? Visibility.Visible : Visibility.Collapsed;
        r.GlyphBox.Visibility = showGlyph ? Visibility.Visible : Visibility.Collapsed;
        r.Letter.Visibility = showPic || showGlyph ? Visibility.Collapsed : Visibility.Visible;
        r.Tile.Background = showPic || showGlyph ? null : _tok.Brush(_dark ? "LgTile" : "LgSunk", _dark);
    }

    /// A data URL into a bitmap, once per distinct picture (the rows that wear it are redrawn when it is ready).
    async void Decode(string url)
    {
        if (_pics.ContainsKey(url)) return;
        _pics[url] = null;                                                   // asked: a letter tile until it is in
        BitmapImage? img = null;
        try
        {
            int comma = url.IndexOf(',');
            byte[] bytes = Convert.FromBase64String(comma >= 0 ? url[(comma + 1)..] : url);
            using var ms = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = new global::Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(bytes); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream(); }
            ms.Seek(0);
            var b = new BitmapImage();
            await b.SetSourceAsync(ms);
            img = b;
        }
        catch (Exception e) { _ctx.Log.Line("an app icon did not decode (" + e.Message + "); its row shows the letter tile"); }
        if (_pics.Count > 512) _pics.Clear();
        _pics[url] = img;
        if (img == null) return;
        for (int i = 0; i < _rows.Count && i < _view.Flat.Count; i++)
            if (_view.Flat[i].Item.Icon == url) Icon(_rows[i], _view.Flat[i].Item);
    }

    void Select(int index, bool scroll = true)
    {
        if (_rows.Count == 0) { _sel = 0; RenderHint(); return; }
        _sel = Math.Clamp(index, 0, _rows.Count - 1);
        QuickMoved();
        for (int i = 0; i < _rows.Count; i++)
        {
            bool on = i == _sel;
            var w = _rows[i].Wash;
            // light: --lg-tile wash + 3 px accent tick (LAU-038); dark: --lg-press wash + hairline ring, no tick (LAU-037)
            if (on)
            {
                w.Background = _tok.Brush(_dark ? "LgPress" : "LgTile", _dark);
                w.BorderBrush = _dark ? _tok.Brush("LgEdgeOut", true) : null;
                w.BorderThickness = new Thickness(_dark ? 1 : 0);
            }
            w.Opacity = on ? 1 : 0;
            _rows[i].Tick.Opacity = on && !_dark ? 1 : 0;
            string status = on ? "selected" : "";
            if (AutomationProperties.GetItemStatus(_rows[i].Root) != status) AutomationProperties.SetItemStatus(_rows[i].Root, status);
        }
        if (scroll) _rows[_sel].Root.StartBringIntoView();
        RenderHint();
    }

    // ------------------------------------------------------------------ the footer's keys (launcher.js renderHint)

    string _hintKey = "";

    void RenderHint()
    {
        if (_hint == null) return;
        var item = _view.Flat.Count > _sel ? _view.Flat[_sel].Item : null;
        var keys = new List<(string Key, string Word)>();
        if (item != null)
        {
            if (item.Kind == "power") keys.Add(("Enter", item.Name));
            else keys.Add(("Enter", EnterVerb(item)));
            if (item.Kind == "file") keys.Add(("Ctrl+Enter", "Show in folder"));
        }
        if (_view.Groups.Count > 1) keys.Add(("Tab", "Next group"));
        keys.Add(("Esc", "Close"));
        if (_mode == QuickMode) { keys.Clear(); keys.AddRange(_quick.Hint()); }
        string key = (_dark ? "d|" : "l|") + string.Join("|", keys.Select(k => k.Key + "=" + k.Word));
        if (key == _hintKey) return;
        _hintKey = key;
        _hint.Children.Clear();
        // the last Esc sits at the right edge (.k-end); the rest run left to right, 14 apart
        if (_hintEnd != null) { _hintRow.Children.Remove(_hintEnd); _hintEnd = null; }
        for (int i = 0; i < keys.Count; i++)
        {
            var k = Keycap(keys[i].Key, keys[i].Word);
            if (keys[i].Key == "Esc" && i == keys.Count - 1 && keys.Count > 1) { Grid.SetColumn(k, 1); _hintRow.Children.Add(k); _hintEnd = k; }
            else _hint.Children.Add(k);
        }
        AutomationProperties.SetName(_hint, string.Join("  ", keys.Select(k => k.Key + " " + k.Word)));
    }

    StackPanel Keycap(string key, string word)
    {
        var t = _tok;
        var kbd = new Border
        {
            MinWidth = 20, Padding = new Thickness(5, 1, 5, 1), BorderThickness = new Thickness(1), BorderBrush = t.Brush("LgEdgeOut", _dark),
            CornerRadius = new CornerRadius(_dark ? 6 : 4), Background = t.Brush(_dark ? "LgTile" : "LgSunk", _dark), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = key, FontSize = 11, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = t.Brush("LgInk2", _dark), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center },
        };
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(kbd);
        s.Children.Add(new TextBlock { Text = word, FontSize = 12, Foreground = t.Brush("LgInk3", _dark), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) });
        return s;
    }

    static string EnterVerb(LauncherItem it) => it.Kind switch
    {
        "command" => "Run", "web" => "Search the web", "power" => it.Name, _ => "Open",
    };

    // ------------------------------------------------------------------ keys (LAU-048..054)

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (QuickKey(e)) return;                                           // Quick links' letters, Esc, Left, Right
        int n = _rows.Count;
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        int page = Math.Max(1, (int)(_scroll.ViewportHeight / 45) - 1);
        // a key that acts on the rows acts on the rows for what is typed: a search the debounce holds runs first
        if (e.Key is VirtualKey.Down or VirtualKey.Up or VirtualKey.PageDown or VirtualKey.PageUp or VirtualKey.Home or VirtualKey.End or VirtualKey.Tab or VirtualKey.Enter)
        {
            FlushSearch();
            n = _rows.Count;
        }
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
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Escape) && n > 0 && _ctx.Test)
            _ctx.Log.Line($"key {e.Key}: row {_sel + 1} of {n} (\"{_view.Flat[_sel].Item.Name}\")");
        e.Handled = true;
    }

    async void Activate()
    {
        if (QuickActivate()) return;
        if (_view.Flat.Count <= _sel) return;
        var it = _view.Flat[_sel].Item;
        if (it.Kind == "web") { _ctx.Log.Line("Enter on the web row: web search is not built yet (part 2), so nothing opened"); return; }
        var r = await _ctx.CallAsync("apps.launch", new JsonObject { ["id"] = it.Id });
        _ctx.Log.Line("Enter on \"" + it.Name + "\": " + (r.Ok ? "ok" : "FAILED " + r.Error) + (r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var why) ? " (" + why.GetString() + ")" : ""));
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
