using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Queries;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// The 100x dataset (about 27,000 receipts) loaded into a real database once, and the queries the API will run against it (DB-05, DB-06, DB-14).
/// Plans are the actual plans, captured with the real parameter values.
/// </summary>
[Collection("Scale datasets: one class at a time")] // the 100x databases load 100,000 rows and the perf tests time requests, so they do not overlap
public sealed class PlanTests(ScaleDatabaseFixture fixture) : IClassFixture<ScaleDatabaseFixture>
{
    private const int BigTableRows = 5_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> QueryNames =>
    [
        "Appointments_PendingBySlot_Page1",
        "Appointments_ByDonorId",
        "Appointments_SlotRange_Page1",
        "Appointments_NewestFirst_Page1",
        "Appointments_ByDonorName",
        "Appointments_CountsByStatus",
        "Requests_PendingNewestFirst_Page1",
        "Requests_ByBloodType_AndStatus",
        "Requests_ByPatientId",
        "Requests_ByPatientName",
        "Requests_OverdueByRequiredDate",
        "Requests_PlacedRange",
        "Receipts_NewestFirst_Page1_WithNames",
        "Receipts_ByKind_DateRange",
        "Receipts_ByStaffId",
        "Receipts_ByBloodType_DateRange",
        "Receipts_ByRequestId",
        "Receipts_ByAppointmentId",
        "Dashboard_UnitsByKindTypeMonth",
        "Dashboard_PendingCounts",
        "BloodBags_ByTypeAndStatus",
        "BloodBags_CollectedRange",
        "BloodBags_ByDonationReceipt",
        "Stock_ByBloodType",
        "Staff_ByUsername",
        "Staff_ListByName",
        "Donors_ByNameStartsWith",
        "Donors_ByBloodType",
        "Gender_Counts",
    ];

