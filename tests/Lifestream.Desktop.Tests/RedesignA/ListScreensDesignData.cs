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
/// The donors of the direction A mock-up (scratch/design/A-donors.png) on Thursday 19 November 2026: the same names, groups, last-donation dates and donation counts, so
/// the renders of the list screens can be compared with the target; then more donors so the first page is a full page of 50 of 118 (the pager shows three pages).
/// Eligibility is worked out by the view models from these answers. Test data only: the screens show what the API answers.
/// </summary>
public static class ListScreensDesignData
{
    public static readonly DateOnly Today = new(2026, 11, 19);

    /// <summary>How many donors the list says there are (the first page holds 50).</summary>
    public const int DonorTotal = 118;

    private static readonly (int Id, string Name, BloodType Group, string? Last, string Phone, int Donations)[] Donors =
    [
        (175, "Adrian Mukherjee", BloodType.OPos, "2026-07-29", "+1-555-0175", 11),
        (198, "Anders Wójcik", BloodType.OPos, "2026-08-20", "+1-555-0198", 6),
        (149, "Asher Devereux", BloodType.APos, "2026-08-03", "+1-555-0149", 4),
        (144, "Bianca Whitlock", BloodType.BNeg, "2026-10-31", "+1-555-0144", 9),
        (142, "Camila Merriweather", BloodType.APos, "2026-10-31", "+1-555-0142", 2),
        (191, "Camila Oyelaran", BloodType.APos, "2026-03-12", "+1-555-0191", 7),
        (135, "Caspian Rosenthal", BloodType.APos, "2026-09-29", "+1-555-0135", 3),
        (197, "Celeste Brightwater", BloodType.OPos, "2026-11-08", "+1-555-0197", 15),
        (114, "Chiara Birch", BloodType.OPos, "2026-09-08", "+1-555-0114", 5),
        (100, "Chiara Cavanaugh", BloodType.APos, "2026-11-15", "+1-555-0100", 8),
        (137, "Elio Bergström", BloodType.ABPos, null, "+1-555-0137", 0),
        (162, "Farid Ulloa", BloodType.BPos, "2026-07-06", "+1-555-0162", 12),
        (154, "Felix Rutherford", BloodType.APos, "2026-10-27", "+1-555-0154", 1),
        (118, "Fenna Galloway", BloodType.APos, "2026-10-25", "+1-555-0118", 3),
        (121, "Gideon Achterberg", BloodType.ONeg, "2026-05-02", "+1-555-0121", 6),
        (130, "Hana Lindqvist", BloodType.ABNeg, "2026-04-18", "+1-555-0130", 4),
    ];

    private static readonly string[] MoreFirst = ["Ines", "Jasper", "Kaito", "Lena", "Malik", "Nora", "Oskar", "Priya", "Rafael", "Sanne", "Tobias", "Uma", "Viktor", "Wren", "Yara", "Zeno", "Amara"];

    private static readonly string[] MoreLast = ["Halvorsen", "Ibarra", "Jansen", "Kowalczyk", "Lindgren"];

    private static readonly BloodType[] Groups = [BloodType.OPos, BloodType.APos, BloodType.BPos, BloodType.ONeg, BloodType.ABPos, BloodType.ANeg, BloodType.BNeg, BloodType.ABNeg];

    public static DonorListItem DonorRow(int index)
    {
        var born = new DateOnly(1984 - (index * 3 % 20), 1 + (index * 5 % 12), 1 + (index * 7 % 27));
        var gender = index % 2 == 0 ? Gender.Male : Gender.Female;
        var frequency = index % 5 == 4 ? DonorFrequency.Occasional : DonorFrequency.Regular;
        if (index < Donors.Length)
        {
            var d = Donors[index];
            return new DonorListItem(d.Id, d.Name, born, gender, d.Group, d.Phone, frequency, d.Last is null ? null : DateOnly.Parse(d.Last, System.Globalization.CultureInfo.InvariantCulture))
            {
                Donations = d.Donations,
            };
        }

        var n = index - Donors.Length;
        var id = 201 + n;
        var last = n % 6 == 5 ? (DateOnly?)null : Today.AddDays(-(20 + (n * 37 % 300)));
        return new DonorListItem(id, $"{MoreFirst[n % MoreFirst.Length]} {MoreLast[n % MoreLast.Length]}", born, gender, Groups[n % Groups.Length], $"+1-555-{id:0000}", frequency, last)
        {
            Donations = last is null ? 0 : 1 + (n * 5 % 14),
        };
    }

