using Lifestream.Api.Infrastructure;
using Lifestream.Contracts;
using Lifestream.Contracts.Auth;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Auth;

/// <summary>Login, demo login, refresh rotation and logout. Failure state (lockout, revoked families) is stored in the database so it survives scale to zero.</summary>
public sealed class AuthService(
    LifestreamDbContext db,
    TokenService tokens,
    TimeProvider time,
    IOptions<AuthSettings> authSettings,
    IOptions<JwtSettings> jwtSettings,
    IOptions<DemoSettings> demoSettings,
    IMemoryCache cache)
{
    // Verified against when the user does not exist, so an unknown name costs the same time as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHashing.Hash("not-a-real-account"));

    private AuthSettings Auth => authSettings.Value;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ExtraProblemCodes.InvalidCredentials, "The username or password is not correct.");

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await db.Staff
            .Where(s => s.Username == username)
            .Select(s => new { s.Id, s.DisplayName, s.PasswordHash, s.Role, s.IsDemo, s.LockoutEndUtc, s.FailedLoginCount })
            .FirstOrDefaultAsync(ct);

        if (user is { LockoutEndUtc: { } until } && until > Now)
        {
            throw new ApiException(StatusCodes.Status429TooManyRequests, ProblemCodes.LockedOut, "Too many failed attempts. Try again later.", retryAfter: until - Now);
        }

        var ok = PasswordHashing.Verify(user?.PasswordHash ?? DummyHash.Value, request.Password) && user is { IsDemo: false };
        if (!ok || user is null)
        {
            if (user is { IsDemo: false })
            {
                await RegisterFailureAsync(user.Id, ct);
            }

            throw InvalidCredentials();
        }

        if (user.FailedLoginCount != 0 || user.LockoutEndUtc is not null)
        {
            await UpdateStaffAsync(user.Id, s =>
            {
                s.FailedLoginCount = 0;
                s.LockoutEndUtc = null;
            }, ct);
        }

        return await IssueAsync(user.Id, user.DisplayName, user.Role, isDemo: false, ct);
    }

    /// <summary>Counts a wrong password (login or change-password). The lock starts after the configured number and counting restarts.</summary>
    /// <summary>Returns true when this failure started the lockout.</summary>
    public async Task<bool> RegisterFailureAsync(int staffId, CancellationToken ct)
    {
        var lockedNow = false;
        await UpdateStaffAsync(staffId, s =>
        {
            lockedNow = false;
            s.FailedLoginCount++;
            if (s.FailedLoginCount >= Auth.MaxFailedAttempts)
            {
                s.LockoutEndUtc = Now.AddMinutes(Auth.LockoutMinutes);
                s.FailedLoginCount = 0;
                lockedNow = true;
            }
        }, ct);
        return lockedNow;
    }

    /// <summary>Changes a staff row through the tracked entity (so it is audited like every other staff write); a clash with a concurrent change reloads and tries again.</summary>
    private async Task UpdateStaffAsync(int staffId, Action<Staff> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            db.ChangeTracker.Clear();
            var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == staffId, ct);
            if (staff is null)
            {
                return;
            }

            change(staff);
            try
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another request changed the row first: read it again and apply the change to the fresh values.
            }
        }
    }

    public async Task<TokenResponse> DemoLoginAsync(CancellationToken ct)
    {
        if (!demoSettings.Value.Enabled)
        {
            throw ApiException.NotFound("The demo");
        }

        var demo = await db.Staff.Where(s => s.IsDemo).Select(s => new { s.Id, s.DisplayName }).FirstOrDefaultAsync(ct)
            ?? throw ApiException.Conflict("The demo account is not available yet.");
        return DemoTokens(demo.Id, demo.DisplayName, Guid.NewGuid());
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var secret = request.RefreshToken;
        if (secret.Contains('.', StringComparison.Ordinal))
        {
            // A demo refresh token (a signed token, no database row).
            if (!demoSettings.Value.Enabled || await tokens.ReadDemoRefreshAsync(secret) is not { } demoFamily)
            {
                throw Unauthorized();
            }

            var demo = await db.Staff.Where(s => s.IsDemo).Select(s => new { s.Id, s.DisplayName }).FirstOrDefaultAsync(ct) ?? throw Unauthorized();
            return DemoTokens(demo.Id, demo.DisplayName, demoFamily);
        }

        var hash = TokenService.HashRefresh(secret);
        var now = Now;
        var stored = await db.RefreshTokens.Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.StaffId, t.FamilyId, t.ExpiresAtUtc, t.RevokedAtUtc })
            .FirstOrDefaultAsync(ct) ?? throw Unauthorized();

        if (stored.RevokedAtUtc is not null)
        {
            // A used token came back: someone holds a copy. End the whole session.
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw Unauthorized();
        }

        if (stored.ExpiresAtUtc <= now)
        {
            throw Unauthorized();
        }

        var rotated = await db.RefreshTokens.Where(t => t.Id == stored.Id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAtUtc, now), ct);
        if (rotated != 1)
        {
            await RevokeFamilyAsync(stored.FamilyId, ct);
            throw Unauthorized();
        }

        var user = await db.Staff.Where(s => s.Id == stored.StaffId).Select(s => new { s.DisplayName, s.Role, s.IsDemo }).FirstOrDefaultAsync(ct) ?? throw Unauthorized();
        return await IssueAsync(stored.StaffId, user.DisplayName, user.Role, user.IsDemo, ct, stored.FamilyId);
    }

    /// <summary>Ends the whole session (every refresh token of the family) from its refresh token. Unknown tokens are ignored; demo tokens are stateless.</summary>
    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (refreshToken.Contains('.', StringComparison.Ordinal))
        {
            return;
        }

        var hash = TokenService.HashRefresh(refreshToken);
        var family = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (family is { } f)
        {
            await RevokeFamilyAsync(f, ct);
        }
    }

    public async Task RevokeAllForAsync(int staffId, Guid? except, CancellationToken ct)
    {
        var now = Now;
        await db.RefreshTokens.Where(t => t.StaffId == staffId && t.RevokedAtUtc == null && t.FamilyId != except)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAtUtc, now), ct);
        RevocationCache.Clear(cache);
    }

    private TokenResponse DemoTokens(int staffId, string displayName, Guid family)
    {
        var (access, expires) = tokens.CreateAccess(staffId, displayName, Role.Staff, true, family);
        return new TokenResponse(access, tokens.CreateDemoRefresh(family), expires, TokenService.Session(staffId, displayName, Role.Staff, true));
    }

    private async Task RevokeFamilyAsync(Guid family, CancellationToken ct)
    {
        var now = Now;
        await db.RefreshTokens.Where(t => t.FamilyId == family && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAtUtc, now), ct);
        RevocationCache.Clear(cache);
    }

    private async Task<TokenResponse> IssueAsync(int staffId, string displayName, Role role, bool isDemo, CancellationToken ct, Guid? family = null)
    {
        var fam = family ?? Guid.NewGuid();
        var now = Now;
        var secret = TokenService.NewRefreshSecret();
        db.RefreshTokens.Add(new RefreshToken
        {
            StaffId = staffId,
            FamilyId = fam,
            TokenHash = TokenService.HashRefresh(secret),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(jwtSettings.Value.RefreshTokenDays),
        });
        await db.SaveChangesAsync(ct);
        var (access, expires) = tokens.CreateAccess(staffId, displayName, role, isDemo, fam);
        return new TokenResponse(access, secret, expires, TokenService.Session(staffId, displayName, role, isDemo));
    }

    private static ApiException Unauthorized() => new(StatusCodes.Status401Unauthorized, ExtraProblemCodes.Unauthorized, "The session has ended. Sign in again.");
}

