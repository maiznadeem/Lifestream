using System.Globalization;

namespace Lifestream.Client.Features.Receipts;

/// <summary>
/// Record codes in the one style of the list screens (a letter code, a hyphen and at least four digits, as Donors' "D-0175"): receipts "RC-0001",
/// appointments "A-0905", requests "R-0148", staff "S-0002" and blood bags "B-0412". Never "#n".
/// </summary>
public static class RecordCodes
{
    public static string Receipt(int id) => Code("RC", id);

    public static string Appointment(int id) => Code("A", id);

    public static string Request(int id) => Code("R", id);

    public static string Staff(int id) => Code("S", id);

    public static string Bag(int id) => Code("B", id);

    private static string Code(string prefix, int id) => prefix + "-" + id.ToString("0000", CultureInfo.InvariantCulture);
}
