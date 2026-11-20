using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// Real-app walk 22: after a request was added, the sidebar still said "Requests 40" beside a page saying 41 open; the counts were only read
/// again at a navigation, and at most every 30 seconds. A record that was just added, fulfilled, cancelled, deleted or restored says so with a
/// success toast, on any screen: the shell reads its counts again when one appears.
/// </summary>
public sealed class SidebarCountRefreshTests
{
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2500 && !condition(); i++)
        {
            await Task.Delay(2);
        }

        Assert.True(condition(), "the condition was not reached in 5 s");
    }

    [Fact]
    public async Task SidebarCounts_AreReadAgain_AsSoonAsARecordIsAddedFulfilledOrCancelled_NotAtTheNextNavigation()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Admin);
        var open = 40;
        rig.Api.RequestsFor = q => q.PageSize == 1 ? ApiResult<PagedResult<RequestListItem>>.Ok(new([], open, 1, 1)) : null;
        rig.Api.AppointmentsFor = q => q.PageSize == 1 ? ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 5, 1, 1)) : null;
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.OPos, 12, 0, 10, false)]));
        using var shell = rig.Get<ShellViewModel>();
        await shell.StartAsync();
        var requests = shell.Items.Single(i => i.Title == "Requests");
        await UntilAsync(() => requests.Badge == "40");
        var toasts = rig.Get<IToastService>();

        // A request is added (what the Requests screen does: the call, then the toast).
        open = 41;
        toasts.Show(ToastKind.Success, "Request for 'Yusuf Birch' added.");
        await UntilAsync(() => requests.Badge == "41"); // released 2.1.2: still 40, until a navigation more than 30 s later

        // Fulfilled, then one cancelled.
        open = 40;
        toasts.Show(ToastKind.Success, "Request for 'Yusuf Birch' fulfilled.");
        await UntilAsync(() => requests.Badge == "40");
        Assert.Equal("Requests, 40 open", requests.AutomationName);

        // A message that reports no change reads nothing.
        var calls = rig.Api.CountOf("GetStockAsync");
        toasts.Show(ToastKind.Info, "Nobody is booked for the rest of today.");
        toasts.Show(ToastKind.Error, "The request could not be saved.");
        await Task.Delay(150);
        Assert.Equal(calls, rig.Api.CountOf("GetStockAsync"));
    }
}
