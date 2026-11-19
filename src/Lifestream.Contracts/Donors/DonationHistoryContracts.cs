using Lifestream.Domain;

namespace Lifestream.Contracts.Donors;

/// <summary>The donors list's "Last donated" filter (<c>?lastDonated=</c>), counted back from today.</summary>
public enum DonationRecency
{
    /// <summary>Last donated within the last 3 months.</summary>
    Within3Months,

    /// <summary>Last donated 3 to 12 months ago.</summary>
    Within12Months,

    /// <summary>Last donated more than 12 months ago.</summary>
    Over12Months,

    /// <summary>Has never donated.</summary>
    Never,
}

/// <summary>Result of GET donors/{id}/donations: the donor's donations, newest first (at most <see cref="MaxItems"/>), and the totals over all of them.</summary>
/// <param name="Total">How many donations the donor has made.</param>
/// <param name="TotalUnits">How many units they gave in all.</param>
/// <param name="Items">The newest donations, with their bags.</param>
public sealed record DonorDonations(int Total, int TotalUnits, IReadOnlyList<DonationHistoryItem> Items)
{
    /// <summary>The most donations one response lists.</summary>
    public const int MaxItems = 20;
}

/// <summary>One donation: its receipt, its appointment, when it was given and the bags it made.</summary>
public sealed record DonationHistoryItem(int ReceiptId, int? AppointmentId, DateTime DonatedAtUtc, int Units, IReadOnlyList<DonatedBag> Bags);

/// <summary>
/// One bag of a donation and where it went: still in stock (until <see cref="ExpiresOn"/>, or expired after it), discarded, or issued (when, for which request and
/// patient).
/// </summary>
public sealed record DonatedBag(int Id, BagStatus Status, DateOnly ExpiresOn, DateTime? IssuedAtUtc, int? RequestId, int? PatientId, string? PatientName);
