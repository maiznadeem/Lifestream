using Lifestream.Api.Common;
using Lifestream.Contracts;
using Lifestream.Contracts.Receipts;
using Lifestream.Data;
using Lifestream.Data.Queries;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>
/// GET receipts/counts: the receipts that match the list's filters (search, blood type, dates), counted by kind in one grouped query, for the Receipts
/// screen's tabs. A search counts the same matches the list pages through (<see cref="SearchQueries.ReceiptsMatching"/>); without one, the count reads
/// the narrow (StaffId, CreatedAtUtc) index, which holds the kind and blood type.
/// </summary>
public static class ReceiptCountEndpoints
{
    public static void Map(RouteGroupBuilder api) => api.MapGet(ApiRoutes.ReceiptCounts, CountAsync);

    private static async Task<ReceiptCounts> CountAsync([AsParameters] ReceiptCountQuery q, LifestreamDbContext db, CancellationToken ct)
    {
        var (start, end) = Lists.DayRange(q.FromUtc, q.ToUtc);
        List<(ReceiptKind Kind, int Count)> counts;
        if (Lists.ParseSearch(q.Search) is { } words)
        {
            var hits = SearchQueries.ReceiptsMatching(db, words);
            if (q.BloodType is { } hitType)
            {
                hits = hits.Where(h => h.BloodType == hitType);
            }

            if (start is { } hitStart)
            {
                hits = hits.Where(h => h.CreatedAtUtc >= hitStart);
            }

            if (end is { } hitEnd)
            {
                hits = hits.Where(h => h.CreatedAtUtc < hitEnd);
            }

            counts = [.. (await hits.GroupBy(h => h.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct)).Select(x => (x.Key, x.Count))];
        }
        else
        {
            var filtered = db.Receipts.AsNoTracking();
            if (q.BloodType is { } type)
            {
                filtered = filtered.Where(r => r.BloodType == type);
            }

            if (start is { } s)
            {
                filtered = filtered.Where(r => r.CreatedAtUtc >= s);
            }

            if (end is { } e)
            {
                filtered = filtered.Where(r => r.CreatedAtUtc < e);
            }

            counts = [.. (await filtered.GroupBy(r => r.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct)).Select(x => (x.Key, x.Count))];
        }

        var donations = counts.Where(c => c.Kind == ReceiptKind.Donation).Sum(c => c.Count);
        var issues = counts.Where(c => c.Kind == ReceiptKind.Issue).Sum(c => c.Count);
        return new ReceiptCounts(donations + issues, donations, issues);
    }
}
