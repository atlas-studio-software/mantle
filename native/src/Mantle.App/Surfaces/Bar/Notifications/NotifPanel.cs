using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using static Mantle.App.Surfaces.Bar.Notifications.BarLook;

namespace Mantle.App.Surfaces.Bar.Notifications;

/// <summary>
/// The bar's notification centre (ui/bar/notifications.js build/paint, notifications.css .ntf*), lane native-r7-notif:
/// the head ("NOTIFICATIONS · N" and Clear all), the list grouped by app (icon or letter, name, count, dismiss-all), one
/// card per toast (logo, title, body clamped to 4 lines, attribution · age, dismiss, hero picture, the unread dot of a
/// toast that was new when the panel opened), the empty and unavailable states, the note, the plain-source detail and
/// the Win+N link to Windows' own centre. Everything goes through the host's notifs.* commands over a
/// <see cref="SurfaceLink"/> (caller "bar"); the list arrives as the "notifications" event. UI thread only.
/// Ids: notifs.panel, notifs.head, notifs.clear, notifs.list, notifs.empty, notifs.group.N (N = the group's place),
/// notifs.group.N.x, notifs.card.ID, notifs.card.ID.x, notifs.note, notifs.detail, notifs.windows.
/// </summary>
internal sealed class NotifPanel
{
    public const double WidthDip = 340;
    static readonly string[] Bell = { "M8 2.2a3.9 3.9 0 0 0-3.9 3.9v2.6L2.9 11h10.2l-1.2-2.3V6.1A3.9 3.9 0 0 0 8 2.2Z", "M6.4 12.9a1.7 1.7 0 0 0 3.2 0" };

    public sealed record Room(Func<double, bool> Resize, Action<string> Close, Action<string, string> Keys);

    readonly Action<string> _log;
    readonly Room _room;
    SurfaceLink? _link;
    JsonObject? _data;
    string _note = "";
    readonly Dictionary<string, bool> _busy = new(StringComparer.Ordinal);
    HashSet<long> _wasUnread = new();
    bool _dark;
    DispatcherTimerLite? _ticker;

    public readonly Grid Root;
    readonly StackPanel _stack, _list;
    readonly ScrollViewer _scroll;
    readonly TextBlock _h, _noteEl, _detail;
    readonly Button _clear, _win;
    readonly Border _foot, _noteBox;

    /// Set by the bar while the pop-up is open.
    public bool Open { get; private set; }
    /// Called with every list the panel takes (the bell repaints from it).
    public event Action<JsonObject?>? Changed;
    public JsonObject? Data => _data;

