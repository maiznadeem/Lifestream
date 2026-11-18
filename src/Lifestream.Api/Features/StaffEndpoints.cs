using System.Security.Cryptography;
using Lifestream.Api.Auth;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Staff;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Lifestream.Api.Features;

/// <summary>Query for GET staff (the contract has no StaffQuery, so it lives with the API).</summary>
public sealed record StaffListQuery(string? Search = null, int Page = 1, int PageSize = Paging.DefaultPageSize, string? Sort = null);

/// <summary>Rules shared by staff management and the profile: passwords and photo storage.</summary>
public sealed class StaffService(LifestreamDbContext db, TimeProvider time)
{
    public const int MinPasswordLength = 12;
    public const int MaxPasswordLength = 128;

    public static void CheckNewPassword(Check check, string field, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            check.Fail(field, "is required");
        }
        else if (password.Length < MinPasswordLength)
        {
            check.Fail(field, $"must be at least {MinPasswordLength} characters");
        }
        else if (password.Length > MaxPasswordLength)
        {
            check.Fail(field, $"must be at most {MaxPasswordLength} characters");
        }
    }

    /// <summary>A random temporary password (no look-alike characters). Only ever returned once, to the admin who asked for the reset.</summary>
    public static string NewTemporaryPassword() => new(RandomNumberGenerator.GetItems<char>("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789".AsSpan(), 16));

    public async Task SavePhotoAsync(int staffId, ProcessedPhoto photo, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            if (!await db.Staff.AnyAsync(s => s.Id == staffId, token))
            {
                throw ApiException.NotFound("The staff member");
            }

            var now = time.GetUtcNow().UtcDateTime;
            var updated = await db.StaffPhotos.Where(p => p.StaffId == staffId).ExecuteUpdateAsync(
                p => p.SetProperty(x => x.Content, photo.Content).SetProperty(x => x.ContentType, "image/jpeg")
                    .SetProperty(x => x.Width, photo.Width).SetProperty(x => x.Height, photo.Height).SetProperty(x => x.UpdatedAtUtc, now),
                token);
            if (updated == 0)
            {
                db.StaffPhotos.Add(new StaffPhoto { StaffId = staffId, Content = photo.Content, ContentType = "image/jpeg", Width = photo.Width, Height = photo.Height, UpdatedAtUtc = now });
                await db.SaveChangesAsync(token);
            }

            var staff = await db.Staff.FirstAsync(s => s.Id == staffId, token);
            if (!staff.HasPhoto)
            {
                staff.HasPhoto = true;
                await db.SaveChangesAsync(token);
            }

            return true;
        }, ct);
    }

    public async Task<IResult> PhotoAsync(int staffId, CancellationToken ct)
    {
        var photo = await db.StaffPhotos.AsNoTracking().Where(p => p.StaffId == staffId).Select(p => new { p.Content, p.UpdatedAtUtc }).FirstOrDefaultAsync(ct)
            ?? throw ApiException.NotFound("The photo");
        return new PhotoResult(photo.Content, photo.UpdatedAtUtc);
    }

    /// <summary>Always image/jpeg (the processor re-encodes everything), never sniffed, cached privately for a short time.</summary>
    private sealed class PhotoResult(byte[] content, DateTime updated) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var etag = $"\"{updated.Ticks:x}\"";
            httpContext.Response.Headers.ETag = etag;
            httpContext.Response.Headers.CacheControl = "private, max-age=300";
            httpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (httpContext.Request.Headers.IfNoneMatch == etag)
            {
                httpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            httpContext.Response.ContentType = "image/jpeg";
            httpContext.Response.ContentLength = content.Length;
            await httpContext.Response.Body.WriteAsync(content, httpContext.RequestAborted);
        }
    }
}

