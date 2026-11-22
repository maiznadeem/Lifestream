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
    UnknownLoginStore unknown,
    ICredentialHasher hasher,
    DummyHash dummyHash,
    DemoSessionDenylist demoSessions,
    DemoDirectory demoDirectory,
    IServiceProvider services)
{
    private const int ConcurrencyRetries = 5;

    private AuthSettings Auth => authSettings.Value;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public static ApiException InvalidCredentials() =>
        new(StatusCodes.Status401Unauthorized, ExtraProblemCodes.InvalidCredentials, "The username or password is not correct.");

    /// <summary>
    /// The order matters: the guess is counted (and the lock checked) in ONE atomic step BEFORE the password is checked, so however
    /// many requests arrive at once, only the first few (the configured maximum) are ever verified. A name with no staff row (and the
    /// demo account, which has no password login) goes through the same counting in the database (same collation), so the two cases are indistinguishable.
    /// </summary>
    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await FindLoginAsync(username, ct);

        // A name with no password account (and the demo account) takes exactly the same steps: the same counting in the database, then a
        // hash check against a dummy hash, then the same answer.
        var staffId = user is null or { IsDemo: true } ? (int?)null : user.Id;
        await unknown.ReserveAsync(staffId, username, Auth.MaxFailedAttempts, Auth.LockoutMinutes, ct);
        if (staffId is null)
        {
            await CredentialWork.RunAsync(() => hasher.Verify(dummyHash.Value, request.Password));
            throw InvalidCredentials();
        }

        var passwordHash = user!.PasswordHash;
        if (!await CredentialWork.RunAsync(() => hasher.Verify(passwordHash, request.Password)))
        {
            throw InvalidCredentials();
        }

        await ClearFailuresAsync(user.Id, ct);
        await DailyResetFirstAsync(ct);
        return await IssueAsync(user.Id, user.DisplayName, user.Role, isDemo: false, user.MustChangePassword, ct);
    }

    /// <summary>What a password sign-in reads about the name it was given.</summary>
    private sealed class LoginRow
    {
        public int Id { get; init; }
        public required string DisplayName { get; init; }
        public Role Role { get; init; }
        public bool IsDemo { get; init; }
        public required string PasswordHash { get; init; }
        public bool MustChangePassword { get; init; }
    }

    /// <summary>The demo account as a demo sign-in and a demo refresh read it.</summary>
    private sealed class DemoRow
    {
        public int Id { get; init; }
        public required string DisplayName { get; init; }
    }

    // One definition of each sign-in read, used by the sign-in and by the warm-up, so the warm-up always compiles the query a sign-in runs.
    private Task<LoginRow?> FindLoginAsync(string username, CancellationToken ct) =>
        db.Staff.AsNoTracking()
            .Where(s => s.Username == username)
            .Select(s => new LoginRow { Id = s.Id, DisplayName = s.DisplayName, Role = s.Role, IsDemo = s.IsDemo, PasswordHash = s.Credential!.PasswordHash, MustChangePassword = s.Credential.MustChangePassword })
            .FirstOrDefaultAsync(ct);

    private Task<DemoRow?> FindDemoAsync(CancellationToken ct) =>
        db.Staff.Where(s => s.IsDemo).Select(s => new DemoRow { Id = s.Id, DisplayName = s.DisplayName }).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Runs the reads of a sign-in once, for <see cref="SignInWarmUp"/>: the look-up of a name (the empty name, which no account has and
    /// the sign-in endpoint refuses) and of the demo account. It reads only: no guess is counted and nobody is signed in.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        await FindLoginAsync(string.Empty, ct);
        if (demoSettings.Value.Enabled)
        {
            await FindDemoAsync(ct);
        }
    }

    /// <summary>
    /// Runs the token code once (make, sign, check), which needs no database, so the first sign-in does not pay for loading it. The
    /// tokens are for no staff row (id 0), never leave this method and are discarded.
    /// </summary>
    public void WarmUpTokens()
    {
        var response = DemoTokens(0, "warm-up", Guid.NewGuid());
        _ = tokens.ReadDemoRefreshAsync(response.RefreshToken).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Counts one password guess for a real account, atomically (an application lock per account serialises the check of the lock
    /// and the increment; the same steps and statements as for an unknown name, see <see cref="UnknownLoginStore.ReserveAsync"/>). Throws 429 when the account is locked. The lock starts
    /// with the guess that reaches the limit, and counting restarts. A write that cannot be made is an error, never a dropped count.
    /// </summary>
    public Task<AttemptReservation> ReserveAttemptAsync(int staffId, CancellationToken ct) =>
        unknown.ReserveAsync(staffId, "", Auth.MaxFailedAttempts, Auth.LockoutMinutes, ct);

    /// <summary>
    /// Loads only the counters and the row version of the credential (never the password hash) into a stub that EF tracks, so the save
    /// writes just the changed counter columns and checks the credential's row version. The person's own record (the Staff row) is never touched.
    /// </summary>
    private async Task<StaffCredential?> AttachCountersAsync(int staffId, CancellationToken token)
    {
        var row = await db.StaffCredentials.AsNoTracking().Where(c => c.StaffId == staffId).Select(c => new { c.FailedCount, c.LockoutEndUtc, c.LastFailureUtc, c.RowVersion }).FirstOrDefaultAsync(token);
        if (row is null)
        {
            return null;
        }

        var credential = new StaffCredential { StaffId = staffId, FailedCount = row.FailedCount, LockoutEndUtc = row.LockoutEndUtc, LastFailureUtc = row.LastFailureUtc, RowVersion = row.RowVersion };
        db.StaffCredentials.Attach(credential);
        return credential;
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
                    await db.LockAsync($"login:{staffId}", token, LoginLocks.WaitMs);
                    var staff = await AttachCountersAsync(staffId, token);
                    if (staff is not null && (staff.FailedCount != 0 || staff.LockoutEndUtc is not null))
                    {
                        staff.FailedCount = 0;
                        staff.LastFailureUtc = null;
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

        await DailyResetFirstAsync(ct);
        var demo = await FindDemoAsync(ct) ?? throw ApiException.Conflict("The demo account is not available yet.");
        return DemoTokens(demo.Id, demo.DisplayName, Guid.NewGuid());
    }

    /// <summary>
    /// The first sign-in of a UTC day (after a cold start, the usual case) runs that day's demo reset before the session is handed out, so
    /// the session begins on the new data and is never told "the demo data was reset" about a reset it caused itself. Before 2.1.9 the reset
    /// ran on the session's first request instead, and every answer of that session carried X-Demo-Reset. It runs only once the sign-in is
    /// known to succeed: a failed attempt does the same work for every name, and a stray request never opens the database.
    /// </summary>
    private Task DailyResetFirstAsync(CancellationToken ct)
    {
        if (!demoSettings.Value.Enabled)
        {
            return Task.CompletedTask;
        }

        var now = Now;
        return demoDirectory.EnsureDailyResetAsync(services, DateOnly.FromDateTime(now), now, ct);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken ct)
    {
        var secret = request.RefreshToken;
        if (secret.Contains('.', StringComparison.Ordinal))
        {
            // A demo refresh token (a signed token, no database row). It is single use: a token that was already rotated, or whose session
            // signed out, is refused, and a replayed one ends the whole session (as for staff).
            if (!demoSettings.Value.Enabled || await tokens.ReadDemoRefreshAsync(secret) is not { } demoRefresh || await demoSessions.IsRevokedAsync(demoRefresh.Family, ct))
            {
                throw Unauthorized();
            }

            if (!await demoSessions.TryUseAsync(demoRefresh.TokenId, demoRefresh.ExpiresUtc, ct))
            {
                await demoSessions.RevokeFamilyAsync(demoRefresh.Family, DemoSessionEnd(), ct);
                throw Unauthorized();
            }

            var demo = await FindDemoAsync(ct) ?? throw Unauthorized();
            return DemoTokens(demo.Id, demo.DisplayName, demoRefresh.Family);
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

        var user = await db.Staff.Where(s => s.Id == stored.StaffId).Select(s => new { s.DisplayName, s.Role, s.IsDemo, s.Credential!.MustChangePassword }).FirstOrDefaultAsync(ct) ?? throw Unauthorized();
        return await IssueAsync(stored.StaffId, user.DisplayName, user.Role, user.IsDemo, user.MustChangePassword, ct, stored.FamilyId);
    }

    /// <summary>Ends the whole session (every refresh token of the family, and for a demo session its access tokens too) from its refresh token. Unknown tokens are ignored.</summary>
    public async Task LogoutAsync(string refreshToken, CancellationToken ct)
    {
        if (refreshToken.Contains('.', StringComparison.Ordinal))
        {
            if (demoSettings.Value.Enabled && await tokens.ReadDemoRefreshAsync(refreshToken) is { } demoRefresh)
            {
                await demoSessions.RevokeFamilyAsync(demoRefresh.Family, DemoSessionEnd(), ct);
            }

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

    /// <summary>When every token of a demo session that exists now will have expired: the denylist entry can be forgotten then.</summary>
    private DateTime DemoSessionEnd() => Now.AddHours(jwtSettings.Value.DemoRefreshHours).AddMinutes(1);

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

    private async Task<TokenResponse> IssueAsync(int staffId, string displayName, Role role, bool isDemo, bool mustChangePassword, CancellationToken ct, Guid? family = null)
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
        return new TokenResponse(access, secret, expires, TokenService.Session(staffId, displayName, role, isDemo, mustChangePassword));
    }

    private static ApiException Unauthorized() => new(StatusCodes.Status401Unauthorized, ExtraProblemCodes.Unauthorized, "The session has ended. Sign in again.");
}

/// <summary>What the database says about a session right now.</summary>
public sealed record SessionState(bool Active, Role Role, bool MustChangePassword = false);

/// <summary>
/// Short-lived memory of "this session is still active and this is the person's role", so an access token check does not hit the
/// database on every request. The role in the database wins over the role written in the token, so a demotion applies at once.
/// </summary>
public static class RevocationCache
{
    internal sealed class Version
    {
        public int Value;
    }

    // The version lives inside the cache it guards, so two hosts in one process (tests) never invalidate each other.
    internal static Version VersionOf(IMemoryCache cache) => cache.GetOrCreate("lifestream.session-version", _ => new Version())!;

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
        var state = await ReadAsync(db, family, now, ct);
        cache.Set(key, state, TimeSpan.FromSeconds(20));
        return state;
    }

    private sealed class SessionRow
    {
        public Role Role { get; init; }
        public bool MustChangePassword { get; init; }
    }

    private static async Task<SessionState> ReadAsync(LifestreamDbContext db, Guid family, DateTime now, CancellationToken ct)
    {
        var session = await db.RefreshTokens
            .Where(t => t.FamilyId == family && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .Select(t => new SessionRow { Role = t.Staff!.Role, MustChangePassword = t.Staff.Credential!.MustChangePassword })
            .FirstOrDefaultAsync(ct);
        return new SessionState(session is not null, session?.Role ?? Role.Staff, session?.MustChangePassword ?? false);
    }

    /// <summary>Runs the session read once, for a session that does not exist (for <see cref="SignInWarmUp"/>). Nothing is remembered.</summary>
    public static Task WarmUpAsync(LifestreamDbContext db, TimeProvider time, CancellationToken ct) =>
        ReadAsync(db, Guid.Empty, time.GetUtcNow().UtcDateTime, ct);
}

/// <summary>The password hashing the login runs (PBKDF2), behind an interface so a test can count the work.</summary>
public interface ICredentialHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public sealed class Pbkdf2CredentialHasher : ICredentialHasher
{
    public string Hash(string password) => PasswordHashing.Hash(password);

    public bool Verify(string hash, string password) => PasswordHashing.Verify(hash, password);
}

/// <summary>
/// The hash a login for a name with no password account is checked against, so an unknown name costs the same work as a wrong password.
/// It is computed when the object is created, which <see cref="LoginTimingSetup.WarmUp"/> does while the application is built, before the host accepts a request: the first unknown name
/// after a start must not pay for one more hash than a real account does (the verifier measured 840 ms against 390 ms).
/// </summary>
public sealed class DummyHash(ICredentialHasher hasher)
{
    public string Value { get; } = hasher.Hash("not-a-real-account");
}

public static class LoginTimingSetup
{
    public static IServiceCollection AddLoginTimingSafeguards(this IServiceCollection services)
    {
        services.AddSingleton<ICredentialHasher, Pbkdf2CredentialHasher>();
        services.AddSingleton<DummyHash>();
        return services;
    }

    /// <summary>Creates the dummy hash now (one PBKDF2 hash), so no request ever pays for it. Called before the host starts listening.</summary>
    public static void WarmUp(IServiceProvider services) => _ = services.GetRequiredService<DummyHash>().Value;
}
