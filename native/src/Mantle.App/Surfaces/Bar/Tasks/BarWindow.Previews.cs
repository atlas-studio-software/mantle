using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.Tasks;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Mantle.App;

/// <summary>
/// The window flyout with live previews (ui/bar/bar.js THE WINDOW FLYOUT; BAR-221..233, BAR-308..314). One pop-up,
/// three ways in, as in Electron: resting 400 ms on a running app's icon (closes 350 ms after the pointer leaves the
/// icon and the flyout), a left click when bar.open answers "list" (two or more windows; stays until a click
/// elsewhere or Esc), and the test hook. One card per window: its icon, title (prefixed "Minimized · " / "Still open
/// · "), the X, and a box DWM draws the live window into (Thumbs.cs, onto the pop-up's own HWND); the active
/// window's card carries the 16x3 accent line. A box with no picture says why in words. More windows than fit as
/// 128 DIP cards: the window list (BarWindow.WindowList.cs) with that reason on top. UI Automation: `bar.preview`,
/// cards `bar.preview.&lt;n&gt;` (ItemStatus "active" / "minimized" / "closing" / "still open"), their X
/// `bar.preview.&lt;n&gt;.close`, the box's words `bar.preview.&lt;n&gt;.shot` (ItemStatus "asking" / "live; source WxH;
/// screen L,T,R,B" / "fail" / "gone" / "held").
/// </summary>
public sealed partial class BarWindow
{
    sealed class FlyCard
    {
        public TaskWindow W = null!;
        public int N;
        public Button Open = null!, X = null!;
        public TextBlock Title = null!, Why = null!;
        public Run Prefix = null!, Text = null!;
        public Rectangle Line = null!;
        public Grid Root = null!;
        public bool Closing, StillOpen, Held, Gone, Live;
    }

    sealed class Fly
    {
        public string Key = "", AppId = "", Name = "", Sig = "", By = "hover";
        public CellVm Cell = null!;
        public FrameworkElement Anchor = null!;
        public int Gen;
        public bool List, Ready;
        public FrameworkElement? Body;
        public IntPtr Dest;
        public readonly List<FlyCard> Cards = new();
    }

    Fly? _fly;
    int _flyGen, _flyAlt;
    CellVm? _flyHover, _noFly;
    DispatcherQueueTimer? _flyDwell, _flyLeave, _flyWatch, _flyRebuild, _flyReady;
    Thumbs? _thumbs;
    string? _testFlyout;
    bool _testFlyoutDone;

    bool FlyUp => Popups?.OpenKey is string k && _fly != null && k == _fly.Key;

    /// A one-shot timer in a slot: a new one replaces (stops) the one before, so a slot never fires twice.
    void Timer(ref DispatcherQueueTimer? t, int ms, Action tick)
    {
        t?.Stop();
        var me = DispatcherQueue.CreateTimer();
        me.Interval = TimeSpan.FromMilliseconds(ms);
        me.IsRepeating = false;
        me.Tick += (s, _) => { s.Stop(); tick(); };
        t = me;
        me.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out Native.Win32.RECT r);

    static void StopTimer(DispatcherQueueTimer? t) => t?.Stop();

    // ------------------------------------------------------------------ the hover (bar.js flyCellEnter / Leave)
    PointerEventHandler? _cellEnter, _cellExit;

    void HookCellHover(Button b)
    {
        _cellEnter ??= new PointerEventHandler((s, e) => { if (s is Button x && x.Tag is CellVm vm) FlyCellEnter(vm, x); });
        _cellExit ??= new PointerEventHandler((s, e) => {
            // PointerExited bubbles up from the cell's own children: only a pointer outside the cell has left it
            if (s is not Button x || x.Tag is not CellVm vm) return;
            var p = e.GetCurrentPoint(x).Position;
            if (p.X >= 0 && p.Y >= 0 && p.X < x.ActualWidth && p.Y < x.ActualHeight) return;
            FlyCellLeave(vm);
        });
        b.RemoveHandler(UIElement.PointerEnteredEvent, _cellEnter);
        b.AddHandler(UIElement.PointerEnteredEvent, _cellEnter, true);
        b.RemoveHandler(UIElement.PointerExitedEvent, _cellExit);
        b.AddHandler(UIElement.PointerExitedEvent, _cellExit, true);
    }

