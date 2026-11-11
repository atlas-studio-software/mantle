using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.Notifications;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Lifecycle;
using Mantle.Core.State;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mantle.App;

/// The bell and its notification centre (lane native-r7-notif; ui/bar/notifications.js init / renderBell / health /
/// open): the bell shows the count of what arrived since the panel was last opened BESIDE the glyph (the button widens
/// only then, .notifs.new), goes quiet (ink-3) when the list cannot be read (.notifs.off) and says so in its tooltip;
/// a click (or a right-click, which only opens) opens the panel, one key "notifs" in the pop-up framework, instant and
/// without focus. A list that cannot be read is also the "notifications off" chip (health()). The lane's surface link
/// (App.xaml.cs, block "notifications wiring") also feeds the shell state to the chips (Surfaces/Bar/Chips) and the
/// "tray" event to the strip (Surfaces/Bar/Tray).
public sealed partial class BarWindow
{
    NotifPanel? _notifs;
    SurfaceLink? _r7Link;
    Button? _bell;
    Border? _badge;
    TextBlock? _badgeText;
    string _bellSig = "";

    /// Called once from the bar's start, after the pop-up framework exists (BarWindow.xaml.cs, after StartQuickSettings).
    void StartNotifications()
    {
        _bell = (VolumeBtn.Parent as Panel)?.Children.OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "bar.notifications");
        if (_bell == null || Popups == null) { Host.Log.Line("notifs: the bell or the pop-up framework is missing, so the notification centre does not open"); return; }
        var popups = Popups;
        _notifs = new NotifPanel(Host.Log.Line, new NotifPanel.Room(
            Resize: h => popups.Resize("notifs", h),
            Close: why => { if (popups.OpenKey == "notifs") popups.Close(why); },
            Keys: (combo, what) => _ = PressKeys(combo, what)));
        _notifs.Changed += p => { PaintBell(p); NotifsHealth(p); };
        // .notifs.new: the glyph and the count side by side (gap 5), the button as wide as they need (padding 0 8 0 9)
        _bell.Content = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(NotifsHost);
        _badgeText = new TextBlock { FontFamily = BarLook.Font, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        _badge = new Border { MinWidth = 16, Height = 16, Padding = new Thickness(4, 0, 4, 0), Child = _badgeText, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        BarLook.Uia(_badge, "bar.notifications.badge", "New notifications");
        row.Children.Add(_badge);
        _bell.Content = row;
        _bell.Click += (_, _) => OpenNotifications(toggle: true);
        _bell.RightTapped += (_, e) => { e.Handled = true; OpenNotifications(toggle: false); };
        popups.Anchor(_bell);
        popups.Closed += (key, why) => { if (key == "notifs" && _notifs is { Open: true }) _notifs.Closed(why); };
        Root.ActualThemeChanged += (_, _) => { _bellSig = ""; PaintBell(_notifs?.Data); PaintTray(force: true); };
        PaintBell(null);
        StartTray();
        StartMore();
    }

    /// Open the notification centre (the bell; the calendar's footer link, BAR-855, calls it without toggle).
    internal void OpenNotifications(bool toggle = false)
    {
        if (_notifs == null || Popups == null || _bell == null) return;
        bool closing = Popups.OpenKey == "notifs";
        if (closing && !toggle) return;
        _popAnchor = _bell;
        var panel = _notifs;
        double h = closing ? 0 : panel.Prepare(Dark);
        Popups.Toggle(new PopupKind("notifs", "Notifications", NotifPanel.WidthDip, closing ? 400 : h, Focusable: false, Instant: true, Away: true, () => panel.Root), _bell);
        if (Popups.OpenKey == "notifs" && !panel.Open) panel.Opened();
    }

    /// From the host thread's wiring, posted here: the lane's road to notifs.*, tray.*, sys.keys and the shell state.
    internal void AttachNotifications(SurfaceLink link)
    {
        _r7Link = link;
        if (_notifs == null) { Host.Log.Line("notifs: the host is up but the bar made no panel, so nothing is attached"); return; }
        _notifs.Attach(link);
        AttachTray(link);
        // bar.js applyState -> renderChips: the shell state's own chips join the failures on the "more" pip
        _shellState = link.State;
        link.OnState(s => { _shellState = s; RenderPip(); });
        RenderPip();
        // bar.js on('bar.winkey'): Win+N toggles the centre (nothing publishes it natively yet: the switcher lane's hook)
        link.On<JsonElement>("bar.winkey", e => {
            string? key = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
            bool only = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("onlyOpen", out var o) && o.ValueKind == JsonValueKind.True;
            switch (NotificationWords.WinKey(key, only, Popups?.OpenKey, _notifs != null)) {
                case "close": Popups?.Close("Win+N"); break;
                case "open": OpenNotifications(); break;
                case "windows": _ = PressKeys("win+n", "Windows notification centre"); break;
            }
        });
    }

    /// sys.keys through the host (a dry run in --test); a refusal is said in the log.
    async System.Threading.Tasks.Task PressKeys(string combo, string what)
    {
        if (_r7Link == null) { Host.Log.Line($"notifs: {what} could not open: the bar is not connected to Mantle yet"); return; }
        var r = await _r7Link.CallAsync("sys.keys", new JsonObject { ["combo"] = combo });
        string? why = !r.Ok ? r.Error : r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False
            ? (r.Result.TryGetProperty("reason", out var rs) ? rs.GetString() : "no reason was given") : null;
        if (why != null) Host.Log.Line($"notifs: {what} could not open: {why}");
    }

    /// notifications.js renderBell().
    void PaintBell(JsonObject? p)
    {
        if (_bell == null || _badge == null || _badgeText == null) return;
        int n = (p?["toasts"] as JsonArray)?.Count ?? 0, unread = (int)(NotificationCentre.Int(p?["unread"]) ?? 0);
        bool bad = p != null && NotificationCentre.IsFalse(p["ok"]), fresh = !bad && unread > 0;
        string tip = NotificationWords.BellTip(p);
        string sig = $"{bad}|{fresh}|{unread}|{tip}|{_dark}";
        if (sig == _bellSig) return;
        _bellSig = sig;
        NotifsHost.Children.Clear();
        NotifsHost.Children.Add(BarLook.Glyph(new[] { "M8 2.2a3.9 3.9 0 0 0-3.9 3.9v2.6L2.9 11h10.2l-1.2-2.3V6.1A3.9 3.9 0 0 0 8 2.2Z", "M6.4 12.9a1.7 1.7 0 0 0 3.2 0" }, 16, 1.25, bad ? "LgInk3" : "LgInk2"));
        _badge.Visibility = fresh ? Visibility.Visible : Visibility.Collapsed;
        _badgeText.Text = NotificationWords.Badge(unread);
        _badgeText.FontSize = _dark ? 11 : 10.5;
        _badgeText.Foreground = BarLook.B(_dark, "LgAccOn", Host.Log.Line);
        _badge.Background = BarLook.B(_dark, "LgAcc", Host.Log.Line);
        _badge.CornerRadius = new CornerRadius(_dark ? 6 : 8);   // --lg-r-chip (dark) / --lg-r-pill
        if (fresh) { _bell.ClearValue(FrameworkElement.WidthProperty); _bell.Padding = new Thickness(9, 0, 8, 0); }
        else { _bell.Width = 36; _bell.Padding = new Thickness(0); }
        ToolTipService.SetToolTip(_bell, tip);
        AutomationProperties.SetName(_bell, tip);
        AutomationProperties.SetItemStatus(_bell, bad ? "off" : fresh ? "new " + unread.ToString(CultureInfo.InvariantCulture) : n > 0 ? "some" : "none");
        Host.Log.Line($"notifs: the bell says \"{tip}\"");
    }

    /// notifications.js health(): a list that cannot be read is a chip, not only a quiet bell.
    void NotifsHealth(JsonObject? p)
    {
        bool changed = p != null && NotificationCentre.IsFalse(p["ok"])
            ? _chips.Fail("notifs", "notifications off", NotificationCentre.Str(p["reason"]) ?? "the notification list could not be read and no reason was given", "warn")
            : p != null && _chips.Clear("notifs");
        if (changed) RenderPip(force: true);
    }

    ShellState? _shellState;
}
