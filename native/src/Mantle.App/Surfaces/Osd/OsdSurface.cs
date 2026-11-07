using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Native;
using Mantle.App.Shell;
using Mantle.App.Shell.Windowing;
using Mantle.Core.Features.Osd;
using Mantle.Core.Features.Toasts;
using Mantle.Core.Look;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App.Surfaces.Osd;

// The volume / brightness pop-up: the native form of ui/osd (index.html, osd.css, osd.js). ONE window, made at start
// and parked hidden (a key wants its answer within ~50 ms). Collapsed it is the pill (glyph: a click mutes; the level
// as a bar you can drag; the number; the chevron that opens the apps); expanded, the panel grows away from the bar over
// the pill. The window never takes focus (WS_EX_NOACTIVATE): whatever the user types into keeps the keyboard.
//
// The window is the pane plus --m (18 DIP) of transparent margin for the light drop shadow. It is shown from the
// feature's "osd.show" event and put on top of everything on EVERY show (WindowKinds.Raise: topmost band, then
// first in it, checked 60 ms later). The fade is the page's: in at once, out over --lg-dur-out on --lg-ease (light;
// dark has no transition), then the window hides and "osd.faded {seq}" tells the feature.
//
// --test: the window follows TestQuiet (alpha 0, click-through) unless MANTLE_TEST_VISIBLE=1; a surface.show in a
// --test run with MANTLE_TEST_OSD={json} calls osd.testShow with that made-up reading ("at": [x, y] places the window
// there, physical px, for the screenshot pairs). Not ported yet: the per-app rows (qs.audio.sessions has no native
// owner: the panel shows Electron's own "can't be read" block with the bus's words), the dark see-through backdrop
// picture (hud-backdrop), and the glide with the bar (osd.lift).
[Surface("osd", AtStart = true)]
internal sealed class OsdSurface : ISurface
{
    const double M = 18, PillW = 288, OpenW = 348, RowH = 44;
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
    const string Warn = "M8 2.2l6.2 10.9H1.8z M8 6.4v3M8 11.2v.01";

    ISurfaceContext _ctx = null!;
    Window _win = null!;
    IntPtr _hwnd;
    Grid _root = null!, _shadowHost = null!, _pill = null!, _level = null!;
    Border _pane = null!, _lip = null!, _fill = null!, _track = null!, _pillLine = null!;
    Button _glyph = null!, _more = null!;
    Ellipse _knob = null!;
    StackPanel _stack = null!, _panel = null!, _apps = null!, _why = null!;
    TextBlock _num = null!, _whyText = null!, _dev = null!;
    Viewbox _glyphBox = null!, _chev = null!;
    readonly List<IDisposable> _subs = new();
    OsdFader _fader = null!;
    bool _dark, _shown, _open, _dragging, _mutePending, _below, _activated, _glyphHover, _moreHover;
    string _kind = "volume";
    string? _why_;
    int _seq;
    double _wheelAcc;
    Storyboard? _fade;
    DispatcherQueueTimerHandle? _fading;
    readonly Dictionary<string, VolumeState> _st = new();
    string _lastSize = "";

    public bool Shown => _shown;

    // ------------------------------------------------------------------ create