    void FlyCellEnter(CellVm vm, Button b)
    {
        _flyHover = vm;
        StopTimer(_flyLeave);
        StopTimer(_flyDwell);
        bool running = vm.Item.Windows.Count > 0;
        // a flyout is already up: follow the pointer across the icons with no dwell, the way Windows swaps its
        // previews; a pin with no windows starts the same close a leave would
        if (FlyUp) {
            if (_fly!.AppId == vm.Item.Id) return;
            if (running) OpenFly(vm, b, "hover");
            else FlyScheduleLeave();
            return;
        }
        // a pin with no windows gets its tooltip only; a hover never replaces a pop-up the user opened on purpose
        if (!running || Popups?.OpenKey != null || _noFly == vm) return;
        Timer(ref _flyDwell, Previews.DwellMs, () => {
            if (_flyHover == vm && Popups?.OpenKey == null && vm.Item.Windows.Count > 0 && _cells.Contains(vm)) OpenFly(vm, b, "hover");
        });
    }

    void FlyCellLeave(CellVm vm)
    {
        if (_flyHover == vm) _flyHover = null;
        if (_noFly == vm) _noFly = null;
        StopTimer(_flyDwell);
        FlyScheduleLeave();
    }

    void FlyScheduleLeave()
    {
        if (_fly == null || _fly.By != "hover") return;
        Timer(ref _flyLeave, Previews.LeaveMs, () => {
            if (_fly != null && _fly.By == "hover" && FlyUp) Popups?.Close("the pointer left the icon and its previews");
        });
    }

    /// The strip's left click (bar.js el.dock click): true when the flyout already answered it.
    bool FlyClick(CellVm vm)
    {
        _noFly = vm;   // no dwell-opened flyout on the icon just clicked, until the pointer leaves it
        StopTimer(_flyDwell);
        if (!FlyUp || _fly!.AppId != vm.Item.Id) return false;
        bool hover = _fly.By == "hover";
        // the hover flyout of this icon is up: two or more windows is what the click would open, so it just keeps it
        if (hover && vm.Item.Windows.Count >= 2) { _fly.By = "click"; Host.Log.Line($"bar previews: {_fly.Name}: kept open by a click"); return true; }
        Popups?.Close(hover ? "its icon was clicked" : "its button was clicked again");
        FlyTeardown(hover ? "its icon was clicked" : "its button was clicked again");
        return !hover;   // a click-opened one: the click only closed it (its own toggle); one window: the click goes on
    }

    // ------------------------------------------------------------------ open, build (bar.js openFly / flyBuild)
    void OpenFly(CellVm vm, FrameworkElement anchor, string by, bool rebuild = false)
    {
        if (Popups == null) return;
        var it = vm.Item;
        if (it.Windows.Count == 0) return;
        if (!rebuild && FlyUp && _fly!.AppId == it.Id) { if (by == "click") _fly.By = "click"; return; }   // already up: a click only takes ownership
        var g = Geometry;
        double barW = (g.Window.Right - g.Window.Left) / g.Scale;
        var fit = Previews.FitCards(it.Windows.Count, barW);
        string name = it.Name.Length > 0 ? it.Name : it.Exe ?? "windows";
        // a rebuild where it stands takes the other key, so the pop-up host replaces it in place (no exit)
        string key = "app:" + it.Id + (rebuild ? "#" + (++_flyAlt & 1) : "");
        FlyTeardown(rebuild ? "rebuilt" : "another flyout");
        var f = new Fly { Key = key, AppId = it.Id, Name = name, Sig = Previews.Sig(it.Windows), By = by, Cell = vm, Anchor = anchor, Gen = ++_flyGen, List = !fit.Cards };
        _fly = f;
        vm.Flying = true;   // no tooltip over the previews (bar.js flyBuild)
        double w, h;
        Func<UIElement> body;
        if (fit.Cards) {
            (w, h) = Previews.FlyoutSize(it.Windows.Count, fit.ShotW, fit.ShotH);
            body = () => FlyBody(f, vm, fit.ShotW, fit.ShotH);
        } else {
            w = 300; h = Math.Min(11 + 25 + 58 + it.Windows.Count * 32 + 11, 420);
            body = () => FlyHoverRoot(WindowListBody(it, fit.Why!), f);
        }
        _popAnchor = anchor;
        Popups.Open(new PopupKind(key, name + " windows", w, h, Focusable: false, Instant: false, Away: true, body), anchor);
        Host.Log.Line($"bar previews: {name}: {(rebuild ? "rebuilt" : "opened")} by {by}, {(fit.Cards ? $"{it.Windows.Count} card{(it.Windows.Count == 1 ? "" : "s")} of {fit.ShotW}x{fit.ShotH}" : "the list: " + fit.Why)}");
        // every road out (a click outside, Esc, another app in front, the bar going down, another pop-up) closes the
        // pop-up in the host; the pictures come off the moment it is no longer the open one
        Timer(ref _flyWatch, 40, WatchFly);
    }

