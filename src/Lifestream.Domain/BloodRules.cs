namespace Lifestream.Domain;

/// <summary>Blood stock rules shared by the API and the client.</summary>
public static class BloodRules
{
    /// <summary>A bag expires this many days after collection.</summary>
    public const int ShelfLifeDays = 42;

    /// <summary>A bag counts as "expiring soon" when it expires within this many days.</summary>
    public const int ExpiringSoonDays = 7;
}
