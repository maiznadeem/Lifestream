using System.Globalization;

namespace Lifestream.Client.Features.Receipts;

/// <summary>
/// Record codes in the one style of the list screens (a letter code, a hyphen and at least four digits): donors "D-0175", patients "P-0053", receipts
/// "RC-0001", appointments "A-0905", requests "R-0148", staff "S-0002" and blood bags "B-0412". Never "#n". This is the only place a code is put together:
/// every screen, panel, dialog, toast and printed receipt asks here, so the same record reads the same everywhere.
/// </summary>
public static class RecordCodes
{
    public static string Donor(int id) => Of("D", id);

    public static string Patient(int id) => Of("P", id);

    public static string Receipt(int id) => Of("RC", id);

    public static string Appointment(int id) => Of("A", id);

    public static string Request(int id) => Of("R", id);

    public static string Staff(int id) => Of("S", id);

    public static string Bag(int id) => Of("B", id);

    /// <summary>The code of a record of the kind with this letter code (for a view that is told the letters, as the list screens' code converter is).</summary>
    public static string Of(string prefix, int id) => prefix + "-" + id.ToString("0000", CultureInfo.InvariantCulture);
}
