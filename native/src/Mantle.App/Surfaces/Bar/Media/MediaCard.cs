using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.Core.Features.Media;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Bar.Media;

/// <summary>
/// The now-playing card (bar.js buildMediaPop / mediaPaint, bar.css .mcard .mc-*): artwork, title, "artist · album",
/// the app line, the progress hairline (a local 1 s tick only while open and playing), Previous / Play-Pause / Next (a
/// button the app does not accept is greyed but still clickable and says why), the lock box under the protected media
/// apps rule, the note, the source list (two or more apps) and the "Updates every 10 s" foot. Code-built; the theme's
/// tokens are read on every paint. UI thread only. Ids: docs/lanes/media.md.
/// </summary>
internal sealed class MediaCard
{
    public const double WidthDip = 300, PadX = 12, PadY = 11, SettleMs = 400;

    public readonly Grid Root = new();
    readonly StackPanel _stack = new() { Width = WidthDip - 2 * PadX };
    readonly Border _art = new() { Width = 72, Height = 72, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    readonly TextBlock _title = T(14), _artist = T(12.5), _appTxt = T(12.5), _now = T(11.5), _dur = T(11.5), _progWhy = T(11.5),
        _lockHead = T(12.5), _lockHow = T(11.5), _note = T(12.5), _srcWhy = T(11.5), _foot = T(11.5);
    readonly Border _appIco = new() { Width = 14, Height = 14, CornerRadius = new CornerRadius(3) };
    readonly StackPanel _app = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel _prog = new() { Margin = new Thickness(0, 12, 0, 0) };
    readonly Border _track = new() { Height = 2, CornerRadius = new CornerRadius(1) }, _fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    readonly StackPanel _ctl = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
    readonly Dictionary<string, Button> _btns = new(StringComparer.Ordinal);
    readonly Border _lock = new() { Padding = new Thickness(10, 9, 10, 9), Margin = new Thickness(0, 12, 0, 0) };
    readonly Border _src = new() { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 9, 0, 0), Margin = new Thickness(0, 10, 0, 0) };
    readonly StackPanel _srcList = new() { Spacing = 1 };
    readonly Dictionary<string, Button> _srcBtns = new(StringComparer.Ordinal);
    Button? _srcAuto;
    string? _srcKeys;
    double _srcMovedAt, _srcAt = double.NaN;
    string? _artSrc = "\u0001", _icoSrc = "\u0001";

    readonly Action<string> _log;
    readonly Func<double> _now0;
    bool _dark;

    /// What the card asks of the bar: send a control, pick a source, re-ask for room.
    public Action<string>? Control;
    public Action<string?, string>? Pick;
    public Action<double>? Room;

    public string Note { get; set; } = "";
    public string Busy { get; set; } = "";
    public bool Open { get; set; }

