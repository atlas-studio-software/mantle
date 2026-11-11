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
    int _dlGen;
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
        _dlBtn.Click += (_, _) => OpenDownloads();
        _dlBtn.RightTapped += (_, e) => { e.Handled = true; OpenDownloads(); };
        RenderDlBtn();
        if (Popups == null) return;
        Popups.Anchor(_dlBtn);
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
        _dlDot.Fill = MTok("LgAcc");
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
        Popups.Toggle(new PopupKind(DlKey, "Downloads", DownloadsShelf.WidthDip, h, Focusable: false, Instant: true, Away: true, () => dl.Root), _dlBtn);
        if (Popups.OpenKey != DlKey || closing) return;
        Host.Log.Line($"downloads: the shelf opened ({DownloadsShelf.WidthDip}x{h} DIP)");
        _ = DlRefresh(false);
        // the ages re-render every 30 s while open, never at rest (BAR-878)
        _dlTick ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _dlTick.Tick -= DlTick; _dlTick.Tick += DlTick;
        _dlTick.Start();
        DlKeyboard(true);
    }
    void DlTick(object? s, object e) { if (Popups?.OpenKey == DlKey) PaintDl(); else _dlTick?.Stop(); }

    void PaintDl(bool measureOnly = false)
    {
        if (_dl == null) return;
        _dl.Paint(Dark, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (measureOnly || Popups?.OpenKey != DlKey) return;
        double h = Math.Min(560, _dl.Need());
        if (!Popups.Resize(DlKey, h)) { _dl.Note = "This panel may be cut off at the screen edge: Mantle could not make it taller"; _dl.Paint(Dark, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); }
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

    /// act(): one press per row at a time (a double-click or held Enter opens once); a dry run or a note stays in the
    /// panel; success closes it; a failure is a sentence in the note, and a stale row re-reads.
    async System.Threading.Tasks.Task DlAct(string verb, string? item, string what)
    {
        if (_dl == null || _dlLink == null) return;
        string k = item ?? "*";
        if (!_dl.Busy.Add(k)) { Host.Log.Line($"downloads: a second {verb} on the same row was ignored (one is in flight)"); return; }
        _dl.Note = "";
        PaintDl();
        var r = await _dlLink.CallAsync("downloads." + verb, item != null ? new JsonObject { ["item"] = item } : new JsonObject());
        _dl.Busy.Remove(k);
        if (r.Ok) {
            bool dry = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
            Host.Log.Line($"downloads: {verb} {(dry ? "was a dry run" : "done")}");
            if (dry) _dl.Note = Str(r.Result, "reason") ?? "test run: nothing was opened";
            else if (Str(r.Result, "note") is { } note) _dl.Note = note;
            else { if (Popups?.OpenKey == DlKey) Popups.Close(verb + " done"); return; }
        } else {
            _dl.Note = what + ": " + r.Error;
            Host.Log.Line($"downloads: {verb} did not happen");
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

    /// keyboard(on): ask for the keyboard on open; refused, the mouse still works and the list's tooltip says why.
    void DlKeyboard(bool on)
    {
        if (_dl == null || Popups == null) return;
        int gen = ++_dlGen;
        if (!on) { if (_dlKbd) { _dlKbd = false; Popups.GiveBackKeyboard("the Downloads shelf closed"); } return; }
        bool allowed = !Host.Args.Test || (Host.Env(DlKeysEnv) == "1" && !Host.Quiet);
        var (ok, words) = allowed ? Popups.TakeKeyboard(DlKey)
            : (false, "a test instance never takes the keyboard from whoever is at the machine");
        if (gen != _dlGen) return;
        _dlKbd = ok;
        _dl.KbdNote = ok ? "" : "Arrow keys do not reach this panel: " + words.Split(", so ")[0];
        Host.Log.Line(ok ? "downloads: the shelf has the keyboard" : $"downloads: the keyboard was not taken ({words})");
        if (ok && _dl.Rows.Count > 0) _dl.Root.Focus(FocusState.Programmatic);
        PaintDl();
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
        int at = fe != null ? rows.ToList().IndexOf(fe) : -1;
        int to = home ? 0 : end ? rows.Count - 1 : at < 0 ? (down ? 0 : rows.Count - 1) : Math.Max(0, Math.Min(rows.Count - 1, at + (down ? 1 : -1)));
        e.Handled = true;
        rows[to].Focus(FocusState.Keyboard);
        rows[to].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    static string? Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
