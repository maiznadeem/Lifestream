using System.Windows;
using System.Windows.Controls;
using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.Patients;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Staff;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;
using Lifestream.Desktop.Features.Appointments;
using Lifestream.Desktop.Features.Donors;
using Lifestream.Desktop.Features.Patients;
using Lifestream.Desktop.Features.Receipts;
using Lifestream.Desktop.Features.Requests;
using Lifestream.Desktop.Features.Staff;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// UI quirk (OWN-09 release prep, FEAT-14): the receipts list's "Issued by" cell (a name plus "#id" on two lines) overflowed the 40 DIP row and overlapped the next row.
/// Every list screen is rendered off-screen from the fake API with very long names, and no text in any cell may extend beyond its own row. The window is never shown.
/// </summary>
[Collection("Wpf")]
public sealed class ListCellFitRenderTests
{
    private const int Width = 1366;
    private const int Height = 736;
    private const string LongName = "Maximiliana Konstantinopoulos-Vandenberghe Montgomery-Featherstonehaugh";

    private static void UseLongNames(Lifestream.Client.Tests.Fakes.FakeApiClient api)
    {
        const int rows = 12;
        api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([.. Enumerable.Range(1, rows).Select(i => DemoData.DonorRow(i) with { Name = LongName })], rows, 1, 50)));
        api.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Ok(new([.. Enumerable.Range(1, rows).Select(i => DemoData.PatientRow(i) with { Name = LongName })], rows, 1, 50)));
        api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new([.. Enumerable.Range(1, rows).Select(i => DemoData2.AppointmentRow(i) with { DonorName = LongName })], rows, 1, 50)));
        api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([.. Enumerable.Range(1, rows).Select(i => DemoData2.RequestRow(i) with { PatientName = LongName })], rows, 1, 50)));
        api.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([.. Enumerable.Range(1, rows).Select(i => DemoData2.ReceiptRow(i) with { PartyName = LongName, StaffName = LongName })], rows, 1, 50)));
        api.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(new([.. Enumerable.Range(1, rows).Select(i => DemoData2.StaffRow(i) with { DisplayName = LongName, Username = "very.long.user.name.that.goes.on" })], rows, 1, 50)));
    }

    /// <summary>The visible text blocks (and hyperlink text) of a row that are not inside the row's own bounds, as "what: top..bottom of height".</summary>
    private static List<string> Overflows(Hosted host, string screen)
    {
        var problems = new List<string>();
        var rows = host.All<DataGridRow>().Where(r => r.IsVisible && r.ActualHeight > 0).ToList();
        if (rows.Count == 0)
        {
            problems.Add($"{screen}: no rows rendered");
        }

        foreach (var row in rows)
        {
            foreach (var cell in Hosted.Descendants(row).OfType<DataGridCell>())
            {
                foreach (var text in Hosted.Descendants(cell).OfType<TextBlock>().Where(t => t.IsVisible && t.ActualHeight > 0))
                {
                    var bounds = Hosted.BoundsOf(text, row);
                    if (bounds.Top < -0.5 || bounds.Bottom > row.ActualHeight + 0.5)
                    {
                        problems.Add($"{screen}: '{text.Text}{string.Concat(text.Inlines.OfType<System.Windows.Documents.Span>().SelectMany(s => s.Inlines).OfType<System.Windows.Documents.Run>().Select(r => r.Text))}' spans {bounds.Top:0.#}..{bounds.Bottom:0.#} in a row {row.ActualHeight:0.#} high");
                    }
                }
            }
        }

        return problems;
    }

    [Fact]
    public void EveryListScreen_WithLongNames_NoCellTextExtendsBeyondItsRow_FakeApi()
    {
        var problems = StaHost.Run(() =>
        {
            using var rig = Part2Harness.NewRig();
            UseLongNames(rig.Api);
            var found = new List<string>();

            void Check<TView, TModel>(string screen, Func<TView> view, Func<TModel, Task> load)
                where TView : UserControl
                where TModel : Lifestream.Client.Common.ViewModelBase
            {
                var vm = Part2Harness.Create<TModel>(rig);
                using var host = Hosted.Create(Part2Harness.OnCanvas(view(), vm), Width, Height);
                Hosted.Await(() => load(vm));
                host.Settle(300);
                Part2Harness.Save(host, $"ListCellFit_{screen}.png");
                found.AddRange(Overflows(host, screen));
            }

            Check("Donors", () => new DonorsView(), (DonorsViewModel vm) => vm.LoadAsync());
            Check("Patients", () => new PatientsView(), (PatientsViewModel vm) => vm.LoadAsync());
            Check("Appointments", () => new AppointmentsView(), (AppointmentsViewModel vm) => vm.LoadAsync());
            Check("Requests", () => new RequestsView(), (RequestsViewModel vm) => vm.LoadAsync());
            Check("Receipts", () => new ReceiptsView(), (ReceiptsViewModel vm) => vm.LoadAsync());
            Check("Staff", () => new StaffView(), (StaffViewModel vm) => vm.LoadAsync());
            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(12)));
    }

    [Fact]
    public void TheCheckItself_FlagsATextThatIsTallerThanItsRow()
    {
        var problems = StaHost.Run(() =>
        {
            var grid = new DataGrid { AutoGenerateColumns = false, RowHeight = 40, ItemsSource = new[] { new { Text = "x" } } };
            var column = new DataGridTemplateColumn { Width = 60 };
            var template = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetValue(TextBlock.TextProperty, string.Join(" ", Enumerable.Repeat("overflowing", 6)));
            factory.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            template.VisualTree = factory;
            column.CellTemplate = template;
            grid.Columns.Add(column);
            using var host = Hosted.Create(grid, 300, 200);
            host.Settle(200);
            return Overflows(host, "Probe");
        });

        Assert.NotEmpty(problems);
    }
}
