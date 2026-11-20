using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// Fulfil is not offered on a request whose cover is "No stock" (design-qa-2 N13): the action is disabled and a line says why, the request stays open until stock arrives.
/// The same holds for a request the stock covers only in part (real-app walk, item 19: the server issues a request in full or not at all, so its Fulfil was
/// offered and then refused); a request the stock covers keeps its Fulfil.
/// </summary>
public sealed class NoStockFulfilTests
{
    [Fact]
    public async Task Requests_NoStockOfTheGroup_FulfilIsDisabled_AndThePanelSaysWhy()
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3, "Omar Haddad")], 1, 1, 50)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 0, 0, 10, true), new StockItem(BloodType.OPos, 12, 0, 10, false)]));
        var vm = rig.Requests();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        Assert.Equal(CoverKind.NoStock, vm.DetailCover?.Kind);
        Assert.False(vm.FulfilCommand.CanExecute(null));
        Assert.Equal("No A− in stock. The request stays open until stock arrives.", vm.FulfilHoldReason);
        Assert.Contains(vm.PanelFacts, f => f.Label == "In stock" && f.Value == "0 units of A−, 3 needed"); // no "in stock … in stock"
        Assert.True(vm.CancelRequestCommand.CanExecute(null)); // the request itself stays open and can still be cancelled or edited

        // One unit arrives, of the three needed: still not offered (the server would refuse), and the line says how many are short.
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 1, 0, 10, true)]));
        await vm.RefreshAsync();
        Assert.Equal(CoverKind.Partial, vm.DetailCover?.Kind);
        Assert.Equal("Only 1 of the 3 units of A− needed is in stock, 2 units short. A request is issued in full, so it stays open until more arrives.", vm.FulfilHoldReason);
        Assert.False(vm.FulfilCommand.CanExecute(null));

        // The rest arrives: the next refresh offers Fulfil again.
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 3, 0, 10, true)]));
        await vm.RefreshAsync();
        Assert.Equal(CoverKind.CanFulfil, vm.DetailCover?.Kind);
        Assert.Null(vm.FulfilHoldReason);
        Assert.True(vm.FulfilCommand.CanExecute(null));
    }

    [Fact]
    public Task Requests_AFulfilAskedForByAnotherScreen_IsDropped_WhenThereIsNoStock() => UiThread.RunAsync(async () =>
    {
        var rig = new Part2Rig();
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([Samples2.RequestRow(7, 3, "Omar Haddad")], 1, 1, 50)));
        rig.Fake.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Samples2.Request(7)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ANeg, 0, 0, 10, true)]));
        var vm = rig.Requests(); // on the UI thread, as navigation makes it
        ((INavigationParameterReceiver)vm).ReceiveParameter(new RelatedRecord(RecordKind.Request, 7, "Omar Haddad"));
        await vm.LoadAsync();
        await vm.PendingFulfil; // the screen has decided: here, to drop the wish

        Assert.Equal(7, vm.Detail?.Id);
        Assert.Null(vm.Fulfilment); // nothing to fulfil with: the step does not open
        Assert.Equal(0, rig.Recorder.CountOf("GetFulfilPreviewAsync"));
        Assert.NotNull(vm.FulfilHoldReason);
    });

    [Fact]
    public async Task Patients_ARequestWithNoStock_OrTooLittle_HasItsFulfilDisabled_WithTheReason_AndOneTheStockCoversKeepsIt()
    {
        var rig = new FeatureRig();
        rig.Fake.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Ok(new([new PatientListItem(53, "Noor Fairbanks", new DateOnly(1971, 6, 4), Gender.Female, BloodType.ONeg, null, 2)], 1, 1, 50)));
        rig.Fake.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(new PatientDetail(53, "Noor Fairbanks", new DateOnly(1971, 6, 4), Gender.Female, BloodType.ONeg, null, null, "rv")));
        var placed = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        rig.Fake.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new(
            [
                new(139, 53, "Noor Fairbanks", BloodType.ONeg, 1, placed, rig.Today.AddDays(3), RequestStatus.Pending),
                new(152, 53, "Noor Fairbanks", BloodType.OPos, 2, placed, rig.Today.AddDays(11), RequestStatus.Pending),
                new(160, 53, "Noor Fairbanks", BloodType.APos, 2, placed, rig.Today.AddDays(12), RequestStatus.Pending),
            ], 3, 1, 10)));
        rig.Fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ONeg, 0, 0, 10, true), new StockItem(BloodType.OPos, 1, 0, 10, true), new StockItem(BloodType.APos, 2, 0, 10, true)]));
        var vm = rig.Patients();
        await vm.LoadAsync();
        vm.SelectedItem = vm.Items[0];
        await vm.PendingDetail;

        var none = vm.WaitingRequests[0];
        Assert.Equal(CoverKind.NoStock, none.Cover!.Kind);
        Assert.False(none.CanFulfil);
        Assert.Equal("No O− in stock. The request stays open until stock arrives.", none.HoldReason);
        Assert.False(vm.FulfilRequestCommand.CanExecute(none));

        // One unit of the two needed: the server would refuse the fulfil, so it is not offered either, and the card says how many are short.
        var some = vm.WaitingRequests[1];
        Assert.Equal(CoverKind.Partial, some.Cover!.Kind);
        Assert.False(some.CanFulfil);
        Assert.Equal("Only 1 of the 2 units of O+ needed is in stock, 1 unit short. A request is issued in full, so it stays open until more arrives.", some.HoldReason);
        Assert.False(vm.FulfilRequestCommand.CanExecute(some));

        var covered = vm.WaitingRequests[2];
        Assert.Equal(CoverKind.CanFulfil, covered.Cover!.Kind);
        Assert.True(covered.CanFulfil);
        Assert.Null(covered.HoldReason);
        Assert.True(vm.FulfilRequestCommand.CanExecute(covered));

        await vm.FulfilRequestCommand.ExecuteAsync(none);
        await vm.FulfilRequestCommand.ExecuteAsync(some);
        Assert.Empty(rig.Navigation.Visits); // even if asked directly, a request the stock does not cover is not opened to fulfil
    }
}
