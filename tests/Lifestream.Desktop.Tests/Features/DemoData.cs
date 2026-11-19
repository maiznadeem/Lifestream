using Lifestream.Client.Api;
using Lifestream.Client.Tests.Fakes;
using Lifestream.Contracts;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Stock;
using Lifestream.Domain;

namespace Lifestream.Desktop.Tests.Features;

/// <summary>
/// Clearly synthetic, demo-like data for the feature screens, served by the fake API (never a real database): made-up names, phone numbers in the
/// reserved fictional range +1-555-0100 to +1-555-0199, and dates relative to a fixed "today" so captures look the same on every run.
/// </summary>
public static class DemoData
{
    private static readonly string[] First = ["Ayşe", "Omar", "Maria", "Liam", "Priya", "Noah", "Chen", "Fatima", "Mateo", "Sofia", "Kofi", "Elena", "Ravi", "Hana", "Lucas", "Amara"];
    private static readonly string[] Last = ["Öztürk", "Haddad", "Lopez", "Walker", "Sharma", "Kim", "Wei", "Al-Sayed", "García", "Rossi", "Mensah", "Petrova", "Patel", "Sato", "Silva", "Okafor"];

    public static DateOnly Today { get; } = new(2026, 11, 19);

    public static string Name(int i) => $"{First[i % First.Length]} {Last[(i / 2) % Last.Length]}";

    public static string Phone(int i) => $"+1-555-01{i % 100:D2}";

    public static BloodType Type(int i) => (BloodType)((i * 3) % 8);

    public static DonorListItem DonorRow(int i) =>
        new(i, Name(i), new DateOnly(1965 + (i % 35), 1 + (i % 12), 1 + (i % 27)), (Gender)(i % 3 == 2 ? 2 : i % 2), Type(i), Phone(i), i % 4 == 0 ? DonorFrequency.Occasional : DonorFrequency.Regular,
            i % 7 == 0 ? null : Today.AddDays(-(20 + (i * 5 % 300))));

    public static DonorDetail DonorDetail(int i)
    {
        var row = DonorRow(i);
        return new(i, row.Name, row.DateOfBirth, row.Gender, row.BloodType, row.Phone, $"{10 + i} Example Street, Demo City", row.Frequency, row.LastDonated, $"rv-{i}");
    }

    public static PatientListItem PatientRow(int i) =>
        new(i, Name(i + 3), new DateOnly(1950 + (i % 55), 1 + (i % 12), 1 + (i % 27)), (Gender)(i % 2), Type(i + 1), Phone(i + 40), i % 5 == 0 ? 2 : i % 3 == 0 ? 1 : 0);

    public static PatientDetail PatientDetail(int i)
    {
        var row = PatientRow(i);
        return new(i, row.Name, row.DateOfBirth, row.Gender, row.BloodType, row.Phone, $"{20 + i} Sample Avenue, Demo City", $"rv-p{i}");
    }

    public static IReadOnlyList<StockItem> Stock() =>
    [
        new(BloodType.APos, 42, 3, 10, false),
        new(BloodType.ANeg, 6, 0, 10, true),
        new(BloodType.BPos, 28, 1, 10, false),
        new(BloodType.BNeg, 14, 0, 10, false),
        new(BloodType.ABPos, 19, 0, 10, false),
        new(BloodType.ABNeg, 3, 2, 10, true),
        new(BloodType.OPos, 66, 6, 10, false),
        new(BloodType.ONeg, 11, 1, 10, false),
    ];

    public static DashboardResponse Dashboard(int months = 12)
    {
        var first = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-(months - 1));
        var monthList = Enumerable.Range(0, months).Select(i => first.AddMonths(i)).ToList();
        var series = BloodTypeExtensions.All.Select((type, t) => new BloodTypeSeries(
            type,
            [.. Enumerable.Range(0, months).Select(m => 2 + ((t * 3) + (m * 5)) % 9)],
            [.. Enumerable.Range(0, months).Select(m => 1 + ((t * 2) + (m * 3)) % 6)])).ToList();
        var stock = Stock();
        return new(
            new DashboardKpis(128, 64, 9, 7, stock.Sum(s => s.Available), stock.Sum(s => s.ExpiringSoon)),
            monthList,
            series,
            [new(Gender.Male, 61), new(Gender.Female, 63), new(Gender.Other, 4)],
            [.. stock.Where(s => s.BelowThreshold)]);
    }

    /// <summary>Scripts the fake API with one full page of donors, patients, their details, stock and the dashboard.</summary>
    public static void Configure(FakeApiClient fake, int donorTotal = 128, int patientTotal = 64)
    {
        fake.On("GetDonorsAsync", ApiResult<PagedResult<DonorListItem>>.Ok(new([.. Enumerable.Range(1, Math.Min(50, donorTotal)).Select(DonorRow)], donorTotal, 1, 50)));
        fake.On("GetPatientsAsync", ApiResult<PagedResult<PatientListItem>>.Ok(new([.. Enumerable.Range(1, Math.Min(50, patientTotal)).Select(PatientRow)], patientTotal, 1, 50)));
        fake.On("GetDonorAsync", ApiResult<DonorDetail>.Ok(DonorDetail(1)));
        fake.On("GetPatientAsync", ApiResult<PatientDetail>.Ok(PatientDetail(1)));
        fake.On("GetDonorEligibilityAsync", ApiResult<DonorEligibility>.Ok(new(true, [], null)));
        fake.On("GetStockAsync", ApiResult<IReadOnlyList<StockItem>>.Ok(Stock()));
        fake.On("GetDashboardAsync", ApiResult<DashboardResponse>.Ok(Dashboard()));
    }
}
