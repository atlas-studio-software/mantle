using System;
using System.Linq;
using System.Security;
using Mantle.App.Shell.Popups;
using Mantle.Core.Features.WindowList;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Mantle.App;

/// <summary>
/// The window list (ui/bar/bar.js#flyList; BAR-199, BAR-234): a click on an app with two or more windows touches no
/// window (bar.open answers action "list") and opens this list above its icon. One row per window: its title
/// ("(untitled window)" if none; tooltip with "(minimized)"), the active one in accent with the 3x14 accent bar, a
/// minimised one in quiet ink; a row's click closes the list and focuses that window, or minimises it if it is the
/// active one (bar.window toggle); its X ("Close this window") closes the list and asks the window to close.
/// Clicking the same icon again closes the list (the pop-up's own toggle). Electron opens live previews (DWM cards)
/// first and keeps this list for too many windows; the native bar has no previews yet, so the list says so in its
/// note (docs/lanes/bar-tasks.md, part 2). UI Automation: `bar.winlist`, rows `bar.winlist.<n>`, X `bar.winlist.<n>.close`.
/// </summary>
public sealed partial class BarWindow
{
    const string WlNs = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    // bar.css .pop .item / .pop .wrow / .pop .x, on the bar's tokens
    const string RowTemplate =
        "<ControlTemplate " + WlNs + " TargetType='Button'>" +
        "<Grid x:Name='Face' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'>" +
        "<VisualStateManager.VisualStateGroups><VisualStateGroup x:Name='CommonStates'>" +
        "<VisualState x:Name='Normal'/>" +
        "<VisualState x:Name='PointerOver'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource HOVER}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Pressed'><VisualState.Setters><Setter Target='Face.Background' Value='{ThemeResource PRESS}'/></VisualState.Setters></VisualState>" +
        "<VisualState x:Name='Disabled'/>" +
        "</VisualStateGroup></VisualStateManager.VisualStateGroups>" +
        "<ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' Foreground='{TemplateBinding Foreground}'/>" +
        "</Grid></ControlTemplate>";

    void OpenWindowList(CellVm vm, FrameworkElement anchor)
    {
        if (Popups == null) return;
        var it = vm.Item;
        if (it.Windows.Count == 0) return;   // nothing to list; the click itself was the action (bar.js openAppPop)
        // 11 px padding, the heading (8 px under it), the note, then 32 px per row; held to 420 DIP (it scrolls past that)
        double h = 11 + 25 + 40 + it.Windows.Count * 32 + 11;
        var kind = new PopupKind("winlist:" + it.Id, it.Name + " windows", 300, Math.Min(h, 420), Focusable: false, Instant: false, Away: true,
            () => WindowListBody(it));
        _popAnchor = anchor;
        Popups.Toggle(kind, anchor);
    }

    UIElement WindowListBody(TaskItem it)
    {
        var panel = (StackPanel)XamlReader.Load(
            "<StackPanel " + WlNs + " Padding='12,11,12,11' Spacing='0'>" +
            "<TextBlock Margin='0,0,0,8' FontSize='12.5' FontWeight='SemiBold' CharacterSpacing='40' Foreground='{ThemeResource LgInk3}' FontFamily='{ThemeResource LgFont}' TextTrimming='CharacterEllipsis'/>" +
            "<TextBlock Margin='0,-2,0,6' FontSize='13' LineHeight='19' TextWrapping='WrapWholeWords' Foreground='{ThemeResource LgInk3}' FontFamily='{ThemeResource LgFont}'/>" +
            "</StackPanel>");
        AutomationProperties.SetAutomationId(panel, "bar.winlist");
        AutomationProperties.SetName(panel, it.Name + " windows");
        ((TextBlock)panel.Children[0]).Text = (it.Name.Length > 0 ? it.Name : it.Exe ?? "windows").ToUpperInvariant();
        ((TextBlock)panel.Children[1]).Text = $"{it.Windows.Count} windows: live previews are not in the native bar yet, so this is the list";
        int n = 0;
        foreach (var w in it.Windows) panel.Children.Add(WindowRow(w, n++));
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        return scroll;
    }

    UIElement WindowRow(TaskWindow w, int n)
    {
        string title = w.Title.Length > 0 ? w.Title : "(untitled window)";
        bool on = RunningWindows.IsActive(w);
        string ink = on ? "LgAcc" : w.Minimized ? "LgInk3" : "LgInk2";
        string tpl = RowTemplate.Replace("HOVER", "LgHover").Replace("PRESS", "LgPress");
        string xTpl = RowTemplate.Replace("HOVER", "LgWarnSoft").Replace("PRESS", "LgWarnSoft");
        var row = (Grid)XamlReader.Load(
            "<Grid " + WlNs + " ColumnSpacing='6'>" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='26'/></Grid.ColumnDefinitions>" +
            "<Button HorizontalAlignment='Stretch' HorizontalContentAlignment='Left' Padding='15,7,8,7' IsTabStop='False' UseSystemFocusVisuals='False'" +
            " Foreground='{ThemeResource " + ink + "}'><Button.Template>" + tpl + "</Button.Template>" +
            "<TextBlock FontSize='13' FontFamily='{ThemeResource LgFont}' TextTrimming='CharacterEllipsis' Text='" + SecurityElement.Escape(title) + "'/></Button>" +
            "<Rectangle Width='3' Height='14' RadiusX='1.5' RadiusY='1.5' Margin='5,0,0,0' HorizontalAlignment='Left' IsHitTestVisible='False'" +
            " Fill='{ThemeResource LgAcc}' Visibility='" + (on ? "Visible" : "Collapsed") + "'/>" +
            "<Button Grid.Column='1' Width='26' Height='26' Padding='0' HorizontalContentAlignment='Center' IsTabStop='False' UseSystemFocusVisuals='False'" +
            " Foreground='{ThemeResource LgInk3}'><Button.Template>" + xTpl + "</Button.Template>" +
            "<TextBlock FontSize='14' Text='&#x00D7;'/></Button>" +
            "</Grid>");
        var open = (Button)row.Children[0];
        var x = (Button)row.Children[2];
        AutomationProperties.SetAutomationId(open, "bar.winlist." + n);
        AutomationProperties.SetName(open, title);
        AutomationProperties.SetItemStatus(open, (on ? "active" : "") + (w.Minimized ? "minimized" : ""));
        ToolTipService.SetToolTip(open, title + (w.Minimized ? "\n(minimized)" : ""));
        AutomationProperties.SetAutomationId(x, "bar.winlist." + n + ".close");
        AutomationProperties.SetName(x, "Close this window");
        ToolTipService.SetToolTip(x, "Close this window");
        open.Click += (_, _) => WindowAct(w.Hwnd, on ? "toggle" : "focus", "the list's row was clicked");
        x.Click += (_, _) => WindowAct(w.Hwnd, "close", "a window was closed from it");
        return row;
    }

    void WindowAct(string hwnd, string action, string why)
    {
        Popups?.Close(why);
        if (_tasks == null) return;
        _tasks.Window(hwnd, action, r => {
            Host.Log.Line($"bar tasks: window {action}: {(r.Ok ? "ok" : "failed")}{(r.Reason != null ? ": " + r.Reason : "")}");
            bool changed = r.Ok ? _chips.Clear("open") : _chips.Fail("open", "Window did not switch", r.Reason ?? "Mantle did not say why", "warn");   // bar.js ACT_LABEL.window, one act chip
            if (changed) RenderPip(force: true);
        });
    }
}