/// <summary>What the database says about a session right now.</summary>
public sealed record SessionState(bool Active, Role Role);

/// <summary>
/// Short-lived memory of "this session is still active and this is the person's role", so an access token check does not hit the
/// database on every request. The role in the database wins over the role written in the token, so a demotion applies at once.
/// </summary>
public static class RevocationCache
{
    private sealed class Version
    {
        public int Value;
    }

    // The version lives inside the cache it guards, so two hosts in one process (tests) never invalidate each other.
    private static Version VersionOf(IMemoryCache cache) => cache.GetOrCreate("lifestream.session-version", _ => new Version())!;

    /// <summary>Forgets every remembered session state (after a logout, role change, password reset or delete).</summary>
    public static void Clear(IMemoryCache cache) => Interlocked.Increment(ref VersionOf(cache).Value);

    public static async Task<SessionState> StateAsync(IMemoryCache cache, LifestreamDbContext db, TimeProvider time, Guid family, CancellationToken ct)
    {
        var key = (Volatile.Read(ref VersionOf(cache).Value), family);
        if (cache.TryGetValue(key, out SessionState? cached) && cached is not null)
        {
            return cached;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var role = await db.RefreshTokens
            .Where(t => t.FamilyId == family && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .Select(t => (Role?)t.Staff!.Role)
            .FirstOrDefaultAsync(ct);
        var state = new SessionState(role is not null, role ?? Role.Staff);
        cache.Set(key, state, TimeSpan.FromSeconds(20));
        return state;
    }
}
