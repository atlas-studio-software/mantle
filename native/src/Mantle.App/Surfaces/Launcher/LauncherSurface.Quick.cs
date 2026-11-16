using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.QuickLinks;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The launcher's Quick links mode (Win+X; ui/launcher/launcher.js mode 'quick', lane keysrun): the same window, no new one.
/// The rows, the submenu, the underlined-letter keys, ask-first arming and the once-per-decision guard are
/// <see cref="QuickMenu"/> (Mantle.Core, unit-tested); this draws them and sends only a row id (quicklinks.open /
/// quicklinks.power). Win+X again over Quick links closes it; over the plain launcher it switches to Quick links. Hiding
/// returns the launcher to its apps mode. Timers only while armed (5 s) or after a live action (1.4 s): none at rest.
/// </summary>
internal sealed partial class LauncherSurface
{
    const string QuickMode = "quick";
    const string AppsPlaceholder = "Apps, files, settings, commands, or a URL";
    string _mode = "apps";
    readonly QuickMenu _quick = new();
    readonly HashSet<Row> _underlined = [];
    DispatcherQueueTimerLike? _armTimer, _hideTimer;

    static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// Create(): Win+X reaches this window's thread through the feature's hook.
    void QuickCreate()
    {
        QuickLinksFeature.ToggleLauncher = why => _ctx.Dispatcher.TryEnqueue(() => ToggleQuick(why));
    }

    void QuickDestroy()
    {
        QuickLinksFeature.ToggleLauncher = null;
        _armTimer?.Stop(); _hideTimer?.Stop();
    }

    /// Win+X: closes Quick links when it is up (as Windows' Win+X menu does); over the plain launcher, switches to it.
    void ToggleQuick(string why)
    {
        if (_shown && _mode == QuickMode) { Hide(why + " again"); return; }
        EnterQuick();
        if (_shown) { _ctx.Log.Line("quick links: " + why + " over the open launcher"); QuickOpening(); return; }
        Show(why);
    }

    void EnterQuick()
    {
        _mode = QuickMode;
        FlushSearch();
        _q.IsReadOnly = true;                                              // Quick links takes letters as keys, not text
        _q.PlaceholderText = QuickMenu.Placeholder;
        if (_q.Text.Length > 0) _q.Text = "";
        _quick.Reset();
    }

    /// Show()'s rows in Quick links mode: drawn at once from the table (no round trip). False in apps mode.
    bool QuickOpening()
    {
        if (_mode != QuickMode) return false;
        _view = _quick.Rows();
        _sel = 0;
        Render();
        int n = _view.Flat.Count;
        _ctx.Log.Line($"quick links: {n} rows in {_view.Groups.Count} groups ({string.Join(", ", _view.Groups.Select(g => g.Label))})");
        if (_shown) NextFrame(() => { LastOpenMs = Ms(_openTs); _ctx.Log.Line($"first frame with rows {LastOpenMs:0.0} ms after the open (quick links; {n} row(s))"); });
        return true;
    }

    /// Hide(): back to the apps mode, so the next Win tap opens the plain launcher.
    void QuickHidden()
    {
        _armTimer?.Stop(); _armTimer = null;
        _hideTimer?.Stop(); _hideTimer = null;
        if (_mode != QuickMode) return;
        _mode = "apps";
        _quick.Reset();
        _q.IsReadOnly = false;
        _q.PlaceholderText = AppsPlaceholder;
    }

    void QuickRedraw()
    {
        int keep = _sel;
        string? id = _view.Flat.Count > _sel ? _view.Flat[_sel].Item.Id : null;
        _view = _quick.Rows();
        _sel = id != null ? Math.Max(0, _view.Flat.ToList().FindIndex(r => r.Item.Id == id)) : Math.Min(keep, Math.Max(0, _view.Flat.Count - 1));
        Render();
    }

