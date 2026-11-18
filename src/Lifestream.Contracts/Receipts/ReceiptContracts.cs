using Lifestream.Domain;

namespace Lifestream.Contracts.Receipts;

public sealed record ReceiptListItem(
    int Id,
    DateTime CreatedAtUtc,
    ReceiptKind Kind,
    BloodType BloodType,
    int Units,
    int StaffId,
    string StaffName,
    string PartyName,
    int? AppointmentId,
    int? RequestId);

public sealed record ReceiptDetail(
    int Id,
    DateTime CreatedAtUtc,
    ReceiptKind Kind,
    BloodType BloodType,
    int Units,
    int StaffId,
    string StaffName,
    string PartyName,
    int? AppointmentId,
    int? RequestId,
    string? Notes,
    IReadOnlyList<int> BagIds);

public sealed record ReceiptQuery(
    string? Search = null,
    ReceiptKind? Kind = null,
    BloodType? BloodType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null);
