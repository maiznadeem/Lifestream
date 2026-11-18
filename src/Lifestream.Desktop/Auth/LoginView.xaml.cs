using System.Windows.Controls;

namespace Lifestream.Desktop.Auth;

/// <summary>The sign-in form. All behaviour is in LoginViewModel; this view only binds to it.</summary>
public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();
}
