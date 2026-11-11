using System;
using System.Linq;
using System.Text.Json.Nodes;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.Notifications;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App;

/// The More menu (lane native-r7-notif; bar.js buildMorePop, bar.css .pop .mprob / .hidtray / .item): "MANTLE", the
/// copy line of a test run, every chip in words (warn glyph, label, reason; the pip on "more" stands for them), the
/// "Tray icons not shown" line when the tray cannot be read, the icons Windows keeps in its hidden flyout (dimmed,
/// never clicked, the reason said under them) with the taskbar settings link, then Settings (Win+I), Restore Windows
/// taskbar, Restart Mantle and Quit Mantle (behind a second click that lapses after 4 s). Built afresh on every open.
/// In --test every item is a dry run (logged, nothing opened, restored or quit). It replaces the MANTLE_TEST_POPUPS
/// stand-in only when that variable is unset. Ids: more.panel, more.who, more.prob.&lt;key&gt;, more.hidden,
/// more.hidwhy, more.taskbar, more.settings, more.restore, more.restart, more.quit.
public sealed partial class BarWindow
{
    const double MoreMin = 210, MoreMax = 340;
    Grid? _more;
    DispatcherTimerLite? _quitArm;

    void StartMore()
    {
        if (Popups == null || Host.Env("MANTLE_TEST_POPUPS") == "1" && Host.Args.Kind != Mantle.Core.RunKind.Live) return;   // the framework's stand-in keeps "more"
        var popups = Popups;
        MoreBtn.Click += (_, _) => OpenMore();
        popups.Anchor(MoreBtn);
    }

    void OpenMore()
    {
        if (Popups == null) return;
        _popAnchor = MoreBtn;
        bool closing = Popups.OpenKey == "more";
        if (!closing) BuildMore();
        var body = _more!;
        var (w, h) = closing ? (MoreMin, 200.0) : MeasureMore();
        Popups.Toggle(new PopupKind("more", "More", w, h, Focusable: false, Instant: false, Away: true, () => body), MoreBtn);
        if (Popups.OpenKey == "more") Host.Log.Line($"more: the menu opened ({_chips.List(_shellState).Count} problem(s), {_trayHidden.Count} hidden tray icon(s))");
    }

    (double W, double H) MeasureMore()
    {
        _more!.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Clamp(Math.Ceiling(_more.DesiredSize.Width) + 2, MoreMin, MoreMax);
        _more.Width = w - 2;
        _more.Measure(new Windows.Foundation.Size(w - 2, double.PositiveInfinity));
        return (w, Math.Min(Math.Ceiling(_more.DesiredSize.Height) + 2, 640));
    }

    void ResizeMore() { if (_more != null && Popups?.OpenKey == "more") { var (w, h) = MeasureMore(); Popups.Resize("more", h, w); } }

