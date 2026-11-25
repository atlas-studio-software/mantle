using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mantle.Core.Features.Downloads;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Surfaces.Bar.Downloads;

/// <summary>
/// The Downloads shelf's panel (ui/bar/downloads.js build/paint/row, downloads.css): "Downloads" and "N still
/// downloading…", the newest rows (a type glyph from one family, the name cut with an ellipsis, "size · age", a dot on
/// a row that was new when the panel opened, Show in folder), the empty / error / reading states, the note, the foot's
/// detail and "Open Downloads folder". Code-built; the theme's tokens are read on every paint. UI thread only.
/// UI Automation ids: docs/lanes/media.md (part 4).
/// </summary>
internal sealed class DownloadsShelf
{
    /// A list panel of the levitated look (lane r12-levitate-host): 360 wide (was 340).
    public const double WidthDip = PopupLevitate.ListWidth;
    static readonly Dictionary<string, string> KindWord = new(StringComparer.Ordinal) {
        ["file"] = "File", ["doc"] = "Document", ["sheet"] = "Spreadsheet", ["code"] = "Code", ["archive"] = "Archive", ["image"] = "Image",
        ["video"] = "Video", ["audio"] = "Audio", ["slides"] = "Presentation", ["app"] = "App or installer", ["disk"] = "Disk image", ["folder"] = "Folder",
    };
    // one drawn family: Windows' own icon font (Segoe Fluent Icons, MDL2 fallback), never per-app icons
    static readonly Dictionary<string, string> KindGlyph = new(StringComparer.Ordinal) {
        ["file"] = "", ["doc"] = "", ["sheet"] = "", ["code"] = "", ["archive"] = "", ["image"] = "",
        ["video"] = "", ["audio"] = "", ["slides"] = "", ["app"] = "", ["disk"] = "", ["folder"] = "",
    };

    public readonly Grid Root = new();
    readonly StackPanel _stack = new();
    readonly TextBlock _sub = T(11), _note = T(12), _detail = T(11);
    readonly StackPanel _list = new() { Spacing = 1 };
    readonly ScrollViewer _scroll = new() { MaxHeight = 366, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    // the foot's one text button (look.css .link): 28 tall, padding 0 12, radius 8, 12 / 500, text-2, fill-1 on hover
    readonly Button _folder = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Height = 28,
        Padding = new Thickness(12, 0, 12, 0), CornerRadius = new CornerRadius(LevitateTokens.RadiusCtlL), FontSize = 12, FontWeight = FontWeights.Medium };
    // the foot: a 40 DIP row, a hair line on top, padding 0 8 0 16 (panels/downloads.css .dl-foot)
    readonly Border _foot = new() { Height = PopupLevitate.FootHeight, BorderThickness = new Thickness(0, PopupLevitate.FootLine, 0, 0),
        Padding = new Thickness(PopupLevitate.FootPadLeft, 0, PopupLevitate.FootPadRight, 0) };
    // the hover / press fills of the rows and buttons (lightweight styling), one brush each, re-coloured on every paint
    readonly SolidColorBrush _fill1 = new(), _fill2 = new(), _press = new(), _clear = new(), _hoverInk = new();
    readonly List<Button> _rows = new();
    readonly List<RowParts> _parts = new();
    readonly Action<string> _log;
    bool _dark;

    public JsonElement View;                         // the last downloads.list answer (Undefined: none yet)
    public JsonElement State;                        // the last downloads.state push
    public bool Loading;
    public string Note = "", KbdNote = "";
    public readonly HashSet<string> WasFresh = new(StringComparer.Ordinal), Busy = new(StringComparer.Ordinal);

    /// What the panel asks of the bar: open / reveal a row (by token), open the folder, drag a row out.
    public Action<string, string?, string>? Act;     // (verb, item or null, what for the note)
    public Action<string, string>? Drag;            // (item, shown name)

