using System;
using Mantle.App.Shell.Popups;
using Mantle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Mantle.App.Surfaces.Bar.Frame;

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
        // the stand-in runs only: every press and release on the bar is logged, so a click that opened nothing says
        // whether it reached the bar at all and on what
        static string Who(object? o) => o is FrameworkElement f ? $"{f.GetType().Name} '{f.Name}' '{AutomationProperties.GetAutomationId(f)}'" : o?.GetType().Name ?? "nothing";
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => Host.Log.Line($"bar: test: pressed on {Who(e.OriginalSource)} at {e.GetCurrentPoint(Root).Position}")), true);
        Root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => Host.Log.Line($"bar: test: released on {Who(e.OriginalSource)} at {e.GetCurrentPoint(Root).Position}")), true);
        MoreBtn.PointerCaptureLost += (_, _) => Host.Log.Line("bar: test: More lost the pointer capture");
        MoreBtn.Click += (_, _) => { Host.Log.Line("bar: test: More clicked"); _popAnchor = MoreBtn; Popups.Toggle(more, MoreBtn); };
        ClockBtn.Click += (_, _) => { _popAnchor = ClockBtn; Popups.Toggle(cal, ClockBtn); };
        Popups.Anchor(MoreBtn); Popups.Anchor(ClockBtn);
        Host.Log.Line("pop-ups: MANTLE_TEST_POPUPS=1: More opens the stand-in \"more\" (instant, parked), the clock \"cal\" (on demand, focusable)");
        WatchInputForTest();
    }

    // DIAGNOSTIC (stand-in runs only): where the mouse messages of the first clicks go, at the Win32 level, and the
    // input site's place over the first frames.
    FrameNative.SubclassProc? _inSub;
    int _inFrames;
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    void WatchInputForTest()
    {
        IntPtr Site() => FindWindowExW(_hwnd, IntPtr.Zero, "Microsoft.UI.Content.DesktopChildSiteBridge", null);
        string Where(IntPtr h) { if (h == IntPtr.Zero) return "none"; FrameNative.GetWindowRect(h, out var r); return $"0x{h.ToInt64():X} {FrameNative.Rect(r)}{(Mantle.App.Native.Win32.IsWindowVisible(h) ? "" : " hidden")}"; }
        int seen = 0;
        _inSub = (h, msg, wp, lp, id, data) => {
            if (seen < 40 && msg is 0x21 or 0x84 or 0xA1 or 0x201 or 0x202 or 0x241 or 0x242 or 0x245 or 0x246 or 0x247 or 0x24B or 0x200) {
                if (msg is not (0x84 or 0x200 or 0x245) || seen < 12) { seen++; long v = lp.ToInt64(); Host.Log.Line($"bar: test: msg 0x{msg:X} to {(h == _hwnd ? "the window" : "the input site")} lp ({(short)(v & 0xFFFF)},{(short)((v >> 16) & 0xFFFF)})"); }
            }
            return FrameNative.DefSubclassProc(h, msg, wp, lp);
        };
        IntPtr site = Site();
        bool a = FrameNative.SetWindowSubclass(_hwnd, _inSub!, new UIntPtr(0x4D50), IntPtr.Zero);
        bool b = site != IntPtr.Zero && FrameNative.SetWindowSubclass(site, _inSub!, new UIntPtr(0x4D50), IntPtr.Zero);
        Host.Log.Line($"bar: test: window {Where(_hwnd)}; input site {Where(site)}; watched {a}/{b}");
        void OnFrame(object? s, object e)
        {
            if (++_inFrames > 4) { CompositionTarget.Rendering -= OnFrame; return; }
            IntPtr now = Site();
            if (!b && now != IntPtr.Zero) { b = FrameNative.SetWindowSubclass(now, _inSub!, new UIntPtr(0x4D50), IntPtr.Zero); Host.Log.Line($"bar: test: input site watched from frame {_inFrames}: {b}"); }
            Host.Log.Line($"bar: test: frame {_inFrames}: input site {Where(now)}");
        }
        CompositionTarget.Rendering += OnFrame;
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
