using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.QuickSettings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Bar.QuickSettings;

/// <summary>
/// The panel one level down (ui/bar/qs-more.js): the Wi-Fi tile's "›" (the networks, connect, disconnect, the password
/// field), the Bluetooth tile's "›" (the paired devices, connect, disconnect) and the volume line's "›" (the output
/// devices and one row per app with sound). A view replaces the main one inside the same pop-up, which is re-asked for
/// its height (PopupHost.Resize). Nothing here runs while the panel is closed: each open view has one read loop (the
/// next read is chosen after each reply, so a slow sidecar never stacks reads), stopped by the back arrow or the close.
/// THE PASSWORD is read once on Next, the field is cleared in the same step, and it goes into one qs.wifi.connect call
/// (SurfaceLink copies the arguments, and the panel's copy loses it at once); it is never kept, logged or put in a note.
/// The field takes the keyboard only on the user's ask (Connect on a network that needs a password), through the
/// pop-up framework (Q-W6), and gives it back when it closes. Ids: docs/lanes/quick-settings.md.
/// </summary>
internal sealed partial class QsPanel
{
    /// The pop-up's room: its height re-asked, and the keyboard taken and given back for the password field.
    public sealed record Room(Func<double, bool> Resize, Func<(bool Ok, string Words)> TakeKeys, Action<string> GiveKeys);

    public const double SubHeightDip = QsMoreView.ViewHeightDip + 2 * PadY + 2;

    Room _room = null!;
    Grid _sub = null!;
    TextBlock _vTitle = null!, _vStatus = null!;
    Button _vScan = null!, _vMore = null!;
    StackPanel _vList = null!, _vNotes = null!;
    ScrollViewer _vScroll = null!;
    readonly Dictionary<string, string> _vNote = new(StringComparer.Ordinal);
    string? _view, _moreTarget, _moreWhat;
    int _gen;
    bool _kbd;
    double _fitted;

    // the last reads, so a view reopened draws at once and then refreshes (names, levels and reasons only)
    JsonObject? _wifiData, _devData, _mixData, _btData;

    string? _wOpen, _wPwFor;
    (string Key, string What)? _wBusy;
    readonly Dictionary<string, QsRowFail> _wFail = new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _wAuto = new(StringComparer.Ordinal);
    bool _wScanning;
    string _wSig = "";
    PasswordBox? _pw;
    TextBlock? _pwProblem;

    string? _bOpen;
    (string Id, string What)? _bBusy;
    readonly Dictionary<string, QsRowFail> _bFail = new(StringComparer.Ordinal);
    bool _bWatch;
    int _bTick;
    string _bSig = "";

    string? _aBusyDev, _aDevErr;
    string _aDevSig = "", _aKeys = "";
    int _aTick;
    StackPanel _aDevs = null!, _aMix = null!;
    readonly Dictionary<string, AppRow> _aRows = new(StringComparer.Ordinal);

    sealed class AppRow
    {
        public string Key = ""; public JsonObject S = null!;
        public Slider Range = null!; public TextBlock Name = null!, Val = null!, Err = null!; public Button Mute = null!; public Canvas Glyph = null!;
        public bool Drag, InFlight, Painting; public double? Waiting; public long DragUntil; public string? Error;
    }

    static readonly string[] ChevR = { "c|M6.2 3.6L10.6 8l-4.4 4.4" }, ChevL = { "c|M9.8 3.6L5.4 8l4.4 4.4" };
    static readonly string[] RefreshPaths = { "S|M12.9 8.6a4.9 4.9 0 1 1-1.3-4.2", "S|M12.2 2.2v2.6H9.6" };
    static readonly string[] CheckPaths = { "c|M3.4 8.3l2.9 2.9 6.3-6.5" };
    static readonly string[] LockPaths = { "F|M10.4 9.6h4.2v4h-4.2z", "s|M11.2 9.6V8.6a1.3 1.3 0 0 1 2.6 0v1" };
    static readonly string[] BellPaths = { "s|M8 2.3a3.6 3.6 0 0 0-3.6 3.6v2.5L3.1 10.6h9.8l-1.3-2.2V5.9A3.6 3.6 0 0 0 8 2.3z", "s|M6.6 12.4a1.5 1.5 0 0 0 2.8 0" };
    static readonly string[] PlusPaths = { "S|M8 3.2v9.6M3.2 8h9.6" };
    static readonly Dictionary<string, string[]> BtIcons = new(StringComparer.Ordinal) {
        ["headphones"] = new[] { "s|M2.8 10.2V8.3a5.2 5.2 0 0 1 10.4 0v1.9", "F|M1.9 9.2h3v4.6h-3z", "F|M11.1 9.2h3v4.6h-3z" },
        ["speaker"] = new[] { "s|M3.7 1.8h8.6v12.4h-8.6z", "s|M5.7 9.9a2.3 2.3 0 1 0 4.6 0a2.3 2.3 0 1 0 -4.6 0" },
        ["mouse"] = new[] { "s|M4.3 5.5a3.7 3.7 0 0 1 7.4 0v5a3.7 3.7 0 0 1 -7.4 0z", "s|M8 2.2v3.6" },
        ["keyboard"] = new[] { "s|M1.3 3.9h13.4v8.2h-13.4z", "S|M6.3 9.4h3.4" },
        ["phone"] = new[] { "s|M4.4 1.4h7.2v13.2h-7.2z", "s|M7 12.2h2" },
        ["gamepad"] = new[] { "s|M5.3 4.7h5.4a3.2 3.2 0 0 1 3.1 2.5l.7 3.1a1.6 1.6 0 0 1-2.8 1.4L10.6 10.4H5.4l-1.1 1.3a1.6 1.6 0 0 1-2.8-1.4l.7-3.1a3.2 3.2 0 0 1 3.1-2.5z", "s|M5.2 6.6v2.4M4 7.8h2.4" },
        ["other"] = new[] { "S|M6 4.4L10.2 8 6 11.6M8 2v12l2.2-2.4M8 8L5.2 5.8M8 8l-2.8 2.2" },   // the tile's rune (a copy: partial files have no set order of static init)
    };

    // ------------------------------------------------------------------ the view's frame (header, list, notes, footer)

