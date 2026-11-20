using Lifestream.Api.Tests.Data;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Dashboard;
using Lifestream.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Lifestream.Api.Tests;

public sealed class Scale10Database : SqlServerDatabaseFixture
{
    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    protected override async Task OnMigratedAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        db.AuditEnabled = false;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        Lifestream.Data.Seed.ScaleDataGenerator.Generate(10, Today).AddTo(db);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The API over 10x data: 1,000 donors, 350 patients, 3,000 appointments, 1,500 requests, 2,700 receipts, 110 staff.</summary>
public sealed class Scale10ApiFixture : ApiFixture
{
    protected override SqlServerDatabaseFixture CreateDb() => new Scale10Database();
}

/// <summary>The dashboard: one endpoint, dense matrix, every gender, local-day ranges, caching, one query per table (API-26, 27, 28).</summary>
public sealed class DashboardTests(Scale10ApiFixture fx) : IClassFixture<Scale10ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dashboard_DenseMatrix_ZerosIncluded_EqualLengths_MatchesIndependentSql()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var dash = await (await admin.Client.GetApi(ApiRoutes.Dashboard)).Read<DashboardResponse>();

        // The default range is the last 12 months, month by month, ascending.
        Assert.Equal(12, dash.Months.Count);
        var thisMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        Assert.Equal(thisMonth, dash.Months[^1]);
        Assert.Equal(thisMonth.AddMonths(-11), dash.Months[0]);

        // Every blood type has a series, and both arrays have one entry per month.
        Assert.Equal(BloodTypeExtensions.All, dash.Series.Select(s => s.BloodType));
        Assert.All(dash.Series, s =>
        {
            Assert.Equal(12, s.Donated.Length);
            Assert.Equal(12, s.Issued.Length);
        });
        Assert.Contains(dash.Series, s => s.Donated.Contains(0) || s.Issued.Contains(0));

        // Compare every cell with a separate, hand-written SQL aggregate.
        var start = dash.Months[0].ToDateTime(TimeOnly.MinValue);
        var rows = await fx.Db.RowsAsync(
            $"SELECT Kind, BloodType, YEAR(CreatedAtUtc), MONTH(CreatedAtUtc), SUM(Units) FROM Receipt WHERE CreatedAtUtc >= '{start:yyyy-MM-dd}' GROUP BY Kind, BloodType, YEAR(CreatedAtUtc), MONTH(CreatedAtUtc)",
            Ct);
        var expected = rows.ToDictionary(r => ((string)r[0]!, (string)r[1]!, (int)r[2]!, (int)r[3]!), r => (int)r[4]!);
        foreach (var series in dash.Series)
        {
            for (var m = 0; m < 12; m++)
            {
                var month = dash.Months[m];
                Assert.Equal(expected.GetValueOrDefault(("Donation", series.BloodType.ToString(), month.Year, month.Month)), series.Donated[m]);
                Assert.Equal(expected.GetValueOrDefault(("Issue", series.BloodType.ToString(), month.Year, month.Month)), series.Issued[m]);
            }
        }

        Assert.True(dash.Series.Sum(s => s.Donated.Sum()) > 0);
    }

    [Fact]
    public async Task Dashboard_Kpis_MatchTheTables_AndCustomRangeIsHonoured()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        var dash = await (await admin.Client.GetApi(ApiRoutes.Dashboard)).Read<DashboardResponse>();
        Assert.Equal(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct), dash.Kpis.TotalDonors);
        Assert.Equal(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Patient", Ct), dash.Kpis.TotalPatients);
        Assert.Equal(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Appointment WHERE Status = 'Pending'", Ct), dash.Kpis.PendingAppointments);
        Assert.Equal(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Request WHERE Status = 'Pending'", Ct), dash.Kpis.PendingRequests);
        Assert.Equal(await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM BloodBag WHERE Status = 'Available' AND ExpiresOn >= CAST(SYSUTCDATETIME() AS date)", Ct), dash.Kpis.UnitsAvailable);

        var ranged = await (await admin.Client.GetApi(ApiRoutes.Dashboard, "?from=2026-01-15&to=2026-03-02")).Read<DashboardResponse>();
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)], ranged.Months);
        Assert.All(ranged.Series, s => Assert.Equal(3, s.Donated.Length));
    }

    [Fact]
    public async Task Dashboard_InvalidRanges_400()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var backwards = await admin.Client.GetApi(ApiRoutes.Dashboard, "?from=2026-03-01&to=2026-01-01");
        using var p1 = await backwards.Problem(400, ProblemCodes.Validation);
        using var huge = await admin.Client.GetApi(ApiRoutes.Dashboard, "?from=2000-01-01&to=2026-01-01");
        using var p2 = await huge.Problem(400, ProblemCodes.Validation);
        using var offset = await admin.Client.GetApi(ApiRoutes.Dashboard, "?utcOffsetMinutes=5000");
        using var p3 = await offset.Problem(400, ProblemCodes.Validation);
    }

    [Fact]
    public async Task Dashboard_UsesAtMostOneQueryPerTable_AndOneEndpoint()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            (await admin.Client.GetApi(ApiRoutes.Dashboard)).Dispose();
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        var commands = fx.Commands.Commands;
        foreach (var table in new[] { "[Donor]", "[Patient]", "[Appointment]", "[Request]", "[Receipt]", "[BloodBag]" })
        {
            var touching = commands.Count(c => c.Contains("FROM " + table, StringComparison.Ordinal));
            Assert.True(touching <= 1, $"{table} is read by {touching} queries: {string.Join(" || ", commands)}");
        }

    }
}

