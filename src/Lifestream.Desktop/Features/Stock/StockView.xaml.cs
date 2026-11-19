using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Stock;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Features.Stock;

/// <summary>
/// The Stock screen. Ctrl+F puts focus on the blood group chip (the screen has no search box); the chip opens its menu on a click; Enter on a row moves
/// into the group's panel and Esc closes it; a compact window drops the "More than 7 days left" column (the panel and the status still carry it).
/// </summary>
public partial class StockView : UserControl
{
    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(UserControl));
    private ViewModelBase? _model;

    public StockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            BreakpointDescriptor.AddValueChanged(this, OnBreakpointChanged);
            ApplyBreakpoint();
        };
        Unloaded += (_, _) =>
        {
            BreakpointDescriptor.RemoveValueChanged(this, OnBreakpointChanged);
            Detach();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _model = e.NewValue as ViewModelBase;
        if (_model is not null)
        {
            _model.FocusSearchRequested += OnFocusSearchRequested;
        }
    }

    private void Detach()
    {
        if (_model is not null)
        {
            _model.FocusSearchRequested -= OnFocusSearchRequested;
            _model = null;
        }
    }

    /// <summary>Ctrl+F: the stock screen has no search box, so the blood group chip takes focus.</summary>
    private void OnFocusSearchRequested(object? sender, EventArgs e) => BloodTypeChip.Focus();

    private void OnBloodTypeChipClick(object sender, RoutedEventArgs e)
    {
        BloodTypeMenu.PlacementTarget = BloodTypeChip;
        BloodTypeMenu.Placement = PlacementMode.Bottom;
        BloodTypeMenu.IsOpen = true;
    }

    /// <summary>A group chosen in the chip's menu (each item's data context is one of the blood type options).</summary>
    private void OnBloodTypeChosen(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is MenuItem { DataContext: Lifestream.Client.Common.FilterOption<Lifestream.Domain.BloodType> option } && DataContext is StockViewModel vm)
        {
            vm.ChooseBloodTypeCommand.Execute(option);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not StockViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && vm.SelectedTile is not null)
        {
            // The row is already open in the panel: Enter moves into it, to its first action.
            Panel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.IsPanelOpen)
        {
            vm.ClosePanelCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBreakpointChanged(object? sender, EventArgs e) => ApplyBreakpoint();

    private void ApplyBreakpoint()
    {
        var compact = Responsive.GetBreakpoint(this) == Breakpoint.Compact;
        FreshColumn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact)
        {
            Panel.SetResourceReference(SidePanel.PanelWidthProperty, "Width.PanelCompact");
        }
        else
        {
            Panel.ClearValue(SidePanel.PanelWidthProperty);
        }
    }
}
