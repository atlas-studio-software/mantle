using System;
using System.Numerics;
using Mantle.Core.Features.TopBar;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace Mantle.App.Surfaces.TopBar;

// The dropdowns' motion and the strip's pointer and focus details:
//  - TOP-080: a dropdown pops in once per menu session (light: up 3 px on the bar's spring over --m-dur-xs 240 ms and a
//    96 ms fade on --m-fade-in; reduced motion: a 120 ms linear fade only; dark: none, as Helm's menus). A re-render never
//    pops in again (MenuLevel.Popped). Composition animations only: nothing runs once they end.
//  - press-drag-release: a press on a title or a stat cell lets the pointer go, so a drag over the rows highlights them
//    and a release on a row picks it (Electron's mouseup on .r after a mousedown on the title).
//  - TOP-028: a strip button focused from the keyboard shows the focus ring (light: 2 px accent inside its edge; dark:
//    --lg-focus's 1.5 px accent ring); a pointer focus shows none.
internal sealed partial class TopBarSurface
{
    const int PopMs = 240, PopFadeMs = 96, PopReducedMs = 120;
    bool _dragArmed;
    readonly bool _reducedMotion = ReducedMotionNow();

    static bool ReducedMotionNow()
    {
        try { return !new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { return false; }
    }

    /// TOP-080: called by Reveal() when a dropdown is first seen in this menu session.
    void PopIn(FrameworkElement box, MenuLevel lv)
    {
        if (lv.Popped) return;
        lv.Popped = true;
        if (_dark) { if (_test) _ctx.Log.Line("dropdown pop-in: none (dark)"); return; }
        var v = ElementCompositionPreview.GetElementVisual(box);
        var c = v.Compositor;
        var op = c.CreateScalarKeyFrameAnimation();
        if (_reducedMotion)
        {
            op.Duration = TimeSpan.FromMilliseconds(PopReducedMs);
            op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f, c.CreateLinearEasingFunction());
            v.StartAnimation("Opacity", op);
            if (_test) _ctx.Log.Line($"dropdown pop-in: fade {PopReducedMs} ms (reduced motion)");
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(box, true);
        var spring = BarWindow.SpringCurve;
        var lin = c.CreateLinearEasingFunction();
        var tr = c.CreateVector3KeyFrameAnimation();
        tr.Duration = TimeSpan.FromMilliseconds(PopMs);
        for (int i = 0; i < spring.Length; i++) tr.InsertKeyFrame(i / (float)(spring.Length - 1), new Vector3(0, -3 * (1 - spring[i]), 0), lin);
        op.Duration = TimeSpan.FromMilliseconds(PopFadeMs);
        op.InsertKeyFrame(0f, 0f); op.InsertKeyFrame(1f, 1f, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f)));
        v.StartAnimation("Translation", tr);
        v.StartAnimation("Opacity", op);
        if (_test) _ctx.Log.Line($"dropdown pop-in: up 3 px over {PopMs} ms, fade {PopFadeMs} ms");
    }

    /// A press that opened a menu lets the pointer go, so the rows hear the drag and the release. Letting go also cancels
    /// the button's own press (its capture is lost), so it would never take the focus a click gives it; it takes it
    /// here instead (as Electron's mousedown focuses the button), or the menu keys after a real click reach nothing.
    void ArmDrag(Control opener)
    {
        opener.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((s, e) =>
        {
            if (_open == null || e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch) return;
            _dragArmed = true;
            var c = (Control)s;
            c.ReleasePointerCaptures();
            bool had = c.FocusState != FocusState.Unfocused;
            bool took = had || c.Focus(FocusState.Pointer);
            if (_test) _ctx.Log.Line($"drag armed: {Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(c)} (focus {(had ? "kept" : took ? "taken" : "refused")})");
        }), true);
    }

    /// The TextBox template's clear (x) button: Electron's name inputs are plain inputs with none, and a click at the
    /// box's right end would empty it instead of placing the caret.
    static void NoClearButton(TextBox box) => box.Loaded += (_, _) =>
    {
        if (Named(box, "DeleteButton", 0) is FrameworkElement b) { b.Width = 0; b.MinWidth = 0; b.Margin = new Thickness(0); b.Opacity = 0; b.IsHitTestVisible = false; b.IsTabStop = false; }
    };

    static DependencyObject? Named(DependencyObject d, string name, int depth)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i);
            if (c is FrameworkElement fe && fe.Name == name) return c;
            if (depth < 16 && Named(c, name, depth + 1) is { } f) return f;
        }
        return null;
    }

    /// A row that a press-drag-release picks: the release after a press on a title (the row's own click never fires then).
    void DragPick(UIElement row, Action pick)
    {
        row.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            if (!_dragArmed) return;
            _dragArmed = false;
            _ctx.Log.Line("drag pick: released on a row");
            pick();
        }), true);
    }

    /// Any release ends the drag (bubbles after the row's handler).
    void InstallDragEnd() => _root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => _dragArmed = false), true);

    /// TOP-028: the keyboard focus ring on a strip button.
    void FocusRing(Button b)
    {
        b.GotFocus += (_, _) =>
        {
            if (b.FocusState != FocusState.Keyboard) return;
            b.BorderBrush = Br("LgAcc");
            b.BorderThickness = new Thickness(_dark ? 1.5 : 2);
            if (_test) _ctx.Log.Line($"focus ring: {Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(b)}");
        };
        b.LostFocus += (_, _) => b.BorderThickness = new Thickness(0);
    }
}
