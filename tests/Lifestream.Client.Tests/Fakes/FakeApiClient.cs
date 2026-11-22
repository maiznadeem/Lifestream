using Lifestream.Client.Api;
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
using Lifestream.Domain;

namespace Lifestream.Client.Tests.Fakes;

/// <summary>
/// A fake <see cref="ILifestreamApiClient"/> (the shell is built and tested against this, never the real API). Every call is
/// recorded by name; a call with no handler answers 404 not_found. Set <see cref="Handlers"/> to script an answer.
/// </summary>
public sealed class FakeApiClient : ILifestreamApiClient
{
    private readonly Lock _gate = new();
    private readonly List<string> _calls = [];

    /// <summary>Scripted answers by method name. The function returns an ApiResult or ApiResult&lt;T&gt; (it may await or throw).</summary>
    public Dictionary<string, Func<CancellationToken, Task<object>>> Handlers { get; } = [];

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public int CountOf(string name) => Calls.Count(c => c == name);

    public static SessionUser User(Role role = Role.Staff, bool demo = false, string name = "Test User") => new(1, name, role, demo, role == Role.Admin);

    public static TokenResponse Tokens(SessionUser? user = null) =>
        new("access-token-1", "refresh-token-1", DateTime.UtcNow.AddMinutes(15), user ?? User());

    public static ApiResult<T> Fail<T>(int status, string code, string title = "Failed") => ApiResult<T>.Fail(new ApiProblem(status, code, title, "corr-1"));

    public static ApiResult<T> Unreachable<T>() => ApiResult<T>.Fail(new ApiProblem(0, ClientProblemCodes.Unreachable, "The server could not be reached."));

    // The latest answer set for a call wins: a scripted answer replaces an answer per id (ById) for the same call.
    public void On<T>(string name, Func<CancellationToken, Task<ApiResult<T>>> handler)
    {
        ById.Remove(name);
        Handlers[name] = async ct => await handler(ct);
    }

    public void On<T>(string name, ApiResult<T> answer)
    {
        ById.Remove(name);
        Handlers[name] = _ => Task.FromResult<object>(answer);
    }

    private async Task<ApiResult<T>> Run<T>(string name, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(name);
        }

        ct.ThrowIfCancellationRequested();
        if (Handlers.TryGetValue(name, out var handler))
        {
            return (ApiResult<T>)await handler(ct);
        }

        // A warm server unless the test says otherwise: since 2.1.9 a sign-in waits for the readiness check when that check could not reach
        // the server, so an unanswered readiness check would turn every sign-in of a test into a wait for the server.
        if (name == nameof(GetReadinessAsync))
        {
            return (ApiResult<T>)(object)ApiResult<ReadinessResponse>.Ok(new ReadinessResponse(DatabaseStates.Ready));
        }

