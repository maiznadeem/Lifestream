using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Features.Staff;

namespace Lifestream.Desktop.Features.Staff;

/// <summary>
/// The Staff screen. A row's thumbnail is requested when the row is first shown (a recycled row asks again for its new person): the list itself
/// carries no photo bytes (FEAT-20).
/// </summary>
public partial class StaffView : UserControl
{
    public StaffView() => InitializeComponent();

    private void OnAvatarShown(object sender, RoutedEventArgs e) => RequestPhoto(sender as FrameworkElement);

    private void OnAvatarContextChanged(object sender, DependencyPropertyChangedEventArgs e) => RequestPhoto(sender as FrameworkElement);

    private void RequestPhoto(FrameworkElement? avatar)
    {
        if (avatar?.DataContext is StaffRow row && DataContext is StaffViewModel vm && row.HasPhoto)
        {
            vm.LoadPhotoCommand.Execute(row);
        }
    }
}