    [Fact]
    public async Task ScaleDataset_IsLoadedIntoARealDatabase_With27000Receipts()
    {
        Assert.Contains("Microsoft SQL Server", fixture.ServerVersion, StringComparison.Ordinal);
        Assert.Equal(27_000, fixture.Receipts);
        Assert.Equal(27_000, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct));
        Assert.Equal(100 * SeedSizes.Appointments, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment", Ct));
        Assert.Equal(100 * SeedSizes.Requests, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Request", Ct));
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM AuditLog", Ct));
    }

    [Theory]
    [MemberData(nameof(QueryNames))]
    public async Task Query_OnLargeTables_UsesNoTableScan(string name)
    {
        var sizes = (await fixture.RowsAsync(
            "SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) GROUP BY t.name", Ct))
            .ToDictionary(r => (string)r[0]!, r => Convert.ToInt64(r[1], System.Globalization.CultureInfo.InvariantCulture));

        var plans = await PlanHelper.ExecuteAndCaptureAsync(fixture, db => RunAsync(name, db), Ct);

        Assert.NotEmpty(plans);
        foreach (var plan in plans)
        {
            var scans = plan.TableScans()
                .Select(t => t.Trim('[', ']'))
                .Where(t => sizes.TryGetValue(t, out var rows) && rows > BigTableRows)
                .ToList();
            Assert.True(scans.Count == 0, $"{name} scans {string.Join(", ", scans)}:\n{plan.Sql}");
        }
    }

    [Fact]
    public async Task Stock_ByBloodType_ReadsUnder40Pages_At100x()
    {
        var plans = await PlanHelper.ExecuteAndCaptureAsync(fixture, db => CountQueries.StockByBloodTypeAsync(db, fixture.Today, BloodRules.ExpiringSoonDays, Ct), Ct);

        var plan = Assert.Single(plans);
        Assert.True(plan.LogicalReads < 40, $"The stock query read {plan.LogicalReads} pages.");
        Assert.True(plan.LogicalReads > 0, "The reads were not measured.");
        Assert.Empty(plan.TableScans());
        // Stock is real: unexpired available bags exist and the query counted them.
        await using var db = fixture.NewContext();
        var stock = await CountQueries.StockByBloodTypeAsync(db, fixture.Today, BloodRules.ExpiringSoonDays, Ct);
        var expected = await fixture.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM BloodBag WHERE Status = 'Available' AND ExpiresOn >= '{fixture.Today:yyyy-MM-dd}'", Ct);
        Assert.True(expected > 0);
        Assert.Equal(expected, stock.Sum(s => s.Available));
    }

    [Fact]
    public async Task GenderCounts_At100x_EqualIndependentCount_AndUseNoJoin()
    {
        await using var db = fixture.NewContext();
        var counts = await CountQueries.GenderCountsAsync(db, Ct);

        Assert.Equal(await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct), counts.Donors.Values.Sum());
        Assert.Equal(await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Patient", Ct), counts.Patients.Values.Sum());
        Assert.Equal(
            await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Donor WHERE Gender = 'Male'", Ct),
            counts.Donors[Gender.Male]);
        Assert.True(counts.Donors[Gender.Other] > 0);
        var plans = await PlanHelper.ExecuteAndCaptureAsync(fixture, d => CountQueries.GenderCountsAsync(d, Ct), Ct);
        Assert.All(plans, p => Assert.False(p.HasJoin()));
    }

    private async Task RunAsync(string name, LifestreamDbContext db)
    {
        var today = fixture.Today;
        var from = today.AddDays(-30);

        // Sample values come from a separate context, so only the named query below is captured.
        int staffId, appointmentId;
        string staffName;
        Donor donor;
        Patient patient;
        Receipt receipt;
        await using (var lookup = fixture.NewContext())
        {
            staffId = await lookup.Staff.Select(s => s.Id).FirstAsync(Ct);
            donor = await lookup.Donors.AsNoTracking().OrderBy(d => d.Id).FirstAsync(Ct);
            patient = await lookup.Patients.AsNoTracking().OrderBy(d => d.Id).FirstAsync(Ct);
            receipt = await lookup.Receipts.AsNoTracking().Where(r => r.Kind == ReceiptKind.Issue).OrderBy(r => r.Id).Skip(500).FirstAsync(Ct);
            appointmentId = await lookup.Appointments.OrderBy(a => a.Id).Skip(100).Select(a => a.Id).FirstAsync(Ct);
            staffName = await lookup.Staff.OrderBy(s => s.Id).Select(s => s.Username).Skip(3).FirstAsync(Ct);
        }

        var (start, end) = DateRanges.HalfOpen(from, today);
        var upperName = staffName.ToUpperInvariant();

        switch (name)
        {
            case "Appointments_PendingBySlot_Page1":
                _ = await db.Appointments.AsNoTracking().Where(a => a.Status == AppointmentStatus.Pending).OrderBy(a => a.SlotStartUtc).Take(50).ToListAsync(Ct);
                break;
            case "Appointments_ByDonorId":
                _ = await db.Appointments.AsNoTracking().Where(a => a.DonorId == donor.Id).OrderByDescending(a => a.SlotStartUtc).Take(50).ToListAsync(Ct);
                break;
            case "Appointments_SlotRange_Page1":
                _ = await db.Appointments.AsNoTracking().InRange(a => a.SlotStartUtc, start, end).OrderBy(a => a.SlotStartUtc).Take(50).ToListAsync(Ct);
                break;
            case "Appointments_NewestFirst_Page1":
                _ = await db.Appointments.AsNoTracking().OrderByDescending(a => a.PlacedAtUtc).Take(50)
                    .Select(a => new { a.Id, DonorName = a.Donor!.Name, a.Status, a.SlotStartUtc }).ToListAsync(Ct);
                break;
            case "Appointments_ByDonorName":
                _ = await db.Appointments.AsNoTracking().Where(a => a.Donor!.Name == donor.Name).Select(a => a.Id).ToListAsync(Ct);
                break;
            case "Appointments_CountsByStatus":
                _ = await db.Appointments.GroupBy(a => a.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(Ct);
                break;
            case "Requests_PendingNewestFirst_Page1":
                _ = await db.Requests.AsNoTracking().Where(r => r.Status == RequestStatus.Pending).OrderByDescending(r => r.PlacedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "Requests_ByBloodType_AndStatus":
                _ = await db.Requests.AsNoTracking().Where(r => r.BloodType == BloodType.APos && r.Status == RequestStatus.Pending).OrderByDescending(r => r.PlacedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "Requests_ByPatientId":
                _ = await db.Requests.AsNoTracking().Where(r => r.PatientId == patient.Id).ToListAsync(Ct);
                break;
            case "Requests_ByPatientName":
                _ = await db.Requests.AsNoTracking().Where(r => r.Patient!.Name == patient.Name).Select(r => r.Id).ToListAsync(Ct);
                break;
            case "Requests_OverdueByRequiredDate":
                _ = await db.Requests.AsNoTracking().Where(r => r.RequiredBy < today && r.Status == RequestStatus.Pending).OrderBy(r => r.RequiredBy).Take(50).ToListAsync(Ct);
                break;
            case "Requests_PlacedRange":
                _ = await db.Requests.AsNoTracking().InRange(r => r.PlacedAtUtc, start, end).OrderByDescending(r => r.PlacedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "Receipts_NewestFirst_Page1_WithNames":
                _ = await db.Receipts.AsNoTracking().OrderByDescending(r => r.CreatedAtUtc).Take(50)
                    .Select(r => new { r.Id, r.Kind, r.Units, StaffName = r.Staff!.DisplayName, Donor = r.Appointment!.Donor!.Name, Patient = r.Request!.Patient!.Name })
                    .ToListAsync(Ct);
                break;
            case "Receipts_ByKind_DateRange":
                _ = await db.Receipts.AsNoTracking().Where(r => r.Kind == ReceiptKind.Donation).InRange(r => r.CreatedAtUtc, start, end).OrderByDescending(r => r.CreatedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "Receipts_ByStaffId":
                _ = await db.Receipts.AsNoTracking().Where(r => r.StaffId == staffId).Take(50).ToListAsync(Ct);
                break;
            case "Receipts_ByBloodType_DateRange":
                _ = await db.Receipts.AsNoTracking().Where(r => r.BloodType == receipt.BloodType).InRange(r => r.CreatedAtUtc, start, end).OrderByDescending(r => r.CreatedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "Receipts_ByRequestId":
                _ = await db.Receipts.AsNoTracking().Where(r => r.RequestId == receipt.RequestId).ToListAsync(Ct);
                break;
            case "Receipts_ByAppointmentId":
                _ = await db.Receipts.AsNoTracking().Where(r => r.AppointmentId == appointmentId).ToListAsync(Ct);
                break;
            case "Dashboard_UnitsByKindTypeMonth":
                _ = await db.Receipts.InRange(r => r.CreatedAtUtc, today.AddDays(-365).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), end)
                    .GroupBy(r => new { r.Kind, r.BloodType, r.CreatedAtUtc.Year, r.CreatedAtUtc.Month })
                    .Select(g => new { g.Key.Kind, g.Key.BloodType, g.Key.Year, g.Key.Month, Units = g.Sum(r => r.Units) })
                    .ToListAsync(Ct);
                break;
            case "Dashboard_PendingCounts":
                _ = await db.Appointments.CountAsync(a => a.Status == AppointmentStatus.Pending, Ct);
                _ = await db.Requests.CountAsync(r => r.Status == RequestStatus.Pending, Ct);
                _ = await db.Donors.CountAsync(Ct);
                _ = await db.Patients.CountAsync(Ct);
                break;
            case "BloodBags_ByTypeAndStatus":
                _ = await db.BloodBags.AsNoTracking().Where(b => b.BloodType == BloodType.OPos && b.Status == BagStatus.Available).OrderBy(b => b.CollectedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "BloodBags_CollectedRange":
                _ = await db.BloodBags.AsNoTracking().InRange(b => b.CollectedAtUtc, start, end).OrderBy(b => b.CollectedAtUtc).Take(50).ToListAsync(Ct);
                break;
            case "BloodBags_ByDonationReceipt":
                _ = await db.BloodBags.AsNoTracking().Where(b => b.DonationReceiptId == receipt.Id).ToListAsync(Ct);
                break;
            case "Stock_ByBloodType":
                _ = await CountQueries.StockByBloodTypeAsync(db, today, BloodRules.ExpiringSoonDays, Ct);
                break;
            case "Staff_ByUsername":
                _ = await db.Staff.AsNoTracking().SingleAsync(s => s.Username == upperName, Ct);
                break;
            case "Staff_ListByName":
                _ = await db.Staff.AsNoTracking().OrderBy(s => s.DisplayName).Take(50).ToListAsync(Ct);
                break;
            case "Donors_ByNameStartsWith":
                _ = await db.Donors.AsNoTracking().Where(d => d.Name.StartsWith("Ay")).OrderBy(d => d.Name).Take(50).ToListAsync(Ct);
                break;
            case "Donors_ByBloodType":
                _ = await db.Donors.AsNoTracking().Where(d => d.BloodType == BloodType.ONeg).OrderBy(d => d.Name).Take(50).ToListAsync(Ct);
                break;
            case "Gender_Counts":
                _ = await CountQueries.GenderCountsAsync(db, Ct);
                break;
            default:
                throw new InvalidOperationException(name);
        }
    }
}
