using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lifestream.Data.Seed;

/// <summary>
/// Restores the demo data without a timer: the API calls <see cref="RunIfDueAsync(LifestreamDbContext, DateTime, CancellationToken)"/> on the first authenticated request of each
/// UTC day. An application lock (<c>sp_getapplock</c>) taken inside a serializable transaction makes sure exactly one caller
/// does the work, however many arrive at once. Protected staff rows (the owner admin) and their audit rows are kept; every other
/// table is rebuilt from <see cref="DemoDataGenerator"/>.
/// </summary>
public static class DemoReset
{
    public const string LockName = "lifestream-demo-reset";

    /// <summary>A manual "Reset demo data" is allowed at most once in this time, for everyone together.</summary>
    public static readonly TimeSpan ForceCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resets when the last reset was before the UTC date of <paramref name="nowUtc"/> (the caller's clock). Returns true for the one caller that did it.
    /// The data is built for that instant: what is over by then is completed, the rest of the day is still to come (<see cref="IDemoDataGenerator"/>).
    /// </summary>
    public static Task<bool> RunIfDueAsync(LifestreamDbContext db, DateTime nowUtc, CancellationToken cancellationToken) =>
        ResetAsync(db, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Mode.Daily, cancellationToken);

    /// <summary>The same, with the clinic as it stands at 00:00 UTC of <paramref name="todayUtc"/>.</summary>
    public static Task<bool> RunIfDueAsync(LifestreamDbContext db, DateOnly todayUtc, CancellationToken cancellationToken) =>
        RunIfDueAsync(db, todayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), cancellationToken);

    /// <summary>Resets now unless a manual reset already happened in the last <see cref="ForceCooldown"/>. Returns false inside the cooldown.</summary>
    public static Task<bool> ForceAsync(LifestreamDbContext db, DateTime nowUtc, CancellationToken cancellationToken) =>
        ResetAsync(db, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Mode.Forced, cancellationToken);

    /// <summary>The same, with the clinic as it stands at 00:00 UTC of <paramref name="todayUtc"/>.</summary>
    public static Task<bool> ForceAsync(LifestreamDbContext db, DateOnly todayUtc, CancellationToken cancellationToken) =>
        ForceAsync(db, todayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), cancellationToken);

    /// <summary>
    /// Rebuilds a demo that was already reset on the UTC date of <paramref name="nowUtc"/> but holds no appointment on that date: data written by a release
    /// whose generator left today empty (before 2.1). The migrate job calls it, so a deploy does not leave the Today view empty until the next UTC midnight.
    /// A dataset of this generator always has appointments today, so this does nothing to it; it does nothing either when the last reset was on another day
    /// (the daily reset, or nobody's: a database whose demo is switched off keeps its data).
    /// </summary>
    public static Task<bool> RunIfTodayIsMissingAsync(LifestreamDbContext db, DateTime nowUtc, CancellationToken cancellationToken) =>
        ResetAsync(db, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Mode.TodayMissing, cancellationToken);

    /// <summary>
    /// Answers whether the daily reset is due at <paramref name="nowUtc"/> and changes nothing. It takes the same steps as the daily check (the
    /// transaction, the lock, the two reads), so a new process that calls it once has loaded and compiled what its first signed-in request needs.
    /// </summary>
    public static Task<bool> IsDueAsync(LifestreamDbContext db, DateTime nowUtc, CancellationToken cancellationToken) =>
        ResetAsync(db, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), Mode.CheckOnly, cancellationToken);

    private enum Mode
    {
        Daily,
        Forced,
        TodayMissing,
        CheckOnly,
    }

    private static async Task<bool> ResetAsync(LifestreamDbContext db, DateTime nowUtc, Mode mode, CancellationToken ct)
    {
        var todayUtc = DateOnly.FromDateTime(nowUtc);
        var force = mode == Mode.Forced;
        var previousAudit = db.AuditEnabled;
        db.AuditEnabled = false;
        try
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await TakeLockAsync(db, transaction.GetDbTransaction(), ct);

                var state = await db.DemoState.SingleOrDefaultAsync(ct);
                // A raw query returns Kind=Unspecified; the server clock is UTC by definition.
                var serverNow = DateTime.SpecifyKind(await db.Database.SqlQuery<DateTime>($"SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(ct), DateTimeKind.Utc);
                var due = mode switch
                {
                    Mode.Forced => state?.LastForcedResetAtUtc is not { } last || serverNow - last >= ForceCooldown,
                    Mode.TodayMissing => state?.LastResetDate == todayUtc && !await HasAppointmentsOnAsync(db, todayUtc, ct),
                    _ => state?.LastResetDate is not { } lastDate || lastDate < todayUtc,
                };
                if (!due)
                {
                    await transaction.CommitAsync(ct);
                    return false;
                }

                if (mode == Mode.CheckOnly)
                {
                    await transaction.CommitAsync(ct);
                    db.ChangeTracker.Clear();
                    return true; // due, and left for the first signed-in request to do
                }

                await WipeAsync(db, ct);
                new DemoDataGenerator().Generate(nowUtc).AddTo(db);
                if (state is null)
                {
                    state = new Entities.DemoState();
                    db.DemoState.Add(state);
                }

                state.LastResetDate = todayUtc;
                if (force)
                {
                    state.LastForcedResetAtUtc = serverNow;
                }

                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                db.ChangeTracker.Clear();
                return true;
            });
        }
        finally
        {
            db.AuditEnabled = previousAudit;
        }
    }

    private static async Task TakeLockAsync(LifestreamDbContext db, System.Data.Common.DbTransaction transaction, CancellationToken ct)
    {
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sp_getapplock";
        command.Parameters.AddWithValue("@Resource", LockName);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Transaction");
        command.Parameters.AddWithValue("@LockTimeout", 120_000);
        var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(result);
        await command.ExecuteNonQueryAsync(ct);
        if ((int)result.Value < 0)
        {
            throw new InvalidOperationException($"Could not take the demo reset lock (sp_getapplock returned {result.Value}).");
        }
    }

    private static Task<bool> HasAppointmentsOnAsync(LifestreamDbContext db, DateOnly dayUtc, CancellationToken ct)
    {
        var start = dayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        return db.Appointments.AnyAsync(a => a.SlotStartUtc >= start && a.SlotStartUtc < end, ct);
    }

    /// <summary>Set-based deletes in foreign-key order. Protected staff, their tokens, photos and audit rows stay.</summary>
    private static async Task WipeAsync(LifestreamDbContext db, CancellationToken ct)
    {
        await db.BloodBags.ExecuteDeleteAsync(ct);
        await db.Receipts.ExecuteDeleteAsync(ct);
        await db.Requests.ExecuteDeleteAsync(ct);
        await db.Appointments.ExecuteDeleteAsync(ct);
        await db.Patients.ExecuteDeleteAsync(ct);
        await db.Donors.ExecuteDeleteAsync(ct);
        await db.DeletedRecords.ExecuteDeleteAsync(ct);
        await db.RefreshTokens.Where(t => !t.Staff!.IsProtected).ExecuteDeleteAsync(ct);
        await db.StaffPhotos.Where(p => !p.Staff!.IsProtected).ExecuteDeleteAsync(ct);
        await db.AuditLogs.Where(a => !db.Staff.Any(s => s.IsProtected && s.Id == a.StaffId)).ExecuteDeleteAsync(ct);
        await db.Staff.Where(s => !s.IsProtected).ExecuteDeleteAsync(ct);
    }
}
