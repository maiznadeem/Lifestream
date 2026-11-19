using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Stock;
using Lifestream.Contracts.Patients;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.RedesignA;

/// <summary>
/// The donors of the direction A mock-up (scratch/design/A-donors.png) on Thursday 19 November 2026: the same names, groups and last-donation dates, so the renders of the
/// list screens can be compared with the target. Eligibility is worked out by the view models from these answers.
/// </summary>
public static class ListScreensDesignData
{
    public static readonly DateOnly Today = new(2026, 11, 19);

    private static readonly (int Id, string Name, BloodType Group, string? Last, string Phone)[] Donors =
    [
        (175, "Adrian Mukherjee", BloodType.OPos, "2026-07-29", "+1-555-0175"),
        (198, "Anders Wójcik", BloodType.OPos, "2026-08-20", "+1-555-0198"),
        (149, "Asher Devereux", BloodType.APos, "2026-08-03", "+1-555-0149"),
        (144, "Bianca Whitlock", BloodType.BNeg, "2026-10-31", "+1-555-0144"),
        (142, "Camila Merriweather", BloodType.APos, "2026-10-31", "+1-555-0142"),
        (191, "Camila Oyelaran", BloodType.APos, "2026-03-12", "+1-555-0191"),
        (135, "Caspian Rosenthal", BloodType.APos, "2026-09-29", "+1-555-0135"),
        (197, "Celeste Brightwater", BloodType.OPos, "2026-11-08", "+1-555-0197"),
        (114, "Chiara Birch", BloodType.OPos, "2026-09-08", "+1-555-0114"),
        (100, "Chiara Cavanaugh", BloodType.APos, "2026-11-15", "+1-555-0100"),
        (137, "Elio Bergström", BloodType.ABPos, null, "+1-555-0137"),
        (162, "Farid Ulloa", BloodType.BPos, "2026-07-06", "+1-555-0162"),
        (154, "Felix Rutherford", BloodType.APos, "2026-10-27", "+1-555-0154"),
        (118, "Fenna Galloway", BloodType.APos, "2026-10-25", "+1-555-0118"),
        (121, "Gideon Achterberg", BloodType.ONeg, "2026-05-02", "+1-555-0121"),
        (130, "Hana Lindqvist", BloodType.ABNeg, "2026-04-18", "+1-555-0130"),
    ];

    public static DonorListItem DonorRow(int index)
    {
        var d = Donors[index];
        var born = new DateOnly(1984 - (index * 3 % 20), 1 + (index * 5 % 12), 1 + (index * 7 % 27));
        return new DonorListItem(d.Id, d.Name, born, index % 2 == 0 ? Gender.Male : Gender.Female, d.Group, d.Phone, index % 5 == 4 ? DonorFrequency.Occasional : DonorFrequency.Regular,
            d.Last is null ? null : DateOnly.Parse(d.Last, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Adrian Mukherjee, the donor selected in the mock-up.</summary>
    public static DonorDetail Adrian => new(175, "Adrian Mukherjee", new DateOnly(1984, 10, 16), Gender.Male, BloodType.OPos, "+1-555-0175", "246 Harbour Road, Northgate",
        DonorFrequency.Regular, new DateOnly(2026, 7, 29), "rv");

    public static void ConfigureDonors(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var rows = Enumerable.Range(0, Donors.Length).Select(DonorRow).ToList();
        api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new PagedResult<DonorListItem>(rows, 40, 1, 50)));
        api.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Adrian));
        api.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new DonorEligibility(true, [], null)));
    }
}

public static class PatientDesignData
{
    private static readonly (int Id, string Name, BloodType Group, int Pending, string Phone)[] Patients =
    [
        (51, "Petra Iversen", BloodType.ABPos, 1, "+1-555-0251"),
        (52, "Talia Yildiz", BloodType.APos, 1, "+1-555-0252"),
        (53, "Noor Fairbanks", BloodType.ONeg, 2, "+1-555-0253"),
        (54, "Nadia Holloway", BloodType.BNeg, 1, "+1-555-0254"),
        (55, "Quinn Crowhurst", BloodType.OPos, 1, "+1-555-0255"),
        (56, "Beatrix Farrow", BloodType.APos, 0, "+1-555-0256"),
        (57, "Elio Kingsley", BloodType.OPos, 0, "+1-555-0257"),
        (58, "Isolde Marchetti", BloodType.BPos, 0, "+1-555-0258"),
        (59, "Jonah Whitfield", BloodType.ANeg, 0, "+1-555-0259"),
        (60, "Leilani Okoro", BloodType.ABNeg, 0, "+1-555-0260"),
        (61, "Mateus Andrade", BloodType.OPos, 0, "+1-555-0261"),
        (62, "Saskia van Dijk", BloodType.APos, 0, "+1-555-0262"),
    ];

    public static PatientListItem Row(int index)
    {
        var p = Patients[index];
        return new PatientListItem(p.Id, p.Name, new DateOnly(1950 + (index * 7 % 50), 1 + (index * 5 % 12), 1 + (index * 3 % 27)), index % 2 == 0 ? Gender.Female : Gender.Male, p.Group, p.Phone, p.Pending);
    }

    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var rows = Enumerable.Range(0, Patients.Length).Select(Row).ToList();
        api.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Ok(new PagedResult<PatientListItem>(rows, 24, 1, 50)));
        api.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(new PatientDetail(53, "Noor Fairbanks", new DateOnly(1971, 6, 4), Gender.Female, BloodType.ONeg, "+1-555-0253",
            "18 Quarry Lane, Eastfield", "rv")));
    }
}

