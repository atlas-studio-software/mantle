using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using Mantle.Core.Look.Levitate;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mantle.App;

/// <summary>
/// Lane r13-bar-islands: the network button says when the machine is offline (Electron ui/bar/net.js 'wifi-off' /
/// 'disconnected'; the prototype's ?net=off): a globe with a slash at .55, named "Not connected, &lt;detail&gt;" with the
/// tooltip "Not connected\n&lt;detail&gt;\nClick: quick settings" (Mantle.Core <see cref="BarIslands.Offline"/>). Offline =
/// the system knows no network (<see cref="NetworkInterface.GetIsNetworkAvailable"/>, followed through
/// <see cref="NetworkChange.NetworkAvailabilityChanged"/>, posted to the UI thread) and the quick-settings Wi-Fi part does
/// not say it is connected; the detail comes from that part. While connected the button keeps today's glyph and name.
/// Known limit: a virtual adapter (Hyper-V, a VPN) can make GetIsNetworkAvailable true while the machine is offline.
/// Test runs: MANTLE_TEST_NET=off draws the offline state whatever the network is.
/// </summary>
public sealed partial class BarWindow
{
    const string NetTestEnv = "MANTLE_TEST_NET";
    bool _netAny = true, _netForcedOff;
    NetOffline _netNow;
    bool _netPainted;
    List<UIElement>? _netOnline;
    UIElement[]? _netOffline;
    NetworkAvailabilityChangedEventHandler? _netHandler;

    /// From the bar's start, after quick settings (its Wi-Fi part gives the words).
    void StartNetwork()
    {
        _netOnline = new List<UIElement>();
        foreach (var c in NetworkHost.Children) _netOnline.Add(c);
        // net.js globe() + SLASH, on the 16 DIP grid
        _netOffline = new[] {
            PathEl(Circle(8, 8, 5.6), false, 1.2),
            PathEl("M2.6 8h10.8M8 2.4c-1.8 1.9-1.8 9.3 0 11.2M8 2.4c1.8 1.9 1.8 9.3 0 11.2", false, 1.1),
            PathEl("M2.2 2.2l11.6 11.6", false, 1.3),
        };
        _netForcedOff = Host.Args.Test && string.Equals(Host.Env(NetTestEnv), "off", StringComparison.OrdinalIgnoreCase);
        if (_netForcedOff) Host.Log.Line($"network: {NetTestEnv}=off: this test run draws the offline network button");
        _netAny = NetworkUp();
        _netHandler = (_, e) => DispatcherQueue.TryEnqueue(() => {
            _netAny = e.IsAvailable;
            _qs?.Refresh();   // the Wi-Fi part, read again for the words
            PaintNetwork(e.IsAvailable ? "the system has a network again" : "the system has no network");
        });
        NetworkChange.NetworkAvailabilityChanged += _netHandler;
        Closed += (_, _) => { if (_netHandler != null) NetworkChange.NetworkAvailabilityChanged -= _netHandler; };
        if (_qs != null) _qs.WifiChanged += () => PaintNetwork("the Wi-Fi part was read");
        PaintNetwork("start");
    }

    static bool NetworkUp()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch (NetworkInformationException e) { Host.Log.Line("network: Windows could not say whether there is a network (" + e.Message + "); the button is drawn as connected"); return true; }
    }

    void PaintNetwork(string why)
    {
        if (_netOnline == null || _netOffline == null) return;
        var (on, connected) = _qs?.Wifi ?? ((bool?)null, (bool?)null);
        var n = BarIslands.Offline(_netAny && !_netForcedOff, on, _netForcedOff ? false : connected);
        if (_netPainted && n == _netNow) return;
        bool glyphChanged = !_netPainted || n.Offline != _netNow.Offline;
        _netPainted = true;
        _netNow = n;
        if (glyphChanged) {
            NetworkHost.Children.Clear();
            foreach (var c in n.Offline ? (IEnumerable<UIElement>)_netOffline : _netOnline) NetworkHost.Children.Add(c);
            NetworkHost.Opacity = n.Offline ? BarIslands.OfflineOpacity : 1;
        }
        AutomationProperties.SetName(NetworkBtn, n.Offline ? n.Label : "Network");
        ToolTipService.SetToolTip(NetworkBtn, n.Offline ? n.Tip : null);
        Host.Log.Line(n.Offline ? $"network: offline ({why}): {n.Label}" : $"network: connected ({why})");
    }
}
