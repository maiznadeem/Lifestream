using Lifestream.Contracts;
using Lifestream.Contracts.Appointments;
using Lifestream.Contracts.Auth;
using Lifestream.Contracts.Dashboard;
using Lifestream.Contracts.Donors;
using Lifestream.Contracts.Health;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Patients;
using Lifestream.Contracts.Profile;
using Lifestream.Contracts.Receipts;
using Lifestream.Contracts.Requests;
using Lifestream.Contracts.Staff;
using Lifestream.Contracts.Stock;

namespace Lifestream.Client.Api;

/// <summary>Which record a <see cref="RecordOption"/> lookup searches.</summary>
public enum RecordKind
{
    Donor,
    Patient,
    Staff,
    Appointment,
    Request,
}

/// <summary>
/// One typed method per API route. Every method returns an <see cref="ApiResult{T}"/>; none throws for a server or network
/// failure (a cancelled <see cref="CancellationToken"/> still throws <see cref="OperationCanceledException"/>).
/// </summary>
public interface ILifestreamApiClient
{
    /// <summary>Sends /healthz once, ignoring the outcome, so the container is already awake when the user has typed a password.</summary>
    [Common.OutsideOperation("A service warms the container up before any screen exists.")]
    Task WarmUpAsync(CancellationToken ct = default);

    Task<ApiResult<ReadinessResponse>> GetReadinessAsync(CancellationToken ct = default);

