using Lifestream.Desktop.Chrome;

namespace Lifestream.Desktop.Auth;

/// <summary>The login window; its title is always plain "Lifestream".</summary>
public partial class LoginWindow : ChromeWindow
{
    public LoginWindow() => InitializeComponent();
}
