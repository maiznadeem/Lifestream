using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lifestream.Client.Session;
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

/// <summary>
/// The API client. It owns no connection: it uses the one shared <see cref="HttpClient"/> (SHELL-08), whose handler does
/// the cold-start retries (SHELL-07). Every call is async, so nothing here can block the UI thread (SHELL-09).
/// </summary>
public sealed class LifestreamApiClient(HttpClient http, ISessionService session) : ILifestreamApiClient, IDisposable
{
    private static readonly JsonSerializerOptions Json = ContractJson.CreateOptions();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public void Dispose() => _refreshGate.Dispose();

    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = NewRequest(HttpMethod.Get, ApiRoutes.Healthz);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Warming is best effort; the first real call reports any failure.
        }
    }

    public Task<ApiResult<ReadinessResponse>> GetReadinessAsync(CancellationToken ct = default) =>
        SendAsync<ReadinessResponse>(HttpMethod.Get, ApiRoutes.Readyz, ct: ct);

    // Auth
    public Task<ApiResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        SendAsync<TokenResponse>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthLogin), request, authorize: false, ct: ct);

    public Task<ApiResult<TokenResponse>> DemoLoginAsync(CancellationToken ct = default) =>
        SendAsync<TokenResponse>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthDemo), null, authorize: false, ct: ct);

    /// <summary>
    /// Revokes the refresh token on the server. It is sent without an access token (so a 401 can never start a refresh) and the
    /// refresh token is read from the session when the request is built, not earlier. With no refresh token there is nothing to revoke.
    /// </summary>
    public async Task<ApiResult> LogoutAsync(CancellationToken ct = default)
    {
        if (session.RefreshToken is null)
        {
            return ApiResult.Success;
        }

        var outcome = await ExecuteAsync(
            HttpMethod.Post,
            ApiRoutes.Path(ApiRoutes.AuthLogout),
            () => JsonContent.Create(new RefreshRequest(session.RefreshToken ?? string.Empty), options: Json),
            authorize: false,
            ct).ConfigureAwait(false);
        using var response = outcome.Response;
        if (response is null)
        {
            return ApiResult.Fail(outcome.Problem!);
        }

        return response.IsSuccessStatusCode ? ApiResult.Success : ApiResult.Fail(await ReadProblemAsync(response, ct).ConfigureAwait(false));
    }

    // Donors
    public Task<ApiResult<PagedResult<DonorListItem>>> GetDonorsAsync(DonorQuery query, CancellationToken ct = default) =>
        SendAsync<PagedResult<DonorListItem>>(HttpMethod.Get, Url(ApiRoutes.Donors, ("search", query.Search), ("bloodType", query.BloodType), ("page", query.Page), ("pageSize", query.PageSize), ("sort", query.Sort)), ct: ct);

    public Task<ApiResult<DonorDetail>> GetDonorAsync(int id, CancellationToken ct = default) =>
        SendAsync<DonorDetail>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.DonorById, id), ct: ct);

    public Task<ApiResult<DonorDetail>> CreateDonorAsync(CreateDonorRequest request, CancellationToken ct = default) =>
        SendAsync<DonorDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Donors), request, ct: ct);

    public Task<ApiResult<DonorDetail>> UpdateDonorAsync(int id, UpdateDonorRequest request, CancellationToken ct = default) =>
        SendAsync<DonorDetail>(HttpMethod.Patch, ApiRoutes.Path(ApiRoutes.DonorById, id), request, ct: ct);

    public Task<ApiResult> DeleteDonorAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, ApiRoutes.Path(ApiRoutes.DonorById, id), null, ct);

    public Task<ApiResult<DonorEligibility>> GetDonorEligibilityAsync(int id, CancellationToken ct = default) =>
        SendAsync<DonorEligibility>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.DonorEligibility, id), ct: ct);

    // Patients
    public Task<ApiResult<PagedResult<PatientListItem>>> GetPatientsAsync(PatientQuery query, CancellationToken ct = default) =>
        SendAsync<PagedResult<PatientListItem>>(HttpMethod.Get, Url(ApiRoutes.Patients, ("search", query.Search), ("bloodType", query.BloodType), ("page", query.Page), ("pageSize", query.PageSize), ("sort", query.Sort)), ct: ct);

    public Task<ApiResult<PatientDetail>> GetPatientAsync(int id, CancellationToken ct = default) =>
        SendAsync<PatientDetail>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.PatientById, id), ct: ct);

    public Task<ApiResult<PatientDetail>> CreatePatientAsync(CreatePatientRequest request, CancellationToken ct = default) =>
        SendAsync<PatientDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Patients), request, ct: ct);

    public Task<ApiResult<PatientDetail>> UpdatePatientAsync(int id, UpdatePatientRequest request, CancellationToken ct = default) =>
        SendAsync<PatientDetail>(HttpMethod.Patch, ApiRoutes.Path(ApiRoutes.PatientById, id), request, ct: ct);

    public Task<ApiResult> DeletePatientAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, ApiRoutes.Path(ApiRoutes.PatientById, id), null, ct);

    // Appointments
    public Task<ApiResult<PagedResult<AppointmentListItem>>> GetAppointmentsAsync(AppointmentQuery query, CancellationToken ct = default) =>
        SendAsync<PagedResult<AppointmentListItem>>(HttpMethod.Get, Url(ApiRoutes.Appointments, ("search", query.Search), ("donorId", query.DonorId), ("status", query.Status), ("bloodType", query.BloodType), ("fromUtc", query.FromUtc), ("toUtc", query.ToUtc), ("page", query.Page), ("pageSize", query.PageSize), ("sort", query.Sort)), ct: ct);

    public Task<ApiResult<AppointmentDetail>> GetAppointmentAsync(int id, CancellationToken ct = default) =>
        SendAsync<AppointmentDetail>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.AppointmentById, id), ct: ct);

    public Task<ApiResult<AppointmentDetail>> BookAppointmentAsync(BookAppointmentRequest request, CancellationToken ct = default) =>
        SendAsync<AppointmentDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Appointments), request, ct: ct);

    public Task<ApiResult<AppointmentDetail>> RescheduleAppointmentAsync(int id, RescheduleAppointmentRequest request, CancellationToken ct = default) =>
        SendAsync<AppointmentDetail>(HttpMethod.Patch, ApiRoutes.Path(ApiRoutes.AppointmentById, id), request, ct: ct);

    public Task<ApiResult<IReadOnlyList<SlotInfo>>> GetSlotsAsync(DateOnly day, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<SlotInfo>>(HttpMethod.Get, Url(ApiRoutes.AppointmentSlots, ("date", day)), ct: ct);

    public Task<ApiResult<ReceiptDetail>> RecordDonationAsync(int appointmentId, RecordDonationRequest request, CancellationToken ct = default) =>
        SendAsync<ReceiptDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AppointmentDonation, appointmentId), request, ct: ct);

    public Task<ApiResult> CancelAppointmentAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AppointmentCancel, id), null, ct);

    // Requests
    public Task<ApiResult<PagedResult<RequestListItem>>> GetRequestsAsync(RequestQuery query, CancellationToken ct = default) =>
        SendAsync<PagedResult<RequestListItem>>(HttpMethod.Get, Url(ApiRoutes.Requests, ("search", query.Search), ("patientId", query.PatientId), ("status", query.Status), ("bloodType", query.BloodType), ("page", query.Page), ("pageSize", query.PageSize), ("sort", query.Sort)), ct: ct);

    public Task<ApiResult<RequestDetail>> GetRequestAsync(int id, CancellationToken ct = default) =>
        SendAsync<RequestDetail>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.RequestById, id), ct: ct);

    public Task<ApiResult<RequestDetail>> CreateRequestAsync(CreateRequestRequest request, CancellationToken ct = default) =>
        SendAsync<RequestDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Requests), request, ct: ct);

    public Task<ApiResult<RequestDetail>> UpdateRequestAsync(int id, UpdateRequestRequest request, CancellationToken ct = default) =>
        SendAsync<RequestDetail>(HttpMethod.Patch, ApiRoutes.Path(ApiRoutes.RequestById, id), request, ct: ct);

    public Task<ApiResult<ReceiptDetail>> FulfilRequestAsync(int id, CancellationToken ct = default) =>
        SendAsync<ReceiptDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.RequestFulfil, id), null, ct: ct);

    public Task<ApiResult> CancelRequestAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.RequestCancel, id), null, ct);

    // Receipts
    public Task<ApiResult<PagedResult<ReceiptListItem>>> GetReceiptsAsync(ReceiptQuery query, CancellationToken ct = default) =>
        SendAsync<PagedResult<ReceiptListItem>>(HttpMethod.Get, Url(ApiRoutes.Receipts, ("search", query.Search), ("kind", query.Kind), ("bloodType", query.BloodType), ("fromUtc", query.FromUtc), ("toUtc", query.ToUtc), ("page", query.Page), ("pageSize", query.PageSize), ("sort", query.Sort)), ct: ct);

    public Task<ApiResult<ReceiptDetail>> GetReceiptAsync(int id, CancellationToken ct = default) =>
        SendAsync<ReceiptDetail>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.ReceiptById, id), ct: ct);

    // Stock and dashboard
    public Task<ApiResult<IReadOnlyList<StockItem>>> GetStockAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<StockItem>>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.Stock), ct: ct);

    public Task<ApiResult<DashboardResponse>> GetDashboardAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken ct = default) =>
        SendAsync<DashboardResponse>(HttpMethod.Get, Url(ApiRoutes.Dashboard, ("from", rangeStart), ("to", rangeEnd)), ct: ct);

    // Staff
    public async Task<ApiResult<PagedResult<StaffListItem>>> GetStaffAsync(string? search = null, int page = 1, int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var raw = await SendAsync<JsonElement>(HttpMethod.Get, Url(ApiRoutes.Staff, ("search", search), ("page", page), ("pageSize", pageSize)), ct: ct).ConfigureAwait(false);
        if (!raw.IsOk)
        {
            return ApiResult<PagedResult<StaffListItem>>.Fail(raw.Problem!);
        }

        try
        {
            if (raw.Value.ValueKind == JsonValueKind.Array)
            {
                var items = raw.Value.Deserialize<List<StaffListItem>>(Json) ?? [];
                return ApiResult<PagedResult<StaffListItem>>.Ok(new PagedResult<StaffListItem>(items, items.Count, 1, Math.Max(items.Count, 1)));
            }

            return ApiResult<PagedResult<StaffListItem>>.Ok(raw.Value.Deserialize<PagedResult<StaffListItem>>(Json)!);
        }
        catch (JsonException)
        {
            return ApiResult<PagedResult<StaffListItem>>.Fail(BadResponse());
        }
    }

    public Task<ApiResult<StaffDetail>> GetStaffMemberAsync(int id, CancellationToken ct = default) =>
        SendAsync<StaffDetail>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.StaffById, id), ct: ct);

    public Task<ApiResult<StaffDetail>> CreateStaffAsync(CreateStaffRequest request, CancellationToken ct = default) =>
        SendAsync<StaffDetail>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.Staff), request, ct: ct);

    public Task<ApiResult<StaffDetail>> UpdateStaffAsync(int id, UpdateStaffRequest request, CancellationToken ct = default) =>
        SendAsync<StaffDetail>(HttpMethod.Patch, ApiRoutes.Path(ApiRoutes.StaffById, id), request, ct: ct);

    public Task<ApiResult> DeleteStaffAsync(int id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, ApiRoutes.Path(ApiRoutes.StaffById, id), null, ct);

    public Task<ApiResult<ResetPasswordResponse>> ResetStaffPasswordAsync(int id, CancellationToken ct = default) =>
        SendAsync<ResetPasswordResponse>(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.StaffResetPassword, id), null, ct: ct);

    public Task<ApiResult<byte[]>> GetStaffPhotoAsync(int id, CancellationToken ct = default) =>
        GetBytesAsync(ApiRoutes.Path(ApiRoutes.StaffPhoto, id), ct);

    public Task<ApiResult> SetStaffPhotoAsync(int id, byte[] jpeg, CancellationToken ct = default) =>
        SendBytesAsync(ApiRoutes.Path(ApiRoutes.StaffPhoto, id), jpeg, ct);

    // Profile
    public Task<ApiResult<ProfileResponse>> GetProfileAsync(CancellationToken ct = default) =>
        SendAsync<ProfileResponse>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.Me), ct: ct);

    public Task<ApiResult<ProfileResponse>> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken ct = default) =>
        SendAsync<ProfileResponse>(HttpMethod.Patch, ApiRoutes.Path(ApiRoutes.Me), request, ct: ct);

    public Task<ApiResult> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.MePassword), request, ct);

    public Task<ApiResult<byte[]>> GetMyPhotoAsync(CancellationToken ct = default) =>
        GetBytesAsync(ApiRoutes.Path(ApiRoutes.MePhoto), ct);

    public Task<ApiResult> SetMyPhotoAsync(byte[] jpeg, CancellationToken ct = default) =>
        SendBytesAsync(ApiRoutes.Path(ApiRoutes.MePhoto), jpeg, ct);

    // Lookups and demo
    public Task<ApiResult<IReadOnlyList<RecordOption>>> SearchRecordsAsync(RecordKind kind, string? search, CancellationToken ct = default)
    {
        var route = kind switch
        {
            RecordKind.Donor => ApiRoutes.LookupDonors,
            RecordKind.Patient => ApiRoutes.LookupPatients,
            RecordKind.Staff => ApiRoutes.LookupStaff,
            RecordKind.Appointment => ApiRoutes.LookupAppointments,
            RecordKind.Request => ApiRoutes.LookupRequests,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return SendAsync<IReadOnlyList<RecordOption>>(HttpMethod.Get, Url(route, ("search", search)), ct: ct);
    }

    public Task<ApiResult<EnumLookups>> GetEnumLookupsAsync(CancellationToken ct = default) =>
        SendAsync<EnumLookups>(HttpMethod.Get, ApiRoutes.Path(ApiRoutes.LookupEnums), ct: ct);

    public Task<ApiResult> ResetDemoAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.DemoReset), null, ct);

    // Plumbing

    /// <summary>
    /// A request that carries the client's HTTP version and policy explicitly. HttpClient only applies its defaults in the
    /// last handler of the chain, so a request cloned by the retry handler would otherwise fall back to HTTP/1.1.
    /// </summary>
    private HttpRequestMessage NewRequest(HttpMethod method, string url) =>
        new(method, url) { Version = http.DefaultRequestVersion, VersionPolicy = http.DefaultVersionPolicy };

    private static string Url(string template, params (string Name, object? Value)[] query)
    {
        var builder = new StringBuilder(ApiRoutes.Path(template));
        var separator = '?';
        foreach (var (name, value) in query)
        {
            var text = value switch
            {
                null => null,
                string s when s.Length == 0 => null,
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTime dt => dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Enum e => e.ToString(),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (text is null)
            {
                continue;
            }

            builder.Append(separator).Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(text));
            separator = '&';
        }

        return builder.ToString();
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body = null, bool authorize = true, CancellationToken ct = default)
    {
        var outcome = await ExecuteAsync(method, url, body is null ? null : JsonContentFactory(body), authorize, ct).ConfigureAwait(false);
        using var response = outcome.Response;
        if (response is null)
        {
            return ApiResult<T>.Fail(outcome.Problem!);
        }

        if (!response.IsSuccessStatusCode)
        {
            return ApiResult<T>.Fail(await ReadProblemAsync(response, ct).ConfigureAwait(false));
        }

        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
            return value is null ? ApiResult<T>.Fail(BadResponse()) : ApiResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            return ApiResult<T>.Fail(BadResponse());
        }
    }

    private async Task<ApiResult> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var outcome = await ExecuteAsync(method, url, body is null ? null : JsonContentFactory(body), authorize: true, ct: ct).ConfigureAwait(false);
        using var response = outcome.Response;
        if (response is null)
        {
            return ApiResult.Fail(outcome.Problem!);
        }

        return response.IsSuccessStatusCode ? ApiResult.Success : ApiResult.Fail(await ReadProblemAsync(response, ct).ConfigureAwait(false));
    }

    private async Task<ApiResult<byte[]>> GetBytesAsync(string url, CancellationToken ct)
    {
        var outcome = await ExecuteAsync(HttpMethod.Get, url, null, authorize: true, ct: ct).ConfigureAwait(false);
        using var response = outcome.Response;
        if (response is null)
        {
            return ApiResult<byte[]>.Fail(outcome.Problem!);
        }

        return response.IsSuccessStatusCode
            ? ApiResult<byte[]>.Ok(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false))
            : ApiResult<byte[]>.Fail(await ReadProblemAsync(response, ct).ConfigureAwait(false));
    }

    private async Task<ApiResult> SendBytesAsync(string url, byte[] bytes, CancellationToken ct)
    {
        Func<HttpContent> content = () =>
        {
            var c = new ByteArrayContent(bytes);
            c.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return c;
        };
        var outcome = await ExecuteAsync(HttpMethod.Put, url, content, authorize: true, ct: ct).ConfigureAwait(false);
        using var response = outcome.Response;
        if (response is null)
        {
            return ApiResult.Fail(outcome.Problem!);
        }

        return response.IsSuccessStatusCode ? ApiResult.Success : ApiResult.Fail(await ReadProblemAsync(response, ct).ConfigureAwait(false));
    }

    private static Func<HttpContent> JsonContentFactory(object body) =>
        () => JsonContent.Create(body, body.GetType(), options: Json);

    private readonly record struct Outcome(HttpResponseMessage? Response, ApiProblem? Problem);

    /// <summary>Sends once (plus one transparent token refresh on a 401). Writes carry one Idempotency-Key for all retries.</summary>
    private async Task<Outcome> ExecuteAsync(HttpMethod method, string url, Func<HttpContent>? content, bool authorize, CancellationToken ct)
    {
        var idempotencyKey = method == HttpMethod.Post ? Guid.NewGuid().ToString("N") : null;
        var refreshed = false;
        while (true)
        {
            using var request = NewRequest(method, url);
            request.Content = content?.Invoke();
            if (idempotencyKey is not null)
            {
                request.Headers.TryAddWithoutValidation(ApiHeaders.IdempotencyKey, idempotencyKey);
            }

            var token = authorize ? session.AccessToken : null;
            if (authorize && refreshed && token is null)
            {
                // The session was cleared while the token was being refreshed: do not send an anonymous call in its place.
                return new Outcome(null, new ApiProblem(401, ClientProblemCodes.SessionExpired, "Your session has expired. Sign in again."));
            }

            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return new Outcome(null, new ApiProblem(0, ClientProblemCodes.Unreachable, "The server could not be reached."));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new Outcome(null, new ApiProblem(0, ClientProblemCodes.Unreachable, "The server did not respond in time."));
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && token is not null && !refreshed)
            {
                refreshed = true;
                response.Dispose();
                if (await TryRefreshAsync(token, ct).ConfigureAwait(false))
                {
                    continue;
                }

                return new Outcome(null, new ApiProblem(401, ClientProblemCodes.SessionExpired, "Your session has expired. Sign in again."));
            }

            return new Outcome(response, null);
        }
    }

    private async Task<bool> TryRefreshAsync(string failedToken, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = session.Snapshot();
            if (snapshot.AccessToken is null)
            {
                return false; // signed out meanwhile: nothing to refresh and nothing to report
            }

            if (snapshot.AccessToken != failedToken)
            {
                return true; // another call already refreshed
            }

            if (snapshot.RefreshToken is not { } refresh)
            {
                session.Expire(snapshot.Generation);
                return false;
            }

            using var request = NewRequest(HttpMethod.Post, ApiRoutes.Path(ApiRoutes.AuthRefresh));
            request.Content = JsonContent.Create(new RefreshRequest(refresh), options: Json);
            try
            {
                using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct).ConfigureAwait(false) is { } tokens)
                {
                    // Compare and swap: if the session was cleared (or replaced) while this was in flight, the tokens are discarded.
                    return session.TryUpdateTokens(tokens, snapshot.Generation);
                }
            }
            catch (HttpRequestException)
            {
                return false;
            }
            catch (JsonException)
            {
                // An unreadable refresh answer is a failed refresh, not a crash.
            }

            session.Expire(snapshot.Generation);
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static ApiProblem BadResponse() => new(0, ClientProblemCodes.BadResponse, "The server sent a response the app could not read.");

    private static async Task<ApiProblem> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var correlation = response.Headers.TryGetValues(ApiHeaders.CorrelationId, out var values) ? values.FirstOrDefault() : null;
        string? code = null;
        string? title = null;
        string? detail = null;
        string? text = null;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
            title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
            detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
            correlation ??= root.TryGetProperty("correlationId", out var cid) ? cid.GetString() : null;
        }
        catch (JsonException)
        {
            // Not a problem document (a proxy error page, for example): fall back to the status code.
            detail = text is { Length: > 0 } ? text[..Math.Min(text.Length, 300)] : null;
        }

        code ??= status switch
        {
            401 => "unauthorized",
            403 => "forbidden",
            404 => ProblemCodes.NotFound,
            409 => ProblemCodes.Conflict,
            429 => ProblemCodes.RateLimited,
            502 or 503 or 504 => ProblemCodes.DatabaseWaking,
            _ => "error",
        };
        return new ApiProblem(status, code, title ?? response.ReasonPhrase ?? "The request failed.", correlation, detail);
    }
}
