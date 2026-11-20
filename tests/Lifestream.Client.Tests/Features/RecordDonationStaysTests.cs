using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// Record donation opens its dialog over the screen it was started from, and that screen stays (real-app walk 21: from the dashboard, the page
/// behind the dialog became Appointments, so closing the dialog left the person somewhere else). After a save the screen shows what changed.
/// Every entry point is here: Ctrl+D, the dashboard's button and the taskbar's thumb button are the shell's RecordDonationCommand; the palette runs
/// its "Record donation" action; the Appointments screen has its own button and its agenda rows.
/// </summary>
public sealed class RecordDonationStaysTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Stands in for the dialog window: records what was shown and lets the test act as the person in it. Nothing is displayed.</summary>
    private sealed class ScriptedPresenter : IFormDialogPresenter
    {
        public List<DialogViewModel> Shown { get; } = [];

        public Func<DialogViewModel, Task>? Script { get; set; }

        public async Task ShowAsync(DialogViewModel viewModel)
        {
            Shown.Add(viewModel);
            if (Script is not null)
            {
                await Script(viewModel);
            }
        }
    }

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 20, 15, 27, 0, TimeSpan.Zero));

    private static AppointmentListItem Today(int id, int hour, int minute, AppointmentStatus status = AppointmentStatus.Pending)
    {
        var slot = new DateTime(2026, 11, 20, hour, minute, 0, DateTimeKind.Utc);
        return new(id, 100 + id, $"Donor {id}", BloodType.OPos, slot.AddDays(-3), slot, status);
    }

    private static readonly AppointmentListItem[] Agenda = [Today(3, 15, 30), Today(4, 16, 0)];

    private static async Task Save(DialogViewModel dialog)
    {
        var donation = Assert.IsType<RecordDonationDialogViewModel>(dialog);
        await donation.PendingCheck;
        await donation.PrimaryCommand.ExecuteAsync();
    }

    private static (Rig Rig, ShellViewModel Shell, ScriptedPresenter Forms) Signed()
    {
        var rig = new Rig(clock: Clock);
        rig.SignIn(Role.Admin);
        rig.Api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        rig.Api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 50)));
        rig.Api.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(103)));
        rig.Api.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.Eligible()));
        rig.Api.On("RecordDonationAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91)));
        rig.Api.AppointmentsFor = q =>
        {
            var rows = Agenda.Where(a => q.Status is null || a.Status == q.Status).ToArray();
            return ApiResult<PagedResult<AppointmentListItem>>.Ok(new(rows, rows.Length, 1, 50));
        };
        rig.Api.ById["GetAppointmentAsync"] = id => ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(id, 100 + id, $"Donor {id}"));
        var forms = new ScriptedPresenter();
        rig.Get<FormDialogService>().Presenter = forms;
        var shell = rig.Get<ShellViewModel>();
        shell.Attach(null, Clock);
        return (rig, shell, forms);
    }

    private static Task Start(ShellViewModel shell, string entry) => entry switch
    {
        "shortcut" => shell.RecordDonationCommand.ExecuteAsync(), // Ctrl+D, the dashboard's button, the taskbar's thumb button
        "palette" => RunFromPalette(shell),
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };

    private static async Task RunFromPalette(ShellViewModel shell)
    {
        shell.Palette.Open();
        await shell.Palette.RunAsync(shell.Palette.Items.Single(i => i.Group == "Actions" && i.Title == "Record donation"));
    }

    [Theory]
    [InlineData("shortcut")]
    [InlineData("palette")]
    public async Task FromTheDashboard_Saved_TheDashboardStays_AndShowsTheDonation(string entry)
    {
        var (rig, shell, forms) = Signed();
        using var a = rig;
        using var b = shell;
        await shell.OpenByTitleAsync("Dashboard");
        var dashboard = Assert.IsType<DashboardViewModel>(shell.CurrentPage);
        Assert.Equal(1, rig.Api.CountOf("GetDashboardAsync"));
        forms.Script = Save;

        await Start(shell, entry);

        var dialog = Assert.IsType<RecordDonationDialogViewModel>(Assert.Single(forms.Shown));
        Assert.Equal("Donor 3", dialog.DonorName); // today's next appointment
        Assert.Same(dashboard, shell.CurrentPage); // released 2.1.6: the Appointments screen
        Assert.Equal(1, rig.Api.CountOf("RecordDonationAsync"));
        Assert.Equal(2, rig.Api.CountOf("GetDashboardAsync")); // the dashboard was read again: its agenda row, its counts, its stock
        Assert.Contains(rig.Get<IToastService>().Toasts, t => t.Kind == ToastKind.Success && t.Message.Contains("Donor 3", StringComparison.Ordinal) && t.ActionLabel == "View receipt");
    }

    [Fact]
    public async Task FromTheDashboard_Cancelled_TheDashboardStays_AndNothingIsReadAgain()
    {
        var (rig, shell, forms) = Signed();
        using var a = rig;
        using var b = shell;
        await shell.OpenByTitleAsync("Dashboard");
        var dashboard = Assert.IsType<DashboardViewModel>(shell.CurrentPage);

        await shell.RecordDonationCommand.ExecuteAsync(); // the dialog is closed without saving

        Assert.Single(forms.Shown);
        Assert.Same(dashboard, shell.CurrentPage); // released 2.1.6: the Appointments screen
        Assert.Equal(0, rig.Api.CountOf("RecordDonationAsync"));
        Assert.Equal(1, rig.Api.CountOf("GetDashboardAsync"));
        Assert.DoesNotContain(rig.Get<IToastService>().Toasts, t => t.Kind == ToastKind.Success);
    }

    [Fact]
    public async Task FromTheAppointmentsScreen_WithAnotherAppointmentOpen_TheScreenAndItsSelectionStay_AndTheListIsReadAgain()
    {
        var (rig, shell, forms) = Signed();
        using var a = rig;
        using var b = shell;
        await shell.OpenByTitleAsync("Appointments");
        var page = Assert.IsType<AppointmentsViewModel>(shell.CurrentPage);
        page.SelectedItem = page.Items.Single(i => i.Id == 4);
        await page.PendingDetail;
        var lists = rig.Api.CountOf("GetAppointmentsAsync");
        var details = rig.Api.CountOf("GetAppointmentAsync");
        forms.Script = Save;

        await shell.RecordDonationCommand.ExecuteAsync();

        Assert.Equal("Donor 3", Assert.IsType<RecordDonationDialogViewModel>(Assert.Single(forms.Shown)).DonorName);
        Assert.Same(page, shell.CurrentPage); // released 2.1.6: a new Appointments screen, opened on the next appointment
        Assert.Equal(4, page.SelectedItem?.Id);
        Assert.Equal(4, page.Detail?.Id);
        Assert.True(rig.Api.CountOf("GetAppointmentsAsync") > lists + 1, "the list was not read again"); // the shell's "next" lookup, then the list
        Assert.True(rig.Api.CountOf("GetAppointmentAsync") >= details + 2, "the open appointment was not read again"); // the dialog's, then the detail's
    }

    [Fact]
    public async Task NobodyLeftToday_StillOpensTodaysAppointments_WithNoDialog()
    {
        var (rig, shell, forms) = Signed();
        using var a = rig;
        using var b = shell;
        rig.Api.AppointmentsFor = _ => ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 0, 1, 50));
        await shell.OpenByTitleAsync("Dashboard");

        await shell.RecordDonationCommand.ExecuteAsync();

        Assert.Empty(forms.Shown); // there is nobody to record a donation for: the person chooses an appointment
        Assert.IsType<AppointmentsViewModel>(shell.CurrentPage);
    }

    [Fact]
    public async Task AppointmentGoneMeanwhile_SaysSo_AndTheDashboardStays()
    {
        var (rig, shell, forms) = Signed();
        using var a = rig;
        using var b = shell;
        rig.Api.ById["GetAppointmentAsync"] = _ => FakeApiClient.Fail<AppointmentDetail>(404, ProblemCodes.NotFound);
        await shell.OpenByTitleAsync("Dashboard");
        var dashboard = shell.CurrentPage;

        await shell.RecordDonationCommand.ExecuteAsync();

        Assert.Empty(forms.Shown);
        Assert.Same(dashboard, shell.CurrentPage);
        Assert.Contains(rig.Get<IToastService>().Toasts, t => t.Kind == ToastKind.Error && t.Message.Contains("Donor 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OnTheAppointmentsScreen_TheAgendaRowsRecordButton_DoesNotNavigate()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, "Omar Haddad"))));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, 42, "Omar Haddad")));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(42)));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.Eligible()));
        rig.Fake.On("RecordDonationAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91)));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        rig.Forms.Script = Save;

        await vm.RecordDonationForCommand.ExecuteAsync(vm.Items[0]);

        Assert.Single(rig.Forms.Shown);
        Assert.Equal(1, rig.Recorder.CountOf("RecordDonationAsync"));
        Assert.Empty(rig.Navigation.Visits); // the screen stays; it refreshes itself
        Assert.Equal(2, rig.Recorder.CountOf("GetAppointmentsAsync"));
    }
}
