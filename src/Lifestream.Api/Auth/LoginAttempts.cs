using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Auth;

/// <summary>
/// Failure counting and lockout for login names that have no password account (or belong to the demo account), so a locked real account
/// and an unknown name behave the same: both count, both lock after the same number of wrong guesses, both answer 429 with the same
/// Retry-After, also after a restart. The counters are rows of <c>UnknownLogin</c>, whose name column has the same collation as
/// <c>Staff.Username</c>: every spelling the database treats as one user name (soft hyphen, NUL, fullwidth letters, ss and the sharp s)
/// is one row, exactly as it is one account. Expired rows are purged whenever the table is used; no timer runs.
/// </summary>
public sealed class UnknownLoginStore(LifestreamDbContext db, TimeProvider time)
{
    private const string LockName = "login:unknown";
    private const int PurgeBatch = 200;

    /// <summary>Counts one guess atomically. Returns how long the name is locked for, or null when the guess may be checked.</summary>
    public async Task<TimeSpan?> ReserveAsync(string username, int maxAttempts, int lockoutMinutes, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await PurgeAsync(now, ct);
        return await db.RunAsync(async token =>
        {
            // One lock for the whole table: the key is decided by the database's collation, which a lock name cannot be.
            await db.LockAsync(LockName, token);
            var row = await db.UnknownLogins.FirstOrDefaultAsync(u => u.Name == username, token);
            if (row is null)
            {
                db.UnknownLogins.Add(row = new UnknownLogin { Name = username });
            }

            row.ExpiresAtUtc = now + UnknownLogin.Retention;
            TimeSpan? wait = null;
            if (row.LockoutEndUtc is { } until && until > now)
            {
                wait = until - now;
            }
            else
            {
                row.FailedCount++;
                if (row.FailedCount >= maxAttempts)
                {
                    row.LockoutEndUtc = now.AddMinutes(lockoutMinutes);
                    row.FailedCount = 0;
                }
            }

            await db.SaveChangesAsync(token);
            return wait;
        }, ct);
    }

    /// <summary>Removes the counters nobody has touched for the retention time (a locked name keeps its row until its lock has ended).</summary>
    private async Task PurgeAsync(DateTime now, CancellationToken ct)
    {
        var expired = await db.UnknownLogins.Where(u => u.ExpiresAtUtc <= now).OrderBy(u => u.ExpiresAtUtc).Select(u => u.Id).Take(PurgeBatch).ToListAsync(ct);
        if (expired.Count > 0)
        {
            await db.UnknownLogins.Where(u => expired.Contains(u.Id) && u.ExpiresAtUtc <= now).ExecuteDeleteAsync(ct);
        }
    }
}

/// <summary>The outcome of counting one guess against a real account.</summary>
public sealed record AttemptReservation(bool LockedNow);

public static class LoginProblems
{
    public static ApiException Locked(TimeSpan retryAfter) =>
        new(StatusCodes.Status429TooManyRequests, Lifestream.Contracts.ProblemCodes.LockedOut, "Too many failed attempts. Try again later.", retryAfter: retryAfter);
}
