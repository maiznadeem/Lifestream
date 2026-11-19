using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Stock;
using Lifestream.Data;
using Lifestream.Data.Queries;
using Lifestream.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Features;

public static class StockEndpoints
{
    public static void Map(RouteGroupBuilder api) => api.MapGet(ApiRoutes.Stock, GetAsync);

    internal static async Task<List<StockItem>> LoadAsync(LifestreamDbContext db, TimeProvider time, int threshold, CancellationToken ct)
    {
        // Expired bags are excluded by date inside the query; no job ever marks them.
        var rows = await CountQueries.StockByBloodTypeAsync(db, Dates.Today(time), BloodRules.ExpiringSoonDays, ct);
        return [.. rows.Select(r => new StockItem(r.BloodType, r.Available, r.ExpiringSoon, threshold, r.Available < threshold))];
    }

    private static async Task<List<StockItem>> GetAsync(LifestreamDbContext db, TimeProvider time, IOptions<StockSettings> stock, CancellationToken ct) =>
        await LoadAsync(db, time, stock.Value.Threshold, ct);
}

public static class DashboardEndpoints
{
    private const int MaxMonths = 60;

    public static void Map(RouteGroupBuilder api) => api.MapGet(ApiRoutes.Dashboard, GetAsync);

    /// <summary>
    /// Everything on the dashboard in one response. from/to are inclusive dates in the caller's local calendar; the optional
    /// utcOffsetMinutes says how far that calendar is from UTC (a record at 23:30 local belongs to that local day and month).
    /// </summary>
    private static async Task<DashboardResponse> GetAsync(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? utcOffsetMinutes,
        LifestreamDbContext db,
        TimeProvider time,
        IMemoryCache cache,
        IOptions<StockSettings> stock,
        IOptions<DashboardSettings> settings,
        CancellationToken ct)
    {
        var offset = utcOffsetMinutes ?? 0;
        if (offset is < -840 or > 840)
        {
            throw ApiException.Validation("utcOffsetMinutes", "must be between -840 and 840");
        }

        var nowUtc = time.GetUtcNow().UtcDateTime;
        var localToday = DateOnly.FromDateTime(nowUtc.AddMinutes(offset));
        if (from is { } givenFrom)
        {
            Dates.CheckWindow(givenFrom, "from");
        }

        if (to is { } givenTo)
        {
            Dates.CheckWindow(givenTo, "to");
        }

        var end = to ?? localToday;
        var start = from ?? new DateOnly(end.Year, end.Month, 1).AddMonths(-11);
        if (start > end)
        {
            throw ApiException.Validation("from", "must not be after to");
        }

        var firstMonth = new DateOnly(start.Year, start.Month, 1);
        var lastMonth = new DateOnly(end.Year, end.Month, 1);
        var monthCount = ((lastMonth.Year - firstMonth.Year) * 12) + lastMonth.Month - firstMonth.Month + 1;
        if (monthCount > MaxMonths)
        {
            throw ApiException.Validation("from", $"the range may cover at most {MaxMonths} months");
        }

        var ttl = Math.Clamp(settings.Value.CacheSeconds, 0, 60);
        var key = ("dashboard", start, end, offset);
        if (ttl > 0 && cache.TryGetValue(key, out DashboardResponse? cached) && cached is not null)
        {
            return cached;
        }

        var response = await BuildAsync(db, time, stock.Value.Threshold, start, end, offset, firstMonth, monthCount, ct);
        if (ttl > 0)
        {
            cache.Set(key, response, TimeSpan.FromSeconds(ttl));
        }

        return response;
    }

    private static async Task<DashboardResponse> BuildAsync(LifestreamDbContext db, TimeProvider time, int threshold, DateOnly start, DateOnly end, int offset, DateOnly firstMonth, int monthCount, CancellationToken ct)
    {
        // Each table is read by exactly one query.
        var donorGenders = await db.Donors.AsNoTracking().GroupBy(d => d.Gender).Select(g => new { Gender = g.Key, Count = g.Count() }).ToListAsync(ct);
        var patients = await db.Patients.AsNoTracking().CountAsync(ct);
        var pendingAppointments = await db.Appointments.AsNoTracking().CountAsync(a => a.Status == AppointmentStatus.Pending, ct);
        var pendingRequests = await db.Requests.AsNoTracking().CountAsync(r => r.Status == RequestStatus.Pending, ct);
        var stockItems = await StockEndpoints.LoadAsync(db, time, threshold, ct);

        var startUtc = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(-offset);
        var endUtc = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(-offset);
        var monthly = await db.Receipts.AsNoTracking()
            .Where(r => r.CreatedAtUtc >= startUtc && r.CreatedAtUtc < endUtc)
            .GroupBy(r => new { r.Kind, r.BloodType, r.CreatedAtUtc.AddMinutes(offset).Year, r.CreatedAtUtc.AddMinutes(offset).Month })
            .Select(g => new { g.Key.Kind, g.Key.BloodType, g.Key.Year, g.Key.Month, Units = g.Sum(r => r.Units) })
            .ToListAsync(ct);

        var months = Enumerable.Range(0, monthCount).Select(i => firstMonth.AddMonths(i)).ToList();
        var series = BloodTypeExtensions.All.Select(type =>
        {
            int[] Row(ReceiptKind kind) =>
                [.. months.Select(m => monthly.Where(x => x.Kind == kind && x.BloodType == type && x.Year == m.Year && x.Month == m.Month).Sum(x => x.Units))];
            return new BloodTypeSeries(type, Row(ReceiptKind.Donation), Row(ReceiptKind.Issue));
        }).ToList();

        var demographics = Enum.GetValues<Gender>().Select(g => new GenderCount(g, donorGenders.FirstOrDefault(x => x.Gender == g)?.Count ?? 0)).ToList();
        var kpis = new DashboardKpis(
            donorGenders.Sum(x => x.Count),
            patients,
            pendingAppointments,
            pendingRequests,
            stockItems.Sum(s => s.Available),
            stockItems.Sum(s => s.ExpiringSoon));
        return new DashboardResponse(kpis, months, series, demographics, [.. stockItems.Where(s => s.BelowThreshold)]);
    }
}
