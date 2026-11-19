namespace Lifestream.Contracts;

/// <summary>
/// The sort keys each list endpoint accepts (<c>sort=name</c> ascending, <c>sort=-name</c> descending), defined once. The API builds its
/// validators from these names and the client offers only these, so a screen can't send a key the server would refuse.
/// </summary>
public static class SortKeys
{
    public const string Name = "name";
    public const string Username = "username";
    public const string LastDonated = "lastDonated";
    public const string Slot = "slot";
    public const string Placed = "placed";
    public const string RequiredBy = "requiredBy";
    public const string Date = "date";

    public static IReadOnlyList<string> Donors { get; } = [Name, LastDonated];

    public static IReadOnlyList<string> Patients { get; } = [Name];

    public static IReadOnlyList<string> Appointments { get; } = [Slot, Placed];

    public static IReadOnlyList<string> Requests { get; } = [Placed, RequiredBy];

    public static IReadOnlyList<string> Receipts { get; } = [Date];

    public static IReadOnlyList<string> Staff { get; } = [Name, Username];
}