    void WatchFly()
    {
        if (_fly == null) return;
        if (!FlyUp) { FlyTeardown("the flyout closed"); return; }
        Timer(ref _flyWatch, 40, WatchFly);
    }

    void FlyTeardown(string why)
    {
        StopTimer(_flyDwell); StopTimer(_flyLeave); StopTimer(_flyWatch); StopTimer(_flyRebuild); StopTimer(_flyReady);
        _thumbs?.Clear(why);
        if (_fly != null) _fly.Cell.Flying = false;
        _fly = null;
    }

    UIElement FlyHoverRoot(UIElement inner, Fly f)
    {
        var root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        root.Children.Add(inner);
        root.PointerEntered += (_, _) => { if (_fly == f) StopTimer(_flyLeave); };
        root.PointerExited += (_, e) => {
            var p = e.GetCurrentPoint(root).Position;
            if (p.X >= 0 && p.Y >= 0 && p.X < root.ActualWidth && p.Y < root.ActualHeight) return;   // bubbled from a child
            if (_fly == f) FlyScheduleLeave();
        };
        return root;
    }

    const string CardTemplate =
        "<ControlTemplate " + WlNs + " TargetType='Button'>" +
        "<Grid x:Name='Face' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='CORNER' Padding='{TemplateBinding Padding}'>" +
        "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'>" +
        "<VisualState x:Name='Normal'/>" +
        "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource HOVER}'/>FGHOVER</VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource PRESS}'/>FGHOVER</VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Disabled'><VisualState.Setters><Setter Target='Face.Opacity' Value='0.45'/></VisualState.Setters></VisualState>" +
        "</VisualStateGroup></VisualStateManager.VisualStateGroups>" +
        "<ContentPresenter x:Name='Cp' HorizontalAlignment='Stretch' VerticalAlignment='Stretch' HorizontalContentAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalContentAlignment='{TemplateBinding VerticalContentAlignment}' Foreground='{TemplateBinding Foreground}'/>" +
        "</Grid></ControlTemplate>";

