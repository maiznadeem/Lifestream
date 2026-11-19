using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Profile;
using Lifestream.Client.Navigation;
using Lifestream.Contracts.Profile;
using Lifestream.Desktop.Features.Profile;
using Lifestream.Desktop.Theme;
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

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Color a, Color b) => (Math.Max(Luminance(a), Luminance(b)) + 0.05) / (Math.Min(Luminance(a), Luminance(b)) + 0.05);

    [Fact]
    public void ProfileView_ShowsDateOfBirthAndMemberSince_AsReadOnlyText_With4Point5Contrast_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            rig.Api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(DemoData2.Profile() with { DateOfBirth = new DateOnly(1990, 5, 17), MemberSinceUtc = new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc) }));
            var vm = Part2Harness.Create<ProfileViewModel>(rig);
            using var host = Hosted.Create(Part2Harness.OnCanvas(new ProfileView(), vm), Width, Height);
            Hosted.Await(() => vm.LoadAsync());
            host.Settle(500);
            FeatureHarness.Save(host, "ProfileView_Dates.png");

            var birth = host.Find<TextBlock>("DateOfBirthValue");
            var since = host.Find<TextBlock>("MemberSinceValue");
            var labels = host.All<TextBlock>().Where(t => t.IsVisible && t.Text is "Date of birth" or "Member since").ToList();
            var raised = (Color)ThemeLoader.Resources["Color.SurfaceRaised"];
            return (
                Birth: birth.Text,
                Since: since.Text,
                AreTextBoxes: host.All<TextBox>().Any(t => t.Name is "DateOfBirthValue" or "MemberSinceValue"),
                ValueContrast: Math.Min(Contrast(((SolidColorBrush)birth.Foreground).Color, raised), Contrast(((SolidColorBrush)since.Foreground).Color, raised)),
                LabelContrast: labels.Select(l => Contrast(((SolidColorBrush)l.Foreground).Color, raised)).DefaultIfEmpty(0).Min(),
                LabelCount: labels.Count);
        });

        Assert.Equal("17 May 1990", facts.Birth);
        Assert.Equal("02 Mar 2026", facts.Since);
        Assert.False(facts.AreTextBoxes); // label and value text, not disabled inputs
        Assert.True(facts.ValueContrast >= 4.5, $"value contrast {facts.ValueContrast:F2}");
        Assert.Equal(2, facts.LabelCount);
        Assert.True(facts.LabelContrast >= 4.5, $"label contrast {facts.LabelContrast:F2}");
    }

    [Fact]
    public void ReceiptDocument_OnScreenAndInPrint_IsSetInTheBundledOnest_FakeApi()
    {
        var facts = StaHost.Run(() =>
        {
            var document = ReceiptDocument.From(DemoData2.ReceiptDetail(1), isDuplicate: true, zone: TimeZoneInfo.Utc);
            var flow = ReceiptDocumentBuilder.Build(document);
            var viewer = new FlowDocumentScrollViewer { Document = flow };
            using var host = Hosted.Create(viewer, Width, Height);
            host.Settle(300);
            Part2Harness.Save(host, "ReceiptDocument_Onest.png");

            static string Resolve(FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch)
            {
                var typeface = new Typeface(family, style, weight, stretch);
                Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), "the typeface did not resolve to a font file");
                return glyphs.Win32FamilyNames.Values.First() + "|" + glyphs.FontUri.AbsolutePath;
            }

            var runs = new List<Run>();
            void Collect(TextElement element)
            {
                switch (element)
                {
                    case Run run:
                        runs.Add(run);
                        break;
                    case Paragraph paragraph:
                        paragraph.Inlines.OfType<Run>().ToList().ForEach(runs.Add);
                        break;
                    case Table table:
                        table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks).OfType<Paragraph>().ToList().ForEach(Collect);
                        break;
                }
            }

            flow.Blocks.ToList().ForEach(Collect);
            // For print and PDF the document is paginated on its own, with no viewer: the family is on the document itself.
            var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
            paginator.PageSize = new Size(794, 1123);
            paginator.ComputePageCount();
            return (
                Document: Resolve(flow.FontFamily, flow.FontStyle, flow.FontWeight, flow.FontStretch),
                Runs: runs.Select(r => Resolve(r.FontFamily, r.FontStyle, r.FontWeight, r.FontStretch)).Distinct().ToList(),
                RunCount: runs.Count,
                Source: flow.FontFamily.Source,
                Pages: paginator.PageCount);
        });

        static bool IsBundledOnest(string face) =>
            face.StartsWith("Onest", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(face, @"/Lifestream;component/Fonts/onest-(regular|medium|semibold|bold)\.ttf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        Assert.True(IsBundledOnest(facts.Document), facts.Document);
        Assert.True(facts.RunCount >= 5, "the receipt's text runs were not found");
        Assert.All(facts.Runs, face => Assert.True(IsBundledOnest(face), face));
        Assert.DoesNotContain("Segoe", facts.Source, StringComparison.OrdinalIgnoreCase);
        Assert.True(facts.Pages >= 1);
    }
}
