using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Data;
using Lifestream.Data.Entities;
using System.Data;
using Microsoft.Data.SqlClient;
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
    private const int ConcurrencyRetries = 5;
    private const int PurgeBatch = 200;

    /// <summary>A real account has no <c>UnknownLogin</c> row and an unknown name has no <c>StaffCredential</c> row; this is what both are counted with.</summary>
    private readonly record struct Counters(int FailedCount, DateTime? LockoutEndUtc, DateTime? LastFailureUtc);

    /// <summary>
    /// Counts one wrong-guess opportunity atomically and answers whether the guess may be checked: throws 429 when the account (or unknown
    /// name) is locked. <paramref name="staffId"/> is null for a name without a password account.
    /// <para>
    /// Both cases run the SAME database work in every state (open, counting, locked, expired): one transaction with an application lock,
    /// a read of the credential counters, a read of the unknown-name counters, one credential update and one unknown-name batch (purge, update, insert).
    /// Which of the writes changes a row is decided by parameters, never by the shape of the statements, so the commands sent and the
    /// number of round trips do not tell a real name from an unknown one. A locked name writes nothing in either case.
    /// </para>
    /// </summary>
    public async Task<AttemptReservation> ReserveAsync(int? staffId, string username, int maxAttempts, int lockoutMinutes, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var (reservation, wait) = await db.RunAsync(token => ReserveOnceAsync(staffId, username, maxAttempts, lockoutMinutes, token), ct);
                return wait is { } w ? throw LoginProblems.Locked(w) : reservation; // thrown after the commit, so a locked name's purge is kept too
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConcurrencyRetries)
            {
                // Another writer (a password reset) changed the credential between the read and the write: count again from the fresh row.
            }
        }
    }

    private async Task<(AttemptReservation Reservation, TimeSpan? Wait)> ReserveOnceAsync(int? staffId, string username, int maxAttempts, int lockoutMinutes, CancellationToken token)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var id = staffId ?? 0; // no staff row has id 0: the credential read finds nothing for an unknown name

        // A lock per real account and one per unknown name, of the same shape (see LockResourceAsync).
        await db.LockAsync(await LockResourceAsync(staffId, username, token), token);

        var credential = await db.StaffCredentials.AsNoTracking().Where(c => c.StaffId == id)
            .Select(c => new { c.FailedCount, c.LockoutEndUtc, c.LastFailureUtc, c.RowVersion }).FirstOrDefaultAsync(token);
        var unknown = await db.UnknownLogins.AsNoTracking().Where(u => u.Name == username)
            .Select(u => new { u.FailedCount, u.LockoutEndUtc, u.ExpiresAtUtc }).FirstOrDefaultAsync(token);
        if (staffId is not null && credential is null)
        {
            throw AuthService.InvalidCredentials(); // the account was deleted between the lookup and now
        }

        // The unknown row's last failure is its expiry minus the retention time (it is written as now + retention with every counted guess).
        var current = staffId is null
            ? new Counters(unknown?.FailedCount ?? 0, unknown?.LockoutEndUtc, unknown is null ? null : unknown.ExpiresAtUtc - UnknownLogin.Retention)
            : new Counters(credential!.FailedCount, credential.LockoutEndUtc, credential.LastFailureUtc);

        if (current.LockoutEndUtc is { } until && until > now)
        {
            await WriteAsync(staffId, username, credential?.RowVersion, current, now, write: false, token);
            return (new AttemptReservation(false), until - now);
        }

        var failed = current.FailedCount;
        if (failed > 0 && current.LastFailureUtc is { } last && last + UnknownLogin.Retention <= now)
        {
            failed = 0; // idle for the retention time: the partial count is forgotten, whether or not the purge has deleted the row yet
        }

        failed++;
        var lockedNow = failed >= maxAttempts;
        var next = lockedNow ? new Counters(0, now.AddMinutes(lockoutMinutes), now) : new Counters(failed, current.LockoutEndUtc, now);
        await WriteAsync(staffId, username, credential?.RowVersion, next, now, write: true, token);
        return (new AttemptReservation(lockedNow), null);
    }

    /// <summary>
    /// The application lock that serialises the counting for one login name. A real account's is <c>login:{staffId}</c>. An unknown name's
    /// is <c>login:{n}</c> with n made from the database's own checksum of the name under the collation of the <c>UnknownLogin.Name</c> column
    /// (a lock name is compared byte for byte, so the database has to do the normalising: every spelling it treats as one name gives one
    /// checksum, one lock). Both are asked the same question, so the commands sent do not tell them apart, and under load a locked unknown name
    /// waits on no lock but its own, like a real account (the verifier measured 14 ms against 1.6 ms with one lock shared by every unknown name).
    /// </summary>
    public async Task<string> LockResourceAsync(int? staffId, string username, CancellationToken ct)
    {
        var checksum = (await db.Database.SqlQueryRaw<int>(
            "SELECT CHECKSUM(COALESCE((SELECT TOP (1) Name FROM UnknownLogin WHERE 1 = 0), @name)) AS Value",
            Param("@name", SqlDbType.NVarChar, username, UnknownLogin.MaxNameLength)).ToListAsync(ct)).Single();
        return LoginLocks.Resource(staffId, checksum);
    }

    /// <summary>
    /// The two writes, always both sent. The credential update is a no-op unless it is the real account's turn to write (and throws a
    /// concurrency exception if the row changed under it); the unknown-name batch purges expired rows (at most a batch, every time),
    /// then updates or inserts the name's row, but only for an unknown name.
    /// </summary>
    private async Task WriteAsync(int? staffId, string username, byte[]? rowVersion, Counters counters, DateTime now, bool write, CancellationToken token)
    {
        var realWrite = write && staffId is not null;
        var unknownWrite = write && staffId is null;
        var expires = now + UnknownLogin.Retention;

        var updated = await db.Database.ExecuteSqlRawAsync(
            "UPDATE StaffCredential SET FailedCount = @failed, LockoutEndUtc = @lockout, LastFailureUtc = @last WHERE StaffId = @id AND RowVersion = @rowVersion AND @write = 1",
            [Param("@failed", SqlDbType.Int, counters.FailedCount), Param("@lockout", SqlDbType.DateTime2, counters.LockoutEndUtc), Param("@last", SqlDbType.DateTime2, counters.LastFailureUtc),
                Param("@id", SqlDbType.Int, staffId ?? 0), Param("@rowVersion", SqlDbType.VarBinary, rowVersion ?? new byte[8]), Param("@write", SqlDbType.Int, realWrite ? 1 : 0)],
            token);
        if (realWrite && updated != 1)
        {
            throw new DbUpdateConcurrencyException("The credential changed while the guess was being counted.");
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            DELETE TOP (@batch) FROM UnknownLogin WHERE ExpiresAtUtc <= @now;
            UPDATE UnknownLogin SET FailedCount = @failed, LockoutEndUtc = @lockout, ExpiresAtUtc = @expires WHERE Name = @name AND @write = 1;
            INSERT INTO UnknownLogin (Name, FailedCount, LockoutEndUtc, ExpiresAtUtc)
                SELECT @name, @failed, @lockout, @expires WHERE @write = 1 AND NOT EXISTS (SELECT 1 FROM UnknownLogin WHERE Name = @name);
            """,
            [Param("@batch", SqlDbType.Int, PurgeBatch), Param("@now", SqlDbType.DateTime2, now), Param("@failed", SqlDbType.Int, counters.FailedCount), Param("@lockout", SqlDbType.DateTime2, counters.LockoutEndUtc),
                Param("@expires", SqlDbType.DateTime2, expires), Param("@name", SqlDbType.NVarChar, username, UnknownLogin.MaxNameLength), Param("@write", SqlDbType.Int, unknownWrite ? 1 : 0)],
            token);
    }

    private static SqlParameter Param(string name, SqlDbType type, object? value, int size = 0) =>
        new(name, type) { Value = value ?? DBNull.Value, Size = size };
}

/// <summary>The outcome of counting one guess against a real account.</summary>
public sealed record AttemptReservation(bool LockedNow);

public static class LoginLocks
{
    // Real account ids are below 2^31 and an unknown name's number is at least 2^32: the two never share a lock, and both are "login:" and digits.
    private const long UnknownBase = 1L << 32;

    public static string Resource(int? staffId, int nameChecksum) =>
        staffId is { } id ? $"login:{id}" : $"login:{UnknownBase + nameChecksum + (1L << 31)}";
}

public static class LoginProblems
{
    public static ApiException Locked(TimeSpan retryAfter) =>
        new(StatusCodes.Status429TooManyRequests, Lifestream.Contracts.ProblemCodes.LockedOut, "Too many failed attempts. Try again later.", retryAfter: retryAfter);
}