    public MediaCard(Action<string> log, Func<double> nowMs)
    {
        _log = log; _now0 = nowMs;
        Uia(Root, "media.card", "Now playing");
        Root.Padding = new Thickness(PadX, PadY, PadX, PadY);
        Root.Children.Add(_stack);
        // head: art + meta (.mc-head, gap 12)
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Uia(_art, "media.card.art", "Artwork");
        head.Children.Add(_art);
        var meta = new StackPanel { Padding = new Thickness(0, 2, 0, 0) };
        Grid.SetColumn(meta, 1);
        _title.FontWeight = FontWeights.SemiBold; _title.MaxLines = 2; _title.TextWrapping = TextWrapping.WrapWholeWords; _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _artist.Margin = new Thickness(0, 2, 0, 0); _artist.TextTrimming = TextTrimming.CharacterEllipsis;
        Uia(_title, "media.card.title", ""); Uia(_artist, "media.card.artist", ""); Uia(_appTxt, "media.card.app", "");
        _appTxt.TextTrimming = TextTrimming.CharacterEllipsis;
        _app.Children.Add(_appIco); _app.Children.Add(_appTxt);
        meta.Children.Add(_title); meta.Children.Add(_artist); meta.Children.Add(_app);
        head.Children.Add(meta);
        _stack.Children.Add(head);
        // progress (.mc-prog: a hairline, not a slider)
        _track.Child = _fill;
        var times = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        _dur.HorizontalAlignment = HorizontalAlignment.Right;
        times.Children.Add(_now); times.Children.Add(_dur);
        Uia(_prog, "media.card.prog", "Progress"); Uia(_now, "media.card.now", ""); Uia(_dur, "media.card.dur", "");
        _prog.Children.Add(_track); _prog.Children.Add(times);
        _stack.Children.Add(_prog);
        _progWhy.Margin = new Thickness(0, 10, 0, 0); _progWhy.TextWrapping = TextWrapping.Wrap;
        Uia(_progWhy, "media.card.progwhy", "");
        _stack.Children.Add(_progWhy);
        // transport
        foreach (var a in new[] { "previous", "toggle", "next" }) {
            var b = new Button { Width = a == "toggle" ? 44 : 36, Height = a == "toggle" ? 36 : 32, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), IsTabStop = true };
            Uia(b, "media.card." + a, a);
            var act = a;
            b.Click += (_, _) => Control?.Invoke(act);
            _btns[a] = b;
            _ctl.Children.Add(b);
        }
        _stack.Children.Add(_ctl);
        // the protected media apps rule's lock box
        var lockStack = new StackPanel();
        _lockHow.Margin = new Thickness(0, 3, 0, 0); _lockHow.TextWrapping = TextWrapping.Wrap; _lockHead.TextWrapping = TextWrapping.Wrap;
        Uia(_lock, "media.card.lock", "Controls off"); Uia(_lockHead, "media.card.lockhead", ""); Uia(_lockHow, "media.card.lockhow", "");
        lockStack.Children.Add(_lockHead); lockStack.Children.Add(_lockHow);
        _lock.Child = lockStack;
        _stack.Children.Add(_lock);
        _note.Margin = new Thickness(0, 10, 0, 0); _note.TextWrapping = TextWrapping.Wrap;
        Uia(_note, "media.card.note", "");
        _stack.Children.Add(_note);
        // the source list
        var srcStack = new StackPanel();
        _srcWhy.Margin = new Thickness(0, 0, 0, 5); _srcWhy.TextWrapping = TextWrapping.Wrap;
        Uia(_src, "media.card.src", "Sources"); Uia(_srcWhy, "media.card.srcwhy", "");
        srcStack.Children.Add(_srcWhy); srcStack.Children.Add(_srcList);
        // The list's place is read after each layout pass too, not only at paint (before layout) and at a click: a
        // paint-time read is pre-layout, so the first click after it saw a "move" that had happened long before.
        _srcList.LayoutUpdated += (_, _) => Moved();
        _src.Child = srcStack;
        _stack.Children.Add(_src);
        _foot.Margin = new Thickness(0, 10, 0, 0); _foot.TextWrapping = TextWrapping.Wrap;
        Uia(_foot, "media.card.foot", "");
        _stack.Children.Add(_foot);
    }

    /// Repaint from the payload and the session shown (<paramref name="readAt"/>: when the payload arrived).
    public void Paint(JsonObject? p, JsonObject s, double readAt, bool dark)
    {
        _dark = dark;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        string app = MediaView.App(s);
        // art: the artwork, else the app's icon small on a tile, else the note glyph
        string? artwork = MediaService.Str(s["artwork"]);
        string? src = artwork ?? MediaService.Str(s["app"]?["icon"]);
        _art.Background = B("LgTile"); _art.BorderBrush = B("LgEdgeOut");
        string? artWhy = artwork == null ? MediaService.Str(s["artworkReason"]) : null;
        ToolTipService.SetToolTip(_art, artWhy != null ? "no artwork: " + artWhy : null);
        AutomationProperties.SetHelpText(_art, artwork != null ? "art" : src != null ? "app" : "none");
        if (src != _artSrc) { _artSrc = src; SetPic(_art, src, artwork != null ? 72 : 32, 26); }
        _title.Text = MediaService.Str(s["title"]) is { Length: > 0 } t ? t : app;
        ToolTipService.SetToolTip(_title, MediaService.Str(s["title"]));
        var (artistText, why) = MediaView.ArtistLine(s);
        _artist.Text = artistText; _artist.TextWrapping = why ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _appTxt.Text = app + " · " + MediaView.Status(s);
        string? ico = MediaService.Str(s["app"]?["icon"]);
        if (ico != _icoSrc) { _icoSrc = ico; SetIcon(_appIco, ico, app); }
        foreach (var tb in new[] { _title, _lockHead }) tb.Foreground = B("LgInk");
        _artist.Foreground = B(why ? "LgInk3" : "LgInk2");
        foreach (var tb in new[] { _appTxt, _now, _dur, _progWhy, _lockHow, _srcWhy, _foot }) tb.Foreground = B("LgInk3");
        _note.Foreground = B("LgInk2");
        PaintProgress(s, readAt);
        bool guarded = MediaView.Guarded(s);
        _ctl.Visibility = guarded ? Visibility.Collapsed : Visibility.Visible;
        _lock.Visibility = guarded ? Visibility.Visible : Visibility.Collapsed;
        _lock.Background = B("LgSunk"); _lock.CornerRadius = new CornerRadius(8);
        if (guarded) {
            _lockHead.Text = MediaService.Str(s["controls"]?["reason"]) ?? "controls are off for this session";
            string? how = MediaService.Str(s["controls"]?["how"]);
            _lockHow.Inlines.Clear();
            // a --flag is one word: its hyphens are non-breaking, so it is never split across lines
            foreach (var (text, flag) in MediaView.HowParts(how)) _lockHow.Inlines.Add(new Run { Text = flag ? text.Replace('-', '‑') : text });
            _lockHow.Visibility = string.IsNullOrEmpty(how) ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(_lockHow, how ?? "");
        } else {
            bool playing = MediaService.Bool(s["playing"]);
            foreach (var a in new[] { "previous", "toggle", "next" }) {
                var b = _btns[a];
                var can = MediaView.Can(s, a);
                string g = a == "toggle" ? (playing ? "pause" : "play") : a;
                var ink = B(can.Ok ? "LgInk" : "LgInk3");
                var glyph = MediaPics.Glyph(g, a == "toggle" ? 16 : 14, ink);
                if (!can.Ok) glyph.Opacity = .45;
                b.Content = glyph;
                b.Background = a == "toggle" ? B("LgSunk") : new SolidColorBrush();
                b.CornerRadius = new CornerRadius(6);
                b.Opacity = Busy == a ? .55 : 1;
                string label = a == "toggle" ? (playing ? "Pause" : "Play") : a == "next" ? "Next track" : "Previous track";
                string tip = can.Ok ? label : label + ": " + can.Why;
                ToolTipService.SetToolTip(b, tip);
                AutomationProperties.SetName(b, tip);
                AutomationProperties.SetItemStatus(b, (can.Ok ? "on" : "off") + (Busy == a ? "; busy" : "") + "; " + g);
            }
        }
        _note.Text = Note;
        AutomationProperties.SetName(_note, Note);
        _note.Visibility = Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PaintSources(p, s);
        _foot.Text = MediaView.Foot(p);
        AutomationProperties.SetName(_foot, _foot.Text);
        _foot.Visibility = _foot.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var tb in new[] { _title, _artist, _appTxt, _progWhy, _lockHead, _srcWhy }) AutomationProperties.SetName(tb, tb.Text);
    }

    /// The progress hairline from the local clock; true when it should tick again in 1 s.
    public bool PaintProgress(JsonObject s, double readAt)
    {
        var pos = MediaView.Pos(s, _now0() - readAt);
        _prog.Visibility = pos != null ? Visibility.Visible : Visibility.Collapsed;
        _progWhy.Visibility = pos != null ? Visibility.Collapsed : Visibility.Visible;
        _track.Background = B("LgEdgeOut"); _fill.Background = B("LgInk2");
        if (pos is { } p) {
            _fill.Width = Math.Max(0, (WidthDip - 2 * PadX) * p.Pos / p.Dur);
            _now.Text = MediaView.FmtMs(p.Pos); _dur.Text = MediaView.FmtMs(p.Dur);
            AutomationProperties.SetName(_now, _now.Text); AutomationProperties.SetName(_dur, _dur.Text);
        } else {
            _progWhy.Text = MediaView.NoPosition(s);
            AutomationProperties.SetName(_progWhy, _progWhy.Text);
        }
        return pos != null && MediaService.Bool(s["playing"]);
    }

    void PaintSources(JsonObject? p, JsonObject s)
    {
        var v = MediaView.Sources(p, s);
        _src.Visibility = v.Shown ? Visibility.Visible : Visibility.Collapsed;
        _src.BorderBrush = B("LgEdgeOut");
        _srcWhy.Text = v.Why;
        if (!v.Shown) { Moved(); return; }
        string keys = string.Join("\u0001", v.Rows.Select(r => r.Aumid.ToLowerInvariant()));
        if (keys != _srcKeys) {
            // rows are rebuilt only when the set of apps changes, and never reorder otherwise
            if (_srcKeys != null) _srcMovedAt = _now0();
            _srcKeys = keys;
            _srcList.Children.Clear();
            _srcBtns.Clear();
            foreach (var r in v.Rows) {
                var b = SrcButton("media.card.src." + r.Aumid.ToLowerInvariant());
                string aumid = r.Aumid;
                b.Click += (_, _) => {
                    if (AutomationProperties.GetItemStatus(b).StartsWith("on", StringComparison.Ordinal) && !AutomationProperties.GetItemStatus(b).Contains("gone")) return;
                    Take(aumid, AutomationProperties.GetName(b).Split(" · ")[0]);
                };
                _srcBtns[r.Aumid.ToLowerInvariant()] = b;
                _srcList.Children.Add(b);
            }
            _srcAuto = SrcButton("media.card.src.auto");
            _srcAuto.Click += (_, _) => { if (AutomationProperties.GetItemStatus(_srcAuto).StartsWith("on", StringComparison.Ordinal)) return; Take(null, "Automatic"); };
            _srcList.Children.Add(_srcAuto);
        }
        foreach (var r in v.Rows) {
            if (!_srcBtns.TryGetValue(r.Aumid.ToLowerInvariant(), out var b)) continue;
            PaintRow(b, r.Icon, r.Name, r.Text, r.Tip, r.On);
            AutomationProperties.SetItemStatus(b, (r.On ? "on" : "off") + (r.Gone ? "; gone" : ""));
        }
        if (_srcAuto != null) { PaintRow(_srcAuto, null, null, "Automatic", v.AutoTip, v.AutoOn); AutomationProperties.SetItemStatus(_srcAuto, v.AutoOn ? "on" : "off"); }
        Moved();
    }

    void PaintRow(Button b, string? icon, string? name, string text, string tip, bool on)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (name != null) { var ico = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4) }; SetIcon(ico, icon, name); row.Children.Add(ico); }
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = B(on ? "LgAcc" : "LgInk2") });
        b.Content = row;
        b.Background = on ? B("LgAccSoft") : new SolidColorBrush();
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetName(b, text);
        AutomationProperties.SetHelpText(b, tip);
    }

    Button SrcButton(string id)
    {
        var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), MinHeight = 0 };
        Uia(b, id, "");
        return b;
    }

    /// srcMoved(): the list's place on screen; a change stamps the time a click is not taken for (400 ms).
    void Moved()
    {
        try {
            var t = _srcList.TransformToVisual(null).TransformPoint(new global::Windows.Foundation.Point(0, 0));
            double at = Math.Round(t.Y) * 10000 + Math.Round(t.Y + _srcList.ActualHeight);
            if (!double.IsNaN(_srcAt) && at != _srcAt) _srcMovedAt = _now0();
            _srcAt = at;
        } catch (ArgumentException) { /* not in a tree yet */ }
    }

    void Take(string? aumid, string label)
    {
        Moved();   // a move since the last paint counts too: it is under the pointer NOW
        if (_now0() - _srcMovedAt < SettleMs) {
            Note = $"That click on {label} was not taken: the source list had just moved under the pointer. Click again if you meant it.";
            _log($"media: a source click on {label} was not taken (the list moved {Math.Round(_now0() - _srcMovedAt)} ms ago)");
            _note.Text = Note; AutomationProperties.SetName(_note, Note); _note.Visibility = Visibility.Visible;
            return;
        }
        Pick?.Invoke(aumid, label);
    }

    /// The height the card needs (DIP), measured at its width.
    public double Need()
    {
        Root.Measure(new global::Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        return Math.Ceiling(Root.DesiredSize.Height);
    }

    async void SetPic(Border box, string? src, double size, double glyph)
    {
        var img = await MediaPics.Load(src);
        if (src != _artSrc) return;   // a newer source arrived meanwhile
        box.Child = img != null ? new Image { Source = img, Width = size, Height = size, Stretch = size >= 72 ? Stretch.UniformToFill : Stretch.Uniform }
            : MediaPics.Glyph("note", glyph, B("LgInk2"));
    }

    async void SetIcon(Border box, string? icon, string name)
    {
        box.Child = Letter(name);
        var img = await MediaPics.Load(icon);
        if (img != null) box.Child = new Image { Source = img, Width = box.Width, Height = box.Height };
        else box.Background = B("LgTile");
    }

    TextBlock Letter(string name) => new() {
        Text = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?", FontSize = 9, FontWeight = FontWeights.SemiBold,
        Foreground = B("LgInk"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock T(double size) => new() { FontSize = size, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI") };

    static void Uia(FrameworkElement e, string id, string name) { AutomationProperties.SetAutomationId(e, id); AutomationProperties.SetName(e, name); }

    readonly HashSet<string> _missing = new();
    /// The --lg-* token of the card's theme (Theme/Tokens.xaml), read in code so a repaint follows the bar's theme.
    Brush B(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        if (_missing.Add(key)) _log($"media: the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }
    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }
}
