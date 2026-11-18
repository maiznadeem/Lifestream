using System.Globalization;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Queries;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// DB-05: every filter and sort the frozen query records allow (DonorQuery, PatientQuery, AppointmentQuery, RequestQuery,
/// ReceiptQuery, and the staff list) is served without a table scan on the 100x dataset. The matrix is built from the
/// records' own properties (a test fails when a record gains a filter that has no shape here), so each filter is tried
/// alone, then with every sort, on page 1 and on a deep page (261). The statements are the real ones EF sends, replayed
/// with their parameters under <c>SET STATISTICS XML</c>.
/// </summary>
public sealed class ListQueryPlanTests(ScaleDatabaseFixture fixture) : IClassFixture<ScaleDatabaseFixture>
{
    private const int BigTableRows = 5_000;
    private const int MaxPagesPerStatement = 1_500;
    private const int PageSize = 50;
    private const int DeepPage = 261;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // What each record's filter properties map to; the "Page", "PageSize" and "Sort" properties are the paging and ordering.
    private static readonly Dictionary<Type, string[]> Filters = new()
    {
        [typeof(DonorQuery)] = ["Search", "BloodType"],
        [typeof(PatientQuery)] = ["Search", "BloodType"],
        [typeof(AppointmentQuery)] = ["Search", "DonorId", "Status", "BloodType", "FromUtc", "ToUtc"],
        [typeof(RequestQuery)] = ["Search", "PatientId", "Status", "BloodType"],
        [typeof(ReceiptQuery)] = ["Search", "Kind", "BloodType", "FromUtc", "ToUtc"],
    };

    private static readonly Dictionary<string, string[]> Sorts = new()
    {
        ["Donors"] = ["name", "-name", "lastDonated", "-lastDonated"],
        ["Patients"] = ["name", "-name"],
        ["Appointments"] = ["slot", "-slot", "placed", "-placed"],
        ["Requests"] = ["placed", "-placed", "requiredBy", "-requiredBy"],
        ["Receipts"] = ["date", "-date"],
        ["Staff"] = ["name", "username"],
    };

    private static readonly Dictionary<string, string[]> ListFilters = new()
    {
        ["Donors"] = ["none", "Search", "BloodType", "Search+BloodType"],
        ["Patients"] = ["none", "Search", "BloodType", "Search+BloodType"],
        ["Appointments"] = ["none", "Search", "DonorId", "Status", "BloodType", "FromUtc", "ToUtc", "FromUtc+ToUtc", "Status+BloodType"],
        ["Requests"] = ["none", "Search", "PatientId", "Status", "BloodType", "Status+BloodType"],
        ["Receipts"] = ["none", "Search", "Kind", "BloodType", "FromUtc", "ToUtc", "FromUtc+ToUtc", "Kind+BloodType"],
        ["Staff"] = ["none", "Search"],
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var (list, filters) in ListFilters)
        {
            foreach (var filter in filters)
            {
                data.Add($"{list}|{filter}|count");
                foreach (var sort in Sorts[list])
                {
                    data.Add($"{list}|{filter}|{sort}|page1");
                    data.Add($"{list}|{filter}|{sort}|page{DeepPage}");
                }
            }
        }

