using System.Windows;
using System.Windows.Controls;

namespace Lifestream.Desktop.Auth;

/// <summary>The sign-in screen. All behaviour is in LoginViewModel; this view only binds to it and folds the brand panel away when the window is narrow.</summary>
public partial class LoginView : UserControl
{
    /// <summary>Below this width the brand panel is hidden and a compact logo row tops the sign-in card.</summary>
    internal const double WideLayoutMinWidth = 760;

    public LoginView() => InitializeComponent();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var wide = e.NewSize.Width >= WideLayoutMinWidth;
        Hero.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        HeroColumn.Width = wide ? new GridLength(5, GridUnitType.Star) : new GridLength(0);
        CompactBrand.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
    }
}