    UIElement FlyBody(Fly f, CellVm vm, double sw, double sh)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Previews.CardGap, Padding = new Thickness(7) };
        AutomationProperties.SetAutomationId(row, "bar.preview");
        AutomationProperties.SetName(row, f.Name + " windows");
        int n = 0;
        foreach (var w in vm.Item.Windows) {
            var c = FlyCardFor(f, vm, w, n++, sw, sh);
            f.Cards.Add(c);
            row.Children.Add(c.Root);
        }
        var root = FlyHoverRoot(row, f);
        f.Body = (FrameworkElement)root;
        // the boxes are where they stay only once the pop-up has risen into place (its 320 ms entrance; the
        // pictures cannot ride the motion, so they land on the resting rects: bar.js popRestRect)
        ((FrameworkElement)root).Loaded += (_, _) => {
            if (_fly != f) return;
            f.Dest = FlyHwnd((FrameworkElement)root, f.Name + " windows");
            int ms = (ReducedMotion ? PopupWindow.ReducedMs : PopupWindow.DurSMs) + 30;
            Timer(ref _flyReady, ms, () => { if (_fly == f) { f.Ready = true; FlySync(); } });
        };
        return root;
    }

    /// The pop-up's own window: from the element's island, else by its title among this process's windows.
    static IntPtr FlyHwnd(FrameworkElement el, string name)
    {
        try {
            var env = el.XamlRoot?.ContentIslandEnvironment;
            if (env != null) {
                var h = Microsoft.UI.Win32Interop.GetWindowFromWindowId(env.AppWindowId);
                if (h != IntPtr.Zero) return h;
            }
        } catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        string want = "Mantle pop-up: " + name;
        IntPtr found = IntPtr.Zero;
        uint me = (uint)Environment.ProcessId;
        Native.Win32.EnumWindows((h, _) => {
            Native.Win32.GetWindowThreadProcessId(h, out uint pid);
            if (pid == me && Native.Win32.WindowText(h) == want) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    FlyCard FlyCardFor(Fly f, CellVm vm, TaskWindow w, int n, double sw, double sh)
    {
        string corner = Dark ? "10" : "8";   // --lg-r per theme
        string tpl = CardTemplate.Replace("CORNER", corner).Replace("HOVER", "LgHover").Replace("PRESS", "LgPress").Replace("FGHOVER", "");
        string xTpl = CardTemplate.Replace("CORNER", "6").Replace("HOVER", "LgWarnSoft").Replace("PRESS", "LgWarnSoft")
            .Replace("FGHOVER", "<Setter Target='Cp.Foreground' Value='{ThemeResource LgWarn}'/>");
        string ico = vm.Icon != null
            ? ""
            : "<Border Width='16' Height='16' CornerRadius='4' Background='{ThemeResource LgTile}'><TextBlock FontSize='10' FontWeight='SemiBold' HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{ThemeResource LgInk}' Text='" + SecurityElement.Escape(vm.Letter) + "'/></Border>";
        var root = (Grid)XamlReader.Load(
            "<Grid " + WlNs + " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Width='" + Num(Previews.CardWidth(sw)) + "'>" +
            "<Button Padding='6' HorizontalAlignment='Stretch' VerticalAlignment='Stretch' HorizontalContentAlignment='Stretch' VerticalContentAlignment='Top' IsTabStop='False' UseSystemFocusVisuals='False'>" +
            "<Button.Template>" + tpl + "</Button.Template>" +
            "<StackPanel Spacing='5'>" +
            "<Grid Height='24' ColumnSpacing='7'><Grid.ColumnDefinitions><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/><ColumnDefinition Width='22'/></Grid.ColumnDefinitions>" +
            "<Grid Width='16' Height='16' VerticalAlignment='Center'>" + ico + "</Grid>" +
            "<TextBlock Grid.Column='1' VerticalAlignment='Center' FontSize='12' FontFamily='{ThemeResource LgFont}' TextTrimming='CharacterEllipsis' TextWrapping='NoWrap' Foreground='{ThemeResource LgInk}'/>" +
            "</Grid>" +
            "<Border Width='" + Num(sw) + "' Height='" + Num(sh) + "' CornerRadius='{ThemeResource LgRCtlCorner}' Background='{ThemeResource " + (Dark ? "LgCard" : "LgSunk") + "}'>" +
            "<TextBlock Margin='10,8,10,8' HorizontalAlignment='Center' VerticalAlignment='Center' TextAlignment='Center' TextWrapping='WrapWholeWords' FontSize='" + (Dark ? "12" : "11.5") + "' FontFamily='{ThemeResource LgFont}' Foreground='{ThemeResource LgInk3}'/>" +
            "</Border>" +
            "<Rectangle Width='16' Height='3' RadiusX='2' RadiusY='2' Margin='0,-1,0,0' HorizontalAlignment='Center' Fill='{ThemeResource LgAcc}'/>" +
            "</StackPanel></Button>" +
            "<Button Width='22' Height='22' Padding='0' Margin='0,7,6,0' HorizontalAlignment='Right' VerticalAlignment='Top' HorizontalContentAlignment='Center' VerticalContentAlignment='Center' IsTabStop='False' UseSystemFocusVisuals='False' Foreground='{ThemeResource LgInk3}'>" +
            "<Button.Template>" + xTpl + "</Button.Template><TextBlock FontSize='14' Text='&#x00D7;' HorizontalAlignment='Center' VerticalAlignment='Center'/></Button>" +
            "</Grid>");
        // by position, not FindName (a FindName cast is the suspect in the launcher's AOT "Specified cast" failure)
        var open = (Button)root.Children[0];
        var sp = open.Content as StackPanel ?? throw new InvalidOperationException("the card's content is not its StackPanel");
        var head = (Grid)sp.Children[0];
        var shot = (Border)sp.Children[1];
        var c = new FlyCard {
            W = w, N = n, Root = root, Open = open, X = (Button)root.Children[1],
            Title = (TextBlock)head.Children[1], Why = shot.Child as TextBlock ?? throw new InvalidOperationException("the card's box has no words"),
            Prefix = new Run(), Text = new Run(), Line = (Rectangle)sp.Children[2],
        };
        c.Title.Inlines.Add(c.Prefix);
        c.Title.Inlines.Add(c.Text);
        if (vm.Icon != null) ((Grid)head.Children[0]).Children.Add(new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform, Source = vm.Icon });
        AutomationProperties.SetAutomationId(c.Open, "bar.preview." + n);
        AutomationProperties.SetAutomationId(c.X, "bar.preview." + n + ".close");
        AutomationProperties.SetName(c.X, "Close this window");
        ToolTipService.SetToolTip(c.X, "Close this window");
        AutomationProperties.SetAutomationId(c.Why, "bar.preview." + n + ".shot");
        SetWhy(c, "asking", "asking Windows for a live picture…", warn: false);
        PaintCard(c);
        c.Open.Click += (_, _) => {
            if (_fly != f) return;
            // the one you are in minimises, like clicking its taskbar icon; any other one comes forward
            WindowAct(c.W.Hwnd, RunningWindows.IsActive(c.W) ? "toggle" : "focus", "a card was clicked");
        };
        c.X.Click += (_, _) => { if (_fly == f) FlyClose(f, c); };
        return c;
    }

    static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// Title, prefix, marks and status from the card's window (bar.js flyCard, flyRefresh).
    void PaintCard(FlyCard c)
    {
        var w = c.W;
        string title = Previews.CardTitle(w);
        c.Text.Text = title;
        c.Prefix.Text = Previews.CardPrefix(w, c.StillOpen);
        if (c.StillOpen) c.Prefix.Foreground = (Brush)Res("LgWarn"); else c.Prefix.ClearValue(TextElement.ForegroundProperty);
        c.Title.Foreground = (Brush)Res(w.Minimized || c.Closing ? "LgInk3" : "LgInk");
        ToolTipService.SetToolTip(c.Title, title + (w.Minimized ? "\n(minimized)" : ""));   // the full title on the TITLE only
        bool on = RunningWindows.IsActive(w);
        c.Line.Opacity = on ? 1 : 0;
        c.Root.Opacity = c.Gone ? 0.55 : 1;
        AutomationProperties.SetName(c.Open, title);
        var st = new List<string>();
        if (on) st.Add("active");
        if (w.Minimized) st.Add("minimized");
        if (c.Closing) st.Add("closing");
        if (c.StillOpen) st.Add("still open");
        AutomationProperties.SetItemStatus(c.Open, string.Join("; ", st));
    }

    object Res(string key) => ThemeToken(key);   // the bar's theme, not the app's (BarWindow.xaml.cs)

    void SetWhy(FlyCard c, string status, string words, bool warn)
    {
        c.Why.Text = words;
        c.Why.Foreground = (Brush)Res(warn ? "LgWarn" : "LgInk3");
        c.Why.Visibility = status.StartsWith("live", StringComparison.Ordinal) ? Visibility.Collapsed : Visibility.Visible;
        c.Live = status.StartsWith("live", StringComparison.Ordinal);
        AutomationProperties.SetName(c.Why, words.Length > 0 ? words : "live picture");
        AutomationProperties.SetItemStatus(c.Why, status);
    }

    // ------------------------------------------------------------------ the pictures (bar.js flySync / flyPaint)
    void FlySync()
    {
        var f = _fly;
        if (f == null || !f.Ready || f.List || f.Body == null || !FlyUp) return;
        _thumbs ??= new Thumbs("the preview pop-up", Host.Log.Line);
        if (!IsUp) {
            foreach (var c in f.Cards) SetWhy(c, "fail", "no live picture: the bar is leaving the screen", warn: true);
            _thumbs.Clear("the bar is leaving the screen");
            return;
        }
        if (f.Dest == IntPtr.Zero) f.Dest = FlyHwnd(f.Body, f.Name + " windows");
        double s = f.Body.XamlRoot?.RasterizationScale ?? Geometry.Scale;
        GetClientRect(f.Dest, out var client);
        var items = new List<Thumbs.Entry>();
        foreach (var c in f.Cards) {
            if (c.Gone || c.Held) continue;
            var box = (FrameworkElement)c.Why.Parent;
            Windows.Foundation.Point p;
            try { p = box.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0)); }
            catch (ArgumentException) { SetWhy(c, "fail", "no live picture: the card is not laid out yet", warn: true); continue; }
            double x = p.X * s, y = p.Y * s, bw = box.ActualWidth * s, bh = box.ActualHeight * s;
            // a box outside the window cannot carry a picture (DWM would draw it off the pop-up): it says so (BAR-230)
            if (x < 0 || y < 0 || x + bw > client.Right + 1 || y + bh > client.Bottom + 1) {
                SetWhy(c, "fail", "no live picture yet: this card is outside the pop-up window, which is still being sized", warn: true);
                continue;
            }
            items.Add(new Thumbs.Entry(c.W.Hwnd, x, y, bw, bh));
        }
        var res = _thumbs.Sync(f.Dest, items);
        var by = res.ToDictionary(r => r.Hwnd, StringComparer.Ordinal);
        var said = new List<string>();
        foreach (var c in f.Cards) {
            if (!by.TryGetValue(c.W.Hwnd, out var r)) continue;
            if (r.Gone) { c.Gone = true; PaintCard(c); }
            if (r.Ok) SetWhy(c, $"live; source {r.SrcW}x{r.SrcH}; screen {r.Screen.Left},{r.Screen.Top},{r.Screen.Right},{r.Screen.Bottom}", "", warn: false);
            else SetWhy(c, r.Gone ? "gone" : "fail", r.Reason ?? "no live picture, and Mantle was not told why", warn: !r.Gone);
            said.Add(r.Ok ? $"{r.Hwnd}: live, source {r.SrcW}x{r.SrcH}{(r.Minimized ? ", minimized" : "")}, drawn at {r.Screen.Left},{r.Screen.Top} {r.Screen.Right - r.Screen.Left}x{r.Screen.Bottom - r.Screen.Top}" : $"{r.Hwnd}: {r.Reason}");
        }
        Host.Log.Line($"bar previews: {f.Name}: " + (said.Count > 0 ? string.Join(" | ", said) : "no card asked for a picture"));
    }

    // ------------------------------------------------------------------ the X (bar.js flyClose)
    void FlyClose(Fly f, FlyCard c)
    {
        if (_tasks == null) return;
        c.X.IsEnabled = false;
        c.Closing = true; PaintCard(c);
        _tasks.Window(c.W.Hwnd, "close", r => {
            Host.Log.Line($"bar tasks: window close (a card's X): {(r.Ok ? "ok" : "failed")}{(r.Reason != null ? ": " + r.Reason : "")}");
            if (r.Ok) {
                if (_chips.Clear("open")) RenderPip(force: true);
                // a "save changes?" prompt keeps it open: said up front after 4 s, and its live picture shows the prompt
                var t = DispatcherQueue.CreateTimer();
                t.Interval = TimeSpan.FromMilliseconds(Previews.StillOpenMs); t.IsRepeating = false;
                t.Tick += (_, _) => { t.Stop(); if (_fly != f || c.Gone) return; c.Closing = false; c.StillOpen = true; c.X.IsEnabled = true; PaintCard(c); };
                t.Start();
                return;
            }
            // refused: held in words, the picture comes off so the reason can be read
            c.Closing = false; c.Held = true; c.X.IsEnabled = true; PaintCard(c);
            if (_chips.Fail("open", "Window did not close", r.Reason ?? "Mantle did not say why", "warn")) RenderPip(force: true);
            if (_fly == f) {
                SetWhy(c, "held", "Not closed: " + (r.Reason ?? "Mantle did not say why"), warn: true);
                FlySync();
            }
        });
    }

    // ------------------------------------------------------------------ a push while open (bar.js flyRefresh)
    void FlyRefresh(TaskList list)
    {
        var f = _fly;
        if (f == null) { TestFlyout(list); return; }
        if (!FlyUp) return;
        // windows that closed: their cards say so at once (bar.thumbs.gone); the rebuild follows
        var shown = f.Cards.Where(c => !c.Gone).Select(c => c.W.Hwnd).ToList();
        var gone = Previews.Gone(shown, list.Items);
        if (gone.Count > 0) {
            _thumbs?.Prune(gone);
            foreach (var c in f.Cards.Where(c => gone.Contains(c.W.Hwnd))) { c.Gone = true; SetWhy(c, "gone", "this window has closed", warn: false); PaintCard(c); }
            Host.Log.Line($"bar previews: {f.Name}: {string.Join(", ", gone)} has closed: its card says so and its picture is down");
        }
        var it = list.Items.FirstOrDefault(x => x.Id == f.AppId);
        var cell = _cells.FirstOrDefault(x => x.Item.Id == f.AppId);
        switch (Previews.OnPush(f.Sig, it)) {
            case Previews.Refresh.Close:
                Popups?.Close(it == null ? "its app went away" : "its app has no windows left");
                break;
            case Previews.Refresh.Update:
                if (f.List) break;
                foreach (var w in it!.Windows) {
                    var c = f.Cards.FirstOrDefault(x => x.W.Hwnd == w.Hwnd);
                    if (c == null) continue;
                    c.W = w; PaintCard(c);
                }
                break;
            case Previews.Refresh.Rebuild:
                if (cell == null) { Popups?.Close("its app went away"); break; }
                // a closed window's card is seen to say so before the flyout is built again without it
                Timer(ref _flyRebuild, gone.Count > 0 ? 600 : 0, () => {
                    if (_fly != f || !FlyUp) return;
                    var now = _cells.FirstOrDefault(x => x.Item.Id == f.AppId);
                    if (now == null || now.Item.Windows.Count == 0) { Popups?.Close("its app has no windows left"); return; }
                    if (Previews.Sig(now.Item.Windows) == f.Sig) return;
                    OpenFly(now, f.Anchor, f.By, rebuild: true);
                });
                break;
        }
    }

    // ------------------------------------------------------------------ TEST RUNS ONLY: open it without a mouse
    // MANTLE_TEST_FLYOUT=<text> in a --test run: the first running app whose name (else exe) contains <text>
    // gets its flyout, as a click would open it (it stays until something closes it), 2 s after the strip first has
    // it. The preview parity script (tools/parity/bar-tasks/run-preview-parity.ps1) photographs it.
    void TestFlyout(TaskList list)
    {
        if (!Host.Args.Test || _testFlyoutDone) return;
        _testFlyout ??= Environment.GetEnvironmentVariable("MANTLE_TEST_FLYOUT") ?? "";
        if (_testFlyout.Length == 0) { _testFlyoutDone = true; return; }
        // the NAME first, then the exe, as Electron's bar.test.flyout picks
        var vm = _cells.FirstOrDefault(c => c.Item.Windows.Count > 0 && c.Name.Contains(_testFlyout, StringComparison.OrdinalIgnoreCase))
            ?? _cells.FirstOrDefault(c => c.Item.Windows.Count > 0 && (c.Item.Exe ?? "").Contains(_testFlyout, StringComparison.OrdinalIgnoreCase));
        if (vm == null) return;
        _testFlyoutDone = true;
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2); t.IsRepeating = false;
        t.Tick += (_, _) => {
            t.Stop();
            int i = _cells.IndexOf(vm);
            if (i < 0 || Dock.ContainerFromIndex(i) is not FrameworkElement fe || FindButton(fe) is not FrameworkElement b) {
                Host.Log.Line($"bar previews: test: {vm.Name} has no cell to open the flyout over"); return;
            }
            Host.Log.Line($"bar previews: test: opening the flyout of {vm.Name} (MANTLE_TEST_FLYOUT)");
            OpenFly(vm, b, "click");
        };
        t.Start();
    }
}