        return data;
    }

    [Fact]
    public void Matrix_CoversEveryFilterOfEveryQueryRecord()
    {
        // Single filters named in ListFilters must equal the record's filter properties, so a new filter cannot go untested.
        var covered = ListFilters.SelectMany(l => l.Value).SelectMany(f => f.Split('+')).ToHashSet();
        foreach (var (type, filters) in Filters)
        {
            var actual = type.GetProperties().Select(p => p.Name).Where(n => n is not ("Page" or "PageSize" or "Sort")).Order().ToArray();
            Assert.Equal(filters.Order().ToArray(), actual);
            Assert.All(filters, f => Assert.Contains(f, covered));
        }
    }

    [Fact]
    public async Task RanAgainstTheScaleDataset_OnARealSqlServer()
    {
        Assert.Contains("Microsoft SQL Server", fixture.ServerVersion, StringComparison.Ordinal);
        Assert.Equal(27_000, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Receipt", Ct));
        Assert.Equal(30_000, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment", Ct));
        Assert.Equal(15_000, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM Request", Ct));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ListQuery_AtScale_UsesNoTableScan(string @case)
    {
        var sizes = (await fixture.RowsAsync(
            "SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1) GROUP BY t.name", Ct))
            .ToDictionary(r => (string)r[0]!, r => Convert.ToInt64(r[1], CultureInfo.InvariantCulture));
        var sample = await SampleAsync();

        var plans = await PlanHelper.ExecuteAndCaptureAsync(fixture, db => RunAsync(@case, db, sample), Ct);

        Assert.NotEmpty(plans);
        Record(@case, plans);
        foreach (var plan in plans)
        {
            var scans = plan.TableScans()
                .Select(t => t.Trim('[', ']'))
                .Where(t => sizes.TryGetValue(t, out var rows) && rows > BigTableRows)
                .ToList();
            Assert.True(scans.Count == 0, $"{@case} scans {string.Join(", ", scans)} ({plan.LogicalReads} reads):\n{plan.Sql}");

            // An index scan with a key lookup per row can read far more than the table itself (the biggest table is about
            // 950 pages at 100x), so the pages read by each statement are bounded as well.
            Assert.True(plan.LogicalReads <= MaxPagesPerStatement, $"{@case} read {plan.LogicalReads} pages (limit {MaxPagesPerStatement}):\n{plan.Sql}");
        }
    }

    private static readonly object CaptureLock = new();

    // With LIFESTREAM_CAPTURE_FILE set, every case's statements, pages read and operators are appended there (run evidence).
    private static void Record(string @case, List<ExecutedPlan> plans)
    {
        if (Environment.GetEnvironmentVariable("LIFESTREAM_CAPTURE_FILE") is not { Length: > 0 } file)
        {
            return;
        }

        var line = $"{@case}: statements={plans.Count} reads=[{string.Join(",", plans.Select(p => p.LogicalReads))}] " +
                   $"ops=[{string.Join(" ; ", plans.Select(p => string.Join(",", p.Operators().Where(o => o.Table is not null).Select(o => $"{o.Physical}:{o.Table}"))))}]";
        lock (CaptureLock)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }

    private sealed record Sample(
        int DonorId, int PatientId, string DonorPrefix, string PatientPrefix, string StaffPrefix, BloodType BloodType, DateTime From, DateTime To);

    private async Task<Sample> SampleAsync()
    {
        await using var db = fixture.NewContext();
        var donor = await db.Donors.AsNoTracking().OrderBy(d => d.Id).Skip(50).FirstAsync(Ct);
        var patient = await db.Patients.AsNoTracking().OrderBy(d => d.Id).Skip(50).FirstAsync(Ct);
        var staff = await db.Staff.AsNoTracking().OrderBy(s => s.Id).Skip(4).FirstAsync(Ct);
        var (from, to) = DateRanges.HalfOpen(fixture.Today.AddDays(-60), fixture.Today.AddDays(-30));
        return new Sample(donor.Id, patient.Id, donor.Name[..3], patient.Name[..3], staff.DisplayName[..3], BloodType.ONeg, from, to);
    }

    private static Task RunAsync(string @case, LifestreamDbContext db, Sample s)
    {
        var parts = @case.Split('|');
        var list = parts[0];
        var filters = parts[1] == "none" ? [] : parts[1].Split('+');
        if (parts[2] == "count")
        {
            return Count(list, filters, db, s);
        }

        var sort = parts[2];
        var page = parts[3] == "page1" ? 1 : DeepPage;
        return list switch
        {
            "Donors" => Donors(filters, sort, page, db, s),
            "Patients" => Patients(filters, sort, page, db, s),
            "Appointments" => Appointments(filters, sort, page, db, s),
            "Requests" => Requests(filters, sort, page, db, s),
            "Receipts" => Receipts(filters, sort, page, db, s),
            "Staff" => StaffList(filters, sort, page, db, s),
            _ => throw new InvalidOperationException(list),
        };
    }

    // ---- the query shapes: what the API's list endpoints are required to send ----

    private static IQueryable<Donor> DonorFilter(IQueryable<Donor> q, string[] f, Sample s)
    {
        if (f.Contains("Search")) { q = q.NameStartsWith(s.DonorPrefix); }
        if (f.Contains("BloodType")) { q = q.Where(d => d.BloodType == s.BloodType); }
        return q;
    }

    private static IQueryable<Patient> PatientFilter(IQueryable<Patient> q, string[] f, Sample s)
    {
        if (f.Contains("Search")) { q = q.NameStartsWith(s.PatientPrefix); }
        if (f.Contains("BloodType")) { q = q.Where(d => d.BloodType == s.BloodType); }
        return q;
    }

    private static IQueryable<Appointment> AppointmentFilter(IQueryable<Appointment> q, string[] f, List<int>? donorIds, Sample s)
    {
        if (donorIds is not null) { q = q.Where(a => donorIds.Contains(a.DonorId)); }
        if (f.Contains("DonorId")) { q = q.Where(a => a.DonorId == s.DonorId); }
        if (f.Contains("Status")) { q = q.Where(a => a.Status == AppointmentStatus.Pending); }
        if (f.Contains("BloodType")) { q = q.Where(a => a.DonorBloodType == s.BloodType); }
        if (f.Contains("FromUtc")) { var from = s.From; q = q.Where(a => a.SlotStartUtc >= from); }
        if (f.Contains("ToUtc")) { var to = s.To; q = q.Where(a => a.SlotStartUtc < to); }
        return q;
    }

    private static IQueryable<Request> RequestFilter(IQueryable<Request> q, string[] f, List<int>? patientIds, Sample s)
    {
        if (patientIds is not null) { q = q.Where(r => patientIds.Contains(r.PatientId)); }
        if (f.Contains("PatientId")) { q = q.Where(r => r.PatientId == s.PatientId); }
        if (f.Contains("Status")) { q = q.Where(r => r.Status == RequestStatus.Pending); }
        if (f.Contains("BloodType")) { q = q.Where(r => r.BloodType == s.BloodType); }
        return q;
    }

    private static IQueryable<Receipt> ReceiptFilter(IQueryable<Receipt> q, string[] f, LifestreamDbContext db, Sample s)
    {
        if (f.Contains("Search")) { q = q.PartyNameStartsWith(db, s.DonorPrefix); }
        if (f.Contains("Kind")) { q = q.Where(r => r.Kind == ReceiptKind.Donation); }
        if (f.Contains("BloodType")) { q = q.Where(r => r.BloodType == s.BloodType); }
        if (f.Contains("FromUtc")) { var from = s.From; q = q.Where(r => r.CreatedAtUtc >= from); }
        if (f.Contains("ToUtc")) { var to = s.To; q = q.Where(r => r.CreatedAtUtc < to); }
        return q;
    }

    private static async Task<List<int>?> DonorIds(LifestreamDbContext db, string[] f, Sample s) =>
        f.Contains("Search") ? await SearchQueries.DonorIdsByNamePrefixAsync(db, s.DonorPrefix, Ct) : null;

    private static async Task<List<int>?> PatientIds(LifestreamDbContext db, string[] f, Sample s) =>
        f.Contains("Search") ? await SearchQueries.PatientIdsByNamePrefixAsync(db, s.PatientPrefix, Ct) : null;

    private static async Task Count(string list, string[] f, LifestreamDbContext db, Sample s) =>
        _ = await (list switch
    {
        "Donors" => DonorFilter(db.Donors, f, s).CountAsync(Ct),
        "Patients" => PatientFilter(db.Patients, f, s).CountAsync(Ct),
        "Appointments" => AppointmentFilter(db.Appointments, f, await DonorIds(db, f, s), s).CountAsync(Ct),
        "Requests" => RequestFilter(db.Requests, f, await PatientIds(db, f, s), s).CountAsync(Ct),
        "Receipts" => ReceiptFilter(db.Receipts, f, db, s).CountAsync(Ct),
        "Staff" => (f.Contains("Search") ? db.Staff.NameStartsWith(s.StaffPrefix) : db.Staff).CountAsync(Ct),
        _ => throw new InvalidOperationException(list),
    });

    private static async Task Donors(string[] f, string sort, int page, LifestreamDbContext db, Sample s)
    {
        var q = DonorFilter(db.Donors.AsNoTracking(), f, s);
        var ordered = sort switch
        {
            "name" => q.OrderBy(d => d.Name).ThenBy(d => d.Id),
            "-name" => q.OrderByDescending(d => d.Name).ThenByDescending(d => d.Id),
            "lastDonated" => q.OrderBy(d => d.LastDonated).ThenBy(d => d.Id),
            "-lastDonated" => q.OrderByDescending(d => d.LastDonated).ThenByDescending(d => d.Id),
            _ => throw new InvalidOperationException(sort),
        };
        var ids = await ListPaging.PageIdsAsync(ordered, page, PageSize, Ct);
        _ = await db.Donors.AsNoTracking().WithIds(ids)
            .Select(d => new DonorListItem(d.Id, d.Name, d.DateOfBirth, d.Gender, d.BloodType, d.Phone, d.Frequency, d.LastDonated)).ToListAsync(Ct);
    }

    private static async Task Patients(string[] f, string sort, int page, LifestreamDbContext db, Sample s)
    {
        var q = PatientFilter(db.Patients.AsNoTracking(), f, s);
        var ordered = sort == "name" ? q.OrderBy(p => p.Name).ThenBy(p => p.Id) : q.OrderByDescending(p => p.Name).ThenByDescending(p => p.Id);
        var ids = await ListPaging.PageIdsAsync(ordered, page, PageSize, Ct);
        _ = await db.Patients.AsNoTracking().WithIds(ids)
            .Select(p => new { p.Id, p.Name, p.DateOfBirth, p.Gender, p.BloodType, p.Phone }).ToListAsync(Ct);

        // PendingRequests for just these patients: a seek on Request.PatientId per id.
        _ = await db.Requests.Where(r => ids.Contains(r.PatientId) && r.Status == RequestStatus.Pending)
            .GroupBy(r => r.PatientId).Select(g => new { PatientId = g.Key, N = g.Count() }).ToListAsync(Ct);
    }

    private static async Task Appointments(string[] f, string sort, int page, LifestreamDbContext db, Sample s)
    {
        var q = AppointmentFilter(db.Appointments.AsNoTracking(), f, await DonorIds(db, f, s), s);
        var ordered = sort switch
        {
            "slot" => q.OrderBy(a => a.SlotStartUtc).ThenBy(a => a.Id),
            "-slot" => q.OrderByDescending(a => a.SlotStartUtc).ThenByDescending(a => a.Id),
            "placed" => q.OrderBy(a => a.PlacedAtUtc).ThenBy(a => a.Id),
            "-placed" => q.OrderByDescending(a => a.PlacedAtUtc).ThenByDescending(a => a.Id),
            _ => throw new InvalidOperationException(sort),
        };
        var ids = await ListPaging.PageIdsAsync(ordered, page, PageSize, Ct);
        _ = await db.Appointments.AsNoTracking().WithIds(ids)
            .Select(a => new AppointmentListItem(a.Id, a.DonorId, a.Donor!.Name, a.Donor.BloodType, a.PlacedAtUtc, a.SlotStartUtc, a.Status)).ToListAsync(Ct);
    }

    private static async Task Requests(string[] f, string sort, int page, LifestreamDbContext db, Sample s)
    {
        var q = RequestFilter(db.Requests.AsNoTracking(), f, await PatientIds(db, f, s), s);
        var ordered = sort switch
        {
            "placed" => q.OrderBy(r => r.PlacedAtUtc).ThenBy(r => r.Id),
            "-placed" => q.OrderByDescending(r => r.PlacedAtUtc).ThenByDescending(r => r.Id),
            "requiredBy" => q.OrderBy(r => r.RequiredBy).ThenBy(r => r.Id),
            "-requiredBy" => q.OrderByDescending(r => r.RequiredBy).ThenByDescending(r => r.Id),
            _ => throw new InvalidOperationException(sort),
        };
        var ids = await ListPaging.PageIdsAsync(ordered, page, PageSize, Ct);
        _ = await db.Requests.AsNoTracking().WithIds(ids)
            .Select(r => new RequestListItem(r.Id, r.PatientId, r.Patient!.Name, r.BloodType, r.Units, r.PlacedAtUtc, r.RequiredBy, r.Status)).ToListAsync(Ct);
    }

    private static async Task Receipts(string[] f, string sort, int page, LifestreamDbContext db, Sample s)
    {
        var q = ReceiptFilter(db.Receipts.AsNoTracking(), f, db, s);
        var ordered = sort == "date" ? q.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Id) : q.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id);
        var ids = await ListPaging.PageIdsAsync(ordered, page, PageSize, Ct);
        _ = await db.Receipts.AsNoTracking().WithIds(ids)
            .Select(r => new ReceiptListItem(
                r.Id, r.CreatedAtUtc, r.Kind, r.BloodType, r.Units, r.StaffId, r.Staff!.DisplayName,
                r.Appointment != null ? r.Appointment.Donor!.Name : r.Request!.Patient!.Name, r.AppointmentId, r.RequestId)).ToListAsync(Ct);
    }

    private static async Task StaffList(string[] f, string sort, int page, LifestreamDbContext db, Sample s)
    {
        var q = f.Contains("Search") ? db.Staff.AsNoTracking().NameStartsWith(s.StaffPrefix) : db.Staff.AsNoTracking();
        var ordered = sort == "name" ? q.OrderBy(x => x.DisplayName).ThenBy(x => x.Id) : q.OrderBy(x => x.Username).ThenBy(x => x.Id);
        var ids = await ListPaging.PageIdsAsync(ordered, page, PageSize, Ct);
        _ = await db.Staff.AsNoTracking().WithIds(ids)
            .Select(x => new { x.Id, x.DisplayName, x.Username, x.Designation, x.Shift, x.Role, x.HasPhoto, x.Salary, x.Phone }).ToListAsync(Ct);
    }
}
