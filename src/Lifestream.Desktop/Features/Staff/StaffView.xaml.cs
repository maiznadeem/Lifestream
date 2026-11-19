using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Lifestream.Client.Features.Staff;
using Lifestream.Desktop.Controls;

namespace Lifestream.Desktop.Features.Staff;

/// <summary>
/// The Staff screen. A row's thumbnail is requested when the row is first shown (a recycled row asks again for its new person): the list itself
/// carries no photo bytes (FEAT-20). Enter on a row moves into its side panel and Esc closes the panel (asking first when a form has changes). A compact
/// window drops the Shift column (the panel still shows it), folds Add staff member to its icon and narrows the panel. While the panel is open the page
/// header's Add staff member is a secondary button, so the panel's Reset password is the one filled button. The salary chip opens the range popup.
/// </summary>
public partial class StaffView : UserControl
{
    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(UserControl));

    private StaffViewModel? _model;

    public StaffView()
    {
        InitializeComponent();
        MoreButton.ContextMenu.SetBinding(FrameworkElement.DataContextProperty, new System.Windows.Data.Binding(nameof(DataContext)) { Source = this });
        DataContextChanged += (_, e) =>
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = e.NewValue as StaffViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
            }

            ApplyPrimary();

            // The "..." menu lives outside the visual tree: give it the screen's model now, so Delete's state and its reason are right before it opens.
        };
        Loaded += (_, _) =>
        {
            BreakpointDescriptor.AddValueChanged(this, OnBreakpointChanged);
            ApplyBreakpoint();
        };
        Unloaded += (_, _) => BreakpointDescriptor.RemoveValueChanged(this, OnBreakpointChanged);
    }

    /// <summary>A row's avatar was shown: ask for its photo, and again whenever the row is recycled for another person.</summary>
    private void OnAvatarShown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement avatar)
        {
            avatar.DataContextChanged -= OnAvatarContextChanged; // once per avatar, however often it is loaded
            avatar.DataContextChanged += OnAvatarContextChanged;
            RequestPhoto(avatar);
        }
    }

    private void OnAvatarContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestPhoto(sender as FrameworkElement);

    private void RequestPhoto(FrameworkElement? avatar)
    {
        if (avatar?.DataContext is StaffRow row && DataContext is StaffViewModel vm && row.HasPhoto)
        {
            vm.LoadPhotoCommand.Execute(row);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not StaffViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && vm.SelectedItem is not null)
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

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StaffViewModel.IsPanelOpen) or null)
        {
            ApplyPrimary();
        }
    }

    /// <summary>One filled button per screen: the header's Add staff member is the primary until a panel opens, then the panel's action is.</summary>
    private void ApplyPrimary() => AddButton.SetResourceReference(StyleProperty, _model?.IsPanelOpen == true ? "Button.Secondary" : "Button.Primary");

    private void OnSalaryChipClick(object sender, RoutedEventArgs e) => SalaryPopup.IsOpen = !SalaryPopup.IsOpen;

    private void OnSalaryPopupOpened(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => SalaryFromBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);

    private void OnSalaryDone(object sender, RoutedEventArgs e) => CloseSalaryPopup();

    private void OnSalaryPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            CloseSalaryPopup();
            e.Handled = true;
        }
    }

    private void CloseSalaryPopup()
    {
        SalaryPopup.IsOpen = false;
        SalaryChip.Focus();
    }

    private void OnBreakpointChanged(object? sender, EventArgs e) => ApplyBreakpoint();

    private void ApplyBreakpoint()
    {
        var compact = Responsive.GetBreakpoint(this) == Breakpoint.Compact;
        ShiftColumn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        AddLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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
