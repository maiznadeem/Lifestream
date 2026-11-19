using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The finished direction A list screens (fix-a): the list base's footer and count, the Donors filters and donation history, the Requests views and cover, the Patients'
/// waiting requests, and the Appointments agenda. All against the fake API.
/// </summary>
public sealed class ListScreenFinishTests
{
    // ---- the list base ----

    [Fact]
    public async Task ListBase_OnePage_StillShowsThePager_TheRangeHasAnEnDash_AndTheCountShowsOnlyOnceLoaded()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([Donor(1)], 1, 1, 50)));
        var vm = rig.Donors();
        Assert.Null(vm.HeaderCount); // nothing loaded yet: no made-up 0
        await vm.LoadAsync();

        Assert.Equal("Showing 1–1 of 1", vm.RangeText);
        Assert.Equal("Page 1 of 1", vm.PageCountText); // the same footer on every list, also with one page
        Assert.Equal("1", vm.HeaderCount);

        rig.Fake.On("GetDonorsAsync", FakeApiClient.Unreachable<PagedResult<DonorListItem>>());
        await vm.RefreshAsync();
        Assert.True(vm.HasError);
        Assert.Null(vm.HeaderCount); // no count while the list failed
        Assert.Equal(ViewModelBase.UnreachableMessage, vm.ErrorMessage);
    }

    [Fact]
    public async Task ListBase_ServerFailure_SaysWhatHappenedAndWhatToDo()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", FakeApiClient.Fail<PagedResult<DonorListItem>>(500, "server_error"));
        var vm = rig.Donors();
        await vm.LoadAsync();
        Assert.Equal(ViewModelBase.ServerFailedMessage, vm.ErrorMessage);
        Assert.Contains("Retry", vm.ErrorMessage, StringComparison.Ordinal);
    }

    // ---- Donors ----

    [Fact]
    public async Task Donors_EligibilityAndLastDonatedChips_AreSent_AndClearFiltersResetsThem()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([Donor(1)], 1, 1, 50)));
        var vm = rig.Donors();
        await vm.LoadAsync();

        vm.EligibilityFilter = vm.EligibilityOptions.Single(o => o.Value == false);
        await vm.PendingRefresh;
        vm.LastDonatedFilter = vm.LastDonatedOptions.Single(o => o.Value == DonationRecency.Over12Months);
        await vm.PendingRefresh;

        var sent = rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync");
        Assert.False(sent.Eligible);
        Assert.Equal(DonationRecency.Over12Months, sent.LastDonated);
        Assert.True(vm.HasActiveFilters);

        await vm.ClearFiltersCommand.ExecuteAsync();
        sent = rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync");
        Assert.Null(sent.Eligible);
        Assert.Null(sent.LastDonated);
        Assert.False(vm.HasActiveFilters);
    }

    [Fact]
    public async Task Donors_Panel_ShowsTheDonationsTotal_AndTheHistoryWithWhereEachBagWent()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([Donor(7)], 1, 1, 50)));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(new DonorDetail(7, "Ines Halvorsen", new DateOnly(1980, 1, 1), Gender.Female, BloodType.OPos, null, null, DonorFrequency.Regular, null, "rv")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var today = rig.Today;
        var items = Enumerable.Range(0, 6).Select(i => new DonationHistoryItem(50 - i, 90 - i, new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddDays(-100 * i), 1,
            [i == 0
                ? new DonatedBag(4187, BagStatus.Issued, today.AddDays(30), new DateTime(2025, 1, 9, 9, 0, 0, DateTimeKind.Utc), 12, 3, "Quinn Crowhurst")
                : new DonatedBag(4100 + i, BagStatus.Discarded, today.AddDays(-5), null, null, null, null)])).ToList();
        rig.Fake.On("GetDonorDonationsAsync", ApiResult<DonorDonations>.Ok(new DonorDonations(8, 8, items)));
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.Contains(vm.PanelFacts, f => f.Label == "Donations" && f.Value == "8 donations, 8 units in all");
        Assert.Equal(6, vm.HistoryRows.Count);
        Assert.Equal(DonorsViewModel.HistoryPreviewCount, vm.VisibleHistory.Count);
        Assert.Equal("Show all 6", vm.HistoryMoreText);
        Assert.Equal("B-4187", vm.HistoryRows[0].CodeText);
        Assert.StartsWith("Issued 09 Jan 2025 to Quinn Crowhurst", vm.HistoryRows[0].FateText, StringComparison.Ordinal);
        Assert.Equal(DonationFate.Issued, vm.HistoryRows[0].Fate);
        Assert.Equal("Discarded", vm.HistoryRows[1].FateText);
        Assert.Equal("The latest 6 of 8 donations are listed.", vm.HistoryNote);

        vm.ShowAllHistoryCommand.Execute(null);
        Assert.Equal(6, vm.VisibleHistory.Count);
        Assert.Equal("Show fewer", vm.HistoryMoreText);
    }

    [Fact]
    public async Task Donors_HistoryThatFails_SaysSoInThePanel_AndTheRestStillShows()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([Donor(7)], 1, 1, 50)));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(new DonorDetail(7, "Ines Halvorsen", new DateOnly(1980, 1, 1), Gender.Female, BloodType.OPos, null, null, DonorFrequency.Regular, null, "rv")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        rig.Fake.On("GetDonorDonationsAsync", FakeApiClient.Unreachable<DonorDonations>());
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.True(vm.HasDetail);
        Assert.Empty(vm.HistoryRows);
        Assert.Contains("couldn't be loaded", vm.HistoryNote, StringComparison.Ordinal);
        Assert.Equal(EligibilityCardKind.Eligible, vm.EligibilityCard?.Kind);
    }

    [Theory]
    [InlineData(BagStatus.Available, 10, "In stock, use by")]
    [InlineData(BagStatus.Available, -1, "Expired")]
    [InlineData(BagStatus.Discarded, 10, "Discarded")]
    public void DonationHistory_FateOfABag(BagStatus status, int expiresInDays, string start)
    {
        var today = new DateOnly(2026, 11, 19);
        var fate = DonationHistory.FateOf(new DonatedBag(1, status, today.AddDays(expiresInDays), null, null, null, null), today, TimeZoneInfo.Utc);
        Assert.StartsWith(start, fate, StringComparison.Ordinal);
    }

    // ---- Requests ----

    [Theory]
    [InlineData(3, 4, CoverKind.CanFulfil, "Can fulfil")]
    [InlineData(3, 3, CoverKind.CanFulfil, "Can fulfil")]
    [InlineData(3, 2, CoverKind.Partial, "1 short")]
    [InlineData(3, 0, CoverKind.NoStock, "No stock")]
    public void RequestCoverage_StockAgainstTheUnits(int units, int available, CoverKind kind, string text)
    {
        var cover = RequestCoverage.Of(units, available);
        Assert.Equal(kind, cover.Kind);
        Assert.Equal(text, cover.Text);
    }

    [Fact]
    public async Task Requests_OpenViewByDefault_WithTheLocalOffset_CoverFromStock_NoStatusColumn()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(1, 3, type: BloodType.ANeg), Samples2.RequestRow(2, 4, type: BloodType.BPos)], 2, 1, 50)));
        rig.Fake.On("GetRequestViewCountsAsync", ApiResult<RequestViewCounts>.Ok(new RequestViewCounts(2, 1, 5, 0)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 5, 0, 10, true)]));
        var vm = rig.Requests();
        await vm.LoadAsync();

        var sent = rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync");
        Assert.Equal(RequestView.Open, sent.View);
        Assert.Equal((int)rig.Clock.GetLocalNow().Offset.TotalMinutes, sent.UtcOffsetMinutes);
        Assert.True(vm.IsOpenView);
        Assert.False(vm.ShowStatusColumn);
        Assert.True(vm.ShowOpenColumns);
        Assert.Equal(new RequestViewCounts(2, 1, 5, 0), vm.ViewCounts);
        Assert.Equal(CoverKind.CanFulfil, vm.CoverOf(vm.Items[0])!.Kind); // 5 A− for 3
        Assert.Equal(CoverKind.NoStock, vm.CoverOf(vm.Items[1])!.Kind); // no B+ in stock

        vm.IsFulfilledView = true;
        await vm.PendingRefresh;
        Assert.Equal(RequestView.Fulfilled, rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").View);
        Assert.True(vm.ShowStatusColumn);
        Assert.False(vm.ShowOpenColumns);
        Assert.Equal("No fulfilled requests yet", vm.EmptyTitle);
    }

    [Fact]
    public async Task Requests_StockThatFails_LeavesTheListUsable_WithNoCover()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(1, 3)], 1, 1, 50)));
        rig.Fake.On("GetStockAsync", FakeApiClient.Unreachable<IReadOnlyList<StockItem>>());
        var vm = rig.Requests();
        await vm.LoadAsync();
        Assert.True(vm.IsReady);
        Assert.Null(vm.CoverOf(vm.Items[0]));
        Assert.Null(vm.ViewCounts);
    }

    [Fact]
    public async Task Requests_ALinkToOneRequest_ShowsEveryView_AndFulfilFromAnotherScreen_OpensTheStep()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3, "Omar Haddad")], 1, 1, 50)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 8, 0, 10, true)]));
        rig.Fake.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(new FulfilPreview(3, 8,
        [
            new SuggestedBag(11, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 12), 5, "Ana Lima"),
            new SuggestedBag(12, new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 13), null, null),
            new SuggestedBag(13, new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 14), 6, "Bo Chen"),
        ])));
        var vm = rig.Requests();
        ((INavigationParameterReceiver)vm).ReceiveParameter(new RelatedRecord(Client.Api.RecordKind.Request, 7, "Omar Haddad"));
        await vm.LoadAsync();
        await vm.PendingDetail;
        for (var i = 0; i < 20 && vm.Fulfilment is null; i++)
        {
            await Task.Yield();
        }

        Assert.Null(vm.View); // every view: the linked request is in the list whatever its status
        Assert.Null(rig.Recorder.ArgsOf<RequestQuery>("GetRequestsAsync").First().View);
        var step = Assert.IsType<FulfilRequestDialogViewModel>(vm.Fulfilment);
        await step.PendingBags;
        Assert.Equal(["B-0011", "B-0012", "B-0013"], step.Bags.Select(b => b.Code));
        Assert.Equal(1, step.Bags[0].Order);
        Assert.Contains("from Ana Lima", step.Bags[0].Origin, StringComparison.Ordinal);
        Assert.Equal("These bags are issued, oldest first:", step.BagsNote);
        Assert.Equal("Not now", step.CancelText);
        await step.CancelCommand.ExecuteAsync();
    }

    [Fact]
    public async Task Requests_ShortStock_TheStepListsWhatCanBeIssued_AndSaysHowManyAreShort()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3)], 1, 1, 50)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(new FulfilPreview(3, 1, [new SuggestedBag(11, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 12), null, null)])));
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        var run = vm.FulfilCommand.ExecuteAsync();
        var step = Assert.IsType<FulfilRequestDialogViewModel>(vm.Fulfilment);
        await step.PendingBags;
        Assert.Single(step.Bags);
        Assert.Equal("Only 1 unit can be issued; 2 units short.", step.BagsNote);
        await step.CancelCommand.ExecuteAsync();
        await run;
        Assert.Equal(0, rig.Recorder.CountOf("FulfilRequestAsync")); // "Not now" issues nothing
    }

    // ---- Patients ----

    [Fact]
    public async Task Patients_Panel_ListsEachWaitingRequestWithCover_CanReceive_AndFulfilOpensTheRequest()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Ok(new([new PatientListItem(53, "Noor Fairbanks", new DateOnly(1971, 6, 4), Gender.Female, BloodType.ONeg, null, 2)], 1, 1, 50)));
        rig.Fake.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(new PatientDetail(53, "Noor Fairbanks", new DateOnly(1971, 6, 4), Gender.Female, BloodType.ONeg, null, null, "rv")));
        var placed = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new(
            [new(139, 53, "Noor Fairbanks", BloodType.ONeg, 1, placed, rig.Today.AddDays(3), RequestStatus.Pending), new(152, 53, "Noor Fairbanks", BloodType.ONeg, 2, placed, rig.Today.AddDays(11), RequestStatus.Pending)], 2, 1, 10)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ONeg, 1, 0, 10, true)]));
        var vm = rig.Patients();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        var asked = rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync");
        Assert.Equal(53, asked.PatientId);
        Assert.Equal(RequestView.Open, asked.View);
        Assert.Equal(2, vm.WaitingRequests.Count);
        Assert.Equal("1 unit of O−", vm.WaitingRequests[0].Title);
        Assert.Equal("R-0139", vm.WaitingRequests[0].Code);
        Assert.Equal(CoverKind.CanFulfil, vm.WaitingRequests[0].Cover!.Kind);
        Assert.Equal(CoverKind.Partial, vm.WaitingRequests[1].Cover!.Kind);
        Assert.Equal(RequestUrgencyKind.Soon, vm.WaitingRequests[0].Urgency.Kind);
        Assert.Equal("2 requests waiting for blood", vm.OpenRequestsText);
        Assert.Contains(vm.PanelFacts, f => f.Label == "Can receive" && f.Value == "O−");

        await vm.FulfilRequestCommand.ExecuteAsync(vm.WaitingRequests[1]);
        var visit = Assert.Single(rig.Navigation.Visits);
        Assert.Equal(typeof(RequestsViewModel), visit.ViewModel);
        Assert.Equal(new RelatedRecord(Client.Api.RecordKind.Request, 152, "Noor Fairbanks"), visit.Parameter);
    }

    // ---- Appointments ----

    [Fact]
    public async Task Appointments_TodayByDefault_TheAgendaMarksNextAndWaiting_AndTabsSendTheirView()
    {
        var rig = new Part2Rig();
        var done = Samples2.AppointmentRow(1, 41, status: AppointmentStatus.Completed);
        var next = Samples2.AppointmentRow(2, 42);
        var later = Samples2.AppointmentRow(3, 43);
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(done, next, later)));
        rig.Fake.On("GetAppointmentViewCountsAsync", ApiResult<AppointmentViewCounts>.Ok(new AppointmentViewCounts(3, 9, 20, 1)));
        var vm = rig.Appointments();
        await vm.LoadAsync();

        Assert.Equal(AppointmentView.Today, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").View);
        Assert.True(vm.IsAgenda);
        Assert.Equal(new AppointmentViewCounts(3, 9, 20, 1), vm.ViewCounts);
        Assert.Equal(AgendaState.Done, vm.AgendaOf(done));
        Assert.Equal(AgendaState.Next, vm.AgendaOf(next));
        Assert.Equal(AgendaState.Waiting, vm.AgendaOf(later));
        Assert.Equal("No appointments today", vm.EmptyTitle); // the empty state names the view

        vm.IsPastView = true;
        await vm.PendingRefresh;
        Assert.Equal(AppointmentView.Past, rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").View);
        Assert.False(vm.IsAgenda);
        Assert.Equal(AgendaState.None, vm.AgendaOf(next));
    }

    [Fact]
    public async Task Appointments_CheckIn_OpensTheAppointment_RecordDonationForARow_OpensItsDialog()
    {
        var rig = new Part2Rig();
        var row = Samples2.AppointmentRow(5, 42);
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(row)));
        rig.Fake.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(Samples2.Appointment(5, 42)));
        var vm = rig.Appointments();
        await vm.LoadAsync();

        await vm.CheckInCommand.ExecuteAsync(vm.Items[0]);
        Assert.Same(vm.Items[0], vm.SelectedItem);
        Assert.Equal(5, vm.Detail?.Id);
        Assert.Empty(rig.Forms.Shown); // checking in opens the panel, no dialog

        await vm.RecordDonationForCommand.ExecuteAsync(vm.Items[0]);
        Assert.IsType<RecordDonationDialogViewModel>(Assert.Single(rig.Forms.Shown));
    }

    [Fact]
    public async Task Appointments_ALinkWithAStatus_ShowsEveryDay_WithTheStatusChipToClear()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42))));
        var vm = rig.Appointments();
        vm.ReceiveParameter(new ListPrefilter(Status: "Pending"));
        await vm.LoadAsync();
        var sent = rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync");
        Assert.Null(sent.View);
        Assert.Equal(AppointmentStatus.Pending, sent.Status);
        Assert.True(vm.HasStatusFilter);
        Assert.False(vm.IsTodayView);
    }

    private static DonorListItem Donor(int id) => new(id, $"Donor {id}", new DateOnly(1980, 1, 1), Gender.Female, BloodType.OPos, null, DonorFrequency.Regular, null);
}