    Grid BuildSub()
    {
        var g = new Grid { Visibility = Visibility.Collapsed, Height = QsMoreView.ViewHeightDip, Margin = new Thickness(PadX, PadY, PadX, PadY) };
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            g.RowDefinitions.Add(new RowDefinition { Height = h });
        var head = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = Btn("qs.back", "Back to quick settings", 28, 28);
        back.Content = new Canvas { Width = 16, Height = 16 };
        ToolTipService.SetToolTip(back, "Back to quick settings");
        back.Click += (_, _) => Back("the back arrow");
        _vTitle = Text("", 14); _vTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; _vTitle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_vTitle, "qs.view.title");
        _vStatus = Text("", 12.5); _vStatus.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_vStatus, "qs.view.status");
        _vScan = Btn("qs.wifi.scan", "Scan for networks", 28, 28);
        _vScan.Content = new Canvas { Width = 16, Height = 16 };
        ToolTipService.SetToolTip(_vScan, "Scan for networks");
        _vScan.Click += (_, _) => _ = Scan();
        Grid.SetColumn(_vTitle, 1); Grid.SetColumn(_vStatus, 2); Grid.SetColumn(_vScan, 3);
        head.Children.Add(back); head.Children.Add(_vTitle); head.Children.Add(_vStatus); head.Children.Add(_vScan);
        g.Children.Add(head);
        _vList = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(_vList, "qs.view.list");
        _vScroll = new ScrollViewer { Content = _vList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_vScroll, 1);
        g.Children.Add(_vScroll);
        _vNotes = new StackPanel();
        Grid.SetRow(_vNotes, 2);
        g.Children.Add(_vNotes);
        _vMore = Btn("qs.view.more", "More settings", double.NaN, double.NaN);
        _vMore.Padding = new Thickness(7, 5, 7, 5); _vMore.HorizontalAlignment = HorizontalAlignment.Left; _vMore.Margin = new Thickness(0, 6, 0, 0);
        _vMore.Click += (_, _) => { if (_moreTarget != null) OpenSettings(_moreTarget, _moreWhat ?? "settings"); };
        Grid.SetRow(_vMore, 3);
        g.Children.Add(_vMore);
        return g;
    }

    /// A "›" beside a tile or after the mute button.
    Button Chevron(string id, string name)
    {
        var b = Btn(id, name, 28, double.NaN);
        b.VerticalAlignment = VerticalAlignment.Stretch;
        b.Content = new Canvas { Width = 16, Height = 16 };
        ToolTipService.SetToolTip(b, name);
        return b;
    }

    void PaintChevron(Button b, string inkKey) => Fill((Canvas)b.Content, ChevR, inkKey);

    void Enter(string kind)
    {
        if (!Open || _view != null) return;
        _gen++;
        _view = kind;
        _vNote.Clear();
        _stack.Visibility = Visibility.Collapsed;
        _sub.Visibility = Visibility.Visible;
        (_vTitle.Text, _moreTarget, _moreWhat, string more) = kind switch {
            "wifi" => ("Wi-Fi", "ms-settings:network-wifi", "Wi-Fi settings", "More Wi-Fi settings"),
            "bt" => ("Bluetooth", "ms-settings:bluetooth", "Bluetooth settings", "More Bluetooth settings"),
            _ => ("Sound output", "ms-settings:apps-volume", "volume settings", "More volume settings"),
        };
        _vMore.Content = new TextBlock { Text = more, FontSize = 12.5 };
        AutomationProperties.SetName(_vMore, more);
        _vScan.Visibility = kind == "wifi" ? Visibility.Visible : Visibility.Collapsed;
        _vStatus.Text = "";
        PaintFrame();
        _vList.Children.Clear();
        bool roomed = _room.Resize(SubHeightDip);
        _log($"qs: the {_vTitle.Text} view opened" + (roomed ? "" : "; the pop-up could not be re-sized, so the view may be clipped"));
        int g = _gen;
        if (kind == "wifi") { if (_wifiData != null) RenderWifi(true); else StateBlock(_vList, "Looking for networks…", null, null, null, false); _ = WifiOpen(g); }
        else if (kind == "bt") { if (_btData != null) RenderBt(true); else StateBlock(_vList, "Looking for devices…", null, null, null, false); _ = BtOpen(g); }
        else SoundOpen(g);
    }

    void PaintFrame()
    {
        Fill((Canvas)((Button)((Grid)_sub.Children[0]).Children[0]).Content, ChevL, "LgInk");
        Fill((Canvas)_vScan.Content, RefreshPaths, "LgInk");
        _vTitle.Foreground = B("LgInk"); _vStatus.Foreground = B("LgInk3");
        _vMore.Foreground = B("LgInk2");
    }

    /// Back to the main view (the back arrow), or the view left because the pop-up closed (<paramref name="closing"/>).
    void Back(string why, bool closing = false)
    {
        if (_view == null) return;
        _gen++;
        if (_kbd) ReleaseKeys(closing ? "the panel closed" : "the view was left");
        if (_view == "bt") _ = Call("qs.bt.watch", new JsonObject { ["on"] = false });   // always sent: a stop is idempotent
        _log($"qs: the {_vTitle.Text} view closed ({why})");
        _view = null; _wPwFor = null; _pw = null; _pwProblem = null; _wSig = ""; _bSig = ""; _aDevSig = ""; _aKeys = "";
        _aRows.Clear();
        _sub.Visibility = Visibility.Collapsed;
        _stack.Visibility = Visibility.Visible;
        if (closing) return;
        Paint();
        Fit();
    }

    /// The main view's height re-asked from what it draws now (a note that arrives while it is open).
    void Fit()
    {
        if (!Open || _view != null) return;
        double h = Need();
        if (Math.Abs(h - _fitted) < 1) return;
        _fitted = h;
        _room.Resize(h);
    }

    double Need()
    {
        Root.Measure(new Windows.Foundation.Size(WidthDip - 2, double.PositiveInfinity));
        double h = Math.Ceiling(Root.DesiredSize.Height) + 2;
        return h < 120 ? 460 : Math.Min(h, 640);
    }

    bool Alive(int g, string view) => Open && g == _gen && _view == view;

    /// One chain per open view: the next step only after the last one answered.
    async Task Loop(int g, string view, Func<int> ms, Func<Task> step)
    {
        while (true) {
            await Task.Delay(ms());
            if (!Alive(g, view)) return;
            await step();
            if (!Alive(g, view)) return;
        }
    }

    async Task<JsonObject> Call(string name, JsonObject? args = null)
    {
        if (_link == null) return QsParts.Fail("quick settings is still starting");
        var r = await _link.CallAsync(name, args);
        if (!r.Ok) return QsParts.Fail($"{name} was refused: {r.Error}");
        return Obj(r.Result) ?? QsParts.Fail($"{name} gave no answer");
    }

    void Note(string key, string? text)
    {
        if (string.IsNullOrEmpty(text)) _vNote.Remove(key); else _vNote[key] = text!;
        _vNotes.Children.Clear();
        int i = 0;
        foreach (var t in _vNote.Values) {
            var tb = Text(t, 12.5);
            tb.TextWrapping = TextWrapping.Wrap; tb.Foreground = B("LgWarn"); tb.Padding = new Thickness(0, 4, 0, 0);
            Uia(tb, "qs.view.note." + i++, t);
            _vNotes.Children.Add(tb);
        }
    }

    /// A whole-view state: its title, why, and one way forward.
    void StateBlock(Panel into, string title, string? why, string? action, Action? click, bool bad, bool accent = false)
    {
        var box = new StackPanel { Spacing = 4, Padding = new Thickness(4, 18, 4, 18), HorizontalAlignment = HorizontalAlignment.Stretch };
        var t = Text(title, 13); t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; t.HorizontalAlignment = HorizontalAlignment.Center; t.TextWrapping = TextWrapping.Wrap;
        Uia(t, "qs.view.state", title);
        box.Children.Add(t);
        if (why != null) {
            var w = Text(why, 12.5); w.TextWrapping = TextWrapping.Wrap; w.TextAlignment = TextAlignment.Center; w.Foreground = bad ? B("LgWarn") : B("LgInk2");
            Uia(w, "qs.view.why", why);
            box.Children.Add(w);
        }
        if (action != null && click != null) {
            var b = Act("qs.view.action", action, accent, true, null, click);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(b);
        }
        into.Children.Add(box);
    }

    Button Act(string id, string label, bool accent, bool enabled, string? tip, Action click)
    {
        var b = Btn(id, label, double.NaN, 28);
        b.Padding = new Thickness(12, 0, 12, 0);
        b.Content = new TextBlock { Text = label, FontSize = 12.5 };
        b.Background = accent ? B("LgAcc") : B("LgTile");
        b.Foreground = accent ? B("LgAccOn") : B("LgInk");
        b.IsEnabled = enabled;
        b.Opacity = enabled ? 1 : .55;
        if (tip != null) { ToolTipService.SetToolTip(b, tip); AutomationProperties.SetHelpText(b, tip); }
        b.Click += (_, _) => click();
        return b;
    }

    StackPanel Buttons(params Button[] bs)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var b in bs) p.Children.Add(b);
        return p;
    }

    TextBlock Hint(string s, bool warn = false, string? id = null)
    {
        var t = Text(s, 12.5); t.TextWrapping = TextWrapping.Wrap; t.Foreground = warn ? B("LgWarn") : B("LgInk2");
        if (id != null) Uia(t, id, s);
        return t;
    }

    /// A row's head: a glyph, a name over a sub line, and something on the right.
    Button RowHead(string id, string name, string sub, string[] glyph, string? tip, bool on, bool open, UIElement? right = null, string[]? badge = null)
    {
        var b = Btn(id, name, double.NaN, double.NaN);
        b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        b.Padding = new Thickness(8, 6, 8, 6);
        b.Background = on || open ? B("LgTile") : new SolidColorBrush();
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ico = new Canvas { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        Fill(ico, badge == null ? glyph : glyph.Concat(badge).ToArray(), on ? "LgAcc" : "LgInk");
        var txt = new StackPanel();
        var n = Text(name, 12.5); n.TextTrimming = TextTrimming.CharacterEllipsis;
        var s = Text(sub, 12); s.Foreground = B("LgInk3"); s.TextTrimming = TextTrimming.CharacterEllipsis;
        txt.Children.Add(n); txt.Children.Add(s);
        Grid.SetColumn(txt, 1);
        g.Children.Add(ico); g.Children.Add(txt);
        if (right != null) { Grid.SetColumn((FrameworkElement)right, 2); g.Children.Add(right); }
        b.Content = g;
        AutomationProperties.SetHelpText(b, sub);
        AutomationProperties.SetItemStatus(b, (on ? "on" : "off") + (open ? "; open" : ""));
        if (tip != null) { ToolTipService.SetToolTip(b, tip); AutomationProperties.SetFullDescription(b, tip); }
        return b;
    }

    StackPanel Body() => new() { Spacing = 4, Padding = new Thickness(36, 2, 8, 8) };

    void Error(Panel into, string lead, string why, string id)
    {
        var e = new StackPanel { Spacing = 1 };
        var l = Text(lead, 12.5); l.Foreground = B("LgWarn"); l.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; l.TextWrapping = TextWrapping.Wrap;
        var w = Text(why, 12.5); w.Foreground = B("LgWarn"); w.TextWrapping = TextWrapping.Wrap;
        e.Children.Add(l); e.Children.Add(w);
        Uia(e, id, lead + " " + why);
        into.Children.Add(e);
    }

    // ------------------------------------------------------------------ the keyboard (Q-W6)

    void ReleaseKeys(string why)
    {
        _kbd = false;
        _room.GiveKeys(why);
    }

    /// The pop-up lost the keyboard ON ITS OWN (another window took focus): a password box left on screen would look
    /// typeable while every key went elsewhere, so it closes and its row says why and how to get it back.
    public void KeyboardLost(string why)
    {
        _kbd = false;
        if (_view != "wifi" || _wPwFor == null) return;
        string key = _wPwFor;
        _wPwFor = null; _pw = null;
        _wFail[key] = new QsRowFail("kbdlost", "The keyboard went to another window, so the password box closed.",
            $"Nothing typed after that reached this panel ({why}). Press Connect to type the password again.");
        _log("qs: the password box closed: the keyboard went to another window");
        RenderWifi(true);
    }

    // ================================================================== WI-FI

    async Task WifiOpen(int g)
    {
        await ReadWifi(g);
        if (Alive(g, "wifi") && QsMoreView.Usable(_wifiData)) await Scan();
        _ = Loop(g, "wifi", () => QsMoreView.WifiPollMs, () => ReadWifi(g));
    }

    async Task ReadWifi(int g)
    {
        var r = await Call("qs.wifi.list");
        if (!Alive(g, "wifi")) return;
        _wifiData = r;
        RenderWifi(false);
    }

    async Task Scan()
    {
        if (_view != "wifi" || _wScanning) return;
        int g = _gen;
        _wScanning = true;
        PaintWifiStatus();
        var r = await Call("qs.wifi.scan");
        if (!Alive(g, "wifi")) return;
        _wScanning = false;
        var s = r["scan"] as JsonObject;
        Note("scan", !QsMoreView.Usable(r) ? "" : s != null && QsParts.IsFalse(s["ok"]) && QsParts.Str(s["reason"]) is string why ? "Scan: " + why
            : s == null && QsParts.IsFalse(r["ok"]) ? "Scan: " + (QsParts.Str(r["reason"]) ?? "no reason given") : "");
        if (r["networks"] != null || QsParts.IsFalse(r["present"]) || QsParts.IsFalse(r["on"])) _wifiData = r;
        RenderWifi(false);
    }

    void PaintWifiStatus()
    {
        _vStatus.Text = _wScanning ? "Scanning…" : _wBusy is { } b ? (b.What == "connect" ? "Connecting…" : "Disconnecting…") : "";
        _vScan.IsEnabled = !_wScanning;
        _vScan.Opacity = _wScanning ? .55 : 1;
    }

    /// force: redraw even if nothing changed. A poll never redraws over an open password field (it would wipe it).
    void RenderWifi(bool force)
    {
        var r = _wifiData;
        if (_view != "wifi" || r == null) return;
        PaintWifiStatus();
        Note("last", QsMoreView.WifiLastNote(r, _wFail.Count > 0));
        if (_wPwFor != null && !force) return;
        var nets = (r["networks"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        string sig = string.Join("|", new[] { QsParts.Fmt(r["ok"]), QsParts.Fmt(r["present"]), QsParts.Fmt(r["on"]), QsParts.Str(r["reason"]) ?? "", _wOpen ?? "", _wPwFor ?? "", _wBusy?.ToString() ?? "",
            string.Join(",", _wFail.Select(kv => kv.Key + kv.Value.Op)), string.Join(",", _wAuto.Select(kv => kv.Key + kv.Value)), _wScanning.ToString() })
            + string.Join(";", nets.Select(n => string.Join(",", new[] { "hex", "ssid", "signal", "known", "connected", "connecting", "action" }.Select(k => QsParts.Fmt(n[k])))));
        if (!force && sig == _wSig) return;
        _wSig = sig;
        _vList.Children.Clear();
        _pw = null;
        var st = QsMoreView.Wifi(r, _wScanning);
        int g = _gen;
        switch (st.State) {
            case "error": StateBlock(_vList, st.Title!, st.Why, "Try again", () => _ = ReadWifi(g), true); return;
            case "none": StateBlock(_vList, st.Title!, st.Why, "Network settings", () => OpenSettings("ms-settings:network", "Network settings"), false); return;
            case "off": StateBlock(_vList, st.Title!, st.Why, "Turn on Wi-Fi", () => _ = TurnOn("wifi"), false, accent: true); return;
            case "empty": StateBlock(_vList, st.Title!, st.Why, "Scan again", () => _ = Scan(), false); return;
        }
        for (int i = 0; i < nets.Count; i++) _vList.Children.Add(NetRow(nets[i], i));
    }

    UIElement NetRow(JsonObject n, int i)
    {
        string key = QsMoreView.NetKey(n);
        var row = new StackPanel();
        bool open = _wOpen == key, connected = QsParts.IsTrue(n["connected"]);
        string? busy = _wBusy is { } b && b.Key == key ? b.What : null;
        _wFail.TryGetValue(key, out var f);
        string sub = QsMoreView.WifiSub(n, busy, f);
        var head = RowHead("qs.wifi.net." + i, QsMoreView.WifiName(n), sub, WifiPaths(QsMoreView.Bars(QsParts.Num(n["signal"]))), QsMoreView.WifiTip(n), connected, open,
            badge: QsParts.IsTrue(n["secure"]) ? LockPaths : null);
        head.Click += (_, _) => {
            if (_wPwFor != null) CancelPassword();   // leaving a password field, by any row: it closes and the keyboard goes back
            _wOpen = _wOpen == key ? null : key;
            RenderWifi(true);
        };
        row.Children.Add(head);
        if (open) row.Children.Add(NetBody(n, key));
        return row;
    }

    UIElement NetBody(JsonObject n, string key)
    {
        var body = Body();
        bool busy = _wBusy is { } b && b.Key == key, other = _wBusy is { } o && o.Key != key;
        string? otherWhy = other ? "Mantle is busy with another network; wait for it to finish" : null;
        bool connected = QsParts.IsTrue(n["connected"]);
        _wFail.TryGetValue(key, out var f);
        if (f != null && f.Shows(connected)) Error(body, f.Lead, f.Reason, "qs.wifi.error");
        string act = QsParts.Str(n["action"]) ?? "";
        if (act == "disconnect") {
            body.Children.Add(Buttons(Act("qs.wifi.disconnect", busy ? "Disconnecting…" : "Disconnect", false, !busy && !other, otherWhy, () => _ = Disconnect(key))));
        } else if (act is "connect" or "open" or "password") {
            if (act == "open") body.Children.Add(Hint("An open network: other people nearby might be able to see what you send over it."));
            if (act != "connect") {
                var box = new CheckBox { Content = new TextBlock { Text = "Connect automatically", FontSize = 12.5 }, IsChecked = !_wAuto.TryGetValue(key, out var au) || au, MinHeight = 28 };
                Uia(box, "qs.wifi.auto", "Connect automatically");
                box.Checked += (_, _) => _wAuto[key] = true;
                box.Unchecked += (_, _) => _wAuto[key] = false;
                body.Children.Add(box);
            }
            if (_wPwFor == key) body.Children.Add(PasswordForm(n, key, busy));
            else {
                var list = new List<Button>();
                if (f != null && !connected && (act == "connect" || f.Op == "kbd"))
                    list.Add(Act("qs.wifi.windows", "Windows Wi-Fi list", false, true,
                        f.Op == "kbd" ? "Type the password in Windows’ own Wi-Fi list" : "Windows can update a saved password there",
                        () => OpenSettings("ms-availablenetworks:", "the Windows Wi-Fi list")));
                list.Add(Act("qs.wifi.connect", busy ? "Connecting…" : "Connect", true, !busy && !other, otherWhy, () => {
                    if (act == "password") AskPassword(key); else _ = Connect(n, key, null);
                }));
                body.Children.Add(Buttons(list.ToArray()));
            }
        } else {
            body.Children.Add(Hint("Mantle can’t connect this one: " + (QsParts.Str(n["why"]) ?? "Windows handles this kind of network") + "."));
            body.Children.Add(Buttons(Act("qs.wifi.windows", "Open Windows Wi-Fi settings", true, true, null, () => OpenSettings("ms-availablenetworks:", "the Windows Wi-Fi list"))));
        }
        return body;
    }

    /// The field appears only once the pop-up holds the keyboard; without it the keys would go to the app in front.
    void AskPassword(string key)
    {
        _wFail.Remove(key);
        var r = _room.TakeKeys();
        if (!r.Ok) {
            _wFail[key] = new QsRowFail("kbd", "A password can’t be typed here.", r.Words);
            _log("qs: the password field did not open: " + r.Words);
            RenderWifi(true);
            return;
        }
        _kbd = true;
        _wPwFor = key;
        _log("qs: the password field has the keyboard (" + r.Words + ")");
        RenderWifi(true);
    }

    void CancelPassword()
    {
        if (_wPwFor == null) return;
        _wPwFor = null; _pw = null;
        if (_kbd) ReleaseKeys("the password field closed");
    }

    UIElement PasswordForm(JsonObject n, string key, bool busy)
    {
        var form = new StackPanel { Spacing = 4 };
        form.Children.Add(Hint("Enter the network security key"));
        var pw = new PasswordBox { MaxLength = 64, IsEnabled = !busy, FontSize = 12.5, MinHeight = 30 };
        Uia(pw, "qs.wifi.password", "Network security key for " + (QsParts.Str(n["ssid"]) is { Length: > 0 } s ? s : "this network"));
        var problem = Hint("", warn: true, id: "qs.wifi.problem");
        problem.Visibility = Visibility.Collapsed;
        form.Children.Add(pw); form.Children.Add(problem);
        _pw = pw; _pwProblem = problem;
        var cancel = Act("qs.wifi.cancel", "Cancel", false, !busy, null, () => { CancelPassword(); RenderWifi(true); });
        var next = Act("qs.wifi.next", busy ? "Connecting…" : "Next", true, !busy, null, () => Submit(n, key));
        form.Children.Add(Buttons(cancel, next));
        pw.KeyDown += (_, e) => {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Submit(n, key); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; CancelPassword(); RenderWifi(true); }
        };
        pw.Loaded += (_, _) => { if (pw.IsEnabled) pw.Focus(FocusState.Programmatic); };
        return form;
    }

    void Submit(JsonObject n, string key)
    {
        if (_wBusy != null || _pw == null) return;
        string? pw = _pw.Password;
        if (QsMoreView.PasswordProblem(pw) is string bad) {
            pw = null;
            if (_pwProblem != null) { _pwProblem.Text = bad; AutomationProperties.SetName(_pwProblem, bad); _pwProblem.Visibility = Visibility.Visible; }
            _pw.Focus(FocusState.Programmatic);
            return;
        }
        _pw.Password = "";   // cleared in the same step it is read
        _ = Connect(n, key, pw);
        pw = null;
    }

    async Task Connect(JsonObject n, string key, string? password)
    {
        int g = _gen;
        _wBusy = (key, "connect");
        _wFail.Remove(key);
        var args = new JsonObject { ["auto"] = !_wAuto.TryGetValue(key, out var au) || au };
        if (QsParts.Str(n["hex"]) is { Length: > 0 } hex) args["hex"] = hex;
        if (QsParts.Str(n["ssid"]) is { Length: > 0 } ssid) args["ssid"] = ssid;
        if (!string.IsNullOrEmpty(password)) args["password"] = password;
        password = null;
        var sent = Call("qs.wifi.connect", args);   // SurfaceLink copies the arguments before it returns
        args.Remove("password");
        RenderWifi(true);
        var r = await sent;
        if (!Alive(g, "wifi")) return;
        _wBusy = null;
        if (QsParts.IsOk(r)) {
            _wPwFor = null; _wOpen = null;
            if (_kbd) ReleaseKeys("the network was joined");
        } else {
            _wFail[key] = new QsRowFail("connect", QsMoreView.ConnectLead(r), QsParts.Str(r["reason"]) ?? "Mantle gave no reason", QsParts.Str(r["codeHex"]));
            _wOpen = key;   // the password field stays, empty, under the reason
        }
        if (_wifiData?["networks"] is JsonArray nets)
            foreach (var x in nets.OfType<JsonObject>()) if (QsMoreView.NetKey(x) == key) x["connecting"] = false;
        RenderWifi(true);
        _ = ReadWifi(g);
    }

    async Task Disconnect(string key)
    {
        int g = _gen;
        _wBusy = (key, "disconnect");
        RenderWifi(true);
        var r = await Call("qs.wifi.disconnect");
        if (!Alive(g, "wifi")) return;
        _wBusy = null;
        if (QsParts.IsOk(r)) { _wOpen = null; _wFail.Remove(key); }
        else _wFail[key] = new QsRowFail("disconnect", "Couldn’t disconnect.", QsParts.Str(r["reason"]) ?? "Mantle gave no reason");
        RenderWifi(true);
        _ = ReadWifi(g);
    }

    /// "Turn on Wi-Fi" / "Turn on Bluetooth": the main panel's own set (a dry run in --test), then a re-read once the
    /// radio has had a moment to come up.
    async Task TurnOn(string part)
    {
        int g = _gen; string view = _view ?? "";
        Note("radio", "");
        bool ok = await Set(part, JsonValue.Create(true), p => p["on"] = true);
        if (!Alive(g, view)) return;
        if (!ok) { Note("radio", (part == "wifi" ? "Wi-Fi" : "Bluetooth") + " could not be turned on: " + (_note.TryGetValue(part, out var w) ? w : "no reason given")); return; }
        await Task.Delay(900);
        if (!Alive(g, view)) return;
        if (part == "wifi") { await ReadWifi(g); if (Alive(g, view) && QsMoreView.Usable(_wifiData)) await Scan(); }
        else await ReadBt(g);
    }

    // ================================================================== BLUETOOTH

    async Task BtOpen(int g)
    {
        await ReadBt(g);
        if (!Alive(g, "bt")) return;
        await RenewBt(g);
        _bTick = 0;
        _ = Loop(g, "bt", () => _bWatch ? QsMoreView.BtRenewMs : QsMoreView.BtPollMs, async () => {
            _bTick++;
            if (_bWatch) await RenewBt(g);
            else { if (_bTick % (QsMoreView.BtRenewMs / QsMoreView.BtPollMs) == 0) await RenewBt(g); await ReadBt(g); }
        });
    }

    async Task ReadBt(int g)
    {
        var r = await Call("qs.bt.list");
        if (!Alive(g, "bt")) return;
        _btData = r;
        RenderBt(false);
    }

    async Task RenewBt(int g)
    {
        var r = await Call("qs.bt.watch", new JsonObject { ["on"] = true });
        if (!Alive(g, "bt")) return;
        _bWatch = QsParts.IsOk(r) && QsParts.IsTrue(r["watching"]);
        Note("watch", _bWatch ? "" : QsMoreView.BtFallbackNote(QsParts.Str(r["reason"]) ?? "the sidecar did not say why"));
    }

    /// The watch's push: the whole list after a change settled, or word that the watch stopped.
    void OnBtPush(JsonElement e)
    {
        if (_view != "bt" || Obj(e) is not JsonObject ev) return;
        if (ev["devices"] is not JsonArray && !QsParts.IsFalse(ev["ok"])) {
            if (QsParts.IsFalse(ev["watching"])) { _bWatch = false; Note("watch", QsMoreView.BtFallbackNote(QsParts.Str(ev["watchReason"]) ?? "it stopped")); }
            return;
        }
        _btData = ev;
        RenderBt(false);
    }

    void RenderBt(bool force)
    {
        var r = _btData;
        if (_view != "bt" || r == null) return;
        var busy = _bBusy ?? QsMoreView.BtBusyOf(r);
        _vStatus.Text = busy is { } b ? (b.What == "connect" ? "Connecting…" : "Disconnecting…") : "";
        Note("radio", QsMoreView.BtRadioNote(r));
        Note("last", QsMoreView.BtLastNote(r, _bFail));
        var v = QsMoreView.Bt(r);
        var list = v.State == "list" ? (r["devices"] as JsonArray)!.OfType<JsonObject>().ToList() : new List<JsonObject>();
        string sig = v.State + v.Why + (_bOpen ?? "") + busy + string.Join(",", _bFail.Keys)
            + string.Join(";", list.Select(d => string.Join(",", new[] { "id", "name", "kind", "connected", "battery", "batteryWhy", "canConnect", "connectWhy" }.Select(k => QsParts.Fmt(d[k])))));
        if (!force && sig == _bSig) return;
        _bSig = sig;
        _vList.Children.Clear();
        int g = _gen;
        switch (v.State) {
            case "error": StateBlock(_vList, v.Title!, v.Why, "Try again", () => _ = ReadBt(g), true); return;
            case "none": StateBlock(_vList, v.Title!, v.Why, "Bluetooth settings", AddDevice, false); return;
            case "off": StateBlock(_vList, v.Title!, v.Why, "Turn on Bluetooth", () => _ = TurnOn("bluetooth"), false, accent: true); return;
            case "empty": StateBlock(_vList, v.Title!, v.Why, "Add device", AddDevice, false, accent: true); return;
        }
        for (int i = 0; i < list.Count; i++) _vList.Children.Add(BtRow(list[i], i, busy));
        var add = RowHead("qs.bt.add", "Add device", "Pair it in Windows’ Settings", PlusPaths, "Pair a new device in Settings › Bluetooth & devices", false, false);
        add.Click += (_, _) => AddDevice();
        _vList.Children.Add(add);
    }

    void AddDevice() => OpenSettings("ms-settings:bluetooth", "Bluetooth settings");

    UIElement BtRow(JsonObject d, int i, (string Id, string What)? busy)
    {
        var x = QsMoreView.BtDescribe(d, busy?.Id, busy?.What, _bFail);
        var row = new StackPanel();
        UIElement? right = null;
        if (x.Battery is int pct) {
            var t = Text(pct + "%", 12); t.Foreground = B("LgInk2"); t.VerticalAlignment = VerticalAlignment.Center;
            right = t;
        }
        var head = RowHead("qs.bt.dev." + i, x.Name, x.Sub, BtIcons[x.Kind], x.Tip, x.Connected, _bOpen == x.Id, right);
        AutomationProperties.SetItemStatus(head, x.State + (_bOpen == x.Id ? "; open" : ""));
        head.Click += (_, _) => { _bOpen = _bOpen == x.Id ? null : x.Id; RenderBt(true); };
        row.Children.Add(head);
        if (_bOpen == x.Id) {
            var body = Body();
            if (x.FailLead != null) Error(body, x.FailLead, x.FailWhy ?? "no reason given", "qs.bt.error");
            if (x.BatteryWhy != null) body.Children.Add(Hint("Battery: " + QsMoreView.Sentence(x.BatteryWhy)));
            if (x.Action == "settings") {
                body.Children.Add(Hint(QsMoreView.Sentence(x.ActionWhy)));
                body.Children.Add(Buttons(Act("qs.bt.settings", "Open Bluetooth settings", false, true, null, AddDevice)));
            } else {
                bool mine = busy is { } b1 && b1.Id == x.Id, other = busy is { } b2 && b2.Id != x.Id;
                body.Children.Add(Buttons(Act("qs.bt.toggle", x.ActionLabel, x.Action == "connect", !mine && !other,
                    other ? "Mantle is busy with another device; wait for it to finish" : null, () => _ = BtToggle(x.Id, x.Action))));
            }
            row.Children.Add(body);
        }
        return row;
    }

    async Task BtToggle(string id, string what)
    {
        if (_bBusy != null || QsMoreView.BtBusyOf(_btData) != null) return;
        int g = _gen;
        _bBusy = (id, what);
        _bFail.Remove(id);
        RenderBt(true);
        var r = await Call(what == "connect" ? "qs.bt.connect" : "qs.bt.disconnect", new JsonObject { ["device"] = id });
        if (!Alive(g, "bt")) return;
        _bBusy = null;
        if (QsParts.IsOk(r)) _bFail.Remove(id);
        else _bFail[id] = new QsRowFail(what, what == "connect" ? "Couldn’t connect." : "Couldn’t disconnect.", QsParts.Str(r["reason"]) ?? "no reason given");
        // the state comes from the read-back: the sidecar re-read Windows' own flag before answering
        if (r["connected"] is JsonValue cv && cv.TryGetValue<bool>(out var c) && _btData?["devices"] is JsonArray devs)
            foreach (var dv in devs.OfType<JsonObject>()) if (QsParts.Str(dv["id"]) == id) dv["connected"] = c;
        RenderBt(true);
        _ = ReadBt(g);
    }

    // ================================================================== SOUND

    void SoundOpen(int g)
    {
        _aDevs = new StackPanel { Spacing = 2 };
        AutomationProperties.SetAutomationId(_aDevs, "qs.sound.devices");
        var section = Text("Volume mixer", 12.5); section.Foreground = B("LgInk2"); section.Margin = new Thickness(4, 10, 0, 4);
        _aMix = new StackPanel { Spacing = 6 };
        AutomationProperties.SetAutomationId(_aMix, "qs.sound.mixer");
        _vList.Children.Add(_aDevs); _vList.Children.Add(section); _vList.Children.Add(_aMix);
        if (_devData != null) RenderDevices(true); else StateBlock(_aDevs, "Reading playback devices…", null, null, null, false);
        if (_mixData != null) RenderMixer(true);
        _ = ReadDevices(g);
        _ = ReadSessions(g);
        _aTick = 0;
        _ = Loop(g, "sound", () => QsMoreView.SoundPollMs, async () => {
            _aTick++;
            if (_aTick % QsMoreView.DevicesEvery == 0) await Task.WhenAll(ReadSessions(g), ReadDevices(g));
            else await ReadSessions(g);
        });
    }

    async Task ReadDevices(int g)
    {
        var r = await Call("qs.audio.devices");
        if (!Alive(g, "sound")) return;
        _devData = r;
        RenderDevices(false);
    }

    void RenderDevices(bool force)
    {
        var r = _devData;
        if (_view != "sound" || r == null) return;
        var list = (r["devices"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        string sig = QsParts.Fmt(r["ok"]) + QsParts.Str(r["reason"]) + _aBusyDev + _aDevErr + string.Join(";", list.Select(d => QsParts.Fmt(d["id"]) + QsParts.Fmt(d["name"]) + QsParts.Fmt(d["isDefault"]) + QsParts.Fmt(d["roles"])));
        if (!force && sig == _aDevSig) return;
        _aDevSig = sig;
        _aDevs.Children.Clear();
        int g = _gen;
        var st = QsMoreView.Devices(r);
        if (st.State == "error") { StateBlock(_aDevs, st.Title!, st.Why, "Try again", () => _ = ReadDevices(g), true); return; }
        if (st.State == "empty") { StateBlock(_aDevs, st.Title!, st.Why, "Sound settings", () => OpenSettings("ms-settings:sound", "Sound settings"), false); return; }
        for (int i = 0; i < list.Count; i++) {
            var d = list[i];
            string id = QsParts.Str(d["id"]) ?? "";
            bool isDefault = QsParts.IsTrue(d["isDefault"]);
            var (sub, tip) = QsMoreView.Device(d, _aBusyDev == id);
            var b = Btn("qs.sound.dev." + i, QsParts.Str(d["name"]) ?? "A device", double.NaN, 32);
            b.HorizontalAlignment = HorizontalAlignment.Stretch; b.HorizontalContentAlignment = HorizontalAlignment.Stretch; b.Padding = new Thickness(8, 0, 8, 0);
            b.Background = isDefault ? B("LgTile") : new SolidColorBrush();
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ck = new Canvas { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            if (isDefault) Fill(ck, CheckPaths, "LgAcc");
            var name = Text(QsParts.Str(d["name"]) ?? "A device", 12.5); name.TextTrimming = TextTrimming.CharacterEllipsis; name.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(name, 1);
            row.Children.Add(ck); row.Children.Add(name);
            if (sub != null) { var s = Text(sub, 12); s.Foreground = B("LgInk3"); s.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(s, 2); row.Children.Add(s); }
            b.Content = row;
            ToolTipService.SetToolTip(b, tip);
            AutomationProperties.SetFullDescription(b, tip);
            AutomationProperties.SetItemStatus(b, (isDefault ? "default" : "") + (_aBusyDev == id ? (isDefault ? "; " : "") + "switching" : ""));
            b.Click += (_, _) => { if (!isDefault && _aBusyDev == null) _ = SetDefault(id); };   // already the output: a no-op call would make Windows re-announce it
            _aDevs.Children.Add(b);
        }
        if (_aDevErr != null) Error(_aDevs, "The output did not change.", _aDevErr, "qs.sound.error");
    }

    async Task SetDefault(string id)
    {
        int g = _gen;
        _aBusyDev = id; _aDevErr = null;
        RenderDevices(true);
        var r = await Call("qs.audio.default", new JsonObject { ["device"] = id });
        if (!Alive(g, "sound")) return;
        _aBusyDev = null;
        if (r["devices"] is JsonArray { Count: > 0 } devs) _devData = new JsonObject { ["ok"] = true, ["reason"] = null, ["devices"] = devs.DeepClone() };
        _aDevErr = QsParts.IsOk(r) ? null : QsParts.Str(r["reason"]) ?? "no reason given";
        RenderDevices(true);
        if (r["devices"] == null) _ = ReadDevices(g);
        _ = ReadSessions(g);   // the mixer follows the default device
    }

    async Task ReadSessions(int g)
    {
        var r = await Call("qs.audio.sessions");
        if (!Alive(g, "sound")) return;
        _mixData = r;
        RenderMixer(false);
    }

    /// The rows are rebuilt only when the set of apps changes; otherwise each is updated in place, so a slider being
    /// dragged is never replaced under the pointer.
    void RenderMixer(bool force)
    {
        var r = _mixData;
        if (_view != "sound" || r == null) return;
        var ss = (r["sessions"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        string keys = (QsParts.IsFalse(r["ok"]) ? "x:" + QsParts.Str(r["reason"]) : "") + "|" + string.Join("|", ss.Select(s => QsParts.Str(s["key"])));
        if (force || keys != _aKeys) {
            _aKeys = keys;
            _aRows.Clear();
            _aMix.Children.Clear();
            int g = _gen;
            var st = QsMoreView.Mixer(r);
            if (st.State == "error") { StateBlock(_aMix, st.Title!, st.Why, "Try again", () => _ = ReadSessions(g), true); return; }
            if (st.State == "empty") { StateBlock(_aMix, st.Title!, st.Why, null, null, false); return; }
            for (int i = 0; i < ss.Count; i++) { var row = AppRowFor(ss[i], i); _aRows[row.Key] = row; }
        }
        foreach (var s in ss) if (QsParts.Str(s["key"]) is string k && _aRows.TryGetValue(k, out var row)) PaintApp(row, s);
    }

    AppRow AppRowFor(JsonObject s, int i)
    {
        var row = new AppRow { Key = QsParts.Str(s["key"]) ?? "", S = s };
        var wrap = new StackPanel { Spacing = 2 };
        Uia(wrap, "qs.app." + i, QsMoreView.AppName(s));
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        UIElement ico;
        if (QsParts.IsTrue(s["system"])) { var c = new Canvas { Width = 16, Height = 16 }; Fill(c, BellPaths, "LgInk2"); ico = c; ToolTipService.SetToolTip(c, "Windows system sounds"); }
        else {
            // icons are not read by this build yet (qs.audio.sessions' iconReason): a letter, which says why on hover
            var l = Text(QsMoreView.AppLetter(s), 12); l.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; l.HorizontalAlignment = HorizontalAlignment.Center;
            ToolTipService.SetToolTip(l, QsParts.Str(s["iconReason"]) ?? "no icon was given");
            ico = l;
        }
        row.Name = Text("", 12.5); row.Name.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Val = Text("", 12.5); row.Val.Foreground = B("LgInk3");
        AutomationProperties.SetAutomationId(row.Val, "qs.app." + i + ".value");
        Grid.SetColumn(row.Name, 1); Grid.SetColumn(row.Val, 2);
        top.Children.Add(ico); top.Children.Add(row.Name); top.Children.Add(row.Val);
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Range = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, SmallChange = 1, LargeChange = 10, IsThumbToolTipEnabled = false, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        Uia(row.Range, "qs.app." + i + ".volume", "Volume for " + QsMoreView.AppName(s));
        Accent(row.Range);
        row.Mute = Btn("qs.app." + i + ".mute", "Mute", 28, 28);
        row.Glyph = new Canvas { Width = 16, Height = 16 };
        row.Mute.Content = row.Glyph; row.Mute.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(row.Mute, 1);
        line.Children.Add(row.Range); line.Children.Add(row.Mute);
        row.Err = Hint("", warn: true); row.Err.Visibility = Visibility.Collapsed;
        wrap.Children.Add(top); wrap.Children.Add(line); wrap.Children.Add(row.Err);
        _aMix.Children.Add(wrap);
        row.Range.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => row.Drag = true), true);
        row.Range.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => row.Drag = false), true);
        row.Range.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => row.Drag = false), true);
        row.Range.ValueChanged += (_, e) => {
            if (row.Painting) return;
            int v = (int)Math.Round(e.NewValue);
            row.DragUntil = Environment.TickCount64 + 700;
            row.Val.Text = v + "%";
            PushApp(row, v);
        };
        row.Mute.Click += (_, _) => _ = SetApp(row, null, !QsParts.IsTrue(row.S["muted"]));
        return row;
    }

    void PaintApp(AppRow row, JsonObject s)
    {
        row.S = s;
        row.Name.Text = QsMoreView.AppName(s);
        ToolTipService.SetToolTip(row.Name, QsMoreView.AppTip(s));
        row.Name.Foreground = QsParts.IsTrue(s["active"]) ? B("LgInk") : B("LgInk3");
        bool dragging = row.Drag || Environment.TickCount64 < row.DragUntil || row.InFlight;
        if (!dragging && QsParts.Num(s["level"]) is double lv) {
            row.Painting = true;
            try { row.Range.Value = Math.Round(lv); } finally { row.Painting = false; }
            row.Val.Text = Math.Round(lv) + "%";
        }
        bool muted = QsParts.IsTrue(s["muted"]);
        double l = QsParts.Num(s["level"]) ?? 0;
        Fill(row.Glyph, VolumePaths(muted || l <= 0.5 ? 0 : l <= 33 ? 1 : l <= 66 ? 2 : 3, muted), muted ? "LgAccOn" : "LgInk");
        row.Mute.Background = muted ? B("LgAcc") : B("LgTile");
        string tip = QsMoreView.MuteTip(s);
        ToolTipService.SetToolTip(row.Mute, tip);
        AutomationProperties.SetName(row.Mute, tip);
        AutomationProperties.SetItemStatus(row.Mute, muted ? "muted" : "unmuted");
        AutomationProperties.SetItemStatus(row.Range, row.Val.Text);
        row.Err.Text = row.Error ?? "";
        row.Err.Visibility = row.Error != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// qs-send.js: one set in flight, only the newest level waiting, the level the pointer stops on always sent.
    void PushApp(AppRow row, double v)
    {
        if (row.InFlight) { row.Waiting = v; return; }
        row.InFlight = true;
        _ = SendApp(row, v);
    }

    async Task SendApp(AppRow row, double v)
    {
        double? next = v;
        while (next is double n) {
            row.Waiting = null;
            await SetApp(row, n, null);
            next = row.Waiting;
        }
        row.InFlight = false;
        if (_view == "sound") PaintApp(row, row.S);
    }

    async Task SetApp(AppRow row, double? level, bool? muted)
    {
        int g = _gen;
        var args = new JsonObject { ["key"] = row.Key };
        if (level is double l) args["level"] = (int)l;
        if (muted is bool m) { args["muted"] = m; row.Mute.Opacity = .6; }
        var r = await Call("qs.audio.session", args);
        if (!Alive(g, "sound")) return;
        row.Mute.Opacity = 1;
        bool dry = QsParts.IsTrue(r["dryRun"]);
        row.Error = QsParts.IsOk(r) ? (dry ? "A test run: " + (QsParts.Str(r["reason"]) ?? "nothing was sent") : null) : QsParts.Str(r["reason"]) ?? "no reason given";
        if (r["session"] is JsonObject got) { if (level != null) row.DragUntil = 0; PaintApp(row, got); }
        else {
            if (dry && muted is bool mm) { var guess = (JsonObject)row.S.DeepClone(); guess["muted"] = mm; PaintApp(row, guess); }   // a test panel moves; Windows did not
            else PaintApp(row, row.S);
            if (!QsParts.IsOk(r)) _ = ReadSessions(g);
        }
    }
}