    public DownloadsShelf(Action<string> log)
    {
        _log = log;
        Uia(Root, "dl.panel", "Downloads");
        // the levitated list panel (lane r12-levitate-host; bar prototype apps-r3/mantle-bar panels/downloads.css): one island,
        // 360 wide, padding 8 round the list; the head and the foot are 40 DIP rows from the island's edge. The pop-up host
        // paints the frost, the dim and the film under it (PopupKind.Levitate), so the panel has no background of its own.
        Root.Padding = new Thickness(0);
        Root.Children.Add(_stack);
        var head = new Grid { Height = PopupLevitate.HeadHeight, Padding = new Thickness(PopupLevitate.HeadPadLeft, 0, PopupLevitate.HeadPadRight, 0) };
        var h = T(PopupLevitate.TitleSize); h.Text = "Downloads"; h.FontWeight = FontWeights.SemiBold; h.VerticalAlignment = VerticalAlignment.Center;
        Uia(h, "dl.title", "Downloads");
        _sub.HorizontalAlignment = HorizontalAlignment.Right; _sub.VerticalAlignment = VerticalAlignment.Center; _sub.Margin = new Thickness(0, 0, 8, 0);
        Uia(_sub, "dl.sub", "");
        head.Children.Add(h); head.Children.Add(_sub);
        _stack.Children.Add(head);
        _scroll.Content = _list;
        _scroll.Margin = new Thickness(PopupLevitate.ListPadding, 0, PopupLevitate.ListPadding, 0);
        _scroll.Padding = new Thickness(0, 0, 0, 4);
        Uia(_scroll, "dl.list", "Downloads list");
        _stack.Children.Add(_scroll);
        _note.Margin = new Thickness(16, 6, 16, 4); _note.TextWrapping = TextWrapping.Wrap;
        Uia(_note, "dl.note", "");
        _stack.Children.Add(_note);
        _detail.TextWrapping = TextWrapping.Wrap; _detail.Margin = new Thickness(16, 2, 16, 6);
        Uia(_detail, "dl.detail", "");
        _stack.Children.Add(_detail);
        _folder.Content = new TextBlock { Text = "Open Downloads folder" };
        Uia(_folder, "dl.folder", "Open Downloads folder");
        Fills(_folder, _fill1, _press);
        _folder.Click += (_, _) => Act?.Invoke("folder", null, "Could not open the Downloads folder");
        _foot.Child = _folder;
        _stack.Children.Add(_foot);
    }

    /// Lightweight styling of one button: its hover and press fills (the shelf's own brushes), no border in any state, the
    /// text ink on hover.
    void Fills(Button b, Brush hover, Brush press)
    {
        b.Resources["ButtonBackgroundPointerOver"] = hover;
        b.Resources["ButtonBackgroundPressed"] = press;
        b.Resources["ButtonBorderBrushPointerOver"] = _clear;
        b.Resources["ButtonBorderBrushPressed"] = _clear;
        b.Resources["ButtonForegroundPointerOver"] = _hoverInk;
        b.Resources["ButtonForegroundPressed"] = _hoverInk;
    }

    /// The rows in order (the keyboard walks them).
    public IReadOnlyList<Button> Rows => _rows;

    /// paint(): rebuild from the view and the push; keeps keyboard focus on the same row and the scroll position.
    public void Paint(bool dark, long nowMs)
    {
        _dark = dark;
        // fill-1 (hover), fill-2 (the reveal's hover), fill-press; in light today's --lg-hover / --lg-press
        _fill1.Color = Color(Ink("LvCtlFillHover", "LgHover"));
        _fill2.Color = Color(Ink("LvCtlFillOn", "LgHover"));
        _press.Color = Color(Ink("LvCtlFillPressed", "LgPress"));
        _hoverInk.Color = Color(Ink("LvText", "LgInk"));
        var v = View;
        bool has = v.ValueKind == JsonValueKind.Object;
        var items = has && v.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array ? it.EnumerateArray().ToList() : new List<JsonElement>();
        bool bad = has && v.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;
        string state = Str(v, "state") ?? "";
        int partial = Int(v, "partial") is int p and > 0 ? p : Int(State, "partial") ?? 0;
        _sub.Text = partial > 0 ? $"{partial} still downloading…" : "";
        _sub.Visibility = partial > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_sub, _sub.Text);
        _sub.Foreground = Ink("LvText2", "LgInk3");

