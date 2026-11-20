using Lifestream.Client.Api;
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
/// The major defects of the real-app walk of 2.1.0 on the list screens, at the level of the view models (run/agents/real-app-walk.md): 13 (Delete donor offered,
/// confirmed with "You can undo this", then refused) and 18 (the pane said "Select a request" over the request it was showing). The server's rule is the one the
/// real API has: a donor with an appointment and a patient with a request can't be deleted (409 conflict).
/// </summary>
public sealed class RealAppWalkMajorTests
{
    private static ApiResult<PagedResult<T>> Page<T>(int total, params T[] items) => ApiResult<PagedResult<T>>.Ok(new([.. items], total, 1, 50));

    private static FeatureRig DonorRig(ApiResult<PagedResult<AppointmentListItem>> appointments)
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetDonorsAsync", Page(1, Samples.DonorRow(2, "Alma Sandoval")));
        rig.Fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Samples.Donor(2, "Alma Sandoval")));
        rig.Fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new DonorEligibility(true, [], null)));
        rig.Fake.On("GetDonorDonationsAsync", ApiResult<DonorDonations>.Ok(new DonorDonations(0, 0, [])));
        rig.Fake.On("GetAppointmentsAsync", appointments);
        return rig;
    }

    // ---- 13. Delete is offered only when it can succeed, and says why when it cannot ------------------------------------------------

    [Fact]
    public async Task Donors_ADonorWithAppointmentsOnRecord_IsNotOfferedDelete_AndThePanelSaysWhy()
    {
        var rig = DonorRig(Page<AppointmentListItem>(4)); // four appointments on record; only the total is read
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.False(vm.DeleteCommand.CanExecute(null)); // released 2.1.0: offered, confirmed with "You can undo this", then refused by the server
        Assert.Equal("A donor with appointments or donations on record can't be deleted: their history is kept.", vm.DeleteBlockedReason);
        Assert.Empty(rig.Dialogs.Confirmations);
        Assert.Equal(0, rig.Recorder.CountOf("DeleteDonorAsync"));
        Assert.True(vm.EditCommand.CanExecute(null)); // the donor can still be edited and booked
    }

    [Fact]
    public async Task Donors_ADonorWithNoAppointments_CanBeDeleted_AndUndone()
    {
        var rig = DonorRig(Page<AppointmentListItem>(0));
        rig.Fake.Handlers["DeleteDonorAsync"] = _ => Task.FromResult<object>(ApiResult.Success);
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.Null(vm.DeleteBlockedReason);
        Assert.True(vm.DeleteCommand.CanExecute(null));
        await vm.DeleteCommand.ExecuteAsync();

        Assert.Equal(1, rig.Recorder.CountOf("DeleteDonorAsync"));
        Assert.Contains("You can undo this", rig.Dialogs.Confirmations.Single().Body, StringComparison.Ordinal);
        Assert.Contains(rig.Toasts.Toasts, t => t.Message.Contains("deleted", StringComparison.Ordinal) && t.HasAction);
    }

    [Fact]
    public async Task Donors_WhenTheServerRefusesADeleteAnyway_TheMessageSaysDeleted_Why_AndThatNothingChanged()
    {
        // The count of appointments could not be read, so Delete is left to the server, which refuses (409 conflict: appointments point at the donor).
        var rig = DonorRig(FakeApiClient.Fail<PagedResult<AppointmentListItem>>(500, "server_error"));
        rig.Fake.Handlers["DeleteDonorAsync"] = _ => Task.FromResult<object>(ApiResult.Fail(new ApiProblem(409, ProblemCodes.Conflict, "The record is in use.")));
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        Assert.True(vm.DeleteCommand.CanExecute(null));

        await vm.DeleteCommand.ExecuteAsync();

        var toast = rig.Toasts.Toasts.Single();
        // Released 2.1.0: "Donor 'Alma Sandoval' can't be changed because it is in use by other records."
        Assert.Equal("Donor 'Alma Sandoval' can't be deleted: they have appointments or donations on record, and that history is kept. Nothing was changed.", toast.Message);
        Assert.Equal(2, vm.Detail?.Id); // the donor is still in the panel and in the list
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Patients_APatientWithRequestsOnRecord_IsNotOfferedDelete_AndThePanelSaysWhy()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetPatientsAsync", Page(1, Samples.PatientRow(3, "Omar Haddad")));
        rig.Fake.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(Samples.Patient(3, "Omar Haddad")));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([]));
        rig.Fake.On("GetRequestsAsync", Page<RequestListItem>(2)); // two requests on record (fulfilled long ago: none waiting, and still not deletable)
        var vm = rig.Patients();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.Equal("A patient with requests on record can't be deleted: their history is kept.", vm.DeleteBlockedReason);

        rig.Fake.On("GetRequestsAsync", Page<RequestListItem>(0));
        await vm.LoadDetailAsync(3);
        Assert.Null(vm.DeleteBlockedReason);
        Assert.True(vm.DeleteCommand.CanExecute(null));
    }

    private static Part2Rig RequestRig(int inStock)
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", Page(1, Samples2.RequestRow(7, 3, "Omar Haddad"))); // 3 units of A−
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, inStock, 0, 10, true)]));
        return rig;
    }

    // ---- 18. The pane never says "Select a request" while it shows one ------------------------------------------------------------------

    [Fact]
    public async Task Requests_ARecordThatLeftTheListsView_StaysInThePane_WithoutTheSelectPlaceholder()
    {
        var rig = RequestRig(inStock: 5);
        var vm = rig.Requests();
        await vm.LoadAsync();
        Assert.True(vm.ShowNoSelection); // nothing chosen yet: "Select a request to see its details."
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        Assert.False(vm.ShowNoSelection);

        // The request is fulfilled: it leaves the Open view, and the pane goes on showing it (as after "Fulfil request" in the real app).
        rig.Fake.On("GetRequestsAsync", Page<RequestListItem>(0));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7, status: RequestStatus.Fulfilled)));
        await vm.RefreshAsync();
        await vm.LoadDetailAsync(7);

        Assert.Null(vm.SelectedItem); // no row in the list for it
        Assert.Equal(7, vm.Detail?.Id);
        Assert.True(vm.HasDetail);
        Assert.False(vm.ShowNoSelection); // released 2.1.0: the placeholder was drawn over the record (it followed the selected row)

        vm.CloseDetailCommand.Execute(null);
        Assert.True(vm.ShowNoSelection);
    }
}
