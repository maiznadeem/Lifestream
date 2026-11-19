using System.Windows.Controls;

namespace Lifestream.Desktop.Shell;

/// <summary>Access-denied page: "Back to Dashboard" is the primary action; nothing here signs the user out.</summary>
public partial class AccessDeniedView : UserControl
{
    public AccessDeniedView() => InitializeComponent();
}
