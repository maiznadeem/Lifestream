using System.Reflection;
using Lifestream.Api.Features;
using Lifestream.Api.Infrastructure;
using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// Sorting on the part 2 lists. Every sort key a screen offers is checked against the API's own validator (its <c>SortMap</c>, read by reflection from
/// the endpoint class, not copied here); a key the API would refuse is never sent, and a refusal that still happens resets to the default sort.
/// </summary>
public sealed class Part2SortTests
{
    /// <summary>Asks the API's real sort validator whether it accepts a key: true when it does, false when it throws its validation problem.</summary>
    private static bool ApiAccepts(Type endpoints, string sort)
    {
        var map = endpoints.GetField("Sorts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var entity = map.GetType().GetGenericArguments()[0];
        var empty = typeof(Queryable).GetMethods().First(m => m.Name == "AsQueryable" && !m.IsGenericMethodDefinition).Invoke(null, [Array.CreateInstance(entity, 0)]);
        try
        {
            map.GetType().GetMethod("Apply")!.Invoke(map, [empty, sort]);
            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ApiException { Code: ProblemCodes.Validation })
        {
            return false;
        }
    }

    [Fact]
    public void Appointments_EverySortableColumnKey_IsAcceptedByTheApiValidator()
    {
        var keys = new Part2Rig().Appointments().SortKeys;
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(ApiAccepts(typeof(AppointmentEndpoints), key), key);
            Assert.True(ApiAccepts(typeof(AppointmentEndpoints), "-" + key), "-" + key);
        });
    }

    [Fact]
    public void Requests_EverySortableColumnKey_IsAcceptedByTheApiValidator()
    {
        var keys = new Part2Rig().Requests().SortKeys;
        Assert.Equal(["placed", "requiredBy"], keys);
        Assert.All(keys, key =>
        {
            Assert.True(ApiAccepts(typeof(RequestEndpoints), key), key);
            Assert.True(ApiAccepts(typeof(RequestEndpoints), "-" + key), "-" + key);
        });
    }

    [Fact]
    public void Receipts_EverySortableColumnKey_IsAcceptedByTheApiValidator()
    {
        var keys = new Part2Rig().Receipts().SortKeys;
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(ApiAccepts(typeof(ReceiptEndpoints), key), key);
            Assert.True(ApiAccepts(typeof(ReceiptEndpoints), "-" + key), "-" + key);
        });
    }

    [Fact]
    public void Staff_OffersNoSortableColumn_BecauseTheClientCallHasNoSortParameter()
    {
        Assert.Empty(new Part2Rig().Staff().SortKeys);
        Assert.DoesNotContain(typeof(ILifestreamApiClient).GetMethod(nameof(ILifestreamApiClient.GetStaffAsync))!.GetParameters(), p => p.Name == "sort");
        Assert.True(ApiAccepts(typeof(StaffEndpoints), "name")); // the API could sort, so the validator is the real one
    }

    [Fact]
    public void TheApiValidator_RefusesTheKeysTheOldGridsSent_SoTheTestCanFail()
    {
        // The bound property names a DataGrid column sorts by when no SortMemberPath is set: the API refuses them.
        Assert.False(ApiAccepts(typeof(AppointmentEndpoints), "DonorName"));
        Assert.False(ApiAccepts(typeof(RequestEndpoints), "PatientName"));
        Assert.False(ApiAccepts(typeof(ReceiptEndpoints), "PartyName"));
        Assert.False(ApiAccepts(typeof(AppointmentEndpoints), "-Status"));
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
    public async Task Staff_NeverSendsASort_EvenWhenAskedTo()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetStaffAsync", ApiResult<PagedResult<StaffListItem>>.Ok(Samples2.StaffPage(Samples2.StaffRow(1))));
        var vm = rig.Staff();
        await vm.LoadAsync();

        await vm.SortCommand.ExecuteAsync("name");

        Assert.True(vm.IsReady);
        Assert.Null(vm.ViewState.SortColumn);
        Assert.Equal(2, rig.Recorder.CountOf("GetStaffAsync"));
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
