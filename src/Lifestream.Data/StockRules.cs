using Lifestream.Domain;

namespace Lifestream.Data;

/// <summary>The freshness and expiry windows, defined once. Nothing else in the data module may hard-code them.</summary>
public static class StockRules
{
    /// <summary>A bag is "fresh" for this many days after collection.</summary>
    public const int FreshDays = 3;

    /// <summary>A bag expires this many days after collection (one value, from the shared domain rules).</summary>
    public const int ShelfLifeDays = BloodRules.ShelfLifeDays;

    public static DateOnly ExpiryFor(DateTime collectedAtUtc) => DateOnly.FromDateTime(collectedAtUtc).AddDays(ShelfLifeDays);

    public static DateTime FreshSinceUtc(DateTime nowUtc) => nowUtc.AddDays(-FreshDays);
}
