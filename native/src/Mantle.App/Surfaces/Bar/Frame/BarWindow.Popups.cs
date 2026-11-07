using System;
using Mantle.App.Shell.Popups;
using Mantle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;

namespace Mantle.App;

/// The bar's pop-ups through the framework in Shell/Popup. The real panels (Quick Settings, calendar, More, ...)
/// are later rounds' work; until then a TEST run with MANTLE_TEST_POPUPS=1 gets two stand-in kinds so the
/// framework can be driven from outside: More opens "more" (instant: parked between opens, no focus) and the
/// clock opens "cal" (made on demand, takes the keyboard). A run without that variable opens nothing new.
public sealed partial class BarWindow
{
    internal PopupHost? Popups { get; private set; }
    internal bool Dark => _dark;
    FrameworkElement? _popAnchor;

    void StartPopups()
    {
        bool live = Host.Args.Kind == RunKind.Live;
        // lib/popwatch.js: a --test run watches nothing real unless asked (--test-popwatch, --test-popwatch-away)
        bool asked = Host.Args.Test && Host.Args.Has("test-popwatch"), askedAway = Host.Args.Test && Host.Args.Has("test-popwatch-away");
        bool watch = live || asked || askedAway, away = live || askedAway;
        Popups = new PopupHost(this, watch, "a test run watches nothing unless --test-popwatch (or --test-popwatch-away)", away);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => Popups.BarPressed(e.OriginalSource as DependencyObject, _popAnchor)), true);
        if (live || Host.Env("MANTLE_TEST_POPUPS") != "1") return;
        var more = new PopupKind("more", "More", 260, 180, Focusable: false, Instant: true, Away: true, () => Stand("more", "More"));
        var cal = new PopupKind("cal", "Calendar", 268, 300, Focusable: true, Instant: false, Away: true, () => Stand("cal", "Calendar"));
        MoreBtn.Click += (_, _) => { _popAnchor = MoreBtn; Popups.Toggle(more, MoreBtn); };
        ClockBtn.Click += (_, _) => { _popAnchor = ClockBtn; Popups.Toggle(cal, ClockBtn); };
        Popups.Anchor(MoreBtn); Popups.Anchor(ClockBtn);
        Host.Log.Line("pop-ups: MANTLE_TEST_POPUPS=1: More opens the stand-in \"more\" (instant, parked), the clock \"cal\" (on demand, focusable)");
    }

    /// A stand-in body: the kind's name, findable as popup.&lt;key&gt; (AUTOMATION.md).
    static UIElement Stand(string key, string name)
    {
        var t = (TextBlock)XamlReader.Load("<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Margin='14' FontSize='13' Foreground='{ThemeResource LgInk2}'/>");
        t.Text = name + " (test stand-in)";
        AutomationProperties.SetAutomationId(t, "popup." + key);
        AutomationProperties.SetName(t, name);
        return t;
    }
}