        return Fail<T>(404, ProblemCodes.NotFound, "No fake handler");
    }

    private async Task<ApiResult> RunVoid(string name, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(name);
        }

        ct.ThrowIfCancellationRequested();
        if (Handlers.TryGetValue(name, out var handler))
        {
            return (ApiResult)await handler(ct);
        }

        return ApiResult.Success;
    }

    public async Task WarmUpAsync(CancellationToken ct = default) => await RunVoid(nameof(WarmUpAsync), ct);

    public Task<ApiResult<ReadinessResponse>> GetReadinessAsync(CancellationToken ct = default) => Run<ReadinessResponse>(nameof(GetReadinessAsync), ct);

    public Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default) => Run<TokenResponse>(nameof(LoginAsync), ct);

    public Task<ApiResult<TokenResponse>> DemoLoginAsync(CancellationToken ct = default) => Run<TokenResponse>(nameof(DemoLoginAsync), ct);

    /// <summary>The refresh tokens that LogoutAsync was called with, in order.</summary>
    public List<string> LogoutTokens { get; } = [];

    /// <summary>When set, LogoutAsync throws before it returns a task, like a client that fails while building the request.</summary>
    public bool LogoutThrowsSynchronously { get; set; }

    public Task<ApiResult> LogoutAsync(string refreshToken, CancellationToken ct = default)
    {
        if (LogoutThrowsSynchronously)
        {
            throw new InvalidOperationException("logout failed before returning a task");
        }

        lock (LogoutTokens)
        {
            LogoutTokens.Add(refreshToken);
        }

        return RunVoid(nameof(LogoutAsync), ct);
    }

    public Task<ApiResult<PagedResult<DonorListItem>>> GetDonorsAsync(DonorQuery query, CancellationToken ct = default) => Run<PagedResult<DonorListItem>>(nameof(GetDonorsAsync), ct);

    public Task<ApiResult<DonorDetail>> GetDonorAsync(int id, CancellationToken ct = default) =>
        DonorById is { } byId ? RecordThen(nameof(GetDonorAsync), () => byId(id)) : Run<DonorDetail>(nameof(GetDonorAsync), ct);

    /// <summary>When set, answers GET donors/{id} per id (the scripted handler cannot see the id).</summary>
    public Func<int, ApiResult<DonorDetail>>? DonorById { get; set; }

    /// <summary>
    /// Answers per id for the detail calls that take one (GetPatientAsync, GetAppointmentAsync, GetRequestAsync), by method name; the function returns the
    /// ApiResult&lt;T&gt; of that call, so a list and its detail agree on every record. A later <see cref="On{T}(string, ApiResult{T})"/> for the same call replaces it.
    /// </summary>
    public Dictionary<string, Func<int, object>> ById { get; } = [];

    private Task<ApiResult<T>> RunById<T>(string name, int id, CancellationToken ct) =>
        ById.TryGetValue(name, out var answer) ? RecordThen(name, () => (ApiResult<T>)answer(id)) : Run<T>(name, ct);

    private Task<ApiResult<T>> RecordThen<T>(string name, Func<ApiResult<T>> answer)
    {
        lock (_gate)
        {
            _calls.Add(name);
        }

        return Task.FromResult(answer());
    }

    public Task<ApiResult<DonorDetail>> CreateDonorAsync(CreateDonorRequest request, CancellationToken ct = default) => Run<DonorDetail>(nameof(CreateDonorAsync), ct);

    public Task<ApiResult<DonorDetail>> UpdateDonorAsync(int id, UpdateDonorRequest request, CancellationToken ct = default) => Run<DonorDetail>(nameof(UpdateDonorAsync), ct);

    public Task<ApiResult> DeleteDonorAsync(int id, CancellationToken ct = default) => RunVoid(nameof(DeleteDonorAsync), ct);

    public Task<ApiResult<DonorDetail>> RestoreDonorAsync(int id, CancellationToken ct = default) => Run<DonorDetail>(nameof(RestoreDonorAsync), ct);

    public Task<ApiResult<DonorEligibility>> GetDonorEligibilityAsync(int id, CancellationToken ct = default) => Run<DonorEligibility>(nameof(GetDonorEligibilityAsync), ct);

    public Task<ApiResult<PagedResult<PatientListItem>>> GetPatientsAsync(PatientQuery query, CancellationToken ct = default) => Run<PagedResult<PatientListItem>>(nameof(GetPatientsAsync), ct);

    public Task<ApiResult<PatientDetail>> GetPatientAsync(int id, CancellationToken ct = default) => RunById<PatientDetail>(nameof(GetPatientAsync), id, ct);

    public Task<ApiResult<PatientDetail>> CreatePatientAsync(CreatePatientRequest request, CancellationToken ct = default) => Run<PatientDetail>(nameof(CreatePatientAsync), ct);

    public Task<ApiResult<PatientDetail>> UpdatePatientAsync(int id, UpdatePatientRequest request, CancellationToken ct = default) => Run<PatientDetail>(nameof(UpdatePatientAsync), ct);

    public Task<ApiResult> DeletePatientAsync(int id, CancellationToken ct = default) => RunVoid(nameof(DeletePatientAsync), ct);

    public Task<ApiResult<PatientDetail>> RestorePatientAsync(int id, CancellationToken ct = default) => Run<PatientDetail>(nameof(RestorePatientAsync), ct);

    public Task<ApiResult<PagedResult<AppointmentListItem>>> GetAppointmentsAsync(AppointmentQuery query, CancellationToken ct = default) =>
        AppointmentsFor?.Invoke(query) is { } answer ? RecordThen(nameof(GetAppointmentsAsync), () => answer) : Run<PagedResult<AppointmentListItem>>(nameof(GetAppointmentsAsync), ct);

    /// <summary>When set, answers GET appointments for the queries it knows (null: the scripted handler answers), so one call can answer two callers differently.</summary>
    public Func<AppointmentQuery, ApiResult<PagedResult<AppointmentListItem>>?>? AppointmentsFor { get; set; }

    public Task<ApiResult<AppointmentDetail>> GetAppointmentAsync(int id, CancellationToken ct = default) => RunById<AppointmentDetail>(nameof(GetAppointmentAsync), id, ct);

    public Task<ApiResult<AppointmentDetail>> BookAppointmentAsync(BookAppointmentRequest request, CancellationToken ct = default) => Run<AppointmentDetail>(nameof(BookAppointmentAsync), ct);

    public Task<ApiResult<AppointmentDetail>> RescheduleAppointmentAsync(int id, RescheduleAppointmentRequest request, CancellationToken ct = default) => Run<AppointmentDetail>(nameof(RescheduleAppointmentAsync), ct);

    public Task<ApiResult<IReadOnlyList<SlotInfo>>> GetSlotsAsync(DateOnly day, CancellationToken ct = default) => Run<IReadOnlyList<SlotInfo>>(nameof(GetSlotsAsync), ct);

    public Task<ApiResult<ReceiptDetail>> RecordDonationAsync(int appointmentId, RecordDonationRequest request, CancellationToken ct = default) => Run<ReceiptDetail>(nameof(RecordDonationAsync), ct);

    public Task<ApiResult> CancelAppointmentAsync(int id, CancellationToken ct = default) => RunVoid(nameof(CancelAppointmentAsync), ct);

    public Task<ApiResult<PagedResult<RequestListItem>>> GetRequestsAsync(RequestQuery query, CancellationToken ct = default) =>
        RequestsFor?.Invoke(query) is { } answer ? RecordThen(nameof(GetRequestsAsync), () => answer) : Run<PagedResult<RequestListItem>>(nameof(GetRequestsAsync), ct);

    /// <summary>When set, answers GET requests for the queries it knows (null: the scripted handler answers), so one call can answer two callers differently.</summary>
    public Func<RequestQuery, ApiResult<PagedResult<RequestListItem>>?>? RequestsFor { get; set; }

    public Task<ApiResult<RequestDetail>> GetRequestAsync(int id, CancellationToken ct = default) => RunById<RequestDetail>(nameof(GetRequestAsync), id, ct);

    public Task<ApiResult<RequestDetail>> CreateRequestAsync(CreateRequestRequest request, CancellationToken ct = default) => Run<RequestDetail>(nameof(CreateRequestAsync), ct);

    public Task<ApiResult<RequestDetail>> UpdateRequestAsync(int id, UpdateRequestRequest request, CancellationToken ct = default) => Run<RequestDetail>(nameof(UpdateRequestAsync), ct);

    public Task<ApiResult<ReceiptDetail>> FulfilRequestAsync(int id, CancellationToken ct = default) => Run<ReceiptDetail>(nameof(FulfilRequestAsync), ct);

    public Task<ApiResult> CancelRequestAsync(int id, CancellationToken ct = default) => RunVoid(nameof(CancelRequestAsync), ct);

    public Task<ApiResult<PagedResult<ReceiptListItem>>> GetReceiptsAsync(ReceiptQuery query, CancellationToken ct = default) => Run<PagedResult<ReceiptListItem>>(nameof(GetReceiptsAsync), ct);

    public Task<ApiResult<ReceiptDetail>> GetReceiptAsync(int id, CancellationToken ct = default) => Run<ReceiptDetail>(nameof(GetReceiptAsync), ct);

    public Task<ApiResult<IReadOnlyList<StockItem>>> GetStockAsync(CancellationToken ct = default) => Run<IReadOnlyList<StockItem>>(nameof(GetStockAsync), ct);

    public Task<ApiResult<DashboardResponse>> GetDashboardAsync(DateOnly rangeStart, DateOnly rangeEnd, int? utcOffsetMinutes = null, CancellationToken ct = default) => Run<DashboardResponse>(nameof(GetDashboardAsync), ct);

    public Task<ApiResult<PagedResult<StaffListItem>>> GetStaffAsync(string? search = null, int page = 1, int pageSize = Paging.DefaultPageSize, string? sort = null, int? minSalary = null, int? maxSalary = null, CancellationToken ct = default) => Run<PagedResult<StaffListItem>>(nameof(GetStaffAsync), ct);

    public Task<ApiResult<StaffDetail>> GetStaffMemberAsync(int id, CancellationToken ct = default) => Run<StaffDetail>(nameof(GetStaffMemberAsync), ct);

    public Task<ApiResult<StaffDetail>> CreateStaffAsync(CreateStaffRequest request, CancellationToken ct = default) => Run<StaffDetail>(nameof(CreateStaffAsync), ct);

    public Task<ApiResult<StaffDetail>> UpdateStaffAsync(int id, UpdateStaffRequest request, CancellationToken ct = default) => Run<StaffDetail>(nameof(UpdateStaffAsync), ct);

    public Task<ApiResult> DeleteStaffAsync(int id, CancellationToken ct = default) => RunVoid(nameof(DeleteStaffAsync), ct);

    public Task<ApiResult<StaffDetail>> RestoreStaffAsync(int id, CancellationToken ct = default) => Run<StaffDetail>(nameof(RestoreStaffAsync), ct);

    public Task<ApiResult> ResetStaffPasswordAsync(int id, ResetPasswordRequest request, CancellationToken ct = default) => RunVoid(nameof(ResetStaffPasswordAsync), ct);

    public Task<ApiResult<byte[]>> GetStaffPhotoAsync(int id, CancellationToken ct = default) => Run<byte[]>(nameof(GetStaffPhotoAsync), ct);

    public Task<ApiResult> SetStaffPhotoAsync(int id, byte[] jpeg, CancellationToken ct = default) => RunVoid(nameof(SetStaffPhotoAsync), ct);

    public Task<ApiResult<ProfileResponse>> GetProfileAsync(CancellationToken ct = default) => Run<ProfileResponse>(nameof(GetProfileAsync), ct);

    public Task<ApiResult<ProfileResponse>> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default) => Run<ProfileResponse>(nameof(UpdateProfileAsync), ct);

    public Task<ApiResult> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) => RunVoid(nameof(ChangePasswordAsync), ct);

    public Task<ApiResult<byte[]>> GetMyPhotoAsync(CancellationToken ct = default) => Run<byte[]>(nameof(GetMyPhotoAsync), ct);

    public Task<ApiResult> SetMyPhotoAsync(byte[] jpeg, CancellationToken ct = default) => RunVoid(nameof(SetMyPhotoAsync), ct);

    public Task<ApiResult<IReadOnlyList<RecordOption>>> SearchRecordsAsync(RecordKind kind, string? search, CancellationToken ct = default) => Run<IReadOnlyList<RecordOption>>(nameof(SearchRecordsAsync), ct);

    public Task<ApiResult<EnumLookups>> GetEnumLookupsAsync(CancellationToken ct = default) => Run<EnumLookups>(nameof(GetEnumLookupsAsync), ct);

    public Task<ApiResult> ResetDemoAsync(CancellationToken ct = default) => RunVoid(nameof(ResetDemoAsync), ct);

    public Task<ApiResult<StockBagList>> GetStockBagsAsync(BloodType bloodType, CancellationToken ct = default) => Run<StockBagList>(nameof(GetStockBagsAsync), ct);

    public Task<ApiResult<ReceiptCounts>> GetReceiptCountsAsync(ReceiptCountQuery query, CancellationToken ct = default) => Run<ReceiptCounts>(nameof(GetReceiptCountsAsync), ct);

    public Task<ApiResult<DonorDonations>> GetDonorDonationsAsync(int id, CancellationToken ct = default) => Run<DonorDonations>(nameof(GetDonorDonationsAsync), ct);

    public Task<ApiResult<AppointmentViewCounts>> GetAppointmentViewCountsAsync(AppointmentQuery query, CancellationToken ct = default) => Run<AppointmentViewCounts>(nameof(GetAppointmentViewCountsAsync), ct);

    public Task<ApiResult<RequestViewCounts>> GetRequestViewCountsAsync(RequestQuery query, CancellationToken ct = default) => Run<RequestViewCounts>(nameof(GetRequestViewCountsAsync), ct);

    public Task<ApiResult<FulfilPreview>> GetFulfilPreviewAsync(int requestId, CancellationToken ct = default) => Run<FulfilPreview>(nameof(GetFulfilPreviewAsync), ct);
}
