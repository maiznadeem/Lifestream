namespace Lifestream.Data.Seed;

/// <summary>Produces the demo dataset. The only source of seed data: there is no SQL seed script and no backup of real data.</summary>
public interface IDemoDataGenerator
{
    /// <summary>The demo data with every date relative to <paramref name="today"/>. Same date, same output.</summary>
    DemoDataSet Generate(DateOnly today);
}

/// <summary>The 1x demo dataset: 11 staff, 40 donors, 35 patients, 300 appointments, 150 requests, 270 receipts and their bags.</summary>
public sealed class DemoDataGenerator : IDemoDataGenerator
{
    public DemoDataSet Generate(DateOnly today) => SyntheticDataBuilder.Build(today, 1);
}

/// <summary>
/// The scaled dataset for performance tests: <c>Generate(100)</c> gives about 27,000 receipts, 30,000 appointments and
/// 15,000 requests. Deterministic for a given date and factor.
/// </summary>
public static class ScaleDataGenerator
{
    public static DemoDataSet Generate(int factor) => Generate(factor, DateOnly.FromDateTime(DateTime.UtcNow));

    public static DemoDataSet Generate(int factor, DateOnly today) => SyntheticDataBuilder.Build(today, factor);
}
