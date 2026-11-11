using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mantle.App.Shell.Popups;
using Mantle.Core.Features.JumpList;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace Mantle.App;

/// <summary>
/// The right-click menu of an app on the bar (ui/bar/jumplist.js, bar.js openJump; BAR-207..220, BAR-315..332): the
/// app's jump list (Pinned, Recent / Frequent, the app's own categories, Tasks; headings and separators), then the
/// app itself (a new window), Pin / Unpin from taskbar (Mantle's own list), Keep on top, Close window(s). Drawn inside
/// the bar's one pop-up, 284 DIP wide, capped at 456 DIP (a long list scrolls). The bottom rows need no read and are
/// drawn at once; "Reading the jump list…" shows until the list arrives. An action says "&lt;Doing&gt;…" while it runs;
/// success closes the menu, failure keeps it open with "&lt;Doing&gt; failed: &lt;reason&gt;". An item's right-click (or
/// Right arrow / Menu key / Shift+F10) opens its own actions: Open, Remove from this list, and why Pin / Unpin to this
/// list cannot be done. Keyboard: the bar takes it for the menu (refused in a test instance unless MANTLE_TEST_JL_KEYS=1;
/// then the menu's tooltip says why); Up/Down (wrap), Home/End, Enter, Right / Left, Esc.
/// UI Automation: the menu <c>bar.jl</c> ("&lt;name&gt; menu"; HelpText = its tooltip), items <c>bar.jl.item.&lt;n&gt;</c>
/// (HelpText = the item's path), headings <c>bar.jl.h.&lt;n&gt;</c>, quiet lines <c>bar.jl.quiet.&lt;n&gt;</c>, the note
/// <c>bar.jl.note</c>, the item actions <c>bar.jl.sub.open|remove|why</c>, the foot <c>bar.jl.app|pin|ontop|close</c>.
/// </summary>
public sealed partial class BarWindow
{
    public const string JlKeysEnv = "MANTLE_TEST_JL_KEYS";
    const double JlWidth = 284, JlMax = 456;
    const string JlNs = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    const string GlyphFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    const string GPin = "", GUnpin = "", GClose = "", GTask = "";

    // .pop.jl .jl-i: min 28 high, padding 4 8, radius 6, hover / press surfaces, a 1.5 accent ring on keyboard focus
    const string JlRowTemplate =
        "<ControlTemplate " + JlNs + " TargetType='Button'>" +
        "<Grid x:Name='Face' Background='Transparent' CornerRadius='6' BorderThickness='1.5' BorderBrush='Transparent' Padding='{TemplateBinding Padding}' MinHeight='{TemplateBinding MinHeight}'>" +
        "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'>" +
        "<VisualState x:Name='Normal'/>" +
        "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource LgHover}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource LgPress}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Disabled'/>" +
        "</VisualStateGroup><VisualStateGroup x:Name='FocusStates'>" +
        "<VisualState x:Name='Focused'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource LgHover}'/><Setter Target='Face.BorderBrush' Value='{ThemeResource LgAcc}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Unfocused'/><VisualState x:Name='PointerFocused'/>" +
        "</VisualStateGroup></VisualStateManager.VisualStateGroups>" +
        "<ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center' Foreground='{TemplateBinding Foreground}'/>" +
        "</Grid></ControlTemplate>";

    sealed class JlMenu
    {
        public string Key = "";
        public CellVm Vm = null!;
        public JumpApp App = null!;
        public TaskItem Item = null!;
        public bool Kbd;
        public StackPanel Body = null!, List = null!, Foot = null!;
        public UiaGrid Root = null!;   // a plain panel has no UI Automation peer
        public Border FootBox = null!;
        public ScrollViewer Scroll = null!;
        public TextBlock Note = null!;
        public Border? Sub;
        public string Tip = "";
    }

    JlMenu? _jl;
    bool _jlHooked;
    RightTappedEventHandler? _cellRight;

    static string JlKey(CellVm vm) => "jl:" + vm.Item.Id;

    /// Hooked from Cell_Loaded (handledEventsToo, remove-then-add like the middle-click).
    void HookJumpCell(Button b)
    {
        _cellRight ??= new RightTappedEventHandler(Cell_RightTapped);
        b.RemoveHandler(UIElement.RightTappedEvent, _cellRight);
        b.AddHandler(UIElement.RightTappedEvent, _cellRight, true);
    }

