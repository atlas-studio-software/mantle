using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Mantle.App.Shell.Hosting;

/// <summary>
/// The first clicks on a WinUI window right after it appears. For some tens of ms after its first frame the
/// window's content island does not take the pointer yet: a press then reaches the input site's HWND
/// (Microsoft.UI.Content.DesktopChildSiteBridge) as WM_POINTERDOWN, WinUI lets it fall through to DefWindowProc,
/// which turns it into WM_LBUTTONDOWN / WM_LBUTTONUP, and no XAML element ever hears it (CI, the pop-up test's first
/// click on the bar: lost when made 16..39 ms after the first frame, taken by XAML itself 62 ms after). Such a
/// fallen-through left click (down and up on the same button) is caught on the input site and given to that button,
/// so a click made the moment the window shows still works. WinUI never makes those legacy messages for a press it
/// took, so nothing is clicked twice; and once XAML has heard a press itself, the watch is taken off.
/// UI thread only.
/// </summary>
internal sealed class EarlyClicks
{
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);

    const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    static readonly UIntPtr Id = new(0x4D45);   // 'ME'
    public const string SiteClass = "Microsoft.UI.Content.DesktopChildSiteBridge";

    readonly IntPtr _site;
    readonly UIElement _root;
    readonly string _name;
    readonly Action<string> _log;
    readonly Action<DependencyObject>? _pressed;
    readonly SubclassProc _proc;   // kept alive as long as the subclass is on
    readonly PointerEventHandler _heard;
    ButtonBase? _down;
    bool _on;
    int _given;

    EarlyClicks(IntPtr site, UIElement root, string name, Action<string> log, Action<DependencyObject>? pressed)
    {
        _site = site; _root = root; _name = name; _log = log; _pressed = pressed;
        _proc = Proc;
        _heard = (_, _) => Off();
        _root.AddHandler(UIElement.PointerPressedEvent, _heard, true);
        _on = SetWindowSubclass(_site, _proc, Id, IntPtr.Zero);
        if (!_on) { _root.RemoveHandler(UIElement.PointerPressedEvent, _heard); _log($"{_name}: input: the early-click watch could not be put on the input site (SetWindowSubclass failed); a click in the first moments after the window shows may be lost"); }
    }

    /// Watch <paramref name="hwnd"/>'s input site from now until XAML hears a press itself. Null (and said) when the
    /// window has no input site. <paramref name="pressed"/> hears a caught press, as a PointerPressed would.
    public static EarlyClicks? Watch(IntPtr hwnd, UIElement root, string name, Action<string> log, Action<DependencyObject>? pressed = null)
    {
        IntPtr site = FindWindowExW(hwnd, IntPtr.Zero, SiteClass, null);
        if (site == IntPtr.Zero) { log($"{name}: input: no input site ({SiteClass}) under the window yet, so a click in the first moments after it shows may be lost"); return null; }
        var w = new EarlyClicks(site, root, name, log, pressed);
        return w._on ? w : null;
    }

    /// Clicks given to their buttons so far.
    public int Given => _given;

    IntPtr Proc(IntPtr h, uint msg, IntPtr wp, IntPtr lp, UIntPtr id, IntPtr data)
    {
        if (_on && (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP)) {
            long v = lp.ToInt64();
            int x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF);   // the input site's client px = the island's px
            bool down = msg == WM_LBUTTONDOWN;
            // read and passed on outside the input site's window procedure, in the order the messages came
            if (!_root.DispatcherQueue.TryEnqueue(() => Fell(down, x, y))) _log($"{_name}: input: a click fell through and could not be passed on (the UI queue is gone)");
        }
        return DefSubclassProc(h, msg, wp, lp);
    }

    void Fell(bool down, int x, int y)
    {
        ButtonBase? b;
        try { b = ButtonAt(x, y); }
        catch (Exception e) { _log($"{_name}: input: an early click could not be placed ({e.GetType().Name}: {e.Message})"); return; }
        if (down) {
            _down = b;
            if (b != null) _pressed?.Invoke(b);
            return;
        }
        var d = _down; _down = null;
        if (d == null || !ReferenceEquals(d, b)) return;
        string who = Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(d) is { Length: > 0 } aid ? aid : d.GetType().Name;
        try { Give(d, who); }
        catch (Exception e) { _log($"{_name}: input: a click on {who} came before the window took the pointer itself and pressing it failed ({e.GetType().Name}: {e.Message})"); }
    }

    void Give(ButtonBase b, string who)
    {
        // a peer of our own: the element's own peer comes back typed as FrameworkElementAutomationPeer in the AOT
        // build (CI run 42382341244), so it cannot be told from a button by its type
        if (b is not Button button) { _log($"{_name}: input: a click on {who} came before the window took the pointer itself and was not passed on ({b.GetType().Name} is not a plain button)"); return; }
        _given++;
        _log($"{_name}: input: a click on {who} came before the window took the pointer itself (its first moments), so Mantle pressed {who} for it");
        new ButtonAutomationPeer(button).Invoke();
    }

    ButtonBase? ButtonAt(int x, int y)
    {
        double s = _root.XamlRoot is { } xr && xr.RasterizationScale > 0 ? xr.RasterizationScale : 1.0;
        foreach (var e in VisualTreeHelper.FindElementsInHostCoordinates(new Windows.Foundation.Point(x / s, y / s), _root))
            if (e is ButtonBase { IsEnabled: true } bb) return bb;
        return null;
    }

    /// XAML heard a press itself: the island takes the pointer, so the watch comes off.
    void Off()
    {
        if (!_on) return;
        _on = false;
        _root.RemoveHandler(UIElement.PointerPressedEvent, _heard);
        RemoveWindowSubclass(_site, _proc, Id);
        if (_given > 0) _log($"{_name}: input: the window takes the pointer itself now; {_given} early click(s) were passed to their buttons");
    }
}
