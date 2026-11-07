using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Mantle.App;

// WinUI 3 panels (Grid, Canvas, StackPanel, Border) make no automation peer, so an AutomationId set
// on one never reaches the UI Automation tree. These carry a Group peer, so bar.window, bar.pill and
// bar.apps are findable (AUTOMATION.md). Their children are found through the visual tree as usual.

public sealed partial class UiaGrid : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

public sealed partial class UiaItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this);
}

internal sealed partial class GroupPeer : FrameworkElementAutomationPeer
{
    public GroupPeer(FrameworkElement owner) : base(owner) { }
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
}
