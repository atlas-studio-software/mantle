using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mantle.App.Surfaces.Bar.Tasks;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mantle.App;

/// <summary>
/// One cell of the strip (ui/bar/bar.js makeCell / updateCell): the app's icon or its letter tile, the running line
/// (6x3 ink-2 while it has windows, 16x3 accent when one is the active window), and the hairline divider before the
/// first running-only app. UI Automation (AUTOMATION.md): bar.app.&lt;n&gt; in display order; Name = the app's window
/// title (its window in front, else its first; a pin with no window: its name); HelpText = the exe's file name.
/// </summary>
public sealed partial class CellVm : INotifyPropertyChanged
{
    internal TaskItem Item;
    internal CellVm(TaskItem item) { Item = item; Update(item, false); }

    public string Name { get; private set; } = "";
    public string Letter { get; private set; } = "?";
    public string ExeName { get; private set; } = "";
    string _tip = "";
    bool _flying;
    /// The tooltip; none while this app's previews are up (bar.js flyBuild: no tooltip window over the pictures).
    public string? Tip => _flying ? null : _tip;
    internal bool Flying { set { if (_flying == value) return; _flying = value; Changed(nameof(Tip)); } }
    string _title = "";
    public string Title => _title;
    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { if (_icon == value) return; _icon = value; Changed(nameof(Icon)); Changed(nameof(IconVisibility)); Changed(nameof(TileVisibility)); } }
    internal string? IconUrl;
    public Visibility IconVisibility => _icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TileVisibility => _icon == null ? Visibility.Visible : Visibility.Collapsed;
    bool _run, _act, _div;
    public bool Active => _act;
    public Visibility RunVisibility => _run && !_act ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActVisibility => _act ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DividerVisibility => _div ? Visibility.Visible : Visibility.Collapsed;
    internal bool Divider { set { if (_div == value) return; _div = value; Changed(nameof(DividerVisibility)); SetStatus(true); } }
    /// UI Automation ItemStatus (the UI tests read it): the kind ("pinned" / "running"), then "open" while it has
    /// windows, "active" while one is the active window (the accent line), "after divider" on the first running-only
    /// app, "N windows" with two or more (a click then opens the window list). The same fields drive the running line
    /// and the hairline, so the status says what the cell draws.
    public string Status { get; private set; } = "";
    void SetStatus(bool notify)
    {
        int n = Item.Windows.Count;
        string s = Item.Kind + (_run ? "; open" : "") + (_act ? "; active" : "") + (_div ? "; after divider" : "") + (n >= 2 ? "; " + n + " windows" : "");
        if (s == Status) return;
        Status = s;
        if (notify) Changed(nameof(Status));
    }
    int _index = -1;
    public int Index { get => _index; set { if (_index == value) return; _index = value; Changed(nameof(AutomationId)); } }
    public string AutomationId => "bar.app." + _index.ToString(CultureInfo.InvariantCulture);

    /// bar.js letterOf: the first letter or digit, upper case.
    internal static string LetterOf(string name)
    {
        foreach (var r in name.EnumerateRunes()) if (System.Text.Rune.IsLetterOrDigit(r)) return r.ToString().ToUpperInvariant();
        return "?";
    }

    internal void Update(TaskItem it, bool notify = true)
    {
        Item = it;
        string name = it.Name.Length > 0 ? it.Name : it.Exe ?? it.Id;
        var front = it.Windows.FirstOrDefault(RunningWindows.IsActive) ?? it.Windows.FirstOrDefault();
        string title = front != null && front.Title.Length > 0 ? front.Title : name;
        // the tooltip (bar.js updateCell): "<name> — N windows", why an icon is missing, what middle-click does
        string tip = name + (it.Windows.Count > 0 ? " — " + it.Windows.Count + (it.Windows.Count == 1 ? " window" : " windows") : "");
        if (it.Icon == null) tip += "\n(no icon: " + (it.IconReason ?? "Windows gave no reason") + ")";
        if (it.Kind == "pinned") tip += "\nMiddle-click for a new window";
        bool run = it.Windows.Count > 0, act = run && it.Active;
        string exeName = it.Exe != null ? Path.GetFileName(it.Exe) : "";
        Set(ref _title, title, nameof(Title), notify);
        if (Name != name) { Name = name; Letter = LetterOf(name); if (notify) { Changed(nameof(Name)); Changed(nameof(Letter)); } }
        if (_tip != tip) { _tip = tip; if (notify) Changed(nameof(Tip)); }
        if (ExeName != exeName) { ExeName = exeName; if (notify) Changed(nameof(ExeName)); }
        if (_run != run || _act != act) { _run = run; _act = act; if (notify) { Changed(nameof(RunVisibility)); Changed(nameof(ActVisibility)); } }
        SetStatus(notify);
    }

    void Set(ref string field, string v, string prop, bool notify) { if (field == v) return; field = v; if (notify) Changed(prop); }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed partial class BarWindow
{
    BarTasksHost? _tasks;
    string? _testOrder;
    int _syncs;
    bool _cellsDirty;
    readonly Dictionary<string, ImageSource?> _iconBitmaps = new(StringComparer.Ordinal);

    /// The strip's host side; App.xaml.cs HostStart attaches it to the platform on the host thread.
    internal BarTasksHost? Tasks => _tasks;

    /// Made in the constructor (before StartHost runs, so HostStart finds it).
    void StartTasks()
    {
        _tasks = new BarTasksHost(DispatcherQueue, Host.Log.Line);
        _tasks.Changed += SyncCells;
        // cells.json once the strip is laid out: a cell inserted or moved has no container until the next layout
        // pass, and an icon lands after its sync (it decodes asynchronously)
        Dock.LayoutUpdated += (_, _) => { if (_cellsDirty && _benchOut != null) WriteCells(); };
        if (Host.Args.Test) StartUiBeat();
    }

    void StopTasks() { FlyTeardown("the bar is closing"); _beat?.Dispose(); _tasks?.Dispose(); }

    // ------------------------------------------------------------------ TEST RUNS ONLY: what the strip's clicks met
    // A queued ping every 250 ms: a UI thread that runs it late is said with how late (a stalled thread drops no click,
    // it holds it). With the press and click lines below, a click a test made that "never reached the app" can be told
    // apart: no press line = it landed elsewhere; a press but no click = the cell changed under it; late pings = stall.
    System.Threading.Timer? _beat;
    long _beatQueued;
    bool _beatSaid;

    void StartUiBeat()
    {
        var q = DispatcherQueue;
        _beat = new System.Threading.Timer(_ => {
            long now = Environment.TickCount64, was = System.Threading.Interlocked.Read(ref _beatQueued);
            if (was != 0) {
                if (now - was >= 2000 && !_beatSaid) { _beatSaid = true; Host.Log.Line($"bar tasks: test: the UI thread has not run queued work for {now - was} ms"); }
                return;
            }
            System.Threading.Interlocked.Exchange(ref _beatQueued, now);
            q.TryEnqueue(() => {
                long late = Environment.TickCount64 - System.Threading.Interlocked.Exchange(ref _beatQueued, 0);
                _beatSaid = false;
                if (late >= 500) Host.Log.Line($"bar tasks: test: the UI thread ran queued work {late} ms late");
            });
        }, null, 250, 250);
    }

    void TestLine(string what, object sender)
    {
        if (!Host.Args.Test || sender is not Button b || b.Tag is not CellVm vm) return;
        Host.Log.Line($"bar tasks: test: {what} on {vm.AutomationId} ({vm.Title})");
    }

    // ------------------------------------------------------------------ the strip (bar.js renderDock)
    void SyncCells()
    {
        if (_tasks == null) return;
        var list = _tasks.Last;
        RenderStripProblem(list, _tasks.WindowsProblem);
        var want = new List<TaskItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var it in list.Items) if (seen.Add(it.Id)) want.Add(it);   // one cell per id (bar.js disambiguates; ids are unique here)
        var have = new Dictionary<string, CellVm>(StringComparer.Ordinal);
        foreach (var c in _cells) have[c.Item.Id] = c;
        for (int i = _cells.Count - 1; i >= 0; i--) if (!seen.Contains(_cells[i].Item.Id)) _cells.RemoveAt(i);
        bool divPlaced = false;
        for (int i = 0; i < want.Count; i++) {
            var it = want[i];
            if (!have.TryGetValue(it.Id, out var vm)) { vm = new CellVm(it); _cells.Insert(Math.Min(i, _cells.Count), vm); }
            else {
                vm.Update(it);
                int at = _cells.IndexOf(vm);
                if (at != i) _cells.Move(at, i);
            }
            // running-but-unpinned apps sit after a hairline, like Windows' (bar.js divEl)
            bool div = it.Kind == "running" && !divPlaced && i > 0;
            if (it.Kind == "running") divPlaced = true;
            vm.Divider = div;
            SetIcon(vm, it.Icon);
        }
        for (int i = 0; i < _cells.Count; i++) _cells[i].Index = i;   // bar.app.<n>, in display order
        if (Host.Args.Test) {
            string order = string.Join(" | ", _cells.Select(c => c.Title));
            if (order != _testOrder) { _testOrder = order; Host.Log.Line($"bar tasks: test: strip now [{order}]"); }
        }
        FlyRefresh(list);   // the open flyout follows the push (bar.js flyRefresh)
        _syncs++;
        _cellsDirty = true;
        if (_benchOut != null) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, WriteCells);
    }

    /// bar.js renderDock: partial is not off (render what arrived, say what is missing); nothing at all is a warn chip.
    void RenderStripProblem(TaskList list, string? windowsProblem)
    {
        bool changed;
        if (!list.Ok && list.Items.Count > 0) changed = _chips.Fail("pinned", "Some apps still loading", list.Reason ?? "part of the app list could not be read yet; the rest is shown");
        else if (!list.Ok) changed = _chips.Fail("pinned", "Apps not shown", list.Reason ?? "the app list could not be read, and no reason was given", "warn");
        else changed = _chips.Clear("pinned");
        changed |= windowsProblem != null ? _chips.Fail("windows", "Open windows not updating", windowsProblem, "warn") : _chips.Clear("windows");
        if (changed) RenderPip(force: true);
    }

    /// A data URL decoded once per distinct picture; a broken one falls back to the letter tile and says so.
    async void SetIcon(CellVm vm, string? url)
    {
        if (vm.IconUrl == url) return;
        vm.IconUrl = url;
        if (url == null) { vm.Icon = null; return; }
        if (_iconBitmaps.TryGetValue(url, out var known)) { vm.Icon = known; _cellsDirty = true; return; }
        ImageSource? img = null;
        try {
            int comma = url.IndexOf(',');
            byte[] bytes = Convert.FromBase64String(comma >= 0 ? url[(comma + 1)..] : url);
            using var ms = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var w = new global::Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(bytes); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream(); }
            ms.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms);
            img = bmp;
        } catch (Exception e) {
            Host.Log.Line($"bar tasks: an icon failed to decode ({e.Message}); the letter tile shows instead");
        }
        if (_iconBitmaps.Count > 256) _iconBitmaps.Clear();
        _iconBitmaps[url] = img;
        if (vm.IconUrl == url) { vm.Icon = img; _cellsDirty = true; }
    }

    void Cell_Click(object sender, RoutedEventArgs e)
    {
        TestLine("click", sender);
        if (sender is not Button b || b.Tag is not CellVm vm) return;
        if (FlyClick(vm)) return;   // the flyout answered it (bar.js el.dock click)
        OpenCell(vm, fresh: false, b);
    }

    PointerEventHandler? _cellMiddle;

    /// The middle-click handler goes on with handledEventsToo: a Button may mark the pointer events it sees handled,
    /// and a XAML PointerReleased attribute would then never hear the middle button. Remove-then-add keeps it to one
    /// handler however often a recycled cell is loaded again.
    void Cell_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        _cellMiddle ??= new PointerEventHandler(Cell_PointerReleased);
        b.RemoveHandler(UIElement.PointerReleasedEvent, _cellMiddle);
        b.AddHandler(UIElement.PointerReleasedEvent, _cellMiddle, true);
        HookCellHover(b);   // the preview flyout's dwell and leave (BarWindow.Previews.cs)
        if (Host.Args.Test) {
            _cellPress ??= new PointerEventHandler((o, _) => TestLine("press", o));
            b.RemoveHandler(UIElement.PointerPressedEvent, _cellPress);
            b.AddHandler(UIElement.PointerPressedEvent, _cellPress, true);
        }
    }

    PointerEventHandler? _cellPress;

    /// Middle-click: a new window or instance (bar.open {fresh:true}, BAR-200, BAR-206).
    void Cell_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not CellVm vm) return;
        var pt = e.GetCurrentPoint(b);
        if (pt.Properties.PointerUpdateKind != PointerUpdateKind.MiddleButtonReleased) return;
        e.Handled = true;
        OpenCell(vm, fresh: true);
    }

    void OpenCell(CellVm vm, bool fresh, FrameworkElement? anchor = null)
    {
        if (_tasks == null) return;
        string id = vm.Item.Id;
        _tasks.Open(id, fresh, r => {
            Host.Log.Line($"bar tasks: click ({vm.Item.Kind}{(fresh ? ", fresh" : "")}): {(r.Ok ? "ok" : "failed")} {r.Action ?? ""}{(r.Reason != null ? ": " + r.Reason : "")}");
            // a failed open is a warn chip until the next success (bar.js act, ACT_LABEL; BAR-195)
            bool changed = r.Ok ? _chips.Clear("open") : _chips.Fail("open", "App did not open", r.Reason ?? "Mantle did not say why", "warn");
            if (changed) RenderPip(force: true);
            // two or more windows: none touched, the previews over the icon (BAR-199, BAR-221; the list if too many)
            if (r.Ok && r.Action == "list" && anchor != null) OpenFly(vm, anchor, "click");
        });
    }

    void WriteCells()
    {
        if (!_cellsDirty) return;
        var list = new List<CellJson>();
        for (int i = 0; i < _cells.Count; i++) {
            if (Dock.ContainerFromIndex(i) is not FrameworkElement fe) return;   // not laid out yet: LayoutUpdated comes back
            var btn = FindButton(fe) ?? fe;
            var p = btn.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
            int x = _winPx.X + (int)Math.Round(p.X * _scale), y = _winPx.Y + (int)Math.Round(p.Y * _scale);
            var c = _cells[i];
            list.Add(new CellJson(c.Name, c.Title, c.AutomationId, c.Item.Exe ?? "", (uint)(c.Item.Windows.FirstOrDefault()?.Pid ?? 0), c.Active, c.Icon != null,
                x, y, (int)Math.Round(btn.ActualWidth * _scale), (int)Math.Round(btn.ActualHeight * _scale), c.Status));
        }
        _cellsDirty = false;
        Write("cells.json", JsonSerializer.Serialize(new CellsJson(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _syncs, list.ToArray()), BenchJson.Default.CellsJson));
    }

    static FrameworkElement? FindButton(DependencyObject d)
    {
        if (d is Button b) return b;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++) if (FindButton(VisualTreeHelper.GetChild(d, i)) is FrameworkElement f) return f;
        return null;
    }
}
