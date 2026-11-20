using System.Windows;
using System.Windows.Input;

namespace Lifestream.Desktop.Controls;

/// <summary>
/// "Waking up the server, this can take about a minute and a half" with a Cancel, for every operation that is not a screen load (SHELL-07). The view
/// binds <see cref="IsActive"/> to the view model's waking flag and <see cref="CancelCommand"/> to the command that cancels the request.
/// </summary>
public partial class WakingNotice : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(WakingNotice), new PropertyMetadata(false, (d, e) => ((WakingNotice)d).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));
    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(nameof(CancelCommand), typeof(ICommand), typeof(WakingNotice), new PropertyMetadata(null));

    public WakingNotice()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed; // takes no room until a call is waiting on a waking server
        Message.Text = Lifestream.Client.Common.WakingTexts.Operation;
    }

    /// <summary>True while the notice is shown.</summary>
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }

    /// <summary>Stops the request that is waiting on the server.</summary>
    public ICommand? CancelCommand { get => (ICommand?)GetValue(CancelCommandProperty); set => SetValue(CancelCommandProperty, value); }
}