        bool noFolder = state is "missing" or "unresolved";
        string? folder = Str(v, "folder");
        string tip = noFolder ? "There is no Downloads folder to open right now" : folder != null ? $"Open {folder} in File Explorer" : "Open your Downloads folder in File Explorer";
        ToolTipService.SetToolTip(_folder, tip);
        AutomationProperties.SetHelpText(_folder, tip);
        _folder.IsEnabled = !Busy.Contains("*") && !noFolder && state != "resting" && !(has && folder == null && !bad);
        _folder.Foreground = _folder.IsEnabled ? Ink("LvText2", "LgInk2") : Ink("LvText4", "LgInk3");
        _folder.Background = new SolidColorBrush();
        _folder.BorderThickness = new Thickness(0);
        AutomationProperties.SetHelpText(_scroll, KbdNote);
        ToolTipService.SetToolTip(_scroll, KbdNote.Length > 0 ? KbdNote : null);

        string? focusItem = null; bool focusReveal = false; var focusHow = FocusState.Keyboard;
        var fe = Root.XamlRoot != null ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as FrameworkElement : null;
        if (fe?.Tag is string tag) { focusItem = tag.TrimStart('r', ':'); focusReveal = tag.StartsWith("r:", StringComparison.Ordinal); }
        // a repaint keeps the focus as it was: one Windows gave on its own stays unasked (no ring, not a walked row)
        if (fe is Control fc && fc.FocusState == FocusState.Programmatic) focusHow = FocusState.Programmatic;
        double scrollTop = _scroll.VerticalOffset;
        // the same items in the same order are filled in place: the row with the keyboard stays that very element, so a
        // press's own repaint (busy) and its answer's (the note) never move the focus; other items are new rows
        bool same = has && !Loading && !bad && items.Count > 0 && _parts.Count == items.Count
            && _parts.Select(rp => rp.Item).SequenceEqual(items.Select(i => Str(i, "item") ?? ""), StringComparer.Ordinal);
        if (!same) { _list.Children.Clear(); _rows.Clear(); _parts.Clear(); }

