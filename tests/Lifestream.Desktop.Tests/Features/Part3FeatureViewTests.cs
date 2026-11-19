using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Controls;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Navigation;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// Rendered off-screen from the fake API (never shown): the donor and patient detail panes offer their related action and it opens the real form with the person chosen (FEAT-04),
/// and the date columns of Receipts and Requests are wide enough for their text (FEAT-14).
/// </summary>
[Collection("Wpf")]
public sealed class Part3FeatureViewTests
{
    private const int Width = 1366;
    private const int Height = 736;

    private static readonly Regex DateText = new(@"^\d{1,2} [A-Za-z]{3} \d{4}( \d{2}:\d{2})?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>True when the text, with the cell's own padding, is wider than the block: it is shown with an ellipsis (TruncationTooltip.IsTrimmed ignores the padding, so it would miss this).</summary>
    private static bool IsClipped(TextBlock block)
    {
        var formatted = new System.Windows.Media.FormattedText(
            block.Text,
            System.Globalization.CultureInfo.CurrentUICulture,
            block.FlowDirection,
            new System.Windows.Media.Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize,
            System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(block).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace + block.Padding.Left + block.Padding.Right > block.ActualWidth + 0.5;
    }

    [Fact]
    public void DonorsView_SelectedDonor_OffersBookAppointment_WhichOpensTheBookingFormWithThatDonor_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<DonorsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.DonorsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            var hiddenBefore = !host.All<Button>().Any(b => b.IsVisible && AutomationProperties.GetName(b) == "Book appointment");
            Hosted.Await(() => { vm.SelectedItem = vm.Items[0]; return vm.PendingDetail; });
            host.Settle(300);
            FeatureHarness.Save(host, "DonorsView_BookAppointment.png");

            var button = host.All<Button>().First(b => b.IsVisible && AutomationProperties.GetName(b) == "Book appointment");
            var enabled = button.IsEnabled;
            var donorId = vm.Detail!.Id;
            Hosted.Await(() => vm.BookAppointmentCommand.ExecuteAsync());
            var appointments = (AppointmentsViewModel)rig.Get<INavigationService>().Current!;
            Hosted.Await(() => appointments.LoadAsync());
            var editor = appointments.Editor as AppointmentEditorViewModel;
            return (hiddenBefore, enabled, donorId, EditorDonor: editor?.DonorId, editor?.IsNew);
        });

        Assert.True(facts.hiddenBefore); // nothing selected: no related action
        Assert.True(facts.enabled);
        Assert.Equal(facts.donorId, facts.EditorDonor); // the booking form opened, with this donor chosen
        Assert.True(facts.IsNew);
    }

    [Fact]
    public void PatientsView_SelectedPatient_OffersNewRequest_WhichOpensTheRequestFormWithThatPatient_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var vm = Part2Harness.Create<PatientsViewModel>(rig);
            using var host = Hosted.Create(FeatureHarness.PatientsView(vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(300);
            Hosted.Await(() => { vm.SelectedItem = vm.Items[0]; return vm.PendingDetail; });
            host.Settle(300);
            FeatureHarness.Save(host, "PatientsView_NewRequest.png");

            var button = host.All<Button>().First(b => b.IsVisible && AutomationProperties.GetName(b) == "New request");
            var enabled = button.IsEnabled;
            var patientId = vm.Detail!.Id;
            Hosted.Await(() => vm.NewRequestCommand.ExecuteAsync());
            var requests = (RequestsViewModel)rig.Get<INavigationService>().Current!;
            Hosted.Await(() => requests.LoadAsync());
            var editor = requests.Editor as RequestEditorViewModel;
            return (enabled, patientId, EditorPatient: editor?.PatientOption?.Id);
        });

        Assert.True(facts.enabled);
        Assert.Equal(facts.patientId, facts.EditorPatient);
    }

    [Fact]
    public void ReceiptsAndRequests_DateColumns_ShowTheirWholeText_AtTheDefaultWindowSize_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            var receipts = Part2Harness.Create<ReceiptsViewModel>(rig);
            using var receiptsHost = Hosted.Create(Part2Harness.OnCanvas(new ReceiptsView(), receipts), Width, Height);
            Hosted.Await(() => receipts.LoadAsync());
            receiptsHost.Settle(400);
            FeatureHarness.Save(receiptsHost, "ReceiptsView_DatesWhole.png");
            var receiptDates = receiptsHost.All<TextBlock>().Where(t => t.IsVisible && t.DataContext is ReceiptRow && DateText.IsMatch(t.Text)).ToList();

            var requests = Part2Harness.Create<RequestsViewModel>(rig);
            using var requestsHost = Hosted.Create(Part2Harness.OnCanvas(new RequestsView(), requests), Width, Height);
            Hosted.Await(() => requests.LoadAsync());
            requestsHost.Settle(400);
            FeatureHarness.Save(requestsHost, "RequestsView_DatesWhole.png");
            var neededBy = requestsHost.All<TextBlock>().Where(t => t.IsVisible && t.DataContext is Lifestream.Contracts.Requests.RequestListItem && DateText.IsMatch(t.Text)).ToList();

            return (
                ReceiptDates: receiptDates.Count,
                ReceiptTrimmed: receiptDates.Where(IsClipped).Select(t => t.Text).ToList(),
                ReceiptDateWithTime: receiptDates.Any(t => t.Text.Contains(':', StringComparison.Ordinal)),
                NeededByDates: neededBy.Count,
                NeededByTrimmed: neededBy.Where(IsClipped).Select(t => t.Text).ToList());
        });

        Assert.True(facts.ReceiptDates >= 5, $"expected the receipt dates to be rendered, found {facts.ReceiptDates}");
        Assert.True(facts.ReceiptDateWithTime); // a receipt has its time
        Assert.Empty(facts.ReceiptTrimmed);
        Assert.True(facts.NeededByDates >= 5, $"expected the 'Needed by' dates to be rendered, found {facts.NeededByDates}");
        Assert.Empty(facts.NeededByTrimmed);
    }
}
