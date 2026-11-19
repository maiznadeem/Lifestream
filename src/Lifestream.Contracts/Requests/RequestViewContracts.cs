namespace Lifestream.Contracts.Requests;

/// <summary>
/// The Requests screen's saved views (<c>?view=</c> on the list, with <c>utcOffsetMinutes</c> naming the caller's local calendar). Open is every pending request;
/// DueSoon the pending ones needed within 48 hours (by the day after tomorrow, overdue included); Fulfilled and Cancelled by status. The open views sort by the
/// date needed (overdue first) unless the caller asks for another sort.
/// </summary>
public enum RequestView
{
    Open,
    DueSoon,
    Fulfilled,
    Cancelled,
}

/// <summary>Result of GET requests/views: how many requests each view holds, with the same search and filters as the list.</summary>
public sealed record RequestViewCounts(int Open, int DueSoon, int Fulfilled, int Cancelled);

/// <summary>
/// Result of GET requests/{id}/bags: the bags a fulfil would issue now, in the order it takes them (the oldest unexpired bags of the request's group first), and how many
/// units of that group are in stock. Fewer bags than the request's units means the stock is short.
/// </summary>
public sealed record FulfilPreview(int Units, int Available, IReadOnlyList<SuggestedBag> Bags);

/// <summary>A bag a fulfil would issue: when it was collected, when it expires, and the donor it came from.</summary>
public sealed record SuggestedBag(int Id, DateTime CollectedAtUtc, DateOnly ExpiresOn, int? DonorId, string? DonorName);
