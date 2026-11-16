using System;
using System.Collections.Generic;
using System.Text.Json;
using Mantle.Core.Features.Badges;
using Mantle.Core.Lifecycle;
using Microsoft.UI.Xaml;

namespace Mantle.App;

/// The Helm badge's disc on a strip cell (lane native-r7-media4; ui/bar/helmbadge.js paint/disc/match). A cell whose
/// app exe matches a badge source shows the count top-right ("9+" above 9, a wide pill for two characters, hidden at 0),
/// accent while only waiting, warn when any stopped on an error; the tooltip gets "N need(s) you: ..." (replaced, never
/// stacked; the keep-on-top line stays below it) and the cell's status "; badge N"; an unreadable badge file says
/// "Count hidden: its badge file could not be read" (not running / turned off add no line).
public sealed partial class CellVm
{
    int _badgeN;
    bool _badgeWarn;
    string _badgeLine = "";
    internal string BadgeLine => _badgeLine;
    internal string BadgeStatus => _badgeN > 0 ? "; badge " + _badgeN + (_badgeWarn ? " warn" : "") : "";
    public string BadgeText => HelmBadge.DiscText(_badgeN);
    public Thickness BadgePad => BadgeText.Length > 1 ? new Thickness(4, 0, 4, 0) : new Thickness(0);
    public Visibility BadgeAccVisibility => _badgeN > 0 && !_badgeWarn ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeWarnVisibility => _badgeN > 0 && _badgeWarn ? Visibility.Visible : Visibility.Collapsed;

    /// Returns true when anything drawn changed.
    internal bool SetBadge(int n, bool warn, string line)
    {
        if (n == _badgeN && warn == _badgeWarn && line == _badgeLine) return false;
        _badgeN = n; _badgeWarn = warn; _badgeLine = line;
        Changed(nameof(BadgeText)); Changed(nameof(BadgePad)); Changed(nameof(BadgeAccVisibility)); Changed(nameof(BadgeWarnVisibility)); Changed(nameof(Tip));
        BadgeMoved();   // lane native-r7-strip-3: the keep-on-top pin goes top-left, "+N" sums the hidden counts (Tasks/)
        SetStatus(true);
        return true;
    }
}

public sealed partial class BarWindow
{
    readonly List<(BadgeSource Src, int Count, int Errors, string Reason, string Tip)> _badges = new();

    /// From the host thread's wiring (App.xaml.cs, block "media wiring"), posted here.
    internal void AttachHelmBadge(SurfaceLink link)
    {
        link.On<JsonElement>(HelmBadgeFeature.StateEvent, s => {
            _badges.Clear();
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("badges", out var bs) && bs.ValueKind == JsonValueKind.Array)
                foreach (var b in bs.EnumerateArray()) {
                    string S(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    int I(string k) => b.TryGetProperty(k, out var v) && v.TryGetInt32(out var n) ? n : 0;
                    _badges.Add((new BadgeSource(S("exe"), "", S("label")), I("count"), I("errors"), S("reason"), S("tip")));
                }
            PaintHelmBadges();
        });
    }

    /// paint(): every cell against every source (the first matching source with a count wins; else the first match's
    /// hidden-count line). Called after each strip sync and each push.
    void PaintHelmBadges()
    {
        foreach (var c in _cells) {
            int n = 0; bool warn = false; string line = "";
            foreach (var b in _badges) {
                if (!HelmBadge.Matches(b.Src, c.Item.Exe)) continue;
                if (b.Count > 0) { n = b.Count; warn = b.Errors > 0; line = "\n" + (b.Tip.Length > 0 ? b.Tip : $"{b.Count} {(b.Count == 1 ? "needs" : "need")} you"); break; }
                if (line.Length == 0 && b.Reason == HelmBadge.Unreadable) line = "\nCount hidden: " + b.Reason;
            }
            if (c.SetBadge(n, warn, line)) Host.Log.Line($"helmbadge: {c.AutomationId} shows {(n > 0 ? HelmBadge.DiscText(n) + (warn ? " (warn)" : "") : line.Length > 0 ? "no count (hidden)" : "no count")}");
        }
    }
}
