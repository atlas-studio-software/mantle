using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.Downloads;
using Mantle.Core.Features.Downloads;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App;

/// The Downloads button and its shelf (lane native-r7-media4; ui/bar/downloads.js init/renderBtn/health/open/refresh/
/// seeAll/act/keyboard). The button (bar.downloads, BarWindow.xaml) opens the shelf on a click or a right-click and
/// closes it on the next; it is hidden while the feature is off, muted while the folder cannot be read or a game rests
/// it, and carries a dot while something arrived since the shelf was last opened. Opening re-reads the folder
/// (downloads.list), then marks all seen. The panel takes the keyboard (PopupHost.TakeKeyboard) for the arrows; a test
/// instance never does (MANTLE_TEST_DL_KEYS=1 with MANTLE_TEST_VISIBLE=1 lets a CI runner), and the list says why.
public sealed partial class BarWindow
{
    public const string DlKeysEnv = "MANTLE_TEST_DL_KEYS";
    const string DlKey = "dl";
    Button? _dlBtn;
    Microsoft.UI.Xaml.Shapes.Ellipse? _dlDot;
    DownloadsShelf? _dl;
    SurfaceLink? _dlLink;
    JsonElement _dlState;
    bool _dlKbd;
    // the user moved the focus since the shelf took the keyboard (a walk, Tab, a press in the shelf); until then a row
    // that has the focus got it from Windows (the window's first focus), whatever its FocusState says (run 43818332316:
    // that focus reads as a keyboard one), so the first Down is the newest
    bool _dlUser;
    DispatcherTimer? _dlTick;

