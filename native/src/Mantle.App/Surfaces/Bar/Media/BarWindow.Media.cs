using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.Media;
using Mantle.Core.Features.Media;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App;

/// The now-playing chip and its card (lane native-r7-media; bar.js renderMedia / paintMediaChip / mediaDo / openMedia,
/// index.html #media #mchip #mtog). The chip sits in the right island before the separator (Electron: chips, media,
/// tray, sep) and shows only while the host's "media" payload has a session: its artwork (else the app's icon, else a
/// note glyph), the song in its tooltip, and play/pause (absent, not greyed, under the protected media apps rule). A
/// click or right-click toggles the card (key "media": instant, no focus). Every press goes through media.control;
/// in --test that is a dry run (MediaService). Ids: docs/lanes/media.md.
public sealed partial class BarWindow
{
    StackPanel? _media;
    Button? _mchip, _mtog;
    Border? _mart;
    MediaCard? _mcard;
    SurfaceLink? _mediaLink;
    JsonObject? _mediaP;
    double _mediaAt;
    string? _martSrc = "\u0001";
    DispatcherTimer? _mediaTick;

    /// Called once from the bar's start, after the pop-up framework (BarWindow.xaml.cs, after StartQuickSettings).
    void StartMedia()
    {
        // the right island: the panel holding the volume button (BarWindow.xaml has no name on it), found by walking the
        // panels from the root (FrameworkElement.Parent was null here at start on the runner)
        string parentName = VolumeBtn.Parent?.GetType().Name ?? "none";
        if ((PanelOf(Root, VolumeBtn) ?? PanelOf(Pill, VolumeBtn) ?? VolumeBtn.Parent as Panel) is not StackPanel right) { Host.Log.Line($"media: the bar's right island was not found (the volume button's parent is {parentName}), so there is no now-playing chip"); return; }
        _media = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_media, "bar.media");
        AutomationProperties.SetName(_media, "Now playing");
        var style = Root.Resources.TryGetValue("LgBtn", out var st) ? st as Style : null;
        _mart = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5) };
        _mchip = new Button { Style = style, Padding = new Thickness(5, 0, 5, 0), Content = _mart, MinWidth = 0 };
        AutomationProperties.SetAutomationId(_mchip, "bar.media.chip");
        _mtog = new Button { Style = style, Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(-3, 0, 0, 0), MinWidth = 0 };
        AutomationProperties.SetAutomationId(_mtog, "bar.media.toggle");
        _media.Children.Add(_mchip); _media.Children.Add(_mtog);
        right.Children.Insert(0, _media);
        _mcard = new MediaCard(Host.Log.Line, () => Environment.TickCount64);
        _mcard.Control = a => MediaDo(a, "card");
        _mcard.Pick = (aumid, label) => _ = MediaPick(aumid, label);
        _mchip.Click += (_, _) => OpenMedia();
        _mchip.RightTapped += (_, e) => { e.Handled = true; OpenMedia(); };
        _mtog.Click += (_, _) => MediaDo("toggle", Popups?.OpenKey == "media" ? "card" : "chip");
        if (Popups != null) {
            Popups.Anchor(_mchip);
            Popups.Closed += (key, why) => {
                if (key != "media" || _mcard is not { Open: true }) return;
                _mcard.Open = false;
                _mediaTick?.Stop();
                if (_mediaLink != null) _ = _mediaLink.CallAsync("media.card", new JsonObject { ["open"] = false });
                Host.Log.Line($"media: the card closed ({why})");
            };
        }
    }

    static Panel? PanelOf(UIElement from, UIElement child)
    {
        if (from is Panel p) {
            if (p.Children.Contains(child)) return p;
            foreach (var c in p.Children) if (PanelOf(c, child) is { } hit) return hit;
        } else if (from is Border b && b.Child != null) return PanelOf(b.Child, child);
        return null;
    }

    /// From the host thread's wiring (App.xaml.cs, block "media wiring"), posted here.
    internal void AttachMedia(SurfaceLink link)
    {
        _mediaLink = link;
        link.On<JsonElement>(MediaService.Event, e => RenderMedia(e.ValueKind == JsonValueKind.Object ? JsonNode.Parse(e.GetRawText()) as JsonObject : null));
    }

    /// renderMedia(): keep the payload, raise or clear "Now playing not reading", paint the chip (and the open card).
    void RenderMedia(JsonObject? p)
    {
        _mediaP = p;
        _mediaAt = Environment.TickCount64;
        if (p != null && p["ok"] is JsonValue v && v.TryGetValue<bool>(out var ok) && !ok) {
            if (_chips.Fail("media", "Now playing not reading", MediaService.Str(p["reason"]) ?? "what is playing could not be read, and no reason was given", "warn")) RenderPip(force: true);
        } else if (_chips.Clear("media")) RenderPip(force: true);
        PaintMediaChip();
    }

    void PaintMediaChip()
    {
        if (_media == null || _mchip == null || _mtog == null || _mart == null) return;
        var s = MediaView.Shown(_mediaP);
        _media.Visibility = s == null ? Visibility.Collapsed : Visibility.Visible;
        if (s == null) {
            if (Popups?.OpenKey == "media") Popups.Close("nothing is playing any more");
            Host.Log.Line("media: the chip is hidden (no session shown)");
            return;
        }
        bool playing = MediaService.Bool(s["playing"]);
        string? art = MediaService.Str(s["artwork"]);
        string? src = art ?? MediaService.Str(s["app"]?["icon"]);
        _mart.Background = MTok("LgTile");
        if (src != _martSrc) { _martSrc = src; _ = ChipArt(src, art != null ? 20 : 14); }
        string tip = MediaView.ChipTip(s);
        ToolTipService.SetToolTip(_mchip, tip);
        AutomationProperties.SetName(_mchip, MediaView.Line(s));
        AutomationProperties.SetHelpText(_mchip, tip);
        AutomationProperties.SetItemStatus(_mchip, playing ? "playing" : "paused");
        bool guarded = MediaView.Guarded(s);
        _mtog.Visibility = guarded ? Visibility.Collapsed : Visibility.Visible;   // absent, not greyed
        if (!guarded) {
            var can = MediaView.Can(s, "toggle");
            string g = playing ? "pause" : "play";
            var glyph = MediaPics.Glyph(g, 12, MTok(can.Ok ? "LgInk" : "LgInk3"));
            if (!can.Ok) glyph.Opacity = .5;
            _mtog.Content = glyph;
            string tt = MediaView.ToggleTip(s);
            ToolTipService.SetToolTip(_mtog, tt);
            AutomationProperties.SetName(_mtog, tt);
            AutomationProperties.SetItemStatus(_mtog, g + (can.Ok ? "" : "; off"));
        }
        Host.Log.Line($"media: the chip shows {MediaView.App(s)} ({(playing ? "playing" : "paused")}{(guarded ? ", controls off" : "")})");
        if (Popups?.OpenKey == "media") PaintMediaCard();
    }

    async System.Threading.Tasks.Task ChipArt(string? src, double size)
    {
        var img = await MediaPics.Load(src);
        if (_mart == null || src != _martSrc) return;
        _mart.Child = img != null ? new Image { Source = img, Width = size, Height = size, Stretch = size >= 20 ? Stretch.UniformToFill : Stretch.Uniform }
            : MediaPics.Glyph("note", 12, MTok("LgInk2"));
    }

    /// mediaDo(): a press the app does not accept says why (in the card, or a chip from the bar); a second press while
    /// one is in flight is ignored; a refusal says "<verb> did not happen: <why>".
    void MediaDo(string action, string where)
    {
        var s = MediaView.Shown(_mediaP);
        var can = MediaView.Can(s, action);
        if (!can.Ok) { MediaSay(can.Why!, where); return; }
        if (_mcard == null || _mcard.Busy.Length > 0 || _mediaLink == null) return;
        _mcard.Busy = action;
        if (Popups?.OpenKey == "media") PaintMediaCard();
        _ = MediaSend(action, MediaService.Str(s!["aumid"])!, where);
    }

    async System.Threading.Tasks.Task MediaSend(string action, string aumid, string where)
    {
        var r = await _mediaLink!.CallAsync("media.control", new JsonObject { ["aumid"] = aumid, ["action"] = action });
        if (r.Ok) {
            _mcard!.Note = "";
            if (_chips.Clear("mediactl")) RenderPip(force: true);
        } else MediaSay($"{MediaView.Verb[action]} did not happen: {r.Error}", where);
        _mcard!.Busy = "";
        if (Popups?.OpenKey == "media") PaintMediaCard();
    }

    /// mediaSay(): into the open card's note, or a warn chip when it came from the bar.
    void MediaSay(string text, string where)
    {
        Host.Log.Line($"media: {where}: {text}");
        if (where == "card" && Popups?.OpenKey == "media" && _mcard != null) { _mcard.Note = text; PaintMediaCard(); }
        else if (_chips.Fail("mediactl", "Playback did not respond", text, "warn")) RenderPip(force: true);
    }

    async System.Threading.Tasks.Task MediaPick(string? aumid, string label)
    {
        if (_mediaLink == null) return;
        Host.Log.Line($"media: the card picked {label} ({aumid ?? "automatic"})");
        var r = await _mediaLink.CallAsync("media.select", new JsonObject { ["aumid"] = aumid });
        if (r.Ok) { if (_mcard != null) _mcard.Note = ""; RenderMedia(JsonNode.Parse(r.Result.GetRawText()) as JsonObject); }
        else MediaSay(aumid != null ? $"could not switch to {label}: {r.Error}" : $"could not go back to automatic: {r.Error}", "card");
    }

    /// openMedia(): toggle the card above the chip; opening re-reads media.get so the position and buttons are fresh.
    void OpenMedia()
    {
        if (_mcard == null || Popups == null || _mchip == null) return;
        var s = MediaView.Shown(_mediaP);
        _popAnchor = _mchip;
        bool closing = Popups.OpenKey == "media";
        if (!closing && s != null) { _mcard.Note = ""; _mcard.Paint(_mediaP, s, _mediaAt, Dark); }
        double h = closing || s == null ? 200 : Math.Min(600, _mcard.Need());
        var card = _mcard;
        Popups.Toggle(new PopupKind("media", "Now playing", MediaCard.WidthDip, h, Focusable: false, Instant: true, Away: true, () => card.Root), _mchip);
        if (Popups.OpenKey != "media" || card.Open) return;
        card.Open = true;
        Host.Log.Line($"media: the card opened ({MediaCard.WidthDip}x{h} DIP)");
        if (_mediaLink == null) return;
        _ = _mediaLink.CallAsync("media.card", new JsonObject { ["open"] = true });
        _ = Refresh(_mediaLink);
        async System.Threading.Tasks.Task Refresh(SurfaceLink link)
        {
            var r = await link.CallAsync("media.get");
            if (r.Ok) RenderMedia(JsonNode.Parse(r.Result.GetRawText()) as JsonObject);
            else MediaSay("could not refresh: " + r.Error, "card");
        }
    }

    double _mediaRoom;
    /// mediaPaint(): repaint in place, re-ask for room when the height changed, and tick the progress while playing.
    void PaintMediaCard()
    {
        var s = MediaView.Shown(_mediaP);
        if (_mcard == null || s == null || Popups == null) return;
        _mcard.Paint(_mediaP, s, _mediaAt, Dark);
        double h = Math.Min(600, _mcard.Need());
        if (h != _mediaRoom && Popups.OpenKey == "media" && !Popups.Resize("media", h)) {
            _mcard.Note = "Part of this card may be cut off: the pop-up could not be made taller";
            _mcard.Paint(_mediaP, s, _mediaAt, Dark);
        }
        _mediaRoom = h;
        _mediaTick ??= MakeTick();
        if (MediaView.Pos(s, 0) != null && MediaService.Bool(s["playing"])) _mediaTick.Start(); else _mediaTick.Stop();
    }

    DispatcherTimer MakeTick()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => {
            var cur = MediaView.Shown(_mediaP);
            if (Popups?.OpenKey != "media" || cur == null || _mcard == null || !_mcard.PaintProgress(cur, _mediaAt)) t.Stop();
        };
        return t;
    }

    /// The bar theme's --lg-* token, read in code.
    Brush MTok(string key)
    {
        string theme = Dark ? "Dark" : "Light";
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary t && t.TryGetValue(key, out var v) && v is Brush b) return b;
        return (Brush)(Application.Current.Resources.TryGetValue(key, out var x) && x is Brush bb ? bb : new SolidColorBrush());
    }
}
