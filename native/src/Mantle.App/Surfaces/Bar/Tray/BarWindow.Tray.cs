using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Surfaces.Bar.Notifications;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App;

/// The notification-area strip (lane native-r7-notif; bar.js renderTray / trayButton / toneOf, bar.css .tray .ti): the
/// icons Windows shows on its own taskbar, left of the cluster's separator, 26 px buttons 2 apart, each a 20 px tile with
/// its 16 px picture or its letter. promoted:false icons (Windows' hidden-icons flyout) are not on the strip: the More
/// menu lists them, dimmed. An icon Mantle cannot click is dimmed (.ti.off) and its click says why as the "can't click"
/// chip; nothing is sent for it. A WHITE icon (drawn for Windows' dark taskbar) sits on an ink-2 plate on light glass,
/// a BLACK one on the plate on dark glass (TrayTone, measured once per picture, TrayToneCache). The strip is rebuilt
/// only when what it draws would change (TrayTone.Signature), so icons never blink. Clicks go through tray.click (a dry
/// run in --test). Ids: bar.tray, bar.tray.&lt;icon id&gt; (ItemStatus: its tone, " off" when it cannot be clicked).
public sealed partial class BarWindow
{
    StackPanel? _tray;
    JsonObject? _trayNow;
    string? _traySig;
    readonly List<JsonObject> _trayHidden = new();
    readonly TrayToneCache _tones = new();

    void StartTray()
    {
        if (VolumeBtn.Parent is not Panel cluster) { Host.Log.Line("tray: the bar's right cluster was not found, so the notification area is not drawn"); return; }
        _tray = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        BarLook.Uia(_tray, "bar.tray", "Notification area");
        cluster.Children.Insert(0, _tray);   // before the separator (index.html: #tray, then .sep)
    }

    void AttachTray(SurfaceLink link)
    {
        link.On<TrayPayload>(NotificationArea.Event, p => {
            if (p == null) return;
            _trayNow = JsonNode.Parse(p.Body.GetRawText()) as JsonObject;
            PaintTray();
        });
    }

    void PaintTray(bool force = false)
    {
        if (_tray == null) return;
        var items = _trayNow?["items"] as JsonArray;
        bool unavailable = NotificationCentre.Str(_trayNow?["status"]) == "unavailable";
        string sig = TrayTone.Signature(unavailable, items);
        if (!force && sig == _traySig) return;
        _traySig = sig;   // forced: a theme change (the plates and letter tiles take the new theme's ink; tones are cached)
        _tray.Children.Clear(); _trayHidden.Clear();
        string? was = _chips.TrayUnavailable;
        _chips.TrayUnavailable = unavailable ? NotificationCentre.Str(_trayNow?["reason"]) ?? "Windows did not share its tray icons, so they are not shown here" : null;
        if (was != _chips.TrayUnavailable) RenderPip(force: true);
        if (!unavailable)
            foreach (var it in (items ?? new JsonArray()).OfType<JsonObject>()) {
                if (it["promoted"] is JsonValue pv && pv.TryGetValue<bool>(out var promoted) && !promoted) { _trayHidden.Add(it); continue; }
                _tray.Children.Add(TrayButton(it, reason => {
                    if (_chips.Fail("trayclick", "can’t click " + (NotificationCentre.Str(it["title"]) ?? "that icon"), reason, "warn")) RenderPip(force: true);
                }));
            }
        _tray.Visibility = _tray.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Host.Log.Line(unavailable ? "tray: the strip is empty: " + _chips.TrayUnavailable
            : $"tray: the strip shows {_tray.Children.Count} icon{(_tray.Children.Count == 1 ? "" : "s")}, {_trayHidden.Count} hidden in Windows' flyout");
    }