    /// Called once from the bar's start, after the pop-up framework (BarWindow.xaml.cs).
    void StartDownloads()
    {
        _dlBtn = DownloadsBtn;
        // the dot (.dls .dldot): top-right of the glyph, accent, hidden unless something is new
        _dlDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, -3, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        AutomationProperties.SetAutomationId(_dlDot, "bar.downloads.dot");
        _dlBtn.Content = null;
        var g = new Grid();
        g.Children.Add(DownloadsHost); g.Children.Add(_dlDot);
        _dlBtn.Content = g;
        _dl = new DownloadsShelf(Host.Log.Line);
        _dl.Act = (verb, item, what) => _ = DlAct(verb, item, what);
        _dl.Drag = (item, nm) => _ = DlDrag(item, nm);
        _dl.Root.KeyDown += DlKey_;
        _dl.Root.PreviewKeyDown += (_, e) => { if (e.Key == global::Windows.System.VirtualKey.Tab) _dlUser = true; };
        _dl.Root.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => _dlUser = true), true);
        if (Host.Args.Test) {
            // a test run traces where the keyboard goes in the shelf and which element an Enter reaches (a lost Enter
            // after a repaint, run 43854350493: no open and no "ignored" line)
            Microsoft.UI.Xaml.Input.FocusManager.GotFocus += (_, e) => {
                if (_dl?.Root.XamlRoot is { } xr && e.NewFocusedElement is UIElement u && u.XamlRoot == xr && Popups?.OpenKey == DlKey)
                    Host.Log.Line($"downloads: the keyboard focus is on {DlWho(u)}");
            };
            _dl.Root.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) => {
                if (e.Key == global::Windows.System.VirtualKey.Enter) Host.Log.Line($"downloads: key Enter reached {DlWho(e.OriginalSource)}{(e.Handled ? " (handled)" : "")}");
            }), true);
        }
        _dlBtn.Click += (_, _) => OpenDownloads();
        _dlBtn.RightTapped += (_, e) => { e.Handled = true; OpenDownloads(); };
        RenderDlBtn();
        if (Popups == null) return;
        Popups.Anchor(_dlBtn);
        // the keyboard is asked for once the shelf is on screen (its first frame), never on a timer: asked on the open,
        // the window is still being made and Windows refuses it, so the arrows never reached a live shelf
        Popups.OnScreen += key => { if (key == DlKey) DlKeyboard(true); };
        Popups.Closed += (key, why) => {
            if (key != DlKey || _dl == null) return;
            _dlTick?.Stop();
            DlKeyboard(false);
            _dl.Busy.Clear();
            Host.Log.Line($"downloads: the shelf closed ({why})");
        };
        Popups.KeyboardLost += key => { if (key == DlKey && Popups.OpenKey == DlKey) Popups.Close("another window took focus"); };   // BAR-401
    }

    /// From the host thread's wiring (App.xaml.cs, block "media wiring"), posted here.
    internal void AttachDownloads(SurfaceLink link)
    {
        _dlLink = link;
        link.On<JsonElement>(DownloadsFeature.StateEvent, s => DlUpdate(s.Clone()));
    }

    void DlUpdate(JsonElement s)
    {
        int oldRev = _dl != null && _dl.View.ValueKind == JsonValueKind.Object && _dl.View.TryGetProperty("rev", out var vr) && vr.TryGetInt32(out var ov) ? ov : -1;
        _dlState = s;
        if (_dl != null) _dl.State = s;
        RenderDlBtn();
        // a game rests the shelf: an open panel says so at once (the commands answer "is resting while")
        if (Popups?.OpenKey == DlKey && _dl != null) {
            if (Str(s, "state") is "resting" or "stopped") { _dl.View = s; PaintDl(); }
            else if (s.TryGetProperty("rev", out var r) && r.TryGetInt32(out var rev) && rev != oldRev) _ = DlRefresh(true);
        }
    }

    static bool DlBad(JsonElement s) => s.ValueKind == JsonValueKind.Object && ((s.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False)
        || Str(s, "state") is "missing" or "unreadable" or "unresolved" or "error");

    /// renderBtn() and health(): hidden while off, muted while bad or resting, the dot while something is new.
    void RenderDlBtn()
    {
        if (_dlBtn == null || _dlDot == null) return;
        var s = _dlState;
        bool has = s.ValueKind == JsonValueKind.Object;
        _dlBtn.Visibility = has && s.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False ? Visibility.Collapsed : Visibility.Visible;
        bool bad = DlBad(s), resting = Str(s, "state") is "resting" or "stopped";
        int fresh = !bad && !resting && has && s.TryGetProperty("newCount", out var nc) && nc.TryGetInt32(out var n) ? n : 0;
        _dlDot.Visibility = fresh > 0 ? Visibility.Visible : Visibility.Collapsed;
        // the dot (look.css .dot: the accent): the islands accent in the islands look (Shell/Levitate/IslandsInk), else steel
        _dlDot.Fill = Surfaces.Bar.Frame.BarGeometry.Islands ? Mantle.App.Shell.Levitate.IslandsInk.AccentBrush(_dark, Host.Log.Line) : MTok("LgAcc");
        DownloadsHost.Opacity = bad || resting ? .5 : 1;
        string tip = !has ? "Downloads — reading…"
            : bad ? "Downloads unavailable — " + (Str(s, "reason") ?? "no reason was given")
            : resting ? "Downloads — " + (Str(s, "reason") ?? Str(s, "state"))
            : fresh > 0 ? $"Downloads — {fresh} new" : "Downloads";
        ToolTipService.SetToolTip(_dlBtn, tip);
        AutomationProperties.SetName(_dlBtn, tip);
        AutomationProperties.SetItemStatus(_dlBtn, !has ? "reading" : bad ? "off" : resting ? "resting" : fresh > 0 ? "new" : "ok");
        // health(): a folder that cannot be read is a chip in the strip, not only a muted button
        bool changed = bad ? _chips.Fail("downloads", "downloads off", Str(s, "reason") ?? "the Downloads folder could not be read and no reason was given", "warn")
            : _chips.Clear("downloads");
        if (changed) RenderPip(force: true);
    }

    /// open(): toggle the shelf above the button; opening re-reads the folder, marks all seen and asks for the keyboard.
    void OpenDownloads()
    {
        if (_dl == null || Popups == null || _dlBtn == null) return;
        _popAnchor = _dlBtn;
        bool closing = Popups.OpenKey == DlKey;
        if (!closing) { _dl.WasFresh.Clear(); _dl.Note = ""; _dl.Loading = _dl.View.ValueKind != JsonValueKind.Object; PaintDl(measureOnly: true); }
        double h = closing ? 200 : Math.Min(560, _dl.Need());
        var dl = _dl;
        Popups.Toggle(new PopupKind(DlKey, "Downloads", DownloadsShelf.WidthDip, h, Focusable: false, Instant: true, Away: true, () => dl.Root, Levitate: true), _dlBtn);   // lane r12-levitate-host: the levitated look, from the host
        if (Popups.OpenKey != DlKey || closing) return;
        Host.Log.Line($"downloads: the shelf opened ({DownloadsShelf.WidthDip}x{h} DIP)");
        _ = DlRefresh(false);
        // the ages re-render every 30 s while open, never at rest (BAR-878)
        _dlTick ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dlTick.Tick -= DlTick; _dlTick.Tick += DlTick;
        _dlTick.Start();
    }
    void DlTick(object? s, object e) { if (Popups?.OpenKey == DlKey) PaintDl(); else _dlTick?.Stop(); }

    void PaintDl(bool measureOnly = false)
    {
        if (_dl == null) return;
        _dl.Paint(Dark, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (measureOnly || Popups?.OpenKey != DlKey) return;
        double h = Math.Min(560, _dl.Need());
        if (!Popups.Resize(DlKey, h)) { _dl.Note = "This panel may be cut off at the screen edge: Mantle could not make it taller"; _dl.Paint(Dark, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
        DlSeatFocus();
    }

    /// refresh(): cached = the feature's last read (a push said it changed); otherwise the folder is read again first.
    async System.Threading.Tasks.Task DlRefresh(bool cached)
    {
        if (_dl == null || _dlLink == null) return;
        _dl.Loading = _dl.View.ValueKind != JsonValueKind.Object;
        if (Popups?.OpenKey == DlKey) PaintDl();
        var r = await _dlLink.CallAsync("downloads.list", cached ? new JsonObject { ["cached"] = true } : new JsonObject());
        _dl.Loading = false;
        bool any = false;
        if (r.Ok) {
            _dl.View = r.Result.Clone();
            if (_dl.View.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray()) if (it.TryGetProperty("fresh", out var f) && f.ValueKind == JsonValueKind.True && Str(it, "item") is { } k) { _dl.WasFresh.Add(k); any = true; }
        } else {
            string m = r.Error ?? "Mantle got no answer when it read the folder";
            // a game is running: the feature rests and its commands say so; expected, not drawn as a failure
            bool resting = m.Contains("is resting while", StringComparison.Ordinal);
            _dl.View = JsonDocument.Parse(new JsonObject { ["ok"] = false, ["state"] = resting ? "resting" : "error", ["reason"] = resting ? m : "could not read your downloads: " + m, ["items"] = new JsonArray() }.ToJsonString()).RootElement.Clone();
        }
        int count = _dl.View.TryGetProperty("items", out var its) && its.ValueKind == JsonValueKind.Array ? its.GetArrayLength() : 0;
        Host.Log.Line($"downloads: the shelf shows {count} rows ({Str(_dl.View, "state") ?? "?"}{(_dl.WasFresh.Count > 0 ? $", {_dl.WasFresh.Count} new" : "")})");
        if (Popups?.OpenKey == DlKey) PaintDl();
        bool stateNew = _dlState.ValueKind == JsonValueKind.Object && _dlState.TryGetProperty("newCount", out var nc) && nc.TryGetInt32(out var n) && n > 0;
        if (r.Ok && (any || stateNew)) {
            var s = await _dlLink.CallAsync("downloads.seen");
            if (!s.Ok) _dl.Note = "could not mark these as seen, so the dot may stay: " + s.Error;
            else if (Str(s.Result, "note") is { } note) _dl.Note = note;
            else return;
            if (Popups?.OpenKey == DlKey) PaintDl();
        }
    }

    string? _dlLastRow;
    long _dlLastAt = long.MinValue / 2;

    /// act(): one press per row at a time (a double-click or held Enter opens once); a dry run or a note stays in the
    /// panel; success closes it; a failure is a sentence in the note, and a stale row re-reads.
    /// The press itself never changes the panel's height: the pop-up stands on the bar (its bottom edge fixed), so a
    /// shorter panel moves every row down under a still pointer. Clearing the last note on the press did that: a double
    /// click's first press opened a row and took the note's line away, the rows dropped by more than half a row, and the
    /// second press landed on the row above and opened that one too (stage 11 run 3: "expected 2, got 3"). The old note
    /// stays until the answer, which always sets its own (or closes the panel).
    async System.Threading.Tasks.Task DlAct(string verb, string? item, string what)
    {
        if (_dl == null || _dlLink == null) return;
        string k = item ?? "*";
        long now = Environment.TickCount64;
        if (!Mantle.Core.Features.Downloads.DownloadsKeys.Takes(_dl.Busy.Contains(k), _dlLastRow, _dlLastAt, k + "|" + verb, now) || !_dl.Busy.Add(k))
        { Host.Log.Line($"downloads: a second {verb} on the same row was ignored (one decision)"); return; }
        _dlLastRow = k + "|" + verb; _dlLastAt = now;
        int at = item == null ? -1 : _dl.Rows.ToList().FindIndex(b => (b.Tag as string) == item);
        string onRow = at >= 0 ? $" (row {at + 1} of {_dl.Rows.Count})" : "";
        PaintDl();
        var r = await _dlLink.CallAsync("downloads." + verb, item != null ? new JsonObject { ["item"] = item } : new JsonObject());
        _dl.Busy.Remove(k);
        if (r.Ok) {
            bool dry = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
            Host.Log.Line($"downloads: {verb} {(dry ? "was a dry run" : "done")}{onRow}");
            if (dry) _dl.Note = Str(r.Result, "reason") ?? "test run: nothing was opened";
            else if (Str(r.Result, "note") is { } note) _dl.Note = note;
            else { if (Popups?.OpenKey == DlKey) Popups.Close(verb + " done"); return; }
        } else {
            _dl.Note = what + ": " + r.Error;
            Host.Log.Line($"downloads: {verb} did not happen{onRow}");
            if (r.Error is { } e && (e.Contains("out of date", StringComparison.Ordinal) || e.Contains("no longer in Downloads", StringComparison.Ordinal))) { _ = DlRefresh(true); return; }
        }
        if (Popups?.OpenKey == DlKey) PaintDl();
    }

    async System.Threading.Tasks.Task DlDrag(string item, string nm)
    {
        if (_dl == null || _dlLink == null) return;
        var r = await _dlLink.CallAsync("downloads.drag", new JsonObject { ["item"] = item });
        _dl.DragAnswer(r.Ok ? Str(r.Result, "path") : null, r.Ok ? null : $"Could not drag {nm}: {r.Error}");
        if (!r.Ok && Popups?.OpenKey == DlKey) PaintDl();
    }

    /// keyboard(on): ask for the keyboard once the shelf is on screen (PopupHost.OnScreen); refused, the mouse still works
    /// and the list's tooltip says why.
    void DlKeyboard(bool on)
    {
        if (_dl == null || Popups == null) return;
        if (!on) { if (_dlKbd) { _dlKbd = false; Popups.GiveBackKeyboard("the Downloads shelf closed"); } return; }
        bool allowed = !Host.Args.Test || (Host.Env(DlKeysEnv) == "1" && !Host.Quiet);
        var (ok, words) = allowed ? Popups.TakeKeyboard(DlKey)
            : (false, "a test instance never takes the keyboard from whoever is at the machine");
        _dlKbd = ok; _dlUser = false;
        _dl.KbdNote = ok ? "" : "Arrow keys do not reach this panel: " + words.Split(", so ")[0];
        Host.Log.Line(ok ? "downloads: the shelf has the keyboard (asked once it was on screen)" : $"downloads: the keyboard was not taken ({words})");
        PaintDl();   // seats the focus (DlSeatFocus) once there are rows
    }

    /// The keys reach the shelf only through a focused element inside it (KeyDown routes up from the focus to Root;
    /// Root is a Grid, which cannot take focus itself). When the shelf holds the keyboard and nothing in it has the
    /// focus, the first row gets it programmatically. That is not the user's choice (_dlUser stays false), so the
    /// first Down is still the newest row (DownloadsKeys.From). Seated on every paint, because the rows can arrive
    /// AFTER the keyboard was taken: stage 9 run 45267680314 took the keyboard, then painted 3 rows, and no key
    /// ever reached the shelf ("the first Down did not reach the newest row").
    void DlSeatFocus()
    {
        if (_dl == null || !_dlKbd || _dlUser || Popups?.OpenKey != DlKey || _dl.Rows.Count == 0) return;
        if (_dl.Root.XamlRoot is not { } xr) return;
        if (Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(xr) is UIElement) return;
        bool ok = _dl.Rows[0].Focus(FocusState.Programmatic);
        if (Host.Args.Test) Host.Log.Line(ok ? "downloads: nothing in the shelf had the focus; the first row has it now (not the user's: the first Down is still the newest)" : "downloads: nothing in the shelf has the focus, and the first row did not take it");
    }

    /// onKey(): Up/Down walk the rows (the first Down is the newest, the first Up the oldest), Home/End, clamped; Esc closes.
    void DlKey_(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_dl == null || Popups?.OpenKey != DlKey) return;
        var k = e.Key;
        if (k == global::Windows.System.VirtualKey.Escape) { e.Handled = true; Popups.Close("Esc"); return; }
        bool down = k == global::Windows.System.VirtualKey.Down, up = k == global::Windows.System.VirtualKey.Up;
        bool home = k == global::Windows.System.VirtualKey.Home, end = k == global::Windows.System.VirtualKey.End;
        if (!(down || up || home || end) || _dl.Rows.Count == 0) return;
        var rows = _dl.Rows;
        var fe = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(_dl.Root.XamlRoot) as Button;
        // a row Windows focused on its own (the window's first focus when it took the keyboard) is not one the user
        // walked to or clicked: the first Down is still the newest (Electron: nothing has the focus yet)
        int at = Mantle.Core.Features.Downloads.DownloadsKeys.From(fe != null ? rows.ToList().IndexOf(fe) : -1, _dlUser);
        _dlUser = true;
        int to = Mantle.Core.Features.Downloads.DownloadsKeys.Walk(home ? "Home" : end ? "End" : down ? "Down" : "Up", at, rows.Count) ?? 0;
        e.Handled = true;
        if (Host.Args.Test) Host.Log.Line($"downloads: key {k}: row {to + 1} of {rows.Count} (from {(at < 0 ? "none" : "row " + (at + 1))})");
        rows[to].Focus(FocusState.Keyboard);
        rows[to].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    static string DlWho(object? o) => o is FrameworkElement f
        ? (AutomationProperties.GetAutomationId(f) is { Length: > 0 } id ? id : f.GetType().Name) + (f is Control c ? $" ({c.FocusState})" : "")
        : o?.GetType().Name ?? "nothing";

    static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
