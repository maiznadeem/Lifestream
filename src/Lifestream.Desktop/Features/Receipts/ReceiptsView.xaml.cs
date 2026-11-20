using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Receipts;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features.Receipts;

/// <summary>
/// The Receipts screen. Double-clicking a row, or pressing Enter on it, opens that receipt's printable view (FEAT-18); Esc closes the side panel. The
/// blood group chip opens its menu on a click. The "Issued by" column folds away while the table is narrower than 860 DIP (a compact window, or the panel
/// open; the panel names who issued the receipt).
/// </summary>
public partial class ReceiptsView : UserControl
{
    public ReceiptsView()
    {
        InitializeComponent();

        // The "..." menu lives outside the visual tree: give it the screen's model, so its commands are bound before it opens. It is in the side panel,
        // which builds its content when it first opens.
        PanelContent.Built += (_, _) => ((FrameworkElement)LogicalTreeHelper.FindLogicalNode(PanelContent.Child, "MoreButton")).ContextMenu
            .SetBinding(FrameworkElement.DataContextProperty, new System.Windows.Data.Binding(nameof(DataContext)) { Source = this });
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a click on a row counts: a double click on the header or the scroll bar must not open a receipt.
        if (e.OriginalSource is DependencyObject source && FindRow(source) is { Item: ReceiptRow row } && DataContext is ReceiptsViewModel vm)
        {
            vm.OpenReceiptCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ReceiptsViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && Grid.SelectedItem is ReceiptRow row)
        {
            vm.OpenReceiptCommand.Execute(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsPanelOpen)
        {
            vm.ClosePanelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBloodTypeChipClick(object sender, RoutedEventArgs e)
    {
        BloodTypeMenu.PlacementTarget = BloodTypeChip;
        BloodTypeMenu.Placement = PlacementMode.Bottom;
        BloodTypeMenu.IsOpen = true;
    }

    /// <summary>A group chosen in the chip's menu (each item's data context is one of the blood type options).</summary>
    private void OnBloodTypeChosen(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: FilterOption<BloodType> option } && DataContext is ReceiptsViewModel vm)
        {
            vm.ChooseBloodTypeCommand.Execute(option);
        }
    }

    private static DataGridRow? FindRow(DependencyObject node)
    {
        for (var current = node; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is DataGridRow row)
            {
                return row;
            }
        }

        return null;
    }
}
