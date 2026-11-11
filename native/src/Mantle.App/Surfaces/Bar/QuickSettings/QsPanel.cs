using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Features.PowerMode;
using Mantle.Core.Features.QuickSettings;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App.Surfaces.Bar.QuickSettings;

/// <summary>
/// The bar's quick-settings panel (bar.js buildQsPop, bar.css .qs*, ui/bar/powermode.js), part 1: the Wi-Fi, Bluetooth,
/// Night light and Energy saver tiles, the brightness and volume sliders with mute, the notes, the power mode row and the
/// footer's battery line. It reads and changes everything through the host's commands (qs.get, qs.set, qs.watch,
/// powermode.get, powermode.set) over a <see cref="SurfaceLink"/> (caller "bar"), and hears the "qs" and
/// "powermode.state" events. A click changes the tile at once (pending) and the read-back has the last word; a switch
/// that did not take springs back with its reason as a note. UI thread only. Ids: docs/lanes/quick-settings.md.
/// The three "›" views (Wi-Fi networks + password, Bluetooth devices, sound output + per-app rows): QsPanel.More.cs.
/// </summary>
internal sealed partial class QsPanel
{
    /// WidthDip: the views' width (and the panel's when nothing could be measured). The main view is sized to its
    /// content as Electron's .pop is (min-width 268, max-width 360 once the Wi-Fi tile is split, qs-more.css).
    public const double WidthDip = 340, MinWidthDip = 268, MaxWidthDip = 360, PadX = 12, PadY = 11;
    const double RCtl = 6, ChevW = 22;   // --lg-r-ctl (both themes); qs-more.css .qsx-chev flex-basis
    double _width = WidthDip;
    /// The pop-up's width now (DIP), measured with its height before each open.
    public double Width => _width;
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    readonly Action<string> _log;
    readonly bool _test;
    SurfaceLink? _link;
    readonly List<IDisposable> _subs = new();
    readonly Dictionary<string, JsonObject> _parts = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _busy = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> _note = new(StringComparer.Ordinal);
    JsonObject? _pm;
    string? _pmPending, _pmErr;
    bool _dark, _painting;
    readonly HashSet<string> _missing = new();

    public readonly Grid Root;
    readonly StackPanel _stack, _notes;
    readonly Dictionary<string, Tile> _tiles = new(StringComparer.Ordinal);
    readonly SliderRow _bright, _vol;
    readonly Button _mute, _soundMore;
    readonly Canvas _muteGlyph = new() { Width = 16, Height = 16 }, _gearGlyph = new() { Width = 16, Height = 16 };
    readonly Grid _pmSeg;
    readonly StackPanel _pmRow;
    readonly TextBlock _pmHead, _pmSub, _pmWhy, _pmNote;
    readonly Grid _pmHeadRow;
    readonly Dictionary<string, Button> _pmBtns = new(StringComparer.Ordinal);
    readonly TextBlock _power;
    readonly Button _toWin, _gear;
    readonly Border _foot;
    readonly Mantle.App.Surfaces.Bar.GameMode.GameModeRow _gm;   // the Game mode row (lane native-r7-media3)

    sealed class Tile { public Button B = null!; public Button? Chev; public Canvas Ico = null!; public TextBlock Lbl = null!, Sub = null!; public QsTileView? View; }
    sealed class SliderRow
    {
        public StackPanel Wrap = null!; public TextBlock Name = null!, Val = null!; public Slider Range = null!; public Grid Line = null!, Head = null!; public Border Track = null!;
        public bool Dragging; public bool InFlight; public double? Waiting;
    }

    public QsPanel(Action<string> log, bool test, Room room)
    {
        _log = log; _test = test; _room = room;
        _stack = new StackPanel { Padding = new Thickness(PadX, PadY, PadX, PadY) };
        Root = new Grid { Width = WidthDip - 2 };
        Root.Children.Add(_stack);
        _sub = BuildSub();
        Root.Children.Add(_sub);
        Uia(Root, "qs.panel", "Quick settings");

        // ---- the tile grid: 2 columns, gap 6, 10 below (.qsgrid)
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int i = 0;
        foreach (var (key, label) in new[] { ("wifi", "Wi-Fi"), ("bluetooth", "Bluetooth"), ("nightlight", "Night light"), ("saver", "Energy saver") }) {
            var t = MakeTile(key, label);
            FrameworkElement cell = t.B;
            if (key is "wifi" or "bluetooth") {
                // Windows 11's split tile: the tile switches the radio, its "›" always opens the list (BAR-608, BAR-630).
                // qs-more.css .qsx-split / .qsx-chev: no gap, the tile's right corners square, a 22 px "›" with a 1 px left
                // edge and its right corners round (--lg-r-ctl)
                var split = new Grid { ColumnSpacing = 0 };
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                t.Chev = Chevron("qs.more." + key, key == "wifi" ? "Show Wi-Fi networks" : "Show Bluetooth devices");
                t.Chev.Width = ChevW;
                t.Chev.BorderThickness = new Thickness(1, 0, 0, 0);
                t.Chev.CornerRadius = new CornerRadius(0, RCtl, RCtl, 0);
                t.B.CornerRadius = new CornerRadius(RCtl, 0, 0, RCtl);
                Grid.SetColumn(t.Chev, 1);
                string view = key == "wifi" ? "wifi" : "bt";
                t.Chev.Click += (_, _) => Enter(view);
                split.Children.Add(t.B); split.Children.Add(t.Chev);
                cell = split;
            }
            Grid.SetColumn(cell, i % 2); Grid.SetRow(cell, i / 2);
            grid.Children.Add(cell);
            _tiles[key] = t; i++;
        }
        BuildTiles(grid, i);   // the second row: Airplane mode, Do not disturb, Focus, Project, Cast (QsPanel.Tiles.cs)
        _stack.Children.Add(grid);
        _stack.Children.Add(_xNotes);
        _tiles["wifi"].B.Click += (_, _) => Toggle("wifi");
        _tiles["bluetooth"].B.Click += (_, _) => BluetoothClick();
        _tiles["nightlight"].B.Click += (_, _) => Toggle("nightlight");
        _tiles["saver"].B.Click += (_, _) => OpenSettings("ms-settings:batterysaver", "energy saver settings");

        // ---- sliders (.qsrow), the volume line with its mute button (.qsmute)
        _bright = MakeSlider("brightness", "Brightness");
        _vol = MakeSlider("volume", "Volume");
        _mute = Btn("qs.mute", "Mute", 28, 28);
        _mute.Content = _muteGlyph;
        _mute.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_mute, 1);
        _vol.Line.Children.Add(_mute);
        _vol.Line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _soundMore = Chevron("qs.more.sound", "Sound output and volume mixer");
        _soundMore.Height = 28; _soundMore.Margin = new Thickness(8, 0, 0, 0);   // a .qsmute square in .qsline (gap 8)
        Grid.SetColumn(_soundMore, 2);
        _soundMore.Click += (_, _) => Enter("sound");
        _vol.Line.Children.Add(_soundMore);
        _mute.Click += (_, _) => {
            if (_busy.ContainsKey("mute")) return;
            bool want = !QsParts.IsTrue(Part("volume")?["muted"]);
            _ = Set("mute", JsonValue.Create(want), p => p["muted"] = want);
        };

