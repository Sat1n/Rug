using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Rug.UI.Controls;

/// <summary>Keep drag pointer handling while exposing the window picker to accessibility/UIA.</summary>
public sealed class WindowPickerControl : ContentControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PickerPeer(this);
    private sealed class PickerPeer(WindowPickerControl owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(WindowPickerControl);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }
}
