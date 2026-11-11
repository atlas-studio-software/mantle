using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Calendar;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App.Surfaces.Bar.Calendar;

/// <summary>
/// The clock's calendar flyout (bar.js buildCalPop, bar.css .cal*, ui/bar/gcal.js + gcal.css): the big live time and the
/// long date, the month title with Today / previous / next, the Sunday-first 6 x 7 grid, and, when Google Calendar is on,
/// the day dots and the selected day's agenda between the grid and the "Notifications" footer.
/// THE OPEN PATH MAKES NO CALL: the panel draws from the last "gcal.status" / "gcal.events" pushes it already holds (the
/// day index is built when a push arrives, GcalDays); gcal.opened goes only after the first frame, and only when signed in.
/// UI thread only. Ids: docs/lanes/calendar.md.
/// </summary>
internal sealed class CalPanel
{
    public const double WidthDip = 268, PadX = 12, PadY = 11, CellH = 28, RowH = 28, ListMax = 140;
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    /// What the panel may ask of the bar: a new height, the keyboard (Q-W6) and its return, the close, and Windows' own
    /// notification centre (win+n) until Mantle's (Bar/Notifications) is native.
    public sealed record Room(Func<double, bool> Resize, Func<(bool Ok, string Words)> TakeKeys, Action<string> GiveKeys, Action<string> Close,
        Func<string, Task<string?>> Keys);

    readonly Action<string> _log;
    readonly bool _test;
    readonly Func<DateTime> _now;
    readonly Room _room;
    readonly DispatcherQueue _q;
    readonly DispatcherQueueTimer _sec;
    SurfaceLink? _link;
    readonly List<IDisposable> _subs = new();
    readonly HashSet<string> _missing = new();

    public readonly Grid Root;
    readonly StackPanel _stack;
    readonly TextBlock _big, _sub, _title;
    readonly Button _today, _prev, _next, _notifs;
    readonly Grid _dowRow, _grid;
    readonly Cell[] _cells = new Cell[42];
    readonly StackPanel _agenda;
    readonly Border _agendaBox, _foot;

    sealed class Cell
    {
        public Button B = null!; public Ellipse Bg = null!, Ring = null!, Focus = null!, Dot = null!; public TextBlock Num = null!;
        public string Key = ""; public bool Other, Today, Sel, Hover;
    }

    JsonObject? _status;
    JsonNode? _payload;
    GcalDays? _idx;
    DateTime _shown;
    string _sel = "";
    readonly Dictionary<string, string> _asked = new(StringComparer.Ordinal);
    string _openNote = "";
    string _gridTip = "";
    bool _dark, _kbd;
    int _openSeq;
    double _wheel;
    double _fitted;

    public bool Open { get; private set; }

