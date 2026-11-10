using System;
using Mantle.App.Shell.Popups;
using Mantle.App.Surfaces.Bar.QuickSettings;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Xaml;

namespace Mantle.App;

/// The quick-settings pop-up (lane native-bar-qs): the speaker, the network and the battery buttons open it (bar.js
/// openQs: el.volume / el.network / el.battery, BAR-392's anchors), one key "qs" in the pop-up framework (Shell/Popup):
/// instant (made once, parked between opens) and no focus (the bar's own pop-ups never take the keyboard). The Wi-Fi
/// password field is the one exception (Q-W6): it takes the keyboard on its own ask, only while it is in use, through
/// PopupHost.TakeKeyboard, and gives it back. Its height is measured before each open, and re-asked (PopupHost.Resize)
/// when a view changes or a note arrives while it is open.
public sealed partial class BarWindow
{
    QsPanel? _qs;
    /// The panel, for the host's wiring (App.xaml.cs, block QuickSettingsWiring).
    internal QsPanel? QuickSettings => _qs;

    /// MANTLE_TEST_QS_KEYS=1 (with MANTLE_TEST_VISIBLE=1, a CI runner only) lets a --test panel take the keyboard for
    /// the password field, so a UI test can type into it; without it a test run refuses, as Electron's does.
    public const string QsKeysEnv = "MANTLE_TEST_QS_KEYS";

    /// Called once from the bar's start, after the pop-up framework exists (BarWindow.xaml.cs, after StartFrame).
    void StartQuickSettings()
    {
        if (Popups == null) { Host.Log.Line("qs: the pop-up framework is not up, so quick settings does not open"); return; }
        var popups = Popups;
        bool keys = !Host.Args.Test || (Host.Env(QsKeysEnv) == "1" && !Host.Quiet);
        _qs = new QsPanel(Host.Log.Line, Host.Args.Test, new QsPanel.Room(
            Resize: (h, w) => popups.Resize("qs", h, w),
            TakeKeys: () => keys ? popups.TakeKeyboard("qs")
                : (false, "a test instance never takes the keyboard from whoever is at the machine, so a password cannot be typed here"),
            GiveKeys: why => popups.GiveBackKeyboard(why)));
        if (Host.Args.Test && keys) Host.Log.Line($"qs: {QsKeysEnv}=1: this test panel may take the keyboard for the password field");
        foreach (var b in new[] { VolumeBtn, NetworkBtn, BatteryBtn }) {
            var anchor = b;
            anchor.Click += (_, _) => OpenQuickSettings(anchor);
            popups.Anchor(anchor);
        }
        // the framework's close event: the panel lets its watches go at once, however it closed
        popups.Closed += (key, why) => {
            if (key != "qs" || _qs is not { Open: true }) return;
            _qs.Closed();
            Host.Log.Line($"qs: the panel closed ({why}); its watch is let go");
        };
        popups.KeyboardLost += key => { if (key == "qs") _qs?.KeyboardLost("another window took focus"); };
    }

    /// Open (or, from its own button, close) the panel above <paramref name="anchor"/>.
    internal void OpenQuickSettings(FrameworkElement anchor)
    {
        if (_qs == null || Popups == null) return;
        _popAnchor = anchor;
        bool closing = Popups.OpenKey == "qs";
        double h = closing ? 0 : _qs.Prepare(Dark);
        var qs = _qs;
        Popups.Toggle(new PopupKind("qs", "Quick settings", closing ? QsPanel.WidthDip : qs.Width, closing ? 400 : h, Focusable: false, Instant: true, Away: true, () => qs.Root), anchor);
        if (Popups.OpenKey == "qs" && !qs.Open) {
            qs.Opened();
            Host.Log.Line($"qs: the panel opened from {Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(anchor)} ({qs.Width}x{h} DIP)");
        }
    }

    /// From the host thread's wiring, posted here: the panel's road to the qs.* and powermode.* commands.
    internal void AttachQuickSettings(SurfaceLink link)
    {
        if (_qs == null) { Host.Log.Line("qs: the host is up but the bar made no panel, so nothing is attached"); return; }
        _qs.Attach(link);
    }
}