public sealed class DashboardLocalDayTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static DateTime LocalToUtc(DateOnly day, int hour, int minute, int offsetMinutes) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)).AddMinutes(-offsetMinutes), DateTimeKind.Utc);

    [Fact]
    public async Task Dashboard_RangeEndingToday_IncludesRecordAt2330Local()
    {
        const int offset = -300; // UTC-5: 23:30 local is 04:30 the next UTC day.
        var admin = await fx.AddStaffAsync(Role.Admin);
        var localToday = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(offset));
        await Seed.ReceiptAsync(fx, ReceiptKind.Issue, LocalToUtc(localToday, 23, 30, offset), BloodType.ABNeg);

        var withOffset = await (await admin.Client.GetApi(ApiRoutes.Dashboard, $"?utcOffsetMinutes={offset}")).Read<DashboardResponse>();
        var series = withOffset.Series.Single(s => s.BloodType == BloodType.ABNeg);
        Assert.Equal(1, series.Issued[^1]);

        // Read as plain UTC days the record falls after "today" and would be missed: that is the bug the offset prevents.
        var naive = await (await admin.Client.GetApi(ApiRoutes.Dashboard, $"?to={localToday:yyyy-MM-dd}")).Read<DashboardResponse>();
        Assert.Equal(0, naive.Series.Single(s => s.BloodType == BloodType.ABNeg).Issued[^1]);
    }

    [Fact]
    public async Task Dashboard_MonthBuckets_FollowTheLocalCalendar()
    {
        const int offset = -300;
        var admin = await fx.AddStaffAsync(Role.Admin);
        var thisMonth = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var lastDayOfPreviousMonth = thisMonth.AddDays(-1);

        // 23:30 local on the last day of last month is already the 1st of this month in UTC.
        await Seed.ReceiptAsync(fx, ReceiptKind.Donation, LocalToUtc(lastDayOfPreviousMonth, 23, 30, offset), BloodType.BNeg);
        var dash = await (await admin.Client.GetApi(ApiRoutes.Dashboard, $"?utcOffsetMinutes={offset}")).Read<DashboardResponse>();
        var series = dash.Series.Single(s => s.BloodType == BloodType.BNeg);
        Assert.Equal(1, series.Donated[^2]);
        Assert.Equal(0, series.Donated[^1]);
    }
}

