using System.Windows.Controls;
using System.Windows.Input;
using Lifestream.Client.Features.Receipts;

namespace Lifestream.Desktop.Features.Receipts;

/// <summary>The Receipts screen. Double-clicking a row, or pressing Enter on it, opens that receipt's printable view (FEAT-18).</summary>
public partial class ReceiptsView : UserControl
{
    public ReceiptsView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a click on a row counts: a double click on the header or the scroll bar must not open a receipt.
        if (e.OriginalSource is System.Windows.DependencyObject source && FindRow(source) is { Item: ReceiptRow row } && DataContext is ReceiptsViewModel vm)
        {
            vm.OpenReceiptCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Grid.SelectedItem is ReceiptRow row && DataContext is ReceiptsViewModel vm)
        {
            vm.OpenReceiptCommand.Execute(row);
            e.Handled = true;
        }
    }

    private static DataGridRow? FindRow(System.Windows.DependencyObject node)
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
