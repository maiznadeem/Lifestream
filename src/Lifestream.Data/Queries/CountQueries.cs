using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Data.Queries;

public sealed record GenderCounts(IReadOnlyDictionary<Gender, int> Donors, IReadOnlyDictionary<Gender, int> Patients);

public sealed record StockRow(BloodType BloodType, int Available, int ExpiringSoon);

/// <summary>Counting queries shared by the API. Each is one LINQ query per table and is covered by plan tests.</summary>
public static class CountQueries
{
    /// <summary>
    /// Donors and patients by gender, every gender value present (zero when none, including Other). One grouped query per
    /// table, each table read once, no join between them (the old function joined them and multiplied the counts).
    /// </summary>
    public static async Task<GenderCounts> GenderCountsAsync(LifestreamDbContext db, CancellationToken cancellationToken = default)
    {
        var donors = await db.Donors.GroupBy(d => d.Gender).Select(g => new { Gender = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var patients = await db.Patients.GroupBy(p => p.Gender).Select(g => new { Gender = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return new GenderCounts(
            Enum.GetValues<Gender>().ToDictionary(g => g, g => donors.FirstOrDefault(x => x.Gender == g)?.Count ?? 0),
            Enum.GetValues<Gender>().ToDictionary(g => g, g => patients.FirstOrDefault(x => x.Gender == g)?.Count ?? 0));
    }

    /// <summary>
    /// Stock per blood type: available, unexpired bags, and how many of them expire within <paramref name="expiringSoonDays"/>.
    /// Served by the filtered index on (ExpiresOn, BloodType) where Status = Available, so it never reads expired or issued bags.
    /// </summary>
    public static async Task<IReadOnlyList<StockRow>> StockByBloodTypeAsync(
        LifestreamDbContext db,
        DateOnly today,
        int expiringSoonDays = BloodRules.ExpiringSoonDays,
        CancellationToken cancellationToken = default)
    {
        var soon = today.AddDays(expiringSoonDays);
        var rows = await db.BloodBags
            .Where(b => b.Status == BagStatus.Available && b.ExpiresOn >= today)
            .GroupBy(b => b.BloodType)
            .Select(g => new { BloodType = g.Key, Available = g.Count(), Soon = g.Count(b => b.ExpiresOn <= soon) })
            .ToListAsync(cancellationToken);
        return BloodTypeExtensions.All
            .Select(t => rows.FirstOrDefault(r => r.BloodType == t) is { } r ? new StockRow(t, r.Available, r.Soon) : new StockRow(t, 0, 0))
            .ToList();
    }
}