    public void Create(ISurfaceContext ctx)
    {
        _ctx = ctx;
        _fader = new OsdFader(new UiClock(ctx.Dispatcher), FadeOut);
        Build();
        _win = new Window { Title = "Mantle volume", Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        _win.SystemBackdrop = new TransparentBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        WindowKinds.Frameless(_hwnd);
        WindowKinds.StripFrame(_hwnd);
        // never takes the keyboard (osd.js: a non-activating window that takes clicks)
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;   // WIN-028: a quiet test window draws nothing
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(_hwnd);

        _subs.Add(ctx.On<ThemePayload>("theme", t => SetTheme(t.View.Theme == "dark")));
        _subs.Add(ctx.On<OsdShow>("osd.show", OnShow));
        _subs.Add(ctx.On<OsdLevel>("osd.level", OnLevel));
        _subs.Add(ctx.On<OsdHide>("osd.hide", h => { StopFade(); Gone(); ctx.Log.Line("hidden: " + h.Why); }));
        ctx.Log.Line("made hidden and parked (" + (Host.Quiet ? "test: quiet" : ctx.Test ? "test: visible" : "live") + ")");
    }

    void Build()
    {
        _root = new Grid { Background = B("LgFill0") };
        _shadowHost = new Grid { IsHitTestVisible = false };
        _root.Children.Add(_shadowHost);

        // the pill: glyph | level | number | chevron (grid-template-columns: 28px 1fr 30px 26px; gap 8; padding 0 9 0 8)
        _pill = new Grid { Height = RowH, Padding = new Thickness(8, 0, 9, 0), ColumnSpacing = 8 };
        foreach (var w in new[] { 28.0, -1, 30, 26 }) _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        _glyphBox = new Viewbox { Width = 16, Height = 16 };
        _glyph = Btn(_glyphBox, 28, "glyph", () => OnGlyph(), on => { _glyphHover = on; Paint(); });
        _pill.Children.Add(_glyph);

        _level = new Grid { Height = 14, VerticalAlignment = VerticalAlignment.Center, Background = B("LgFill0") };
        _track = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        _fill = new Border { Height = 6, CornerRadius = new CornerRadius(3, 0, 0, 3), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        _knob = new Ellipse { Width = 14, Height = 14, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform() };
        _level.Children.Add(_track); _level.Children.Add(_fill); _level.Children.Add(_knob);
        AutomationProperties.SetAutomationId(_level, "level");
        AutomationProperties.SetName(_level, "Volume");
        _level.SizeChanged += (_, _) => PaintLevel(LevelValue);
        _level.PointerPressed += (s, e) => { _dragging = true; _level.CapturePointer(e.Pointer); _fader.Hold(true); DragTo(e.GetCurrentPoint(_level).Position.X); };
        _level.PointerMoved += (s, e) => { if (_dragging) DragTo(e.GetCurrentPoint(_level).Position.X); };
        _level.PointerReleased += (s, e) => { _dragging = false; _level.ReleasePointerCaptures(); _fader.Hold(false); Paint(); };
        Grid.SetColumn(_level, 1); _pill.Children.Add(_level);

        _num = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_num, "num");
        Grid.SetColumn(_num, 2); _pill.Children.Add(_num);

        _chev = new Viewbox { Width = 14, Height = 14, RenderTransformOrigin = new global::Windows.Foundation.Point(.5, .5), RenderTransform = new RotateTransform() };
        _more = Btn(_chev, 26, "more", () => { SetOpen(!_open); _fader.Poke(); }, on => { _moreHover = on; Paint(); });
        Grid.SetColumn(_more, 3); _pill.Children.Add(_more);
        _pillLine = new Border { Child = _pill };

        // the why line: a small warn glyph and the words in --lg-warn (margin -3px 14px 9px)
        _whyText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 16.8 };
        AutomationProperties.SetAutomationId(_whyText, "why");
        _why = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(14, -3, 14, 9), Visibility = Visibility.Collapsed };

        // the panel (padding 10px 12px 4px): "Sound" + the device, then the apps (or a state block)
        _dev = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        _apps = new StackPanel();
        AutomationProperties.SetAutomationId(_apps, "apps");
        _panel = new StackPanel { Padding = new Thickness(12, 10, 12, 4), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_panel, "panel");

