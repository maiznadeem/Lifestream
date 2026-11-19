using Lifestream.Client.Api;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Receipts;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>Appointments: booking, rescheduling, recording a donation, filtering and the list (FEAT-09 to FEAT-14, FEAT-16), against the fake API client.</summary>
public sealed class Part2AppointmentTests
{
    private static readonly DateOnly Day = new(2027, 3, 10);

    private static Part2Rig RigWithSlots()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetSlotsAsync", _ => Task.FromResult(ApiResult<IReadOnlyList<SlotInfo>>.Ok(Samples2.DaySlots(rig.Recorder.LastArg<DateOnly>("GetSlotsAsync")))));
        return rig;
    }

    private static async Task<AppointmentEditorViewModel> BookingWithDonorAsync(Part2Rig rig, DonorDetail? donor = null)
    {
        donor ??= Samples2.RestedDonor();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(donor));
        var form = rig.BookingForm();
        form.DonorOption = new RecordOption(donor.Id, donor.Name, null);
        await form.PendingDonor;
        return form;
    }

    // FEAT-11

    [Fact]
    public async Task BookAppointment_PastDatesBlocked_TakenSlotsDisabled_FakeApi()
    {
        var rig = RigWithSlots();
        var form = await BookingWithDonorAsync(rig);

        Assert.Equal(rig.Today, form.EarliestDate); // the date field's minimum

        form.Date = rig.Today.AddDays(-1);
        await form.PendingSlots;
        Assert.False(form.ValidateAll());
        Assert.Contains("past", form[nameof(AppointmentEditorViewModel.Date)]!, StringComparison.OrdinalIgnoreCase);

        form.Date = Day;
        await form.PendingSlots;
        Assert.Equal(6, form.Slots.Count);
        var taken = form.Slots.Single(s => s.IsTaken);
        Assert.False(taken.IsAvailable);
        Assert.Contains("(taken)", taken.Label, StringComparison.Ordinal);

        form.SelectedSlot = taken; // a taken slot can't be chosen
        Assert.Null(form.SelectedTime);

        form.TimeText = "09:30"; // nor can it be typed
        form.ValidateOnBlur(AppointmentEditorViewModel.Time);
        Assert.Contains("taken", form.GetErrorFor(AppointmentEditorViewModel.Time)!, StringComparison.OrdinalIgnoreCase);

        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("BookAppointmentAsync")); // a taken time is not a time: nothing is sent

        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        form.SelectedSlot = form.Slots.Single(s => s.Display == "10:30");
        Assert.Equal(new TimeOnly(10, 30), form.SelectedTime);
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(1, rig.Recorder.CountOf("BookAppointmentAsync")); // sent once the form is valid
    }

    [Fact]
    public async Task BookAppointment_ValidForm_SendsTheChosenSlotInstant_FakeApi()
    {
        var rig = RigWithSlots();
        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = await BookingWithDonorAsync(rig);
        form.Date = Day;
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.Single(s => s.Display == "10:30");

        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<BookAppointmentRequest>("BookAppointmentAsync");
        Assert.Equal(43, sent.DonorId);
        Assert.Equal(new DateTime(2027, 3, 10, 10, 30, 0, DateTimeKind.Utc), sent.SlotStartUtc);
        Assert.Null(sent.OverrideReason); // an eligible donor needs none
    }

    [Fact]
    public async Task SlotPicker_ShowsLocalTimes_FakeApi()
    {
        var rig = RigWithSlots();
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5");
        var form = rig.BookingForm(zone: plus5);
        form.Date = Day;
        await form.PendingSlots;

        Assert.Equal(["14:00", "14:30", "15:00", "15:30", "17:00", "18:00"], form.Slots.Select(s => s.Display));
        form.SelectedSlot = form.Slots.Single(s => s.Display == "15:30"); // 10:30 UTC
        Assert.Equal(new DateTime(2027, 3, 10, 10, 30, 0, DateTimeKind.Utc), form.Slots.Single(s => s.Local == form.SelectedTime).StartUtc);
    }

    // FEAT-12

    [Theory]
    [InlineData("12:00 AM", 0, 0)]
    [InlineData("12:00 PM", 12, 0)]
    [InlineData("1:00 PM", 13, 0)]
    [InlineData("1:00 pm", 13, 0)]
    [InlineData("11:59 PM", 23, 59)]
    [InlineData("12:30 AM", 0, 30)]
    [InlineData("13:00", 13, 0)]
    [InlineData("09:30", 9, 30)]
    public void SlotPicker_Parses1200AMand1200PMCorrectly(string text, int hour, int minute)
    {
        Assert.True(SlotTime.TryParse(text, out var time));
        Assert.Equal(new TimeOnly(hour, minute), time);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("13:00 PM")]
    [InlineData("0:00 PM")]
    [InlineData("25:00")]
    [InlineData("9.30")]
    [InlineData("noon")]
    public void SlotTime_RefusesAnythingElse(string? text) => Assert.False(SlotTime.TryParse(text, out _));

    [Fact]
    public async Task SlotPicker_TypingATime_ChoosesThatSlot_NoInputCombinationCrashes_FakeApi()
    {
        var rig = RigWithSlots();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor()));
        var form = rig.BookingForm();
        form.Date = Day;
        await form.PendingSlots;

        form.TimeText = "1:00 PM";
        Assert.Equal(new TimeOnly(13, 0), form.SelectedTime);
        Assert.Equal("13:00", form.SelectedSlot?.Display);
        form.TimeText = "12:00 PM";
        Assert.Equal("12:00", form.SelectedSlot?.Display);
        form.TimeText = "12:00 AM"; // parses to 00:00, which the clinic doesn't offer
        Assert.Equal(new TimeOnly(0, 0), form.SelectedTime);
        Assert.Null(form.SelectedSlot);
        Assert.False(form.ValidateAll());
        Assert.Contains("no slot", form.GetErrorFor(AppointmentEditorViewModel.Time)!, StringComparison.OrdinalIgnoreCase);

        foreach (var text in new[] { "", "  ", "abc", "99:99", "13:00 PM", "\0", new string('9', 500), "12:00 PM 12:00 PM" })
        {
            form.TimeText = text;
            form.ValidateAll(); // never throws
            await form.SaveCommand.ExecuteAsync(); // never throws
        }

        Assert.Equal(0, rig.Recorder.CountOf("BookAppointmentAsync"));
    }

    [Fact]
    public async Task Booking_NothingChosen_ShowsMessagesUnderTheFields_AndSendsNothing_FakeApi()
    {
        var rig = RigWithSlots();
        var form = rig.BookingForm();

        await form.SaveCommand.ExecuteAsync(); // no donor, no date, no time

        Assert.False(form.HasUnsavedChanges);
        Assert.Equal("Choose a donor.", form.GetErrorFor(nameof(AppointmentEditorViewModel.DonorId)));
        Assert.Equal("Choose a date.", form.GetErrorFor(nameof(AppointmentEditorViewModel.Date)));
        Assert.Equal("Choose a time.", form.GetErrorFor(AppointmentEditorViewModel.Time));
        Assert.Equal(0, rig.Recorder.CountOf("BookAppointmentAsync"));
        Assert.False(form.IsBusy);
    }

    [Fact]
    public async Task Booking_ClosedDayAndFailedSlotLoad_AreExplainedInWords_FakeApi()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetSlotsAsync", ApiResult<IReadOnlyList<SlotInfo>>.Ok([]));
        var form = rig.BookingForm();
        form.Date = Day;
        await form.PendingSlots;
        Assert.Equal("The clinic has no times on that day.", form.SlotsMessage);

        rig.Fake.On("GetSlotsAsync", FakeApiClient.Unreachable<IReadOnlyList<SlotInfo>>());
        form.Date = Day.AddDays(1);
        await form.PendingSlots;
        Assert.Empty(form.Slots);
        Assert.Contains("Couldn't load", form.SlotsMessage, StringComparison.Ordinal);
    }

    // FEAT-09

    [Fact]
    public async Task Booking_IneligibleDonor_ShowsBanner_AndNeedsAReason_FakeApi()
    {
        var rig = RigWithSlots();
        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = await BookingWithDonorAsync(rig, Samples2.RecentDonor());
        form.Date = rig.Today.AddDays(10);
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.First(s => s.IsAvailable);

        Assert.Equal(BannerState.NotEligible, form.Banner.State);
        Assert.True(form.Banner.RequiresReason);
        Assert.Contains(form.Banner.Lines, l => l.StartsWith("Age: ", StringComparison.Ordinal));
        Assert.Contains(form.Banner.Lines, l => l.StartsWith("Last donation: ", StringComparison.Ordinal) && l.Contains("days ago", StringComparison.Ordinal));
        Assert.Contains(form.Banner.Lines, l => l.Contains("90 days", StringComparison.Ordinal));

        await form.SaveCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("BookAppointmentAsync")); // blocked until a reason is entered
        Assert.NotNull(form.GetErrorFor(nameof(AppointmentEditorViewModel.OverrideReason)));

        form.OverrideReason = "Urgent: rare blood type, cleared by the duty doctor";
        await form.SaveCommand.ExecuteAsync();
        Assert.Equal("Urgent: rare blood type, cleared by the duty doctor", rig.Recorder.LastArg<BookAppointmentRequest>("BookAppointmentAsync").OverrideReason);
    }

    [Fact]
    public async Task Booking_EligibleDonor_ShowsEligibleBanner_NoReasonAsked_FakeApi()
    {
        var rig = RigWithSlots();
        var form = await BookingWithDonorAsync(rig, Samples2.RestedDonor());

        Assert.Equal(BannerState.Eligible, form.Banner.State);
        Assert.False(form.Banner.RequiresReason);
        Assert.Equal("Eligible to donate", form.Banner.Headline);
    }

    [Fact]
    public async Task RecordDonation_FromAppointment_PrefillsDonor_ShowsEligibilityBanner_OverrideNeedsReason()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RecentDonor()));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.NotEligible()));
        rig.Fake.On("RecordDonationAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91)));
        var dialog = new RecordDonationDialogViewModel(rig.Api, Samples2.Appointment(), rig.Clock, TimeZoneInfo.Utc);
        await dialog.PendingCheck;

        Assert.Equal("Ayşe Nur Öztürk-Abdulrahman", dialog.DonorName); // from the appointment, not typed
        Assert.Equal("O+", dialog.BloodTypeText);
        Assert.Equal(BannerState.NotEligible, dialog.Banner.State);
        Assert.Contains(dialog.Banner.Lines, l => l.StartsWith("Age: 35", StringComparison.Ordinal));
        Assert.Contains(dialog.Banner.Lines, l => l.Contains("days ago", StringComparison.Ordinal));

        await dialog.PrimaryCommand.ExecuteAsync(); // no reason yet
        Assert.False(dialog.Completed);
        Assert.Equal(0, rig.Recorder.CountOf("RecordDonationAsync"));
        Assert.Contains("isn't eligible", dialog.GetError(nameof(RecordDonationDialogViewModel.OverrideReason))!, StringComparison.Ordinal);

        dialog.OverrideReason = "Cleared by the duty doctor";
        dialog.Notes = "Gave 450 ml";
        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.True(dialog.Completed);
        Assert.Equal(91, dialog.Receipt!.Id);
        var sent = rig.Recorder.LastArg<RecordDonationRequest>("RecordDonationAsync");
        Assert.Equal(1, sent.Units);
        Assert.Equal("Eligibility override: Cleared by the duty doctor\nGave 450 ml", sent.Notes);
    }

    [Fact]
    public async Task RecordDonation_EligibleDonor_NeedsNoReason_AndFailureStaysInTheDialog()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor()));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.Eligible()));
        rig.Fake.On("RecordDonationAsync", FakeApiClient.Fail<ReceiptDetail>(409, ProblemCodes.AlreadyReceipted, "This appointment already has a donation receipt."));
        var dialog = new RecordDonationDialogViewModel(rig.Api, Samples2.Appointment(), rig.Clock, TimeZoneInfo.Utc);
        await dialog.PendingCheck;
        Assert.Equal(BannerState.Eligible, dialog.Banner.State);

        await dialog.PrimaryCommand.ExecuteAsync();

        Assert.False(dialog.Completed);
        Assert.Equal("This appointment already has a donation receipt.", dialog.FormError);
        Assert.False(dialog.IsBusy);
        Assert.Null(rig.Recorder.LastArg<RecordDonationRequest>("RecordDonationAsync").Notes);
    }

    // FEAT-13

    [Fact]
    public async Task Reschedule_DateOnly_KeepsTime()
    {
        var rig = RigWithSlots();
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.RescheduleForm(Samples2.Appointment()); // 10:30 on 10 Mar 2027
        await form.PendingSlots;
        Assert.Equal(new TimeOnly(10, 30), form.SelectedTime);
        Assert.False(form.HasUnsavedChanges);

        form.Date = Day.AddDays(3); // only the date
        await form.PendingSlots;
        Assert.Equal(new TimeOnly(10, 30), form.SelectedTime); // the free 10:30 slot of the new day is kept
        await form.SaveCommand.ExecuteAsync();

        var sent = rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync");
        Assert.Equal(new DateTime(2027, 3, 13, 10, 30, 0, DateTimeKind.Utc), sent.SlotStartUtc); // date and time, time unchanged
        Assert.Equal("rv-a1", sent.RowVersion);
    }

    [Fact]
    public async Task Reschedule_DateOnly_KeepsLocalTimeOfDay_InAnotherZone()
    {
        var rig = RigWithSlots();
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5");
        var form = rig.RescheduleForm(Samples2.Appointment(), plus5); // 15:30 local
        await form.PendingSlots;
        Assert.Equal(new TimeOnly(15, 30), form.SelectedTime);

        form.Date = Day.AddDays(1);
        await form.PendingSlots;
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(new DateTime(2027, 3, 11, 10, 30, 0, DateTimeKind.Utc), rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync").SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_WhenTheTimesCouldNotBeLoaded_StillSendsDateAndTheExistingTime()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetSlotsAsync", FakeApiClient.Unreachable<IReadOnlyList<SlotInfo>>());
        rig.Fake.On("RescheduleAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment()));
        var form = rig.RescheduleForm(Samples2.Appointment());
        form.Date = Day.AddDays(5);
        await form.PendingSlots;

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(new DateTime(2027, 3, 15, 10, 30, 0, DateTimeKind.Utc), rig.Recorder.LastArg<RescheduleAppointmentRequest>("RescheduleAppointmentAsync").SlotStartUtc);
    }

    [Fact]
    public async Task Reschedule_ToATakenTime_IsExplained_AndNothingIsSent()
    {
        var rig = RigWithSlots();
        var form = rig.RescheduleForm(Samples2.Appointment());
        await form.PendingSlots;

        form.Date = Day.AddDays(1);
        await form.PendingSlots;
        form.TimeText = "9:30 AM"; // taken on every day of the fake clinic
        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(0, rig.Recorder.CountOf("RescheduleAppointmentAsync"));
        Assert.Contains("taken", form.GetErrorFor(AppointmentEditorViewModel.Time)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reschedule_NothingChanged_SendsNothing_AndClosesAsSaved()
    {
        var rig = RigWithSlots();
        var form = rig.RescheduleForm(Samples2.Appointment());
        await form.PendingSlots;
        EditorClosedEventArgs? closed = null;
        form.Closed += (_, e) => closed = e;

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal(0, rig.Recorder.CountOf("RescheduleAppointmentAsync"));
        Assert.True(closed?.Saved);
    }

    [Fact]
    public async Task Booking_SlotJustTaken_ShowsMessageUnderTime_AndRefreshesTheSlots()
    {
        var rig = RigWithSlots();
        rig.Fake.On("BookAppointmentAsync", FakeApiClient.Fail<AppointmentDetail>(409, ProblemCodes.SlotTaken, "That slot is already taken."));
        var form = await BookingWithDonorAsync(rig);
        form.Date = Day;
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.Single(s => s.Display == "10:30");

        await form.SaveCommand.ExecuteAsync();
        await form.PendingSlots;

        Assert.Equal("Someone else just took that time. Choose another.", form.GetErrorFor(AppointmentEditorViewModel.Time));
        Assert.Equal(2, rig.Recorder.CountOf("GetSlotsAsync")); // the list was refreshed
        Assert.False(form.IsBusy);
    }

    [Fact]
    public async Task Booking_ServerFieldErrors_ShowUnderTheirFields_ThroughErrorsFor()
    {
        var rig = RigWithSlots();
        rig.Fake.On("BookAppointmentAsync", ApiResult<AppointmentDetail>.Fail(new ApiProblem(400, ProblemCodes.Validation, "Validation failed", "c", null, new Dictionary<string, string[]> { ["slotStartUtc"] = ["must be in the future"] })));
        var form = await BookingWithDonorAsync(rig);
        form.Date = Day;
        await form.PendingSlots;
        form.SelectedSlot = form.Slots.Single(s => s.Display == "10:30");

        await form.SaveCommand.ExecuteAsync();

        Assert.Equal("must be in the future", form.GetErrorFor(AppointmentEditorViewModel.Time));
        Assert.False(form.HasFormError);
    }

    [Fact]
    public async Task Booking_Cancel_WithEntries_AsksDiscardChanges()
    {
        var rig = RigWithSlots();
        var form = rig.BookingForm();
        form.Date = Day;
        await form.PendingSlots;
        rig.Dialogs.Answer = false;

        await form.CancelCommand.ExecuteAsync();

        Assert.Single(rig.Dialogs.Confirmations);
        Assert.Equal("Discard changes?", rig.Dialogs.Confirmations[0].Title);
    }

    // The list: FEAT-10, FEAT-14

    [Fact]
    public async Task AppointmentsList_FilterByDonor_UsesDonorId()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", _ =>
        {
            var query = rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync");
            return Task.FromResult(ApiResult<PagedResult<AppointmentListItem>>.Ok(query.DonorId is { } donor
                ? Samples2.AppointmentPage(Samples2.AppointmentRow(5, donor, "Omar Haddad"), Samples2.AppointmentRow(9, donor, "Omar Haddad"))
                : Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, "Omar Haddad"), Samples2.AppointmentRow(6, 7, "Priya Sharma"), Samples2.AppointmentRow(9, 42, "Omar Haddad"))));
        });
        var vm = rig.Appointments();
        await vm.LoadAsync();
        Assert.Null(rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").DonorId); // not filtered yet

        var row = vm.Items.First(i => i.Id == 5); // appointment 5 belongs to donor 42: the two ids differ
        await vm.FilterByDonorCommand.ExecuteAsync(row);
        await vm.PendingRefresh;

        var query = rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync");
        Assert.Equal(42, query.DonorId);
        Assert.NotEqual(row.Id, query.DonorId);
        Assert.Equal([5, 9], vm.Items.Select(i => i.Id));
        Assert.All(vm.Items, i => Assert.Equal(42, i.DonorId));
        Assert.True(vm.HasActiveFilters);
        Assert.Equal(2, rig.Recorder.CountOf("GetAppointmentsAsync")); // one call per filter change
    }

    [Fact]
    public async Task AppointmentsList_DonorPickerSelection_SendsThePickedDonorsId()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42))));
        var vm = rig.Appointments();
        await vm.LoadAsync();

        vm.DonorFilter = new RecordOption(42, "Omar Haddad", "O+");
        await vm.PendingRefresh;

        Assert.Equal(42, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").DonorId);
        await vm.ClearFiltersCommand.ExecuteAsync();
        Assert.Null(rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").DonorId);
        Assert.Null(vm.DonorFilter);
    }

    [Fact]
    public async Task AppointmentsList_RowShowsNameWithIdAsSecondaryText_AndOpensTheReferencedDonor()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, "Omar Haddad"))));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        var row = vm.Items.Single();

        Assert.Equal("Omar Haddad", row.DonorName);
        Assert.Equal("#42", RecordFormatting.IdText(row.DonorId));

        await vm.OpenDonorCommand.ExecuteAsync(row);

        var visit = Assert.Single(rig.Navigation.Visits);
        Assert.Equal(typeof(DonorsViewModel), visit.ViewModel);
        Assert.Equal("Omar Haddad", ((ListPrefilter)visit.Parameter!).Search);
    }

    [Fact]
    public void AppointmentTimes_AreShownOnlyWhenPresent_InLocalTime()
    {
        var plus5 = TimeZoneInfo.CreateCustomTimeZone("plus5", TimeSpan.FromHours(5), "plus5", "plus5");
        Assert.Equal("10 Mar 2027 15:30", RecordFormatting.When(Samples2.SlotUtc, plus5));
        Assert.Equal("10 Mar 2027", RecordFormatting.When(new DateTime(2027, 3, 9, 19, 0, 0, DateTimeKind.Utc), plus5)); // local midnight: a date, no made-up 00:00
        Assert.Equal(string.Empty, RecordFormatting.When(null));
    }

    [Fact]
    public async Task AppointmentsList_StatusPrefilterFromTheDashboard_IsSent()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage()));
        var vm = rig.Appointments();
        vm.ReceiveParameter(new ListPrefilter(Status: "Pending"));
        await vm.LoadAsync();

        Assert.Equal(AppointmentStatus.Pending, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").Status);
        Assert.Equal(AppointmentStatus.Pending, vm.StatusFilter.Value);
    }

    [Fact]
    public async Task AppointmentsList_RelatedDonor_FiltersToThemAndOpensBooking()
    {
        var rig = RigWithSlots();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage()));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(43)));
        var vm = rig.Appointments();
        ((Lifestream.Client.Common.INavigationParameterReceiver)vm).ReceiveParameter(new RelatedRecord(RecordKind.Donor, 43, "Omar Haddad"));
        await vm.LoadAsync();

        Assert.Equal(43, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").DonorId);
        var editor = Assert.IsType<AppointmentEditorViewModel>(vm.Editor);
        Assert.Equal(43, editor.DonorId); // booking is prefilled with the donor
        Assert.Equal("Omar Haddad", editor.DonorOption!.Title);
    }

    // FEAT-16: actions on the appointment

    [Fact]
    public async Task AppointmentsList_RecordDonation_IsAnActionOnTheAppointment_IssuesReceiptAndRefreshes()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, "Omar Haddad"))));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, 42, "Omar Haddad")));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples2.RestedDonor(42)));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(Samples2.Eligible()));
        rig.Fake.On("RecordDonationAsync", ApiResult<ReceiptDetail>.Ok(Samples2.Receipt(91)));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        Assert.False(vm.RecordDonationCommand.CanExecute(null)); // nothing selected: nothing to record
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        Assert.True(vm.RecordDonationCommand.CanExecute(null));

        rig.Forms.Script = async dialog =>
        {
            var donation = Assert.IsType<RecordDonationDialogViewModel>(dialog);
            await donation.PendingCheck;
            await donation.PrimaryCommand.ExecuteAsync();
        };
        await vm.RecordDonationCommand.ExecuteAsync();

        Assert.Equal(5, rig.Recorder.Calls.Single(c => c.Name == "RecordDonationAsync").Args[0]); // exactly that appointment
        var toast = Assert.Single(rig.Toasts.Toasts);
        Assert.Contains("Omar Haddad", toast.Message, StringComparison.Ordinal);
        Assert.Contains("#91", toast.Message, StringComparison.Ordinal);
        Assert.Equal("View receipt", toast.ActionLabel);
        Assert.Equal(2, rig.Recorder.CountOf("GetAppointmentsAsync")); // the list was refreshed
        await toast.ActionCommand.ExecuteAsync();
        Assert.Equal([(91, false)], rig.Viewer.Opened);
    }

    [Fact]
    public async Task AppointmentsList_ActionsAreOnlyForPendingAppointments()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, status: AppointmentStatus.Completed))));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, status: AppointmentStatus.Completed, receiptId: 90)));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.False(vm.RecordDonationCommand.CanExecute(null));
        Assert.False(vm.CancelAppointmentCommand.CanExecute(null));
        Assert.False(vm.RescheduleCommand.CanExecute(null));
        Assert.True(vm.ViewReceiptCommand.CanExecute(null));
        await vm.ViewReceiptCommand.ExecuteAsync();
        Assert.Equal([(90, true)], rig.Viewer.Opened);
    }

    [Fact]
    public async Task AppointmentsList_Cancel_ConfirmsNamingThePerson_ThenCancels()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42, "Omar Haddad"))));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, 42, "Omar Haddad")));
        var vm = rig.Appointments();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        rig.Dialogs.Answer = false;
        await vm.CancelAppointmentCommand.ExecuteAsync();
        Assert.Equal(0, rig.Recorder.CountOf("CancelAppointmentAsync")); // declined: nothing happens

        rig.Dialogs.Answer = true;
        await vm.CancelAppointmentCommand.ExecuteAsync();

        Assert.Equal(2, rig.Dialogs.Confirmations.Count);
        Assert.Contains("Omar Haddad", rig.Dialogs.Confirmations[1].Body, StringComparison.Ordinal);
        Assert.Equal("Cancel appointment", rig.Dialogs.Confirmations[1].Verb);
        Assert.Equal(1, rig.Recorder.CountOf("CancelAppointmentAsync"));
        Assert.Contains("Omar Haddad", Assert.Single(rig.Toasts.Toasts).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppointmentsList_LoadsExposeIsLoadingAndIsBusy()
    {
        var rig = new Part2Rig();
        var gate = new TaskCompletionSource();
        rig.Fake.On("GetAppointmentsAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage());
        });
        var vm = rig.Appointments();

        var load = vm.LoadAsync();
        Assert.True(vm.IsLoading);
        gate.SetResult();
        await load;

        Assert.False(vm.IsLoading);
        Assert.False(vm.IsBusy);
        Assert.True(vm.IsEmpty); // an empty list shows the empty state, not a blank grid
    }
}

internal static class FormExtensions
{
    /// <summary>The message under a field of a form view model.</summary>
    public static string? GetErrorFor(this Lifestream.Client.Validation.FormViewModelBase form, string field) => form.GetError(field);
}
