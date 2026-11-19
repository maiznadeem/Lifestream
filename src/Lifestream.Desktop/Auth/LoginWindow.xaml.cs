using System.Globalization;
using System.Windows.Data;
using System.Windows.Shell;
using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Auth;

/// <summary>The sign-in window; its title is always plain "Lifestream". The sign-in screen draws under the title bar; only the caption buttons sit on top.</summary>
public partial class LoginWindow : ChromeWindow
{
    /// <summary>True (the server is waking) to indeterminate taskbar progress, false to none.</summary>
    public static readonly IValueConverter WakingToProgress = new WakingProgressConverter();

    public LoginWindow() => InitializeComponent();

    private sealed class WakingProgressConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is true ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.None;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