/// <summary>
/// Gender counts are exact numbers, so this class has a database of its own (a shared one gets donors from every other test:
/// the count of 17 became 18 and the test failed depending on what ran before it).
/// </summary>
public sealed class DashboardDemographicsTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dashboard_GenderCounts_EqualIndependentCount_IncludingOther()
    {
        // Exact counts need an empty donor table: the other tests of this class seed receipts, which come with a (male) donor each, and the
        // order the tests of a class run in changes with every build. Staff is cleared too, so the admin is created after.
        await fx.Db.ClearAllAsync(Ct);
        var admin = await fx.AddStaffAsync(Role.Admin);
        Assert.Equal(0, await fx.Db.ScalarAsync<int>("SELECT COUNT(*) FROM Donor", Ct));
        await using (var db = fx.Db.NewContext())
        {
            db.Donors.AddRange(Enumerable.Range(0, 17).Select(i => TestRows.Donor($"Male {i}", Gender.Male)));
            db.Donors.AddRange(Enumerable.Range(0, 3).Select(i => TestRows.Donor($"Female {i}", Gender.Female)));
            db.Donors.AddRange(Enumerable.Range(0, 2).Select(i => TestRows.Donor($"Other {i}", Gender.Other)));

            // Patients of every gender: a Cartesian join between the tables would multiply the donor counts.
            db.Patients.AddRange(Enumerable.Range(0, 5).Select(i => TestRows.Patient($"Patient {i}", Gender.Male)));
            await db.SaveChangesAsync(Ct);
        }

        var dash = await (await admin.Client.GetApi(ApiRoutes.Dashboard)).Read<DashboardResponse>();
        Assert.Equal(3, dash.Demographics.Count);
        Assert.Equal(17, dash.Demographics.Single(g => g.Gender == Gender.Male).Count);
        Assert.Equal(3, dash.Demographics.Single(g => g.Gender == Gender.Female).Count);
        Assert.Equal(2, dash.Demographics.Single(g => g.Gender == Gender.Other).Count);
        foreach (var g in dash.Demographics)
        {
            var sql = await fx.Db.ScalarAsync<int>($"SELECT COUNT(*) FROM Donor WHERE Gender = '{g.Gender}'", Ct);
            Assert.Equal(sql, g.Count);
        }
    }
}

public sealed class CachedDashboardFixture : ApiFixture
{
    protected override void Configure(Dictionary<string, string?> settings) => settings["Dashboard:CacheSeconds"] = "30";
}

public sealed class DashboardCacheTests(CachedDashboardFixture fx) : IClassFixture<CachedDashboardFixture>
{
    [Fact]
    public async Task Dashboard_IsCachedBriefly_SecondCallRunsNoQuery()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        (await admin.Client.GetApi(ApiRoutes.Me)).Dispose();
        (await admin.Client.GetApi(ApiRoutes.Dashboard)).Dispose();

        fx.Commands.Clear();
        fx.Commands.Recording = true;
        try
        {
            using var again = await admin.Client.GetApi(ApiRoutes.Dashboard);
            Assert.Equal(System.Net.HttpStatusCode.OK, again.StatusCode);
        }
        finally
        {
            fx.Commands.Recording = false;
        }

        Assert.Empty(fx.Commands.Commands);
    }
}

/// <summary>Records every cache entry the API creates, so a test can read the lifetime that was really applied.</summary>
public sealed class RecordingMemoryCache : Microsoft.Extensions.Caching.Memory.IMemoryCache
{
    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _inner = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());

    public System.Collections.Concurrent.ConcurrentQueue<Microsoft.Extensions.Caching.Memory.ICacheEntry> Entries { get; } = new();

    public Microsoft.Extensions.Caching.Memory.ICacheEntry CreateEntry(object key)
    {
        var entry = _inner.CreateEntry(key);
        Entries.Enqueue(entry);
        return entry;
    }

    public void Remove(object key) => _inner.Remove(key);

    public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

    public void Dispose() => _inner.Dispose();
}

/// <summary>A configuration that asks for an hour of caching: the API must still never keep the dashboard longer than a minute.</summary>
public sealed class HourlyCacheDashboardFixture : ApiFixture
{
    public RecordingMemoryCache Cache { get; } = new();

    protected override void Configure(Dictionary<string, string?> settings) => settings["Dashboard:CacheSeconds"] = "3600";

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(Cache);
}

public sealed class DashboardCachePolicyTests(HourlyCacheDashboardFixture fx) : IClassFixture<HourlyCacheDashboardFixture>
{
    [Fact]
    public async Task Dashboard_EvenWhenConfiguredForAnHour_IsCachedForAtMostSixtySeconds_TheEffectivePolicyNotTheOption()
    {
        var admin = await fx.AddStaffAsync(Role.Admin);
        using var response = await admin.Client.GetApi(ApiRoutes.Dashboard);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var dashboardEntries = fx.Cache.Entries.Where(e => e.Key is ValueTuple<string, DateOnly, DateOnly, int> k && k.Item1 == "dashboard").ToList();
        Assert.NotEmpty(dashboardEntries);
        Assert.All(dashboardEntries, e =>
        {
            Assert.NotNull(e.AbsoluteExpirationRelativeToNow);
            Assert.InRange(e.AbsoluteExpirationRelativeToNow!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60));
        });
    }
}
