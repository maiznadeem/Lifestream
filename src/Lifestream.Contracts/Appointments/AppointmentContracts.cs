using Lifestream.Domain;

namespace Lifestream.Contracts.Appointments;

public sealed record AppointmentListItem(
    int Id,
    int DonorId,
    string DonorName,
    BloodType DonorBloodType,
    DateTime PlacedAtUtc,
    DateTime SlotStartUtc,
    AppointmentStatus Status);

public sealed record AppointmentDetail(
    int Id,
    int DonorId,
    string DonorName,
    BloodType DonorBloodType,
    DateTime PlacedAtUtc,
    DateTime SlotStartUtc,
    AppointmentStatus Status,
    string? OverrideReason,
    int? ReceiptId,
    string RowVersion);

public sealed record BookAppointmentRequest(int DonorId, DateTime SlotStartUtc, string? OverrideReason);

/// <summary>PATCH body: moves the appointment to a new slot (the client keeps the time of day when only the date changes).</summary>
public sealed record RescheduleAppointmentRequest(string RowVersion, DateTime SlotStartUtc);

public sealed record SlotInfo(DateTime StartUtc, bool IsTaken);

public sealed record RecordDonationRequest(int Units, string? Notes);

public sealed record AppointmentQuery(
    string? Search = null,
    int? DonorId = null,
    AppointmentStatus? Status = null,
    BloodType? BloodType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null);
