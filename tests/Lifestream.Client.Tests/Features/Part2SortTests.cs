using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// Sorting on the part 2 lists. Every sort key a screen offers is checked against the keys the shared contracts define (the API builds its own validators from
/// the same constants, which its own tests check, so this project needs no reference to the API); a key the API would refuse is never sent, and a refusal that
/// still happens resets to the default sort.
/// </summary>
public sealed class Part2SortTests
{
    /// <summary>Whether the contracts' list accepts a key the way the API's validator does: case-insensitive, with an optional "-" or "+" in front.</summary>
    private static bool Accepts(IReadOnlyList<string> contract, string sort) =>
        contract.Contains(sort.Trim().TrimStart('-', '+'), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Appointments_EverySortableColumnKey_IsAcceptedByTheApiValidator()
    {
        var keys = new Part2Rig().Appointments().SortKeys;
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(Accepts(SortKeys.Appointments, key), key);
            Assert.True(Accepts(SortKeys.Appointments, "-" + key), "-" + key);
        });
    }

    [Fact]
    public void Requests_EverySortableColumnKey_IsAcceptedByTheApiValidator()
    {
        var keys = new Part2Rig().Requests().SortKeys;
        Assert.Equal(["placed", "requiredBy"], keys);
        Assert.All(keys, key =>
        {
            Assert.True(Accepts(SortKeys.Requests, key), key);
            Assert.True(Accepts(SortKeys.Requests, "-" + key), "-" + key);
        });
    }

    [Fact]
    public void Receipts_EverySortableColumnKey_IsAcceptedByTheApiValidator()
    {
        var keys = new Part2Rig().Receipts().SortKeys;
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(Accepts(SortKeys.Receipts, key), key);
            Assert.True(Accepts(SortKeys.Receipts, "-" + key), "-" + key);
        });
    }

    [Fact]
    public void Staff_OffersExactlyTheSortKeysTheContractsDefine_AndTheClientCallCarriesASortParameter()
    {
        Assert.Equal(SortKeys.Staff, new Part2Rig().Staff().SortKeys);
        Assert.Contains(typeof(ILifestreamApiClient).GetMethod(nameof(ILifestreamApiClient.GetStaffAsync))!.GetParameters(), p => p.Name == "sort");
    }

    [Fact]
    public void DonorsAndPatients_OfferOnlyTheContractsKeys()
    {
        var rig = new Part2Rig();
        Assert.Equal(SortKeys.Donors, rig.Base.Donors().SortKeys);
        Assert.Equal(SortKeys.Patients, rig.Base.Patients().SortKeys);
    }

    [Fact]
    public void TheApiValidator_RefusesTheKeysTheOldGridsSent_SoTheTestCanFail()
    {
        // The bound property names a DataGrid column sorts by when no SortMemberPath is set: the API refuses them.
        Assert.False(Accepts(SortKeys.Appointments, "DonorName"));
        Assert.False(Accepts(SortKeys.Requests, "PatientName"));
        Assert.False(Accepts(SortKeys.Receipts, "PartyName"));
        Assert.False(Accepts(SortKeys.Appointments, "-Status"));
        Assert.False(Accepts(SortKeys.Donors, "bloodType")); // the Donors grid used to send this one
    }

    [Fact]
    public async Task RememberedKeyTheScreenCannotSend_IsDroppedBeforeTheCall_AndForgotten()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3)], 1, 1, 50)));
        var vm = rig.Requests();
        vm.ViewState.SortColumn = "PatientName"; // what an old grid would have left behind
        vm.ViewState.SortDescending = true;

        await vm.LoadAsync();

        Assert.Null(rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").Sort);
        Assert.Null(vm.ViewState.SortColumn);
        Assert.False(vm.SortDescending);
        Assert.True(vm.IsReady);
    }

    [Fact]
    public async Task SupportedSort_IsSentExactly_AndSurvivesPaging()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3)], 1, 1, 50)));
        var vm = rig.Requests();
        await vm.LoadAsync();

        await vm.SortCommand.ExecuteAsync("-requiredBy");

        Assert.Equal("-requiredBy", rig.Recorder.LastArg<RequestQuery>("GetRequestsAsync").Sort);
        Assert.Equal("requiredBy", vm.SortColumn);
        Assert.True(vm.SortDescending);
    }

    [Fact]
    public async Task SortRefusedByTheApi_ResetsToTheDefaultSort_AndShowsTheList_NotAnError()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetAppointmentsAsync", _ =>
        {
            var query = rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync");
            return Task.FromResult(query.Sort is null
                ? ApiResult<PagedResult<AppointmentListItem>>.Ok(Samples2.AppointmentPage(Samples2.AppointmentRow(5, 42)))
                : FakeApiClient.Fail<PagedResult<AppointmentListItem>>(400, ProblemCodes.Validation, "sort must be one of: nothing"));
        });
        var vm = rig.Appointments();
        await vm.LoadAsync();

        await vm.SortCommand.ExecuteAsync("slot"); // a supported key that the (changed) API refuses all the same

        Assert.True(vm.IsReady);
        Assert.False(vm.HasError);
        Assert.Single(vm.Items);
        Assert.Null(vm.SortColumn); // back to the default; the refused sort is not remembered
        Assert.Null(vm.ViewState.SortColumn);
        Assert.Null(rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync").Sort);

        await vm.RefreshAsync(); // and the next refresh stays on the default
        Assert.True(vm.IsReady);
    }

    [Fact]
    public async Task Staff_SendsTheSortToTheApi_AndDropsAKeyItCannotSend()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(Samples2.StaffPage(Samples2.StaffRow(1))));
        var vm = rig.Staff();
        await vm.LoadAsync();

        await vm.SortCommand.ExecuteAsync("-username");

        Assert.True(vm.IsReady);
        Assert.Equal("username", vm.ViewState.SortColumn);
        Assert.Equal("-username", rig.Recorder.Calls.Last(c => c.Name == "GetStaffAsync").Args[3]);

        vm.ViewState.SortColumn = "salary"; // not a staff sort key
        await vm.RefreshAsync();
        Assert.Null(rig.Recorder.Calls.Last(c => c.Name == "GetStaffAsync").Args[3]);
        Assert.Null(vm.ViewState.SortColumn);
    }

    [Fact]
    public async Task Receipts_RememberedBadKey_IsDropped()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetReceiptsAsync", ApiResult<PagedResult<ReceiptListItem>>.Ok(new([Samples2.ReceiptRow(90)], 1, 1, 50)));
        var vm = rig.Receipts();
        vm.ViewState.SortColumn = "StaffName";

        await vm.LoadAsync();

        Assert.Null(rig.Recorder.LastArg<ReceiptQuery>("GetReceiptsAsync").Sort);
        Assert.Null(vm.ViewState.SortColumn);
    }
}
