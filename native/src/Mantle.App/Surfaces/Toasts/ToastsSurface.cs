using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Native;
using Mantle.App.Shell;
using Mantle.App.Shell.Windowing;
using Mantle.App.Surfaces.Osd;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Features.Toasts;
using Mantle.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App.Surfaces.Toasts;

// Mantle's notification banners: the native form of ui/toasts (index.html, toasts.css, toasts.js).
//
// THE WINDOW: made on the first banner and closed IdleCloseMs after the last one went (the user's RAM rule, as
// Electron's), never activated (WS_EX_NOACTIVATE), raised above everything on every new card (WindowKinds.Raise). It is
// 392 DIP wide (a 360 card + 16 each side) and as tall as the stack plus 16 below and 8 above, at the bottom right of
// the primary work area, its bottom above the bar's pill when the pill reaches the corner (ToastsCore.Spot). Electron's
// window is a tall column SHAPED to the stack; native sizes the window to the stack (Electron's own no-shape road).
//
// THE STACK: newest nearest the corner, older above, at most Max on screen (Mantle's own first: never under "+N more"),
// the rest as "+N more" on top. A card comes in from 12 DIP right with a fade over --m-dur-s (320 ms; reduced motion:
// the fade only), leaves the same way over 45 % of it, and is gone OutMs later. Each leaves LifeMs after it came on
// screen; a sticky one (alarm, reminder, call; Mantle's own) waits for you. Hovering the stack holds every timer;
// leaving gives each at least ResumeMs more. A hidden card older than StaleMs is dropped (it is in the list).
//
// --test: the window follows TestQuiet unless MANTLE_TEST_VISIBLE=1. A surface.show in a --test run runs the next step
// of MANTLE_TEST_TOASTS (a JSON list: {"cmd":"testShow"|"testOwn"|"testIds", ...args}; "at":[x,y] in a step places the
// window's bottom right there, physical px), so the UI tests and the screenshot pairs never touch Windows' own
// notifications. A surface.hide takes every card down.
//
// Not ported (docs/lanes/osd-toasts.md): the glide with the bar (no bar motion source yet: the stack stands above the
// pill when the bar is up), the FLIP slide of the cards below a leaving one, the screenshot card as a corner occupant.
[Surface("toasts", AtStart = true)]
internal sealed class ToastsSurface : ISurface
{
    const double WinW = 392, CardW = 360, Pad = 16, RoomTop = 8, ShadowPad = 40;
    const int Max = 3, LifeMs = 6000, ResumeMs = 1500, StaleMs = 20000, OutMs = 200, InMs = 320, IdleCloseMs = 30000, CopiedMs = 2500;
    static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");
    static readonly FontFamily MonoFont = new("Consolas");
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    sealed class Item
    {
        public ToastCard B = null!;
        public Grid El = null!;
        public Border Face = null!, Ring = null!, Lip = null!;
        public Image Shadow = null!;
        public Button X = null!;
        public TextBlock? Note, Say;
        public bool Shown, Leaving;
        public double Left = LifeMs, At, Due;
        public DispatcherQueueTimerHandle? Timer, SayT;
        public readonly HashSet<string> Pending = new();
    }

    ISurfaceContext _ctx = null!;
    Window? _win;
    IntPtr _hwnd;
    Grid _root = null!;
    StackPanel _stack = null!;
    Button? _more;
    readonly List<Item> _items = new();   // newest first
    readonly List<IDisposable> _subs = new();
    bool _dark, _hover, _activated, _visible;
    int _steps;
    (int X, int Y)? _at;
    DispatcherQueueTimerHandle? _idle;
    string _lastLayout = "";
    readonly double _t0 = Environment.TickCount64;

    public bool Shown => _visible;

    double Now => Environment.TickCount64 - _t0;

    // ------------------------------------------------------------------ create

    public void Create(ISurfaceContext ctx)
    {
        _ctx = ctx;
        if (Environment.GetEnvironmentVariable("MANTLE_DIAG_NO_TOASTS_SURFACE") == "1") { ctx.Log.Line("DIAG: toasts surface left empty"); return; }   // DIAG (temporary)
        _subs.Add(ctx.On<ThemePayload>("theme", t => { _dark = t.View.Theme == "dark"; foreach (var it in _items) Paint(it); PaintMore(); }));
        _subs.Add(ctx.On<ToastsShow>("toasts.show", s => Add(s.Banners)));
        _subs.Add(ctx.On<ToastsIds>("toasts.ids", s => Keep(s.Ids)));
        _subs.Add(ctx.On<ToastsUpdate>("toasts.update", u => Update(u.Banner)));
        _subs.Add(ctx.On<ToastsDrop>("toasts.drop", d => { var it = _items.FirstOrDefault(i => i.B.Id == d.Id); if (it != null) Leave(it, "its poster dropped it"); }));
        ctx.Log.Line("listening; the window is made on the first banner (" + (Host.Quiet ? "test: quiet" : ctx.Test ? "test: visible" : "live") + ")");
        _ = ctx.CallAsync("toasts.ready");
    }

