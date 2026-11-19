using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Lifestream.Desktop.Features.ListScreens;

/// <summary>
/// A button that opens its context menu under itself on a click, Enter or Space: the "..." menu of a detail panel, which holds the
/// less frequent and destructive actions (Delete). The menu items inherit the button's data context.
/// </summary>
public sealed class MenuButton : Button
{
    /// <inheritdoc />
    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is { } menu)
        {
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