    // Auth
    Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default);

    Task<ApiResult<TokenResponse>> DemoLoginAsync(CancellationToken ct = default);

    /// <summary>
    /// Revokes the given refresh token on the server: one attempt, no cold-start retries, a 5 s timeout. The caller takes the token
    /// from the session before clearing it, so sign-out never waits on this (SHELL-17).
    /// </summary>
    [Common.OutsideOperation("Sign-out is made by the app flow after the screens are gone and must not wait on or be cancelled by one.")]
    Task<ApiResult> LogoutAsync(string refreshToken, CancellationToken ct = default);

    // Donors
    Task<ApiResult<PagedResult<DonorListItem>>> GetDonorsAsync(DonorQuery query, CancellationToken ct = default);

    Task<ApiResult<DonorDetail>> GetDonorAsync(int id, CancellationToken ct = default);

    Task<ApiResult<DonorDetail>> CreateDonorAsync(CreateDonorRequest request, CancellationToken ct = default);

    Task<ApiResult<DonorDetail>> UpdateDonorAsync(int id, UpdateDonorRequest request, CancellationToken ct = default);

    Task<ApiResult> DeleteDonorAsync(int id, CancellationToken ct = default);

    /// <summary>Undo of a delete: the API puts the donor back with the same id and every value (within a few seconds of the delete).</summary>
    Task<ApiResult<DonorDetail>> RestoreDonorAsync(int id, CancellationToken ct = default);

    Task<ApiResult<DonorEligibility>> GetDonorEligibilityAsync(int id, CancellationToken ct = default);

    // Patients
    Task<ApiResult<PagedResult<PatientListItem>>> GetPatientsAsync(PatientQuery query, CancellationToken ct = default);

    Task<ApiResult<PatientDetail>> GetPatientAsync(int id, CancellationToken ct = default);

    Task<ApiResult<PatientDetail>> CreatePatientAsync(CreatePatientRequest request, CancellationToken ct = default);

    Task<ApiResult<PatientDetail>> UpdatePatientAsync(int id, UpdatePatientRequest request, CancellationToken ct = default);

    Task<ApiResult> DeletePatientAsync(int id, CancellationToken ct = default);

    /// <summary>Undo of a delete: the API puts the patient back with the same id and every value (within a few seconds of the delete).</summary>
    Task<ApiResult<PatientDetail>> RestorePatientAsync(int id, CancellationToken ct = default);

    // Appointments
    Task<ApiResult<PagedResult<AppointmentListItem>>> GetAppointmentsAsync(AppointmentQuery query, CancellationToken ct = default);

    Task<ApiResult<AppointmentDetail>> GetAppointmentAsync(int id, CancellationToken ct = default);

    Task<ApiResult<AppointmentDetail>> BookAppointmentAsync(BookAppointmentRequest request, CancellationToken ct = default);

    Task<ApiResult<AppointmentDetail>> RescheduleAppointmentAsync(int id, RescheduleAppointmentRequest request, CancellationToken ct = default);

    Task<ApiResult<IReadOnlyList<SlotInfo>>> GetSlotsAsync(DateOnly day, CancellationToken ct = default);

    Task<ApiResult<ReceiptDetail>> RecordDonationAsync(int appointmentId, RecordDonationRequest request, CancellationToken ct = default);

    Task<ApiResult> CancelAppointmentAsync(int id, CancellationToken ct = default);

    // Requests
    Task<ApiResult<PagedResult<RequestListItem>>> GetRequestsAsync(RequestQuery query, CancellationToken ct = default);

    Task<ApiResult<RequestDetail>> GetRequestAsync(int id, CancellationToken ct = default);

    Task<ApiResult<RequestDetail>> CreateRequestAsync(CreateRequestRequest request, CancellationToken ct = default);

    Task<ApiResult<RequestDetail>> UpdateRequestAsync(int id, UpdateRequestRequest request, CancellationToken ct = default);

    Task<ApiResult<ReceiptDetail>> FulfilRequestAsync(int id, CancellationToken ct = default);

    Task<ApiResult> CancelRequestAsync(int id, CancellationToken ct = default);

    // Receipts
    Task<ApiResult<PagedResult<ReceiptListItem>>> GetReceiptsAsync(ReceiptQuery query, CancellationToken ct = default);

    Task<ApiResult<ReceiptDetail>> GetReceiptAsync(int id, CancellationToken ct = default);

    // Stock and dashboard
    Task<ApiResult<IReadOnlyList<StockItem>>> GetStockAsync(CancellationToken ct = default);

    Task<ApiResult<DashboardResponse>> GetDashboardAsync(DateOnly rangeStart, DateOnly rangeEnd, int? utcOffsetMinutes = null, CancellationToken ct = default);

    // Staff (the list is paged like every list; a plain JSON array is accepted too)
    Task<ApiResult<PagedResult<StaffListItem>>> GetStaffAsync(string? search = null, int page = 1, int pageSize = Paging.DefaultPageSize, string? sort = null, int? minSalary = null, int? maxSalary = null, CancellationToken ct = default);

    Task<ApiResult<StaffDetail>> GetStaffMemberAsync(int id, CancellationToken ct = default);

    Task<ApiResult<StaffDetail>> CreateStaffAsync(CreateStaffRequest request, CancellationToken ct = default);

    Task<ApiResult<StaffDetail>> UpdateStaffAsync(int id, UpdateStaffRequest request, CancellationToken ct = default);

    Task<ApiResult> DeleteStaffAsync(int id, CancellationToken ct = default);

    /// <summary>Undo of a staff delete: the API puts the person back with the same id, credential and photo within a few seconds of the delete.</summary>
    Task<ApiResult<StaffDetail>> RestoreStaffAsync(int id, CancellationToken ct = default);

    /// <summary>Sets the temporary password the admin typed; the API answers 204 and never returns a password. The person must choose a new one at next sign-in.</summary>
    Task<ApiResult> ResetStaffPasswordAsync(int id, ResetPasswordRequest request, CancellationToken ct = default);

    Task<ApiResult<byte[]>> GetStaffPhotoAsync(int id, CancellationToken ct = default);

    Task<ApiResult> SetStaffPhotoAsync(int id, byte[] jpeg, CancellationToken ct = default);

    // Profile
    Task<ApiResult<ProfileResponse>> GetProfileAsync(CancellationToken ct = default);

    Task<ApiResult<ProfileResponse>> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default);

    Task<ApiResult> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default);

    Task<ApiResult<byte[]>> GetMyPhotoAsync(CancellationToken ct = default);

    Task<ApiResult> SetMyPhotoAsync(byte[] jpeg, CancellationToken ct = default);

    // Lookups and demo
    Task<ApiResult<IReadOnlyList<RecordOption>>> SearchRecordsAsync(RecordKind kind, string? search, CancellationToken ct = default);

    Task<ApiResult<EnumLookups>> GetEnumLookupsAsync(CancellationToken ct = default);

    Task<ApiResult> ResetDemoAsync(CancellationToken ct = default);
}
