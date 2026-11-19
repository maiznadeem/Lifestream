using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Receipts;
using Lifestream.Client.Features.Staff;
using Lifestream.Client.Features.Stock;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Staff;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The design-review fixes of Stock, Receipts, Staff and Profile in their view models: the stock-by-age cells and the panel's bag list, the receipt
/// tab counts and record codes, the salary chip and job titles, and the Profile shortcuts.
/// </summary>
public sealed class FixRoundScreenTests
{
    private static IReadOnlyList<StockItem> StockWithAges() =>
    [
        .. Samples.Stock().Select(s => s with { AgeBands = [s.Available - s.ExpiringSoon, 0, 0, 0, 0, s.ExpiringSoon] }),
    ];

    private static StockBagList Bags(BloodType type, DateOnly today, int count, bool hasMore = false) =>
        new(type, [.. Enumerable.Range(0, count).Select(i => new StockBag(400 + i, today.AddDays(-40 + i).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc), today.AddDays(2 + i)))], hasMore);

    /// <summary>The server's calendar day (bags expire by UTC date), as the screen counts days left.</summary>
    private static DateOnly UtcToday(FeatureRig rig) => DateOnly.FromDateTime(rig.Clock.GetUtcNow().UtcDateTime);

    private static async Task<(FeatureRig Rig, StockViewModel Vm)> StockAsync(IReadOnlyList<StockItem>? stock = null, FakeNavigation? navigation = null)
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(stock ?? StockWithAges()));
        var vm = new StockViewModel(rig.Api, rig.States, navigation) { Zone = TimeZoneInfo.Utc };
        vm.Attach(null, rig.Clock);
        await vm.LoadAsync();
        return (rig, vm);
    }

    // ---- Stock: the age matrix ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_AgeCells_ComeFromTheApiBands_TheLastIsTheExpiringBand()
    {
        var (_, vm) = await StockAsync();
        var oPos = vm.Tiles.Single(t => t.BloodType == BloodType.OPos); // 60 units, 5 expiring
        Assert.Equal(StockAges.BandCount, oPos.AgeCells.Count);
        Assert.Equal([55, 0, 0, 0, 0, 5], oPos.AgeCells.Select(c => c.Count));
        Assert.Equal(["55", "0", "0", "0", "0", "5"], oPos.AgeCells.Select(c => c.Text));
        Assert.True(oPos.AgeCells[^1].IsExpiringBand);
        Assert.All(oPos.AgeCells.Take(5), c => Assert.False(c.IsExpiringBand));
        Assert.Equal([4, 0, 0, 0, 0, 3], oPos.AgeCells.Select(c => c.Level)); // tint steps: 8 or more, none, then 4 to 7
        Assert.Equal("35 to 42 days old: 5 bags, expires within 7 days", oPos.AgeCells[^1].AutomationName);
        Assert.Equal("60 units", oPos.UnitsText);
    }

    [Fact]
    public async Task Stock_WithoutAgeBands_CellsSayNotKnown_NeverZero()
    {
        var (_, vm) = await StockAsync(Samples.Stock());
        Assert.All(vm.Tiles, t => Assert.All(t.AgeCells, c => Assert.Equal(("–", 0, (int?)null), (c.Text, c.Level, c.Count))));
    }

    [Fact]
    public async Task Stock_MeterScale_IsThreeTimesTheMinimum_SoTheMinimumTickSitsAtAThird()
    {
        var (_, vm) = await StockAsync();
        Assert.All(vm.Tiles, t => Assert.Equal(30, t.Scale)); // minimum 10
        Assert.All(vm.Tiles, t => Assert.Equal(1.0 / 3, t.MinimumFraction, 6));
        Assert.Equal(1, vm.Tiles.Single(t => t.BloodType == BloodType.OPos).FillFraction); // more than 3x fills the bar; the number says how much
        Assert.Equal(4 / 30.0, vm.Tiles.Single(t => t.BloodType == BloodType.ANeg).FillFraction, 6);
    }

    // ---- Stock: the panel's bags ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_SelectingAGroup_LoadsItsBagsOnce_InUseFirstOrder_WithCodesAndDaysLeft()
    {
        var (rig, vm) = await StockAsync();
        rig.Fake.On("GetStockBagsAsync", ApiResult<StockBagList>.Ok(Bags(BloodType.ANeg, UtcToday(rig), 4)));

        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.ANeg);
        await vm.PendingBags;

        Assert.Equal(1, rig.Recorder.CountOf("GetStockBagsAsync"));
        Assert.True(vm.HasBags);
        Assert.False(vm.ShowBagsSkeleton);
        Assert.Equal(["B-0400", "B-0401", "B-0402", "B-0403"], vm.Bags.Select(b => b.Code));
        Assert.Equal([1, 2, 3, 4], vm.Bags.Select(b => b.Position));
        Assert.True(vm.Bags[0].IsFirst);
        Assert.Equal("2 days left", vm.Bags[0].DaysLeftText);
        Assert.True(vm.Bags[0].IsExpiring);
        Assert.Equal(UtcToday(rig).AddDays(-40), DateOnly.ParseExact(vm.Bags[0].CollectedText, "dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(string.Empty, vm.BagsMoreText);

        // The same group again (a filter rebuilding the rows) does not ask again; another group does.
        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.ANeg) with { };
        await vm.PendingBags;
        Assert.Equal(1, rig.Recorder.CountOf("GetStockBagsAsync"));

        await vm.ClosePanelCommand.ExecuteAsync(null);
        Assert.Empty(vm.Bags);
        Assert.False(vm.HasNoBags);
    }

    [Fact]
    public async Task Stock_ACappedBagList_SaysHowManyItShows()
    {
        var (rig, vm) = await StockAsync();
        rig.Fake.On("GetStockBagsAsync", ApiResult<StockBagList>.Ok(Bags(BloodType.OPos, UtcToday(rig), 3, hasMore: true)));
        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.OPos);
        await vm.PendingBags;
        Assert.Equal("Showing the oldest 3 of 60 bags.", vm.BagsMoreText);
    }

    [Fact]
    public async Task Stock_AGroupWithNoStock_MakesNoBagCall_AndSaysSo()
    {
        var stock = StockWithAges().Select(s => s.BloodType == BloodType.ABNeg ? s with { Available = 0, ExpiringSoon = 0, AgeBands = [0, 0, 0, 0, 0, 0] } : s).ToList();
        var (rig, vm) = await StockAsync(stock);
        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.ABNeg);
        await vm.PendingBags;
        Assert.Equal(0, rig.Recorder.CountOf("GetStockBagsAsync"));
        Assert.True(vm.HasNoBags);
    }

    [Fact]
    public async Task Stock_AFailedBagLoad_IsAnErrorWithRetry_NotAnEmptyList()
    {
        var (rig, vm) = await StockAsync();
        rig.Fake.On("GetStockBagsAsync", ApiResult<StockBagList>.Fail(FeatureRig.Problem(503, "database_waking")));
        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.BPos);
        await vm.PendingBags;
        Assert.True(vm.HasBagsError);
        Assert.False(vm.HasNoBags);
        Assert.Contains("retry", vm.BagsErrorText, StringComparison.Ordinal);

        rig.Fake.On("GetStockBagsAsync", ApiResult<StockBagList>.Ok(Bags(BloodType.BPos, UtcToday(rig), 2)));
        await vm.RetryBagsCommand.ExecuteAsync(null);
        await vm.PendingBags;
        Assert.False(vm.HasBagsError);
        Assert.Equal(2, vm.Bags.Count);
    }

    [Fact]
    public async Task Stock_Refresh_ReloadsTheOpenGroupsBags()
    {
        var (rig, vm) = await StockAsync();
        rig.Fake.On("GetStockBagsAsync", ApiResult<StockBagList>.Ok(Bags(BloodType.APos, UtcToday(rig), 2)));
        vm.SelectedTile = vm.Tiles.Single(t => t.BloodType == BloodType.APos);
        await vm.PendingBags;
        await vm.RetryCommand.ExecuteAsync(null);
        await vm.PendingBags;
        Assert.Equal(2, rig.Recorder.CountOf("GetStockBagsAsync"));
        Assert.Equal(BloodType.APos, vm.SelectedTile?.BloodType);
    }

    [Fact]
    public async Task Stock_EmptyState_OpensAppointments_WhereDonationsAreRecorded()
    {
        var navigation = new FakeNavigation();
        var (_, vm) = await StockAsync([], navigation);
        Assert.False(vm.HasStock);
        await vm.RecordDonationCommand!.ExecuteAsync(null);
        Assert.Equal(typeof(AppointmentsViewModel), navigation.Visits.Single().ViewModel);
    }

    // ---- Receipts --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receipts_TabCounts_ComeFromTheCountsCall_ForTheSameSearchAndGroup()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90)], 1, 1, 50)));
        rig.Fake.On("GetReceiptCountsAsync", ApiResult<ReceiptCounts>.Ok(new(12, 9, 3)));
        var vm = rig.Receipts();
        await vm.LoadAsync();
        Assert.Equal(("12", "9", "3"), (vm.AllCountText, vm.DonationCountText, vm.IssueCountText));

        vm.SearchText = "Haddad";
        await vm.SearchNowCommand.ExecuteAsync(null);
        var query = rig.Recorder.LastArg<ReceiptCountQuery>("GetReceiptCountsAsync");
        Assert.Equal("Haddad", query.Search);
    }

    [Fact]
    public async Task Receipts_WithoutCounts_TheTabsShowNoNumber_NeverAWrongOne()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90)], 1, 1, 50)));
        rig.Fake.On("GetReceiptCountsAsync", ApiResult<ReceiptCounts>.Fail(FeatureRig.Problem(500, "server_error")));
        var vm = rig.Receipts();
        await vm.LoadAsync();
        Assert.Equal(LoadState.Ready, vm.State); // the list itself loaded
        Assert.Equal((string.Empty, string.Empty, string.Empty), (vm.AllCountText, vm.DonationCountText, vm.IssueCountText));
    }

    [Fact]
    public void RecordCodes_AreTheLetterAndNumberStyle()
    {
        Assert.Equal(["RC-0001", "A-0905", "R-0148", "S-0002", "B-0412", "B-12345"],
            new[] { RecordCodes.Receipt(1), RecordCodes.Appointment(905), RecordCodes.Request(148), RecordCodes.Staff(2), RecordCodes.Bag(412), RecordCodes.Bag(12345) });
    }

    // ---- Staff -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_SalaryChip_SaysTheRange_AndClearEmptiesIt()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(Samples2.StaffPage()));
        var vm = rig.Staff();
        await vm.LoadAsync();
        Assert.Equal(("Salary", false), (vm.SalaryChipText, vm.HasSalaryRange));
        vm.SalaryFromText = "2000";
        Assert.Equal("Salary: from 2,000", vm.SalaryChipText);
        vm.SalaryToText = "3500";
        Assert.Equal(("Salary: 2,000–3,500", true), (vm.SalaryChipText, vm.HasSalaryRange));
        vm.SalaryFromText = string.Empty;
        Assert.Equal("Salary: up to 3,500", vm.SalaryChipText);
        await vm.ClearSalaryCommand.ExecuteAsync(null);
        Assert.Equal(("Salary", string.Empty, string.Empty), (vm.SalaryChipText, vm.SalaryFromText, vm.SalaryToText));
    }

    [Fact]
    public void Staff_JobTitleAndRole_AreNeverBothAdmin()
    {
        Assert.Equal("Administration", StaffText.JobTitle(Designation.Admin));
        Assert.Equal("Doctor", StaffText.JobTitle(Designation.Doctor));
        Assert.Equal(("Administrator", "Staff"), (StaffText.Role(Role.Admin), StaffText.Role(Role.Staff)));
        var row = new StaffRow(new StaffListItem(2, "Elena Kim", "ekim", Designation.Admin, Shift.Morning, Role.Staff, false, null, null));
        Assert.Equal(("Administration", "Staff", "S-0002"), (row.DesignationText, row.RoleText, row.CodeText));
    }

    // ---- Profile ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Profile_ListsTheShellsShortcuts_AndTheJobTitle()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetProfileAsync", ApiResult<Contracts.Profile.ProfileResponse>.Ok(Samples2.Profile()));
        var vm = rig.Profile();
        await vm.LoadAsync();
        Assert.Equal("Administration", vm.DesignationText);
        Assert.Contains(vm.Shortcuts, s => s.Keys.SequenceEqual(["Ctrl", "K"]));
        Assert.Contains(vm.Shortcuts, s => s.Keys.SequenceEqual(["Ctrl", "F"]));
        Assert.Contains(vm.Shortcuts, s => s.Keys.SequenceEqual(["Esc"]));
        Assert.All(vm.Shortcuts, s => Assert.False(string.IsNullOrWhiteSpace(s.Action)));
        Assert.False(vm.HasUnsavedChanges); // the save bar is hidden until something changes
        vm.Phone = "+1 555 0199";
        Assert.True(vm.HasUnsavedChanges);
    }
}
