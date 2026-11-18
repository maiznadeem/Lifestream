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
    string? Sort = null);
