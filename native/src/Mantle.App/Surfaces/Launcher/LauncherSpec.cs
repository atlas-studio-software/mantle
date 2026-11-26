using System;
using System.Collections.Generic;
using Mantle.Core.Logging;
using Microsoft.UI.Xaml;

namespace Mantle.App.Surfaces.Launcher;

/// <summary>
/// The islands launcher's numbers (lane r15-quick-launcher), read ONCE at Create from Theme/LvLauncher.xaml (merged by
/// Theme.xaml; the prototype apps-r3/mantle-bar panels/launcher.css, pinned by Mantle.Core.Tests LauncherSpecTests). A key
/// the dictionary does not have falls back to the prototype's number and is said once in the log. Theme-free: the inks are
/// Levitate.xaml's LvBar* (IslandsInk), read per theme where they are used.
/// </summary>
internal sealed class LauncherSpec
{
    public double Width, Height, ScreenSideMargin, ScreenCapMargin, Radius;
    public Thickness ShadowRoom;
    public double FieldHeight, FieldGap, FieldIcon, FieldFont, FieldLine, FieldLineFadeMs;
    public Thickness FieldPadding, FieldLineInset, BodyPadding;
    public Thickness GroupPadding, GroupPaddingFirst, NonePadding;
    public double GroupFont, NoneFont;
    public double RowHeight, RowHeightBare, RowGap, RowRadius, Icon, NameFont, SubFont, SubShare;
    public Thickness RowPadding, RowPaddingBare;
    public CornerRadius IconCorner;
    public double TickWidth, TickInset, TickInsetBare, TickRadius;
    public double FootHeight, FootLine, HintFont, HintGap, KeyGap, KbdFont;
    public Thickness FootPadding, KbdPadding;
    public CornerRadius KbdCorner;
    public double InFadeMs, InMoveMs, OutFadeMs, OutMoveMs, Rise, ScaleFrom;

    readonly List<string> _missing = [];

