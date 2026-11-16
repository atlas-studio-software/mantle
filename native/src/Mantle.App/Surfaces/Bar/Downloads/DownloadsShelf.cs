using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mantle.Core.Features.Downloads;
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
    public const double WidthDip = 340;
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
    readonly Button _folder = new() { HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 4, 8, 4) };
    readonly Border _foot = new() { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(2, 6, 2, 0) };
    readonly List<Button> _rows = new();
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
        Root.Padding = new Thickness(8, 11, 8, 8);
        Root.Children.Add(_stack);
        var head = new Grid { Padding = new Thickness(6, 0, 6, 4) };
        var h = T(14); h.Text = "Downloads"; h.FontWeight = FontWeights.SemiBold;
        Uia(h, "dl.title", "Downloads");
        _sub.HorizontalAlignment = HorizontalAlignment.Right; _sub.VerticalAlignment = VerticalAlignment.Bottom;
        Uia(_sub, "dl.sub", "");
        head.Children.Add(h); head.Children.Add(_sub);
        _stack.Children.Add(head);
        _scroll.Content = _list;
        Uia(_scroll, "dl.list", "Downloads list");
        _stack.Children.Add(_scroll);
        _note.Margin = new Thickness(6, 6, 6, 0); _note.TextWrapping = TextWrapping.Wrap;
        Uia(_note, "dl.note", "");
        _stack.Children.Add(_note);
        var foot = new StackPanel { Spacing = 4 };
        _detail.TextWrapping = TextWrapping.Wrap; _detail.Padding = new Thickness(4, 0, 4, 0);
        Uia(_detail, "dl.detail", "");
        foot.Children.Add(_detail);
        var fl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        fl.Children.Add(new FontIcon { Glyph = KindGlyph["folder"], FontSize = 13 });
        fl.Children.Add(new TextBlock { Text = "Open Downloads folder", FontSize = 12 });
        _folder.Content = fl;
        Uia(_folder, "dl.folder", "Open Downloads folder");
        _folder.Click += (_, _) => Act?.Invoke("folder", null, "Could not open the Downloads folder");
        foot.Children.Add(_folder);
        _foot.Child = foot;
        _stack.Children.Add(_foot);
    }

    /// The rows in order (the keyboard walks them).
    public IReadOnlyList<Button> Rows => _rows;

    /// paint(): rebuild from the view and the push; keeps keyboard focus on the same row and the scroll position.
    public void Paint(bool dark, long nowMs)
    {
        _dark = dark;
        var v = View;
        bool has = v.ValueKind == JsonValueKind.Object;
        var items = has && v.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array ? it.EnumerateArray().ToList() : new List<JsonElement>();
        bool bad = has && v.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False;
        string state = Str(v, "state") ?? "";
        int partial = Int(v, "partial") is int p and > 0 ? p : Int(State, "partial") ?? 0;
        _sub.Text = partial > 0 ? $"{partial} still downloading…" : "";
        _sub.Visibility = partial > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_sub, _sub.Text);
        _sub.Foreground = B("LgInk3");

        bool noFolder = state is "missing" or "unresolved";
        string? folder = Str(v, "folder");
        string tip = noFolder ? "There is no Downloads folder to open right now" : folder != null ? $"Open {folder} in File Explorer" : "Open your Downloads folder in File Explorer";
        ToolTipService.SetToolTip(_folder, tip);
        AutomationProperties.SetHelpText(_folder, tip);
        _folder.IsEnabled = !Busy.Contains("*") && !noFolder && state != "resting" && !(has && folder == null && !bad);
        _folder.Foreground = B(_folder.IsEnabled ? "LgInk2" : "LgInk3");
        _folder.Background = new SolidColorBrush();
        _folder.BorderThickness = new Thickness(0);
        AutomationProperties.SetHelpText(_scroll, KbdNote);
        ToolTipService.SetToolTip(_scroll, KbdNote.Length > 0 ? KbdNote : null);

        string? focusItem = null; bool focusReveal = false;
        var fe = Root.XamlRoot != null ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as FrameworkElement : null;
        if (fe?.Tag is string tag) { focusItem = tag.TrimStart('r', ':'); focusReveal = tag.StartsWith("r:", StringComparison.Ordinal); }
        double scrollTop = _scroll.VerticalOffset;
        _list.Children.Clear(); _rows.Clear();

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
        else {
            int n = 0;
            foreach (var i in items) _list.Children.Add(Row(i, n++, nowMs));
            if (focusItem != null && _rows.FirstOrDefault(r => (r.Tag as string) == focusItem) is { } back) {
                if (focusReveal && back.Parent is Grid g && g.Children.OfType<Button>().LastOrDefault() is { } rv && rv != back) rv.Focus(FocusState.Keyboard);
                else back.Focus(FocusState.Keyboard);
            }
        }
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, scrollTop, null, true);

        string say = Note.Length > 0 ? Note : Str(v, "seenNote") ?? "";
        _note.Text = say.Length > 0 ? DownloadsList.Sentence(say) : "";
        _note.Visibility = say.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _note.Foreground = B("LgInk2");
        AutomationProperties.SetName(_note, _note.Text);
        var words = new List<string>();
        if (Str(v, "detail") is { Length: > 0 } d) words.Add(d);
        if (!bad && Str(v, "watchReason") is { Length: > 0 } wr) words.Add(DownloadsList.Sentence(wr));
        _detail.Text = string.Join(" ", words);
        _detail.Visibility = words.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _detail.Foreground = B("LgInk3");
        AutomationProperties.SetName(_detail, _detail.Text);
        _foot.BorderBrush = B("LgDivider");
        Root.Background = null;
    }

    StackPanel Empty(string big, string? sub, string status)
    {
        var u = new StackPanel { Padding = new Thickness(8, 18, 8, 16) };
        Uia(u, "dl.empty", big);
        AutomationProperties.SetItemStatus(u, status);
        var b = T(13.5); b.Text = big; b.HorizontalAlignment = HorizontalAlignment.Center; b.TextAlignment = TextAlignment.Center;
        b.Foreground = B(status == "bad" ? "LgWarn" : "LgInk");
        Uia(b, "dl.empty.big", big);
        u.Children.Add(b);
        if (sub != null) {
            var s = T(12); s.Text = sub; s.TextWrapping = TextWrapping.Wrap; s.TextAlignment = TextAlignment.Center; s.Foreground = B("LgInk2"); s.Margin = new Thickness(0, 4, 0, 0);
            Uia(s, "dl.empty.sub", sub);
            u.Children.Add(s);
        }
        return u;
    }

    TextBlock Line(string text, string id)
    {
        var t = T(11); t.Text = text; t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Center; t.Foreground = B("LgInk3"); t.Margin = new Thickness(0, 6, 0, 0);
        t.IsTextSelectionEnabled = id == "dl.path";
        Uia(t, id, text);
        return t;
    }

    Grid Row(JsonElement i, int n, long nowMs)
    {
        string item = Str(i, "item") ?? "";
        string name = Str(i, "name") ?? "";
        bool dir = i.TryGetProperty("dir", out var dv) && dv.ValueKind == JsonValueKind.True;
        var si = new ShelfItem(name, dir, Long(i, "size"), Long(i, "at"), false);
        string nm = DownloadsList.Shown(name), meta = DownloadsList.Meta(si, nowMs), kind = DownloadsList.KindOf(name, dir);
        bool fresh = WasFresh.Contains(item), busy = Busy.Contains(item);

        var cell = new Grid { MinHeight = 42 };
        var row = new Button {
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 5, 36, 5),
            Background = new SolidColorBrush(), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(8),
            Tag = item, CanDrag = true, Opacity = busy ? .5 : 1, IsHitTestVisible = !busy,
        };
        var g = new Grid { ColumnSpacing = 10 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var tile = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(_dark ? 8 : 7), Background = B("LgTile"),
            Child = new FontIcon { Glyph = KindGlyph[kind], FontSize = 15, Foreground = B("LgInk2") } };
        g.Children.Add(tile);
        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(txt, 1);
        var nameT = T(12.5); nameT.Text = nm; nameT.FontWeight = FontWeights.Medium; nameT.TextTrimming = TextTrimming.CharacterEllipsis; nameT.Foreground = B("LgInk");
        var metaT = T(11); metaT.Text = meta; metaT.Foreground = B("LgInk3"); metaT.Margin = new Thickness(0, 1, 0, 0);
        txt.Children.Add(nameT); txt.Children.Add(metaT);
        g.Children.Add(txt);
        if (fresh) {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Fill = B("LgAcc"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-7, 0, 0, 0) };
            g.Children.Add(dot);
        }
        row.Content = g;
        string label = $"{nm}, {(fresh ? "new, " : "")}{KindWord[kind]}, {meta}";
        string tip = nm + "\n" + (dir ? "Click to open the folder" : "Click to open") + ", or drag it into an app or onto the desktop";
        Uia(row, $"dl.row.{n}", label);
        AutomationProperties.SetHelpText(row, tip);
        AutomationProperties.SetItemStatus(row, (fresh ? "fresh" : "") + (busy ? (fresh ? "; " : "") + "busy" : ""));
        ToolTipService.SetToolTip(row, tip);
        row.Click += (_, _) => Act?.Invoke("open", item, "Could not open " + nm);
        if (Drag != null) row.DragStarting += (s, e) => { e.AllowedOperations = global::Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy; DragData(e, item, nm); };
        cell.Children.Add(row);
        _rows.Add(row);

        var show = new Button {
            Width = 28, Height = 28, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0), Background = new SolidColorBrush(), BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 13, Foreground = B("LgInk2") }, Tag = "r:" + item, Opacity = .55, IsHitTestVisible = !busy,
        };
        Uia(show, $"dl.reveal.{n}", "Show in folder");
        ToolTipService.SetToolTip(show, "Show in folder");
        show.Click += (_, _) => Act?.Invoke("reveal", item, $"Could not show {nm} in its folder");
        // shown on hover or focus (.dl-row:hover .dl-reveal); always reachable by Tab and by UI Automation
        cell.PointerEntered += (_, _) => show.Opacity = 1;
        cell.PointerExited += (_, _) => show.Opacity = .55;
        show.GotFocus += (_, _) => show.Opacity = 1;
        cell.Children.Add(show);
        return cell;
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
