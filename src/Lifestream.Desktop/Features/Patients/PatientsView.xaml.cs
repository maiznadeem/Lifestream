using System.Windows.Controls;

namespace Lifestream.Desktop.Features.Patients;

/// <summary>The Patients screen. It never sets its own data context: the page host gives it the view model once per visit (SHELL-13).</summary>
public partial class PatientsView : UserControl
{
    public PatientsView() => InitializeComponent();
}
