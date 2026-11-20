using Lifestream.Client.Api;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// Real-app walk 6: the bell's accessible name said "7 blood groups are below their minimum" while four things needed attention. Before the
/// list has been read the shell knows only that stock is low: it then says "at least 1 item", and the count once the list is known.
/// </summary>
public sealed class BellNameTests
{
    [Fact]
    public async Task BellName_BeforeTheListIsRead_NeverDescribesOneItemAsTheWholeList_AndGivesTheCountOnceItIsKnown()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Staff);
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        rig.Api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 50)));
        rig.Api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 0, 1, 50)));
        using var shell = rig.Get<ShellViewModel>();
        Assert.Equal("Notifications", shell.NotificationsSummary);

        shell.LowStockGroups = 7; // what the sidebar's count refresh learns, on any screen
        Assert.False(shell.Notifications.IsKnown);
        Assert.True(shell.HasNotifications);
        Assert.Equal("Notifications, at least 1 item needs attention", shell.NotificationsSummary); // released 2.1.2: "Notifications, 7 blood groups are below their minimum"

        await shell.Notifications.ToggleCommand.ExecuteAsync();
        await shell.Notifications.PendingLoad;
        Assert.True(shell.Notifications.IsKnown);
        Assert.Equal($"Notifications, {shell.Notifications.Items.Count} items need attention", shell.NotificationsSummary);
        Assert.True(shell.Notifications.Items.Count >= 2);
    }
}
