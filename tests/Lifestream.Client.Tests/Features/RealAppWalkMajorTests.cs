using Lifestream.Client.Api;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The major defects of the real-app walk of 2.1.0 on the list screens, at the level of the view models (run/agents/real-app-walk.md): 13 (Delete donor offered,
/// confirmed with "You can undo this", then refused), 18 (the pane said "Select a request" over the request it was showing) and 19 ("Fulfil request" offered for a
/// request the stock could not cover, then refused). The server's rules are the ones the real API has: a donor with an appointment and a patient with a request
/// can't be deleted (409 conflict), and a request is issued in full or not at all (409 insufficient_stock).
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

        rig.Fake.On("GetAppointmentsAsync", Page<AppointmentListItem>(4)); // readable again by the time the server has refused
        await vm.DeleteCommand.ExecuteAsync();

        var toast = rig.Toasts.Toasts.Single();
        // Released 2.1.0: "Donor 'Alma Sandoval' can't be changed because it is in use by other records."
        Assert.Equal("Donor 'Alma Sandoval' can't be deleted: they have appointments or donations on record, and that history is kept. Nothing was changed.", toast.Message);
        Assert.Equal(2, vm.Detail?.Id); // the donor is still in the panel and in the list
        Assert.Single(vm.Items);

        // The donor and the count are read again after the refusal: Delete is no longer offered, and the reason is under it. The list is not reloaded.
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.Equal("A donor with appointments or donations on record can't be deleted: their history is kept.", vm.DeleteBlockedReason);
        Assert.Equal(2, rig.Recorder.CountOf("GetDonorAsync"));
        Assert.Equal(1, rig.Recorder.CountOf("GetDonorsAsync"));
    }

    [Fact]
    public async Task Donors_WithAServerThatNeverAnswersTheCountOfAppointments_ThePanelWaits_AndCancelStopsThatCallToo()
    {
        var rig = DonorRig(Page<AppointmentListItem>(0));
        CancellationToken seen = default;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Fake.On<PagedResult<AppointmentListItem>>("GetAppointmentsAsync", async ct =>
        {
            seen = ct;
            asked.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        var vm = rig.Donors();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await asked.Task;

        Assert.True(vm.ShowDetailSkeleton); // the panel is still loading: the count is part of the record's load, inside its operation
        Assert.False(vm.DeleteCommand.CanExecute(null)); // nothing to delete until the record is in the panel
        vm.CancelDetailCommand.Execute(null);
        await vm.PendingDetail;

        Assert.True(seen.IsCancellationRequested, "Cancel did not stop the call that counts the donor's appointments");
        Assert.True(vm.HasDetailError);
        Assert.Equal("The request was cancelled.", vm.DetailError);
    }

    [Fact]
    public async Task AScreenWithoutADeleteRule_KeepsItsOwnRefusal_AndMakesNoCallForIt()
    {
        // Requests: its inherited Delete is the cancel call; a refused one (409) is not "records refer to it", and nothing is read again because of it.
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", Page(1, Samples2.RequestRow(7, 3, "Omar Haddad")));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([]));
        rig.Fake.Handlers["CancelRequestAsync"] = _ => Task.FromResult<object>(ApiResult.Fail(new ApiProblem(409, ProblemCodes.Conflict, "Only a pending request can be cancelled.")));
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;
        var before = rig.Recorder.Calls.Count;

        await vm.DeleteCommand.ExecuteAsync();

        Assert.Equal(["CancelRequestAsync"], rig.Recorder.Calls.Skip(before).Select(c => c.Name)); // released in 98f5b1a: it also read the request again
        Assert.Null(vm.DeleteBlockedReason);
        Assert.True(vm.DeleteCommand.CanExecute(null));
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

    // ---- 19. Fulfil is offered only when the server will issue the request ---------------------------------------------------------------

    private static FulfilPreview Preview(int units, int available) =>
        new(units, available, [.. Enumerable.Range(1, Math.Min(units, available)).Select(i => new SuggestedBag(200 + i, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 12, 1), 4, "Bianca Wainwright"))]);

    [Fact]
    public async Task Requests_StockCoversOnlyPartOfTheRequest_FulfilIsNotOffered_AndThePanelSaysHowManyUnitsAreShort()
    {
        var rig = RequestRig(inStock: 2);
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.Equal(CoverKind.Partial, vm.DetailCover?.Kind);
        Assert.Equal("1 short", vm.DetailCover?.Text);
        Assert.False(vm.FulfilCommand.CanExecute(null)); // released 2.1.0: offered, then refused by the server (409 insufficient_stock)
        Assert.Equal("Only 2 of the 3 units of A− needed are in stock, 1 unit short. A request is issued in full, so it stays open until more arrives.", vm.FulfilHoldReason);
        Assert.True(vm.CancelRequestCommand.CanExecute(null));
        Assert.Equal(0, rig.Recorder.CountOf("GetFulfilPreviewAsync"));

        // The third unit arrives: Fulfil is offered.
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 3, 0, 10, false)]));
        await vm.RefreshAsync();
        Assert.Null(vm.FulfilHoldReason);
        Assert.True(vm.FulfilCommand.CanExecute(null));
    }

    [Fact]
    public async Task FulfilStep_WhenTheStockIsShort_DoesNotOfferFulfil_SaysHowManyUnitsAreShort_AndDoesNotCallTheBagsBagsToIssue()
    {
        // The step can still open on a short request: the stock went down after the list was read, or the list could not read the stock at all.
        var rig = new Part2Rig();
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 2, 0, 10, true)]));
        rig.Fake.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(Preview(3, 2)));
        using var step = new FulfilRequestDialogViewModel(rig.Api, Samples2.Request(7));
        await step.PendingStock;
        await step.PendingBags;

        Assert.True(step.StockIsShort);
        Assert.False(step.PrimaryCommand.CanExecute(null)); // released 2.1.0: enabled, with two "bags to issue"
        Assert.Equal("Only 2 of the 3 units of A− needed are in stock, 1 unit short. A request is issued in full, so it stays open until more arrives.", step.ShortReason);
        Assert.Equal("Bags in stock", step.BagsLabel);
        Assert.Equal("Not enough to issue. In stock now, oldest first:", step.BagsNote); // released 2.1.0: "Only 2 units can be issued; 1 unit short."
        Assert.Equal(2, step.Bags.Count);
        Assert.Equal("In stock: 2 units of A−", step.StockText);
        Assert.True(step.CancelCommand.CanExecute(null));
        Assert.Equal(0, rig.Recorder.CountOf("FulfilRequestAsync"));
    }

    [Fact]
    public async Task FulfilStep_WhenTheStockCoversTheRequest_OffersFulfil_AndListsTheBagsToIssue()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 5, 0, 10, false)]));
        rig.Fake.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(Preview(3, 5)));
        using var step = new FulfilRequestDialogViewModel(rig.Api, Samples2.Request(7));
        await step.PendingStock;
        await step.PendingBags;

        Assert.False(step.StockIsShort);
        Assert.Null(step.ShortReason);
        Assert.True(step.PrimaryCommand.CanExecute(null));
        Assert.Equal("Bags to issue", step.BagsLabel);
        Assert.Equal("These bags are issued, oldest first:", step.BagsNote);
        Assert.Equal(3, step.Bags.Count);
    }

    [Fact]
    public async Task FulfilStep_WhenTheServerRefusesBecauseTheStockWentDown_SaysNothingWasIssued_ShowsWhatIsShortNow_AndStopsOfferingFulfil()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 3, 0, 10, false)]));
        rig.Fake.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(Preview(3, 3)));
        rig.Fake.On("FulfilRequestAsync", FakeApiClient.Fail<ReceiptDetail>(409, ProblemCodes.InsufficientStock, "There is not enough unexpired blood in stock for this request."));
        using var step = new FulfilRequestDialogViewModel(rig.Api, Samples2.Request(7));
        step.Bind(null, rig.Clock, rig.Dialogs);
        await step.PendingBags;
        Assert.True(step.PrimaryCommand.CanExecute(null));

        // Someone else issued a bag in the meantime: the server refuses, and the step reads the stock again.
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 2, 0, 10, true)]));
        rig.Fake.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(Preview(3, 2)));
        await step.PrimaryCommand.ExecuteAsync();
        await step.PendingStock;
        await step.PendingBags;

        Assert.False(step.Completed);
        Assert.Equal("Nothing was issued: there is no longer enough A− in stock for the 3 units this request needs.", step.FormError);
        Assert.Equal("Only 2 of the 3 units of A− needed are in stock, 1 unit short. A request is issued in full, so it stays open until more arrives.", step.ShortReason);
        Assert.False(step.PrimaryCommand.CanExecute(null));
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
