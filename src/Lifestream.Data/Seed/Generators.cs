namespace Lifestream.Data.Seed;

/// <summary>Produces the demo dataset. The only source of seed data: there is no SQL seed script and no backup of real data.</summary>
public interface IDemoDataGenerator
{
    /// <summary>
    /// The demo data as the clinic stands at <paramref name="nowUtc"/>, the moment of the reset: every date is relative to its UTC date, and of that day's
    /// appointments those that are over by then are completed (with receipt and bags) and the rest are pending. Same instant, same output.
    /// </summary>
    DemoDataSet Generate(DateTime nowUtc);

    /// <summary>The demo data as the clinic stands at 00:00 UTC of <paramref name="today"/>: that day's appointments are all still to come. Same date, same output.</summary>
    DemoDataSet Generate(DateOnly today);
}

/// <summary>The 1x demo dataset: 11 staff, 40 donors, 35 patients, 300 appointments, 150 requests, 270 receipts and their bags.</summary>
public sealed class DemoDataGenerator : IDemoDataGenerator
{
    public DemoDataSet Generate(DateTime nowUtc) => SyntheticDataBuilder.Build(nowUtc, 1);

    public DemoDataSet Generate(DateOnly today) => SyntheticDataBuilder.Build(today, 1);
}

/// <summary>
/// The scaled dataset for performance tests: <c>Generate(100)</c> gives about 27,000 receipts, 30,000 appointments and
/// 15,000 requests. Deterministic for a given instant (or date: 00:00 UTC of it) and factor.
/// </summary>
public static class ScaleDataGenerator
{
    public static DemoDataSet Generate(int factor) => Generate(factor, DateTime.UtcNow);

    public static DemoDataSet Generate(int factor, DateTime nowUtc) => SyntheticDataBuilder.Build(nowUtc, factor);

    public static DemoDataSet Generate(int factor, DateOnly today) => SyntheticDataBuilder.Build(today, factor);
}
