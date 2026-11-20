using Lifestream.Client.Api;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The 2.1 client against a 2.0 server. The release order is server first, then the client, but a failed or rolled-back deploy can leave the 2.0
/// server up while a 2.1 client runs. That server does not have the six read-only endpoints 2.1 added, and sends the 2.0 shape of everything
/// else (no age bands, no donation count, no first-time mark). What it answers was measured against a real 2.0.1 API: 404 "not found" to
/// stock/bags, donors/{id}/donations and requests/{id}/bags, and 400 "validation" to receipts/counts, appointments/views and requests/views,
/// which fall into its receipts/{id}, appointments/{id} and requests/{id} routes ("counts" and "views" are not ids). The fake API answers 404
/// to every call a test did not set up; the three 400s are set up here. Each screen must stay usable: the list loads, and only the part that
/// needs a new endpoint says it could not be had. Nothing throws.
/// </summary>
public sealed class OlderServerTests
{
    /// <summary>What a 2.0 server answers to a new route that its own {id} route catches.</summary>
    private static ApiResult<T> NotAnId<T>() => FakeApiClient.Fail<T>(400, ProblemCodes.Validation);

    [Fact]
    public async Task TheFake_AnswersAnEndpointItDoesNotHave_LikeA20Server_404NotFound()
    {
        var rig = new FeatureRig();
        var answers = new ApiProblem?[]
        {
            (await rig.Fake.GetDonorDonationsAsync(7)).Problem,
            (await rig.Fake.GetStockBagsAsync(BloodType.OPos)).Problem,
            (await rig.Fake.GetFulfilPreviewAsync(7)).Problem,
        };
        Assert.All(answers, problem =>
        {
            Assert.NotNull(problem);
            Assert.Equal(404, problem.Status);
            Assert.Equal(ProblemCodes.NotFound, problem.Code);
        });
        Assert.Equal(400, NotAnId<ReceiptCounts>().Problem!.Status);
    }

    [Fact]
    public async Task Appointments_TheListLoads_AndTheViewsShowNoCount()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(1, 41), Samples2.AppointmentRow(2, 42))));
        rig.Fake.On("GetAppointmentViewCountsAsync", NotAnId<AppointmentViewCounts>());
        var vm = rig.Appointments();
        await vm.LoadAsync();

        Assert.True(vm.IsReady);
        Assert.False(vm.HasError);
        Assert.Equal(2, vm.Items.Count);
        Assert.False(vm.Items[0].DonorFirstTime); // the 2.0 list has no first-time mark: nobody is marked, nobody is asked about
        Assert.Null(vm.ViewCounts);
        Assert.Equal(1, rig.Recorder.CountOf("GetAppointmentViewCountsAsync")); // asked once, answered 404, not asked again
    }

    [Fact]
    public async Task Requests_TheListLoads_AndTheViewsShowNoCount()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3)], 1, 1, 50)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        rig.Fake.On("GetRequestViewCountsAsync", NotAnId<RequestViewCounts>());
        var vm = rig.Requests();
        await vm.LoadAsync();

        Assert.True(vm.IsReady);
        Assert.Single(vm.Items);
        Assert.Null(vm.ViewCounts);
        Assert.Equal(1, rig.Recorder.CountOf("GetRequestViewCountsAsync"));
    }

    [Fact]
    public async Task Requests_TheFulfilStepOpens_SaysTheBagsCouldNotBeListed_AndCanBeLeft()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3)], 1, 1, 50)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        var run = vm.FulfilCommand.ExecuteAsync();
        var step = Assert.IsType<FulfilRequestDialogViewModel>(vm.Fulfilment);
        await step.PendingBags;

        Assert.Empty(step.Bags);
        Assert.StartsWith("The bags couldn't be listed.", step.BagsNote, StringComparison.Ordinal);
        await step.CancelCommand.ExecuteAsync();
        await run;
        Assert.Equal(0, rig.Recorder.CountOf("FulfilRequestAsync"));
    }

    [Fact]
    public async Task Receipts_TheListLoads_AndTheTabsShowNoNumber()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90)], 1, 1, 50)));
        rig.Fake.On("GetReceiptCountsAsync", NotAnId<ReceiptCounts>());
        var vm = rig.Receipts();
        await vm.LoadAsync();

        Assert.True(vm.IsReady);
        Assert.Single(vm.Items);
        Assert.Equal((string.Empty, string.Empty, string.Empty), (vm.AllCountText, vm.DonationCountText, vm.IssueCountText));
    }

    [Fact]
    public async Task Donors_TheListAndThePanelLoad_AndTheHistorySaysItCouldNotBeLoaded()
    {
        var rig = new FeatureRig();
        // The 2.0 shape of a row: no Donations count (it reads as 0).
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([new DonorListItem(7, "Ines Halvorsen", new DateOnly(1980, 1, 1), Gender.Female, BloodType.OPos, null, DonorFrequency.Regular, null)], 1, 1, 50)));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(new DonorDetail(7, "Ines Halvorsen", new DateOnly(1980, 1, 1), Gender.Female, BloodType.OPos, null, null, DonorFrequency.Regular, null, "rv")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        var vm = rig.Donors();
        await vm.LoadAsync();
        Assert.True(vm.IsReady);
        Assert.Equal(0, vm.Items[0].Donations);

        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.True(vm.HasDetail);
        Assert.Empty(vm.HistoryRows);
        Assert.Contains("couldn't be loaded", vm.HistoryNote, StringComparison.Ordinal);
        Assert.Contains(vm.PanelFacts, f => f.Label == "Donations" && f.Value == "Could not be loaded right now");
    }

    [Fact]
    public async Task Stock_TheGroupsLoadWithoutAgeBands_AndTheBagListIsAnErrorWithRetry_NotAnEmptyList()
    {
        var rig = new FeatureRig();
        var stock = Samples.Stock();
        Assert.All(stock, item => Assert.Null(item.AgeBands)); // the 2.0 shape
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(stock));
        var vm = new StockViewModel(rig.Api, rig.States) { Zone = TimeZoneInfo.Utc };
        vm.Attach(null, rig.Clock);
        await vm.LoadAsync();

        Assert.True(vm.IsReady);
        Assert.Equal(stock.Count, vm.Tiles.Count);
        Assert.Equal(40, vm.Tiles.Single(t => t.BloodType == BloodType.APos).Available);

        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.APos);
        await vm.PendingBags;

        Assert.True(vm.HasBagsError);
        Assert.False(vm.HasNoBags); // never "no bags" when the list could not be had
        Assert.Empty(vm.Bags);
        Assert.Contains("retry", vm.BagsErrorText, StringComparison.Ordinal);
    }
}