    /// <summary>Adrian Mukherjee, the donor selected in the mock-up.</summary>
    public static DonorDetail Adrian => new(175, "Adrian Mukherjee", new DateOnly(1984, 10, 16), Gender.Male, BloodType.OPos, "+1-555-0175", "246 Harbour Road, Northgate",
        DonorFrequency.Regular, new DateOnly(2026, 7, 29), "rv");

    /// <summary>Adrian's eleven donations, newest first, as in A-donors.png: where each bag went, and one that expired unused.</summary>
    public static DonorDonations AdrianDonations
    {
        get
        {
            var issuedTo = new (string Donated, string? Issued, int? PatientId, string? Patient)[]
            {
                ("2026-07-29", "2026-08-12", 55, "Quinn Crowhurst"),
                ("2026-04-22", "2026-04-30", 56, "Beatrix Farrow"),
                ("2026-01-14", "2026-01-19", 57, "Elio Kingsley"),
                ("2025-10-09", null, null, null),
                ("2025-06-30", "2025-07-08", 58, "Isolde Marchetti"),
                ("2025-03-21", "2025-03-27", 51, "Petra Iversen"),
                ("2024-12-10", "2024-12-15", 52, "Talia Yildiz"),
                ("2024-09-02", "2024-09-10", 53, "Noor Fairbanks"),
                ("2024-05-27", "2024-06-03", 54, "Nadia Holloway"),
                ("2024-02-15", "2024-02-21", 59, "Jonah Whitfield"),
                ("2023-11-06", "2023-11-14", 60, "Leilani Okoro"),
            };
            var bagIds = new[] { 4187, 2961, 418, 8873, 7712, 6630, 5524, 4410, 3305, 2207, 1102 };
            var items = issuedTo.Select((d, i) =>
            {
                var donated = DateOnly.Parse(d.Donated, System.Globalization.CultureInfo.InvariantCulture);
                var at = donated.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);
                var bag = d.Issued is { } issued
                    ? new DonatedBag(bagIds[i], BagStatus.Issued, donated.AddDays(BloodRules.ShelfLifeDays), DateOnly.Parse(issued, System.Globalization.CultureInfo.InvariantCulture).ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc), 100 + i, d.PatientId, d.Patient)
                    : new DonatedBag(bagIds[i], BagStatus.Available, donated.AddDays(BloodRules.ShelfLifeDays), null, null, null, null);
                return new DonationHistoryItem(300 - i, 900 - i, at, 1, [bag]);
            }).ToList();
            return new DonorDonations(items.Count, items.Count, items);
        }
    }

    public static void ConfigureDonors(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var rows = Enumerable.Range(0, 50).Select(DonorRow).ToList();
        api.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new PagedResult<DonorListItem>(rows, DonorTotal, 1, 50)));
        api.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(Adrian));
        api.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new DonorEligibility(true, [], null)));
        api.On("GetDonorDonationsAsync", ApiResult<DonorDonations>.Ok(AdrianDonations));
    }
}

public static class PatientDesignData
{
    // One record per patient, for the list row and the detail alike (the API reads both from the same patient), so they never disagree.
    private static readonly (int Id, string Name, BloodType Group, int Pending, string Phone, string Born, Gender Gender, string Address)[] Patients =
    [
        (51, "Petra Iversen", BloodType.ABPos, 1, "+1-555-0251", "1950-01-01", Gender.Female, "4 Mill Street, Northgate"),
        (52, "Talia Yildiz", BloodType.APos, 1, "+1-555-0252", "1957-06-04", Gender.Female, "27 Orchard Row, Westbrook"),
        (53, "Noor Fairbanks", BloodType.ONeg, 2, "+1-555-0253", "1971-06-04", Gender.Female, "18 Quarry Lane, Eastfield"),
        (54, "Nadia Holloway", BloodType.BNeg, 1, "+1-555-0254", "1971-04-10", Gender.Female, "9 Linden Court, Eastfield"),
        (55, "Quinn Crowhurst", BloodType.OPos, 1, "+1-555-0255", "1978-09-13", Gender.Male, "61 Harbour Road, Northgate"),
        (56, "Beatrix Farrow", BloodType.APos, 0, "+1-555-0256", "1985-02-16", Gender.Female, "3 Chapel Walk, Southmere"),
        (57, "Elio Kingsley", BloodType.OPos, 0, "+1-555-0257", "1992-07-19", Gender.Male, "140 Station Road, Westbrook"),
        (58, "Isolde Marchetti", BloodType.BPos, 0, "+1-555-0258", "1999-12-22", Gender.Female, "12 Willow Bank, Southmere"),
        (59, "Jonah Whitfield", BloodType.ANeg, 0, "+1-555-0259", "1956-05-25", Gender.Male, "75 Kiln Lane, Northgate"),
        (60, "Leilani Okoro", BloodType.ABNeg, 0, "+1-555-0260", "1963-10-01", Gender.Female, "8 Ferry Street, Eastfield"),
        (61, "Mateus Andrade", BloodType.OPos, 0, "+1-555-0261", "1970-03-04", Gender.Male, "22 Granary Close, Westbrook"),
        (62, "Saskia van Dijk", BloodType.APos, 0, "+1-555-0262", "1977-08-07", Gender.Female, "5 Beacon Hill, Southmere"),
    ];