public static class StaffEndpoints
{
    private static readonly SortMap<Staff> Sorts = new SortMap<Staff>(s => s.Id, "name")
        .Add("name", s => s.DisplayName).Add("username", s => s.Username);

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet(ApiRoutes.Staff, ListAsync).RequireAuthorization(PolicyNames.StaffReader);
        api.MapGet(ApiRoutes.StaffById, GetAsync).RequireAuthorization(PolicyNames.StaffReader);
        api.MapGet(ApiRoutes.StaffPhoto, PhotoAsync);
        api.MapPost(ApiRoutes.Staff, CreateAsync).RequireAuthorization(PolicyNames.Admin);
        api.MapPatch(ApiRoutes.StaffById, UpdateAsync).RequireAuthorization(PolicyNames.Admin);
        api.MapDelete(ApiRoutes.StaffById, DeleteAsync).RequireAuthorization(PolicyNames.Admin);
        api.MapPost(ApiRoutes.StaffResetPassword, ResetPasswordAsync).RequireAuthorization(PolicyNames.Admin);
        api.MapPut(ApiRoutes.StaffPhoto, UploadPhotoAsync).RequireAuthorization(PolicyNames.Admin).WithMetadata(new BodyLimitMetadata(BodyLimitKind.Photo));
    }

    /// <summary>The demo account sees only the demo dataset: the protected owner admin row is never visible to it.</summary>
    internal static IQueryable<Staff> Visible(LifestreamDbContext db, CurrentUser user) =>
        user.IsDemo ? db.Staff.AsNoTracking().Where(s => !s.IsProtected) : db.Staff.AsNoTracking();

    private static async Task<PagedResult<StaffListItem>> ListAsync([AsParameters] StaffListQuery q, LifestreamDbContext db, CurrentUser user, CancellationToken ct)
    {
        var (page, size) = Lists.Normalize(q.Page, q.PageSize);
        var filtered = Visible(db, user);
        if (Lists.CleanSearch(q.Search) is { } term)
        {
            var like = Lists.PrefixPattern(term);
            var id = int.TryParse(term, out var parsed) ? parsed : -1;
            filtered = filtered.Where(s => s.Id == id || EF.Functions.Like(s.DisplayName, like, "\\") || EF.Functions.Like(s.Username, like, "\\"));
        }

        var sorted = Sorts.Apply(filtered, q.Sort).Skip((page - 1) * size).Take(size);

        // Salary and phone are read, and sent, for an Admin only. Never the password hash, never the photo bytes.
        var rows = user.IsAdmin
            ? await sorted.Select(s => new ListRow<StaffListItem>(
                new StaffListItem(s.Id, s.DisplayName, s.Username, s.Designation, s.Shift, s.Role, s.HasPhoto, s.Salary, s.Phone), filtered.Count())).ToListAsync(ct)
            : await sorted.Select(s => new ListRow<StaffListItem>(
                new StaffListItem(s.Id, s.DisplayName, s.Username, s.Designation, s.Shift, s.Role, s.HasPhoto, null, null), filtered.Count())).ToListAsync(ct);
        return await Lists.BuildAsync(rows, page, size, () => filtered.CountAsync(ct));
    }

    private static async Task<StaffDetail> GetAsync(int id, LifestreamDbContext db, CurrentUser user, CancellationToken ct) =>
        ToDetail(
            await Visible(db, user).Where(s => s.Id == id).Select(s => new StaffRow(s.Id, s.DisplayName, s.Username, s.Gender, s.DateOfBirth, s.Designation, s.Shift, s.Role, s.HasPhoto, s.Salary, s.Phone, s.IsProtected, s.RowVersion)).FirstOrDefaultAsync(ct)
                ?? throw ApiException.NotFound("The staff member"),
            user.IsAdmin);

    private sealed record StaffRow(int Id, string DisplayName, string Username, Gender Gender, DateOnly DateOfBirth, Designation Designation, Shift Shift, Role Role, bool HasPhoto, int? Salary, string? Phone, bool IsProtected, byte[] RowVersion);

    private static StaffDetail ToDetail(StaffRow s, bool includePrivate) =>
        new(s.Id, s.DisplayName, s.Username, s.Gender, s.DateOfBirth, s.Designation, s.Shift, s.Role, s.HasPhoto, includePrivate ? s.Salary : null, includePrivate ? s.Phone : null, s.IsProtected, RowVersions.ToText(s.RowVersion));

    private static StaffDetail ToDetail(Staff s) =>
        ToDetail(new StaffRow(s.Id, s.DisplayName, s.Username, s.Gender, s.DateOfBirth, s.Designation, s.Shift, s.Role, s.HasPhoto, s.Salary, s.Phone, s.IsProtected, s.RowVersion), true);

    private static async Task<IResult> PhotoAsync(int id, CurrentUser user, StaffService staff, LifestreamDbContext db, CancellationToken ct)
    {
        // Admin and the demo account (read-only staff screen) may see any photo; everyone else only their own.
        var allowed = user.IsAdmin || user.IsDemo || id == user.TokenStaffId;
        if (!allowed)
        {
            throw ApiException.Forbidden();
        }

        if (user.IsDemo && await db.Staff.AnyAsync(s => s.Id == id && s.IsProtected, ct))
        {
            throw ApiException.NotFound("The photo");
        }

        return await staff.PhotoAsync(id, ct);
    }

    private static async Task<NoContent> UploadPhotoAsync(int id, HttpRequest http, PhotoProcessor photos, StaffService staff, LifestreamDbContext db, CurrentUser user, CancellationToken ct)
    {
        // Same gate as edit and reset: the owner's own record can be changed only by the owner.
        if (id != user.TokenStaffId && await db.Staff.AnyAsync(s => s.Id == id && s.IsProtected, ct))
        {
            throw ApiException.Forbidden("The owner account can only be changed by the owner.");
        }

        await staff.SavePhotoAsync(id, await photos.ReadAsync(http, ct), ct);
        return TypedResults.NoContent();
    }

    private static async Task<Created<StaffDetail>> CreateAsync(HttpRequest http, LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<CreateStaffRequest>(ct);
        var check = new Check().Text("displayName", r.DisplayName, 100).Text("username", r.Username, 50, min: 3).Enum("gender", r.Gender)
            .DateOfBirth("dateOfBirth", r.DateOfBirth, Dates.Today(time)).Enum("designation", r.Designation).Enum("shift", r.Shift).Enum("role", r.Role).Phone("phone", r.Phone);
        if (!string.IsNullOrWhiteSpace(r.Username) && !r.Username.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            check.Fail("username", "may contain only letters, digits, dot, underscore and dash");
        }

        if (r.DateOfBirth != default && Eligibility.AgeOn(r.DateOfBirth, Dates.Today(time)) < 16)
        {
            check.Fail("dateOfBirth", "staff must be at least 16 years old");
        }

        if (r.Salary is < 0 or > 10_000_000)
        {
            check.Fail("salary", "must be between 0 and 10,000,000");
        }

        StaffService.CheckNewPassword(check, "initialPassword", r.InitialPassword);
        check.ThrowIfInvalid();

        var hash = PasswordHashing.Hash(r.InitialPassword);
        var username = r.Username.Trim();
        var staff = await db.RunAsync(async token =>
        {
            if (await db.Staff.AnyAsync(s => s.Username == username, token))
            {
                throw ApiException.Conflict("That username is already taken.");
            }

            var entity = new Staff
            {
                DisplayName = r.DisplayName.Trim(),
                Username = username,
                PasswordHash = hash,
                Gender = r.Gender,
                DateOfBirth = r.DateOfBirth,
                Designation = r.Designation,
                Shift = r.Shift,
                Role = r.Role,
                Salary = r.Salary,
                Phone = DonorEndpoints.Blank(r.Phone),
            };
            db.Staff.Add(entity);
            await db.SaveChangesAsync(token);
            return entity;
        }, ct);
        return TypedResults.Created($"{ApiRoutes.Prefix}/staff/{staff.Id}", ToDetail(staff));
    }

    private static async Task<StaffDetail> UpdateAsync(int id, HttpRequest http, LifestreamDbContext db, CurrentUser user, TimeProvider time, IMemoryCache cache, CancellationToken ct)
    {
        var r = await http.ReadBodyAsync<UpdateStaffRequest>(ct);
        var version = RowVersions.FromText(r.RowVersion);
        var check = new Check();
        if (r.DisplayName.HasValue)
        {
            check.Text("displayName", r.DisplayName.Value, 100);
        }

        if (r.Gender.HasValue)
        {
            check.Enum("gender", r.Gender.Value);
        }

        if (r.DateOfBirth.HasValue)
        {
            check.DateOfBirth("dateOfBirth", r.DateOfBirth.Value, Dates.Today(time));
        }

        if (r.Designation.HasValue)
        {
            check.Enum("designation", r.Designation.Value);
        }

        if (r.Shift.HasValue)
        {
            check.Enum("shift", r.Shift.Value);
        }

        if (r.Role.HasValue)
        {
            check.Enum("role", r.Role.Value);
        }

        if (r.Salary.HasValue && r.Salary.Value is < 0 or > 10_000_000)
        {
            check.Fail("salary", "must be between 0 and 10,000,000");
        }

        if (r.Phone.HasValue)
        {
            check.Phone("phone", r.Phone.Value);
        }

        check.ThrowIfInvalid();

        var staff = await db.RunAsync(async token =>
        {
            var entity = await db.Staff.FirstOrDefaultAsync(s => s.Id == id && !s.IsDemo, token) ?? throw ApiException.NotFound("The staff member");
            if (entity.IsProtected && entity.Id != user.TokenStaffId)
            {
                throw ApiException.Forbidden("The owner account can only be changed by the owner.");
            }

            db.Entry(entity).Property(s => s.RowVersion).OriginalValue = version;
            if (r.Role.HasValue && r.Role.Value != entity.Role && entity.Role == Role.Admin)
            {
                if (entity.IsProtected || !await db.Staff.AnyAsync(s => s.Role == Role.Admin && s.Id != id, token))
                {
                    throw ApiException.Conflict("The last administrator cannot be demoted.");
                }
            }

            if (r.DisplayName.HasValue)
            {
                entity.DisplayName = r.DisplayName.Value!.Trim();
            }

            if (r.Gender.HasValue)
            {
                entity.Gender = r.Gender.Value;
            }

            if (r.DateOfBirth.HasValue)
            {
                entity.DateOfBirth = r.DateOfBirth.Value;
            }

            if (r.Designation.HasValue)
            {
                entity.Designation = r.Designation.Value;
            }

            if (r.Shift.HasValue)
            {
                entity.Shift = r.Shift.Value;
            }

            if (r.Role.HasValue)
            {
                entity.Role = r.Role.Value;
            }

            if (r.Salary.HasValue)
            {
                entity.Salary = r.Salary.Value;
            }

            if (r.Phone.HasValue)
            {
                entity.Phone = DonorEndpoints.Blank(r.Phone.Value);
            }

            await db.SaveChangesAsync(token);
            return entity;
        }, ct);

        // The role in the database is authoritative on every request; forget what was cached so a demotion applies at once.
        RevocationCache.Clear(cache);
        return ToDetail(staff);
    }

    private static async Task<NoContent> DeleteAsync(int id, LifestreamDbContext db, CurrentUser user, IMemoryCache cache, CancellationToken ct)
    {
        await db.RunAsync(async token =>
        {
            var entity = await db.Staff.FirstOrDefaultAsync(s => s.Id == id && !s.IsDemo, token) ?? throw ApiException.NotFound("The staff member");
            if (entity.Id == user.TokenStaffId)
            {
                throw ApiException.Conflict("You cannot delete your own account.");
            }

            if (entity.IsProtected)
            {
                throw ApiException.Conflict("The owner account cannot be deleted.");
            }

            if (entity.Role == Role.Admin && !await db.Staff.AnyAsync(s => s.Role == Role.Admin && s.Id != id, token))
            {
                throw ApiException.Conflict("The last administrator cannot be deleted.");
            }

            db.Staff.Remove(entity);
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        RevocationCache.Clear(cache);
        return TypedResults.NoContent();
    }

    /// <summary>Sets a new random temporary password, ends the person's sessions, clears any lock. The stored value is a hash; the temporary text is returned once.</summary>
    private static async Task<ResetPasswordResponse> ResetPasswordAsync(int id, LifestreamDbContext db, CurrentUser user, AuthService auth, CancellationToken ct)
    {
        var temporary = StaffService.NewTemporaryPassword();
        var hash = PasswordHashing.Hash(temporary);
        await db.RunAsync(async token =>
        {
            var entity = await db.Staff.FirstOrDefaultAsync(s => s.Id == id && !s.IsDemo, token) ?? throw ApiException.NotFound("The staff member");
            if (entity.IsProtected && entity.Id != user.TokenStaffId)
            {
                throw ApiException.Forbidden("The owner account can only be changed by the owner.");
            }

            entity.PasswordHash = hash;
            entity.FailedLoginCount = 0;
            entity.LockoutEndUtc = null;
            await db.SaveChangesAsync(token);
            return true;
        }, ct);
        await auth.RevokeAllForAsync(id, null, ct);
        return new ResetPasswordResponse(temporary);
    }
}
