using Lifestream.Client.Api;
using Lifestream.Client.Features.Dashboard;
using Lifestream.Client.Features.Requests;
using Lifestream.Client.Shell;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Features;

/// <summary>
/// The dashboard says what the screens it sends people to say (real-app walk 19, 7 and 21): a request the stock cannot cover is described by
/// the Requests screen's own rule and words, a request due today is "due today" in every line, and "Next" is the appointment that "Record
/// donation" opens.
/// </summary>
public sealed class DashboardAgreesTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 11, 20, 15, 27, 0, TimeSpan.Zero)); // a Friday
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static StockItem Stock(BloodType group, int available, int expiring = 0) => new(group, available, expiring, 10, available < 10);

    private static RequestListItem Request(int id, string patient, BloodType group, int units, DateOnly by) =>
        new(id, id, patient, group, units, new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), by, RequestStatus.Pending);

    [Fact]
    public void RequestThatItsOwnGroupCannotCover_IsDescribedInTheRequestsScreensWords_AndNeverOfferedForFulfilFromAnotherGroupsStock()
    {
        // 3 units of O− wanted, 2 O− in stock. (The walk's request: the Requests screen said "1 short" and its Fulfil failed with 409.)
        IReadOnlyList<StockItem> stock = [Stock(BloodType.ONeg, 2), Stock(BloodType.OPos, 30)];
        IReadOnlyList<RequestListItem> requests = [Request(7, "Nadia Jovanović", BloodType.ONeg, 3, Today.AddDays(1))];

        var items = AttentionBuilder.Build(stock, requests, [], Clock);

        var item = Assert.Single(items, i => i.ActionText == "Open request");
        Assert.Equal("Not enough O− for Nadia Jovanović", item.Title);
        Assert.Equal("1 short", RequestCoverage.Of(3, 2).Text); // the Requests screen's words...
        Assert.Equal("Nadia Jovanović needs 3 units by Sat 21 Nov: 2 units in stock, 1 short.", item.Detail); // ...are the dashboard's
        Assert.DoesNotContain(items, i => i.Detail.Contains("can be given", StringComparison.Ordinal)); // released 2.1.2: "; 2 units can be given."

        // A patient who is O+ could be given O−, but Fulfil issues the request's own group only: 2 O+ in stock cannot cover 3 O+, whatever O− there is.
        IReadOnlyList<StockItem> compatible = [Stock(BloodType.OPos, 2), Stock(BloodType.ONeg, 30)];
        IReadOnlyList<RequestListItem> oPositive = [Request(8, "Omar Haddad", BloodType.OPos, 3, Today)];

        var second = AttentionBuilder.Build(compatible, oPositive, [], Clock);

        Assert.DoesNotContain(second, i => i.ActionText == "Fulfil"); // released 2.1.2: "Fulfil" was offered, and the Requests screen then refused it
        Assert.Contains(second, i => i.ActionText == "Open request" && i.Detail.EndsWith("2 units in stock, 1 short.", StringComparison.Ordinal));
    }

    [Fact]
    public void RequestDueToday_IsDueToday_InEveryLineThatMentionsIt()
    {
        IReadOnlyList<StockItem> stock = [Stock(BloodType.OPos, 18, expiring: 6)];
        IReadOnlyList<RequestListItem> requests = [Request(9, "Nadia Blackwood", BloodType.OPos, 4, Today)];

        var items = AttentionBuilder.Build(stock, requests, [], Clock);

        Assert.Equal("Use them for Nadia Blackwood’s request (4 units, due today).", Assert.Single(items, i => i.ActionText == "Review bags").Detail); // released 2.1.2: "due Fri"
        Assert.Equal("Nadia Blackwood’s request is due today", Assert.Single(items, i => i.ActionText == "Fulfil").Title);

        IReadOnlyList<RequestListItem> tomorrow = [Request(9, "Nadia Blackwood", BloodType.OPos, 4, Today.AddDays(1))];
        var later = AttentionBuilder.Build(stock, tomorrow, [], Clock);
        Assert.Equal("Use them for Nadia Blackwood’s request (4 units, due tomorrow).", Assert.Single(later, i => i.ActionText == "Review bags").Detail);
        Assert.Equal("Nadia Blackwood’s request is due tomorrow", Assert.Single(later, i => i.ActionText == "Fulfil").Title);
    }

    private static AppointmentListItem Appointment(int id, int hour, int minute, AppointmentStatus status = AppointmentStatus.Pending)
    {
        var slot = new DateTime(2026, 11, 20, hour, minute, 0, DateTimeKind.Utc);
        return new(id, 100 + id, $"Donor {id}", BloodType.OPos, slot.AddDays(-3), slot, status);
    }

    [Fact]
    public async Task Next_OnTheDashboard_IsTheAppointmentThatRecordDonationOpens()
    {
        // 15:27. The 14:00 donor did not come; the 15:30 one is due. (The walk: the dashboard marked 14:00 "Next", Record donation opened 15:30.)
        AppointmentListItem[] today = [Appointment(1, 13, 0, AppointmentStatus.Completed), Appointment(2, 14, 0), Appointment(3, 15, 30), Appointment(4, 16, 0)];
        using var rig = new Rig(clock: Clock);
        rig.SignIn(Role.Admin);
        rig.Api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Samples.Dashboard()));
        rig.Api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Samples.Stock()));
        rig.Api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new([], 0, 1, 50)));
        rig.Api.AppointmentsFor = q =>
        {
            var rows = today.Where(a => q.Status is null || a.Status == q.Status).ToArray();
            return ApiResult<PagedResult<AppointmentListItem>>.Ok(new(rows, rows.Length, 1, 50));
        };
        var opened = new List<int>();
        rig.Api.ById["GetAppointmentAsync"] = id =>
        {
            lock (opened)
            {
                opened.Add(id);
            }

            return FakeApiClient.Fail<AppointmentDetail>(404, ProblemCodes.NotFound);
        };
        using var shell = rig.Get<ShellViewModel>();
        shell.Attach(null, Clock);

        await shell.OpenByTitleAsync("Dashboard");
        var dashboard = Assert.IsType<DashboardViewModel>(shell.CurrentPage);
        var next = Assert.Single(dashboard.Today, t => t.IsNext);
        await shell.RecordDonationCommand.ExecuteAsync();

        Assert.Equal(3, next.Id); // released 2.1.2: 2, the 14:00 appointment nobody came to
        Assert.Equal("15:30", next.Time);
        Assert.Equal([3], opened.Distinct()); // and Record donation opens the same one
        Assert.Equal(3, NextAppointment.Of(today, Clock.GetUtcNow().UtcDateTime)!.Id);

        // When everyone still pending is more than an hour late, the earliest of them is next: somebody still has to be seen.
        AppointmentListItem[] late = [Appointment(1, 9, 0), Appointment(2, 11, 0)];
        Assert.Equal(1, NextAppointment.Of(late, Clock.GetUtcNow().UtcDateTime)!.Id);
        Assert.Null(NextAppointment.Of([Appointment(1, 13, 0, AppointmentStatus.Completed)], Clock.GetUtcNow().UtcDateTime));
    }
}
