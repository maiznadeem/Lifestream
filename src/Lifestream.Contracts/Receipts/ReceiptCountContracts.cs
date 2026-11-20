using Lifestream.Domain;

namespace Lifestream.Contracts.Receipts;

/// <summary>How many receipts match the filters, in all and by kind: the counts of the Receipts screen's All / Donations / Blood issues tabs.</summary>
public sealed record ReceiptCounts(int All, int Donations, int Issues);

/// <summary>The filters of GET receipts/counts: those of <see cref="ReceiptQuery"/> without the kind (each tab is a kind) and without paging.</summary>
public sealed record ReceiptCountQuery(
    string? Search = null,
    BloodType? BloodType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);
