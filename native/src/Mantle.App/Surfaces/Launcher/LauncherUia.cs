using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Mantle.App.Surfaces.Launcher;

// WinUI 3 panels make no automation peer, so an AutomationId on a Grid or StackPanel never reaches UI Automation.
// The launcher's panel, results list, rows and key hints carry one, as Electron's DOM roles do: the panel a group,
// #results a list (role=listbox), each row a list item (role=option, its ItemStatus "selected" when picked).

internal sealed partial class LauncherGrid : Grid
{
    readonly AutomationControlType _type;
    public LauncherGrid(AutomationControlType type) { _type = type; }
    protected override AutomationPeer OnCreateAutomationPeer() => new LauncherPeer(this, _type);
}

internal sealed partial class LauncherStack : StackPanel
{
    readonly AutomationControlType _type;
    public LauncherStack(AutomationControlType type) { _type = type; }
    protected override AutomationPeer OnCreateAutomationPeer() => new LauncherPeer(this, _type);
}

internal sealed partial class LauncherPeer : FrameworkElementAutomationPeer
{
    readonly AutomationControlType _type;
    public LauncherPeer(FrameworkElement owner, AutomationControlType type) : base(owner) { _type = type; }
    protected override AutomationControlType GetAutomationControlTypeCore() => _type;
    protected override string GetClassNameCore() => "Launcher" + _type;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
}
