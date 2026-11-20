using System.Windows.Controls;
using System.Windows.Data;
using Lifestream.Client.Features.Appointments;

namespace Lifestream.Desktop.Features.Appointments;

/// <summary>
/// The Appointments screen. It never sets its own data context: the page host gives it the view model once per visit (SHELL-13). The table shows the view model's rows
/// grouped by their local day (today's group stands out), through a collection view made here because the day depends on the view model's time zone.
/// </summary>
public partial class AppointmentsView : UserControl
{
    public AppointmentsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => GroupByDay(e.NewValue as AppointmentsViewModel);
    }

    private void GroupByDay(AppointmentsViewModel? model)
    {
        if (model is null)
        {
            Grid.ItemsSource = null;
            return;
        }

        var days = new ListCollectionView(model.Items);
        days.GroupDescriptions.Add(new PropertyGroupDescription(null, new AppointmentDayConverter(model)));
        Grid.ItemsSource = days;
    }
}