    void MakeWindow()
    {
        if (_win != null) return;
        _root = new Grid { Background = Clear() };
        _stack = new StackPanel { Width = CardW, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(Pad, RoomTop, Pad, Pad) };
        _stack.PointerEntered += (_, _) => Hover(true);
        _stack.PointerExited += (_, _) => Hover(false);
        _stack.SizeChanged += (_, _) => Layout();
        AutomationProperties.SetAutomationId(_stack, "stack");
        _root.Children.Add(_stack);
        _root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light;
        _win = new Window { Title = "Mantle notifications", Content = _root };
        var p = OverlappedPresenter.Create();
        p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false; p.IsAlwaysOnTop = true;
        p.SetBorderAndTitleBar(false, false);
        _win.AppWindow.SetPresenter(p);
        _win.AppWindow.IsShownInSwitchers = false;
        _win.SystemBackdrop = new Mantle.App.Surfaces.Osd.SeeThroughBackdrop(_ctx.Log.Line);
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_win);
        int pref = Win32.DWMWCP_DONOTROUND; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
        int none = Win32.DWMWA_COLOR_NONE; Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref none, 4);
        WindowKinds.Frameless(_hwnd);
        WindowKinds.StripFrame(_hwnd);
        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64() | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        if (Host.Quiet) ex |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
        if (Host.Quiet) Win32.SetLayeredWindowAttributes(_hwnd, 0, 0, Win32.LWA_ALPHA);
        TransparentBackdrop.MakeSeeThrough(_hwnd);
        _activated = false;
        _ctx.Log.Line("window made");
    }

    void CloseWindow(string why)
    {
        _idle?.Dispose(); _idle = null;
        if (_win == null) return;
        foreach (var it in _items) { it.Timer?.Dispose(); it.SayT?.Dispose(); }
        _items.Clear();
        var w = _win; _win = null; _more = null; _visible = false; _lastLayout = "";
        try { w.Close(); } catch (Exception e) { _ctx.Log.Line("the window did not close cleanly (" + e.Message + ")"); }
        _ctx.Log.Line("window closed (" + why + ")");
    }

    // ------------------------------------------------------------------ the stack (toasts.js)

    static bool Reduced() { try { return !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled; } catch (Exception) { return false; } }

    void Add(IReadOnlyList<ToastCard> cards)
    {
        if (cards.Count == 0) return;
        MakeWindow();
        _idle?.Dispose(); _idle = null;
        foreach (var b in cards)
        {
            if (_items.Any(i => i.B.Id == b.Id)) { Update(b); continue; }
            var it = new Item { B = b, At = Now };
            Build(it);
            _items.Insert(0, it);
            _ctx.Log.Line($"card {b.Id} arrived ({(b.Own ? "Mantle's own" : b.App)}{(b.Sticky ? ", sticky" : "")})");
        }
        Refresh(true);
    }

    /// Which cards are on screen: Mantle's own first, then the newest apps', at most Max; stale hidden ones go.
    void Refresh(bool raise)
    {
        if (_win == null) return;
        double t = Now;
        _items.RemoveAll(i => !i.B.Own && !i.Shown && !i.Leaving && t - i.At > StaleMs && Live().IndexOf(i) >= Max);
        var live = Live();
        var on = live.Where(i => i.B.Own).Concat(live.Where(i => !i.B.Own)).Take(Max).ToHashSet();
        _stack.Children.Clear();
        int hidden = live.Count(i => !on.Contains(i));
        if (hidden > 0) { _more ??= MoreButton(); _more.Content = $"+{hidden} more"; AutomationProperties.SetName(_more, $"{hidden} more in the notification list"); _stack.Children.Add(_more); PaintMore(); }
        // newest nearest the corner: the stack draws top-down, so oldest first
        foreach (var it in _items.AsEnumerable().Reverse())
        {
            if (!on.Contains(it) && !it.Leaving) continue;
            _stack.Children.Add(it.El);
            if (!it.Shown && !it.Leaving) { it.Shown = true; Enter(it); Arm(it); }
        }
        if (raise && on.Count > 0) Reveal();
        Layout();
    }

    List<Item> Live() => _items.Where(i => !i.Leaving).ToList();

    void Enter(Item it)
    {
        bool reduced = Reduced();
        var tr = (TranslateTransform)it.El.RenderTransform;
        var sb = new Storyboard();
        if (!reduced)
        {
            var x = new DoubleAnimation { From = 12, To = 0, Duration = TimeSpan.FromMilliseconds(InMs), EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 } };
            Storyboard.SetTarget(x, tr); Storyboard.SetTargetProperty(x, "X"); sb.Children.Add(x);
        }
        var o = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(reduced ? 120 : InMs * .4) };
        Storyboard.SetTarget(o, it.El); Storyboard.SetTargetProperty(o, "Opacity"); sb.Children.Add(o);
        sb.Begin();
    }

    void Arm(Item it)
    {
        it.Timer?.Dispose(); it.Timer = null;
        if (it.B.Sticky || _hover || it.Leaving) return;
        it.Due = Now + it.Left;
        it.Timer = new DispatcherQueueTimerHandle(_ctx.Dispatcher, (int)it.Left, () => Leave(it, "its time was up"));
    }

    void Hover(bool on)
    {
        if (_hover == on) return;
        _hover = on;
        foreach (var it in _items)
        {
            if (!it.Shown || it.Leaving || it.B.Sticky) continue;
            if (on) { it.Timer?.Dispose(); it.Timer = null; it.Left = Math.Max(0, it.Due - Now); }
            else { it.Left = Math.Max(it.Left, ResumeMs); Arm(it); }
            it.X.Opacity = on || it.B.Sticky ? 1 : 0;
        }
    }

    void Leave(Item it, string why)
    {
        if (it.Leaving || !_items.Contains(it)) return;
        it.Leaving = true;
        it.Timer?.Dispose(); it.Timer = null;
        _ctx.Log.Line($"card {it.B.Id} left ({why})");
        if (!it.Shown) { _items.Remove(it); Refresh(false); return; }
        bool reduced = Reduced();
        var sb = new Storyboard();
        double ms = InMs * .45;
        if (!reduced)
        {
            var x = new DoubleAnimation { To = 12, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(x, it.El.RenderTransform); Storyboard.SetTargetProperty(x, "X"); sb.Children.Add(x);
        }
        var o = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(reduced ? 120 : ms) };
        Storyboard.SetTarget(o, it.El); Storyboard.SetTargetProperty(o, "Opacity"); sb.Children.Add(o);
        it.El.IsHitTestVisible = false;
        sb.Begin();
        // never waits on an animation event: a timer takes the card out (toasts.js OUT_MS)
        _ = new DispatcherQueueTimerHandle(_ctx.Dispatcher, OutMs, () => { _items.Remove(it); Refresh(false); });
    }

    void Keep(IReadOnlyList<long> ids)
    {
        var keep = ids.ToHashSet();
        foreach (var it in _items.ToList()) if (!keep.Contains(it.B.Id)) Leave(it, "its notification is gone from the list");
    }

    void Update(ToastCard b)
    {
        var it = _items.FirstOrDefault(i => i.B.Id == b.Id);
        if (it == null) return;
        it.B = b;
        Fill(it);
        Paint(it);
        _ctx.Log.Line($"card {b.Id} updated in place");
    }

    /// toasts.layout {h, count}: the stack's height from the window's bottom plus RoomTop; the window follows.
    void Layout()
    {
        if (_win == null) return;
        int count = Live().Count(i => i.Shown);
        if (count == 0 && !_items.Any(i => i.Leaving))
        {
            Win32.ShowWindow(_hwnd, 0);
            _visible = false;
            Report(0, 0);
            _idle ??= new DispatcherQueueTimerHandle(_ctx.Dispatcher, IdleCloseMs, () => { _idle = null; if (Live().Count == 0) CloseWindow($"no banner for {IdleCloseMs / 1000} s"); });
            return;
        }
        _stack.Measure(new global::Windows.Foundation.Size(CardW, double.PositiveInfinity));
        double h = _stack.DesiredSize.Height + RoomTop + Pad;
        Place(h);
        Report(h, count);
    }

    void Report(double h, int count)
    {
        string key = $"{Math.Ceiling(h)}:{count}";
        if (key == _lastLayout) return;
        _lastLayout = key;
        _ = _ctx.CallAsync("toasts.layout", new JsonObject { ["h"] = Math.Ceiling(h), ["count"] = count });
    }

    // ------------------------------------------------------------------ the window's place

    void Place(double hDip)
    {
        double scale = Win32.GetDpiForWindow(_hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        int w = (int)Math.Ceiling(WinW * scale);
        int x, bottom, h = (int)Math.Ceiling(hDip * scale);
        if (_at is { } a) { x = a.X - w; bottom = a.Y; }
        else
        {
            var area = DisplayArea.Primary;
            if (area == null || area.WorkArea.Width <= 0) { x = 0; bottom = h; _ctx.Log.Line("the primary display reports no size; the banners open at its top left"); }
            else
            {
                var wa = area.WorkArea;
                var pill = OsdSurface.BarPill();
                var spot = ToastsCore.Spot(new SpotRect(wa.X / scale, wa.Y / scale, wa.Width / scale, wa.Height / scale), (int)WinW, RoomTop,
                    pill is { } pr ? new SpotRect(pr.X / scale, pr.Y / scale, pr.Width / scale, pr.Height / scale) : null, null);
                if (spot.Hide)
                {
                    _ctx.Log.Line("banners held back: " + spot.Why);
                    Win32.ShowWindow(_hwnd, 0); _visible = false;
                    return;
                }
                x = (int)Math.Round(spot.X * scale);
                bottom = (int)Math.Round((spot.Y + spot.Height) * scale);
                h = Math.Min(h, (int)Math.Round(spot.Height * scale) + (int)Math.Ceiling(RoomTop * scale));
            }
        }
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, x, bottom - h, w, h, Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER);
    }

    void Reveal()
    {
        if (_win == null) return;
        IntPtr over = Win32.GetForegroundWindow();
        if (over == _hwnd) over = IntPtr.Zero;
        if (!_activated)
        {
            // WinUI starts drawing at the first Activate; a no-focus window gives the foreground straight back
            _activated = true;
            _win.Activate();
            if (over != IntPtr.Zero && Win32.GetForegroundWindow() == _hwnd) Win32.SetForegroundWindow(over);
        }
        else Win32.ShowWindow(_hwnd, Win32.SW_SHOWNOACTIVATE);
        WindowKinds.StripFrame(_hwnd);
        _visible = true;
        WindowKinds.Raise(_hwnd, _ctx.Dispatcher, "toasts", over, (ok, why) => { if (_ctx.Test) _ctx.Log.Line(ok ? "raised: on top of everything" + (over != IntPtr.Zero ? " (above the app in front)" : "") : "raised: NOT on top: " + why); });
    }

    // ------------------------------------------------------------------ one card (toasts.css .toast)

    void Build(Item it)
    {
        var b = it.B;
        it.El = new Grid { Width = CardW, RenderTransform = new TranslateTransform(), Opacity = 0 };
        AutomationProperties.SetAutomationId(it.El, "toast-" + b.Id);
        it.Shadow = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill, Margin = new Thickness(-ShadowPad), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        it.Ring = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(-1), IsHitTestVisible = false };
        it.Face = new Border { Padding = new Thickness(12, 10, 12, 12) };
        it.Lip = new Border { BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
        it.El.Children.Add(it.Shadow); it.El.Children.Add(it.Ring); it.El.Children.Add(it.Face); it.El.Children.Add(it.Lip);
        it.El.SizeChanged += (_, _) => Shade(it);
        it.Face.PointerEntered += (_, _) => { if (!it.B.Own) it.Face.Background = Lift(Face(), "LgHover"); };
        it.Face.PointerExited += (_, _) => it.Face.Background = Face();
        it.Face.Tapped += (_, e) => { if (e.OriginalSource is DependencyObject d && Inside(d, it.X)) return; Click(it); };
        it.X = new Button { Width = 22, Height = 22, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(0, 0, -4, 0), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6), Content = Glyph("M6 6l8 8M14 6l-8 8", "LgInk2"), IsTabStop = false, Opacity = b.Sticky ? 1 : 0 };
        AutomationProperties.SetAutomationId(it.X, "x-" + b.Id);
        AutomationProperties.SetName(it.X, "Close");
        it.X.Click += (_, _) => Close(it);
        Fill(it);
        Paint(it);
    }

    static bool Inside(DependencyObject d, DependencyObject outer)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p)) if (p == outer) return true;
        return false;
    }

    /// The card's content from its banner (again on an update in place).
    void Fill(Item it)
    {
        var b = it.B;
        var col = new StackPanel();
        // the app line: icon, name, age, close
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 6), ColumnSpacing = 7 };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto }) head.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        head.Children.Add(Icon(b));
        var app = Text(b.App, 12, true, "LgInk2"); app.TextTrimming = TextTrimming.CharacterEllipsis; app.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(app, 1); head.Children.Add(app);
        if (!b.Own) { var when = Text("now", 11, false, "LgInk3"); when.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(when, 2); head.Children.Add(when); }
        Grid.SetColumn(it.X, 3);
        if (it.X.Parent is Panel old) old.Children.Remove(it.X);
        head.Children.Add(it.X);
        col.Children.Add(head);
        // the message: logo, title (2 lines), body (3; Mantle's own 5), attribution
        var main = new Grid { ColumnSpacing = 10 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (Picture(b.Logo) is { } logo) { var lb = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(b.LogoCircle ? 18 : 6), Child = new Image { Source = logo, Stretch = Stretch.UniformToFill }, VerticalAlignment = VerticalAlignment.Top, Tag = "sunk" }; main.Children.Add(lb); }
        var text = new StackPanel();
        Grid.SetColumn(text, 1);
        if (!string.IsNullOrEmpty(b.Title)) { var t = Text(b.Title, 13, true, "LgInk"); t.LineHeight = 18; t.MaxLines = 2; t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.CharacterEllipsis; AutomationProperties.SetAutomationId(t, "title-" + b.Id); text.Children.Add(t); }
        if (!string.IsNullOrEmpty(b.Body)) { var t = Text(b.Body, 12.5, false, "LgInk2"); t.LineHeight = 17; t.MaxLines = b.Own ? 5 : 3; t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.CharacterEllipsis; t.Margin = new Thickness(0, 1, 0, 0); AutomationProperties.SetAutomationId(t, "body-" + b.Id); text.Children.Add(t); }
        if (!string.IsNullOrEmpty(b.Attribution)) { var t = Text(b.Attribution, 11, false, "LgInk3"); t.LineHeight = 15; t.Margin = new Thickness(0, 3, 0, 0); t.TextTrimming = TextTrimming.CharacterEllipsis; text.Children.Add(t); }
        main.Children.Add(text);
        col.Children.Add(main);
        if (Picture(b.Image) is { } pic) col.Children.Add(new Border { Margin = new Thickness(0, 10, 0, 0), CornerRadius = new CornerRadius(6), MaxHeight = 180, Child = new Image { Source = pic, Stretch = Stretch.UniformToFill } });
        if (b.Own) OwnBox(it, col);
        it.Face.Child = col;
        AutomationProperties.SetName(it.El, (b.Title ?? b.App) ?? "");
    }

    /// Mantle's own card: progress, detail with Copy, a note for a failed button, the buttons (primary last).
    void OwnBox(Item it, StackPanel col)
    {
        var b = it.B;
        if (b.Progress is { } pr)
        {
            var row = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = Text(pr.Text, 12, false, "LgInk2"); words.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(words);
            AutomationProperties.SetAutomationId(words, "progress-" + b.Id);   // on the words: a bare Grid has no automation peer, so UIA never sees it
            if (pr.Pct is double pc) { var pct = Text($"{Math.Round(pc)}%", 12, false, "LgInk3"); Grid.SetColumn(pct, 1); row.Children.Add(pct); }
            col.Children.Add(row);
            var bar = new Grid { Height = 3, Margin = new Thickness(0, 5, 0, 0), CornerRadius = new CornerRadius(2), Tag = "sunk" };
            var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Tag = b.Tone == "error" ? "danger" : "ink3" };
            bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * (pr.Pct is double p ? Math.Clamp(p, 0, 100) / 100 : .32);
            bar.Children.Add(fill);
            col.Children.Add(bar);
        }
        if (!string.IsNullOrEmpty(b.Detail))
        {
            var box = new Border { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(9, 7, 9, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), MaxHeight = 112, Tag = "sunk-edge" };
            var tb = new TextBlock { Text = b.Detail, FontFamily = MonoFont, FontSize = 11.5, LineHeight = 16, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Tag = "LgInk" };
            AutomationProperties.SetAutomationId(tb, "detail-" + b.Id);
            box.Child = new ScrollViewer { Content = tb, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            col.Children.Add(box);
            var copyRow = new Grid { Height = 22, Margin = new Thickness(0, 3, 0, 0), ColumnSpacing = 8 };
            copyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            copyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            it.Say = Text("", 11, false, "LgInk3"); it.Say.TextAlignment = TextAlignment.Right; it.Say.VerticalAlignment = VerticalAlignment.Center;
            copyRow.Children.Add(it.Say);
            var copy = SmallButton("Copy", "copy-" + b.Id, false);
            copy.Height = 22; copy.Margin = new Thickness(0, 0, -4, 0);
            copy.Click += async (_, _) => {
                copy.IsEnabled = false;
                var r = await _ctx.CallAsync("toasts.copy", new JsonObject { ["id"] = b.Id });
                copy.IsEnabled = true;
                string? text = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("text", out var t) ? t.GetString() : null;
                string? why = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var re) ? re.GetString() : r.Error;
                if (text != null)
                {
                    try { var dp = new global::Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(text); global::Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); SayOn(it, "Copied", false); }
                    catch (Exception e) { SayOn(it, "Not copied: " + e.Message, true); }
                }
                else SayOn(it, "Not copied: " + (why ?? "no reason came back"), true);
            };
            Grid.SetColumn(copy, 1); copyRow.Children.Add(copy);
            col.Children.Add(copyRow);
        }
        it.Note = Text("", 12, false, "LgDanger"); it.Note.TextWrapping = TextWrapping.Wrap; it.Note.Margin = new Thickness(0, 8, 0, 0); it.Note.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(it.Note, "note-" + b.Id);
        col.Children.Add(it.Note);
        if (b.Actions.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var a in b.Actions.OrderBy(a => a.Primary ? 1 : 0))
            {
                var btn = SmallButton(a.Label, $"act-{b.Id}-{a.Id}", a.Primary);
                btn.IsEnabled = !it.Pending.Contains(a.Id);
                string id = a.Id;
                btn.Click += async (_, _) => await Act(it, id, btn);
                row.Children.Add(btn);
            }
            col.Children.Add(row);
        }
    }

    void SayOn(Item it, string words, bool bad)
    {
        if (it.Say == null) return;
        it.SayT?.Dispose();
        it.Say.Text = words;
        it.Say.Foreground = B(bad ? "LgDanger" : "LgInk3");
        _ctx.Log.Line($"card {it.B.Id}: {words}");
        if (!bad) it.SayT = new DispatcherQueueTimerHandle(_ctx.Dispatcher, CopiedMs, () => { if (it.Say != null) it.Say.Text = ""; });
    }

    async System.Threading.Tasks.Task Act(Item it, string action, Button? btn)
    {
        if (!it.Pending.Add(action)) return;
        if (btn != null) btn.IsEnabled = false;
        var r = await _ctx.CallAsync("toasts.action", new JsonObject { ["id"] = it.B.Id, ["action"] = action });
        it.Pending.Remove(action);
        if (btn != null) btn.IsEnabled = true;
        bool ok = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        bool keep = !(r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("keep", out var k) && k.ValueKind == JsonValueKind.False);
        string? why = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : r.Error;
        _ctx.Log.Line($"card {it.B.Id}: \"{action}\" answered ok={ok.ToString().ToLowerInvariant()} keep={keep.ToString().ToLowerInvariant()}{(why != null ? " (" + why + ")" : "")}");
        if (!ok && it.Note != null) { it.Note.Text = why ?? "it did not work, and no reason came back"; it.Note.Visibility = Visibility.Visible; }
        if (!keep) Leave(it, $"its \"{action}\" said so");
    }

    void Close(Item it)
    {
        if (it.B.Own) { _ = Act(it, "close", it.X); return; }
        Leave(it, "the X");   // the toast stays in Windows' list (Windows' X does the same)
    }

    async void Click(Item it)
    {
        if (it.B.Own || it.Leaving) return;   // Mantle's own card opens nothing; its buttons do
        var r = await _ctx.CallAsync("toasts.click", new JsonObject { ["id"] = it.B.Id });
        _ctx.Log.Line($"card {it.B.Id} clicked: " + (r.Ok ? "opened" : "not opened (" + r.Error + ")"));
        Leave(it, "clicked");
    }

    Button MoreButton()
    {
        var b = new Button { Height = 26, MinHeight = 0, MinWidth = 0, Padding = new Thickness(11, 0, 11, 0), HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontFamily = TextFont, IsTabStop = false };
        AutomationProperties.SetAutomationId(b, "more");
        b.Click += async (_, _) => { var r = await _ctx.CallAsync("toasts.more"); _ctx.Log.Line("\"+N more\": " + (r.Ok ? "the list opened" : r.Error)); };
        return b;
    }

    Button SmallButton(string label, string id, bool primary)
    {
        var b = new Button { Content = label, Height = 28, MinWidth = 64, MinHeight = 0, Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(6),
            FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontFamily = TextFont, BorderThickness = new Thickness(primary ? 0 : 1), IsTabStop = false, Tag = primary ? "primary" : "plain" };
        AutomationProperties.SetAutomationId(b, id);
        return b;
    }

    UIElement Icon(ToastCard b)
    {
        if (Picture(b.Icon) is { } src) return new Image { Source = src, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        var letter = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center, Tag = "tile",
            Child = new TextBlock { Text = Letter(b.App), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = TextFont, Tag = b.Tone == "error" && b.Own ? "LgDanger" : "LgInk2" } };
        return letter;
    }

    /// toasts.js letterOf: the first letter or digit, upper case; "?" when there is none.
    static string Letter(string? s) { foreach (char c in s ?? "") if (char.IsAsciiLetterOrDigit(c)) return char.ToUpperInvariant(c).ToString(); return "?"; }

    /// A picture the sidecar named: a file path or an http(s) / ms-appx URI; anything else (data:, empty) none.
    static ImageSource? Picture(string? src)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        try
        {
            if (System.IO.Path.IsPathRooted(src) && System.IO.File.Exists(src)) return new BitmapImage(new Uri(src));
            if (Uri.TryCreate(src, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http" || u.Scheme == "ms-appx" || u.Scheme == "file")) return new BitmapImage(u);
        }
        catch (Exception) { }
        return null;
    }

    TextBlock Text(string? s, double size, bool bold, string ink) =>
        new() { Text = s ?? "", FontSize = size, FontFamily = TextFont, FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Tag = ink };

    UIElement Glyph(string d, string ink)
    {
        var path = (UIElement)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Path {Ns} Data='{d}' Stroke='{{ThemeResource {ink}}}' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>");
        return new Viewbox { Width = 16, Height = 16, Child = new Canvas { Width = 20, Height = 20, Children = { path } } };   // toasts.css .x svg: viewBox 0 0 20 20 at 16 px
    }

    // ------------------------------------------------------------------ paint (light and dark)

    void Paint(Item it)
    {
        if (_root != null) _root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light;
        double r = D("LgRPop");
        it.Face.CornerRadius = new CornerRadius(r);
        it.Face.Background = Face();
        it.Ring.CornerRadius = new CornerRadius(r + 1);
        it.Ring.BorderBrush = B("LgEdgeOut");
        it.Lip.CornerRadius = new CornerRadius(r);
        it.Lip.BorderBrush = _dark ? B("LgLip") : Clear();
        it.X.Background = Clear();
        it.X.Resources["ButtonBackgroundPointerOver"] = B("LgHover");
        it.X.Resources["ButtonBackgroundPressed"] = B("LgPress");
        PaintTree(it.Face);
        Shade(it);
    }

    void PaintTree(DependencyObject d)
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        if (d is TextBlock t && t.Tag is string ink && ink.StartsWith("Lg", StringComparison.Ordinal)) t.Foreground = B(ink);
        if (d is Border bd)
        {
            switch (bd.Tag as string)
            {
                case "tile": bd.Background = B("LgTile"); break;
                case "sunk": bd.Background = B("LgSunk"); break;
                case "sunk-edge": bd.Background = B("LgSunk"); bd.BorderBrush = B("LgEdgeOut"); break;
                case "ink3": bd.Background = B("LgInk3"); break;
                case "danger": bd.Background = B("LgDanger"); break;
            }
            if (bd.Child != null) PaintTree(bd.Child);
        }
        if (d is Grid g && g.Tag as string == "sunk") g.Background = B("LgSunk");
        if (d is Button b && b.Tag is string kind && kind != "")
        {
            bool p = kind == "primary";
            b.Background = p ? B("LgAcc") : B("LgSunk");
            b.Foreground = p ? B("LgAccOn") : B("LgInk");
            b.BorderBrush = B("LgEdgeOut");
            b.Resources["ButtonBackgroundPointerOver"] = p ? B("LgAccStrong") : Lift(B("LgSunk"), "LgHover");
            b.Resources["ButtonBackgroundPressed"] = p ? B("LgAcc") : Lift(B("LgSunk"), "LgPress");
            b.Resources["ButtonForegroundPointerOver"] = b.Resources["ButtonForegroundPressed"] = b.Foreground;
        }
        if (d is Panel pn) foreach (var c in pn.Children) PaintTree(c);
        else if (d is ScrollViewer sv && sv.Content is DependencyObject sc) PaintTree(sc);
        else if (d is Border) { }
        else for (int i = 0; i < n; i++) PaintTree(VisualTreeHelper.GetChild(d, i));
    }

    void PaintMore()
    {
        if (_more == null) return;
        _more.Background = Face();
        _more.BorderBrush = B("LgEdgeOut");
        _more.Foreground = B("LgInk2");
        _more.Resources["ButtonBackgroundPointerOver"] = Lift(Face(), "LgHover");
        _more.Resources["ButtonForegroundPointerOver"] = B("LgInk");
    }

    /// The light drop shadow of one card (--lg-shadow by CssShadow, moving with the card); dark has none (toasts.css).
    void Shade(Item it)
    {
        if (_dark || it.El.ActualWidth <= 0) { it.Shadow.Source = null; return; }
        double scale = _root?.XamlRoot?.RasterizationScale ?? 1;
        double w = it.El.ActualWidth + 2 * ShadowPad, h = it.El.ActualHeight + 2 * ShadowPad;
        // the ring (0 0 0 1px edge-out) is part of the box the shadow is cast from
        var box = new CssShadow.Box(ShadowPad - 1, ShadowPad - 1, it.El.ActualWidth + 2, it.El.ActualHeight + 2, D("LgRPop") + 1);
        it.Shadow.Source = CssShadow.Bitmap((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), scale, new[] { box }, CssShadow.LightLg);
    }

    /// --hud-face: light = --lg-fill-strong three times over (one flat colour), dark = --lg-fill-pop.
    Brush Face()
    {
        if (_dark) return B("LgFillPop");
        var c = ((SolidColorBrush)B("LgFillStrong")).Color;
        c.A = (byte)Math.Round((1 - Math.Pow(1 - c.A / 255.0, 3)) * 255);
        return new SolidColorBrush(c);
    }

    /// linear-gradient(over, over), under: one flat colour (source-over).
    Brush Lift(Brush under, string overKey)
    {
        var u = (under as SolidColorBrush)?.Color ?? default;
        var o = (B(overKey) as SolidColorBrush)?.Color ?? default;
        double oa = o.A / 255.0, ua = u.A / 255.0, a = oa + ua * (1 - oa);
        if (a <= 0) return Clear();
        byte Mix(byte oc, byte uc) => (byte)Math.Round((oc * oa + uc * ua * (1 - oa)) / a);
        var c = o;   // the two tokens' colours composited: no colour of its own
        c.A = (byte)Math.Round(a * 255); c.R = Mix(o.R, u.R); c.G = Mix(o.G, u.G); c.B = Mix(o.B, u.B);
        return new SolidColorBrush(c);
    }

    /// No colour at all (a default brush is transparent): a hit-testable fill that draws nothing.
    static SolidColorBrush Clear() => new();

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
    readonly HashSet<string> _missing = new();
    Brush B(string key) => Tok(key) as Brush ?? new SolidColorBrush();
    double D(string key) => Tok(key) is double v ? v : 0;

    // ------------------------------------------------------------------ ISurface (the test road)

    public void Show(string why)
    {
        if (!_ctx.Test) { _ctx.Log.Line("show (" + why + "): the banners come from the notification list, not from a show"); return; }
        var steps = TestSteps();
        if (steps == null || _steps >= steps.Count) { _ctx.Log.Line($"show ({why}): no MANTLE_TEST_TOASTS step {_steps + 1} to run"); return; }
        var step = steps[_steps++] as JsonObject ?? new JsonObject();
        if (step["at"] is JsonArray at && at.Count == 2) { try { _at = ((int)at[0]!.GetValue<double>(), (int)at[1]!.GetValue<double>()); } catch (Exception) { } }
        if (step["theme"] is JsonValue tv && tv.TryGetValue<string>(out var th)) { _dark = th == "dark"; foreach (var it in _items) Paint(it); }
        string cmd = step["cmd"]?.GetValue<string>() ?? "testShow";
        var args = (JsonObject)step.DeepClone();
        args.Remove("cmd"); args.Remove("at"); args.Remove("theme");
        if (cmd == "click" || cmd == "x" || cmd == "act" || cmd == "hover")
        {
            // a step that presses something on a card (the UI tests click through UIA; the pairs need none of this)
            _ctx.Log.Line($"test step {_steps}: {cmd} is for the UI tests' own clicks; nothing done");
            return;
        }
        _ = Run(cmd, args);
        async System.Threading.Tasks.Task Run(string c, JsonObject a)
        {
            var r = await _ctx.CallAsync("toasts." + c, a);
            _ctx.Log.Line($"test step {_steps}: toasts.{c} " + (r.Ok ? "ok" : "failed: " + r.Error));
        }
    }

    static JsonArray? TestSteps()
    {
        var raw = Environment.GetEnvironmentVariable("MANTLE_TEST_TOASTS");
        if (string.IsNullOrWhiteSpace(raw)) return new JsonArray(new JsonObject { ["cmd"] = "testShow" });
        try { return JsonNode.Parse(raw) as JsonArray; } catch (JsonException) { return null; }
    }

    public void Hide(string why)
    {
        foreach (var it in _items.ToList()) Leave(it, "hidden: " + why);
    }

    public void Destroy(string why)
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        CloseWindow(why);
        _ctx?.Log.Line("destroyed (" + why + ")");
    }
}
