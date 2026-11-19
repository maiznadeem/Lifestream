using System.Diagnostics;
using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Donors;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>Stock tiles (FEAT-19) and the dashboard (FEAT-27 to FEAT-31), against the fake API client.</summary>
public sealed class StockDashboardTests
{
    private static async Task<(FeatureRig Rig, StockViewModel Vm)> LoadedStockAsync()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        var vm = rig.Stock();
        await vm.LoadAsync();
        return (rig, vm);
    }

    private static async Task<(FeatureRig Rig, DashboardViewModel Vm, FakeNavigation Navigation)> LoadedDashboardAsync(int months = 12)
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard(months)));
        var navigation = new FakeNavigation();
        var vm = rig.Dashboard(navigation);
        await vm.LoadAsync();
        return (rig, vm, navigation);
    }

    // FEAT-19

    [Fact]
    public async Task Stock_OneTilePerBloodType_WithUnitsAvailableAndExpiringWithin7Days_FakeApi()
    {
        var (rig, vm) = await LoadedStockAsync();

        Assert.Equal(8, vm.Tiles.Count);
        Assert.Equal(BloodTypeExtensions.All, vm.Tiles.Select(t => t.BloodType).ToList());
        var oPos = vm.Tiles.Single(t => t.BloodType == BloodType.OPos);
        Assert.Equal("60 units available", oPos.AvailableText);
        Assert.Equal("5 expiring within 7 days", oPos.ExpiringText);
        Assert.Equal(1, rig.Recorder.CountOf("GetStockAsync")); // one call fills every tile
    }

    [Fact]
    public async Task Stock_TileBelowThreshold_HasIconTextAndToken_FakeApi()
    {
        var (_, vm) = await LoadedStockAsync();

        var low = vm.Tiles.Where(t => t.BelowThreshold).ToList();
        Assert.Equal([BloodType.ANeg, BloodType.ABNeg, BloodType.ONeg], low.Select(t => t.BloodType).ToList()); // every blood type below threshold
        Assert.All(low, t =>
        {
            Assert.Equal(StockSeverity.LowStock, t.Severity); // the view maps this to the Low stock chip: icon, text, danger token
            Assert.Equal("Low stock", t.StatusText);
            Assert.Contains("Low stock", t.AutomationName, StringComparison.Ordinal);
        });
        Assert.Equal(StockSeverity.ExpiringSoon, vm.Tiles.Single(t => t.BloodType == BloodType.BNeg).Severity);
        Assert.Equal(StockSeverity.InStock, vm.Tiles.Single(t => t.BloodType == BloodType.BPos).Severity);
        Assert.Equal("3 blood types are below their thresholds.", vm.SummaryText);
    }

    [Fact]
    public async Task Stock_Filter_AppliesImmediately_WithNoExtraCall_FakeApi()
    {
        var (rig, vm) = await LoadedStockAsync();

        vm.ViewFilter = vm.ViewOptions.First(o => o.Value == StockFilter.BelowThreshold);
        Assert.Equal(3, vm.Tiles.Count); // changed at once, no debounce, no refresh
        vm.ViewFilter = vm.ViewOptions.First(o => o.Value == StockFilter.ExpiringSoon);
        Assert.Equal([BloodType.APos, BloodType.BNeg, BloodType.ABNeg, BloodType.OPos], vm.Tiles.Select(t => t.BloodType).ToList());
        vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.OPos);
        Assert.Single(vm.Tiles);
        Assert.True(vm.HasFilters);

        await vm.ClearFiltersCommand.ExecuteAsync();
        Assert.Equal(8, vm.Tiles.Count);
        Assert.False(vm.HasFilters);
        Assert.Equal(1, rig.Recorder.CountOf("GetStockAsync"));
    }

    [Fact]
    public async Task Stock_FilterThatMatchesNothing_SaysSo_FakeApi()
    {
        var (_, vm) = await LoadedStockAsync();
        vm.ViewFilter = vm.ViewOptions.First(o => o.Value == StockFilter.BelowThreshold);
        vm.BloodTypeFilter = vm.BloodTypeOptions.First(o => o.Value == BloodType.OPos);

        Assert.Empty(vm.Tiles);
        Assert.True(vm.NothingMatches);
    }

    [Fact]
    public async Task Stock_OpenedFromADashboardCard_StartsFiltered_AndKeepsFiltersForTheSession_FakeApi()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        var first = rig.Stock();
        first.ReceiveParameter(new ListPrefilter(BloodType: BloodType.ANeg, StockView: StockFilter.BelowThreshold));
        await first.LoadAsync();
        Assert.Equal([BloodType.ANeg], first.Tiles.Select(t => t.BloodType).ToList());
        first.Dispose();

        var again = rig.Stock(); // coming back later in the session
        await again.LoadAsync();
        Assert.Equal([BloodType.ANeg], again.Tiles.Select(t => t.BloodType).ToList());
    }

    [Fact]
    public async Task Stock_ShowsSkeletonError_AndEmptyStates_FakeApi()
    {
        var rig = new FeatureRig();
        var gate = new TaskCompletionSource();
        rig.Fake.On("GetStockAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock());
        });
        var vm = rig.Stock();
        var load = vm.LoadAsync();
        Assert.True(vm.ShowSkeleton);
        gate.SetResult();
        await load;
        Assert.Equal(LoadState.Ready, vm.State);

        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Fail(FeatureRig.Problem(503, "database_waking")));
        await vm.LoadAsync();
        Assert.Equal(LoadState.Error, vm.State);
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([]));
        await vm.RetryCommand.ExecuteAsync();
        Assert.Equal(LoadState.Empty, vm.State);
    }

    // FEAT-27

    [Trait("Category", "Perf")] // asserts a wall-clock budget: runs in the perf pass (tests/perf/run-perf.ps1), not in the loaded default run
    [Fact]
    public async Task Dashboard_OneApiCall_SkeletonWithin100ms_FakeApi()
    {
        var rig = new FeatureRig();
        var gate = new TaskCompletionSource();
        rig.Fake.On("GetDashboardAsync", async _ =>
        {
            await gate.Task;
            return ApiResult<DashboardResponse>.Ok(Samples.Dashboard());
        });
        var vm = rig.Dashboard();

        var clock = Stopwatch.StartNew();
        var load = vm.LoadAsync();
        var skeletonAfter = clock.Elapsed;
        Assert.True(vm.ShowSkeleton);
        Assert.True(skeletonAfter < TimeSpan.FromMilliseconds(100), $"skeleton state took {skeletonAfter.TotalMilliseconds:F1} ms");

        gate.SetResult();
        await load;

        Assert.Equal(LoadState.Ready, vm.State);

        // Exactly one API call per data set of direction A's dashboard (the figures and charts, the stock, today's appointments, the open requests), started
        // together; never one per row (a call per donor to find the first-time ones was a regression) and never the same one twice.
        Assert.Equal(["GetDashboardAsync", "GetStockAsync", "GetAppointmentsAsync", "GetRequestsAsync"], rig.Recorder.Calls.Select(c => c.Name));
    }

    [Fact]
    public async Task Dashboard_MarksFirstTimeDonorsFromTheAppointmentList_WithoutAskingForEachDonor()
    {
        var rig = new FeatureRig();
        var slot = DateTime.UtcNow.Date.AddHours(12);
        Lifestream.Contracts.Appointments.AppointmentListItem[] today =
        [
            new(1, 101, "Returning Donor", BloodType.OPos, slot.AddDays(-3), slot, AppointmentStatus.Pending),
            new(2, 102, "New Donor", BloodType.APos, slot.AddDays(-3), slot.AddMinutes(30), AppointmentStatus.Pending) { DonorFirstTime = true },
        ];
        rig.Fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        rig.Fake.On("GetAppointmentsAsync", ApiResult<Lifestream.Contracts.PagedResult<Lifestream.Contracts.Appointments.AppointmentListItem>>.Ok(new(today, today.Length, 1, 50)));
        var vm = rig.Dashboard();

        await vm.LoadAsync();

        Assert.Equal([false, true], vm.Today.Select(a => a.IsFirstTime));
        Assert.DoesNotContain("GetDonorAsync", rig.Recorder.Calls.Select(c => c.Name));
    }

    // FEAT-28

    [Fact]
    public async Task Dashboard_DonationsChart_AggregatesByMonth_DefaultsToLast12Months_FakeApi()
    {
        var (rig, vm, _) = await LoadedDashboardAsync();
        var data = Samples.Dashboard();

        Assert.Equal("Last 12 months", vm.SelectedPeriod.Display);
        var (from, to) = (rig.Recorder.Calls[0].Args[0], rig.Recorder.Calls[0].Args[1]);
        var today = rig.Today;
        Assert.Equal(new DateOnly(today.Year, today.Month, 1).AddMonths(-11), from); // twelve month buckets ending with this month
        Assert.Equal(today, to);

        // One value per month bucket, summed over every blood type, and the label of each comes from the same month value.
        Assert.Equal(12, vm.DonatedByMonth.Count);
        Assert.Equal(12, vm.MonthLabels.Count);
        Assert.Equal(data.Months.Select(DashboardViewModel.MonthLabel), vm.MonthLabels);
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(data.Series.Sum(s => s.Donated[i]), vm.DonatedByMonth[i]);
            Assert.Equal(data.Series.Sum(s => s.Issued[i]), vm.IssuedByMonth[i]);
        }

        Assert.Equal(0, DashboardViewModel.YAxisMinimum); // the y-axis starts at zero
        Assert.Equal("Month", vm.XAxisTitle); // the axis title is the bucket unit
        Assert.Equal(DashboardViewModel.BucketName, vm.XAxisTitle);
    }

    [Fact]
    public async Task Dashboard_ViewAsTable_ListsTheSameValuesAndLabelsAsTheChart_FakeApi()
    {
        var (_, vm, _) = await LoadedDashboardAsync();

        var table = vm.DonationsTable;
        Assert.Equal(["Month", "Donated", "Issued"], table.Columns);
        Assert.Equal(vm.MonthLabels.Count, table.Rows.Count);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            Assert.Equal(vm.MonthLabels[i], table.Rows[i][0]);
            Assert.Equal(vm.DonatedByMonth[i].ToString("N0", System.Globalization.CultureInfo.CurrentCulture), table.Rows[i][1]);
            Assert.Equal(vm.IssuedByMonth[i].ToString("N0", System.Globalization.CultureInfo.CurrentCulture), table.Rows[i][2]);
        }
    }

    [Fact]
    public async Task Dashboard_TogglingBloodTypes_ChangesTheChartWithoutANewCall_FakeApi()
    {
        var (rig, vm, _) = await LoadedDashboardAsync();
        var data = Samples.Dashboard();
        var raised = 0;
        vm.ChartDataChanged += (_, _) => raised++;

        foreach (var toggle in vm.BloodTypes.Where(t => t.BloodType != BloodType.OPos))
        {
            toggle.IsSelected = false;
        }

        var onlyOPos = data.Series.Single(s => s.BloodType == BloodType.OPos);
        Assert.Equal(onlyOPos.Donated, vm.DonatedByMonth);
        Assert.Equal(["O+", "Total"], vm.Totals.Select(t => t.Label));
        Assert.True(raised >= 7);
        Assert.Equal(1, rig.Recorder.CountOf("GetDashboardAsync"));
    }

    [Fact]
    public async Task Dashboard_PeriodSelector_FetchesTheChosenRange_FakeApi()
    {
        var (rig, vm, _) = await LoadedDashboardAsync();
        rig.Fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard(6)));

        vm.SelectedPeriod = vm.Periods.First(p => p.Months == 6);
        await vm.PendingLoad;

        var from = (DateOnly)rig.Recorder.Calls.Last(c => c.Name == "GetDashboardAsync").Args[0]!;
        var today = rig.Today;
        Assert.Equal(new DateOnly(today.Year, today.Month, 1).AddMonths(-5), from);
        Assert.Equal(["Last 3 months", "Last 6 months", "Last 12 months", "Last 24 months"], vm.Periods.Select(p => p.Display));
    }

    // FEAT-29

    [Fact]
    public async Task Dashboard_Demographics_IncludeEveryGenderValue_WithAData_Table_FakeApi()
    {
        var rig = new FeatureRig();
        var data = Samples.Dashboard() with { Demographics = [new(Gender.Male, 70), new(Gender.Female, 0), new(Gender.Other, 2)] };
        rig.Fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(data));
        var vm = rig.Dashboard();
        await vm.LoadAsync();

        Assert.Equal(Enum.GetValues<Gender>(), vm.Demographics.Select(b => b.Gender)); // every stored value, zeros included
        Assert.Equal(["Gender", "Donors"], vm.DemographicsTable.Columns);
        Assert.Equal(["Female", "0"], vm.DemographicsTable.Rows[1]);
    }

    // FEAT-30

    [Fact]
    public async Task Dashboard_KpiCard_OpensFilteredList_FakeApi()
    {
        var (_, vm, navigation) = await LoadedDashboardAsync();

        await vm.OpenCardCommand.ExecuteAsync(vm.Cards.Single(c => c.Title == "Donors"));
        await vm.OpenCardCommand.ExecuteAsync(vm.Cards.Single(c => c.Title == "Pending appointments"));
        await vm.OpenCardCommand.ExecuteAsync(vm.Cards.Single(c => c.Title == "Expiring within 7 days"));

        Assert.Equal(typeof(DonorsViewModel), navigation.Visits[0].ViewModel);
        Assert.Equal(typeof(Lifestream.Client.Features.Appointments.AppointmentsViewModel), navigation.Visits[1].ViewModel); // part 2 replaced the placeholder
        Assert.Equal("Pending", ((ListPrefilter)navigation.Visits[1].Parameter!).Status);
        Assert.Equal(typeof(StockViewModel), navigation.Visits[2].ViewModel);
        Assert.Equal(StockFilter.ExpiringSoon, ((ListPrefilter)navigation.Visits[2].Parameter!).StockView);
        Assert.Equal(6, vm.Cards.Count);
        Assert.Equal(120, vm.Cards.Single(c => c.Title == "Donors").Value);
    }

    [Fact]
    public async Task Dashboard_LowStock_ListsEveryBloodTypeBelowThreshold_AndOpensStockOnIt_FakeApi()
    {
        var (_, vm, navigation) = await LoadedDashboardAsync();

        Assert.True(vm.HasLowStock);
        Assert.Equal([BloodType.ANeg, BloodType.ABNeg, BloodType.ONeg], vm.LowStock.Select(a => a.BloodType).ToList());
        Assert.Equal("A-: low stock, 4 of 10 units", vm.LowStock[0].Text); // words, not only a colour

        await vm.OpenLowStockCommand.ExecuteAsync(vm.LowStock[2]);

        Assert.Equal(typeof(StockViewModel), navigation.Visits.Single().ViewModel);
        var prefilter = (ListPrefilter)navigation.Visits.Single().Parameter!;
        Assert.Equal(BloodType.ONeg, prefilter.BloodType);
        Assert.Equal(StockFilter.BelowThreshold, prefilter.StockView);
    }

    [Fact]
    public async Task Dashboard_Cards_OpenTheRealScreensThroughNavigation_FakeApi()
    {
        using var rig = new Rig();
        rig.SignIn();
        rig.Api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        var navigation = rig.Get<Navigation.INavigationService>();
        await navigation.NavigateTo<DashboardViewModel>();
        var dashboard = (DashboardViewModel)navigation.Current!;

        await dashboard.OpenCardCommand.ExecuteAsync(dashboard.Cards.Single(c => c.Title == "Expiring within 7 days"));

        var stock = Assert.IsType<StockViewModel>(navigation.Current);
        Assert.Equal([BloodType.APos, BloodType.BNeg, BloodType.ABNeg, BloodType.OPos], stock.Tiles.Select(t => t.BloodType).ToList());
    }

    // FEAT-31

    [Fact]
    public async Task Dashboard_TotalsTable_HasOneRowPerBloodTypeAndATotalRow_FakeApi()
    {
        var (_, vm, _) = await LoadedDashboardAsync();
        var data = Samples.Dashboard();

        Assert.Equal(9, vm.Totals.Count);
        var total = vm.Totals[^1];
        Assert.True(total.IsTotal);
        Assert.Equal(data.Series.Sum(s => s.Donated.Sum()), total.Donated);
        Assert.Equal(data.Series.Sum(s => s.Issued.Sum()), total.Issued);
        Assert.Equal("O+", vm.Totals[6].Label);
    }
}