    public CalPanel(Action<string> log, bool test, Func<DateTime> now, Room room)
    {
        _log = log; _test = test; _now = now; _room = room;
        _q = DispatcherQueue.GetForCurrentThread();
        _sec = _q.CreateTimer();
        _sec.IsRepeating = false;
        _sec.Tick += (_, _) => { if (!Open) return; PaintClock(); AimSecond(); };

        _stack = new StackPanel { Padding = new Thickness(PadX, PadY, PadX, PadY) };
        Root = new Grid { Width = WidthDip - 2, XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled };
        Root.Children.Add(_stack);
        Uia(Root, "cal.panel", "Calendar");

        // ---- .calhead: the big time (15, tabular) and the long date (ink-2), 8 below
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        _big = Text("", 15); Uia(_big, "cal.time", "Time");
        _sub = Text("", 12.5); _sub.Margin = new Thickness(0, 2, 0, 0); Uia(_sub, "cal.date", "Date");
        head.Children.Add(_big); head.Children.Add(_sub);
        _stack.Children.Add(head);

        // ---- .calnav: the title, Today, previous, next (gap 2, 6 below)
        var nav = new Grid { ColumnSpacing = 2, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var w in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) nav.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        _title = Text("", 12.5); _title.VerticalAlignment = VerticalAlignment.Center; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        Uia(_title, "cal.title", "Month");
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
        _today = NavBtn("cal.today", "Back to this month", null); _today.Content = Text("Today", 12.5); _today.Padding = new Thickness(7, 0, 7, 0);
        _prev = NavBtn("cal.prev", "Previous month", "M9.8 3.8L5.6 8l4.2 4.2");
        _next = NavBtn("cal.next", "Next month", "M6.2 3.8L10.4 8l-4.2 4.2");
        _today.Click += (_, _) => { var t = _now(); _shown = new DateTime(t.Year, t.Month, 1); PaintGrid(); };
        _prev.Click += (_, _) => Step(-1);
        _next.Click += (_, _) => Step(1);
        int col = 0;
        foreach (var e in new FrameworkElement[] { _title, _today, _prev, _next }) { Grid.SetColumn(e, col++); nav.Children.Add(e); }
        _stack.Children.Add(nav);

        // ---- the weekday row (Sunday first, the locale's two letters, ink-3, 2 below) and the 42-day grid (gap 1, 28 tall)
        _dowRow = Columns(); _dowRow.Margin = new Thickness(0, 0, 0, 2);
        for (int i = 0; i < 7; i++) {
            var t = Text(GcalDays.Words.Dow((DayOfWeek)i), 12.5); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 2, 0, 2);
            Uia(t, "cal.dow." + i, t.Text);
            Grid.SetColumn(t, i); _dowRow.Children.Add(t);
        }
        _stack.Children.Add(_dowRow);
        _grid = Columns(); _grid.RowSpacing = 1;
        for (int r = 0; r < 6; r++) _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellH) });
        for (int i = 0; i < 42; i++) { var c = MakeCell(); Grid.SetColumn(c.B, i % 7); Grid.SetRow(c.B, i / 7); _grid.Children.Add(c.B); _cells[i] = c; }
        Uia(_grid, "cal.grid", "Days: arrow keys move, Enter shows the day's events");
        _stack.Children.Add(_grid);
        foreach (var el in new UIElement[] { nav, _dowRow, _grid }) el.PointerWheelChanged += OnWheel;

        // ---- .gcal: the agenda, between the grid and the footer (8 above, a hairline, 8 inside); takes the width, never sets it
        _agenda = new StackPanel();
        _agendaBox = new Border { Child = _agenda, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        Uia(_agendaBox, "cal.agenda", "Events");
        _stack.Children.Add(_agendaBox);

        // ---- .calfoot: "Notifications", right-aligned, a hairline 9 above it
        _notifs = new Button { Style = BtnStyle(), Content = Text("Notifications", 12.5), Padding = new Thickness(7, 5, 7, 5), HorizontalAlignment = HorizontalAlignment.Right };
        ToolTipService.SetToolTip(_notifs, "Open Mantle's notification centre (the bell beside the clock)");
        Uia(_notifs, "cal.notifications", "Notifications");
        _notifs.Click += (_, _) => _ = Notifications();
        _foot = new Border { Child = _notifs, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 9, 0, 0), Padding = new Thickness(0, 9, 0, 0) };
        _stack.Children.Add(_foot);

        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKey), true);
    }

    // ------------------------------------------------------------------ host side

    /// From the host (App.xaml.cs, block "calendar wiring"), on the UI thread: the road to the gcal.* commands and pushes.
    public void Attach(SurfaceLink link)
    {
        _link = link;
        _subs.Add(link.On<JsonElement>(GcalFeature.StatusEvent, e => { _status = Obj(e); if (Open) { RepaintKeepFocus(); } }));
        _subs.Add(link.On<JsonElement>(GcalFeature.EventsEvent, e => {
            _payload = Obj(e);
            _idx = GcalDays.Index(_payload);
            _log($"cal: gcal.events arrived (ver {_idx.Ver}, {_idx.Days.Count} days with events); the day index is built now, not on open");
            if (Open) RepaintKeepFocus();
        }));
    }

    bool Active => _status != null && !(_status["enabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
    bool Linked => Active && GcalCore.Bool(_status!["signedIn"]);

    /// Before the pop-up opens: today's month, today selected, painted for the bar's theme; the height it needs (DIP).
    public double Prepare(bool dark)
    {
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        var t = _now();
        _shown = new DateTime(t.Year, t.Month, 1);     // EVERY open starts on today's month (BAR-418)
        _sel = GcalDays.KeyOf(t);
        _asked.Clear(); _openNote = ""; _gridTip = ""; _wheel = 0;
        Rezone();
        PaintClock(); PaintChrome(); PaintGrid(); PaintAgenda();
        return _fitted = Need();
    }

    /// A Windows time-zone change since the last push re-buckets the same push (no network) on the next open.
    void Rezone()
    {
        if (_payload == null || _idx == null) return;
        if (_idx.Zone != GcalDays.ZoneOf(TimeZoneInfo.Local)) { TimeZoneInfo.ClearCachedData(); _idx = GcalDays.Index(_payload); _log("cal: the time zone changed since the last push, so its events were placed on their new local days"); }
    }

    double Need()
    {
        Root.Measure(new Windows.Foundation.Size(WidthDip - 2, double.PositiveInfinity));
        return Math.Min(Math.Ceiling(Root.DesiredSize.Height) + 2, 520);
    }

    void Refit()
    {
        if (!Open) return;
        double h = Need();
        if (Math.Abs(h - _fitted) < 0.5) return;
        _fitted = h;
        _room.Resize(h);
    }

    public void Opened()
    {
        Open = true;
        int seq = ++_openSeq;
        AimSecond();
        _log($"cal: the calendar opened ({WidthDip}x{_fitted} DIP), {GcalDays.Words.MonthYear(_shown)}, {(Linked ? "Google Calendar on" : Active ? "Google Calendar not connected" : "no Google Calendar")}");
        // after the first frame, never before it: the refresh-if-stale ask and the keyboard (gcal.js mount)
        _q.TryEnqueue(DispatcherQueuePriority.Low, () => {
            if (!Open || seq != _openSeq) return;
            if (Linked && _link != null) { _ = _link.CallAsync("gcal.opened"); _log("cal: gcal.opened sent after the first frame"); }
            else _log("cal: no gcal call on open (" + (Active ? "not signed in" : "Google Calendar is off") + ")");
            Keyboard(seq, 0);
        });
    }

    public void Closed(string why)
    {
        Open = false; _openSeq++;
        _sec.Stop();
        if (_kbd) { _kbd = false; _room.GiveKeys("the calendar closed"); }
        _openNote = "";
        _log($"cal: the calendar closed ({why})");
    }

    public void KeyboardLost() { if (!_kbd) return; _kbd = false; _log("cal: another window took the keyboard"); }

    void Keyboard(int seq, int tries)
    {
        if (!Open || seq != _openSeq || !Active) return;
        var (ok, words) = _room.TakeKeys();
        if (!ok && words == "the pop-up is not open" && tries < 20) { var t = _q.CreateTimer(); t.Interval = TimeSpan.FromMilliseconds(50); t.IsRepeating = false; t.Tick += (_, _) => Keyboard(seq, tries + 1); t.Start(); return; }
        _kbd = ok;
        _gridTip = ok ? "" : "Arrow keys do not reach this calendar: " + words.Split(", so ")[0];
        PaintGridTip();
        _log(ok ? "cal: the calendar has the keyboard; the selected day has focus" : "cal: " + _gridTip);
        if (ok) FocusSel();
    }

    void FocusSel()
    {
        var c = _cells.FirstOrDefault(x => x.Key == _sel);
        if (c != null) c.B.Focus(FocusState.Keyboard);
    }

    // ------------------------------------------------------------------ the clock and the grid

    void AimSecond()
    {
        var n = _now();
        _sec.Interval = TimeSpan.FromMilliseconds(1000 - n.Millisecond + 20);
        _sec.Start();
    }

    void PaintClock()
    {
        var d = _now();
        int h = d.Hour % 12; if (h == 0) h = 12;
        _big.Text = h.ToString(CultureInfo.InvariantCulture) + ":" + d.ToString("mm:ss", CultureInfo.InvariantCulture) + (d.Hour < 12 ? " AM" : " PM");
        _sub.Text = GcalDays.Words.Long(d);
        Type(_big, _dark ? 16 : 15, 1.33); Type(_sub, Fs, 1.33);
        _big.Foreground = B("LgInk"); _sub.Foreground = B("LgInk2");
    }

    double Fs => _dark ? 13 : 12.5;

    /// The parts that only change with the theme: the footer link (.qslink, ink-2) and the two hairlines (--lg-edge-out).
    void PaintChrome()
    {
        var t = (TextBlock)_notifs.Content;
        t.Foreground = B("LgInk2"); Type(t, Fs, 1.33);
        _foot.BorderBrush = B("LgEdgeOut");
        _agendaBox.BorderBrush = B("LgEdgeOut");
    }

    void Step(int n)
    {
        _shown = _shown.AddMonths(n);
        PaintGrid();
    }

    void PaintGrid()
    {
        var t = _now().Date;
        _title.Text = GcalDays.Words.MonthYear(_shown);
        Type(_title, Fs, 1.33); _title.Foreground = B("LgInk");
        bool onThis = _shown.Year == t.Year && _shown.Month == t.Month;
        _today.IsEnabled = !onThis;
        ((TextBlock)_today.Content).Foreground = B(onThis ? "LgInk3" : "LgInk2");
        _today.Opacity = onThis ? (_dark ? 0.45 : 0.6) : 1;
        Type((TextBlock)_today.Content, Fs, 1.33);
        foreach (var tb in _dowRow.Children.OfType<TextBlock>()) { Type(tb, _dark ? 12 : 12.5, 1.33); tb.Foreground = B("LgInk3"); }
        var start = _shown.AddDays(-(int)_shown.DayOfWeek);   // back to the Sunday on or before the 1st
        bool linked = Linked && _idx != null;
        bool selHere = false;
        for (int i = 0; i < 42; i++) {
            var d = start.AddDays(i);
            var c = _cells[i];
            c.Key = GcalDays.KeyOf(d); c.Other = d.Month != _shown.Month; c.Today = d == t; c.Sel = Active && c.Key == _sel;
            selHere |= c.Sel;
            c.Num.Text = d.Day.ToString(CultureInfo.InvariantCulture);
            string name = GcalDays.Words.Long(d);
            var dot = linked ? _idx!.DotOf(c.Key) : null;
            if (dot != null) name += ", " + dot.N + (dot.N == 1 ? " event" : " events");
            ToolTipService.SetToolTip(c.B, GcalDays.Words.Long(d));
            AutomationProperties.SetAutomationId(c.B, "cal.day." + c.Key);
            AutomationProperties.SetName(c.B, name);
            AutomationProperties.SetItemStatus(c.B, string.Join("; ", new[] { c.Sel ? "selected" : null, c.Today ? "today" : null, c.Other ? "other" : null, dot != null ? "dot" : null }.Where(x => x != null)));
            AutomationProperties.SetHelpText(c.B, dot == null ? "" : dot.Many ? "many" : dot.Color ?? "no colour");
            c.Dot.Visibility = dot != null ? Visibility.Visible : Visibility.Collapsed;
            c.Dot.Fill = dot?.Color != null && GcalColors.Parse(dot.Color) is { } rgb ? Solid(rgb) : B("LgInk2");
            c.Dot.Opacity = c.Other ? 0.6 : 1;
            PaintCell(c);
        }
        // the selected day is in another month: the grid still gets one tab stop (its 1st)
        foreach (var c in _cells) c.B.IsTabStop = c.Sel || (!selHere && !c.Other && c.Num.Text == "1");
        PaintGridTip();
        if (Open) {
            _log("cal: shows " + GcalDays.Words.MonthYear(_shown));
            AskMonth();
            PaintAgenda();
            Refit();
        }
    }

    void PaintGridTip()
    {
        ToolTipService.SetToolTip(_grid, _gridTip.Length > 0 ? _gridTip : null);
        AutomationProperties.SetHelpText(_grid, _gridTip);
    }

    void PaintCell(Cell c)
    {
        bool on = Active;
        c.Num.Foreground = B(c.Today ? "LgAcc" : (on && (c.Sel || c.Hover)) ? "LgInk" : c.Other ? "LgInk3" : "LgInk2");
        c.Num.Opacity = c.Other ? 0.7 : 1;
        Type(c.Num, Fs, 1.2);
        c.Bg.Fill = on && c.Sel ? B("LgAccSoft") : on && c.Hover ? B("LgHover") : null;
        c.Ring.Stroke = c.Today ? B("LgAcc") : null;
        c.Focus.Stroke = c.B.FocusState == FocusState.Keyboard ? B("LgAcc") : null;
    }

    Cell MakeCell()
    {
        var c = new Cell();
        var g = new Grid { Height = CellH };
        c.Bg = new Ellipse(); c.Ring = new Ellipse { StrokeThickness = 1 }; c.Focus = new Ellipse { StrokeThickness = 2 };
        c.Num = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Font() };
        // gcal.css .gcal-dot: 4 px, centred, 2 above the cell's bottom
        c.Dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        g.Children.Add(c.Bg); g.Children.Add(c.Ring); g.Children.Add(c.Num); g.Children.Add(c.Dot); g.Children.Add(c.Focus);
        c.B = new Button { Style = CellStyle(), Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        c.B.Click += (_, _) => Select(c.Key, false);
        c.B.PointerEntered += (_, _) => { c.Hover = true; PaintCell(c); };
        c.B.PointerExited += (_, _) => { c.Hover = false; PaintCell(c); };
        c.B.GotFocus += (_, _) => PaintCell(c);
        c.B.LostFocus += (_, _) => PaintCell(c);
        return c;
    }

    void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        // down = later, one month per notch; a touchpad's small deltas summed to 50 per step (bar.js)
        int delta = e.GetCurrentPoint(Root).Properties.MouseWheelDelta;
        e.Handled = true;
        _wheel += -delta;
        if (Math.Abs(_wheel) < 50) return;
        Step(_wheel > 0 ? 1 : -1);
        _wheel = 0;
    }

    // ------------------------------------------------------------------ selecting a day, the keys

    void Select(string k, bool focus)
    {
        if (!Active) return;
        var d = GcalDays.DateOf(k);
        _sel = k; _openNote = "";
        int diff = (d.Year - _shown.Year) * 12 + d.Month - _shown.Month;
        if (diff != 0) Step(diff);   // repaints the grid, which re-marks the selection
        else { foreach (var c in _cells) { c.Sel = c.Key == k; c.B.IsTabStop = c.Sel; AutomationProperties.SetItemStatus(c.B, Status(c)); PaintCell(c); } PaintAgenda(); Refit(); }
        _log($"cal: day {k} selected ({(_idx != null && Linked ? _idx.On(k).Count : 0)} events)");
        if (focus) FocusSel();
    }

    string Status(Cell c) => string.Join("; ", new[] { c.Sel ? "selected" : null, c.Today ? "today" : null, c.Other ? "other" : null, c.Dot.Visibility == Visibility.Visible ? "dot" : null }.Where(x => x != null));

    void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (!Open || !Active) return;
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        var cell = _cells.FirstOrDefault(c => ReferenceEquals(c.B, focused));
        if (cell != null) {
            var d = GcalDays.DateOf(cell.Key);
            DateTime? n = e.Key switch {
                Windows.System.VirtualKey.Left => d.AddDays(-1),
                Windows.System.VirtualKey.Right => d.AddDays(1),
                Windows.System.VirtualKey.Up => d.AddDays(-7),
                Windows.System.VirtualKey.Down => d.AddDays(7),
                Windows.System.VirtualKey.Home => d.AddDays(-(int)d.DayOfWeek),
                Windows.System.VirtualKey.End => d.AddDays(6 - (int)d.DayOfWeek),
                Windows.System.VirtualKey.PageUp => new DateTime(d.Year, d.Month, 1).AddMonths(-1).AddDays(Math.Min(d.Day, 28) - 1),
                Windows.System.VirtualKey.PageDown => new DateTime(d.Year, d.Month, 1).AddMonths(1).AddDays(Math.Min(d.Day, 28) - 1),
                _ => null,
            };
            if (e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space) {
                e.Handled = true;
                Select(cell.Key, false);
                var first = (UIElement?)_rows.FirstOrDefault() ?? _connect;
                if (first is Control f) f.Focus(FocusState.Keyboard); else FocusSel();
                return;
            }
            if (n is { } to) { e.Handled = true; Select(GcalDays.KeyOf(to), true); }
            return;
        }
        int i = _rows.FindIndex(r => ReferenceEquals(r, focused));
        if (i < 0 || (e.Key != Windows.System.VirtualKey.Down && e.Key != Windows.System.VirtualKey.Up)) return;
        e.Handled = true;
        if (e.Key == Windows.System.VirtualKey.Up && i == 0) { FocusSel(); return; }   // up out of the list: back to the day
        int j = Math.Clamp(i + (e.Key == Windows.System.VirtualKey.Down ? 1 : -1), 0, _rows.Count - 1);
        _rows[j].Focus(FocusState.Keyboard);
    }

    // ------------------------------------------------------------------ the agenda

    readonly List<Button> _rows = new();
    Button? _connect;

    void RepaintKeepFocus()
    {
        var focused = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        int row = _rows.FindIndex(r => ReferenceEquals(r, focused));
        string? key = row >= 0 ? (string?)_rows[row].Tag : null;
        bool onConnect = focused != null && ReferenceEquals(focused, _connect);
        PaintGrid(); PaintAgenda();
        if (key != null) ((Control?)_rows.FirstOrDefault(r => (string?)r.Tag == key) ?? _rows.FirstOrDefault())?.Focus(FocusState.Keyboard);
        else if (onConnect) _connect?.Focus(FocusState.Keyboard);
        Refit();
    }

    void PaintAgenda()
    {
        _agenda.Children.Clear(); _rows.Clear(); _connect = null;
        _agendaBox.BorderBrush = B("LgEdgeOut");
        if (!Active) { _agendaBox.Visibility = Visibility.Collapsed; return; }
        _agendaBox.Visibility = Visibility.Visible;
        var s = _status!;
        if (!GcalCore.Bool(s["signedIn"])) {
            // not connected: ONE quiet row; Settings says the rest (the client file, the sign-in)
            _connect = new Button { Style = BtnStyle(), Content = Text("Connect Google Calendar", Fs), Padding = new Thickness(7, 5, 7, 5), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            ((TextBlock)_connect.Content).Foreground = B("LgInk2");
            ToolTipService.SetToolTip(_connect, "Open Mantle Settings to sign in with Google (read-only)");
            Uia(_connect, "cal.connect", "Connect Google Calendar");
            _connect.IsTabStop = true;
            _connect.Click += (_, _) => _ = ConnectClick();
            _agenda.Children.Add(_connect);
            if (_openNote.Length > 0) _agenda.Children.Add(Note(_openNote));
            return;
        }
        var head = Text(GcalDays.DayTitle(_sel, _now().Date), _dark ? 12 : 12);
        head.Foreground = B("LgInk2"); head.Margin = new Thickness(6, 0, 6, 3); Type(head, 12, 1.33);
        Uia(head, "cal.agenda.head", head.Text);
        _agenda.Children.Add(head);
        var list = new StackPanel();
        Uia(list, "cal.agenda.list", "Events of the day");
        var evs = _idx?.On(_sel) ?? Array.Empty<GcalDays.Row>();
        if (evs.Count == 0) {
            bool cov = _idx == null || _idx.CoversDay(_sel);
            var empty = Text(cov ? "No events" : "Not loaded yet", Fs);
            empty.Foreground = B("LgInk3"); empty.Margin = new Thickness(6, 6, 6, 6); Type(empty, Fs, 1.2);
            Uia(empty, "cal.agenda.empty", empty.Text);
            list.Children.Add(empty);
        }
        int n = 0;
        foreach (var ev in evs) list.Children.Add(MakeRow(ev, n++));
        var scroll = new ScrollViewer { Content = list, MinHeight = RowH, MaxHeight = ListMax, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _agenda.Children.Add(scroll);
        string line = GcalDays.StatusLine(s, _openNote, _asked.GetValueOrDefault(MonthKey()), _idx?.Covers(_shown.Year, _shown.Month - 1) ?? false, _shown, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (line.Length > 0) {
            var note = Note(line);
            AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
            ToolTipService.SetToolTip(note, line);
            _agenda.Children.Add(note);
        }
    }

    Button MakeRow(GcalDays.Row ev, int n)
    {
        var g = new Grid { Height = RowH, ColumnSpacing = 8, Padding = new Thickness(6, 0, 6, 0) };
        foreach (var w in new[] { new GridLength(3), GridLength.Auto, new GridLength(1, GridUnitType.Star) }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        string? hex = _idx!.Colors.GetValueOrDefault(ev.Cal);
        var rgb = GcalColors.Parse(hex);
        var bar = new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(2), Background = rgb is { } c ? Solid(c) : B("LgInk3"), VerticalAlignment = VerticalAlignment.Center };
        var time = Text(_idx.TimeOf(ev, _sel), Fs); time.MinWidth = 58; time.Foreground = B("LgInk2"); time.VerticalAlignment = VerticalAlignment.Center; Type(time, Fs, 1.2);
        var title = Text(ev.Title, Fs); title.TextTrimming = TextTrimming.CharacterEllipsis; title.TextWrapping = TextWrapping.NoWrap; title.VerticalAlignment = VerticalAlignment.Center; Type(title, Fs, 1.2);
        title.Foreground = TitleInk(rgb);
        Grid.SetColumn(time, 1); Grid.SetColumn(title, 2);
        g.Children.Add(bar); g.Children.Add(time); g.Children.Add(title);
        var b = new Button { Style = BtnStyle(), Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Tag = ev.K, IsTabStop = true };
        string tip = ev.Title + "\n" + _idx.RangeOf(ev) + (_idx.Names.GetValueOrDefault(ev.Cal) is { Length: > 0 } nm ? "\n" + nm : "") + "\nClick: open in your browser";
        ToolTipService.SetToolTip(b, tip);
        Uia(b, "cal.row." + n, ev.Title);
        AutomationProperties.SetHelpText(b, tip);
        AutomationProperties.SetItemStatus(b, time.Text);
        b.Click += (_, _) => _ = OpenEvent(ev.K);
        _rows.Add(b);
        return b;
    }

    /// gcal.css: color-mix(in oklab, the calendar's colour 60%, --lg-ink).
    Brush TitleInk(GcalColors.Rgba? cal)
    {
        var ink = B("LgInk");
        if (cal is not { } c || ink is not SolidColorBrush sb) return ink;
        var m = GcalColors.MixOklab(c, new GcalColors.Rgba(sb.Color.R, sb.Color.G, sb.Color.B, sb.Color.A / 255.0 * sb.Opacity), 0.6);
        return Solid(m);
    }

    TextBlock Note(string text)
    {
        var t = Text(text, _dark ? 12 : 11.5);
        t.Foreground = B("LgInk2"); t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(6, 6, 6, 0); Type(t, _dark ? 12 : 11.5, 1.35);
        Uia(t, "cal.agenda.note", text);
        return t;
    }

    string MonthKey() => _shown.Year + "-" + (_shown.Month - 1);

    /// A month the cache does not hold is asked for once per open, after the paint (gcal.js painted).
    void AskMonth()
    {
        if (!Open || !Linked || _idx == null || _link == null) return;
        int y = _shown.Year, m = _shown.Month - 1; string key = MonthKey();
        if (_idx.Covers(y, m) || _asked.ContainsKey(key)) return;
        _asked[key] = "asking";
        int seq = _openSeq;
        _q.TryEnqueue(DispatcherQueuePriority.Low, async () => {
            if (!Open || seq != _openSeq) return;
            _log($"cal: asked gcal.want for {y}-{m + 1:00}");
            var r = await _link.CallAsync("gcal.want", new JsonObject { ["year"] = y, ["month"] = m });
            if (!Open || seq != _openSeq) return;
            var o = r.Ok ? Obj(r.Result) : null;
            _asked[key] = !r.Ok ? "no:" + r.Error : GcalCore.Bool(o?["loading"]) ? "loading" : GcalCore.Str(o?["reason"]) is { } why ? "no:" + why : "done";
            PaintAgenda(); Refit();
        });
    }

    async Task OpenEvent(string k)
    {
        if (_link == null) return;
        var r = await _link.CallAsync("gcal.open", new JsonObject { ["event"] = k });
        var o = r.Ok ? Obj(r.Result) : null;
        if (!r.Ok || o?["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok) _openNote = "The event did not open: " + (r.Ok ? GcalCore.Str(o?["reason"]) ?? "no reason came back" : r.Error);
        else if (GcalCore.Bool(o?["dryRun"])) _openNote = GcalCore.Str(o?["reason"]) ?? "test run: the browser is not opened";
        else { _room.Close("an event opened in the browser"); return; }
        _log("cal: " + _openNote);
        RepaintKeepFocus();
    }

    async Task ConnectClick()
    {
        if (_link == null) return;
        var r = await _link.CallAsync("settings.open", new JsonObject { ["section"] = "time", ["page"] = "datetime", ["row"] = "time.datetime.gcal", ["via"] = "the calendar" });
        var o = r.Ok ? Obj(r.Result) : null;
        if (!r.Ok || o?["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok) {
            _openNote = "Settings did not open: " + (r.Ok ? GcalCore.Str(o?["reason"]) ?? "no reason came back" : r.Error);
            _log("cal: " + _openNote);
            RepaintKeepFocus();
            return;
        }
        _room.Close("Settings opened");
    }

    async Task Notifications()
    {
        // Mantle's own centre (the bell, Bar/Notifications) is another lane's; until it is native, Windows' own (win+n)
        if (_test) { _log("cal: dry run (--test): would press win+n (Windows' notification centre); nothing was pressed"); return; }
        _room.Close("Notifications");
        if (await _room.Keys("win+n") is { } why) _log("cal: Windows' notification centre did not open: " + why);
    }

    // ------------------------------------------------------------------ look

    Grid Columns()
    {
        var g = new Grid { ColumnSpacing = 1 };
        for (int i = 0; i < 7; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return g;
    }

    Button NavBtn(string id, string tip, string? chevron)
    {
        var b = new Button { Style = BtnStyle(), MinWidth = 26, Height = 26 };
        if (chevron != null) {
            var c = new Canvas { Width = 16, Height = 16 };
            c.Children.Add((UIElement)XamlReader.Load($"<Path {Ns} Data='{chevron.Replace("L", " L ").Replace("l", " l ").Replace("M", "M ")}' Stroke='{{ThemeResource LgInk2}}' StrokeThickness='1.35' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>"));
            b.Content = new Viewbox { Width = 14, Height = 14, Child = c };
            b.Width = 26;
        }
        ToolTipService.SetToolTip(b, tip);
        Uia(b, id, tip);
        return b;
    }

    static Style? _btnStyle, _cellStyle;
    /// .calbtn / .qslink / .gcal-row: a flat face, the hover and press tokens over it, the focus ring inside.
    static Style BtnStyle() => _btnStyle ??= (Style)XamlReader.Load(
        $"<Style {Ns} TargetType='Button'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/>" +
        "<Setter Property='MinWidth' Value='0'/><Setter Property='MinHeight' Value='0'/><Setter Property='CornerRadius' Value='{ThemeResource LgRCtlCorner}'/>" +
        "<Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='{ThemeResource LgInk}'/>" +
        "<Setter Property='IsTabStop' Value='False'/><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
        "<Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='VerticalContentAlignment' Value='Center'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<Grid x:Name='Face' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' CornerRadius='{TemplateBinding CornerRadius}'>" +
        "<Border x:Name='Over' CornerRadius='{TemplateBinding CornerRadius}'/>" +
        "<Border x:Name='Ring' CornerRadius='{TemplateBinding CornerRadius}' BorderThickness='2'/>" +
        "<ContentPresenter Padding='{TemplateBinding Padding}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}' Foreground='{TemplateBinding Foreground}'/>" +
        "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'><VisualState x:Name='Normal'/>" +
        "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgHover}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgPress}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Disabled'/></VisualStateGroup>" +
        "<VisualStateGroup x:Name='FocusStates'><VisualState x:Name='Unfocused'/><VisualState x:Name='PointerFocused'/>" +
        "<VisualState x:Name='Focused'><VisualState.Setters><Setter Target='Ring.BorderBrush' Value='{ThemeResource LgAcc}'/></VisualState.Setters></VisualState></VisualStateGroup>" +
        "</VisualStateManager.VisualStateGroups></Grid></ControlTemplate></Setter.Value></Setter></Style>");

    /// A day cell: the content draws everything itself (an ellipse, as border-radius 50% on a 34 x 28 box is).
    static Style CellStyle() => _cellStyle ??= (Style)XamlReader.Load(
        $"<Style {Ns} TargetType='Button'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/>" +
        "<Setter Property='MinWidth' Value='0'/><Setter Property='MinHeight' Value='0'/><Setter Property='Background' Value='Transparent'/>" +
        "<Setter Property='IsTabStop' Value='False'/><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<ContentPresenter Background='{TemplateBinding Background}' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Stretch'/>" +
        "</ControlTemplate></Setter.Value></Setter></Style>");

    TextBlock Text(string s, double size) => new() { Text = s, FontSize = size, Foreground = B("LgInk"), FontFamily = Font() };
    FontFamily? _font;
    FontFamily Font() => _font ??= new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

    static void Uia(FrameworkElement e, string id, string name)
    {
        AutomationProperties.SetAutomationId(e, id);
        AutomationProperties.SetName(e, name);
    }

    /// A CSS line box: font size and line-height (a multiple), whole pixels.
    static void Type(TextBlock t, double fs, double lh)
    {
        t.FontSize = fs;
        t.LineHeight = Math.Round(fs * lh, MidpointRounding.AwayFromZero);
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    /// A calendar's own colour (Google's data, GcalColors), never a theme colour.
    static SolidColorBrush Solid(GcalColors.Rgba c) => new(new Windows.UI.Color { A = (byte)Math.Round(c.A * 255), R = c.R, G = c.G, B = c.B });

    static JsonObject? Obj(JsonElement e) => e.ValueKind == JsonValueKind.Object ? JsonNode.Parse(e.GetRawText()) as JsonObject : null;

    /// The --lg-* token of the panel's theme (Theme/Tokens.xaml), read in code so a repaint follows the bar's theme.
    Brush B(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        if (_missing.Add(key)) _log($"cal: the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }
    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }
}
