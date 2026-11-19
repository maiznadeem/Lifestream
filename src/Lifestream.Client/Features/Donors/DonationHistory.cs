using System.Globalization;
using Lifestream.Client.Common;
using Lifestream.Client.Features.ListScreens;
using Lifestream.Contracts.Donors;
using Lifestream.Domain;

namespace Lifestream.Client.Features.Donors;

/// <summary>Where the blood of one donation went, for the dot of its history row: all issued (filled), some still in stock, or gone without being used.</summary>
public enum DonationFate
{
    Issued,
    InStock,
    Unused,
}

/// <summary>
/// One row of a donor's donation history in the side panel (direction A, design system 7.6 step 4): the date, the bag code(s) at the right, and one line per bag that
/// says where it went ("Issued 12 Aug to Quinn Crowhurst", "In stock, use by 09 Sep", "Expired 20 Nov 2025", "Discarded").
/// </summary>
public sealed record DonationHistoryRow(string DateText, string CodeText, string FateText, DonationFate Fate);

/// <summary>Turns the API's donation history into the panel's rows and summary lines.</summary>
public static class DonationHistory
{
    /// <summary>The bag's code as the screens show it, "B-0187" (the bag's real id: the schema has no donation number).</summary>
    public static string BagCode(int id) => $"B-{id.ToString("0000", CultureInfo.InvariantCulture)}";

    /// <summary>"11 donations, 12 units in all", or "None yet".</summary>
    public static string TotalText(DonorDonations history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.Total == 0
            ? "None yet"
            : $"{(history.Total == 1 ? "1 donation" : $"{history.Total.ToString("N0", CultureInfo.CurrentCulture)} donations")}, {RecordFormatting.Units(history.TotalUnits)} in all";
    }

    public static IReadOnlyList<DonationHistoryRow> Rows(DonorDonations history, DateOnly today, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(history);
        return [.. history.Items.Select(item => Row(item, today, zone))];
    }

    private static DonationHistoryRow Row(DonationHistoryItem item, DateOnly today, TimeZoneInfo zone)
    {
        var date = Formatting.Date(RecordFormatting.LocalDate(item.DonatedAtUtc, zone));
        if (item.Bags.Count == 0)
        {
            return new(date, RecordFormatting.Units(item.Units), "No bag on record", DonationFate.Unused);
        }

        var code = item.Bags.Count == 1 ? BagCode(item.Bags[0].Id) : RecordFormatting.Units(item.Bags.Count);
        var lines = item.Bags.Select(b => (item.Bags.Count == 1 ? string.Empty : BagCode(b.Id) + ": ") + FateOf(b, today, zone));
        var fate = item.Bags.All(b => b.Status == BagStatus.Issued) ? DonationFate.Issued
            : item.Bags.Any(b => b.Status == BagStatus.Available && b.ExpiresOn >= today) ? DonationFate.InStock
            : DonationFate.Unused;
        return new(date, code, string.Join(Environment.NewLine, lines), fate);
    }

    /// <summary>Where one bag went, in a few words.</summary>
    public static string FateOf(DonatedBag bag, DateOnly today, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(bag);
        return bag.Status switch
        {
            BagStatus.Issued => "Issued"
                + (bag.IssuedAtUtc is { } issued ? " " + ShortDate(RecordFormatting.LocalDate(issued, zone), today) : string.Empty)
                + (bag.PatientName is { Length: > 0 } patient ? " to " + patient : string.Empty),
            BagStatus.Discarded => "Discarded",
            _ when bag.ExpiresOn < today => "Expired " + ShortDate(bag.ExpiresOn, today),
            _ => "In stock, use by " + ShortDate(bag.ExpiresOn, today),
        };
    }

    /// <summary>"12 Aug" this year, "12 Aug 2025" in another.</summary>
    private static string ShortDate(DateOnly date, DateOnly today) =>
        date.ToString(date.Year == today.Year ? "dd MMM" : "dd MMM yyyy", CultureInfo.InvariantCulture);
}
