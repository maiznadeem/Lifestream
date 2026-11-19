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
/// window drops the Shift column (the panel still shows it) and narrows the panel.
/// </summary>
public partial class StaffView : UserControl
{
    private static readonly DependencyPropertyDescriptor BreakpointDescriptor = DependencyPropertyDescriptor.FromProperty(Responsive.BreakpointProperty, typeof(UserControl));

    public StaffView()
    {
        InitializeComponent();
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

    private void OnBreakpointChanged(object? sender, EventArgs e) => ApplyBreakpoint();

    private void ApplyBreakpoint()
    {
        var compact = Responsive.GetBreakpoint(this) == Breakpoint.Compact;
        ShiftColumn.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
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