        _scroll.MaxHeight = Note.Length > 0 || (Str(v, "detail") ?? "").Length > 0 ? 300 : 366;
        if (!has || Loading) _list.Children.Add(Empty("Reading your Downloads folder…", null, "reading"));
        else if (bad) {
            string big = state switch {
                "missing" => "Your Downloads folder is missing", "resting" => "Paused while you play", "reading" => "Not read yet",
                "unresolved" => "Downloads folder not found", "unreadable" => "Mantle cannot read your Downloads folder", _ => "Downloads unavailable",
            };
            string why = Str(v, "reason") ?? "the folder could not be read and no reason was given";
            var twice = state == "missing" ? Regex.Match(why, @"^the Downloads folder does not exist \((.+)\)$") : Match.Empty;
            var u = Empty(big, DownloadsList.Sentence(twice.Success ? twice.Groups[1].Value : why), state == "resting" ? "resting" : "bad");
            if (folder != null) u.Children.Add(Line(folder, "dl.path"));
            if (Int(v, "retryMs") is int ms and > 0 && state != "resting")
                u.Children.Add(Line($"Mantle looks again every {Math.Round(ms / 60000.0)} minutes and each time you open this panel.", "dl.next"));
            _list.Children.Add(u);
        } else if (items.Count == 0) _list.Children.Add(Empty("Nothing downloaded yet", "Files you download show up here, newest first.", "empty"));
        else if (same) { for (int n = 0; n < items.Count; n++) Fill(_parts[n], items[n], n, nowMs); }
        else {
            int n = 0;
            foreach (var i in items) { var rp = MakeRow(); Fill(rp, i, n++, nowMs); _parts.Add(rp); _rows.Add(rp.Row); _list.Children.Add(rp.Cell); }
            if (focusItem != null && _rows.FirstOrDefault(r => (r.Tag as string) == focusItem) is { } back) {
                bool got = focusReveal && back.Parent is Grid g && g.Children.OfType<Button>().LastOrDefault() is { } rv && rv != back ? rv.Focus(focusHow) : back.Focus(focusHow);
                if (!got) _log($"downloads: a repaint could not put the keyboard back on row {_rows.IndexOf(back) + 1} ({focusHow})");
            }
        }
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, scrollTop, null, true);

        string say = Note.Length > 0 ? Note : Str(v, "seenNote") ?? "";
        _note.Text = say.Length > 0 ? DownloadsList.Sentence(say) : "";
        _note.Visibility = say.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _note.Foreground = Ink("LvText2", "LgInk2");
        AutomationProperties.SetName(_note, _note.Text);
        var words = new List<string>();
        if (Str(v, "detail") is { Length: > 0 } d) words.Add(d);
        if (!bad && Str(v, "watchReason") is { Length: > 0 } wr) words.Add(DownloadsList.Sentence(wr));
        _detail.Text = string.Join(" ", words);
        _detail.Visibility = words.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _detail.Foreground = Ink("LvText3", "LgInk3");
        AutomationProperties.SetName(_detail, _detail.Text);
        _foot.BorderBrush = Ink("LvHair", "LgDivider");
        Root.Background = null;
    }

    StackPanel Empty(string big, string? sub, string status)
    {
        // the one Mantle empty-state recipe (panels/downloads.css .dl-empty): padding 32 16, a 40 DIP tile at radius 12 for
        // "nothing yet", the line 14 / 600, the sentence 12 in text-2, 4 apart
        var u = new StackPanel { Padding = new Thickness(16, 32, 16, 32), Spacing = 4 };
        Uia(u, "dl.empty", big);
        AutomationProperties.SetItemStatus(u, status);
        if (status == "empty")
            u.Children.Add(new Border {
                Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(LevitateTokens.IslandRadius), Background = Ink("LvCtlFillHover", "LgTile"), IsHitTestVisible = false,
                Child = new FontIcon { Glyph = "", FontSize = 20, Foreground = Ink("LvText2", "LgInk2") },
            });
        var b = T(PopupLevitate.TitleSize); b.FontWeight = FontWeights.SemiBold; b.Text = big; b.TextWrapping = TextWrapping.Wrap; b.HorizontalAlignment = HorizontalAlignment.Center; b.TextAlignment = TextAlignment.Center;
        b.Foreground = status == "bad" ? B("LgWarn") : Ink("LvText", "LgInk");
        Uia(b, "dl.empty.big", big);
        u.Children.Add(b);
        if (sub != null) {
            var s = T(12); s.Text = sub; s.TextWrapping = TextWrapping.Wrap; s.TextAlignment = TextAlignment.Center; s.Foreground = Ink("LvText2", "LgInk2");
            Uia(s, "dl.empty.sub", sub);
            u.Children.Add(s);
        }
        return u;
    }

    TextBlock Line(string text, string id)
    {
        var t = T(11); t.Text = text; t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; t.Foreground = Ink("LvText3", "LgInk3"); t.Margin = new Thickness(0, 6, 0, 0);
        t.IsTextSelectionEnabled = id == "dl.path";
        Uia(t, id, text);
        return t;
    }

    /// One row's parts, made once per item and filled on every paint (Fill), so a repaint whose items did not change
    /// (a press makes its row busy, an answer clears it and sets the note, the 30 s ages) keeps the very Button that has
    /// the keyboard: rebuilt rows dropped it, and a quick second Enter reached no row (runs 43854350493, 43857488257).
    sealed class RowParts
    {
        public string Item = "", Shown = "";
        public bool Hover;
        public readonly Grid Cell = new() { Height = PopupLevitate.RowHeight };
        public Button Row = null!, Show = null!;
        public Border Tile = null!;
        public FontIcon Glyph = null!;
        public TextBlock NameT = null!, MetaT = null!;
        public Microsoft.UI.Xaml.Shapes.Ellipse Dot = null!;
    }

    RowParts MakeRow()
    {
        var p = new RowParts();
        // a row (panels/downloads.css .dl-row): 40 tall, padding 0 4 0 12, its parts 12 apart, radius 8, fill-1 on hover and
        // fill-press pressed; the right padding leaves the Show in folder button (28) and its 12 of air clear
        var row = new Button {
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(PopupLevitate.RowPadLeft, 0, PopupLevitate.RowPadRight + PopupLevitate.TileSize + PopupLevitate.RowGap, 0),
            Background = new SolidColorBrush(), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(PopupLevitate.RowRadius), CanDrag = true,
        };
        Fills(row, _fill1, _press);
        var g = new Grid { ColumnSpacing = PopupLevitate.RowGap };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PopupLevitate.TileSize) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        p.Glyph = new FontIcon { FontSize = 16 };
        p.Tile = new Border { Width = PopupLevitate.TileSize, Height = PopupLevitate.TileSize, Child = p.Glyph };
        g.Children.Add(p.Tile);
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(txt, 1);
        p.NameT = T(13); p.NameT.FontWeight = FontWeights.Medium; p.NameT.TextTrimming = TextTrimming.CharacterEllipsis;
        p.MetaT = T(11);
        txt.Children.Add(p.NameT); txt.Children.Add(p.MetaT);
        g.Children.Add(txt);
        p.Dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-PopupLevitate.RowPadLeft + 4, 0, 0, 0) };   // 4 from the row's left edge
        g.Children.Add(p.Dot);
        row.Content = g;
        row.Click += (_, _) => Act?.Invoke("open", p.Item, "Could not open " + p.Shown);
        if (Drag != null) row.DragStarting += (s, e) => { e.AllowedOperations = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; DragData(e, p.Item, p.Shown); };
        p.Cell.Children.Add(row);
        p.Row = row;

        var show = new Button {
            Width = PopupLevitate.TileSize, Height = PopupLevitate.TileSize, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, PopupLevitate.RowPadRight, 0), Background = new SolidColorBrush(), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(LevitateTokens.RadiusCtlL), Content = new FontIcon { Glyph = "", FontSize = 14 }, Opacity = 0,
        };
        Fills(show, _fill2, _press);
        ToolTipService.SetToolTip(show, "Show in folder");
        show.Click += (_, _) => Act?.Invoke("reveal", p.Item, $"Could not show {p.Shown} in its folder");
        // there on hover and keyboard focus, out of the way otherwise (.dl-row:hover / :focus-within .dl-reveal); clear, it
        // is still hit-testable and always reachable by Tab and by UI Automation
        void Seen() => show.Opacity = p.Hover || show.FocusState != FocusState.Unfocused || row.FocusState != FocusState.Unfocused ? 1 : 0;
        p.Cell.PointerEntered += (_, _) => { p.Hover = true; Seen(); };
        p.Cell.PointerExited += (_, _) => { p.Hover = false; Seen(); };
        show.GotFocus += (_, _) => Seen();
        show.LostFocus += (_, _) => Seen();
        row.GotFocus += (_, _) => Seen();
        row.LostFocus += (_, _) => Seen();
        p.Cell.Children.Add(show);
        p.Show = show;
        return p;
    }

    /// Everything a row shows from its item, the theme and the busy / new sets; in place, never a new element.
    void Fill(RowParts p, JsonElement i, int n, long nowMs)
    {
        string item = Str(i, "item") ?? "";
        string name = Str(i, "name") ?? "";
        bool dir = i.TryGetProperty("dir", out var dv) && dv.ValueKind == JsonValueKind.True;
        var si = new ShelfItem(name, dir, Long(i, "size"), Long(i, "at"), false);
        string nm = DownloadsList.Shown(name), meta = DownloadsList.Meta(si, nowMs), kind = DownloadsList.KindOf(name, dir);
        bool fresh = WasFresh.Contains(item), busy = Busy.Contains(item);
        p.Item = item; p.Shown = nm;

        var row = p.Row;
        row.Tag = item; row.Opacity = busy ? .5 : 1; row.IsHitTestVisible = !busy;
        p.Tile.CornerRadius = new CornerRadius(PopupLevitate.TileRadius); p.Tile.Background = Ink("LvCtlFillHover", "LgTile");
        p.Glyph.Glyph = KindGlyph[kind]; p.Glyph.Foreground = Ink("LvText2", "LgInk2");
        p.NameT.Text = nm; p.NameT.Foreground = Ink("LvText", "LgInk");
        p.MetaT.Text = meta; p.MetaT.Foreground = Ink("LvText2", "LgInk3");
        p.Dot.Fill = B("LgAcc"); p.Dot.Visibility = fresh ? Visibility.Visible : Visibility.Collapsed;
        string label = $"{nm}, {(fresh ? "new, " : "")}{KindWord[kind]}, {meta}";
        string tip = nm + "\n" + (dir ? "Click to open the folder" : "Click to open") + ", or drag it into an app or onto the desktop";
        Uia(row, $"dl.row.{n}", label);
        AutomationProperties.SetHelpText(row, tip);
        AutomationProperties.SetItemStatus(row, (fresh ? "fresh" : "") + (busy ? (fresh ? "; " : "") + "busy" : ""));
        ToolTipService.SetToolTip(row, tip);

        var show = p.Show;
        show.Tag = "r:" + item; show.IsHitTestVisible = !busy;
        ((FontIcon)show.Content).Foreground = Ink("LvText2", "LgInk2");
        Uia(show, $"dl.reveal.{n}", "Show in folder");
    }

    /// The real Windows file drag: the bar is asked for the row's file (downloads.drag), then the data package carries
    /// it as a storage item, so it drops on a browser upload, a chat or the desktop.
    void DragData(DragStartingEventArgs e, string item, string nm)
    {
        var def = e.GetDeferral();
        _pendingDrag = (e, def);
        Drag?.Invoke(item, nm);
    }
    (DragStartingEventArgs E, DragOperationDeferral D)? _pendingDrag;

    /// The bar's answer to <see cref="Drag"/>: the file to drag, or the words why it cannot.
    public async void DragAnswer(string? path, string? why)
    {
        if (_pendingDrag is not { } p) return;
        _pendingDrag = null;
        try {
            if (path != null) {
                global::Windows.Storage.IStorageItem si = System.IO.Directory.Exists(path)
                    ? await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(path)
                    : await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                p.E.Data.SetStorageItems(new[] { si });
            } else { p.E.Cancel = true; Note = why ?? ""; }
        } catch (Exception ex) { p.E.Cancel = true; Note = "the drag could not start: " + ex.GetType().Name; _log("downloads: the drag could not start (" + ex.GetType().Name + ")"); }
        finally { p.D.Complete(); }
    }

    /// The height the panel needs (DIP), measured at its width.
    public double Need()
    {
        Root.Measure(new global::Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        return Math.Ceiling(Root.DesiredSize.Height);
    }

    static TextBlock T(double size) => new() { FontSize = size, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI Variable, Segoe UI") };
    static void Uia(FrameworkElement e, string id, string name) { AutomationProperties.SetAutomationId(e, id); AutomationProperties.SetName(e, name); }
    static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    static long Long(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    /// The levitated look's ink (Theme/Levitate.xaml: the look is dark, one neutral family) in dark; in light today's --lg-*
    /// token, since the light Levitate look is not built yet (the host lays no film in light either). The accent stays the
    /// steel --lg-acc in both (the "islands" accent profile is a later job).
    Brush Ink(string lv, string lg)
    {
        if (!_dark) return B(lg);
        if (Application.Current.Resources.TryGetValue(lv, out var v) && v is Brush b) return b;
        if (_missing.Add(lv)) _log($"downloads: the levitated token {lv} is missing from Theme/Levitate.xaml, so {lg} is used");
        return B(lg);
    }

    static global::Windows.UI.Color Color(Brush b) => b is SolidColorBrush s ? s.Color : default;

    readonly HashSet<string> _missing = new();
    /// The --lg-* token of the panel's theme (Theme/Tokens.xaml), read in code so a repaint follows the bar's theme.
    Brush B(string key)
    {
        string theme = _dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (Find(d, key, theme) is Brush b) return b;
        if (_missing.Add(key)) _log($"downloads: the theme token {key} is missing from Theme/Tokens.xaml, so that part is drawn clear");
        return new SolidColorBrush();
    }
    static object? Find(ResourceDictionary d, string key, string theme)
    {
        if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v)) return v;
        foreach (var m in d.MergedDictionaries) if (Find(m, key, theme) is { } x) return x;
        return null;
    }
}