        _stack = new StackPanel();
        _lip = new Border { IsHitTestVisible = false, BorderThickness = new Thickness(0, 1, 0, 0) };
        var inner = new Grid();
        inner.Children.Add(_stack); inner.Children.Add(_lip);
        _pane = new Border { BorderThickness = new Thickness(1), Child = inner, Width = PillW, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(M) };
        AutomationProperties.SetAutomationId(_pane, "osd");
        _pane.PointerEntered += (_, _) => _fader.Hover(true);
        _pane.PointerExited += (_, _) => _fader.Hover(false);
        _pane.PointerPressed += (_, e) => { _fader.Poke(); if (_ctx.Test) _ctx.Log.Line($"pointer pressed at {e.GetCurrentPoint(_pane).Position.X:0},{e.GetCurrentPoint(_pane).Position.Y:0} DIP on the pane"); };
        _pane.PointerWheelChanged += OnWheel;
        _pane.SizeChanged += (_, _) => { Report(); Shadow(); };
        _root.Children.Add(_pane);
        Arrange();
    }

    /// A flat icon button (osd.css .glyph/.more): 28 tall, --lg-r-ctl, transparent; hover/press/on painted in Paint().
    /// A real Button (UIA Invoke, keyboard, EarlyClicks-compatible) drawn flat: its own template's states get the
    /// osd.css colours through the Button* resources set in Paint (hover, press); Background carries "on".
    Button Btn(UIElement content, double w, string id, Action click, Action<bool> hover)
    {
        var b = new Button { Width = w, Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(6), Content = content,
            Background = B("LgFill0"), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsTabStop = false };
        AutomationProperties.SetAutomationId(b, id);
        b.PointerEntered += (_, _) => hover(true);
        b.PointerExited += (_, _) => hover(false);
        b.Click += (_, _) => click();
        return b;
    }

    /// Bottom-docked bar: panel over the pill, the why line under it; top-docked (below): mirrored.
    void Arrange()
    {
        _stack.Children.Clear(); _panel.Children.Clear(); _why.Children.Clear();
        var head = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 0, 0, 4) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock { Text = "Sound", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Tag = "ink" };
        head.Children.Add(title); Grid.SetColumn(_dev, 1); head.Children.Add(_dev);
        _panel.Children.Add(head); _panel.Children.Add(_apps);
        _why.Children.Add(WarnGlyph()); _why.Children.Add(_whyText);
        if (_below) { _stack.Children.Add(_pillLine); _stack.Children.Add(_panel); _stack.Children.Add(_why); }
        else { _stack.Children.Add(_panel); _stack.Children.Add(_pillLine); _stack.Children.Add(_why); }
        _pane.VerticalAlignment = _below ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    }

    static Viewbox WarnGlyph()
    {
        var c = new Canvas { Width = 16, Height = 16 };
        c.Children.Add(PathOf(Warn, false, 1.3, "LgWarn"));
        return new Viewbox { Width = 12, Height = 12, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top, Child = c, Tag = "warn" };
    }

    /// One SVG path as XAML (BarWindow.PathEl's road): the data re-spaced (WinUI's path parser refuses SVG's compact
    /// forms such as "1.8z" or ".01"), the ink a {ThemeResource} so it follows the theme; only cast to UIElement.
    static UIElement PathOf(string d, bool fill, double stroke, string brush)
    {
        string line = FormattableString.Invariant($"Stroke='{{ThemeResource {brush}}}' StrokeThickness='{stroke}' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'");
        string paint = fill ? $"Fill='{{ThemeResource {brush}}}'" + (stroke > 0 ? " " + line : "") : line;   // the sun's disc: filled AND stroked
        return (UIElement)XamlReader.Load($"<Path {Ns} Data='{Norm(d)}' {paint}/>");
    }
    static readonly System.Text.RegularExpressions.Regex Tok_ = new(@"[A-Za-z]|[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
    static string Norm(string d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in Tok_.Matches(d)) sb.Append(m.Value).Append(' ');
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------ theme and brushes

    void SetTheme(bool dark)
    {
        _dark = dark;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Paint();
        Shadow();
    }

    /// The --lg-* token of the current theme (Theme/Tokens.xaml), read in code so every paint follows a theme change.
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
    Brush B(string key) => Tok(key) as Brush ?? new SolidColorBrush();
    readonly HashSet<string> _missing = new();
    double D(string key) => Tok(key) is double v ? v : 0;

    /// --hud-face: light = --lg-fill-strong three times over (one flat colour), dark = --lg-fill-pop.
    Brush Face()
    {
        if (_dark) return B("LgFillPop");
        var c = ((SolidColorBrush)B("LgFillStrong")).Color;
        double a = c.A / 255.0, three = 1 - Math.Pow(1 - a, 3);
        c.A = (byte)Math.Round(three * 255);   // the token's own colour, three layers of its alpha
        return new SolidColorBrush(c);
    }

    // ------------------------------------------------------------------ painting (osd.js paint)

    VolumeState Cur => _st.TryGetValue(_kind, out var s) ? s : new VolumeState(false, null, false, (_kind == "brightness" ? "brightness" : "volume") + " has not been read yet");
    double LevelValue { get; set; }
    string Label => _kind == "brightness" ? "Brightness" : "Volume";

    void Paint()
    {
        if (_pane == null) return;
        var s = Cur;
        var d = OsdModel.Describe(_kind, s.Ok, s.Level, s.Muted, s.Reason);
        bool vol = _kind == "volume";
        bool muted = vol && d.Muted;
        _pane.Background = Face();
        _pane.BorderBrush = B("LgEdgeOut");
        double r = _open ? D("LgRPop") : D("LgRPill");
        _pane.CornerRadius = new CornerRadius(r);
        _lip.CornerRadius = new CornerRadius(Math.Max(0, r - 1));
        _lip.BorderBrush = _dark ? B("LgLip") : B("LgFill0");   // dark: --lg-lip-up, no outer shadow
        _pane.Width = _open ? OpenW : PillW;
        _pillLine.BorderThickness = _open ? (_below ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0)) : new Thickness(0);
        _pillLine.BorderBrush = _dark ? B("LgDivider") : B("LgSunk");
        foreach (var t in Texts()) t.FontSize = t == _num ? 14 : t == _whyText ? 12 : t.Tag as string == "ink" ? 14 : _dark ? 13 : 12.5;

        // glyph
        bool on = vol && muted && !d.Dead;
        string ink = _dark ? (on || _glyphHover ? "LgInk" : "LgInk2") : on ? "LgAccOn" : "LgInk";
        if (!vol && _dark) ink = "LgInk2";
        _glyphBox.Child = GlyphCanvas(vol ? OsdModel.VolumeGlyph(d.Dead ? null : d.Fill, muted) : OsdModel.SunGlyph(d.Dead ? null : d.Fill), ink);
        _glyph.Background = on ? (_dark ? B("LgPress") : B("LgAcc")) : B("LgFill0");
        _glyph.IsEnabled = vol && !d.Dead;
        _glyph.BorderBrush = on && _dark ? B("LgEdgeOut") : null;
        _glyph.BorderThickness = new Thickness(on && _dark ? 1 : 0);
        _glyph.Opacity = _mutePending ? .6 : 1;
        AutomationProperties.SetName(_glyph, d.Dead ? d.Aria : !vol ? d.Aria : muted ? "Unmute (mute key)" : "Mute (mute key)");
        ToolTipService.SetToolTip(_glyph, AutomationProperties.GetName(_glyph));

        // the chevron (brightness has none); points away from the bar, turned when open
        _more.Visibility = vol ? Visibility.Visible : Visibility.Collapsed;
        _pill.ColumnDefinitions[3].Width = new GridLength(vol ? 26 : 0);
        _pill.ColumnSpacing = 8;
        var cc = new Canvas { Width = 16, Height = 16 };
        cc.Children.Add(PathOf("M4.5 9.8L8 6.3l3.5 3.5", false, 1.4, _dark && _moreHover ? "LgInk" : "LgInk2"));
        _chev.Child = cc;
        ((RotateTransform)_chev.RenderTransform).Angle = _open ^ _below ? 180 : 0;
        _more.Background = B("LgFill0");
        foreach (var btn in new[] { _glyph, _more })
        {
            btn.Resources["ButtonBackgroundPointerOver"] = btn == _glyph && on ? btn.Background : _dark ? B("LgPress") : B("LgHover");
            btn.Resources["ButtonBackgroundPressed"] = btn == _glyph && on ? btn.Background : _dark ? B("LgCard") : B("LgPress");
            btn.Resources["ButtonBackgroundDisabled"] = B("LgFill0");
            btn.Resources["ButtonBorderBrushPointerOver"] = btn.Resources["ButtonBorderBrushPressed"] = btn.BorderBrush ?? B("LgFill0");
        }
        AutomationProperties.SetName(_more, _open ? "Hide the apps" : "Show every app’s volume");

        // the level, the number
        _track.Background = B("LgTile");
        _fill.Background = muted ? B("LgInk3") : B("LgAcc");
        _knob.Fill = _dark ? B("LgInk") : B("LgAccOn");
        _knob.Stroke = B("LgEdgeOut");
        _level.Opacity = d.Dead ? .5 : 1;
        if (!_dragging) { LevelValue = d.Fill; _num.Text = d.Value; }
        PaintLevel(LevelValue);
        _num.Foreground = d.Dead ? B("LgInk2") : B("LgInk");
        _num.FontWeight = d.Dead ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetName(_num, _num.Text);
        AutomationProperties.SetHelpText(_pane, d.Aria);

        // a line of why
        string line = d.Dead ? Sentence(Label + " is unavailable: " + OsdModel.Plain(s.Reason)) : _why_ != null ? Sentence(_why_) : "";
        _whyText.Text = line;
        _why.Visibility = line.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _whyText.Foreground = B("LgWarn");
        _dev.Foreground = B("LgInk2");
        foreach (var t in Texts()) if (t.Tag as string == "ink") t.Foreground = B("LgInk");
        PaintApps();
    }

    IEnumerable<TextBlock> Texts()
    {
        yield return _num; yield return _whyText; yield return _dev;
        if (_panel.Children.Count > 0 && _panel.Children[0] is Grid g && g.Children[0] is TextBlock title) yield return title;
        foreach (var x in _apps.Children) if (x is StackPanel sp) foreach (var y in sp.Children) if (y is TextBlock tb) yield return tb;
    }

    void PaintLevel(double v)
    {
        double w = _level.ActualWidth;
        if (w <= 0) return;
        double p = Math.Max(0, Math.Min(100, v)) / 100.0;
        _fill.Width = Math.Max(0, w * p);
        _fill.CornerRadius = p >= 1 ? new CornerRadius(3) : new CornerRadius(3, 0, 0, 3);
        ((TranslateTransform)_knob.RenderTransform).X = (w - 14) * p;   // a range thumb travels inside the track
    }

    static Canvas GlyphCanvas(IReadOnlyList<GlyphPart> parts, string ink)
    {
        var c = new Canvas { Width = 16, Height = 16 };
        foreach (var g in parts) c.Children.Add(PathOf(g.Data, g.Fill, g.Stroke, ink));
        return c;
    }

    static string Sentence(string s) => s.Length == 0 || ".!?…".IndexOf(s[^1]) >= 0 ? s : s + ".";

    // ------------------------------------------------------------------ the panel (apps)

    string? _appsWhy;
    void SetOpen(bool on)
    {
        on = on && _kind == "volume";
        if (on == _open) return;
        _open = on;
        _panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _appsWhy = null;
        if (on) _ = ReadApps();
        Paint();
    }

    async System.Threading.Tasks.Task ReadApps()
    {
        var r = await _ctx.CallAsync("qs.audio.sessions");
        if (!_open) return;
        _appsWhy = r.Ok ? null : r.Error ?? "qs.audio.sessions gave no answer";
        if (r.Ok) _appsWhy = "the per-app list is not drawn by this build yet";   // said, never an empty list
        Paint();
    }

    void PaintApps()
    {
        _apps.Children.Clear();
        if (!_open) return;
        // osd.js stateBlock: a bold title (warn glyph + --lg-warn when bad), the words, "Try again"
        var block = new StackPanel { Padding = new Thickness(2, 12, 2, 10) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(WarnGlyph());
        head.Children.Add(new TextBlock { Text = "The apps can’t be read", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = B("LgWarn"), FontSize = _dark ? 13 : 12.5 });
        block.Children.Add(head);
        block.Children.Add(new TextBlock { Text = Sentence(OsdModel.Plain(_appsWhy ?? "they are being read")), TextWrapping = TextWrapping.Wrap, Foreground = B("LgInk2"), FontSize = _dark ? 13 : 12.5, LineHeight = 18 });
        var again = new Border { CornerRadius = new CornerRadius(6), Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Background = _dark ? B("LgCard") : B("LgSunk"), Child = new TextBlock { Text = "Try again", VerticalAlignment = VerticalAlignment.Center, Foreground = B("LgInk"), FontSize = _dark ? 13 : 12.5 } };
        AutomationProperties.SetAutomationId(again, "retry");
        again.Tapped += (_, e) => { e.Handled = true; _ = ReadApps(); };
        block.Children.Add(again);
        AutomationProperties.SetAutomationId(block, "state");
        AutomationProperties.SetName(block, "The apps can’t be read");
        _apps.Children.Add(block);
    }

    // ------------------------------------------------------------------ what the user does on it

    void OnGlyph()
    {
        _fader.Poke();
        if (_ctx.Test) _ctx.Log.Line("glyph clicked (" + _kind + ")");
        if (_kind != "volume" || _mutePending) return;
        var cur = Cur;
        if (!cur.Ok) return;
        bool want = !cur.Muted;
        _st["volume"] = cur with { Muted = want };   // the click shows at once...
        _mutePending = true;
        Paint();
        _ = Set("mute", want, cur, want ? "Mute" : "Unmute");
    }

    void DragTo(double x)
    {
        double w = Math.Max(1, _level.ActualWidth - 14);
        int v = (int)Math.Round(Math.Max(0, Math.Min(1, (x - 7) / w)) * 100);
        Step(v);
    }

    void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (Cur is { Ok: false }) return;
        e.Handled = true;
        int delta = e.GetCurrentPoint(_pane).Properties.MouseWheelDelta;
        var w = OsdModel.Wheel(_wheelAcc, -delta / 120.0 * OsdModel.Notch, 0);
        _wheelAcc = w.Acc;
        if (w.Steps == 0) return;
        double v = LevelValue;
        for (int i = 0; i < Math.Abs(w.Steps); i++) v = w.Steps > 0 ? Math.Floor(v / 2) * 2 + 2 : Math.Ceiling(v / 2) * 2 - 2;
        v = Math.Max(0, Math.Min(100, v));
        if (v == LevelValue) { _fader.Poke(); return; }
        Step((int)v);
    }

    void Step(int v)
    {
        LevelValue = v;
        _num.Text = v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PaintLevel(v);
        _fader.Poke();
        var k = _kind;
        _ = Set(k, v, Cur, Label);
    }

    /// qs.set {part, value} as the page sends it; a refusal is a line of why, never silent.
    async System.Threading.Tasks.Task Set(string part, object value, VolumeState before, string what)
    {
        var a = new JsonObject { ["part"] = part, ["value"] = value is bool b ? JsonValue.Create(b) : JsonValue.Create((int)value) };
        var r = await _ctx.CallAsync("qs.set", a);
        if (part == "mute") _mutePending = false;
        if (r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object)
            _st[part == "mute" ? "volume" : part] = new VolumeState(!(st.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False),
                st.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null,
                st.TryGetProperty("muted", out var m) && m.ValueKind == JsonValueKind.True, null);
        else if (!r.Ok && part == "mute") _st["volume"] = before;   // refused: spring back
        _why_ = r.Ok ? null : what + (part == "mute" ? " did not take: " : " did not change: ") + OsdModel.Plain(r.Error);
        if (!_dragging) Paint(); else { _whyText.Text = Sentence(_why_ ?? ""); _why.Visibility = _why_ != null ? Visibility.Visible : Visibility.Collapsed; }
    }

    // ------------------------------------------------------------------ show, level, fade (osd.js show/level/fadeOut/gone)

    void OnShow(OsdShow p)
    {
        string k = p.Kind == "brightness" ? "brightness" : "volume";
        if (_shown && _open && k != _kind && _fader.State.Hovered) return;
        if (!(_dragging && k == _kind)) _st[k] = p.State;
        if (k != _kind) { _why_ = null; _kind = k; if (k != "volume") SetOpen(false); }
        _seq = p.Seq;
        bool fresh = !_shown;
        StopFade();
        _pane.Opacity = 1;
        _shown = true;
        if (p.Expand && _kind == "volume") SetOpen(true);
        if (p.Hold) _fader.Hold(true);
        Paint();
        _fader.Poke();
        Place(fresh);
        _ctx.Log.Line($"shown ({p.Via}, {k} {(p.State.Ok ? p.State.Level?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?" : "unavailable")}{(p.State.Muted ? ", muted" : "")}{(p.Expand ? ", panel open" : "")}, seq {p.Seq})");
    }

    void OnLevel(OsdLevel p)
    {
        string k = p.Kind == "brightness" ? "brightness" : "volume";
        _st[k] = p.State;
        if (!_shown || k != _kind) return;
        if (!_dragging) Paint();
        if (!_fader.State.Hovered) _fader.Poke();
        WindowKinds.Raise(_hwnd, _ctx.Dispatcher, "osd");
    }

    int FadeMs => Reduced() ? 0 : 260;   // --lg-dur-out

    void FadeOut()
    {
        int s = _seq;
        if (FadeMs > 0 && !_dark)
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new SplineDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(FadeMs)), Value = 0, KeySpline = new KeySpline { ControlPoint1 = new global::Windows.Foundation.Point(.22, .68), ControlPoint2 = new global::Windows.Foundation.Point(.30, 1) } });
            Storyboard.SetTarget(a, _pane); Storyboard.SetTargetProperty(a, "Opacity");
            _fade = new Storyboard(); _fade.Children.Add(a); _fade.Begin();
        }
        else _pane.Opacity = 0;   // dark: no transition (osd.css); the window still goes after fadeMs
        _fading = new DispatcherQueueTimerHandle(_ctx.Dispatcher, FadeMs, () =>
        {
            _fading = null;
            Gone();
            _ = _ctx.CallAsync("osd.faded", new JsonObject { ["seq"] = s });
        });
    }

    void StopFade() { _fading?.Dispose(); _fading = null; _fade?.Stop(); _fade = null; }

    void Gone()
    {
        bool was = _shown;
        _shown = false;
        SetOpen(false);
        _fader.Reset();
        _why_ = null;
        Win32.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        if (was && _ctx.Test) _ctx.Log.Line($"faded and hidden (seq {_seq})");
    }

    static bool Reduced() { try { return !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled; } catch (Exception) { return false; } }

    // ------------------------------------------------------------------ the window: size, place, raise

    (int W, int H) SizePx(double scale)
    {
        _pane.Measure(new global::Windows.Foundation.Size(_pane.Width, double.PositiveInfinity));
        double h = _pane.DesiredSize.Height;   // includes the 18 DIP margin each side
        double w = _pane.Width + 2 * M;
        return ((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale));
    }

    void Place(bool fresh)
    {
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        var (w, h) = SizePx(scale);
        int x, y;
        var at = TestAt();
        var area = DisplayArea.Primary;
        if (at is { } a) { x = a.X; y = a.Y; }
        else if (area == null || area.WorkArea.Width <= 0) { x = 0; y = 0; _ctx.Log.Line("the primary display reports no size; the pop-up opens at its top left"); }
        else
        {
            var wa = area.WorkArea;
            var spot = OsdModel.Place(BarPill(), new OsdRect(wa.X, wa.Y, wa.Width, wa.Height), w, h, 0);
            x = spot.X; y = spot.Y;
            if (spot.Below != _below) { _below = spot.Below; Arrange(); Paint(); (w, h) = SizePx(scale); }
        }
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        IntPtr over = Win32.GetForegroundWindow();
        if (over == _hwnd) over = IntPtr.Zero;
        if (fresh)
        {
            if (!_activated)
            {
                // WinUI starts drawing at the first Activate; a no-focus window gives the foreground straight back
                _activated = true;
                IntPtr prev = over;
                _win.Activate();
                if (prev != IntPtr.Zero && Win32.GetForegroundWindow() == _hwnd) Win32.SetForegroundWindow(prev);
            }
            else Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
            WindowKinds.StripFrame(_hwnd);
        }
        // EVERY show, first or not: a pop-up already up can have been sunk since (osd.js raise)
        WindowKinds.Raise(_hwnd, _ctx.Dispatcher, "osd", over, (ok, why) => { if (_ctx.Test) _ctx.Log.Line(ok ? "raised: on top of everything" + (over != IntPtr.Zero ? " (above the app in front)" : "") : "raised: NOT on top: " + why); });
        _lastSize = "";
        Report();
    }

    /// osd.size: what the pane drew (w x h in DIP with the margin), so the feature's state and the test log know.
    void Report()
    {
        if (!_shown) return;
        double w = Math.Ceiling(_pane.ActualWidth + 2 * M), h = Math.Ceiling(_pane.ActualHeight + 2 * M);
        if (w <= 2 * M) return;
        string key = w + "x" + h;
        if (key == _lastSize) return;
        _lastSize = key;
        GetWindowRect(_hwnd, out var r);
        _ = _ctx.CallAsync("osd.size", new JsonObject { ["w"] = w, ["h"] = h, ["open"] = _open, ["at"] = $"{r.Left},{r.Top}" });
        // the window follows the pane (the panel opened or closed, a why line came)
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        int pw = (int)Math.Ceiling(w * scale), ph = (int)Math.Ceiling(h * scale);
        if (r.Right - r.Left != pw || r.Bottom - r.Top != ph)
        {
            int y = _below ? r.Top : r.Bottom - ph;
            int x = r.Left + ((r.Right - r.Left) - pw) / 2;
            Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, pw, ph, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
        }
    }

    /// The bar's pill in physical px: the bar window ("Mantle bar", this process) less its shadow room
    /// (BarWindow ShadowTop 56 / ShadowSide 28 DIP). Null when the bar is not on screen.
    static OsdRect? BarPill()
    {
        uint me = (uint)Environment.ProcessId;
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowExW(IntPtr.Zero, h, null, "Mantle bar")) != IntPtr.Zero)
        {
            Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid != me || !Win32.IsWindowVisible(h) || !GetWindowRect(h, out var r)) continue;
            double s = Win32.GetDpiForWindow(h) / 96.0;
            double side = 28 * s, top = 56 * s;
            return new OsdRect(r.Left + side, r.Top + top, Math.Max(0, r.Right - r.Left - 2 * side), Math.Max(0, r.Bottom - r.Top - top));
        }
        return null;
    }

    (int X, int Y)? TestAt()
    {
        if (!_ctx.Test || TestSpec() is not JsonObject o || o["at"] is not JsonArray a || a.Count != 2) return null;
        try { return ((int)a[0]!.GetValue<double>(), (int)a[1]!.GetValue<double>()); } catch (Exception) { return null; }
    }
    static JsonObject? TestSpec()
    {
        var raw = Environment.GetEnvironmentVariable("MANTLE_TEST_OSD");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonNode.Parse(raw) as JsonObject; } catch (JsonException) { return null; }
    }

    // ------------------------------------------------------------------ the light drop shadow (--lg-shadow; dark has none)

    void Shadow()
    {
        var host = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_shadowHost).Compositor;
        if (_dark || _pane.ActualWidth <= 0) { Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(_shadowHost, null); return; }
        var c = host;
        var box = c.CreateContainerVisual();
        double pw = _pane.ActualWidth, ph = _pane.ActualHeight, rad = _pane.CornerRadius.TopLeft;
        var o = _pane.TransformToVisual(_root).TransformPoint(new global::Windows.Foundation.Point(0, 0));
        // the shadow ink: --lg-shadow is rgba(14,18,38,a); with no shadow token yet this is the light --lg-ink, the
        // nearest token (a request for a shadow token is in docs/lanes/osd-toasts.md)
        var ink = ((SolidColorBrush)B("LgInk")).Color;
        void Layer(double dy, double blur, double spread, byte alpha)
        {
            float w = (float)(pw + 2 * spread), h = (float)(ph + 2 * spread);
            if (w <= 0 || h <= 0) return;
            float r = (float)Math.Max(0, rad + spread);
            var geo = c.CreateRoundedRectangleGeometry(); geo.Size = new System.Numerics.Vector2(w, h); geo.CornerRadius = new System.Numerics.Vector2(r, r);
            var shape = c.CreateSpriteShape(geo); shape.FillBrush = c.CreateColorBrush(ink);   // a mask: only its alpha is used
            var sv = c.CreateShapeVisual(); sv.Size = new System.Numerics.Vector2(w, h); sv.Shapes.Add(shape);
            var surf = c.CreateVisualSurface(); surf.SourceVisual = sv; surf.SourceSize = new System.Numerics.Vector2(w, h);
            var sprite = c.CreateSpriteVisual();
            sprite.Size = new System.Numerics.Vector2(w, h);
            sprite.Offset = new System.Numerics.Vector3((float)(o.X - spread), (float)(o.Y - spread), 0);
            var ds = c.CreateDropShadow();
            ds.Mask = c.CreateSurfaceBrush(surf);
            ds.BlurRadius = (float)blur;
            ds.Offset = new System.Numerics.Vector3(0, (float)dy, 0);
            var col = ink; col.A = alpha; ds.Color = col;
            sprite.Shadow = ds;
            box.Children.InsertAtTop(sprite);
        }
        // 0 1px 1px rgba(14,18,38,.05), 0 10px 28px -12px rgba(14,18,38,.22), 0 26px 56px -28px rgba(14,18,38,.30)
        Layer(1, 1, 0, 13);
        Layer(10, 28, -12, 56);
        Layer(26, 56, -28, 77);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(_shadowHost, box);
    }

    // ------------------------------------------------------------------ ISurface

    public void Show(string why)
    {
        // a surface.show: ask the feature, so the reading, the seq and the backstop are its (osd.show, the bar's road)
        var spec = _ctx.Test ? TestSpec() : null;
        string cmd = spec != null ? "osd.testShow" : "osd.show";
        var args = spec != null ? (JsonObject)spec.DeepClone() : new JsonObject { ["via"] = why.Length > 40 ? why[..40] : why };
        _ = Ask(cmd, args, why);
    }

    async System.Threading.Tasks.Task Ask(string cmd, JsonObject args, string why)
    {
        var r = await _ctx.CallAsync(cmd, args);
        if (!r.Ok) _ctx.Log.Line($"not shown ({why}): {cmd}: {r.Error}");
    }

    public void Hide(string why)
    {
        if (!_shown) return;
        StopFade();
        int s = _seq;
        Gone();
        _ctx.Log.Line("hidden (" + why + ")");
        _ = _ctx.CallAsync("osd.faded", new JsonObject { ["seq"] = s });
    }

    public void Destroy(string why)
    {
        StopFade();
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        _fader?.Reset();
        _shown = false;
        try { _win?.Close(); } catch (Exception e) when (e is InvalidOperationException or COMException) { }
        _ctx?.Log.Line("destroyed (" + why + ")");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Win32.RECT r);
}

/// A one-shot UI-thread timer that can be dropped.
internal sealed partial class DispatcherQueueTimerHandle : IDisposable
{
    Microsoft.UI.Dispatching.DispatcherQueueTimer? _t;
    public DispatcherQueueTimerHandle(Microsoft.UI.Dispatching.DispatcherQueue q, int ms, Action action)
    {
        _t = q.CreateTimer();
        _t.Interval = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        _t.IsRepeating = false;
        _t.Tick += (_, _) => { if (_t == null) return; Dispose(); action(); };
        _t.Start();
    }
    public void Dispose() { _t?.Stop(); _t = null; }
}

/// The fader's clock on the surface's UI thread.
internal sealed partial class UiClock : IToastClock
{
    readonly Microsoft.UI.Dispatching.DispatcherQueue _q;
    public UiClock(Microsoft.UI.Dispatching.DispatcherQueue q) { _q = q; }
    public double NowMs => Environment.TickCount64;
    public IDisposable Later(int ms, Action action) => new DispatcherQueueTimerHandle(_q, ms, action);
}
