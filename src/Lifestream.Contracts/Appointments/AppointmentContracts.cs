using Lifestream.Domain;

namespace Lifestream.Contracts.Appointments;

public sealed record AppointmentListItem(
    int Id,
    int DonorId,
    string DonorName,
    BloodType DonorBloodType,
    DateTime PlacedAtUtc,
    DateTime SlotStartUtc,
    AppointmentStatus Status)
{
    /// <summary>True when the donor had no completed donation before this appointment (a first-time donor). False from an older server.</summary>
    public bool DonorFirstTime { get; init; }
}

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
public sealed record RescheduleAppointmentRequest(string RowVersion, DateTime SlotStartUtc)
{
    /// <summary>Why the donor is booked on the new day although ineligible on it; required by the API only in that case (stored on the appointment).</summary>
    public string? OverrideReason { get; init; }
}

public sealed record SlotInfo(DateTime StartUtc, bool IsTaken);

public sealed record RecordDonationRequest(int Units, string? Notes)
{
    /// <summary>Why a donor who is ineligible today may donate anyway; required by the API only in that case (stored on the appointment).</summary>
    public string? OverrideReason { get; init; }
}

public sealed record AppointmentQuery(
    string? Search = null,
    int? DonorId = null,
    AppointmentStatus? Status = null,
    BloodType? BloodType = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize,
    string? Sort = null)
{
    /// <summary>Optional: one of the screen's saved views (Today, Upcoming, Past, Cancelled) in the caller's local calendar.</summary>
    public AppointmentView? View { get; init; }

    /// <summary>How far the caller's local calendar is from UTC, in minutes (for <see cref="View"/>); UTC when absent.</summary>
    public int? UtcOffsetMinutes { get; init; }
}