    private static DateOnly Born(int index) => DateOnly.Parse(Patients[index].Born, System.Globalization.CultureInfo.InvariantCulture);

    public static PatientListItem Row(int index)
    {
        var p = Patients[index];
        return new PatientListItem(p.Id, p.Name, Born(index), p.Gender, p.Group, p.Phone, p.Pending);
    }

    /// <summary>The detail of a patient, from the same record as its row.</summary>
    public static PatientDetail Detail(int id)
    {
        var index = Array.FindIndex(Patients, p => p.Id == id);
        var p = Patients[index];
        return new PatientDetail(p.Id, p.Name, Born(index), p.Gender, p.Group, p.Phone, p.Address, "rv");
    }

    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var rows = Enumerable.Range(0, Patients.Length).Select(Row).ToList();
        api.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Ok(new PagedResult<PatientListItem>(rows, rows.Count, 1, 50)));
        api.ById["GetPatientAsync"] = id => ApiResult<PatientDetail>.Ok(Detail(id));
        // Noor's two open requests, needed first at the top; there is no O− in stock (as the dashboard says).
        var placed = new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new PagedResult<RequestListItem>(
            [
                new(139, 53, "Noor Fairbanks", BloodType.ONeg, 1, placed, new DateOnly(2026, 11, 22), RequestStatus.Pending),
                new(152, 53, "Noor Fairbanks", BloodType.ONeg, 2, placed, new DateOnly(2026, 11, 30), RequestStatus.Pending),
            ], 2, 1, 10)));
        api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok([new StockItem(BloodType.ONeg, 0, 0, 10, true), new StockItem(BloodType.OPos, 12, 1, 10, false)]));
    }
}

/// <summary>The appointments of the mock-up's dashboard (today, 19 November) with yesterday's and the next days' around them, answered per saved view.</summary>
public static class AppointmentDesignData
{
    private static readonly (int Day, int Hour, int Minute, int Donor, string Name, BloodType Group, AppointmentStatus Status, bool FirstTime)[] Rows =
    [
        (18, 10, 0, 162, "Farid Ulloa", BloodType.BPos, AppointmentStatus.Completed, false),
        (18, 14, 30, 118, "Fenna Galloway", BloodType.APos, AppointmentStatus.NoShow, false),
        (19, 9, 0, 201, "Saoirse Yildiz", BloodType.OPos, AppointmentStatus.Completed, false),
        (19, 9, 30, 122, "Lior Underhill", BloodType.APos, AppointmentStatus.Completed, false),
        (19, 10, 0, 131, "Imani Novak", BloodType.ANeg, AppointmentStatus.Completed, true),
        (19, 11, 0, 149, "Asher Devereux", BloodType.APos, AppointmentStatus.Pending, false),
        (19, 11, 30, 121, "Gideon Achterberg", BloodType.ONeg, AppointmentStatus.Pending, false),
        (19, 12, 0, 137, "Elio Bergström", BloodType.ABPos, AppointmentStatus.Pending, true),
        (19, 14, 0, 198, "Anders Wójcik", BloodType.OPos, AppointmentStatus.Pending, false),
        (19, 15, 30, 130, "Hana Lindqvist", BloodType.ABNeg, AppointmentStatus.Pending, false),
        (17, 16, 0, 144, "Bianca Whitlock", BloodType.BNeg, AppointmentStatus.Cancelled, false),
        (20, 9, 30, 175, "Adrian Mukherjee", BloodType.OPos, AppointmentStatus.Pending, false),
        (20, 13, 0, 191, "Camila Oyelaran", BloodType.APos, AppointmentStatus.Pending, false),
        (23, 10, 30, 114, "Chiara Birch", BloodType.OPos, AppointmentStatus.Pending, false),
    ];

