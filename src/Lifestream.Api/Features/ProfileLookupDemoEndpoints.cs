using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Lookups;
using Lifestream.Contracts.Profile;
using Lifestream.Data;
using Lifestream.Data.Queries;
using Lifestream.Data.Security;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>"Me": every staff member can read and edit only their own profile. The demo account can read it but not change it.</summary>
public static class ProfileEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Me, GetAsync);
        api.MapPatch(ApiRoutes.Me, UpdateAsync).RequireAuthorization(PolicyNames.NotDemo);
        api.MapPut(ApiRoutes.MePassword, ChangePasswordAsync).RequireAuthorization(PolicyNames.NotDemo).RequireRateLimiting(RateLimitPolicies.Login);
        api.MapPut(ApiRoutes.MePhoto, UploadPhotoAsync).RequireAuthorization(PolicyNames.NotDemo).WithMetadata(new BodyLimitMetadata(BodyLimitKind.Photo));
    }

    private static async Task<ProfileResponse> LoadAsync(LifestreamDbContext db, int id, CancellationToken ct)
    {
        var s = await db.Staff.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.DisplayName, x.Username, x.Designation, x.Shift, x.Phone, HasPhoto = db.StaffPhotos.Any(p => p.StaffId == x.Id), x.RowVersion, x.DateOfBirth, x.CreatedAtUtc })
            .FirstOrDefaultAsync(ct) ?? throw ApiException.NotFound("The profile");
        return new ProfileResponse(s.Id, s.DisplayName, s.Username, s.Designation, s.Shift, s.Phone, s.HasPhoto, RowVersions.ToText(s.RowVersion)) { DateOfBirth = s.DateOfBirth, MemberSinceUtc = s.CreatedAtUtc };
    }

    private static async Task<ProfileResponse> GetAsync(LifestreamDbContext db, CurrentUser user, CancellationToken ct) =>
        await LoadAsync(db, await user.StaffIdAsync(ct), ct);

    private static async Task<ProfileResponse> UpdateAsync(HttpRequest http, LifestreamDbContext db, CurrentUser user, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<UpdateProfileRequest>(ct);
        var version = RowVersions.FromText(r.RowVersion);
        var check = new Check();
        if (r.DisplayName.HasValue)
        {
            check.Text("displayName", r.DisplayName.Value, 100);
        }

        if (r.Phone.HasValue)
        {
            check.Phone("phone", r.Phone.Value);
        }

        check.ThrowIfInvalid();

        var id = user.TokenStaffId;
        await db.RunAsync(async token =>
        {
            var entity = await db.Staff.FirstOrDefaultAsync(s => s.Id == id, token) ?? throw ApiException.NotFound("The profile");
            db.Entry(entity).Property(s => s.RowVersion).OriginalValue = version;
            if (r.DisplayName.HasValue)
            {
                entity.DisplayName = r.DisplayName.Value!.Trim();
            }

            if (r.Phone.HasValue)
            {
                entity.Phone = DonorEndpoints.Blank(r.Phone.Value);
            }

            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        return await LoadAsync(db, id, ct);
    }

    /// <summary>Needs the current password (a wrong one counts toward the lockout), a new one of at least 12 characters; ends every other session.</summary>
    private static async Task<NoContent> ChangePasswordAsync(HttpRequest http, LifestreamDbContext db, CurrentUser user, AuthService auth, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<ChangePasswordRequest>(ct);
        var check = new Check();
        if (string.IsNullOrEmpty(r.CurrentPassword))
        {
            check.Fail("currentPassword", "is required");
        }

        StaffService.CheckNewPassword(check, "newPassword", r.NewPassword);
        if (!string.IsNullOrEmpty(r.NewPassword) && r.NewPassword == r.CurrentPassword)
        {
            check.Fail("newPassword", "must be different from the current password");
        }

        check.ThrowIfInvalid();

        var id = user.TokenStaffId;
        var current = await db.StaffCredentials.AsNoTracking().Where(c => c.StaffId == id).Select(c => new { c.PasswordHash }).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("The profile");

        // The guess is counted, and the lock checked, before the password is: a locked account is refused (429) without any check,
        // so a stolen access token cannot be used to guess the password, however many requests arrive at once.
        var attempt = await auth.ReserveAttemptAsync(id, ct);
        if (!PasswordHashing.Verify(current.PasswordHash, r.CurrentPassword))
        {
            if (attempt.LockedNow)
            {
                // The lock started: sign the caller out everywhere.
                await auth.RevokeAllForAsync(id, null, ct);
            }

            throw ApiException.Validation("currentPassword", "is not correct");
        }

        var hash = PasswordHashing.Hash(r.NewPassword);
        await db.RunAsync(async token =>
        {
            // Only the credential row changes: the profile record the person has open keeps its row version.
            var entity = await db.StaffCredentials.FirstOrDefaultAsync(c => c.StaffId == id, token) ?? throw ApiException.NotFound("The profile");
            entity.PasswordHash = hash;
            entity.SecurityStamp = Guid.NewGuid();
            entity.MustChangePassword = false;
            entity.FailedCount = 0;
            entity.LockoutEndUtc = null;
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        await auth.RevokeAllForAsync(id, user.Family, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Returns the profile (with its row version, which the upload did not change), so the caller needs no reload.</summary>
    private static async Task<ProfileResponse> UploadPhotoAsync(HttpRequest http, CurrentUser user, PhotoProcessor photos, StaffService staff, LifestreamDbContext db, CancellationToken ct)
    {
        await staff.SavePhotoAsync(user.TokenStaffId, await photos.ReadAsync(http, ct), ct);
        return await LoadAsync(db, user.TokenStaffId, ct);
    }
}

/// <summary>Pickers: a short list of id + title + subtitle matched by a prefix of the name, the id or the contact. Never more than 20 rows.</summary>
public static class LookupEndpoints
{
    private const int Take = 20;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.LookupEnums, () => EnumLookups.Create());
        api.MapGet(ApiRoutes.LookupDonors, DonorsAsync);
        api.MapGet(ApiRoutes.LookupPatients, PatientsAsync);
        api.MapGet(ApiRoutes.LookupStaff, StaffAsync).RequireAuthorization(PolicyNames.StaffReader);
        api.MapGet(ApiRoutes.LookupAppointments, AppointmentsAsync);
        api.MapGet(ApiRoutes.LookupRequests, RequestsAsync);
    }

    private static async Task<List<RecordOption>> DonorsAsync([FromQuery] string? search, LifestreamDbContext db, CancellationToken ct)
    {
        var rows = await DonorEndpoints.Filter(db.Donors.AsNoTracking(), search, null).OrderBy(d => d.Name).ThenBy(d => d.Id).Take(Take)
            .Select(d => new { d.Id, d.Name, d.BloodType, d.Phone }).ToListAsync(ct);
        return [.. rows.Select(d => new RecordOption(d.Id, d.Name, $"{d.BloodType.ToDisplay()}{(d.Phone is null ? "" : " · " + d.Phone)}"))];
    }

    private static async Task<List<RecordOption>> PatientsAsync([FromQuery] string? search, LifestreamDbContext db, CancellationToken ct)
    {
        var rows = await PatientEndpoints.Filter(db.Patients.AsNoTracking(), search, null).OrderBy(p => p.Name).ThenBy(p => p.Id).Take(Take)
            .Select(p => new { p.Id, p.Name, p.BloodType, p.Phone }).ToListAsync(ct);
        return [.. rows.Select(p => new RecordOption(p.Id, p.Name, $"{p.BloodType.ToDisplay()}{(p.Phone is null ? "" : " · " + p.Phone)}"))];
    }

    private static async Task<List<RecordOption>> StaffAsync([FromQuery] string? search, LifestreamDbContext db, CurrentUser user, CancellationToken ct)
    {
        var query = StaffEndpoints.Visible(db, user);
        if (Lists.CleanSearch(search) is { } term)
        {
            var like = Lists.PrefixPattern(term);
            var id = SearchQueries.ParseTerm(term).Id ?? -1;
            query = query.Where(s => s.Id == id || EF.Functions.Like(s.DisplayName, like, "\\") || EF.Functions.Like(s.Username, like, "\\"));
        }

        var rows = await query.OrderBy(s => s.DisplayName).ThenBy(s => s.Id).Take(Take).Select(s => new { s.Id, s.DisplayName, s.Designation }).ToListAsync(ct);
        return [.. rows.Select(s => new RecordOption(s.Id, s.DisplayName, s.Designation.ToString()))];
    }

    private static async Task<List<RecordOption>> AppointmentsAsync([FromQuery] string? search, LifestreamDbContext db, CancellationToken ct)
    {
        var query = db.Appointments.AsNoTracking().Where(a => a.Status == AppointmentStatus.Pending);
        if (Lists.CleanSearch(search) is { } term)
        {
            var parsed = SearchQueries.ParseTerm(term);
            var donorIds = db.Donors.NameOrPhoneMatching(parsed).Select(d => d.Id);
            query = parsed.Id is { } id ? query.Where(a => a.Id == id || donorIds.Contains(a.DonorId)) : query.Where(a => donorIds.Contains(a.DonorId));
        }

        var rows = await query.OrderBy(a => a.SlotStartUtc).ThenBy(a => a.Id).Take(Take).Select(a => new { a.Id, Donor = a.Donor!.Name, a.SlotStartUtc }).ToListAsync(ct);
        return [.. rows.Select(a => new RecordOption(a.Id, a.Donor, a.SlotStartUtc.ToString("dd MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture)))];
    }

    private static async Task<List<RecordOption>> RequestsAsync([FromQuery] string? search, LifestreamDbContext db, CancellationToken ct)
    {
        var query = db.Requests.AsNoTracking().Where(r => r.Status == RequestStatus.Pending);
        if (Lists.CleanSearch(search) is { } term)
        {
            var parsed = SearchQueries.ParseTerm(term);
            var patientIds = db.Patients.NameOrPhoneMatching(parsed).Select(p => p.Id);
            query = parsed.Id is { } id ? query.Where(r => r.Id == id || patientIds.Contains(r.PatientId)) : query.Where(r => patientIds.Contains(r.PatientId));
        }

        var rows = await query.OrderBy(r => r.RequiredBy).ThenBy(r => r.Id).Take(Take).Select(r => new { r.Id, Patient = r.Patient!.Name, r.BloodType, r.Units }).ToListAsync(ct);
        return [.. rows.Select(r => new RecordOption(r.Id, r.Patient, $"{r.Units} x {r.BloodType.ToDisplay()}"))];
    }
}

/// <summary>"Reset demo data": the demo account only, at most once per 10 minutes for everyone together.</summary>
public static class DemoEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapPost(ApiRoutes.DemoReset, ResetAsync).RequireAuthorization(PolicyNames.DemoOnly);

    private static async Task<NoContent> ResetAsync(LifestreamDbContext db, DemoDirectory demo, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (!await DemoReset.ForceAsync(db, DateOnly.FromDateTime(now), ct))
        {
            // The reset time was stamped by the database's clock, so "how long is left" is measured with the same clock (DateTime.UtcNow is
            // SYSUTCDATETIME() inside the query); mixing it with this host's clock made the answer depend on the difference between them.
            var state = await db.DemoState.AsNoTracking().Select(s => new { s.LastForcedResetAtUtc, DatabaseNow = DateTime.UtcNow }).FirstOrDefaultAsync(ct);
            var wait = state is { LastForcedResetAtUtc: { } at } ? at + DemoReset.ForceCooldown - state.DatabaseNow : DemoReset.ForceCooldown;
            throw new ApiException(StatusCodes.Status429TooManyRequests, ProblemCodes.DemoResetCooldown, "The demo data was reset a moment ago. Try again later.", retryAfter: wait > TimeSpan.Zero ? wait : TimeSpan.FromMinutes(1));
        }

        demo.AfterReset(now);
        return TypedResults.NoContent();
    }
}