    void BuildMore()
    {
        bool dark = _dark, test = Host.Args.Test;
        Brush T(string k) => BarLook.B(dark, k, Host.Log.Line);
        double fs = dark ? 13 : 12.5;
        var root = new Grid { RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light, MaxWidth = MoreMax - 2 };
        BarLook.Uia(root, "more.panel", "More");
        var s = new StackPanel { Padding = new Thickness(12, 11, 12, 11) };
        root.Children.Add(s);
        TextBlock Txt(string t, string ink, double size, string? id = null, bool wrap = true)
        {
            var x = new TextBlock { Text = t, FontFamily = BarLook.Font, FontSize = size, Foreground = T(ink), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxWidth = MoreMax - 2 - 24 };
            if (id != null) BarLook.Uia(x, id, t);
            return x;
        }
        var h3 = Txt("MANTLE", "LgInk3", dark ? 11 : 12.5, "more.head");
        h3.FontWeight = FontWeights.SemiBold; h3.CharacterSpacing = 40; h3.Margin = new Thickness(0, 0, 0, 8);
        s.Children.Add(h3);
        if (test) {
            var who = Txt("Test copy: it changes nothing on this PC", "LgInk3", fs, "more.who");
            BarLook.Line(who, fs, 1.45); who.Margin = new Thickness(0, -4, 0, 8);
            s.Children.Add(who);
        }
        // problems first: each chip as a warn glyph and its label, the reason under it in quiet ink
        var probs = _chips.List(_shellState);
        if (_chips.TrayUnavailable is { } tu) probs.Add(new Surfaces.Bar.Frame.Chip("tray", "Tray icons not shown", tu, ""));
        foreach (var c in probs) {
            bool warn = c.Tone == "warn";
            var line = new Grid { ColumnSpacing = 7, Margin = new Thickness(0, 0, 0, 9) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var ico = new Viewbox { Width = 14, Height = 14, Margin = new Thickness(0, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top,
                Child = BarLook.Glyph(new[] { "M8 2.4 14 13H2z", "M8 6.4v3.1", "M8 11.3h.01" }, 16, 1.3, warn ? "LgWarn" : "LgInk3") };
            line.Children.Add(ico);
            var tx = new StackPanel();
            var lb = Txt(c.Label, warn ? "LgWarn" : "LgInk", fs); BarLook.Line(lb, fs, 1.35);
            var rs = Txt(c.Reason, "LgInk3", fs); BarLook.Line(rs, fs, 1.4); rs.Margin = new Thickness(0, 1, 0, 0);
            tx.Children.Add(lb); tx.Children.Add(rs);
            Grid.SetColumn(tx, 1); line.Children.Add(tx);
            BarLook.Uia(line, "more.prob." + c.Key, c.Label);
            AutomationProperties.SetHelpText(line, (warn ? "warn: " : "") + c.Reason);
            s.Children.Add(line);
        }
        // the icons Windows keeps in its hidden flyout: listed dimmed, never clicked; a click says why under the row
        if (_trayHidden.Count > 0) {
            var hh = Txt("Hidden tray icons", "LgInk2", fs); hh.Margin = new Thickness(0, 2, 0, 3);
            s.Children.Add(hh);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(-4, 0, -4, 3) };
            BarLook.Uia(row, "more.hidden", "Hidden tray icons");
            var why = Txt("Windows hides these, so Mantle can't click them. Turn one on in taskbar settings to use it here.", "LgInk3", fs, "more.hidwhy");
            BarLook.Line(why, fs, 1.45); why.Margin = new Thickness(0, 0, 0, 9);
            foreach (var it in _trayHidden.ToList())
                row.Children.Add(TrayButton(it, reason => { why.Text = reason; why.Foreground = T("LgWarn"); AutomationProperties.SetName(why, reason); ResizeMore(); }));
            s.Children.Add(row); s.Children.Add(why);
            var tbs = MoreItem("more.taskbar", "Open taskbar settings", null, false, fs);
            tbs.HorizontalAlignment = HorizontalAlignment.Left; tbs.Padding = new Thickness(7, 5, 7, 5); tbs.Margin = new Thickness(0, -4, 0, 8);
            ToolTipService.SetToolTip(tbs, "Settings › Personalization › Taskbar › Other system tray icons");
            tbs.Click += (_, _) => { Popups?.Close("taskbar settings was chosen"); OpenUri("ms-settings:taskbar", "taskbar settings"); };
            s.Children.Add(tbs);
        }
        var set = MoreItem("more.settings", "Settings", "Win+I", false, fs);
        ToolTipService.SetToolTip(set, "Open Mantle Settings");
        set.Click += (_, _) => { Popups?.Close("Settings was chosen"); OpenUri("ms-settings:", "Settings"); };
        s.Children.Add(set);
        var restore = MoreItem("more.restore", "Restore Windows taskbar", null, false, fs);
        ToolTipService.SetToolTip(restore, "Bring back Windows' own taskbar in place of this bar");
        restore.Click += async (_, _) => {
            if (test) { Host.Log.Line("more: dry run (--test): would restore Windows' taskbar; nothing was changed"); Popups?.Close("Restore was chosen"); return; }
            restore.IsEnabled = false;
            var r = _r7Link != null ? await _r7Link.CallAsync("shell.restore") : Core.Commands.CommandResult.Fail("the bar is not connected to Mantle yet");
            if (r.Ok) { Popups?.Close("Restore was chosen"); return; }
            restore.IsEnabled = true; Arm(restore, "Could not restore: " + r.Error);
        };
        s.Children.Add(restore);
        var restart = MoreItem("more.restart", "Restart Mantle", null, false, fs);
        restart.Click += (_, _) => {
            // shell.restart is not native yet (docs/lanes/notifications.md, Deviations): said, never silent
            Host.Log.Line("more: Restart Mantle: restarting is not part of this build yet");
            Arm(restart, "Could not restart: restarting is not part of this build yet");
        };
        s.Children.Add(restart);
        var quit = MoreItem("more.quit", "Quit Mantle", null, true, fs);
        ToolTipService.SetToolTip(quit, "Close Mantle; Windows' own taskbar comes back");
        bool armed = false;
        quit.Click += async (_, _) => {
            if (!armed) {
                armed = true; Arm(quit, "Click again to quit");
                _quitArm?.Stop();
                _quitArm = new DispatcherTimerLite(4000, () => { _quitArm?.Stop(); armed = false; Arm(quit, null, "Quit Mantle"); });
                _quitArm.Start();
                return;
            }
            _quitArm?.Stop();
            if (test) { Host.Log.Line("more: dry run (--test): would quit Mantle; nothing was closed"); armed = false; Arm(quit, null, "Quit Mantle"); Popups?.Close("Quit was chosen"); return; }
            quit.IsEnabled = false;
            var r = _r7Link != null ? await _r7Link.CallAsync("shell.quit") : Core.Commands.CommandResult.Fail("the bar is not connected to Mantle yet");
            if (!r.Ok) { quit.IsEnabled = true; armed = false; Arm(quit, "Could not quit: " + r.Error); }
        };
        s.Children.Add(quit);
        _more = root;
    }

    /// .pop .item (and .mitem with its kbd): a full-width row, ink-2, padding 7 8 (dark 5 10, at least 30 tall).
    Button MoreItem(string id, string text, string? kbd, bool danger, double fs)
    {
        bool dark = _dark;
        var g = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = text, FontFamily = BarLook.Font, FontSize = fs, Foreground = BarLook.B(dark, "LgInk2", Host.Log.Line), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        if (kbd != null) {
            var k = new TextBlock { Text = kbd, FontFamily = BarLook.Font, FontSize = dark ? 12 : 11.5, Foreground = BarLook.B(dark, "LgInk3", Host.Log.Line), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(k, 1); g.Children.Add(k);
        }
        var b = new Button { Style = BarLook.Flat(), Content = g, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = dark ? new Thickness(10, 5, 10, 5) : new Thickness(8, 7, 8, 7), MinHeight = dark ? 30 : 0, Tag = danger ? "danger" : null };
        BarLook.Uia(b, id, text);
        return b;
    }

    /// .item.armed: warn ink on the warn-soft ground, with its new words (null: back to rest with <paramref name="rest"/>).
    void Arm(Button b, string? words, string? rest = null)
    {
        if (b.Content is not Grid g || g.Children[0] is not TextBlock t) return;
        t.Text = words ?? rest ?? t.Text;
        t.Foreground = BarLook.B(_dark, words != null ? "LgWarn" : "LgInk2", Host.Log.Line);
        b.Background = words != null ? BarLook.B(_dark, "LgWarnSoft", Host.Log.Line) : null;
        AutomationProperties.SetName(b, t.Text);
        AutomationProperties.SetItemStatus(b, words != null ? "armed" : "");
        ResizeMore();
    }

    void OpenUri(string target, string what)
    {
        if (Host.Args.Test) { Host.Log.Line($"more: dry run (--test): would open {what} ({target}); nothing was opened"); return; }
        try {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            Host.Log.Line($"more: opened {what} ({target})");
        } catch (Exception e) {
            Host.Log.Line($"more: {what} did not open ({target}): {e.Message}");
            if (_chips.Fail("open", what + " did not open", e.Message, "warn")) RenderPip(force: true);
        }
    }
}