    public NotifPanel(Action<string> log, Room room)
    {
        _log = log; _room = room;
        Root = new Grid { Width = WidthDip - 2 };
        Uia(Root, "notifs.panel", "Notifications");
        _stack = new StackPanel { Padding = new Thickness(10, 11, 10, 9) };
        Root.Children.Add(_stack);

        // ---- the head (.ntf-head: padding 0 2 6, the h3 and Clear all on one line)
        var head = new Grid { Padding = new Thickness(2, 0, 2, 6) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _h = new TextBlock { FontFamily = Font, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, CharacterSpacing = 40 };
        Uia(_h, "notifs.head", "Notifications");
        head.Children.Add(_h);
        _clear = Link("notifs.clear", "Clear all", "Dismiss every notification in this list (here and in Windows' own centre)");
        Grid.SetColumn(_clear, 1);
        _clear.Click += (_, _) => _ = Act("all", "notifs.clear", new JsonObject(), "Could not clear them all");
        head.Children.Add(_clear);
        _stack.Children.Add(head);

        // ---- the list (.ntf-list: max 300 tall, 240 with a note or detail; 8 between groups)
        _list = new StackPanel { Spacing = 8 };
        Uia(_list, "notifs.list", "Notifications list");
        _scroll = new ScrollViewer { Content = _list, MaxHeight = 300, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _stack.Children.Add(_scroll);

        // ---- the note (.pop .note: 9 above, an 8 padded hairline, warn ink)
        _noteEl = new TextBlock { FontFamily = Font, TextWrapping = TextWrapping.Wrap };
        Uia(_noteEl, "notifs.note", "Note");
        _noteBox = new Border { Child = _noteEl, Margin = new Thickness(0, 9, 0, 0), Padding = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), Visibility = Visibility.Collapsed };
        _stack.Children.Add(_noteBox);

        // ---- the foot (.ntf-foot: 7 above, a 6 padded hairline; the detail on its own line, the link right)
        var foot = new StackPanel { Spacing = 4 };
        _detail = new TextBlock { FontFamily = Font, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 0), Visibility = Visibility.Collapsed };
        Uia(_detail, "notifs.detail", "Detail");
        foot.Children.Add(_detail);
        _win = Link("notifs.windows", "Windows notification centre", "Open Windows' own notification centre (Win+N): focus settings, and anything Mantle cannot do here");
        _win.HorizontalAlignment = HorizontalAlignment.Right;
        _win.Click += (_, _) => { _room.Close("its Windows notification centre link was clicked"); _room.Keys("win+n", "Windows notification centre"); };
        foot.Children.Add(_win);
        _foot = new Border { Child = foot, Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) };
        _stack.Children.Add(_foot);
    }

    /// From the host (App.xaml.cs, block "notifications wiring"), on the UI thread.
    public void Attach(SurfaceLink link)
    {
        _link = link;
        link.On<NotificationsPayload>(NotificationCentre.Event, p => {
            if (p == null) return;
            Update(JsonNode.Parse(p.Body.GetRawText()) as JsonObject);
        });
    }

    /// notifications.js update(): the bell repaints; an open panel marks what arrived as seen and repaints.
    void Update(JsonObject? p)
    {
        _data = p;
        Changed?.Invoke(p);
        if (Open) { SeeAll(); Paint(); Fit(); }
    }

    JsonArray Toasts() => _data?["toasts"] as JsonArray ?? new JsonArray();

    /// Before the pop-up opens: repaint for the bar's theme and measure the height the panel needs (DIP).
    public double Prepare(bool dark)
    {
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        Paint();
        return Need();
    }

    public void Opened()
    {
        Open = true;
        _wasUnread = new HashSet<long>(Toasts().OfType<JsonObject>().Where(t => t["unread"] is JsonValue v && v.TryGetValue<bool>(out var u) && u)
            .Select(NotificationCentre.Id).Where(i => i != null).Select(i => i!.Value));
        SeeAll();
        if (_data == null && _link != null) _ = Get();
        Paint(); Fit();
        _ticker ??= new DispatcherTimerLite(30000, () => { if (Open) Paint(); });
        _ticker.Start();
        _log($"notifs: the panel opened ({Toasts().Count} notifications, {_wasUnread.Count} new)");
    }

    public void Closed(string why)
    {
        Open = false;
        _ticker?.Stop();
        _log($"notifs: the panel closed ({why})");
    }

    async Task Get()
    {
        var r = await _link!.CallAsync("notifs.get");
        if (r.Ok) Update(JsonNode.Parse(r.Result.GetRawText()) as JsonObject);
        else { _note = "could not read the notifications: " + r.Error; if (Open) { Paint(); Fit(); } }
    }

    async void SeeAll()
    {
        var ids = Toasts().OfType<JsonObject>().Where(t => t["unread"] is JsonValue v && v.TryGetValue<bool>(out var u) && u)
            .Select(NotificationCentre.Id).Where(i => i != null).Select(i => (JsonNode)JsonValue.Create(i!.Value)).ToArray();
        if (ids.Length == 0 || _link == null) return;
        var r = await _link.CallAsync("notifs.seen", new JsonObject { ["ids"] = new JsonArray(ids) });
        if (!r.Ok) { _note = "could not mark these as seen, so the bell may keep counting them: " + r.Error; if (Open) { Paint(); Fit(); } }
    }

    /// notifications.js act(): busy while it runs; a refusal or a partial failure becomes the note.
    async Task<JsonObject?> Act(string key, string command, JsonObject args, string what)
    {
        if (_link == null) { _note = what + " failed: the bar is not connected to Mantle yet"; Paint(); return null; }
        _busy[key] = true; _note = "";
        Paint();
        var r = await _link.CallAsync(command, args);
        _busy.Remove(key);
        JsonObject? o = null;
        if (!r.Ok) _note = what + " failed: " + r.Error;
        else {
            o = JsonNode.Parse(r.Result.GetRawText()) as JsonObject;
            if (o?["failed"] is JsonArray f && f.Count > 0) _note = what + ": " + (NotificationCentre.Str(o["reason"]) ?? "Windows kept some of them");
        }
        if (Open) { Paint(); Fit(); }
        return o;
    }

    async void OpenToast(JsonObject t)
    {
        if (NotificationCentre.Str(t["opens"]) == "none") return;
        var id = NotificationCentre.Id(t);
        var r = await Act("id:" + id, "notifs.open", new JsonObject { ["id"] = id }, "Could not open " + (NotificationCentre.Str(t["app"]) ?? "it"));
        if (r != null && !NotificationCentre.IsFalse(r["ok"])) _room.Close("a notification was opened");
    }

    // ------------------------------------------------------------------ paint

    Brush T(string key) => B(_dark, key, _log);

    double Fs => _dark ? 13 : 12.5;

    void Paint()
    {
        var ts = Toasts().OfType<JsonObject>().ToList();
        bool bad = _data != null && NotificationCentre.IsFalse(_data["ok"]);
        _h.Text = (ts.Count > 0 ? "Notifications · " + ts.Count : "Notifications").ToUpperInvariant();
        _h.FontSize = _dark ? 11 : 12.5;
        _h.Foreground = T("LgInk3");
        AutomationProperties.SetName(_h, ts.Count > 0 ? "Notifications · " + ts.Count : "Notifications");
        _clear.Visibility = ts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _clear.IsEnabled = !_busy.ContainsKey("all");
        _clear.Foreground = _busy.ContainsKey("all") ? (_dark ? Alpha(T("LgInk2"), 0.4) : T("LgInk3")) : T("LgInk2");
        _clear.FontSize = Fs; _win.FontSize = Fs; _win.Foreground = T("LgInk2");
        _list.Children.Clear();

        if (_data == null) _list.Children.Add(Empty(null, "Reading your notifications…", null, false));
        else if (bad && ts.Count == 0) _list.Children.Add(Empty(null, "Notifications unavailable", NotificationCentre.Str(_data["reason"]) ?? "the list could not be read and no reason was given", true));
        else if (ts.Count == 0) _list.Children.Add(Empty(Bell, "No notifications", "What apps tell you lands here, newest first.", false));
        else {
            int gi = 0;
            foreach (var g in NotificationWords.Groups(ts)) _list.Children.Add(Group(g, gi++));
        }

        string say = NotificationWords.Note(_note, _data);
        _noteEl.Text = say; _noteEl.FontSize = Fs; _noteEl.Foreground = T("LgWarn");
        _noteEl.LineHeight = Math.Round(Fs * 1.45); _noteEl.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _noteBox.BorderBrush = T("LgEdgeOut");
        _noteBox.Visibility = say.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        string? detail = NotificationCentre.Str(_data?["detail"]);
        _detail.Text = detail != null ? "Plainer than it could be: " + detail : "";
        Line(_detail, 11, 1.35); _detail.Foreground = T("LgInk3");
        _detail.Visibility = detail != null ? Visibility.Visible : Visibility.Collapsed;
        _scroll.MaxHeight = say.Length > 0 || detail != null ? 240 : 300;
        _foot.BorderBrush = T(_dark ? "LgDivider" : "LgEdgeOut");
    }

    /// .ntf-empty: an icon tile (ui/shared/empty-state.css .es-tile), the big line and the sub line, centred.
    FrameworkElement Empty(string[]? glyph, string big, string? sub, bool warn)
    {
        var s = new StackPanel { Padding = new Thickness(8, 18, 8, 16), HorizontalAlignment = HorizontalAlignment.Stretch };
        Uia(s, "notifs.empty", big);
        if (glyph != null) {
            var tile = new Border {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = T("LgEdgeOut"),
                Background = T("LgCard"), Margin = new Thickness(0, 0, 0, 12), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Viewbox { Width = 18, Height = 18, Child = Glyph(glyph, 16, 1.3, "LgInk2") },
            };
            s.Children.Add(tile);
        }
        var b = new TextBlock { Text = big, FontFamily = Font, FontSize = _dark ? 16 : 13.5, Foreground = T(warn ? "LgWarn" : "LgInk"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        s.Children.Add(b);
        if (sub != null) {
            var t = new TextBlock { Text = sub, FontFamily = Font, Foreground = T("LgInk3"), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            Line(t, Fs, 1.45);
            s.Children.Add(t);
        }
        return s;
    }

    FrameworkElement Group(NotificationWords.Group g, int index)
    {
        string key = "app:" + g.Aumid;
        var box = new StackPanel { Spacing = 3, Opacity = _busy.ContainsKey(key) ? 0.5 : 1 };
        string name = g.App ?? g.Aumid ?? "Unknown app";
        Uia(box, "notifs.group." + index, name);
        AutomationProperties.SetHelpText(box, g.Aumid ?? "");
        // .ntf-ghead: 26 tall (28 dark), padding 0 0 1 4, gap 7: icon, name, count, dismiss-all
        var head = new Grid { MinHeight = _dark ? 28 : 26, Padding = new Thickness(4, 0, 0, 1), ColumnSpacing = 7 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(Icon(g.First, 16, 4, _dark ? 11 : 10));
        var n = Text(name, 12, T("LgInk2"), semibold: true);
        n.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(n, 1); head.Children.Add(n);
        if (g.Items.Count > 1) {
            var c = Text(g.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11, T("LgInk3"));
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(c, 2); head.Children.Add(c);
        }
        var x = XButton("notifs.group." + index + ".x", "Dismiss all from " + (g.App ?? "this app"), () =>
            _ = Act(key, "notifs.clear", new JsonObject { ["aumid"] = g.Aumid }, "Could not dismiss " + (g.App ?? "that app") + "'s notifications"));
        Grid.SetColumn(x, 3); head.Children.Add(x);
        box.Children.Add(head);
        foreach (var t in g.Items) box.Children.Add(Card(t));
        return box;
    }

    /// The group's 16 px icon (.ntf-gicon) or its letter tile (.ntf-letter: radius 4, tile ground, ink-2, 600).
    FrameworkElement Icon(JsonObject t, double size, double radius, double letterFs)
    {
        var host = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var letter = new Border {
            CornerRadius = new CornerRadius(radius), Background = T("LgTile"),
            Child = new TextBlock { Text = NotificationWords.Letter(NotificationCentre.Str(t["app"])), FontFamily = Font, FontSize = letterFs, FontWeight = FontWeights.SemiBold, Foreground = T("LgInk2"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        string? icon = NotificationCentre.Str(t["icon"]);
        if (icon == null) { host.Children.Add(letter); return host; }
        var img = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        host.Children.Add(img);
        _ = Fill(img, icon, () => { host.Children.Clear(); host.Children.Add(letter); });
        return host;
    }

    async Task Fill(Image img, string url, Action? fallback = null)
    {
        var src = await Pic(url, _log);
        if (src != null) img.Source = src; else fallback?.Invoke();
    }

    FrameworkElement Card(JsonObject t)
    {
        long id = NotificationCentre.Id(t) ?? 0;
        bool dead = NotificationCentre.Str(t["opens"]) == "none", busy = _busy.ContainsKey("id:" + id), unread = _wasUnread.Contains(id);
        Brush rest = T(_dark ? "LgCard" : "LgSunk"), over = T(_dark ? "LgPress" : "LgHover");
        var card = new Grid { Padding = new Thickness(9, 8, 4, 8), CornerRadius = new CornerRadius(6), Background = rest, Opacity = busy ? 0.5 : 1, IsHitTestVisible = !busy };
        string tip = NotificationWords.CardTip(t);
        Uia(card, "notifs.card." + id, NotificationWords.Title(t));
        AutomationProperties.SetHelpText(card, tip);
        AutomationProperties.SetItemStatus(card, (unread ? "unread" : "seen") + (dead ? " dead" : ""));
        ToolTipService.SetToolTip(card, tip);
        if (!dead) {
            card.PointerEntered += (_, _) => card.Background = over;
            card.PointerExited += (_, _) => card.Background = rest;
            card.Tapped += (_, e) => { e.Handled = true; OpenToast(t); };
        }
        var col = new StackPanel();
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (NotificationCentre.Str(t["logo"]) is { } logo) {
            bool circle = t["logoCircle"] is JsonValue cv && cv.TryGetValue<bool>(out var cb) && cb;
            var img = new Image { Width = 32, Height = 32, Stretch = Stretch.UniformToFill };
            var frame = new Border { Width = 32, Height = 32, Margin = new Thickness(0, 1, 0, 0), CornerRadius = new CornerRadius(circle ? 16 : 6), Child = img, VerticalAlignment = VerticalAlignment.Top };
            _ = Fill(img, logo, () => frame.Visibility = Visibility.Collapsed);
            row.Children.Add(frame);
        }
        var text = new StackPanel();
        var title = new TextBlock { Text = NotificationWords.Title(t), FontFamily = Font, FontWeight = FontWeights.SemiBold, Foreground = T("LgInk"), TextWrapping = TextWrapping.Wrap };
        Line(title, Fs, 1.35);
        text.Children.Add(title);
        if (NotificationCentre.Str(t["body"]) is { Length: > 0 } body) {
            var b = new TextBlock { Text = body, FontFamily = Font, Foreground = T("LgInk2"), TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            Line(b, Fs, 1.4);
            text.Children.Add(b);
        }
        string meta = NotificationWords.Meta(t, DateTimeOffset.Now);
        if (meta.Length > 0) {
            var m = new TextBlock { Text = meta, FontFamily = Font, FontSize = 11, Foreground = T("LgInk3"), Margin = new Thickness(0, 3, 0, 0) };
            AutomationProperties.SetAutomationId(m, "notifs.card." + id + ".meta");
            text.Children.Add(m);
        }
        Grid.SetColumn(text, 1); row.Children.Add(text);
        var x = XButton("notifs.card." + id + ".x", "Dismiss", () => _ = Act("id:" + id, "notifs.remove", new JsonObject { ["ids"] = new JsonArray((JsonNode)JsonValue.Create(id)) }, "Could not dismiss it"));
        x.Margin = new Thickness(0, -2, 0, 0); x.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(x, 2); row.Children.Add(x);
        col.Children.Add(row);
        if ((NotificationCentre.Str(t["hero"]) ?? NotificationCentre.Str(t["image"])) is { } pic) {
            var img = new Image { Stretch = Stretch.UniformToFill, MaxHeight = 132 };
            var frame = new Border { CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 7, 0, 0), Child = img, MaxHeight = 132 };
            _ = Fill(img, pic, () => frame.Visibility = Visibility.Collapsed);
            col.Children.Add(frame);
        }
        card.Children.Add(col);
        if (unread) {
            // .ntf-card.unread::before: a 4 px accent dot at left 3, top 14
            card.Children.Add(new Ellipse { Width = 4, Height = 4, Fill = T("LgAcc"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3 - 9, 14 - 8, 0, 0), IsHitTestVisible = false });
        }
        return card;
    }

    /// .pop .x: "×" in ink-3, the warn-soft ground and warn ink on hover; the click never reaches the card.
    Button XButton(string id, string tip, Action click)
    {
        var x = new Button { Style = Flat("LgWarnSoft", "LgWarnSoft"), Width = 22, Height = 22, Content = new TextBlock { Text = "×", FontFamily = Font, FontSize = Fs, Foreground = T("LgInk3"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Uia(x, id, tip);
        ToolTipService.SetToolTip(x, tip);
        x.Click += (_, _) => click();
        x.Tapped += (_, e) => e.Handled = true;
        return x;
    }

    /// .qslink: padding 5 7, ink-2, the panel's size; hover the --lg-hover ground.
    Button Link(string id, string text, string tip)
    {
        var b = new Button { Style = Flat(), Padding = new Thickness(7, 5, 7, 5), FontFamily = Font, Content = text };
        Uia(b, id, text);
        AutomationProperties.SetHelpText(b, tip);
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    /// The height the panel needs now (DIP); Electron's cap is 480 (POPOVER_MAX), the list scrolls instead.
    double Need()
    {
        Root.Measure(new Windows.Foundation.Size(WidthDip - 2, double.PositiveInfinity));
        double h = Math.Ceiling(Root.DesiredSize.Height) + 2;
        return h < 60 ? 200 : Math.Min(h, 480);
    }

    /// The panel changes height as toasts come and go: the pop-up is asked for the room again.
    void Fit()
    {
        if (!Open) return;
        double h = Need();
        if (!_room.Resize(h)) _log($"notifs: the panel could not be resized to {h} DIP, so it may be clipped");
    }
}

/// A DispatcherQueue timer that is never left running (the panel's age ticker).
internal sealed class DispatcherTimerLite
{
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _t;
    public DispatcherTimerLite(int ms, Action tick)
    {
        _t = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _t.Interval = TimeSpan.FromMilliseconds(ms);
        _t.IsRepeating = true;
        _t.Tick += (_, _) => tick();
    }
    public void Start() => _t.Start();
    public void Stop() => _t.Stop();
}
