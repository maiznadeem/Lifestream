using System.Windows.Controls;

namespace Lifestream.Desktop.Features.Profile;

/// <summary>The Profile screen. It never sets its own data context: the page host gives it the view model once per visit (SHELL-13).</summary>
public partial class ProfileView : UserControl
{
    public ProfileView() => InitializeComponent();
}