    /// bar.js trayButton(): one icon as a button, on the strip or in the More menu's hidden row; onRefuse(reason) is how
    /// the caller says "this one cannot be clicked" out loud.
    internal Button TrayButton(JsonObject it, Action<string> onRefuse)
    {
        string id = NotificationCentre.Str(it["id"]) ?? "";
        string name = NotificationCentre.Str(it["title"]) ?? NotificationCentre.Str(it["tooltip"]) ?? (id.Length > 0 ? id : "tray icon");
        bool can = !(it["clickable"] is JsonValue cv && cv.TryGetValue<bool>(out var c) && !c);
        string why = NotificationCentre.PlainOr(NotificationCentre.Str(it["reason"]), "Mantle cannot click " + name + ", and Windows did not say why");
        string tip = can ? name : name + "\n\n" + why;
        var b = new Button { Style = BarLook.Flat(), Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        BarLook.Uia(b, "bar.tray." + id, name);
        var tile = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Opacity = can ? 1 : 0.42 };
        b.Content = tile;
        string tone = "normal";
        void Status() { AutomationProperties.SetItemStatus(b, tone + (can ? "" : " off")); AutomationProperties.SetHelpText(b, tip); ToolTipService.SetToolTip(b, tip); }
        void Letter()
        {
            tile.Background = BarLook.B(_dark, "LgTile", Host.Log.Line);
            tile.Child = new TextBlock { Text = NotificationWords.Letter(name), FontFamily = BarLook.Font, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = BarLook.B(_dark, "LgInk", Host.Log.Line), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
        Status();
        string? icon = NotificationCentre.Str(it["icon"]);
        if (icon != null) {
            var img = new Image { Width = 16, Height = 16, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            tile.Child = img;
            _ = SetTrayPic(img, icon, Letter);
            ToneOf(icon, (t, w) => {
                tone = t;
                if (w != null) tip += "\n(its contrast could not be checked, so it is drawn as it came: " + w + ")";
                Plate(tile, tone, !can);
                Status();
            });
        } else Letter();
        b.Click += (_, _) => { if (!can) { onRefuse(why); return; } _ = TrayClick(id, "left", name); };
        b.RightTapped += (_, e) => { e.Handled = true; if (!can) { onRefuse(why); return; } _ = TrayClick(id, "right", name); };
        return b;
    }

    async System.Threading.Tasks.Task SetTrayPic(Image img, string url, Action fallback)
    {
        var src = await BarLook.Pic(url, Host.Log.Line);
        if (src != null) img.Source = src; else fallback();
    }

    /// bar.css .ti.light / .ti.dark: the plate goes under whichever tone the theme would swallow.
    void Plate(Border tile, string tone, bool off)
    {
        bool plate = _dark ? tone == "dark" : tone == "light";
        if (tile.Child is Image) tile.Background = plate ? BarLook.B(_dark, "LgInk2", Host.Log.Line) : null;
        tile.Opacity = off ? 0.42 : 1;
    }

    /// bar.js toneOf(): each picture measured once (alpha-weighted luminance), shared by every caller asking meanwhile.
    void ToneOf(string src, Action<string, string?> done)
    {
        if (!_tones.Begin(src, done)) return;
        _ = Measure();
        async System.Threading.Tasks.Task Measure()
        {
            var (px, why) = await BarLook.Pixels(src);
            _tones.Settle(src, px != null ? TrayTone.Of(px) : "normal", px != null ? null : why);
        }
    }

    async System.Threading.Tasks.Task TrayClick(string id, string button, string name)
    {
        if (_r7Link == null) { Host.Log.Line($"tray: {name} was not clicked: the bar is not connected to Mantle yet"); return; }
        var r = await _r7Link.CallAsync("tray.click", new JsonObject { ["id"] = id, ["button"] = button });
        if (r.Ok) { if (_chips.Clear("tray")) RenderPip(force: true); return; }
        Host.Log.Line($"tray: the {button} click on {name} did not go through: {r.Error}");
        if (_chips.Fail("tray", "Tray click failed", r.Error ?? "the click did not go through", "warn")) RenderPip(force: true);
    }
}