    /// BAR-207: right-click opens the app menu over the icon; a second right-click on the same icon closes it.
    void Cell_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not CellVm vm || DragBusy) return;
        e.Handled = true;
        TestLine("right-click", sender);
        StopTimer(_flyDwell);
        if (Popups?.OpenKey == JlKey(vm)) { Popups.Close("a second right-click on its icon"); return; }
        // never take the bar down over a menu: a fault is said in the log and the chip strip
        try { OpenJump(vm, b); }
        catch (Exception ex) {
            Host.Log.Line($"bar tasks: menu: the app menu could not open ({ex.GetType().Name}: {ex.Message})");
            if (_chips.Fail("menu", "App menu did not open", ex.Message, "warn")) RenderPip(force: true);
        }
    }

    void HookJlOnce()
    {
        if (_jlHooked || Popups == null || _tasks == null) return;
        _jlHooked = true;
        Popups.Closed += (key, why) => {
            if (_jl == null || _jl.Key != key) return;
            if (_jl.Kbd) Popups.GiveBackKeyboard("the app menu closed");
            _jl = null;
        };
        Popups.KeyboardLost += key => { if (_jl != null && _jl.Key == key) _jl.Kbd = false; };
        _tasks.OnTopChanged += () => { foreach (var c in _cells) c.OnTop = OnTopOf(c.Item) != null; };
        _tasks.OnTopToast += words => Host.Log.Line("bar tasks: keep on top: " + (Host.Args.Test ? words : "a result came back"));
    }

    internal void OpenJump(CellVm vm, FrameworkElement anchor)
    {
        if (Popups == null || _tasks == null) return;
        HookJlOnce();
        var it = vm.Item;
        var m = new JlMenu { Key = JlKey(vm), Vm = vm, Item = it, App = JumpApp.Of(it) };
        _jl = m;
        BuildJl(m);
        _popAnchor = anchor;
        Popups.Open(new PopupKind(m.Key, (it.Name.Length > 0 ? it.Name : "App") + " menu", JlWidth, JlHeight(m), Focusable: false, Instant: false, Away: true, () => m.Root), anchor);
        // counts only: never the app's name (the log rule of lib/features/jumplist.js)
        Host.Log.Line($"bar tasks: menu: opened for a {it.Kind} app with {it.Windows.Count} window(s)");
        long t0 = Environment.TickCount64;
        var tasks = _tasks;
        tasks.JumpGet(m.App, r => {
            if (_jl != m) return;
            JlFill(m, r);
            Host.Log.Line($"bar tasks: menu: the jump list {(r.Ok ? $"arrived: {r.Sections.Sum(s => s.Items.Count(i => !i.Sep))} item(s) in {r.Sections.Count} section(s)" : "could not be read")} ({Environment.TickCount64 - t0} ms)");
        });
        JlKeyboard(m, 0);
    }

    // ------------------------------------------------------------------ building

    static TextBlock JlText(string text, string ink, double size = 13, string? id = null)
    {
        var t = (TextBlock)XamlReader.Load("<TextBlock " + JlNs + " FontSize='" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            "' TextWrapping='WrapWholeWords' LineHeight='19' Foreground='{ThemeResource " + ink + "}' FontFamily='{ThemeResource LgFont}'/>");
        t.Text = text;
        if (id != null) { AutomationProperties.SetAutomationId(t, id); AutomationProperties.SetName(t, text); }
        return t;
    }

    Button JlRow(string text, string? glyph, string? iconUrl, string? letter, string id, Action<Button> run, string ink = "LgInk2", bool bold = false)
    {
        var b = (Button)XamlReader.Load(
            "<Button " + JlNs + " HorizontalAlignment='Stretch' HorizontalContentAlignment='Stretch' Padding='8,4,8,4' MinHeight='28' IsTabStop='True' UseSystemFocusVisuals='False'" +
            " Foreground='{ThemeResource " + ink + "}'><Button.Template>" + JlRowTemplate + "</Button.Template>" +
            "<Grid ColumnSpacing='9'><Grid.ColumnDefinitions><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>" +
            "<Image Width='16' Height='16' Visibility='Collapsed'/>" +
            "<TextBlock FontSize='13' HorizontalAlignment='Center' VerticalAlignment='Center' Foreground='{ThemeResource LgInk3}'/>" +
            "<TextBlock Grid.Column='1' FontSize='13' FontFamily='{ThemeResource LgFont}' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'" + (bold ? " FontWeight='SemiBold'" : "") + "/>" +
            "</Grid></Button>");
        var g = (Grid)b.Content;
        var img = (Image)g.Children[0];
        var ico = (TextBlock)g.Children[1];
        ((TextBlock)g.Children[2]).Text = text;
        if (iconUrl != null) _ = JlIcon(img, ico, iconUrl);
        if (glyph != null) { ico.FontFamily = new FontFamily(GlyphFont); ico.FontSize = 12; ico.Text = glyph; }
        else if (iconUrl == null) { ico.Text = letter ?? ""; ico.FontSize = 10; ico.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; }
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, text);
        b.Click += (_, _) => run(b);
        return b;
    }

    /// An item's icon from the sidecar's data URL; one that does not decode keeps the task glyph.
    async Task JlIcon(Image img, TextBlock ico, string url)
    {
        ico.FontFamily = new FontFamily(GlyphFont); ico.FontSize = 12; ico.Text = GTask;
        try {
            if (_iconBitmaps.TryGetValue(url, out var known) && known != null) { img.Source = known; }
            else {
                int comma = url.IndexOf(',');
                byte[] bytes = Convert.FromBase64String(comma >= 0 ? url[(comma + 1)..] : url);
                using var ms = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var w = new global::Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0))) { w.WriteBytes(bytes); await w.StoreAsync(); await w.FlushAsync(); w.DetachStream(); }
                ms.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms);
                img.Source = bmp;
            }
            img.Visibility = Visibility.Visible; ico.Text = "";
        } catch (Exception) { /* the glyph stays: ui/bar/jumplist.js img.onerror */ }
    }

    void BuildJl(JlMenu m)
    {
        var it = m.Item;
        string name = it.Name.Length > 0 ? it.Name : "App";
        m.Body = new StackPanel { Padding = new Thickness(6), Spacing = 0 };
        m.Root = new UiaGrid();
        m.Root.Children.Add(m.Body);
        AutomationProperties.SetAutomationId(m.Root, "bar.jl");
        AutomationProperties.SetName(m.Root, name + " menu");
        m.List = new StackPanel { Padding = new Thickness(0, 0, 0, 4) };
        m.List.Children.Add(JlText("Reading the jump list…", "LgInk3", 13, "bar.jl.quiet.0"));
        ((TextBlock)m.List.Children[0]).Margin = new Thickness(8, 6, 8, 6);
        m.Scroll = new ScrollViewer { Content = m.List, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        m.Note = JlText("", "LgWarn", 13, "bar.jl.note");
        m.Note.Margin = new Thickness(8, 6, 8, 6);
        m.Note.Visibility = Visibility.Collapsed;
        m.Foot = new StackPanel();
        var footBox = (Border)XamlReader.Load("<Border " + JlNs + " BorderThickness='0,1,0,0' BorderBrush='{ThemeResource LgEdgeOut}' Margin='0,2,0,0' Padding='0,4,0,0'/>");
        footBox.Child = m.Foot;
        m.FootBox = footBox;
        // the app itself: a new window (middle-click does the same; BAR-214, BAR-322)
        m.Foot.Children.Add(JlRow(name, null, it.Icon, CellVm.LetterOf(name), "bar.jl.app", _ => JlFresh(m), "LgInk", bold: true));
        // Mantle's own pin list, never Windows' pins (BAR-216, BAR-324)
        bool pinned = it.Kind == "pinned";
        m.Foot.Children.Add(JlRow(pinned ? "Unpin from taskbar" : "Pin to taskbar", pinned ? GUnpin : GPin, null, null, "bar.jl.pin",
            _ => JlRun(m, pinned ? "Unpinning" : "Pinning", done => _tasks!.JumpPin(m.App, !pinned, false, done))));
        if (it.Windows.Count > 0) {
            // keep on top: a window Mantle keeps on top -> "Stop keeping on top"; else the window in use (BAR-217, BAR-325)
            string? onH = OnTopOf(it);
            var target = onH != null ? it.Windows.First(w => w.Hwnd == onH)
                : it.Windows.FirstOrDefault(w => RunningWindows.IsActive(w) && !w.Minimized) ?? it.Windows[0];
            var ot = JlRow(onH != null ? "Stop keeping on top" : "Keep on top", onH != null ? GUnpin : GPin, null, null, "bar.jl.ontop",
                _ => JlRun(m, onH != null ? "Stopping" : "Keeping on top", done => _tasks!.OnTopSet(target.Hwnd, target.Pid, onH == null, name, done)));
            string otTip = _tasks?.OnTopProblem is { } cant ? "Keep on top: " + cant
                : (onH != null ? "Mantle keeps this window above the others" : "Keep this window above all the others") + " (Win+Ctrl+T does the same for the window in front)";
            ToolTipService.SetToolTip(ot, otTip);
            AutomationProperties.SetHelpText(ot, otTip);
            if (onH != null) AutomationProperties.SetItemStatus(ot, "kept on top");
            m.Foot.Children.Add(ot);
            // close, politely: WM_CLOSE, the app may still ask to save (BAR-218, BAR-326)
            var hwnds = it.Windows.Select(w => w.Hwnd).ToArray();
            m.Foot.Children.Add(JlRow(it.Windows.Count == 1 ? "Close window" : "Close all windows", GClose, null, null, "bar.jl.close",
                _ => JlRun(m, "Closing", done => _tasks!.JumpClose(hwnds, done))));
        }
        m.Body.Children.Add(m.Scroll);
        m.Body.Children.Add(m.Note);
        m.Body.Children.Add(footBox);
        m.Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { if (JlKey(m, e.Key)) e.Handled = true; }), true);
    }

    /// The window Mantle keeps on top among this app's windows (ui/bar/ontop.js pinnedOf), or null.
    string? OnTopOf(TaskItem it)
    {
        var on = _tasks?.OnTopPinned;
        if (on == null || on.Count == 0) return null;
        foreach (var w in it.Windows) if (on.ContainsKey(w.Hwnd)) return w.Hwnd;
        return null;
    }

    double JlHeight(JlMenu m)
    {
        // the list scrolls inside the 456 DIP cap; the note and the foot always show
        m.Scroll.MaxHeight = double.PositiveInfinity;
        m.Note.Measure(new global::Windows.Foundation.Size(JlWidth - 14, double.PositiveInfinity));
        m.FootBox.Measure(new global::Windows.Foundation.Size(JlWidth - 14, double.PositiveInfinity));
        double fixedH = 12 + (m.Note.Visibility == Visibility.Visible ? m.Note.DesiredSize.Height : 0) + m.FootBox.DesiredSize.Height;
        m.Scroll.MaxHeight = Math.Max(48, JlMax - fixedH - 2);
        m.Body.Measure(new global::Windows.Foundation.Size(JlWidth - 2, double.PositiveInfinity));
        return Math.Min(JlMax, Math.Ceiling(m.Body.DesiredSize.Height) + 2);
    }

    void JlResize(JlMenu m) { if (_jl == m) Popups?.Resize(m.Key, JlHeight(m)); }

    void JlSay(JlMenu m, string text, bool quiet)
    {
        // a fresh line in the right ink (quiet while it runs, warn when it failed), in the old one's place
        var t = JlText(text, quiet ? "LgInk3" : "LgWarn", 13, "bar.jl.note");
        t.Margin = new Thickness(8, 6, 8, 6);
        AutomationProperties.SetItemStatus(t, quiet ? "quiet" : "warn");
        t.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        int at = m.Body.Children.IndexOf(m.Note);
        if (at >= 0) m.Body.Children[at] = t;
        m.Note = t;
        JlResize(m);
    }

    /// An action (jumplist.js run): "&lt;what&gt;…" while it runs; success closes the menu; failure keeps it open and says why.
    void JlRun(JlMenu m, string what, Action<Action<JumpAct>> start)
    {
        if (_tasks == null || _jl != m) return;
        JlSay(m, what + "…", quiet: true);
        start(r => {
            Host.Log.Line($"bar tasks: menu: {what}: {(r.Ok ? (r.DryRun ? "dry run" : "done") : "failed")}{(r.Did != null ? $" ({r.Did})" : "")}{(Host.Args.Test && r.Reason != null ? ": " + r.Reason : "")}");
            if (_jl != m) return;
            if (r.Ok && r.Did != "dry") { Popups?.Close(what + " done"); return; }
            if (r.Ok) { Popups?.Close(what + " (a dry run)"); return; }
            JlSay(m, what + " failed: " + (r.Reason ?? "Mantle did not say why"), quiet: false);
        });
    }

    /// The app row: pinned = the pin's own shortcut, like a middle-click; running only = jumplist.open {item:'app'}.
    void JlFresh(JlMenu m)
    {
        string name = m.Item.Name.Length > 0 ? m.Item.Name : "the app";
        if (m.Item.Kind == "pinned") {
            JlRun(m, "Starting " + name, done => _tasks!.Open(m.Item.Id, true, r => done(new JumpAct(r.Ok, r.Reason, DryRun: r.DryRun))));
            return;
        }
        JlRun(m, "Starting " + name, done => _tasks!.JumpOpen(m.App, "app", null, done));
    }

    void JlFill(JlMenu m, JumpListReply r)
    {
        m.List.Children.Clear();
        int n = 0, h = 0;
        foreach (var sec in r.Sections) {
            var items = sec.Items.Where(x => x.Sep || x.Title.Length > 0).ToList();
            if (items.Count == 0) continue;
            var head = JlText(sec.Name.ToUpperInvariant(), "LgInk3", 11.5, "bar.jl.h." + h++);
            head.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            head.CharacterSpacing = 40;
            head.Margin = new Thickness(8, m.List.Children.Count == 0 ? 2 : 8, 8, 4);
            m.List.Children.Add(head);
            foreach (var x in items) {
                if (x.Sep) {
                    m.List.Children.Add((UIElement)XamlReader.Load("<Rectangle " + JlNs + " Height='1' Margin='8,4,8,4' Fill='{ThemeResource LgEdgeOut}'/>"));
                    continue;
                }
                m.List.Children.Add(JlItemRow(m, x, sec, n++));
            }
        }
        var (lines, warn) = r.Quiet();
        int q = 0;
        foreach (var line in lines) {
            var t = JlText(line, warn ? "LgWarn" : "LgInk3", 13, "bar.jl.quiet." + q++);
            t.Margin = new Thickness(8, 6, 8, 6);
            AutomationProperties.SetItemStatus(t, warn ? "warn" : "quiet");
            m.List.Children.Add(t);
        }
        JlResize(m);
    }

    Button JlItemRow(JlMenu m, JumpItem x, JumpSection sec, int n)
    {
        var b = JlRow(x.Title, x.Icon == null ? GTask : null, x.Icon, null, "bar.jl.item." + n,
            _ => JlRun(m, "Opening " + x.Title, done => _tasks!.JumpOpen(m.App, x.Item, x.Title, done)));
        string tip = x.Tip ?? x.Title;
        ToolTipService.SetToolTip(b, tip);
        AutomationProperties.SetHelpText(b, tip);
        AutomationProperties.SetItemStatus(b, sec.Kind + (x.Pinned ? "; pinned" : "") + (x.CanRemove ? "; can remove" : ""));
        b.Tag = (x, sec);
        b.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler((_, e) => { e.Handled = true; JlToggleSub(m, b); }), true);
        return b;
    }

    /// An item's own actions under it (jumplist.js toggleSub, BAR-211..213, BAR-320); the same item again closes them.
    void JlToggleSub(JlMenu m, Button b)
    {
        if (b.Tag is not ValueTuple<JumpItem, JumpSection> t) return;
        var (x, sec) = t;
        bool wasOpen = m.Sub != null && m.Sub.Tag == b;
        if (m.Sub != null) { m.List.Children.Remove(m.Sub); m.Sub = null; }
        if (wasOpen) { b.Focus(FocusState.Programmatic); JlResize(m); return; }
        var sub = new StackPanel();
        sub.Children.Add(JlRow("Open", GTask, null, null, "bar.jl.sub.open", _ => JlRun(m, "Opening " + x.Title, done => _tasks!.JumpOpen(m.App, x.Item, x.Title, done))));
        if (x.CanRemove)
            sub.Children.Add(JlRow("Remove from this list", GClose, null, null, "bar.jl.sub.remove",
                _ => JlRun(m, "Removing " + x.Title, done => _tasks!.JumpRemove(m.App, x.Item, x.Title, done))));
        var why = JlText(JumpRules.SubWhy(sec.Kind, x.Pinned), "LgInk3", 11.5, "bar.jl.sub.why");
        why.Margin = new Thickness(8, 3, 8, 4);
        sub.Children.Add(why);
        m.Sub = new Border { Padding = new Thickness(6, 2, 0, 2), Child = sub, Tag = b };
        m.List.Children.Insert(m.List.Children.IndexOf(b) + 1, m.Sub);
        Host.Log.Line($"bar tasks: menu: an item's own actions opened ({(x.CanRemove ? "removable" : "not removable")}, {sec.Kind})");
        JlResize(m);
        if (m.Kbd && b.FocusState != FocusState.Unfocused) ((Button)sub.Children[0]).Focus(FocusState.Keyboard);
    }

    // ------------------------------------------------------------------ the keyboard (BAR-220, BAR-328)

    void JlKeyboard(JlMenu m, int tries)
    {
        if (_jl != m || Popups == null) return;
        bool allowed = !Host.Args.Test || (Host.Env(JlKeysEnv) == "1" && !Host.Quiet);
        var (ok, words) = allowed ? Popups.TakeKeyboard(m.Key)
            : (false, "a test instance never takes the keyboard from whoever is at the machine, so the arrow keys stay where they are");
        if (!ok && words == "the pop-up is not open" && tries < 20) {
            var t = DispatcherQueue.CreateTimer(); t.Interval = TimeSpan.FromMilliseconds(50); t.IsRepeating = false;
            t.Tick += (_, _) => JlKeyboard(m, tries + 1); t.Start();
            return;
        }
        m.Kbd = ok;
        m.Tip = ok ? "" : "Arrow keys do not reach this menu: " + words.Split(", so ")[0];
        ToolTipService.SetToolTip(m.Root, ok ? null : m.Tip);
        AutomationProperties.SetHelpText(m.Root, m.Tip);
        Host.Log.Line(ok ? "bar tasks: menu: the bar has the keyboard for the menu" : "bar tasks: menu: " + m.Tip);
    }

    List<Button> JlRows(JlMenu m)
    {
        var l = new List<Button>();
        void Walk(DependencyObject d)
        {
            if (d is Button b && AutomationProperties.GetAutomationId(b).StartsWith("bar.jl.", StringComparison.Ordinal)) { l.Add(b); return; }
            if (d is Panel p) foreach (var c in p.Children) Walk(c);
            else if (d is Border bo && bo.Child != null) Walk(bo.Child);
            else if (d is ScrollViewer sv && sv.Content is DependencyObject sc) Walk(sc);
        }
        Walk(m.Body);
        return l;
    }

    /// Arrows / Home / End over every row (wrapping), Right / Menu / Shift+F10 = the item's own actions, Left = back, Esc = close.
    bool JlKey(JlMenu m, VirtualKey k)
    {
        if (_jl != m) return false;
        if (k == VirtualKey.Escape) { Popups?.Close("Esc"); return true; }
        var rows = JlRows(m);
        if (rows.Count == 0) return false;
        int at = rows.FindIndex(r => r.FocusState != FocusState.Unfocused);
        if (k is VirtualKey.Down or VirtualKey.Up or VirtualKey.Home or VirtualKey.End) {
            int n = k == VirtualKey.Home ? 0 : k == VirtualKey.End ? rows.Count - 1
                : at < 0 ? (k == VirtualKey.Down ? 0 : rows.Count - 1)
                : (at + (k == VirtualKey.Down ? 1 : -1) + rows.Count) % rows.Count;
            rows[n].Focus(FocusState.Keyboard);
            rows[n].StartBringIntoView();
            return true;
        }
        var cur = at >= 0 ? rows[at] : null;
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (cur != null && cur.Tag is ValueTuple<JumpItem, JumpSection> && (k == VirtualKey.Right || k == VirtualKey.Application || (k == VirtualKey.F10 && shift))) { JlToggleSub(m, cur); return true; }
        if (cur != null && k == VirtualKey.Left && m.Sub is { Tag: Button owner } && IsIn(cur, m.Sub)) { JlToggleSub(m, owner); return true; }
        if (cur != null && k == VirtualKey.Enter) {
            var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(cur) as Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer
                ?? new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(cur);
            peer.Invoke();
            return true;
        }
        return false;
    }

    static bool IsIn(DependencyObject d, DependencyObject box)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p)) if (p == box) return true;
        return false;
    }
}
