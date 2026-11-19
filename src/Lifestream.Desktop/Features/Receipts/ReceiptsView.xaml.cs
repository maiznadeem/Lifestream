using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Receipts;
using Lifestream.Desktop.Controls;
using Lifestream.Domain;

namespace Lifestream.Desktop.Features.Receipts;

/// <summary>
/// The Receipts screen. Double-clicking a row, or pressing Enter on it, opens that receipt's printable view (FEAT-18); Esc closes the side panel. The
/// blood group chip opens its menu on a click. The "Issued by" column folds away while the table is narrower than 860 DIP (a compact window, or the panel
/// open; the panel names who issued the receipt), and a compact window narrows the panel.
/// </summary>
public partial class ReceiptsView : UserControl
{
    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(UserControl));

    public ReceiptsView()
    {
        InitializeComponent();
        MoreButton.ContextMenu.SetBinding(FrameworkElement.DataContextProperty, new System.Windows.Data.Binding(nameof(DataContext)) { Source = this });

        // The "..." menu lives outside the visual tree: give it the screen's model, so its commands are bound before it opens.
        Loaded += (_, _) =>
        {
            BreakpointDescriptor.AddValueChanged(this, OnBreakpointChanged);
            ApplyBreakpoint();
            AlignNumericHeaders();
        };
        Unloaded += (_, _) => BreakpointDescriptor.RemoveValueChanged(this, OnBreakpointChanged);
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

    /// <summary>The Units header sits right, over its right-aligned numbers (the grid's own header style, with the alignment changed).</summary>
    private void AlignNumericHeaders()
    {
        if (UnitsColumn.HeaderStyle is null && Grid.ColumnHeaderStyle is { } baseStyle)
        {
            var style = new Style(typeof(DataGridColumnHeader), baseStyle);
            style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
            UnitsColumn.HeaderStyle = style;
        }
    }

    private void OnBreakpointChanged(object? sender, EventArgs e) => ApplyBreakpoint();

    private void ApplyBreakpoint()
    {
        var compact = Responsive.GetBreakpoint(this) == Breakpoint.Compact;
        if (compact)
        {
            Panel.SetResourceReference(SidePanel.PanelWidthProperty, "Width.PanelCompact");
        }
        else
        {
            Panel.ClearValue(SidePanel.PanelWidthProperty);
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
