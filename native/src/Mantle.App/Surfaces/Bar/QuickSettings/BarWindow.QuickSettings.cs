using System;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.QuickSettings;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mantle.App;

/// The quick-settings pop-up (lane native-bar-qs): the speaker, the network and the battery buttons open it (bar.js
/// openQs: el.volume / el.network / el.battery, BAR-392's anchors), one key "qs" in the pop-up framework (Shell/Popup):
/// instant (made once, parked between opens) and no focus (the bar's own pop-ups never take the keyboard; the Wi-Fi
/// password field of part 2 is the exception, Q-W6). Its height is measured before each open from what it will draw.
public sealed partial class BarWindow
{
    QsPanel? _qs;
    DispatcherQueueTimer? _qsCheck;
    /// The panel, for the host's wiring (App.xaml.cs, block QuickSettingsWiring).
    internal QsPanel? QuickSettings => _qs;

    /// Called once from the bar's start, after the pop-up framework exists (BarWindow.xaml.cs, after StartFrame).
    void StartQuickSettings()
    {
        if (Popups == null) { Host.Log.Line("qs: the pop-up framework is not up, so quick settings does not open"); return; }
        _qs = new QsPanel(Host.Log.Line, Host.Args.Test);
        foreach (var b in new[] { VolumeBtn, NetworkBtn, BatteryBtn }) {
            var anchor = b;
            anchor.Click += (_, _) => OpenQuickSettings(anchor);
            Popups.Anchor(anchor);
        }
    }

    /// Open (or, from its own button, close) the panel above <paramref name="anchor"/>.
    internal void OpenQuickSettings(FrameworkElement anchor)
    {
        if (_qs == null || Popups == null) return;
        _popAnchor = anchor;
        bool closing = Popups.OpenKey == "qs";
        double h = closing ? 0 : _qs.Prepare(Dark);
        var qs = _qs;
        Popups.Toggle(new PopupKind("qs", "Quick settings", QsPanel.WidthDip, closing ? 400 : h, Focusable: false, Instant: true, Away: true, () => qs.Root), anchor);
        if (Popups.OpenKey == "qs" && !qs.Open) {
            qs.Opened();
            Host.Log.Line($"qs: the panel opened from {Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(anchor)} ({QsPanel.WidthDip}x{h} DIP)");
            // the framework has no close event yet (requested in docs/lanes/quick-settings.md): the panel's watch is let go
            // within 250 ms of the pop-up closing, however it closed (outside click, Esc, another pop-up, the bar hiding)
            _qsCheck ??= DispatcherQueue.CreateTimer();
            _qsCheck.Interval = TimeSpan.FromMilliseconds(250);
            _qsCheck.IsRepeating = true;
            _qsCheck.Tick -= QsCheck; _qsCheck.Tick += QsCheck;
            _qsCheck.Start();
        }
    }

    void QsCheck(DispatcherQueueTimer t, object o)
    {
        if (Popups?.OpenKey == "qs") return;
        t.Stop();
        if (_qs is { Open: true }) { _qs.Closed(); Host.Log.Line("qs: the panel closed; its watch is let go"); }
    }

    /// From the host thread's wiring, posted here: the panel's road to the qs.* and powermode.* commands.
    internal void AttachQuickSettings(SurfaceLink link)
    {
        if (_qs == null) { Host.Log.Line("qs: the host is up but the bar made no panel, so nothing is attached"); return; }
        _qs.Attach(link);
    }
}
