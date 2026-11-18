using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Shell;

/// <summary>The main window's content: sidebar, header with the account control, the page host and the toasts.</summary>
public partial class ShellView : UserControl
{
    public ShellView() => InitializeComponent();

    /// <summary>The account control's menu opens on click and on Enter or Space (the button is focusable), so it is reachable by keyboard.</summary>
    private void OnAccountClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }
}
