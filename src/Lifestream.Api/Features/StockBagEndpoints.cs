using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Stock;
using Lifestream.Data;
using Lifestream.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>
/// Stock by age and the bags of one group (the Stock screen's matrix and side panel). Both read only the filtered indexes on available bags
/// (Status = Available), so expired and issued bags are never touched.
/// </summary>
public static class StockBagEndpoints
{
    public static void Map(RouteGroupBuilder api) => api.MapGet(ApiRoutes.StockBags, ListAsync);

    /// <summary>
    /// The stock of every group with its age bands, from one query: available, unexpired bags counted by group and expiry date (at most 8 x 43 rows,
    /// read from the (ExpiresOn, BloodType) index), then summed into the bands of <see cref="StockAges"/>. Available and ExpiringSoon come from the same
    /// rows, so they equal what <see cref="StockEndpoints.LoadAsync"/> returns.
    /// </summary>
    internal static async Task<List<StockItem>> LoadWithAgesAsync(LifestreamDbContext db, TimeProvider time, int threshold, CancellationToken ct)
    {
        var today = Dates.Today(time);
        var rows = await db.BloodBags.AsNoTracking()
            .Where(b => b.Status == BagStatus.Available && b.ExpiresOn >= today)
            .GroupBy(b => new { b.BloodType, b.ExpiresOn })
            .Select(g => new { g.Key.BloodType, g.Key.ExpiresOn, Count = g.Count() })
            .ToListAsync(ct);
        return
        [
            .. BloodTypeExtensions.All.Select(type =>
            {
                var bands = new int[StockAges.BandCount];
                var expiring = 0;
                foreach (var row in rows.Where(r => r.BloodType == type))
                {
                    var daysLeft = row.ExpiresOn.DayNumber - today.DayNumber;
                    bands[StockAges.BandOfDaysLeft(daysLeft)] += row.Count;
                    if (daysLeft <= BloodRules.ExpiringSoonDays)
                    {
                        expiring += row.Count;
                    }
                }

                var available = bands.Sum();
                return new StockItem(type, available, expiring, threshold, available < threshold, bands);
            }),
        ];
    }

    /// <summary>
    /// GET stock/bags?bloodType=APos: the group's available, unexpired bags in use-first order (oldest collection first, then id: the order a fulfil
    /// issues them in), at most <see cref="StockBagList.Limit"/>. One query, a seek on the (BloodType, ExpiresOn) index that includes the collection date.
    /// </summary>
    private static async Task<StockBagList> ListAsync([FromQuery] BloodType? bloodType, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (bloodType is not { } type)
        {
            throw ApiException.Validation("bloodType", "is required");
        }

        var today = Dates.Today(time);
        var bags = await db.BloodBags.AsNoTracking()
            .Where(b => b.BloodType == type && b.Status == BagStatus.Available && b.ExpiresOn >= today)
            .OrderBy(b => b.CollectedAtUtc).ThenBy(b => b.Id)
            .Take(StockBagList.Limit + 1)
            .Select(b => new StockBag(b.Id, b.CollectedAtUtc, b.ExpiresOn))
            .ToListAsync(ct);
        var hasMore = bags.Count > StockBagList.Limit;
        return new StockBagList(type, hasMore ? bags[..StockBagList.Limit] : bags, hasMore);
    }
}
