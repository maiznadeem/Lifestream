using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Lifestream.Api.Common;
using Lifestream.Api.Infrastructure;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Features;

/// <summary>
/// The server side of Undo after a delete (FEAT-06). A delete of a donor, patient or staff member keeps a copy of the row in <see cref="DeletedRecord"/>
/// in the same transaction; <c>POST {entity}/{id}/restore</c> inserts it again with the SAME id and data within <see cref="Window"/>, so
/// nothing (last-donation date, medical hold, the id other screens link to) is lost. The foreign keys are untouched: a donor or patient
/// that is still referenced is refused by the delete itself (409) exactly as before.
/// </summary>
internal static class Tombstones
{
    /// <summary>How long after the delete a restore is accepted: the shared <see cref="UndoWindow"/>, which the client reads too.</summary>
    public static readonly TimeSpan Window = Lifestream.Contracts.UndoWindow.Duration;

    // Only the columns a restore needs: everything the row holds except the row version, which the database makes anew.
    private static readonly JsonSerializerOptions Options = new()
    {
        // A staff snapshot holds the person and the credential, which point back at each other: the way back is dropped, not followed.
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { DropRowVersion },
        },
    };

    private static void DropRowVersion(JsonTypeInfo info)
    {
        for (var i = info.Properties.Count - 1; i >= 0; i--)
        {
            if (info.Properties[i].Name.Equals("rowVersion", StringComparison.OrdinalIgnoreCase))
            {
                info.Properties.RemoveAt(i);
            }
        }
    }

    /// <summary>Drops copies past the window. There is no timer (it would wake the paused database), so every delete and every restore does this.</summary>
    private static Task<int> PurgeExpiredAsync(LifestreamDbContext db, TimeProvider time, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().UtcDateTime - Window;
        return db.DeletedRecords.Where(d => d.DeletedAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    /// <summary>The same as <see cref="KeepAsync{T}"/> for a row that is more than one table (a person with their credential and photo): the snapshot object is stored under <paramref name="entity"/>.</summary>
    public static async Task KeepSnapshotAsync(LifestreamDbContext db, string entity, object snapshot, int id, TimeProvider time, CancellationToken ct)
    {
        await PurgeExpiredAsync(db, time, ct);
        db.DeletedRecords.Add(new DeletedRecord { Entity = entity, EntityId = id, Json = JsonSerializer.Serialize(snapshot, Options) });
    }

    /// <summary>
    /// Brings back a snapshot kept by <see cref="KeepSnapshotAsync"/>: <paramref name="insert"/> adds the entities (and refuses with a 409 when the id or a
    /// unique value is in use again); the save runs with identity insert on for <paramref name="table"/> so the id is the old one. 404 outside the window.
    /// </summary>
    public static Task<TResult> RestoreSnapshotAsync<TSnapshot, TResult>(LifestreamDbContext db, string entity, string table, int id, string label, TimeProvider time, Func<TSnapshot, CancellationToken, Task<TResult>> insert, CancellationToken ct)
        where TSnapshot : class =>
        db.RunAsync(async token =>
        {
            await PurgeExpiredAsync(db, time, token);
            var cutoff = time.GetUtcNow().UtcDateTime - Window;
            var tomb = await db.DeletedRecords.Where(d => d.Entity == entity && d.EntityId == id && d.DeletedAtUtc >= cutoff)
                .OrderByDescending(d => d.Id).FirstOrDefaultAsync(token)
                ?? throw ApiException.NotFound($"A recently deleted {label}");
            var result = await insert(JsonSerializer.Deserialize<TSnapshot>(tomb.Json, Options)!, token);
            await SetIdentityInsertAsync(db, table, on: true, token);
            try
            {
                await db.SaveChangesAsync(token);
            }
            finally
            {
                await SetIdentityInsertAsync(db, table, on: false, token);
            }

            await db.DeletedRecords.Where(d => d.Entity == entity && d.EntityId == id).ExecuteDeleteAsync(token);
            return result;
        }, ct);

    /// <summary>Copies the entity into the tombstone table and drops copies past the window. Call before removing the entity, in the same transaction.</summary>
    public static async Task KeepAsync<T>(LifestreamDbContext db, T entity, int id, TimeProvider time, CancellationToken ct)
        where T : class
    {
        await PurgeExpiredAsync(db, time, ct);
        db.DeletedRecords.Add(new DeletedRecord { Entity = typeof(T).Name, EntityId = id, Json = JsonSerializer.Serialize(entity, Options) });
    }

    /// <summary>
    /// Inserts the deleted row again with its id. 404 when nothing was deleted under that id or the window has passed, 409 when the id
    /// is in use again. Runs in one transaction; the insert is audited like any other.
    /// </summary>
    public static async Task<T> RestoreAsync<T>(LifestreamDbContext db, DbSet<T> set, string table, int id, string label, TimeProvider time, Func<CancellationToken, Task> ensureRoom, CancellationToken ct)
        where T : class, IEntity
    {
        var name = typeof(T).Name;
        return await db.RunAsync(async token =>
        {
            await PurgeExpiredAsync(db, time, token);
            var cutoff = time.GetUtcNow().UtcDateTime - Window;
            var tomb = await db.DeletedRecords.Where(d => d.Entity == name && d.EntityId == id && d.DeletedAtUtc >= cutoff)
                .OrderByDescending(d => d.Id).FirstOrDefaultAsync(token)
                ?? throw ApiException.NotFound($"A recently deleted {label}");
            if (await set.AnyAsync(e => e.Id == id, token))
            {
                throw ApiException.Conflict($"The {label} is already back.");
            }

            await ensureRoom(token); // a restore is a create: the demo's table caps apply to it too
            var entity = JsonSerializer.Deserialize<T>(tomb.Json, Options)!;
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
        ("Staff", true) => db.Database.ExecuteSqlAsync($"SET IDENTITY_INSERT [Staff] ON", ct),
        ("Staff", false) => db.Database.ExecuteSqlAsync($"SET IDENTITY_INSERT [Staff] OFF", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Only donors, patients and staff can be restored."),
    };
}
