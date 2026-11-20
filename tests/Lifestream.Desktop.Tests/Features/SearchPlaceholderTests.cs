using System.Windows.Controls;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Desktop.Controls;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// The search field of every list says what the search finds (the server's rule, SearchQueries: a part of a name, a part of a phone number, the record's
/// code or number; a blood group too, which the chip beside the field also offers), and the placeholder is never cut off with an ellipsis in the width
/// the field has ("Name, phone, blood group or ID" is, at 300). Each list screen is rendered off-screen from the fake API; the window is never shown.
/// </summary>
[Collection("Wpf")]
public sealed class SearchPlaceholderTests
{
    private const int Width = 1366;
    private const int Height = 736;

    [Fact]
    public void EveryListScreen_SearchFieldSaysWhatItFinds_AndThePlaceholderIsNotCutOff_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var found = new List<(string Screen, string Placeholder, bool Shown, bool CutOff)>();

            void Check<TView, TModel>(string screen, Func<TView> view, Func<TModel, Task> load)
                where TView : UserControl
                where TModel : Lifestream.Client.Common.ViewModelBase
            {
                var vm = Part2Harness.Create<TModel>(rig);
                using var host = Hosted.Create(Part2Harness.OnCanvas(view(), vm), Width, Height);
                Hosted.Await(() => load(vm));
                host.Settle(300);
                var box = host.Find<TextBox>("SearchBox");
                var placeholder = (TextBlock)box.Template.FindName("Placeholder", box);
                found.Add((screen, InputAssist.GetPlaceholder(box), placeholder.IsVisible, TruncationTooltip.IsTrimmed(placeholder)));
            }

            Check("Donors", () => new DonorsView(), (DonorsViewModel vm) => vm.LoadAsync());
            Check("Patients", () => new PatientsView(), (PatientsViewModel vm) => vm.LoadAsync());
            Check("Appointments", () => new AppointmentsView(), (AppointmentsViewModel vm) => vm.LoadAsync());
            Check("Requests", () => new RequestsView(), (RequestsViewModel vm) => vm.LoadAsync());
            Check("Receipts", () => new ReceiptsView(), (ReceiptsViewModel vm) => vm.LoadAsync());
            Check("Staff", () => new StaffView(), (StaffViewModel vm) => vm.LoadAsync());
            return found;
        });

        Assert.Equal(
            [
                ("Donors", "Name, phone or donor ID"),
                ("Patients", "Name, phone or patient ID"),
                ("Appointments", "Donor name, phone or ID"),
                ("Requests", "Patient name, phone or ID"),
                ("Receipts", "Name, phone or receipt ID"),
                ("Staff", "Name, username or staff ID"),
            ],
            facts.Select(f => (f.Screen, f.Placeholder)));
        Assert.All(facts, f => Assert.True(f.Shown, $"{f.Screen}: the empty search field does not show its placeholder"));
        Assert.All(facts, f => Assert.False(f.CutOff, $"{f.Screen}: the placeholder '{f.Placeholder}' is cut off"));
    }
}