    public static IReadOnlyList<AppointmentListItem> All { get; } = [.. Rows.Select((a, i) =>
    {
        var slot = new DateTime(2026, 11, a.Day, a.Hour, a.Minute, 0, DateTimeKind.Utc);
        return new AppointmentListItem(900 + i, a.Donor, a.Name, a.Group, slot.AddDays(-6), slot, a.Status) { DonorFirstTime = a.FirstTime };
    })];

    /// <summary>The rows of one saved view, as the API answers it (Today: the day, not cancelled, in time order).</summary>
    public static IReadOnlyList<AppointmentListItem> In(AppointmentView view) => view switch
    {
        AppointmentView.Today => [.. All.Where(a => a.SlotStartUtc.Day == 19 && a.Status != AppointmentStatus.Cancelled)],
        AppointmentView.Upcoming => [.. All.Where(a => a.SlotStartUtc.Day > 19 && a.Status == AppointmentStatus.Pending)],
        AppointmentView.Past => [.. All.Where(a => a.SlotStartUtc.Day < 19 && a.Status != AppointmentStatus.Cancelled).OrderByDescending(a => a.SlotStartUtc)],
        _ => [.. All.Where(a => a.Status == AppointmentStatus.Cancelled)],
    };

    /// <summary>Answers the list with one view's rows (the fake does not read the query, so a render sets the view's answer before switching tab).</summary>
    public static void Show(FakeApiClient api, AppointmentView view)
    {
        ArgumentNullException.ThrowIfNull(api);
        var items = In(view).ToList();
        api.On("GetAppointmentsAsync", ApiResult<PagedResult<AppointmentListItem>>.Ok(new PagedResult<AppointmentListItem>(items, items.Count, 1, 50)));
    }

    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        Show(api, AppointmentView.Today);
        api.On("GetAppointmentViewCountsAsync", ApiResult<AppointmentViewCounts>.Ok(new AppointmentViewCounts(
            In(AppointmentView.Today).Count, In(AppointmentView.Upcoming).Count, In(AppointmentView.Past).Count, In(AppointmentView.Cancelled).Count)));
        api.ById["GetAppointmentAsync"] = id => All.FirstOrDefault(a => a.Id == id) is { } a
            ? ApiResult<AppointmentDetail>.Ok(new AppointmentDetail(a.Id, a.DonorId, a.DonorName, a.DonorBloodType, a.PlacedAtUtc, a.SlotStartUtc, a.Status, null, null, "rv"))
            : FakeApiClient.Fail<AppointmentDetail>(404, "not_found");
        api.DonorById = Donor;
    }

    /// <summary>The donor of an appointment (for the panel's eligibility card): Asher Devereux as in the mock-up; the others from the donors' design list where they are on it.</summary>
    public static ApiResult<DonorDetail> Donor(int id)
    {
        if (id == 149)
        {
            return ApiResult<DonorDetail>.Ok(new DonorDetail(149, "Asher Devereux", new DateOnly(1990, 3, 14), Gender.Male, BloodType.APos, "+1-555-0149", null,
                DonorFrequency.Regular, new DateOnly(2026, 8, 3), "rv"));
        }

        var row = Enumerable.Range(0, 50).Select(ListScreensDesignData.DonorRow).FirstOrDefault(d => d.Id == id);
        var booked = All.FirstOrDefault(a => a.DonorId == id);
        return row is not null
            ? ApiResult<DonorDetail>.Ok(new DonorDetail(row.Id, row.Name, row.DateOfBirth, row.Gender, row.BloodType, row.Phone, null, row.Frequency, row.LastDonated, "rv"))
            : booked is not null
                ? ApiResult<DonorDetail>.Ok(new DonorDetail(id, booked.DonorName, new DateOnly(1988, 5, 20), Gender.Female, booked.DonorBloodType, null, null, DonorFrequency.Regular, null, "rv"))
                : FakeApiClient.Fail<DonorDetail>(404, "not_found");
    }
}

