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
/// The sidebar badges (design-qa-2 N5) are the shell's own counts, the same on every screen: Appointments is today's pending appointments (the whole local day),
/// Requests the open requests, and Stock's alert shows whenever a blood group is below its minimum.
/// </summary>
public sealed class ShellBadgeTests
{
    /// <summary>08:42 UTC on 19 November 2026, in a clinic two hours ahead of UTC (10:42 local).</summary>
    private sealed class ClinicClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 8, 42, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Clinic+2", TimeSpan.FromHours(2), "Clinic", "Clinic");
    }

    [Fact]
    public async Task Badges_OpenRequests_AppointmentsPendingToday_AndTheStockAlert_AreTheShellsOwnCounts()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Admin);
        AppointmentQuery? appointmentsAsked = null;
        RequestQuery? requestsAsked = null;
        rig.Api.AppointmentsFor = q =>
        {
            if (q.PageSize != 1)
            {
                return null; // the dashboard's own list
            }

            appointmentsAsked = q;
            return ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 4, 1, 1));
        };
        rig.Api.RequestsFor = q =>
        {
            if (q.PageSize != 1)
            {
                return null;
            }

            requestsAsked = q;
            return ApiResult<PagedResult<RequestListItem>>.Ok(new([], 6, 1, 1));
        };
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ONeg, 0, 0, 10, true), new StockItem(BloodType.OPos, 12, 0, 10, false)]));
        var shell = rig.Get<ShellViewModel>();
        shell.Attach(null, new ClinicClock());
        await shell.StartAsync();
        await shell.RefreshCountsAsync();

        // Today's pending appointments: the whole local day (from local midnight, not from now), pending only.
        Assert.NotNull(appointmentsAsked);
        Assert.Equal(AppointmentStatus.Pending, appointmentsAsked.Status);
        Assert.Equal(new DateTime(2026, 11, 18, 22, 0, 0, DateTimeKind.Utc), appointmentsAsked.FromUtc);
        Assert.Equal(new DateTime(2026, 11, 19, 22, 0, 0, DateTimeKind.Utc), appointmentsAsked.ToUtc);
        Assert.Null(appointmentsAsked.DonorId);

        // Open requests: every pending request (what the Requests page's "Open" tab counts), for no patient in particular.
        Assert.NotNull(requestsAsked);
        Assert.Equal(RequestStatus.Pending, requestsAsked.Status);
        Assert.Null(requestsAsked.PatientId);

        var items = shell.Items.ToDictionary(i => i.Title);
        Assert.Equal("4", items["Appointments"].Badge);
        Assert.Equal("Appointments, 4 pending today", items["Appointments"].AutomationName);
        Assert.Equal("6", items["Requests"].Badge);
        Assert.Equal("Requests, 6 open", items["Requests"].AutomationName);
        Assert.Equal("1", items["Stock"].Badge);
        Assert.True(items["Stock"].HasAlert);
        Assert.False(items["Appointments"].HasAlert);
        Assert.False(items["Requests"].HasAlert);
        Assert.Equal(1, shell.LowStockGroups);

        // The alert follows the stock, whichever screen is open: once no group is below its minimum it goes.
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ONeg, 11, 0, 10, false)]));
        await shell.RefreshCountsAsync();
        Assert.Null(items["Stock"].Badge);
        Assert.False(items["Stock"].HasAlert);
        Assert.Equal(0, shell.LowStockGroups);
    }

    [Fact]
    public async Task Badges_FromTheCountsTheShellStartsWith_AreAllInPlace_WithoutAnotherRefresh()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Admin);
        rig.Api.AppointmentsFor = q => q.PageSize == 1 ? ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 4, 1, 1)) : null;
        rig.Api.RequestsFor = q => q.PageSize == 1 ? ApiResult<PagedResult<RequestListItem>>.Ok(new([], 6, 1, 1)) : null;
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ONeg, 0, 0, 10, true)]));
        var shell = rig.Get<ShellViewModel>();
        shell.Attach(null, new ClinicClock());
        await shell.StartAsync();
        var items = shell.Items.ToDictionary(i => i.Title);
        for (var i = 0; i < 200 && items["Requests"].Badge is null; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // The first counts, started with the shell: every badge, the Stock alert included (design-qa-2 N5: it showed only after a later refresh).
        Assert.Equal(("4", "6", "1"), (items["Appointments"].Badge, items["Requests"].Badge, items["Stock"].Badge));
        Assert.True(items["Stock"].HasAlert);
    }
}