/// <summary>The appointments of the mock-up's dashboard (today, 19 November) with yesterday's and the next days' around them.</summary>
public static class AppointmentDesignData
{
    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var rows = new (int Day, int Hour, int Minute, int Donor, string Name, BloodType Group, AppointmentStatus Status)[]
        {
            (18, 10, 0, 162, "Farid Ulloa", BloodType.BPos, AppointmentStatus.Completed),
            (18, 14, 30, 118, "Fenna Galloway", BloodType.APos, AppointmentStatus.NoShow),
            (19, 9, 0, 201, "Saoirse Yildiz", BloodType.OPos, AppointmentStatus.Completed),
            (19, 9, 30, 122, "Lior Underhill", BloodType.APos, AppointmentStatus.Completed),
            (19, 10, 0, 131, "Imani Novak", BloodType.ANeg, AppointmentStatus.Completed),
            (19, 11, 0, 149, "Asher Devereux", BloodType.APos, AppointmentStatus.Pending),
            (19, 11, 30, 121, "Gideon Achterberg", BloodType.ONeg, AppointmentStatus.Pending),
            (19, 12, 0, 137, "Elio Bergström", BloodType.ABPos, AppointmentStatus.Pending),
            (19, 14, 0, 198, "Anders Wójcik", BloodType.OPos, AppointmentStatus.Pending),
            (19, 15, 30, 130, "Hana Lindqvist", BloodType.ABNeg, AppointmentStatus.Cancelled),
            (20, 9, 30, 175, "Adrian Mukherjee", BloodType.OPos, AppointmentStatus.Pending),
            (20, 13, 0, 191, "Camila Oyelaran", BloodType.APos, AppointmentStatus.Pending),
            (23, 10, 30, 114, "Chiara Birch", BloodType.OPos, AppointmentStatus.Pending),
        };
        var items = rows.Select((a, i) =>
        {
            var slot = new DateTime(2026, 11, a.Day, a.Hour, a.Minute, 0, DateTimeKind.Utc);
            return new AppointmentListItem(900 + i, a.Donor, a.Name, a.Group, slot.AddDays(-6), slot, a.Status);
        }).ToList();
        api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new PagedResult<AppointmentListItem>(items, items.Count, 1, 50)));
        var asher = items[5];
        api.On("GetAppointmentAsync", ApiResult<AppointmentDetail>.Ok(new AppointmentDetail(asher.Id, asher.DonorId, asher.DonorName, asher.DonorBloodType, asher.PlacedAtUtc, asher.SlotStartUtc,
            asher.Status, null, null, "rv")));
        api.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(new DonorDetail(149, "Asher Devereux", new DateOnly(1990, 3, 14), Gender.Male, BloodType.APos, "+1-555-0149", null,
            DonorFrequency.Regular, new DateOnly(2026, 8, 3), "rv")));
    }
}

/// <summary>Open requests of the mock-up (one needed today, one overdue, two within three days) and fulfilled ones.</summary>
public static class RequestDesignData
{
    public static readonly RequestDetail Petra = new(148, 51, "Petra Iversen", BloodType.ABPos, 3, new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 19),
        RequestStatus.Pending, null, "rv");

    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var placed = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        var items = new List<RequestListItem>
        {
            new(148, 51, "Petra Iversen", BloodType.ABPos, 3, placed, new DateOnly(2026, 11, 19), RequestStatus.Pending),
            new(133, 60, "Leilani Okoro", BloodType.ABNeg, 2, placed, new DateOnly(2026, 11, 17), RequestStatus.Pending),
            new(141, 52, "Talia Yildiz", BloodType.APos, 3, placed, new DateOnly(2026, 11, 20), RequestStatus.Pending),
            new(139, 53, "Noor Fairbanks", BloodType.ONeg, 1, placed, new DateOnly(2026, 11, 22), RequestStatus.Pending),
            new(136, 54, "Nadia Holloway", BloodType.BNeg, 2, placed, new DateOnly(2026, 11, 26), RequestStatus.Pending),
            new(150, 55, "Quinn Crowhurst", BloodType.OPos, 3, placed, new DateOnly(2026, 12, 3), RequestStatus.Pending),
            new(129, 56, "Beatrix Farrow", BloodType.APos, 1, placed, new DateOnly(2026, 11, 12), RequestStatus.Fulfilled),
            new(127, 57, "Elio Kingsley", BloodType.OPos, 2, placed, new DateOnly(2026, 11, 10), RequestStatus.Fulfilled),
            new(124, 58, "Isolde Marchetti", BloodType.BPos, 1, placed, new DateOnly(2026, 11, 8), RequestStatus.Cancelled),
        };
        api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new PagedResult<RequestListItem>(items, items.Count, 1, 50)));
        api.On("GetRequestAsync", ApiResult<RequestDetail>.Ok(Petra));
        api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ABPos, 4, 0, 10, true)]));
    }
}

/// <summary>A clock stopped at 10:42 on 19 November 2026, in UTC.</summary>
public sealed class ListScreensClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 10, 42, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
