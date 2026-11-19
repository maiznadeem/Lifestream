using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The booking, reschedule and record-donation gaps the verifiers found (FEAT-11, FEAT-13, FEAT-16, FEAT-09, API-11), against the fake API client:
/// the old day's times never reach a request for the new day, the action waits for what it depends on, a reschedule keeps its time and can carry an
/// override reason, and a donation can't be recorded before the eligibility check has answered.
/// </summary>
public sealed class AppointmentGapTests
{
    private static readonly DateOnly Day = new(2027, 3, 10);

    /// <summary>Times for any day; the day in <paramref name="held"/> answers only when <paramref name="gate"/> is released.</summary>
    private static Part2Rig RigWithGatedDay(DateOnly? held = null, TaskCompletionSource? gate = null)
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetSlotsAsync", async _ =>
        {
            var day = rig.Recorder.LastArg<DateOnly>("GetSlotsAsync");
            if (day == held && gate is not null)
            {
                await gate.Task;
            }

            return ApiResult<IReadOnlyList<SlotInfo>>.Ok(Samples2.DaySlots(day));
        });
        return rig;
    }

    private static ApiProblem IneligibleProblem(params string[] reasons) =>
        new(409, ProblemCodes.Ineligible, "The donor is not eligible.", "corr-1") { Errors = new Dictionary<string, string[]> { ["reasons"] = reasons } };

    // ---- FEAT-11: booking ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Booking_DateChangedBeforeTheNewTimesArrive_ClearsTheOldTimes_DisablesBook_ThenSendsTheNewDate_FakeApi()
    {
        var gate = new TaskCompletionSource();
        var rig = RigWithGatedDay(Day.AddDays(1), gate);
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor()));
        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.BookingForm();
        form.DonorOption = new RecordOption(43, "Omar Haddad", null);
        await form.PendingDonor;
        form.Date = Day;
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.Single(s => s.Display == "10:30");

        form.Date = Day.AddDays(1); // the new day's times are held back

        Assert.Empty(form.Slots); // the old day's times are gone at once
        Assert.True(form.IsLoadingSlots);
        Assert.False(form.SaveCommand.CanExecute(null)); // Book is disabled while the times load
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("BookAppointmentAsync")); // and pressing it (Enter) books nothing

        gate.SetResult();
        await form.PendingSlots;
        Assert.True(form.SaveCommand.CanExecute(null));
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(new DateTime(2027, 3, 11, 10, 30, 0, DateTimeKind.Utc), rig.Recorder.LastArg<BookAppointmentRequest>("BookAppointmentAsync").SlotStartUtc);
    }

    [Fact]
    public async Task Booking_WhenTheNewDaysTimesCouldNotBeLoaded_AChosenTimeIsStillConvertedOnTheChosenDate_FakeApi()
    {
        var rig = new Part2Rig();
        var first = true;
        rig.Fake.On("GetSlotsAsync", _ =>
        {
            var day = rig.Recorder.LastArg<DateOnly>("GetSlotsAsync");
            var answer = first ? ApiResult<IReadOnlyList<SlotInfo>>.Ok(Samples2.DaySlots(day)) : FakeApiClient.Fail<IReadOnlyList<SlotInfo>>(0, ClientProblemCodes.Unreachable);
            first = false;
            return Task.FromResult(answer);
        });
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor()));
        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.BookingForm();
        form.DonorOption = new RecordOption(43, "Omar Haddad", null);
        await form.PendingDonor;
        form.Date = Day;
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.Single(s => s.Display == "10:30");

        form.Date = Day.AddDays(2); // its times fail to load
        await form.PendingSlots;
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(new DateTime(2027, 3, 12, 10, 30, 0, DateTimeKind.Utc), rig.Recorder.LastArg<BookAppointmentRequest>("BookAppointmentAsync").SlotStartUtc);
    }

    // ---- FEAT-13: reschedule -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reschedule_DateChangedBeforeTheNewTimesArrive_WaitsAndThenSendsTheNewDate_FakeApi()
    {
        var gate = new TaskCompletionSource();
        var rig = RigWithGatedDay(Day.AddDays(3), gate);
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(42)));
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.RescheduleForm(Samples2.Appointment()); // 10 Mar 2027 10:30
        await form.PendingSlots;

        form.Date = Day.AddDays(3); // only the date changes
        Assert.False(form.SaveCommand.CanExecute(null));
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("RescheduleAppointmentAsync")); // nothing is sent with the old date

        gate.SetResult();
        await form.PendingSlots;
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(new DateTime(2027, 3, 13, 10, 30, 0, DateTimeKind.Utc), rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync").SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_OfAPastPendingAppointment_ChangingOnlyTheDate_KeepsTheTime_FakeApi()
    {
        var rig = RigWithGatedDay();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(42)));
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var twoDaysAgo = rig.Clock.GetUtcNow().UtcDateTime.Date.AddDays(-2).AddHours(10).AddMinutes(30);
        var past = Samples2.Appointment() with { SlotStartUtc = DateTime.SpecifyKind(twoDaysAgo, DateTimeKind.Utc) };
        var form = rig.RescheduleForm(past);
        await form.PendingSlots; // the old day's 10:30 is a past slot

        Assert.Equal(new TimeOnly(10, 30), form.SelectedTime); // the appointment's own time is kept, not cleared as "past"
        form.Date = rig.Today.AddDays(6);
        await form.PendingSlots;

        Assert.Equal(new TimeOnly(10, 30), form.SelectedTime); // only the date changed: the time stays
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(rig.Today.AddDays(6).ToDateTime(new TimeOnly(10, 30)), rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync").SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_OntoADayWhereTheDonorIsNotEligible_AsksForAReason_AndSendsIt_FakeApi()
    {
        var rig = RigWithGatedDay();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RecentDonor(42))); // last donated 17 Nov 2025: not eligible for 90 days
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.RescheduleForm(Samples2.Appointment()); // 10 Mar 2027: eligible on that day
        await form.PendingDonor;
        await form.PendingSlots;
        Assert.Equal(BannerState.Eligible, form.Banner.State);

        form.Date = rig.Today.AddDays(5);
        await form.PendingSlots;
        Assert.Equal(BannerState.NotEligible, form.Banner.State);
        Assert.True(form.Banner.RequiresReason);

        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("RescheduleAppointmentAsync")); // no reason: nothing is sent
        Assert.NotNull(form.GetErrorFor(nameof(AppointmentEditorViewModel.OverrideReason)));

        form.OverrideReason = "Travelling that week, doctor agreed.";
        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync");
        Assert.Equal("Travelling that week, doctor agreed.", sent.OverrideReason);
        Assert.Equal(rig.Today.AddDays(5).ToDateTime(new TimeOnly(10, 30)), sent.SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_OntoAnEligibleDay_SendsNoReason_FakeApi()
    {
        var rig = RigWithGatedDay();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(42)));
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.RescheduleForm(Samples2.Appointment());
        await form.PendingDonor;
        await form.PendingSlots;

        form.Date = Day.AddDays(1);
        await form.PendingSlots;
        await form.SaveCommand.ExecuteAsync();

        Assert.Null(rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync").OverrideReason);
    }

    // ---- FEAT-09 / API-11: the donor check and the server's refusal ------------------------------------------------------

    [Fact]
    public async Task Booking_WhileTheDonorCheckIsPending_IsDisabled_FakeApi()
    {
        var gate = new TaskCompletionSource();
        var rig = RigWithGatedDay();
        rig.Fake.On("GetDonorAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<DonorDetail>.Ok(Samples2.RecentDonor(42));
        });
        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.BookingForm();
        form.Date = rig.Today.AddDays(5);
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.First(s => s.IsAvailable);

        form.DonorOption = new RecordOption(42, "Ayse", null);
        Assert.Equal(BannerState.Checking, form.Banner.State);
        Assert.False(form.SaveCommand.CanExecute(null));
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("BookAppointmentAsync")); // an ineligible donor is never booked with no reason

        gate.SetResult();
        await form.PendingDonor;
        Assert.Equal(BannerState.NotEligible, form.Banner.State);
        Assert.False(form.SaveCommand.CanExecute(null) && form.Banner.State == BannerState.Checking);
    }

    [Fact]
    public async Task Booking_WhenTheCheckFailedAndTheServerRefusesTheDonor_ShowsTheReasonsAndAsksForOne_FakeApi()
    {
        var rig = RigWithGatedDay();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Fail(new ApiProblem(0, ClientProblemCodes.Unreachable, "unreachable")));
        var attempts = 0;
        rig.Fake.On("BookAppointmentAsync", _ => Task.FromResult(++attempts == 1
            ? ApiResult<AppointmentDetail>.Fail(IneligibleProblem("At least 90 days are needed between donations."))
            : ApiResult<AppointmentDetail>.Ok(Samples2.Appointment())));
        var form = rig.BookingForm();
        form.DonorOption = new RecordOption(42, "Ayse", null);
        await form.PendingDonor;
        Assert.Equal(BannerState.Unavailable, form.Banner.State);
        form.Date = rig.Today.AddDays(5);
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.First(s => s.IsAvailable);

        await form.SaveCommand.ExecuteAsync(); // the API says not eligible

        Assert.Equal(BannerState.NotEligible, form.Banner.State);
        Assert.True(form.Banner.RequiresReason); // so the reason field is shown
        Assert.Contains("At least 90 days are needed between donations.", form.Banner.Lines);
        Assert.NotNull(form.GetErrorFor(nameof(AppointmentEditorViewModel.OverrideReason)));

        form.OverrideReason = "Cleared by the duty doctor.";
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal("Cleared by the duty doctor.", rig.Recorder.LastArg<BookAppointmentRequest>("BookAppointmentAsync").OverrideReason);
    }

    [Fact]
    public void Banner_ShowsTheMedicalHold_AndAHoldMakesTheDonorNotEligible()
    {
        var banner = new EligibilityBannerViewModel();
        var held = Samples2.RestedDonor() with { MedicalHold = true };

        banner.Show(held, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1));

        Assert.Equal(BannerState.NotEligible, banner.State);
        Assert.Contains("Medical hold: yes", banner.Lines);
        Assert.Contains(Eligibility.MedicalHoldReason, banner.Lines);

        banner.Show(Samples2.RestedDonor(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1));
        Assert.Equal(BannerState.Eligible, banner.State);
        Assert.Contains("Medical hold: none", banner.Lines);
    }

    // ---- FEAT-16: record donation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordDonation_IsDisabledUntilTheEligibilityCheckAnswers_FakeApi()
    {
        var gate = new TaskCompletionSource();
        var rig = new Part2Rig();
        rig.Fake.On("GetDonorAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<DonorDetail>.Ok(Samples2.RecentDonor());
        });
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.NotEligible()));
        rig.Fake.On("RecordDonationAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91)));
        var dialog = new RecordDonationDialogViewModel(rig.Api, Samples2.Appointment(), rig.Clock, TimeZoneInfo.Utc);

        Assert.Equal(BannerState.Checking, dialog.Banner.State);
        Assert.False(dialog.PrimaryCommand.CanExecute(null)); // the action waits for the check
        await dialog.PrimaryCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("RecordDonationAsync")); // an ineligible donor is not recorded with no reason

        gate.SetResult();
        await dialog.PendingCheck;
        Assert.Equal(BannerState.NotEligible, dialog.Banner.State);
        Assert.True(dialog.PrimaryCommand.CanExecute(null));
        await dialog.PrimaryCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("RecordDonationAsync")); // still needs the reason

        dialog.OverrideReason = "Cleared by the duty doctor";
        await dialog.PrimaryCommand.ExecuteAsync();
        var sent = rig.Recorder.LastArg<RecordDonationRequest>("RecordDonationAsync");
        Assert.Equal("Cleared by the duty doctor", sent.OverrideReason); // the API stores it on the appointment
    }

    [Fact]
    public async Task RecordDonation_WhenTheCheckFailed_TheApiRefusesWithoutAReason_AndTheDialogAsksForOne_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Fail(new ApiProblem(0, ClientProblemCodes.Unreachable, "unreachable")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Fail(new ApiProblem(0, ClientProblemCodes.Unreachable, "unreachable")));
        var attempts = 0;
        rig.Fake.On("RecordDonationAsync", _ => Task.FromResult(++attempts == 1
            ? ApiResult<ReceiptDetail>.Fail(IneligibleProblem("At least 90 days are needed between donations."))
            : ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91))));
        var dialog = new RecordDonationDialogViewModel(rig.Api, Samples2.Appointment(), rig.Clock, TimeZoneInfo.Utc);
        await dialog.PendingCheck;
        Assert.Equal(BannerState.Unavailable, dialog.Banner.State);
        Assert.True(dialog.PrimaryCommand.CanExecute(null)); // a failed check does not lock the dialog: the API decides

        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.False(dialog.Completed);
        Assert.Equal(BannerState.NotEligible, dialog.Banner.State);
        Assert.True(dialog.Banner.RequiresReason);
        Assert.Contains("isn't eligible", dialog.FormError!, StringComparison.Ordinal);

        dialog.OverrideReason = "Cleared by the duty doctor";
        await dialog.PrimaryCommand.ExecuteAsync();
        Assert.True(dialog.Completed);
        Assert.Equal("Cleared by the duty doctor", rig.Recorder.LastArg<RecordDonationRequest>("RecordDonationAsync").OverrideReason);
    }

    [Fact]
    public async Task RecordDonation_ForAnEligibleDonor_SendsNoReason_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor()));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.Eligible()));
        rig.Fake.On("RecordDonationAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91)));
        var dialog = new RecordDonationDialogViewModel(rig.Api, Samples2.Appointment(), rig.Clock, TimeZoneInfo.Utc);
        await dialog.PendingCheck;

        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.Null(rig.Recorder.LastArg<RecordDonationRequest>("RecordDonationAsync").OverrideReason);
    }
}
