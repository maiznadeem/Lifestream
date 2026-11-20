using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>List screens (FEAT-01, FEAT-02, FEAT-03), driven against the fake API client.</summary>
public sealed class ListScreenTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    // FEAT-02

    [Fact]
    public async Task DonorsList_TypingSearch_Debounced300ms_OneApiCall_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var vm = rig.Donors();
        await vm.LoadAsync();
        Assert.Equal(1, rig.Recorder.CountOf("GetDonorsAsync"));

        vm.SearchText = "a";
        rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
        vm.SearchText = "an";
        rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
        vm.SearchText = "ann";
        rig.Clock.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(1, rig.Recorder.CountOf("GetDonorsAsync")); // still waiting: not yet 300 ms since the last key

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await vm.PendingRefresh;
        Assert.Equal(2, rig.Recorder.CountOf("GetDonorsAsync")); // exactly one call for three keystrokes
        Assert.Equal("ann", rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search);
    }

    [Fact]
    public async Task DonorsList_Enter_AppliesTheSearchAtOnce_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var vm = rig.Donors();
        await vm.LoadAsync();

        vm.SearchText = "ayse";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal("ayse", rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search);

        rig.Clock.Advance(Debounce); // the pending debounce must not fire a second call
        Assert.Equal(2, rig.Recorder.CountOf("GetDonorsAsync"));
    }

    [Fact]
    public async Task DonorsList_PlaceholderNeverSent_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var vm = rig.Donors();
        await vm.LoadAsync();
        Assert.Null(rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search); // the first load sends no text filter

        vm.SearchText = "   ";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Null(rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search); // spaces are not a filter

        vm.SearchText = "ann";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal("ann", rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search);

        vm.SearchText = string.Empty;
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Null(rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Search);
        Assert.DoesNotContain(rig.Recorder.ArgsOf<DonorQuery>("GetDonorsAsync"), q => q.Search is { Length: > 0 } text && text.Contains("...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DonorsList_BloodTypeFilter_AppliesImmediately_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var vm = rig.Donors();
        await vm.LoadAsync();

        vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.ONeg);
        await vm.PendingRefresh;

        Assert.Equal(2, rig.Recorder.CountOf("GetDonorsAsync")); // no debounce wait, one call
        Assert.Equal(BloodType.ONeg, rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").BloodType);
        Assert.True(vm.HasActiveFilters);
    }

    [Fact]
    public async Task DonorsList_ClearFilters_RemovesSearchAndBloodType_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SearchText = "ann";
        vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.ABNeg);
        await vm.PendingRefresh;
        Assert.True(vm.HasActiveFilters);

        await vm.ClearFiltersCommand.ExecuteAsync();

        var query = rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync");
        Assert.Null(query.Search);
        Assert.Null(query.BloodType);
        Assert.Equal(string.Empty, vm.SearchText);
        Assert.True(vm.BloodTypeFilter.IsAll);
        Assert.False(vm.HasActiveFilters);
        rig.Clock.Advance(Debounce);
        Assert.Equal(3, rig.Recorder.CountOf("GetDonorsAsync")); // initial, the filter, the clear: the cancelled debounce added none
    }

    [Fact]
    public async Task DonorsList_SortAndFiltersSurviveLeavingAndComingBack_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(_ => Samples.DonorPage(3));
        var first = rig.Donors();
        await first.LoadAsync();
        first.SearchText = "ann";
        first.BloodTypeFilter = first.BloodTypeOptions.First(o => o.Value == BloodType.APos);
        await first.PendingRefresh;
        await first.SortCommand.ExecuteAsync("-name");
        first.Dispose();

        var again = rig.Donors(); // a new visit: a new view model, the same session state
        await again.LoadAsync();

        var query = rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync");
        Assert.Equal("ann", query.Search);
        Assert.Equal(BloodType.APos, query.BloodType);
        Assert.Equal("-name", query.Sort);
        Assert.Equal("ann", again.SearchText);
    }

    // FEAT-01

    [Fact]
    public async Task DonorsList_ShowsOnePageOf50_WithShowingXToYOfN_AndFetchesFurtherPagesFromTheApi_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(q => Samples.DonorPage(120, q.Page, q.PageSize));
        var vm = rig.Donors();
        await vm.LoadAsync();

        Assert.Equal(50, vm.Items.Count);
        Assert.Equal("Showing 1–50 of 120", vm.RangeText);
        Assert.Equal(50, rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").PageSize);

        await vm.NextPageCommand.ExecuteAsync();
        Assert.Equal(2, rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Page);
        Assert.Equal("Showing 51–100 of 120", vm.RangeText);

        await vm.NextPageCommand.ExecuteAsync();
        Assert.Equal(20, vm.Items.Count);
        Assert.Equal("Showing 101–120 of 120", vm.RangeText);
        Assert.False(vm.HasNextPage);

        await vm.PreviousPageCommand.ExecuteAsync();
        Assert.Equal("Showing 51–100 of 120", vm.RangeText);
        Assert.Equal(4, rig.Recorder.CountOf("GetDonorsAsync")); // one call per page, never the whole list
    }

    [Fact]
    public async Task DonorsList_NewSearch_StartsAtPageOne_FakeApi()
    {
        var rig = new FeatureRig();
        rig.DonorsReturn(q => Samples.DonorPage(120, q.Page, q.PageSize));
        var vm = rig.Donors();
        await vm.LoadAsync();
        await vm.NextPageCommand.ExecuteAsync();
        Assert.Equal(2, vm.Page);

        vm.SearchText = "a";
        await vm.SearchNowCommand.ExecuteAsync();

        Assert.Equal(1, rig.Recorder.LastArg<DonorQuery>("GetDonorsAsync").Page);
    }

    // FEAT-03

    [Fact]
    public async Task DonorsList_ShowsSkeletonThenRows_EmptyState_ErrorWithRetry_FakeApi()
    {
        var rig = new FeatureRig();
        var gate = new TaskCompletionSource();
        rig.Fake.On("GetDonorsAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(3));
        });
        var vm = rig.Donors();

        var load = vm.LoadAsync();
        Assert.True(vm.ShowSkeleton); // set synchronously: the skeleton shows before any data
        Assert.Empty(vm.Items);
        gate.SetResult();
        await load;
        Assert.Equal(LoadState.Ready, vm.State);
        Assert.Equal(3, vm.Items.Count);

        // Empty: nothing stored.
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(0)));
        await vm.LoadAsync();
        Assert.Equal(LoadState.Empty, vm.State);
        Assert.Equal("No donors yet", vm.EmptyTitle);
        Assert.Equal("Add donor", vm.EmptyActionText);

        // Empty because of a search: the action is to clear it.
        vm.SearchText = "zzz";
        await vm.SearchNowCommand.ExecuteAsync();
        Assert.Equal(LoadState.Empty, vm.State);
        Assert.Equal("Clear filters", vm.EmptyActionText);
        Assert.Same(vm.ClearFiltersCommand, vm.EmptyActionCommand);

        // Error with Retry.
        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Fail(FeatureRig.Problem(503, ProblemCodes.DatabaseWaking)));
        await vm.LoadAsync();
        Assert.Equal(LoadState.Error, vm.State);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));

        rig.Fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(2)));
        await vm.RetryCommand.ExecuteAsync();
        Assert.Equal(LoadState.Ready, vm.State);
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task PatientsList_UsesTheSamePagingSearchAndStates_FakeApi()
    {
        var rig = new FeatureRig();
        rig.PatientsReturn(q => Samples.PatientPage(75, q.Page, q.PageSize));
        var vm = rig.Patients();
        await vm.LoadAsync();
        Assert.Equal("Showing 1–50 of 75", vm.RangeText);

        vm.SearchText = "omar";
        rig.Clock.Advance(Debounce);
        await vm.PendingRefresh;

        var query = rig.Recorder.LastArg<PatientQuery>("GetPatientsAsync");
        Assert.Equal("omar", query.Search);
        Assert.Equal(1, query.Page);
        Assert.Equal(50, query.PageSize);

        rig.Fake.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Fail(FeatureRig.Problem(500, "error")));
        await vm.LoadAsync();
        Assert.Equal(LoadState.Error, vm.State);
    }

    [Fact]
    public async Task List_SlowLoad_TurnsIntoErrorWithRetryAfter10Seconds_FakeApi()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ApiResult<PagedResult<DonorListItem>>.Ok(Samples.DonorPage(1));
        });
        var vm = rig.Donors();

        var load = vm.LoadAsync();
        Assert.Equal(LoadState.Loading, vm.State);
        for (var i = 0; i < 2500 && rig.Fake.CountOf("GetDonorsAsync") == 0; i++)
        {
            await Task.Delay(2); // the request is on its way before the clock moves: the limit times the wait for the server
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await load;

        Assert.Equal(LoadState.Error, vm.State);
        Assert.NotNull(vm.RetryCommand);
    }
}
