using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
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
    IMemoryCache cache,
    UnknownLoginTracker unknown)
{
    private const int ConcurrencyRetries = 5;

    // Verified against when the user does not exist, so an unknown name costs the same time as a wrong password.
    private static readonly Lazy<string> DummyHash = new(() => PasswordHashing.Hash("not-a-real-account"));

    private AuthSettings Auth => authSettings.Value;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ExtraProblemCodes.InvalidCredentials, "The username or password is not correct.");

    /// <summary>
    /// The order matters: the guess is counted (and the lock checked) in ONE atomic step BEFORE the password is checked, so however
    /// many requests arrive at once, only the first few (the configured maximum) are ever verified. A name with no staff row (and the
    /// demo account, which has no password login) goes through the same counting in memory, so the two cases are indistinguishable.
    /// </summary>
    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await db.Staff.AsNoTracking()
            .Where(s => s.Username == username)
            .Select(s => new { s.Id, s.DisplayName, s.PasswordHash, s.Role, s.IsDemo, s.LockoutEndUtc })
            .FirstOrDefaultAsync(ct);

        if (user is null or { IsDemo: true })
        {
            if (unknown.Reserve(username, Auth.MaxFailedAttempts, Auth.LockoutMinutes) is { } wait)
            {
                throw LoginProblems.Locked(wait);
            }

            PasswordHashing.Verify(DummyHash.Value, request.Password);
            throw InvalidCredentials();
        }

        // Fast path for an account that is already locked: no hash is checked and nothing is written.
        if (user.LockoutEndUtc is { } until && until > Now)
        {
            throw LoginProblems.Locked(until - Now);
        }

        await ReserveAttemptAsync(user.Id, ct);
        if (!PasswordHashing.Verify(user.PasswordHash, request.Password))
        {
            throw InvalidCredentials();
        }

        await ClearFailuresAsync(user.Id, ct);
        return await IssueAsync(user.Id, user.DisplayName, user.Role, isDemo: false, ct);
    }

    /// <summary>
    /// Counts one password guess for a real account, atomically (an application lock per account serialises the check of the lock
    /// and the increment; the write is audited like every other staff write). Throws 429 when the account is locked. The lock starts
    /// with the guess that reaches the limit, and counting restarts. A write that cannot be made is an error, never a dropped count.
    /// </summary>
    public async Task<AttemptReservation> ReserveAttemptAsync(int staffId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await db.RunAsync(async token =>
                {
                    await db.LockAsync($"login:{staffId}", token);
                    var staff = await AttachCountersAsync(staffId, token) ?? throw InvalidCredentials();
                    var now = Now;
                    if (staff.LockoutEndUtc is { } until && until > now)
                    {
                        throw LoginProblems.Locked(until - now);
                    }

                    staff.FailedLoginCount++;
                    var lockedNow = staff.FailedLoginCount >= Auth.MaxFailedAttempts;
                    if (lockedNow)
                    {
                        staff.LockoutEndUtc = now.AddMinutes(Auth.LockoutMinutes);
                        staff.FailedLoginCount = 0;
                    }

                    await db.SaveChangesAsync(token);
                    return new AttemptReservation(lockedNow);
                }, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConcurrencyRetries)
            {
                // Another writer changed the row between the read and the write: count again from the fresh row.
            }
        }
    }

    /// <summary>
    /// Loads only the counters and the row version (never the password hash) into a stub that EF tracks, so the save writes just the
    /// changed counter columns, checks the row version and is audited like any other staff update.
    /// </summary>
    private async Task<Staff?> AttachCountersAsync(int staffId, CancellationToken token)
    {
        var row = await db.Staff.AsNoTracking().Where(s => s.Id == staffId).Select(s => new { s.FailedLoginCount, s.LockoutEndUtc, s.RowVersion }).FirstOrDefaultAsync(token);
        if (row is null)
        {
            return null;
        }

        var staff = new Staff { Id = staffId, FailedLoginCount = row.FailedLoginCount, LockoutEndUtc = row.LockoutEndUtc, RowVersion = row.RowVersion };
        db.Staff.Attach(staff);
        return staff;
    }

    /// <summary>A right password ends the run of failures (and any lock the guess itself started).</summary>
    public async Task ClearFailuresAsync(int staffId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.RunAsync(async token =>
                {
                    await db.LockAsync($"login:{staffId}", token);
                    var staff = await AttachCountersAsync(staffId, token);
                    if (staff is not null && (staff.FailedLoginCount != 0 || staff.LockoutEndUtc is not null))
                    {
                        staff.FailedLoginCount = 0;
                        staff.LockoutEndUtc = null;
                        await db.SaveChangesAsync(token);
                    }

                    return true;
                }, ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConcurrencyRetries)
            {
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
