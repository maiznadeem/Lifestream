using Lifestream.Client.Api;
using Lifestream.Client.Common;
using Lifestream.Client.Features.Appointments;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Client.Tests.Features;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests;

/// <summary>
/// "Today" outside the Appointments screen (the dashboard's panel, the sidebar count, Ctrl+D) is the user's local day. The server's <c>fromUtc</c>/<c>toUtc</c> are
/// whole UTC days, so a query with them alone returns two days to anyone; these places name the Today view with their UTC offset as well, and drop what an older
/// server, which does not know the view, returns outside the day.
/// </summary>
public sealed class LocalDayTests
{
    /// <summary>08:42 UTC on 19 November 2026, seen from a zone <paramref name="hours"/> from UTC.</summary>
    private sealed class ZonedClock(double hours) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 8, 42, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone { get; } = hours == 0 ? TimeZoneInfo.Utc : TimeZoneInfo.CreateCustomTimeZone($"Zone{hours}", TimeSpan.FromHours(hours), "Zone", "Zone");
    }

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 11, day, hour, minute, 0, DateTimeKind.Utc);

    private static AppointmentListItem Appointment(int id, DateTime slotUtc, AppointmentStatus status = AppointmentStatus.Pending) =>
        new(id, 100 + id, $"Donor {id}", BloodType.OPos, slotUtc.AddDays(-3), slotUtc, status);

    [Theory]
    [InlineData(2, 18, 22, 120)] // 10:42 on the 19th there: the day began at 22:00 UTC on the 18th
    [InlineData(0, 19, 0, 0)]
    [InlineData(-8, 19, 8, -480)] // 00:42 on the 19th there
    [InlineData(13, 18, 11, 780)] // 21:42 on the 19th there: the day began at 11:00 UTC on the 18th
    [InlineData(5.75, 18, 18, 345)] // a quarter-hour zone: the day began at 18:15 UTC
    public void LocalDay_IsTheClocksCalendarDay_AsAUtcRange_WithItsOffset(double zoneHours, int startDay, int startHour, int offsetMinutes)
    {
        var day = LocalDay.Of(new ZonedClock(zoneHours));

        var start = Utc(startDay, startHour).AddMinutes(zoneHours == 5.75 ? 15 : 0);
        Assert.Equal(start, day.StartUtc);
        Assert.Equal(start.AddDays(1), day.EndUtc);
        Assert.Equal(offsetMinutes, day.UtcOffsetMinutes);
        Assert.True(day.StartUtc <= Utc(19, 8, 42) && Utc(19, 8, 42) < day.EndUtc);

        // Half-open: the day's first instant belongs to it, the next day's first does not.
        Assert.True(day.Holds(Appointment(1, day.StartUtc)));
        Assert.True(day.Holds(Appointment(2, day.EndUtc.AddMinutes(-30))));
        Assert.False(day.Holds(Appointment(3, day.EndUtc)));
        Assert.False(day.Holds(Appointment(4, day.StartUtc.AddMinutes(-30))));
    }

    [Fact]
    public void LocalDay_Query_NamesTheTodayViewAndTheOffset_BesideTheRangeAnOlderServerUnderstands()
    {
        var day = LocalDay.Of(new ZonedClock(2));

        var query = day.Query(AppointmentStatus.Pending, pageSize: 1, sort: SortKeys.Slot);

        Assert.Equal(AppointmentView.Today, query.View);
        Assert.Equal(120, query.UtcOffsetMinutes);
        Assert.Equal(Utc(18, 22), query.FromUtc);
        Assert.Equal(Utc(19, 22), query.ToUtc);
        Assert.Equal(AppointmentStatus.Pending, query.Status);
        Assert.Equal(1, query.PageSize);
        Assert.Equal(SortKeys.Slot, query.Sort);
        Assert.Null(day.Query().Status);
        Assert.Equal(50, day.Query().PageSize);
    }

    [Fact]
    public async Task SidebarCount_AsksForTheTodayViewOfTheLocalDay_PendingOnly()
    {
        using var rig = new Rig();
        rig.SignIn(Role.Admin);
        AppointmentQuery? asked = null;
        rig.Api.AppointmentsFor = q =>
        {
            if (q.PageSize != 1)
            {
                return null;
            }

            asked = q;
            return ApiResult<PagedResult<AppointmentListItem>>.Ok(new([], 3, 1, 1));
        };
        rig.Api.RequestsFor = q => q.PageSize == 1 ? ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 1)) : null;
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([]));
        var shell = rig.Get<ShellViewModel>();
        shell.Attach(null, new ZonedClock(2));
        await shell.StartAsync();
        await shell.RefreshCountsAsync();

        Assert.NotNull(asked);
        Assert.Equal(AppointmentView.Today, asked.View);
        Assert.Equal(120, asked.UtcOffsetMinutes);
        Assert.Equal(AppointmentStatus.Pending, asked.Status);
        Assert.Equal((Utc(18, 22), Utc(19, 22)), (asked.FromUtc, asked.ToUtc));
        Assert.Equal("3", shell.Items.Single(i => i.Title == "Appointments").Badge);
    }

    [Fact]
    public async Task Dashboard_TodaysAppointments_AreTheLocalDayOnly_WhateverTheServerReturnsAroundIt()
    {
        // What fromUtc/toUtc alone return for a clinic two hours ahead of UTC: the whole of 18 and 19 November UTC.
        AppointmentListItem[] twoUtcDays =
        [
            Appointment(1, Utc(18, 10), AppointmentStatus.Completed), // yesterday there
            Appointment(2, Utc(18, 21, 30), AppointmentStatus.Completed), // 23:30 yesterday there
            Appointment(3, Utc(18, 22), AppointmentStatus.Completed), // 00:00 today there
            Appointment(4, Utc(19, 9)),
            Appointment(5, Utc(19, 21, 30)), // 23:30 today there
            Appointment(6, Utc(19, 22)), // 00:00 tomorrow there
            Appointment(7, Utc(19, 12), AppointmentStatus.Cancelled),
        ];
        var rig = new FeatureRig();
        rig.Fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        rig.Fake.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new(twoUtcDays, twoUtcDays.Length, 1, 50)));
        var vm = new DashboardViewModel(rig.Api, new FakeNavigation());
        vm.Attach(null, new ZonedClock(2));

        await vm.LoadAsync();

        Assert.Equal([3, 4, 5], vm.Today.Select(a => a.Id));
        Assert.Equal(["00:00", "11:00", "23:30"], vm.Today.Select(a => a.Time));
        Assert.Equal("1 of 3 seen", vm.TodaySeenText);
        var asked = rig.Recorder.LastArg<AppointmentQuery>("GetAppointmentsAsync");
        Assert.Equal(AppointmentView.Today, asked.View);
        Assert.Equal(120, asked.UtcOffsetMinutes);
        Assert.Equal((Utc(18, 22), Utc(19, 22)), (asked.FromUtc, asked.ToUtc));
        Assert.Null(asked.Status);
    }

    [Fact]
    public async Task RecordDonationShortcut_DoesNotOpenAnAppointmentOfAnotherDay()
    {
        // Only yesterday's and tomorrow's pending appointments come back (an older server's whole UTC days): nobody is left for today.
        AppointmentListItem[] otherDays = [Appointment(1, Utc(18, 10)), Appointment(2, Utc(19, 22, 30))];
        using var rig = new Rig();
        rig.SignIn(Role.Admin);
        AppointmentQuery? asked = null;
        rig.Api.AppointmentsFor = q =>
        {
            if (q is { PageSize: 50, Status: AppointmentStatus.Pending, FromUtc: not null })
            {
                asked = q;
                return ApiResult<PagedResult<AppointmentListItem>>.Ok(new(otherDays, otherDays.Length, 1, 50));
            }

            return null;
        };
        var shell = rig.Get<ShellViewModel>();
        shell.Attach(null, new ZonedClock(2));
        await shell.StartAsync();

        await shell.RecordDonationCommand.ExecuteAsync();

        Assert.NotNull(asked);
        Assert.Equal(AppointmentView.Today, asked.View);
        Assert.Equal(120, asked.UtcOffsetMinutes);
        Assert.Contains(rig.Get<IToastService>().Toasts, t => t.Message.StartsWith("Nobody is booked for the rest of today", StringComparison.Ordinal));
        var page = Assert.IsType<AppointmentsViewModel>(shell.CurrentPage);
        Assert.Null(page.SelectedItem);
    }
}
