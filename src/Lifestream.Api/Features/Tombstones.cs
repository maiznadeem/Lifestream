using System.Text.Json;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>
/// The server side of Undo after a delete (FEAT-06). A delete of a donor or patient keeps a copy of the row in <see cref="DeletedRecord"/>
/// in the same transaction; <c>POST {entity}/{id}/restore</c> inserts it again with the SAME id and data within <see cref="Window"/>, so
/// nothing (last-donation date, medical hold, the id other screens link to) is lost. The foreign keys are untouched: a donor or patient
/// that is still referenced is refused by the delete itself (409) exactly as before.
/// </summary>
internal static class Tombstones
{
    /// <summary>How long after the delete a restore is accepted (the client's Undo toast lasts 10 seconds; the margin covers a slow link).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Options = new() { IncludeFields = false };

    /// <summary>Copies the entity into the tombstone table and drops copies past the window. Call before removing the entity, in the same transaction.</summary>
    public static async Task KeepAsync<T>(LifestreamDbContext db, T entity, int id, TimeProvider time, CancellationToken ct)
        where T : class
    {
        var cutoff = time.GetUtcNow().UtcDateTime - Window;
        await db.DeletedRecords.Where(d => d.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
        db.DeletedRecords.Add(new DeletedRecord { Entity = typeof(T).Name, EntityId = id, Json = JsonSerializer.Serialize(entity, Options) });
    }

    /// <summary>
    /// Inserts the deleted row again with its id. 404 when nothing was deleted under that id or the window has passed, 409 when the id
    /// is in use again. Runs in one transaction; the insert is audited like any other.
    /// </summary>
    public static async Task<T> RestoreAsync<T>(LifestreamDbContext db, DbSet<T> set, string table, int id, string label, TimeProvider time, CancellationToken ct)
        where T : class, IEntity
    {
        var name = typeof(T).Name;
        return await db.RunAsync(async token =>
        {
            var cutoff = time.GetUtcNow().UtcDateTime - Window;
            var tomb = await db.DeletedRecords.Where(d => d.Entity == name && d.EntityId == id && d.DeletedAtUtc >= cutoff)
                .OrderByDescending(d => d.Id).FirstOrDefaultAsync(token)
                ?? throw ApiException.NotFound($"A recently deleted {label}");
            if (await set.AnyAsync(e => e.Id == id, token))
            {
                throw ApiException.Conflict($"The {label} is already back.");
            }

            var entity = JsonSerializer.Deserialize<T>(tomb.Json, Options)!;
            ClearRowVersion(entity);
            set.Add(entity);
            await SetIdentityInsertAsync(db, table, on: true, token);
            try
            {
                await db.SaveChangesAsync(token);
            }
            finally
            {
                await SetIdentityInsertAsync(db, table, on: false, token);
            }

            await db.DeletedRecords.Where(d => d.Entity == name && d.EntityId == id).ExecuteDeleteAsync(token);
            return entity;
        }, ct);
    }

    // Fixed statements per table (no text is ever built from input).
    private static Task<int> SetIdentityInsertAsync(LifestreamDbContext db, string table, bool on, CancellationToken ct) => (table, on) switch
    {
        ("Donor", true) => db.Database.ExecuteSqlAsync($"SET IDENTITY_INSERT [Donor] ON", ct),
        ("Donor", false) => db.Database.ExecuteSqlAsync($"SET IDENTITY_INSERT [Donor] OFF", ct),
        ("Patient", true) => db.Database.ExecuteSqlAsync($"SET IDENTITY_INSERT [Patient] ON", ct),
        ("Patient", false) => db.Database.ExecuteSqlAsync($"SET IDENTITY_INSERT [Patient] OFF", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Only donors and patients can be restored."),
    };

    private static void ClearRowVersion(object entity)
    {
        switch (entity)
        {
            case Donor donor:
                donor.RowVersion = [];
                break;
            case Patient patient:
                patient.RowVersion = [];
                break;
        }
    }
}
