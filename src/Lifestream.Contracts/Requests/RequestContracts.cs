using Lifestream.Domain;

namespace Lifestream.Contracts.Requests;

public sealed record RequestListItem(
    int Id,
    int PatientId,
    string PatientName,
    BloodType BloodType,
    int Units,
    DateTime PlacedAtUtc,
    DateOnly RequiredBy,
    RequestStatus Status);

public sealed record RequestDetail(
    int Id,
    int PatientId,
    string PatientName,
    BloodType BloodType,
    int Units,
    DateTime PlacedAtUtc,
    DateOnly RequiredBy,
    RequestStatus Status,
    int? ReceiptId,
    string RowVersion);

public sealed record CreateRequestRequest(int PatientId, int Units, DateOnly RequiredBy);

public sealed record UpdateRequestRequest(
    string RowVersion,
    Optional<int> Units = default,
    Optional<DateOnly> RequiredBy = default);

public sealed record RequestQuery(
    string? Search = null,
    int? PatientId = null,
    RequestStatus? Status = null,
    BloodType? BloodType = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null)
{
    /// <summary>Optional: one of the screen's saved views (Open, DueSoon, Fulfilled, Cancelled) in the caller's local calendar.</summary>
    public RequestView? View { get; init; }

    /// <summary>How far the caller's local calendar is from UTC, in minutes (for <see cref="View"/>); UTC when absent.</summary>
    public int? UtcOffsetMinutes { get; init; }
}
