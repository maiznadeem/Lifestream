using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Health;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The demo clinic of the direction A mock-ups (scratch/design/_tools/p3.html): Thursday 19 November 2026, 10:42, Hana Greenleaf signed in. The same
/// numbers as the target images, served by the fake API, so the renders can be compared with A-login.png, A-dashboard.png and A-dashboard-dark.png.
/// Every block of the dashboard is computed by the view models from these API answers; nothing is drawn from the mock directly.
/// </summary>
public static class DesignData
{
    /// <summary>10:42 on the day of the mock-ups, in UTC; the clock's local zone is UTC so the renders do not depend on the machine.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 42, 0, TimeSpan.Zero);

    private static readonly int[] Donated = [20, 14, 14, 21, 19, 31, 21, 27, 19, 21, 29, 12];
    private static readonly int[] Issued = [7, 7, 8, 6, 9, 12, 6, 14, 13, 8, 12, 8];

    public static SessionUser User => new(7, "Hana Greenleaf", Role.Admin, false, true);

    /// <summary>The design clinic's stock: six of eight groups below the minimum of 10, two of them empty, and eight units of A+ expiring.</summary>
    public static IReadOnlyList<StockItem> Stock() =>
    [
        .. new (BloodType Type, int Units, int Expiring)[]
        {
            (BloodType.APos, 17, 8), (BloodType.ANeg, 3, 0), (BloodType.BPos, 1, 0), (BloodType.BNeg, 1, 0),
            (BloodType.ABPos, 4, 0), (BloodType.ABNeg, 0, 0), (BloodType.OPos, 11, 0), (BloodType.ONeg, 0, 0),
        }.Select(s => new StockItem(s.Type, s.Units, s.Expiring, 10, s.Units < 10)),
    ];

    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var months = Enumerable.Range(0, 12).Select(i => new DateOnly(2025, 12, 1).AddMonths(i)).ToList();
        var series = BloodTypeExtensions.All.Select(t => t == BloodType.APos ? new BloodTypeSeries(t, Donated, Issued) : new BloodTypeSeries(t, new int[12], new int[12])).ToList();
        var stock = Stock();

        api.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(new DashboardResponse(
            new DashboardKpis(40, 35, 5, 40, 37, 8), months, series, [new GenderCount(Gender.Male, 18), new GenderCount(Gender.Female, 20), new GenderCount(Gender.Other, 2)],
            [.. stock.Where(s => s.BelowThreshold)])));
        api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(stock));

        var today = new (string Time, int Donor, string Name, BloodType Group, AppointmentStatus Status)[]
        {
            ("09:00", 201, "Saoirse Yildiz", BloodType.OPos, AppointmentStatus.Completed),
            ("09:30", 122, "Lior Underhill", BloodType.APos, AppointmentStatus.Completed),
            ("10:00", 131, "Imani Novak", BloodType.ANeg, AppointmentStatus.Completed),
            ("11:00", 149, "Asher Devereux", BloodType.APos, AppointmentStatus.Pending),
            ("11:30", 121, "Gideon Achterberg", BloodType.ONeg, AppointmentStatus.Pending),
            ("12:00", 137, "Elio Bergström", BloodType.ABPos, AppointmentStatus.Pending),
            ("14:00", 198, "Anders Wójcik", BloodType.OPos, AppointmentStatus.Pending),
            ("15:30", 130, "Hana Lindqvist", BloodType.ABNeg, AppointmentStatus.Pending),
        };
        var appointments = today.Select((a, i) =>
        {
            var parts = a.Time.Split(':');
            var slot = new DateTime(2026, 11, 19, int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), 0, DateTimeKind.Utc);
            // Elio Bergström (137) has not donated before: the list says so itself, which is where the dashboard reads it.
            return new AppointmentListItem(900 + i, a.Donor, a.Name, a.Group, slot.AddDays(-5), slot, a.Status) { DonorFirstTime = a.Donor == 137 };
        }).ToList();
        api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new PagedResult<AppointmentListItem>(appointments, appointments.Count, 1, 50)));

        var requests = new List<RequestListItem>
        {
            new(148, 51, "Petra Iversen", BloodType.ABPos, 3, Now.UtcDateTime.AddDays(-3), new DateOnly(2026, 11, 21), RequestStatus.Pending),
            new(141, 52, "Talia Yildiz", BloodType.APos, 3, Now.UtcDateTime.AddDays(-4), new DateOnly(2026, 11, 24), RequestStatus.Pending),
            new(139, 53, "Noor Fairbanks", BloodType.ONeg, 1, Now.UtcDateTime.AddDays(-2), new DateOnly(2026, 11, 25), RequestStatus.Pending),
            new(136, 54, "Nadia Holloway", BloodType.BNeg, 2, Now.UtcDateTime.AddDays(-2), new DateOnly(2026, 11, 26), RequestStatus.Pending),
            new(150, 55, "Quinn Crowhurst", BloodType.OPos, 3, Now.UtcDateTime.AddDays(-1), new DateOnly(2026, 12, 3), RequestStatus.Pending),
        };
        api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new PagedResult<RequestListItem>(requests, requests.Count, 1, 50)));

        // The shell's counts for the sidebar (one row asked for, only the total read): the clinic's open requests, as the Requests page's "Open" tab counts them,
        // and its appointments pending today. Any other query gets the screen's own answer.
        api.RequestsFor = q => q is { PageSize: 1, Status: RequestStatus.Pending, PatientId: null, View: null }
            ? ApiResult<PagedResult<RequestListItem>>.Ok(new PagedResult<RequestListItem>([], RequestDesignData.In(RequestView.Open).Count, 1, 1))
            : null;
        api.AppointmentsFor = q => q is { PageSize: 1, Status: AppointmentStatus.Pending, DonorId: null, View: null, FromUtc: { } from, ToUtc: { } to }
            ? ApiResult<PagedResult<AppointmentListItem>>.Ok(new PagedResult<AppointmentListItem>([], appointments.Count(a => a.Status == AppointmentStatus.Pending && a.SlotStartUtc >= from && a.SlotStartUtc < to), 1, 1))
            : null;

        api.On("GetProfileAsync", ApiResult<ProfileResponse>.Ok(new ProfileResponse(7, "Hana Greenleaf", "hana.greenleaf", Designation.Doctor, Shift.Morning, "+1-555-0100", false, "rv")));
        api.On("GetReadinessAsync", ApiResult<ReadinessResponse>.Ok(new ReadinessResponse(DatabaseStates.Ready)));
    }
}

/// <summary>A clock stopped at <see cref="DesignData.Now"/>, in UTC.</summary>
public sealed class DesignClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DesignData.Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