    public static LauncherSpec Read(ILogSink log)
    {
        var s = new LauncherSpec();
        s.Width = s.D("LvLauncherWidth", 600); s.Height = s.D("LvLauncherHeight", 520);
        s.ScreenSideMargin = s.D("LvLauncherScreenSideMargin", 24); s.ScreenCapMargin = s.D("LvLauncherScreenCapMargin", 80);
        s.Radius = s.D("LvLauncherRadius", 14);
        s.ShadowRoom = s.T("LvLauncherShadowRoom", new Thickness(44, 12, 44, 88));
        s.FieldHeight = s.D("LvLauncherFieldHeight", 56); s.FieldPadding = s.T("LvLauncherFieldPadding", new Thickness(16, 0, 16, 0));
        s.FieldGap = s.D("LvLauncherFieldGap", 12); s.FieldIcon = s.D("LvLauncherFieldIcon", 18); s.FieldFont = s.D("LvLauncherFieldFont", 16);
        s.FieldLine = s.D("LvLauncherFieldLine", 1); s.FieldLineInset = s.T("LvLauncherFieldLineInset", new Thickness(16, 0, 16, 0));
        s.FieldLineFadeMs = s.D("LvLauncherFieldLineFadeMs", 160);
        s.BodyPadding = s.T("LvLauncherBodyPadding", new Thickness(8, 4, 8, 8));
        s.GroupPadding = s.T("LvLauncherGroupPadding", new Thickness(8, 12, 8, 4)); s.GroupPaddingFirst = s.T("LvLauncherGroupPaddingFirst", new Thickness(8, 4, 8, 4));
        s.GroupFont = s.D("LvLauncherGroupFont", 12);
        s.NonePadding = s.T("LvLauncherNonePadding", new Thickness(8, 8, 8, 0)); s.NoneFont = s.D("LvLauncherNoneFont", 13);
        s.RowHeight = s.D("LvLauncherRowHeight", 36); s.RowHeightBare = s.D("LvLauncherRowHeightBare", 32);
        s.RowPadding = s.T("LvLauncherRowPadding", new Thickness(8, 0, 12, 0)); s.RowPaddingBare = s.T("LvLauncherRowPaddingBare", new Thickness(12, 0, 12, 0));
        s.RowGap = s.D("LvLauncherRowGap", 12); s.RowRadius = s.D("LvLauncherRowRadius", 8);
        s.Icon = s.D("LvLauncherIcon", 24); s.IconCorner = s.R("LvLauncherIconCorner", new CornerRadius(6));
        s.NameFont = s.D("LvLauncherNameFont", 14); s.SubFont = s.D("LvLauncherSubFont", 12); s.SubShare = s.D("LvLauncherSubShare", 0.44);
        s.TickWidth = s.D("LvLauncherTickWidth", 3); s.TickInset = s.D("LvLauncherTickInset", 11); s.TickInsetBare = s.D("LvLauncherTickInsetBare", 9);
        s.TickRadius = s.D("LvLauncherTickRadius", 999);
        s.FootHeight = s.D("LvLauncherFootHeight", 40); s.FootPadding = s.T("LvLauncherFootPadding", new Thickness(16, 0, 16, 0));
        s.FootLine = s.D("LvLauncherFootLine", 1); s.HintFont = s.D("LvLauncherHintFont", 12); s.HintGap = s.D("LvLauncherHintGap", 16);
        s.KeyGap = s.D("LvLauncherKeyGap", 8); s.KbdFont = s.D("LvLauncherKbdFont", 11);
        s.KbdPadding = s.T("LvLauncherKbdPadding", new Thickness(6, 1, 6, 1)); s.KbdCorner = s.R("LvLauncherKbdCorner", new CornerRadius(4));
        s.InFadeMs = s.D("LvLauncherInFadeMs", 160); s.InMoveMs = s.D("LvLauncherInMoveMs", 260);
        s.OutFadeMs = s.D("LvLauncherOutFadeMs", 120); s.OutMoveMs = s.D("LvLauncherOutMoveMs", 160);
        s.Rise = s.D("LvLauncherRise", 6); s.ScaleFrom = s.D("LvLauncherScaleFrom", 0.97);
        if (s._missing.Count > 0)
            log.Line("Theme/LvLauncher.xaml has no " + string.Join(", ", s._missing) + ", so the launcher uses the prototype's number for each");
        return s;
    }

    /// The pane's size on a monitor this big (DIP): launcher.css min(600px, 100vw - 24px) x min(520px, 100vh - 80px).
    public (double W, double H) PaneFor(double monitorW, double monitorH) =>
        (Math.Max(0, Math.Min(Width, monitorW - ScreenSideMargin)), Math.Max(0, Math.Min(Height, monitorH - ScreenCapMargin)));

    double D(string key, double dflt) { if (Find(key) is double v) return v; _missing.Add(key); return dflt; }
    Thickness T(string key, Thickness dflt) { if (Find(key) is Thickness v) return v; _missing.Add(key); return dflt; }
    CornerRadius R(string key, CornerRadius dflt) { if (Find(key) is CornerRadius v) return v; _missing.Add(key); return dflt; }

    /// A theme-free key of the app's merged dictionaries (App.xaml, Theme.xaml, LvLauncher.xaml), or null.
    static object? Find(string key)
    {
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d is not Microsoft.UI.Xaml.Controls.XamlControlsResources && Find(d, key, 0) is { } v) return v;
        return null;
    }

    static object? Find(ResourceDictionary d, string key, int depth)
    {
        if (d.TryGetValue(key, out var v)) return v;
        if (depth >= 4) return null;   // App.xaml, Theme.xaml, LvLauncher.xaml is two levels; the cap only stops a cycle
        foreach (var m in d.MergedDictionaries) if (Find(m, key, depth + 1) is { } x) return x;
        return null;
    }
}