/// <summary>Open requests of the mock-up (one needed today, one overdue, two within three days) and closed ones, answered per saved view, with the stock behind the Cover.</summary>
public static class RequestDesignData
{
    public static readonly RequestDetail Petra = new(148, 51, "Petra Iversen", BloodType.ABPos, 3, new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 19),
        RequestStatus.Pending, null, "rv");

    private static readonly DateTime Placed = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

    public static IReadOnlyList<RequestListItem> All { get; } =
    [
        new(133, 60, "Leilani Okoro", BloodType.ABNeg, 2, Placed, new DateOnly(2026, 11, 17), RequestStatus.Pending),
        new(148, 51, "Petra Iversen", BloodType.ABPos, 3, Placed, new DateOnly(2026, 11, 19), RequestStatus.Pending),
        new(141, 52, "Talia Yildiz", BloodType.APos, 3, Placed, new DateOnly(2026, 11, 20), RequestStatus.Pending),
        new(139, 53, "Noor Fairbanks", BloodType.ONeg, 1, Placed, new DateOnly(2026, 11, 22), RequestStatus.Pending),
        new(136, 54, "Nadia Holloway", BloodType.BNeg, 2, Placed, new DateOnly(2026, 11, 26), RequestStatus.Pending),
        new(150, 55, "Quinn Crowhurst", BloodType.OPos, 3, Placed, new DateOnly(2026, 12, 3), RequestStatus.Pending),
        new(129, 56, "Beatrix Farrow", BloodType.APos, 1, Placed, new DateOnly(2026, 11, 12), RequestStatus.Fulfilled),
        new(127, 57, "Elio Kingsley", BloodType.OPos, 2, Placed, new DateOnly(2026, 11, 10), RequestStatus.Fulfilled),
        new(124, 58, "Isolde Marchetti", BloodType.BPos, 1, Placed, new DateOnly(2026, 11, 8), RequestStatus.Cancelled),
    ];

    public static IReadOnlyList<RequestListItem> In(RequestView view) => view switch
    {
        RequestView.Open => [.. All.Where(r => r.Status == RequestStatus.Pending)],
        RequestView.DueSoon => [.. All.Where(r => r.Status == RequestStatus.Pending && r.RequiredBy <= ListScreensDesignData.Today.AddDays(2))],
        RequestView.Fulfilled => [.. All.Where(r => r.Status == RequestStatus.Fulfilled)],
        _ => [.. All.Where(r => r.Status == RequestStatus.Cancelled)],
    };

    public static void Show(FakeApiClient api, RequestView view)
    {
        ArgumentNullException.ThrowIfNull(api);
        var items = In(view).ToList();
        api.On("GetRequestsAsync", ApiResult<PagedResult<RequestListItem>>.Ok(new PagedResult<RequestListItem>(items, items.Count, 1, 50)));
    }

    public static void Configure(FakeApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        Show(api, RequestView.Open);
        api.On("GetRequestViewCountsAsync", ApiResult<RequestViewCounts>.Ok(new RequestViewCounts(
            In(RequestView.Open).Count, In(RequestView.DueSoon).Count, In(RequestView.Fulfilled).Count, In(RequestView.Cancelled).Count)));
        api.ById["GetRequestAsync"] = id => All.FirstOrDefault(r => r.Id == id) is { } r
            ? ApiResult<RequestDetail>.Ok(new RequestDetail(r.Id, r.PatientId, r.PatientName, r.BloodType, r.Units, r.PlacedAtUtc, r.RequiredBy, r.Status, null, "rv"))
            : FakeApiClient.Fail<RequestDetail>(404, "not_found");
        api.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(
        [
            new StockItem(BloodType.ABPos, 4, 0, 10, true),
            new StockItem(BloodType.ABNeg, 0, 0, 10, true),
            new StockItem(BloodType.APos, 2, 0, 10, true),
            new StockItem(BloodType.ONeg, 0, 0, 10, true),
            new StockItem(BloodType.BNeg, 5, 1, 10, true),
            new StockItem(BloodType.OPos, 12, 2, 10, false),
        ]));
        // The bags a fulfil of Petra's request takes: the three oldest unexpired AB+ bags.
        api.On("GetFulfilPreviewAsync", ApiResult<FulfilPreview>.Ok(new FulfilPreview(3, 4,
        [
            new SuggestedBag(601, new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 25), 137, "Elio Bergström"),
            new SuggestedBag(644, new DateTime(2026, 10, 22, 11, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 12, 3), 191, "Camila Oyelaran"),
            new SuggestedBag(702, new DateTime(2026, 11, 3, 9, 30, 0, DateTimeKind.Utc), new DateOnly(2026, 12, 15), 175, "Adrian Mukherjee"),
        ])));
    }
}

/// <summary>A clock stopped at 10:42 on 19 November 2026, in UTC.</summary>
public sealed class ListScreensClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 11, 19, 10, 42, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