        // ---- game mode (ui/bar/gamemode.js, attached before .qsnotes as bar.js does): its own row (Surfaces/Bar/GameMode)
        _gm = new Mantle.App.Surfaces.Bar.GameMode.GameModeRow(B, Font(), log, Fit); _stack.Children.Add(_gm.Root);

        // ---- notes (.qsnotes): one line per part that is not ok, and each set that did not take
        _notes = new StackPanel();
        Uia(_notes, "qs.notes", "Notes");
        _stack.Children.Add(_notes);

        // ---- power mode (ui/bar/powermode.js: .pmrow / .pmseg / .pmbtn; its top margin is 2 and collapses into the
        // volume row's 9 unless a note sits between them, PaintNotes)
        _pmRow = new StackPanel { Margin = new Thickness(0, 0, 0, 9) };
        var pmHead = _pmHeadRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        _pmHead = Text("Power mode", 12.5); _pmSub = Text("", 12.5);
        _pmSub.HorizontalAlignment = HorizontalAlignment.Right; _pmSub.TextTrimming = TextTrimming.CharacterEllipsis;
        pmHead.Children.Add(_pmHead); pmHead.Children.Add(_pmSub);
        _pmRow.Children.Add(pmHead);
        _pmSeg = new Grid { ColumnSpacing = 2, Padding = new Thickness(2), CornerRadius = new CornerRadius(RCtl) };
        int c = 0;
        foreach (var (mode, label, _) in PowerModeView.Modes) {
            _pmSeg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var b = Btn("qs.powermode." + mode, label, double.NaN, 26);
            b.CornerRadius = new CornerRadius(5);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Padding = new Thickness(4, 0, 4, 0);
            b.Content = new TextBlock { Text = label, FontSize = 12, FontFamily = Font(), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(b, c++);
            string m = mode;
            b.Click += (_, _) => _ = PowerMode(m);
            _pmBtns[mode] = b;
            _pmSeg.Children.Add(b);
        }
        Uia(_pmSeg, "qs.powermode", "Power mode");
        _pmRow.Children.Add(_pmSeg);
        _pmWhy = Text("", 12.5); _pmWhy.Margin = new Thickness(0, 5, 0, 0); _pmWhy.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(_pmWhy, "qs.powermode.why");
        _pmRow.Children.Add(_pmWhy);
        _pmNote = Text("", 12.5); _pmNote.Margin = new Thickness(0, 5, 0, 0); _pmNote.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetAutomationId(_pmNote, "qs.powermode.note");
        _pmRow.Children.Add(_pmNote);
        _stack.Children.Add(_pmRow);

        // ---- footer (.qsfoot): the battery line, "Windows quick settings", the gear
        var foot = new Grid { ColumnSpacing = 8 };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _power = Text("Battery state unknown", 12.5);
        _power.TextTrimming = TextTrimming.CharacterEllipsis; _power.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_power, "qs.power");
        foot.Children.Add(_power);
        var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _toWin = Btn("qs.windows", "Windows quick settings", double.NaN, double.NaN);
        _toWin.Padding = new Thickness(7, 5, 7, 5);
        _toWin.Content = Text("Windows quick settings", 12.5);
        ToolTipService.SetToolTip(_toWin, "Open Windows' own quick settings (Win+A)");
        _toWin.Click += (_, _) => OpenSettings("ms-actioncenter:controlcenter/&showFooter=true", "Windows quick settings");
        _gear = Btn("qs.gear", "Open Windows Settings", 26, 26);
        _gear.Content = _gearGlyph;
        ToolTipService.SetToolTip(_gear, "Open Windows Settings");
        _gear.Click += (_, _) => OpenSettings("ms-settings:", "settings");
        acts.Children.Add(_toWin); acts.Children.Add(_gear);
        Grid.SetColumn(acts, 1);
        foot.Children.Add(acts);
        _foot = new Border { Child = foot, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 10 - 9, 0, 0), Padding = new Thickness(0, 9, 0, 0) };
        _stack.Children.Add(_foot);
    }

    /// From the host (App.xaml.cs, block QuickSettingsWiring), on the UI thread: the road to the qs.* commands.
    public void Attach(SurfaceLink link)
    {
        _link = link;
        _subs.Add(link.On<QsSnapshot>(QsService.Event, s => { Take(s.Body); if (Open) Paint(); }));
        _subs.Add(link.On<JsonElement>("powermode.state", e => { _pm = Obj(e); if (Open) Paint(); }));
        _subs.Add(link.On<JsonElement>(QsService.BtEvent, OnBtPush));
        _subs.Add(link.On<JsonElement>(QsService.TilesEvent, e => { if (Obj(e) is JsonObject s) { _xState = s; if (Open) Paint(); } }));
        _gm.Attach(link);
        _ = TilesRefresh();  // warmed at start (lib/features/qstiles.js reads 3 s after the helper is up)
        _ = Snapshot();      // warmed at start, so the first open draws real values in its first frame
        _ = ReadPowerMode();
    }

    /// Set by the bar while the pop-up is open.
    public bool Open { get; set; }

    /// Before the pop-up opens: repaint for the bar's theme and measure the height the panel needs (DIP).
    public double Prepare(bool dark)
    {
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _gm.Theme(dark);
        Paint();
        return _fitted = Need();
    }

    public void Opened()
    {
        Open = true;
        Paint();
        _ = Snapshot();
        _ = ReadPowerMode();
        TilesOpened();
        _ = _gm.Opened(_dark);
        if (_link != null) _ = _link.CallAsync("qs.watch", new JsonObject { ["on"] = true });
    }

    public void Closed()
    {
        Open = false;
        TilesClosed();
        Back("the panel closed", closing: true);
        if (_link != null) _ = _link.CallAsync("qs.watch", new JsonObject { ["on"] = false });
    }

    // ------------------------------------------------------------------ data

    async System.Threading.Tasks.Task Snapshot()
    {
        if (_link == null) return;
        var r = await _link.CallAsync("qs.get");
        if (r.Ok) Take(r.Result);
        else _note["qs"] = "Quick Settings not reading: the settings could not be read, so the panel may show old values: " + r.Error;
        if (Open) Paint();
    }

    async System.Threading.Tasks.Task ReadPowerMode()
    {
        if (_link == null) return;
        var r = await _link.CallAsync("powermode.get");
        _pm = r.Ok ? Obj(r.Result) : new JsonObject { ["ok"] = false, ["reason"] = r.Error };
        if (Open) Paint();
    }

    void Take(JsonElement snap)
    {
        if (snap.ValueKind != JsonValueKind.Object) return;
        _note.Remove("qs");
        foreach (var p in QsParts.All) {
            if (!snap.TryGetProperty(p.Key, out var v) || Obj(v) is not JsonObject o) continue;
            if (_busy.ContainsKey(p.Key) || (p.Key == "volume" && _busy.ContainsKey("mute"))) continue;   // a set in flight: its read-back decides
            if (Held(p.Key) && Part(p.Key)?["level"] is JsonNode lv) o["level"] = lv.DeepClone();         // a held slider keeps the pointer's value
            _parts[p.Key] = o;
        }
    }

    JsonObject? Part(string k) => _parts.TryGetValue(k, out var v) ? v : null;
    bool Held(string part) => part == "volume" ? _vol.Dragging || _vol.InFlight : part == "brightness" && (_bright.Dragging || _bright.InFlight);

    /// qsSet: the guess drawn at once (pending), the read-back has the last word, a refusal springs back and says why.
    async System.Threading.Tasks.Task<bool> Set(string part, JsonNode value, Action<JsonObject>? guess)
    {
        string key = part == "mute" ? "volume" : part;
        if (guess != null && Part(key) is JsonObject cur) { var g = (JsonObject)cur.DeepClone(); guess(g); _parts[key] = g; }
        _busy[part] = (_busy.TryGetValue(part, out var n) ? n : 0) + 1;
        _note.Remove(key);
        Paint();
        if (_link == null) { _busy.Remove(part); _note[key] = QsView.SetNote(part, "Quick settings is still starting"); Paint(); return false; }
        var r = await _link.CallAsync("qs.set", new JsonObject { ["part"] = part, ["value"] = value.DeepClone() });
        if (--_busy[part] <= 0) _busy.Remove(part);
        if (r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("state", out var st) && Obj(st) is JsonObject s) _parts[key] = s;
        if (!r.Ok) {
            _note[key] = QsView.SetNote(part, r.Error);
            _ = Snapshot();   // what the system says, again: the guess is undone
        }
        Paint();
        return r.Ok;
    }

    void Toggle(string key)
    {
        var p = Part(key);
        if (p == null) { _note[key] = $"{Label(key)} is still being read, so it cannot be switched yet"; Paint(); _ = Snapshot(); return; }
        if (!QsParts.IsOk(p) || QsParts.IsFalse(p["present"]) || _busy.ContainsKey(key)) return;
        bool want = !QsParts.IsTrue(p["on"]);
        _ = Set(key, JsonValue.Create(want), g => { g["on"] = want; if (key == "wifi" && !want) g["connected"] = false; });
    }

    void BluetoothClick()
    {
        var p = Part("bluetooth");
        var t = QsView.Bluetooth(p);
        if (!t.Live && t.Opens != null) { OpenSettings(t.Opens, t.OpensWhat ?? "Bluetooth settings"); return; }
        if (p != null && !t.Live && !_busy.ContainsKey("bluetooth")) { _note["bluetooth"] = t.Tip.Split('\n')[0]; Paint(); return; }
        Toggle("bluetooth");
    }

    async System.Threading.Tasks.Task PowerMode(string mode)
    {
        if (_link == null || _pmPending != null) return;
        var pm = _pm;
        // powermode.js: no click while unread, unavailable or locked (settable false; absent is settable)
        if (pm == null || !QsParts.IsOk(pm) || QsParts.IsFalse(pm["available"]) || QsParts.IsFalse(pm["settable"]) || QsParts.Str(pm["mode"]) == mode) return;
        _pmPending = mode; _pmErr = null; Paint();
        var r = await _link.CallAsync("powermode.set", new JsonObject { ["mode"] = mode });
        _pmPending = null;
        var res = r.Ok ? Obj(r.Result) : null;
        if (res?["state"] is JsonObject st) _pm = st;
        if (res == null || !QsParts.IsOk(res))
            _pmErr = QsParts.Str(res?["reason"]) ?? r.Error ?? "";
        Paint();
    }

    /// The panel's ways out to Windows' own pages. A --test run opens nothing on the runner: it says what it would open.
    void OpenSettings(string target, string what)
    {
        if (_test) {
            _log($"qs: dry run (--test): would open {what} ({target}); nothing was opened");
            _note["open"] = $"A test run: {what} would open here";
            Paint();
            return;
        }
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            _log($"qs: opened {what} ({target})");
        } catch (Exception e) {
            _note["open"] = $"{char.ToUpperInvariant(what[0])}{what[1..]} did not open: {e.Message}";
            _log($"qs: {what} did not open ({target}): {e.Message}");
            Paint();
        }
    }

    // ------------------------------------------------------------------ paint

    void Paint()
    {
        _painting = true;
        try {
            PaintTile(QsView.Wifi(Part("wifi")), WifiPaths(QsView.Wifi(Part("wifi")).Bars));
            var bt = QsView.Bluetooth(Part("bluetooth"));
            PaintTile(bt, QsParts.IsFalse(Part("bluetooth")?["present"]) ? NoBtPaths : BtPaths);
            PaintTile(QsView.NightLight(Part("nightlight")), MoonPaths);
            PaintTile(QsView.Saver(Part("power")), SaverPaths);
            PaintTiles();
            PaintSlider(_bright, QsView.Slider(Part("brightness"), true), "brightness");
            PaintSlider(_vol, QsView.Slider(Part("volume"), false), "volume");
            var v = Part("volume");
            bool muted = QsParts.IsTrue(v?["muted"]);
            double lvl = muted ? 0 : QsParts.Num(v?["level"]) ?? 0;
            int waves = muted ? 0 : lvl <= 0.005 ? 0 : lvl <= 0.33 ? 1 : lvl <= 0.66 ? 2 : 3;   // bar.js volumeSvg (0-100 levels: 3 waves at any level)
            Fill(_muteGlyph, VolumePaths(waves, muted), muted ? "LgAccOn" : "LgInk");
            _mute.Background = muted ? B("LgAcc") : B("LgTile");
            _mute.Opacity = _busy.ContainsKey("mute") ? .6 : 1;
            AutomationProperties.SetName(_mute, muted ? "Unmute" : "Mute");
            AutomationProperties.SetItemStatus(_mute, muted ? "muted" : "unmuted");
            ToolTipService.SetToolTip(_mute, muted ? "Unmute" : "Mute");
            PaintPower();
            var bd = QsBattery.Describe(Part("power"));
            _power.Text = bd.Footer;
            _power.Foreground = B("LgInk2");
            Type(_power, Fs, 1.35); Type((TextBlock)_toWin.Content, Fs, 1);
            _power.Margin = new Thickness(0, -1, 0, 1);   // a BlockLineHeight line sets its text ~1 px lower than CSS centres it
            ToolTipService.SetToolTip(_power, bd.Tip);
            _foot.BorderBrush = B("LgEdgeOut");
            var notes = new List<string>(_note.Values);
            PaintNotes(QsView.Problems(_parts, notes));
            foreach (var t in new[] { _toWin, _gear }) t.Foreground = B("LgInk2");
            ((TextBlock)_toWin.Content).Foreground = B("LgInk2");   // its own TextBlock (Text() inks it at build time)
            Fill(_gearGlyph, GearPaths, "LgInk2");
            _soundMore.Background = B("LgTile");
            PaintChevron(_soundMore, "LgInk");
            PaintFrame();
        } finally { _painting = false; }
    }

    void PaintTile(QsTileView v, string[] paths, bool pending = false)
    {
        var t = _tiles[v.Key];
        t.View = v;
        pending = pending || _busy.ContainsKey(v.Key);
        // dark (bar.css, Helm's look): a dead tile is a card, not the input well
        t.B.Background = v.On ? B("LgAcc") : v.Dead ? (_dark ? B("LgCard") : B("LgSunk")) : B("LgTile");
        string inkKey = v.On ? "LgAccOn" : v.Dead ? "LgInk3" : "LgInk";
        var ink = B(inkKey);
        t.Lbl.Foreground = ink;
        Type(t.Lbl, Fs, 1.1); Type(t.Sub, SubFs, 1.2);
        t.Sub.Text = v.Sub;
        t.Sub.Foreground = v.On ? B("LgAccOn") : B("LgInk3");
        t.Sub.Opacity = v.On ? .82 : 1;
        Fill(t.Ico, paths, inkKey);
        if (t.Chev != null) {
            // .qsx-chev wears its tile's state: accent when on, sunk when dead; its left edge a hairline
            t.Chev.Background = v.On ? B("LgAcc") : v.Dead ? B("LgSunk") : B("LgTile");
            t.Chev.BorderBrush = v.On ? Alpha(B("LgAccOn"), .3) : _dark ? B("LgDivider") : B("LgEdgeOut");
            PaintChevron(t.Chev, inkKey);
        }
        t.Ico.Opacity = v.Dead ? (_dark ? .45 : .55) : 1;
        t.B.Opacity = pending ? .6 : 1;
        ToolTipService.SetToolTip(t.B, v.Tip);
        AutomationProperties.SetHelpText(t.B, v.Sub);
        AutomationProperties.SetFullDescription(t.B, v.Tip);
        AutomationProperties.SetItemStatus(t.B, (v.On ? "on" : "off") + (v.Dead ? "; dead" : "") + (pending ? "; pending" : ""));
    }

    void PaintSlider(SliderRow s, QsSliderView v, string part)
    {
        bool held = Held(part);
        s.Range.IsEnabled = v.Live;
        s.Wrap.Opacity = v.Live ? 1 : _dark ? .45 : .55;
        Type(s.Name, Fs, 1); Type(s.Val, Fs, 1);
        s.Head.Margin = new Thickness(0, 0, 0, HeadGap(s == _bright ? 0 : 1));
        // Chromium's own range track (not a token in Electron either): light #efefef ringed #b2b2b2, dark #3b3b3b
        // ringed #858585; drawn from the nearest tokens
        s.Track.Background = _dark ? B("LgEdgeOut") : B("LgFill0");
        s.Track.BorderBrush = _dark ? Alpha(B("LgInk2"), .7) : Alpha(B("LgInk3"), .45);
        if (!held && v.Level is double l) s.Range.Value = Math.Round(l);
        if (!held || !v.Live) s.Val.Text = v.Value;
        s.Name.Foreground = B("LgInk2"); s.Val.Foreground = B("LgInk3");
        ToolTipService.SetToolTip(s.Wrap, v.Live ? null : v.Why);
        AutomationProperties.SetHelpText(s.Range, v.Live ? "" : v.Why);
        AutomationProperties.SetItemStatus(s.Range, v.Value);
    }

    /// powermode.js paint() from its pure view() (PowerModeView); light is the accent block, dark is Helm's .seg
    /// (a --lg-field recess with a hairline, 12/500 ink-2 segments, the current one --lg-press with a hairline).
    void PaintPower()
    {
        var v = PowerModeView.View(_pm, _pmPending, _pmErr);
        Type(_pmHead, Fs, 1); Type(_pmSub, Fs, 1);
        _pmHeadRow.Margin = new Thickness(0, 0, 0, HeadGap(2));
        _pmHead.Foreground = B("LgInk2"); _pmSub.Foreground = B("LgInk3");
        _pmSub.Text = v.Sub;
        _pmSub.MaxWidth = (_width - 2 - 2 * PadX) * 0.62;
        _pmSeg.Background = _dark ? B("LgField") : v.Dead ? B("LgSunk") : B("LgTile");
        _pmSeg.BorderBrush = B("LgEdgeOut");
        _pmSeg.BorderThickness = new Thickness(_dark ? 1 : 0);
        _pmSeg.Padding = new Thickness(_dark ? 1 : 2);
        AutomationProperties.SetName(_pmSeg, v.Aria);
        foreach (var g in v.Segs) {
            var b = _pmBtns[g.Mode];
            var tb = (TextBlock)b.Content;
            b.CornerRadius = new CornerRadius(_dark ? RCtl - 2 : 5);
            tb.FontWeight = _dark ? FontWeights.Medium : FontWeights.Normal;
            Brush bg = B("LgFill0"), ink;
            bool ring = false;
            if (g.On && !v.Dead) { bg = _dark ? B("LgPress") : B("LgAcc"); ink = _dark ? B("LgInk") : B("LgAccOn"); ring = _dark; }
            else if (g.On) { bg = _dark ? B("LgCard") : B("LgTile"); ink = B("LgInk2"); ring = _dark; }
            else ink = v.Dead ? B("LgInk3") : _dark ? B("LgInk2") : B("LgInk");
            b.Background = bg; b.Foreground = ink; tb.Foreground = ink;
            b.BorderBrush = B("LgEdgeOut");
            b.BorderThickness = new Thickness(ring ? 1 : 0);
            b.Opacity = g.Pending ? .6 : 1;
            ToolTipService.SetToolTip(b, g.Title);
            AutomationProperties.SetItemStatus(b, (g.On ? "chosen" : "") + (v.Dead ? (g.On ? "; " : "") + "locked" : ""));
        }
        _pmWhy.Text = v.Why;
        _pmWhy.Foreground = B("LgInk2");
        Type(_pmWhy, Fs, 1.4);
        _pmWhy.Visibility = v.Why.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_pmWhy, v.Detail != null ? PowerModeView.Sentence(v.Detail) : null);
        _pmNote.Text = v.Note ?? "";
        _pmNote.Foreground = B("LgWarn");
        Type(_pmNote, Fs, 1.4);
        _pmNote.Visibility = v.Note != null ? Visibility.Visible : Visibility.Collapsed;
    }

    void PaintNotes(List<string> lines)
    {
        _notes.Children.Clear();
        for (int i = 0; i < lines.Count; i++) {
            var t = Text(lines[i], Fs);
            t.TextWrapping = TextWrapping.Wrap; Type(t, Fs, 1.45); t.Foreground = B("LgWarn");
            t.Padding = new Thickness(0, i == 0 ? 6 : 2, 0, 0);
            AutomationProperties.SetAutomationId(t, "qs.note." + i);
            AutomationProperties.SetName(t, lines[i]);
            _notes.Children.Add(t);
        }
        _notes.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // .pmrow's 2 px top margin collapses into the volume row's 9 when no note is between them
        _pmRow.Margin = new Thickness(0, lines.Count > 0 ? 2 : 0, 0, 9);
        if (lines.Count != _notesShown) { _notesShown = lines.Count; if (Open && _view == null) Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => Fit()); }   // a late note is not cut off
    }
    int _notesShown;

    // ------------------------------------------------------------------ parts of the UI

    Tile MakeTile(string key, string label)
    {
        var t = new Tile { B = Btn("qs.tile." + key, label, double.NaN, double.NaN) };
        t.B.HorizontalAlignment = HorizontalAlignment.Stretch;
        t.B.VerticalAlignment = VerticalAlignment.Stretch;   // fills a fixed dark row (QsPanel.Tiles.cs SizeRows)
        t.B.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        t.B.Padding = new Thickness(9, 8, 9, 8);
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        t.Ico = new Canvas { Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRowSpan(t.Ico, 2);
        t.Lbl = Text(label, 12.5); Type(t.Lbl, 12.5, 1.1);
        t.Sub = Text("", 12.5); t.Sub.TextTrimming = TextTrimming.CharacterEllipsis; Type(t.Sub, 12.5, 1.2);
        Grid.SetColumn(t.Lbl, 1); Grid.SetColumn(t.Sub, 1); Grid.SetRow(t.Sub, 1);
        g.Children.Add(t.Ico); g.Children.Add(t.Lbl); g.Children.Add(t.Sub);
        t.B.Content = g;
        return t;
    }

    SliderRow MakeSlider(string key, string label)
    {
        var s = new SliderRow { Wrap = new StackPanel { Margin = new Thickness(0, 0, 0, 9) } };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        s.Head = head;
        s.Name = Text(label, 12.5); s.Val = Text("—", 12.5); s.Val.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetAutomationId(s.Val, "qs." + key + ".value");
        head.Children.Add(s.Name); head.Children.Add(s.Val);
        s.Line = new Grid();
        s.Line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        s.Line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // .qsline input[type=range] with accent-color, as Chromium draws it at scale 1: a 16 px box with a 2 px margin,
        // an 8 px track 1 px in from its ends (Track, behind), the part before the thumb and a 16 px solid thumb in --lg-acc
        s.Track = new Border { Height = 8, Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        s.Line.Children.Add(s.Track);
        s.Range = new Slider { Style = RangeStyle(), Minimum = 0, Maximum = 100, StepFrequency = 1, SmallChange = 1, LargeChange = 10, IsThumbToolTipEnabled = false, VerticalAlignment = VerticalAlignment.Center, Height = 16, MinHeight = 0, Padding = new Thickness(0), Margin = new Thickness(2) };
        Uia(s.Range, "qs." + key, label);
        s.Line.Children.Add(s.Range);
        s.Wrap.Children.Add(head); s.Wrap.Children.Add(s.Line);
        _stack.Children.Add(s.Wrap);
        s.Range.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => s.Dragging = true), true);
        void LetGo() { if (!s.Dragging) return; s.Dragging = false; if (!s.InFlight) Paint(); }
        s.Range.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => LetGo()), true);
        s.Range.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => LetGo()), true);
        s.Range.ValueChanged += (_, e) => {
            if (_painting) return;
            int v = (int)Math.Round(e.NewValue);
            s.Val.Text = v + "%";
            Push(s, key, v);
        };
        return s;
    }

    /// qs-send.js: one set in flight, only the newest value waiting, the last one always sent.
    void Push(SliderRow s, string part, double v)
    {
        if (s.InFlight) { s.Waiting = v; return; }
        s.InFlight = true;
        _ = SendLoop(s, part, v);
    }

    async System.Threading.Tasks.Task SendLoop(SliderRow s, string part, double v)
    {
        double? next = v;
        while (next is double n) {
            s.Waiting = null;
            await Set(part, JsonValue.Create((int)n), p => p["level"] = n);
            next = s.Waiting;
        }
        s.InFlight = false;
        if (!s.Dragging) Paint();
    }

    Button Btn(string id, string name, double w, double h)
    {
        var b = new Button { Style = BtnStyle(), FontSize = 12.5 };
        if (!double.IsNaN(w)) b.Width = w;
        if (!double.IsNaN(h)) b.Height = h;
        Uia(b, id, name);
        return b;
    }

    static Style? _btnStyle;
    /// .qstile / .qsmute / .pmbtn / .qslink: a flat face in the element's own Background, the hover and press tokens over it.
    static Style BtnStyle() => _btnStyle ??= (Style)XamlReader.Load(
        $"<Style {Ns} TargetType='Button'><Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/>" +
        "<Setter Property='MinWidth' Value='0'/><Setter Property='MinHeight' Value='0'/><Setter Property='CornerRadius' Value='{ThemeResource LgRCtlCorner}'/>" +
        "<Setter Property='Background' Value='Transparent'/><Setter Property='Foreground' Value='{ThemeResource LgInk}'/>" +
        "<Setter Property='IsTabStop' Value='False'/><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
        "<Setter Property='HorizontalContentAlignment' Value='Center'/><Setter Property='VerticalContentAlignment' Value='Center'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
        "<Grid x:Name='Face' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='{TemplateBinding CornerRadius}'>" +
        "<Border x:Name='Over' CornerRadius='{TemplateBinding CornerRadius}'/>" +
        "<ContentPresenter Padding='{TemplateBinding Padding}' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}' Foreground='{TemplateBinding Foreground}'/>" +
        "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'><VisualState x:Name='Normal'/>" +
        "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgHover}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Over.Background' Value='{ThemeResource LgPress}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Disabled'/></VisualStateGroup></VisualStateManager.VisualStateGroups>" +
        "</Grid></ControlTemplate></Setter.Value></Setter></Style>");

    TextBlock Text(string s, double size) => new() { Text = s, FontSize = size, Foreground = B("LgInk"), FontFamily = Font() };

    FontFamily? _font;
    FontFamily Font() => _font ??= new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI");

    static void Uia(FrameworkElement e, string id, string name)
    {
        AutomationProperties.SetAutomationId(e, id);
        AutomationProperties.SetName(e, name);
    }

    static string Label(string key) => key switch { "wifi" => "Wi-Fi", "bluetooth" => "Bluetooth", "nightlight" => "Night light", _ => key };

    static JsonObject? Obj(JsonElement e) => e.ValueKind == JsonValueKind.Object ? JsonNode.Parse(e.GetRawText()) as JsonObject : null;

    /// The --lg-* token of the panel's theme (Theme/Tokens.xaml), read in code so a repaint follows the bar's theme.
    Brush B(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        if (_missing.Add(key)) _log($"qs: the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }
    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }

    // ------------------------------------------------------------------ glyphs (bar.js svg bodies, 16x16 view box)
    // A path is "S|<data>" (stroked, 1.3 px, round caps), "s|<data>" (stroked 1.2), "F|<data>" (filled), "f<op>|<data>" (stroked at opacity).

    static string[] WifiPaths(int bars)
    {
        var arcs = new[] { "M6.3 10.8a2.6 2.6 0 0 1 3.4 0", "M4 8.2a6 6 0 0 1 8 0", "M1.6 5.6a9.5 9.5 0 0 1 12.8 0" };
        var o = new List<string> { "F|M8 12.2a1 1 0 1 1 0 2a1 1 0 1 1 0 -2z" };
        for (int i = 0; i < 3; i++) o.Add((i < bars ? "S|" : "D|") + arcs[i]);
        return o.ToArray();
    }
    static readonly string[] BtPaths = { "S|M6 4.4L10.2 8 6 11.6M8 2v12l2.2-2.4M8 8L5.2 5.8M8 8l-2.8 2.2" };
    static readonly string[] NoBtPaths = { "S|M6 4.4L10.2 8 6 11.6M8 2v12l2.2-2.4M8 8L5.2 5.8M8 8l-2.8 2.2", "S|M2.5 2.5l11 11" };
    static readonly string[] MoonPaths = { "F|M12.6 10.2A5.2 5.2 0 0 1 6.1 3.6a5.4 5.4 0 1 0 6.5 6.6z" };
    static readonly string[] SaverPaths = { "s|M1.4 7a2 2 0 0 1 2-2h6.8a2 2 0 0 1 2 2v2a2 2 0 0 1 -2 2h-6.8a2 2 0 0 1 -2 -2z", "F|M13.9 6.8a.7 .7 0 0 1 .7 .7v1a.7 .7 0 0 1 -1.4 0v-1a.7 .7 0 0 1 .7 -.7z", "F|M7.4 5.9L5.6 8.4h1.6l-.4 1.8 2-2.4H7.1z" };
    static readonly string[] GearPaths = { "g|M8 5.6A2.4 2.4 0 1 0 8 10.4 2.4 2.4 0 0 0 8 5.6z", "g|M8 1.6l1 1.7 2-.3.5 1.9 1.8.9-.9 1.8.9 1.8-1.8.9-.5 1.9-2-.3-1 1.7-1-1.7-2 .3-.5-1.9-1.8-.9.9-1.8-.9-1.8 1.8-.9.5-1.9 2 .3z" };
    static string[] VolumePaths(int waves, bool muted)
    {
        var o = new List<string> { "F|M2.2 6.1h2.4L8.1 3.1v9.8L4.6 9.9H2.2z" };
        var arcs = new[] { "M10 6.2a2.6 2.6 0 0 1 0 3.6", "M11.8 4.9a5 5 0 0 1 0 6.2", "M13.6 3.6a7.4 7.4 0 0 1 0 8.8" };
        for (int i = 0; i < waves; i++) o.Add("s|" + arcs[i]);
        if (muted) o.Add("S|M10.2 5.6l4.6 4.8M14.8 5.6l-4.6 4.8");
        return o.ToArray();
    }

    static Style? _rangeStyle;
    /// The range's own template: the Slider's named parts (it sizes HorizontalDecreaseRect and puts the thumb after it),
    /// with the accent fill drawn to the thumb's centre over them; the track itself is SliderRow.Track.
    static Style RangeStyle() => _rangeStyle ??= (Style)XamlReader.Load(
        $"<Style {Ns} xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Slider'><Setter Property='UseSystemFocusVisuals' Value='False'/>" +
        "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>" +
        "<Grid x:Name='SliderContainer' Background='{ThemeResource LgFill0}'><Grid x:Name='HorizontalTemplate'>" +
        "<Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>" +
        "<Rectangle x:Name='HorizontalTrackRect' Grid.ColumnSpan='3' Height='8' Fill='{ThemeResource LgFill0}'/>" +
        "<Rectangle x:Name='HorizontalDecreaseRect' Height='8' Fill='{ThemeResource LgFill0}'/>" +
        "<Border Grid.ColumnSpan='2' Height='8' Margin='1,0,8,0' CornerRadius='4' Background='{ThemeResource LgAcc}'/>" +
        "<Thumb x:Name='HorizontalThumb' Grid.Column='1' Width='16' Height='16'><Thumb.Template><ControlTemplate TargetType='Thumb'>" +
        "<Ellipse Fill='{ThemeResource LgAcc}'/></ControlTemplate></Thumb.Template></Thumb>" +
        "</Grid></Grid></ControlTemplate></Setter.Value></Setter></Style>");

    /// .qsrowhead's 4 px under a head line. Light heads are 12.5 px lines, which Chromium keeps fractional and a TextBlock
    /// makes 13: the brightness and power mode heads give the half pixel back so the rows land where Electron's do.
    double HeadGap(int row) => _dark || row == 1 ? 4 : 3;

    /// Electron's type per theme: light 12.5 throughout; dark (Helm's scale, bar.css) 13, a tile's sub line 12.
    double Fs => _dark ? 13 : 12.5;
    double SubFs => _dark ? 12 : 12.5;

    /// A CSS line box: font size and line-height (a multiple), the line exactly that tall as Chromium makes it.
    static void Type(TextBlock t, double fs, double lh)
    {
        t.FontSize = fs;
        t.LineHeight = Math.Round(fs * lh, MidpointRounding.AwayFromZero);   // a TextBlock's line is whole pixels (Chromium's is not)
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    /// A token at a fraction of its own opacity (a colour Electron mixes, or Chromium's own control colours).
    static Brush Alpha(Brush b, double a) => b is SolidColorBrush s ? new SolidColorBrush(s.Color) { Opacity = s.Opacity * a } : b;

    /// The main view's width as Electron's .pop takes it: its widest row unwrapped (max-content), clamped to
    /// min-width 268 / max-width 360. The rows are measured with one detached TextBlock in the panel's font.
    double MainWidth()
    {
        double tile = 0;
        foreach (var t in _tiles.Values)
            tile = Math.Max(tile, 9 + 22 + 8 + Math.Max(TextW(t.Lbl.Text, Fs), TextW(t.Sub.Text, SubFs)) + 9 + (t.Chev != null ? ChevW : 0));
        double need = 2 * tile + 6;
        foreach (var n in _notes.Children) if (n is TextBlock nt) need = Math.Max(need, TextW(nt.Text, Fs));
        foreach (var n in _xNotes.Children) if (n is TextBlock nt) need = Math.Max(need, TextW(nt.Text, Fs));
        need = Math.Max(need, TextW(_pmHead.Text, Fs) + TextW(_pmSub.Text, Fs));
        double seg = 0;
        foreach (var (_, label, _) in PowerModeView.Modes) seg = Math.Max(seg, TextW(label, 12, _dark ? FontWeights.Medium : FontWeights.Normal) + 8);
        need = Math.Max(need, 3 * seg + 2 * 2 + 2 * 2);
        if (_pmWhy.Visibility == Visibility.Visible) need = Math.Max(need, TextW(_pmWhy.Text, Fs));
        if (_pmNote.Visibility == Visibility.Visible) need = Math.Max(need, TextW(_pmNote.Text, Fs));
        double foot = TextW(_power.Text, Fs) + 8 + TextW("Windows quick settings", Fs) + 14 + 4 + 26;
        need = Math.Max(need, foot);
        if (!(foot > 40)) { _log("qs: the panel's text could not be measured, so it keeps the fixed width"); return WidthDip; }
        return Math.Clamp(Math.Ceiling(need) + 2 * PadX + 2, MinWidthDip, MaxWidthDip);
    }

    TextBlock? _meter;
    double TextW(string? s, double fs, Windows.UI.Text.FontWeight? weight = null)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        _meter ??= new TextBlock { FontFamily = Font(), TextWrapping = TextWrapping.NoWrap };
        _meter.FontSize = fs;
        _meter.FontWeight = weight ?? FontWeights.Normal;
        _meter.Text = s;
        _meter.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return _meter.DesiredSize.Width;
    }

    /// The sound view's per-app sliders (the stock Slider): fill and thumb in --lg-acc, per theme, set before the
    /// template is applied.
    void Accent(Slider s)
    {
        foreach (var theme in new[] { "Light", "Dark" }) {
            var d = new ResourceDictionary();
            var acc = Tok(theme, "LgAcc");
            foreach (var k in new[] { "SliderTrackValueFill", "SliderTrackValueFillPointerOver", "SliderTrackValueFillPressed", "SliderThumbBackground", "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed" })
                d[k] = acc;
            s.Resources.ThemeDictionaries[theme] = d;
        }
    }

    Brush Tok(string theme, string key)
    {
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        return new SolidColorBrush();
    }

    /// SVG path data in the XAML path syntax's spelling: "2-.3.5" is "2 -.3 .5" (SVG lets a sign or a second dot start
    /// the next number; the XAML parser is not promised to).
    static string Norm(string d)
    {
        var o = new System.Text.StringBuilder(d.Length + 16);
        bool inNum = false, dot = false;
        foreach (char ch in d) {
            if (char.IsDigit(ch)) { inNum = true; o.Append(ch); continue; }
            if (ch == '.') { if (inNum && dot) o.Append(' '); inNum = true; dot = true; o.Append(ch); continue; }
            if (ch == '-') { if (inNum) o.Append(' '); inNum = true; dot = false; o.Append(ch); continue; }
            if (char.IsLetter(ch)) o.Append(' ').Append(ch).Append(' ');
            else o.Append(' ');
            inNum = false; dot = false;
        }
        return System.Text.RegularExpressions.Regex.Replace(o.ToString(), " +", " ").Trim();
    }

    /// Draws the paths into a 16x16 canvas in one ink (the SVG's currentColor), a theme token resolved by the element's
    /// own theme. Built inline: the AOT build cannot cast XamlReader's result to Shapes.Path (it can to UIElement).
    static void Fill(Canvas c, string[] paths, string inkKey)
    {
        c.Children.Clear();
        foreach (var spec in paths) {
            int bar = spec.IndexOf('|');
            string kind = spec[..bar], data = spec[(bar + 1)..];
            string paint = kind == "F" ? $"Fill='{{ThemeResource {inkKey}}}'"
                : $"Stroke='{{ThemeResource {inkKey}}}' StrokeThickness='{(kind == "s" ? "1.2" : kind == "g" ? "1.1" : "1.3")}' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'" + (kind == "D" ? " Opacity='0.28'" : "");
            c.Children.Add((UIElement)XamlReader.Load($"<Path {Ns} Data='{Norm(data)}' {paint}/>"));
        }
    }
}
