using Lifestream.Domain;

namespace Lifestream.Contracts.Stock;

/// <summary>One band of the stock-by-age view: bags whose age (whole days since collection) is from <see cref="FromDay"/> to <see cref="ToDay"/>, inclusive.</summary>
public sealed record StockAgeBand(int FromDay, int ToDay)
{
    /// <summary>"0–6": the band's heading, with an en dash.</summary>
    public string Label => $"{FromDay}–{ToDay}";
}

/// <summary>
/// The six bands of the stock-by-age view, defined once from the shelf life (<see cref="BloodRules.ShelfLifeDays"/>) and the expiring-soon window
/// (<see cref="BloodRules.ExpiringSoonDays"/>), so the server's counts and the screen's headings always agree. The last band holds exactly the bags
/// that expire within the expiring-soon window (age 35 to 42 of a 42-day life), so it matches <see cref="StockItem.ExpiringSoon"/>; the five before it
/// are seven days each.
/// </summary>
public static class StockAges
{
    public const int BandCount = 6;

    public static IReadOnlyList<StockAgeBand> Bands { get; } = Build();

    /// <summary>The band index (0 to 5) of a bag that has this many days of shelf life left (0 = expires today).</summary>
    public static int BandOfDaysLeft(int daysLeft) => BandOfAge(BloodRules.ShelfLifeDays - daysLeft);

    /// <summary>The band index (0 to 5) of a bag of this age in days; ages below 0 count as 0, ages past the shelf life as the last band.</summary>
    public static int BandOfAge(int ageDays)
    {
        for (var i = 0; i < Bands.Count - 1; i++)
        {
            if (ageDays <= Bands[i].ToDay)
            {
                return i;
            }
        }

        return Bands.Count - 1;
    }

    private static StockAgeBand[] Build()
    {
        var lastFrom = BloodRules.ShelfLifeDays - BloodRules.ExpiringSoonDays;
        var width = lastFrom / (BandCount - 1);
        var bands = new StockAgeBand[BandCount];
        for (var i = 0; i < BandCount - 1; i++)
        {
            bands[i] = new StockAgeBand(i * width, i == BandCount - 2 ? lastFrom - 1 : ((i + 1) * width) - 1);
        }

        bands[BandCount - 1] = new StockAgeBand(lastFrom, BloodRules.ShelfLifeDays);
        return bands;
    }
}

/// <summary>One available, unexpired bag in the stock of a group.</summary>
public sealed record StockBag(int Id, DateTime CollectedAtUtc, DateOnly ExpiresOn);

/// <summary>
/// The available, unexpired bags of one group in the order they should be used: oldest collection first, the same order a fulfil issues them in.
/// At most <see cref="StockBagList.Limit"/> bags; <see cref="HasMore"/> says there are more.
/// </summary>
public sealed record StockBagList(BloodType BloodType, IReadOnlyList<StockBag> Bags, bool HasMore)
{
    public const int Limit = 200;
}
