using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Stock;
using Lifestream.Contracts.Stock;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Features.Stock;

/// <summary>
/// The Stock screen. Ctrl+F puts focus on the table (the screen has no search box; eight rows need none); the blood group chip, shown only for a group
/// another screen asked for, opens its menu on a click; Enter on a row moves into the group's panel and Esc closes it. The six age-band headers come from
/// <see cref="StockAges.Bands"/>, the definition the server counts with. A compact window folds the Refresh label to its icon, shows the units without
/// their meter and narrows the panel;
/// the Status column folds away when the table gets narrow (the panel still says it).
/// </summary>
public partial class StockView : UserControl
{
    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(UserControl));
    private ViewModelBase? _model;

    public StockView()
    {
        InitializeComponent();
        SetBandHeaders();
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

    /// <summary>Ctrl+F: the stock screen has no search box, so the table takes focus (on its selected or first row).</summary>
    private void OnFocusSearchRequested(object? sender, EventArgs e)
    {
        if (Grid.Items.Count > 0)
        {
            Grid.CurrentCell = new DataGridCellInfo(Grid.SelectedItem ?? Grid.Items[0], Grid.Columns[0]);
        }

        Grid.Focus();
    }

    /// <summary>"0–6" to "35–42" for each band column, with what it means as its tooltip; the last band is marked as the one that expires soon.</summary>
    private void SetBandHeaders()
    {
        DataGridTemplateColumn[] columns = [Band0, Band1, Band2, Band3, Band4, Band5];
        for (var i = 0; i < columns.Length && i < StockAges.Bands.Count; i++)
        {
            var band = StockAges.Bands[i];
            columns[i].Header = new TextBlock
            {
                Text = band.Label,
                ToolTip = i == columns.Length - 1
                    ? $"{band.FromDay} to {band.ToDay} days since collection: expires within {Lifestream.Domain.BloodRules.ExpiringSoonDays} days"
                    : $"{band.FromDay} to {band.ToDay} days since collection",
            };
            AutomationProperties.SetName((TextBlock)columns[i].Header, $"{band.FromDay} to {band.ToDay} days old");
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
        RefreshLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UnitsColumn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; // the count with its meter
        UnitsCompactColumn.Visibility = compact ? Visibility.Visible : Visibility.Collapsed; // the count alone
    }
}