    /// FillRow: a quick row's name with its access letter underlined, as Windows' Win+X menu draws it. True when it drew the name.
    bool QuickName(Row r, LauncherItem it, string name)
    {
        var parts = QuickMenu.LabelOf(it);
        if (parts == null)
        {
            if (!_underlined.Remove(r)) return false;
            r.Name.Inlines.Clear();
            r.Name.Text = name;
            return true;
        }
        _underlined.Add(r);
        r.Name.Inlines.Clear();
        if (parts.Before.Length > 0) r.Name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = parts.Before });
        var u = new Underline();
        u.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = parts.Letter });
        r.Name.Inlines.Add(u);
        if (parts.After.Length > 0) r.Name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = parts.After });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessKey(r.Root, parts.Key.ToUpperInvariant());
        return true;
    }

    /// Select(): moving off an armed row disarms it.
    void QuickMoved()
    {
        if (_mode != QuickMode || _view.Flat.Count <= _sel) return;
        string? before = _quick.ArmedId;
        _quick.Moved(_view.Flat[_sel].Item.Id);
        if (before != null && _quick.ArmedId == null) { _armTimer?.Stop(); _armTimer = null; _ctx.Log.Line("quick links: moved off " + before + ", so it is no longer armed"); QuickRedraw(); }
    }

    /// OnKey's first ask: Esc / Left / Right and the letters in Quick links. True = handled.
    bool QuickKey(KeyRoutedEventArgs e)
    {
        if (_mode != QuickMode) return false;
        string? k = e.Key switch
        {
            VirtualKey.Escape => "Escape", VirtualKey.Left => "Left", VirtualKey.Right => "Right",
            >= VirtualKey.A and <= VirtualKey.Z => ((char)('a' + (e.Key - VirtualKey.A))).ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (e.Key - VirtualKey.Number0))).ToString(),
            _ => null,
        };
        if (k == null) return false;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (k.Length == 1 && (ctrl || alt)) return false;
        string? sel = _view.Flat.Count > _sel ? _view.Flat[_sel].Item.Id : null;
        var step = _quick.Key(k, e.KeyStatus.WasKeyDown, sel);
        e.Handled = step.Kind != "none" || k.Length == 1;                 // a letter never reaches the (read-only) box
        if (_ctx.Test) _ctx.Log.Line($"quick links: key {k}{(e.KeyStatus.WasKeyDown ? " (held)" : "")}: {step.Kind}{(step.Id != null ? " " + step.Id : "")}{(step.Arg != null ? " (" + step.Arg + ")" : "")}");
        switch (step.Kind)
        {
            case "close": Hide("Esc"); break;
            case "back": _armTimer?.Stop(); _armTimer = null; QuickRedrawTop(); break;
            case "sub": QuickRedrawTop(); break;
            case "select":
                int at = _view.Flat.ToList().FindIndex(r => r.Item.Id == step.Id);
                if (at >= 0) { Select(at); if (step.Activate) QuickActivate(); }
                break;
        }
        return e.Handled;
    }

    void QuickRedrawTop() { _view = _quick.Rows(); _sel = 0; Render(); _ctx.Log.Line("quick links: " + (_quick.Sub != null ? "the " + QuickMenu.SubLabel + " submenu" : "the main list") + $" ({_view.Flat.Count} rows)"); }

    /// Activate()'s first ask in Quick links: a row by Enter, a click or its letter. True = handled.
    bool QuickActivate()
    {
        if (_mode != QuickMode) return false;
        _hideTimer?.Stop(); _hideTimer = null;
        if (_view.Flat.Count <= _sel) return true;
        var it = _view.Flat[_sel].Item;
        var step = _quick.Activate(it, NowMs);
        switch (step.Kind)
        {
            case "sub": QuickRedrawTop(); break;
            case "armed":
                _ctx.Log.Line($"quick links: {it.Name} is armed: {it.Confirm}");
                _armTimer?.Stop();
                _armTimer = DispatcherQueueTimerLike.After(_ctx.Dispatcher, QuickMenu.ArmMs, () => { _armTimer = null; if (_quick.ArmExpired(NowMs)) { _ctx.Log.Line("quick links: the ask ran out"); QuickRedraw(); } });
                QuickRedraw();
                break;
            case "page":
                if (step.Arg == "search")
                {
                    QuickHidden();
                    _ctx.Log.Line("quick links: Search: the launcher's search");
                    RunSearch(_q.Text, 0);
                    _q.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
                }
                else { _quick.SetNote(it.Id, "Run (Win+R) is not in the native build yet, so it cannot open from here.", false); _ctx.Log.Line("quick links: Run is not built natively yet"); QuickRedraw(); }
                break;
            case "open": Send(it, "quicklinks.open", new JsonObject { ["link"] = step.Arg }, "open"); break;
            case "power": _armTimer?.Stop(); _armTimer = null; Send(it, "quicklinks.power", new JsonObject { ["action"] = step.Arg, ["confirmed"] = step.Confirmed }, "power"); break;
            case "none": if (step.Arg != null) _ctx.Log.Line($"quick links: {it.Name} held back ({step.Arg})"); break;
        }
        return true;
    }

    /// activateQuick / activatePower's answer: a dry run or a fallback is said under the row (the launcher stays up for a
    /// dry run, and goes 1.4 s later otherwise); a refusal stays up with its reason; a plain ok hides it.
    async void Send(LauncherItem it, string cmd, JsonObject args, string action)
    {
        var r = await _ctx.CallAsync(cmd, args);
        _quick.Done(it.Id, action);
        if (_mode != QuickMode) return;
        bool ok = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        bool dry = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("dryRun", out var d) && d.ValueKind == JsonValueKind.True;
        string? why = r.Ok && r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var w) ? w.GetString() : null;
        _ctx.Log.Line($"quick links: {it.Name}: {(ok ? (dry ? "dry run" : "ok") : "refused")}{(why != null ? " (" + why + ")" : r.Ok ? "" : " (" + r.Error + ")")}");
        if (!r.Ok) { _quick.SetNote(it.Id, "The quick link request failed: " + r.Error, false); QuickRedraw(); return; }
        if (!ok) { _quick.SetNote(it.Id, why ?? it.Name + " didn't open, and no reason came back.", false); QuickRedraw(); return; }
        if (dry || why != null)
        {
            _quick.SetNote(it.Id, why ?? "", true);
            QuickRedraw();
            if (!dry) _hideTimer = DispatcherQueueTimerLike.After(_ctx.Dispatcher, 1400, () => { _hideTimer = null; Hide("launched"); });
            return;
        }
        Hide("launched");
    }
}
