using Lifestream.Data.Entities;
using System.Data;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;

namespace Lifestream.Data;

/// <summary>
/// The only way the application reaches the database. The schema is created by migrations only.
/// <para>
/// Auditing (DB-16): every insert, update and delete of a Donor, Patient, Request, Receipt, Appointment or Staff made
/// through <c>SaveChanges</c>/<c>SaveChangesAsync</c> (any overload) writes an <see cref="AuditLog"/> row, with the real
/// generated id, in the same transaction as the change. When the caller has no transaction open, the save opens one and
/// commits it only after the audit rows are written, so the change and its audit rows are atomic; when the caller owns a
/// transaction, the audit rows join it. See <see cref="AuditEnabled"/> and <see cref="CurrentStaffId"/>.
/// </para>
/// <para>
/// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> (and raw SQL) bypass change tracking and therefore bypass auditing. Bulk work on
/// audited tables must use <see cref="BulkDeleteAsync{T}"/> / <see cref="BulkUpdateAsync{T}"/>, which write the audit rows.
/// </para>
/// <para>
/// Time (DB-09): every <see cref="DateTime"/> column is UTC. Writing a value whose <see cref="DateTime.Kind"/> is Local or
/// Unspecified throws; values read back have Kind Utc (<see cref="UtcDateTimeConverter"/>).
/// </para>
/// </summary>
public sealed class LifestreamDbContext(DbContextOptions<LifestreamDbContext> options) : DbContext(options)
{
    private const int AuditRowsPerStatement = 400; // 4 parameters each: 1,600, under the 2,100-parameter limit

    public DbSet<Donor> Donors => Set<Donor>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<BloodBag> BloodBags => Set<BloodBag>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StaffCredential> StaffCredentials => Set<StaffCredential>();
    public DbSet<UnknownLogin> UnknownLogins => Set<UnknownLogin>();
    public DbSet<DemoRevocation> DemoRevocations => Set<DemoRevocation>();
    public DbSet<StaffPhoto> StaffPhotos => Set<StaffPhoto>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DemoState> DemoState => Set<DemoState>();
    public DbSet<DeletedRecord> DeletedRecords => Set<DeletedRecord>();

    /// <summary>The acting staff id written to audit rows (from the token); null for system work.</summary>
    public int? CurrentStaffId { get; set; }

    /// <summary>Turn off for bulk system work (generators, the demo reset), which is not a user action.</summary>
    public bool AuditEnabled { get; set; } = true;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as their names (varchar) and guarded by CHECK constraints (see EnumColumns).
        configurationBuilder.Properties<BloodType>().HaveConversion<string>().HaveMaxLength(5).AreUnicode(false);
        configurationBuilder.Properties<Gender>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<Role>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<Designation>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<Shift>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<DonorFrequency>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<AppointmentStatus>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<RequestStatus>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<ReceiptKind>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);
        configurationBuilder.Properties<BagStatus>().HaveConversion<string>().HaveMaxLength(16).AreUnicode(false);

        // Every timestamp is UTC datetime2; date-only values are DateOnly -> date.
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2(3)").HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveColumnType("datetime2(3)").HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LifestreamDbContext).Assembly);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        foreach (var (receipt, appointmentId, requestId) in AddedReceiptsNeedingParty())
        {
            receipt.DonorId = appointmentId is { } a ? Appointments.AsNoTracking().Where(x => x.Id == a).Select(x => (int?)x.DonorId).SingleOrDefault() : null;
            receipt.PatientId = requestId is { } r ? Requests.AsNoTracking().Where(x => x.Id == r).Select(x => (int?)x.PatientId).SingleOrDefault() : null;
        }

        if (!AuditEnabled)
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        if (Database.CurrentTransaction is not null)
        {
            return SaveAndAudit(acceptAllChangesOnSuccess);
        }

        return Database.CreateExecutionStrategy().Execute(() =>
        {
            using var transaction = Database.BeginTransaction();
            var rows = SaveAndAudit(acceptAllChangesOnSuccess);
            transaction.Commit();
            return rows;
        });
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        foreach (var (receipt, appointmentId, requestId) in AddedReceiptsNeedingParty())
        {
            receipt.DonorId = appointmentId is { } a ? await Appointments.AsNoTracking().Where(x => x.Id == a).Select(x => (int?)x.DonorId).SingleOrDefaultAsync(cancellationToken) : null;
            receipt.PatientId = requestId is { } r ? await Requests.AsNoTracking().Where(x => x.Id == r).Select(x => (int?)x.PatientId).SingleOrDefaultAsync(cancellationToken) : null;
        }

        if (!AuditEnabled)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        if (Database.CurrentTransaction is not null)
        {
            return await SaveAndAuditAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        return await Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            await using var transaction = await Database.BeginTransactionAsync(ct);
            var rows = await SaveAndAuditAsync(acceptAllChangesOnSuccess, ct);
            await transaction.CommitAsync(ct);
            return rows;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes the rows of <paramref name="rows"/> in one statement and writes one audit row per deleted id, atomically.
    /// This is the only supported bulk delete for the six audited tables (a bare <c>ExecuteDeleteAsync</c> writes no audit).
    /// Returns the number of rows deleted.
    /// </summary>
    public Task<int> BulkDeleteAsync<T>(IQueryable<T> rows, CancellationToken cancellationToken = default)
        where T : class, IEntity =>
        BulkAsync(
            rows,
            AuditActions.Delete,
            (ids, ct) => Set<T>().Where(e => ids.Contains(EF.Property<int>(e, nameof(IEntity.Id)))).ExecuteDeleteAsync(ct),
            cancellationToken);

    /// <summary>
    /// Updates the rows of <paramref name="rows"/> in one statement and writes one audit row per updated id, atomically.
    /// The bulk counterpart of an update through <c>SaveChanges</c>; a bare <c>ExecuteUpdateAsync</c> writes no audit.
    /// Returns the number of rows updated.
    /// </summary>
    public Task<int> BulkUpdateAsync<T>(IQueryable<T> rows, Action<UpdateSettersBuilder<T>> setters, CancellationToken cancellationToken = default)
        where T : class, IEntity =>
        BulkAsync(
            rows,
            AuditActions.Update,
            (ids, ct) => Set<T>().Where(e => ids.Contains(EF.Property<int>(e, nameof(IEntity.Id)))).ExecuteUpdateAsync(setters, ct),
            cancellationToken);

    private async Task<int> BulkAsync<T>(IQueryable<T> rows, string action, Func<List<int>, CancellationToken, Task<int>> run, CancellationToken cancellationToken)
        where T : class, IEntity
    {
        async Task<int> Work(CancellationToken ct)
        {
            var ids = await rows.Select(e => EF.Property<int>(e, nameof(IEntity.Id))).ToListAsync(ct);
            if (ids.Count == 0)
            {
                return 0;
            }

            // Donor blood types before the update: the appointments' copy of a changed one is rewritten below (DB-16).
            var donorTypesBefore = action == AuditActions.Update && typeof(T) == typeof(Donor)
                ? await Donors.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.BloodType }).ToDictionaryAsync(d => d.Id, d => d.BloodType, ct)
                : null;

            var changed = await run(ids, ct);
            if (changed != ids.Count)
            {
                throw new InvalidOperationException($"Bulk {action} changed {changed} rows but {ids.Count} were selected; nothing was kept.");
            }

            var audit = AuditEnabled && IsAudited(typeof(T)) ? ids.Select(id => (action, typeof(T).Name, id)).ToList() : [];
            if (donorTypesBefore is not null)
            {
                var donorTypesAfter = await Donors.AsNoTracking().Where(d => ids.Contains(d.Id)).Select(d => new { d.Id, d.BloodType }).ToListAsync(ct);
                foreach (var group in donorTypesAfter.Where(d => donorTypesBefore[d.Id] != d.BloodType).GroupBy(d => d.BloodType))
                {
                    var rewrite = await RewriteAppointmentBloodTypesAsync(group.Select(d => d.Id).ToList(), group.Key, ct);
                    if (AuditEnabled)
                    {
                        audit.AddRange(rewrite.Audit);
                    }
                }
            }

            if (audit.Count > 0)
            {
                await WriteAuditAsync(audit, ct);
            }

            return changed;
        }

        if (Database.CurrentTransaction is not null)
        {
            return await Work(cancellationToken);
        }

        return await Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            // Serializable: the rows selected are the rows changed, so the audit rows match exactly.
            await using var transaction = await Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var changed = await Work(ct);
            await transaction.CommitAsync(ct);
            return changed;
        }, cancellationToken);
    }

    private int SaveAndAudit(bool acceptAllChangesOnSuccess)
    {
        var rewrite = SyncDonorBloodType();
        var pending = PendingAudit();
        var temporaryIds = TemporaryIds();

        // Save without accepting: the entries stay as they were until the audit rows are written too, so a failure in
        // either step leaves the tracker able to repeat the whole save (execution-strategy retry, or the caller's own).
        try
        {
            var rows = base.SaveChanges(acceptAllChangesOnSuccess: false);
            if (pending.Count > 0 || rewrite.Audit.Count > 0)
            {
                WriteAudit([.. AuditRowsFor(pending), .. rewrite.Audit]);
            }

            if (acceptAllChangesOnSuccess)
            {
                ChangeTracker.AcceptAllChanges();
            }

            return rows;
        }
        catch
        {
            rewrite.Undo();
            RestoreTemporaryIds(temporaryIds);
            throw;
        }
    }

    private async Task<int> SaveAndAuditAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken)
    {
        var rewrite = await SyncDonorBloodTypeAsync(cancellationToken);
        var pending = PendingAudit();
        var temporaryIds = TemporaryIds();
        try
        {
            var rows = await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            if (pending.Count > 0 || rewrite.Audit.Count > 0)
            {
                await WriteAuditAsync([.. AuditRowsFor(pending), .. rewrite.Audit], cancellationToken);
            }

            if (acceptAllChangesOnSuccess)
            {
                ChangeTracker.AcceptAllChanges();
            }

            return rows;
        }
        catch
        {
            rewrite.Undo();
            RestoreTemporaryIds(temporaryIds);
            throw;
        }
    }

    // Appointment.DonorBloodType is a copy of the donor's blood type that makes "appointments by donor blood type" an index
    // seek. It is kept right here: set when an appointment is added, rewritten (one UPDATE, same transaction) when a donor's
    // blood type changes. The rewrite changes appointment rows, so it is audited like any other update of an appointment
    // (DB-16: an Update audit row per rewritten appointment, in the same transaction), and appointments that are tracked by this
    // context are brought up to date (new blood type, new row version), so editing one afterwards is not a concurrency error.
    private AppointmentRewrite SyncDonorBloodType()
    {
        foreach (var (appointment, donorId) in AddedAppointmentsNeedingBloodType())
        {
            appointment.DonorBloodType = Donors.AsNoTracking().Where(d => d.Id == donorId).Select(d => (BloodType?)d.BloodType).SingleOrDefault() ?? appointment.DonorBloodType;
        }

        var rewrite = new AppointmentRewrite();
        foreach (var group in DonorsWithNewBloodType().GroupBy(d => d.BloodType))
        {
            var donorIds = group.Select(d => d.DonorId).ToList();
            var type = group.Key;
            var before = Appointments.AsNoTracking().Where(a => donorIds.Contains(a.DonorId) && a.DonorBloodType != type).Select(a => new AppointmentVersion(a.Id, a.RowVersion)).ToList();
            if (before.Count == 0)
            {
                continue;
            }

            var ids = before.Select(v => v.Id).ToList();
            Appointments.Where(a => ids.Contains(a.Id)).ExecuteUpdate(s => s.SetProperty(a => a.DonorBloodType, type));
            var after = Appointments.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new AppointmentVersion(a.Id, a.RowVersion)).ToList();
            rewrite.Add(ids, RefreshTrackedAppointments(before, after, type));
        }

        return rewrite;
    }

    private async Task<AppointmentRewrite> SyncDonorBloodTypeAsync(CancellationToken cancellationToken)
    {
        foreach (var (appointment, donorId) in AddedAppointmentsNeedingBloodType())
        {
            appointment.DonorBloodType =
                await Donors.AsNoTracking().Where(d => d.Id == donorId).Select(d => (BloodType?)d.BloodType).SingleOrDefaultAsync(cancellationToken)
                ?? appointment.DonorBloodType;
        }

        var rewrite = new AppointmentRewrite();
        foreach (var group in DonorsWithNewBloodType().GroupBy(d => d.BloodType))
        {
            rewrite.Merge(await RewriteAppointmentBloodTypesAsync(group.Select(d => d.DonorId).ToList(), group.Key, cancellationToken));
        }

        return rewrite;
    }

    /// <summary>
    /// Sets <c>DonorBloodType</c> of every appointment of <paramref name="donorIds"/> that does not have <paramref name="type"/> yet, in one
    /// UPDATE, and returns what must be audited and how to undo the in-memory refresh of tracked appointments.
    /// </summary>
    private async Task<AppointmentRewrite> RewriteAppointmentBloodTypesAsync(List<int> donorIds, BloodType type, CancellationToken cancellationToken)
    {
        var rewrite = new AppointmentRewrite();
        var before = await Appointments.AsNoTracking().Where(a => donorIds.Contains(a.DonorId) && a.DonorBloodType != type).Select(a => new AppointmentVersion(a.Id, a.RowVersion)).ToListAsync(cancellationToken);
        if (before.Count == 0)
        {
            return rewrite;
        }

        var ids = before.Select(v => v.Id).ToList();
        await Appointments.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.DonorBloodType, type), cancellationToken);
        var after = await Appointments.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => new AppointmentVersion(a.Id, a.RowVersion)).ToListAsync(cancellationToken);
        rewrite.Add(ids, RefreshTrackedAppointments(before, after, type));
        return rewrite;
    }

    /// <summary>The appointments a donor's blood-type change rewrote: their audit rows, and the way back for the tracked entities.</summary>
    private sealed class AppointmentRewrite
    {
        private readonly List<Action> _undo = [];

        public List<(string Action, string Entity, int EntityId)> Audit { get; } = [];

        public void Add(List<int> appointmentIds, Action undo)
        {
            Audit.AddRange(appointmentIds.Select(id => (AuditActions.Update, nameof(Appointment), id)));
            _undo.Add(undo);
        }

        public void Merge(AppointmentRewrite other)
        {
            Audit.AddRange(other.Audit);
            _undo.AddRange(other._undo);
        }

        /// <summary>After a failed save the database rolled the rewrite back, so the tracked entities go back to what they were.</summary>
        public void Undo()
        {
            foreach (var undo in _undo)
            {
                undo();
            }
        }
    }

    private sealed record AppointmentVersion(int Id, byte[] RowVersion);

    /// <summary>
    /// Appointments that this context tracks and that were just rewritten get the new blood type and the new row version (only when
    /// their row version was still the one the rewrite replaced, so a real concurrent change still fails its own save).
    /// </summary>
    private Action RefreshTrackedAppointments(List<AppointmentVersion> before, List<AppointmentVersion> after, BloodType type)
    {
        var undo = new List<Action>();
        var oldVersions = before.ToDictionary(v => v.Id, v => v.RowVersion);
        var newVersions = after.ToDictionary(v => v.Id, v => v.RowVersion);
        foreach (var entry in ChangeTracker.Entries<Appointment>().Where(e => e.State is EntityState.Unchanged or EntityState.Modified && oldVersions.ContainsKey(e.Entity.Id)).ToList())
        {
            var id = entry.Entity.Id;
            var version = entry.Property(a => a.RowVersion);
            var blood = entry.Property(a => a.DonorBloodType);
            var oldOriginalVersion = version.OriginalValue;
            var oldCurrentBlood = blood.CurrentValue;
            var oldOriginalBlood = blood.OriginalValue;
            var oldBloodModified = blood.IsModified;
            var oldState = entry.State;

            blood.OriginalValue = type;
            blood.CurrentValue = type;
            blood.IsModified = false;
            if (version.OriginalValue is byte[] original && original.AsSpan().SequenceEqual(oldVersions[id]) && newVersions.TryGetValue(id, out var fresh))
            {
                version.OriginalValue = fresh;
                version.CurrentValue = fresh;
            }

            if (oldState == EntityState.Unchanged)
            {
                entry.State = EntityState.Unchanged;
            }

            undo.Add(() =>
            {
                version.OriginalValue = oldOriginalVersion;
                blood.OriginalValue = oldOriginalBlood;
                blood.CurrentValue = oldCurrentBlood;
                blood.IsModified = oldBloodModified;
                if (oldState == EntityState.Unchanged)
                {
                    entry.State = EntityState.Unchanged;
                }
            });
        }

        return () => undo.ForEach(u => u());
    }

    /// <summary>
    /// Receipt.DonorId and Receipt.PatientId are the receipt's party, a copy of its appointment's donor or its request's patient (see
    /// <see cref="Receipt.DonorId"/>). Every save sets them on the receipts being added, audited or not, so no code that creates a receipt has to:
    /// a receipt added with its appointment or request as an object gets the id from Entity Framework (the foreign key includes it); one added
    /// by id takes it here from the appointment or request this context tracks, and the rest are returned to be looked up. A receipt whose
    /// appointment or request does not exist keeps no party, and the database refuses it.
    /// </summary>
    private List<(Receipt Receipt, int? AppointmentId, int? RequestId)> AddedReceiptsNeedingParty()
    {
        var lookups = new List<(Receipt, int?, int?)>();
        foreach (var entry in ChangeTracker.Entries<Receipt>().Where(e => e.State == EntityState.Added).ToList())
        {
            var receipt = entry.Entity;
            if (receipt.Appointment is not null || receipt.Request is not null || receipt.DonorId is not null || receipt.PatientId is not null
                || (receipt.AppointmentId is null && receipt.RequestId is null))
            {
                continue;
            }

            if (receipt.AppointmentId is { } appointmentId && ChangeTracker.Entries<Appointment>().FirstOrDefault(e => e.Entity.Id == appointmentId) is { } appointment)
            {
                receipt.DonorId = appointment.Entity.DonorId;
            }
            else if (receipt.RequestId is { } requestId && ChangeTracker.Entries<Request>().FirstOrDefault(e => e.Entity.Id == requestId) is { } request)
            {
                receipt.PatientId = request.Entity.PatientId;
            }
            else
            {
                lookups.Add((receipt, receipt.AppointmentId, receipt.RequestId));
            }
        }

        return lookups;
    }

    /// <summary>New appointments: those whose donor is already loaded or new get the type directly; the rest need a lookup.</summary>
    private List<(Appointment Appointment, int DonorId)> AddedAppointmentsNeedingBloodType()
    {
        ChangeTracker.DetectChanges();
        var lookups = new List<(Appointment, int)>();
        foreach (var entry in ChangeTracker.Entries<Appointment>().Where(e => e.State == EntityState.Added).ToList())
        {
            if (entry.Entity.Donor is { } donor)
            {
                entry.Entity.DonorBloodType = donor.BloodType;
            }
            else
            {
                lookups.Add((entry.Entity, entry.Entity.DonorId));
            }
        }

        return lookups;
    }

    private List<(int DonorId, BloodType BloodType)> DonorsWithNewBloodType() =>
        ChangeTracker.Entries<Donor>()
            .Where(e => e.State == EntityState.Modified && e.Property(d => d.BloodType).IsModified && !e.Property(d => d.Id).IsTemporary)
            .Select(e => (e.Entity.Id, e.Entity.BloodType))
            .ToList();

    /// <summary>The temporary id of every entity about to be inserted, so a rolled-back save can be repeated from the same state.</summary>
    private List<(PropertyEntry Id, object? Temporary)> TemporaryIds() =>
        ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added && e.Entity is IEntity)
            .Select(e => e.Property(nameof(IEntity.Id)))
            .Where(p => p.IsTemporary)
            .Select(p => (p, p.CurrentValue))
            .ToList();

    /// <summary>
    /// After a failed save the database rolled the inserts back, but entries saved before the failure already carry the
    /// generated id. Putting the temporary id back makes a repeat insert a new row instead of a duplicate-key error.
    /// </summary>
    private static void RestoreTemporaryIds(List<(PropertyEntry Id, object? Temporary)> saved)
    {
        foreach (var (id, temporary) in saved)
        {
            if (!id.IsTemporary)
            {
                id.CurrentValue = temporary;
                id.IsTemporary = true;
            }
        }
    }

    /// <summary>The audited entries about to be saved and what is happening to each. Ids are read after the save.</summary>
    private List<(string Action, EntityEntry Entry)> PendingAudit()
    {
        ChangeTracker.DetectChanges();
        var pending = new List<(string, EntityEntry)>();
        foreach (var entry in ChangeTracker.Entries())
        {
            // A password set or reset is an update of the person (audited as such); failed-login counters and lockouts are not an edit of anyone's record.
            if (entry.Entity is StaffCredential && entry.State == EntityState.Modified && entry.Property(nameof(StaffCredential.PasswordHash)).IsModified)
            {
                pending.Add((AuditActions.Update, entry));
                continue;
            }

            if (entry.Entity is not IEntity entity || !IsAudited(entity.GetType()))
            {
                continue;
            }

            var action = entry.State switch
            {
                EntityState.Added => AuditActions.Insert,
                EntityState.Modified => AuditActions.Update,
                EntityState.Deleted => AuditActions.Delete,
                _ => null,
            };
            if (action is not null)
            {
                pending.Add((action, entry));
            }
        }

        return pending;
    }

    // Read from the entry, not the entity: until AcceptAllChanges the generated key is held by the change tracker and the
    // entity's own Id property can still be 0 (reading it was what wrote audit rows with entity id 0).
    private static List<(string Action, string Entity, int EntityId)> AuditRowsFor(List<(string Action, EntityEntry Entry)> pending) =>
        pending.Select(p => p.Entry.Entity is StaffCredential
            ? (p.Action, nameof(Entities.Staff), (int)p.Entry.Property(nameof(StaffCredential.StaffId)).CurrentValue!)
            : (p.Action, p.Entry.Entity.GetType().Name, (int)p.Entry.Property(nameof(IEntity.Id)).CurrentValue!)).ToList();

    // The audit rows are inserted with SQL in the current transaction rather than through the change tracker: a second
    // tracked save would send the business rows again (the cause of the double insert this replaces).
    private void WriteAudit(List<(string Action, string Entity, int EntityId)> rows)
    {
        foreach (var (sql, parameters) in AuditStatements(rows))
        {
            Database.ExecuteSqlRaw(sql, parameters);
        }
    }

    private async Task WriteAuditAsync(List<(string Action, string Entity, int EntityId)> rows, CancellationToken cancellationToken)
    {
        foreach (var (sql, parameters) in AuditStatements(rows))
        {
            await Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
        }
    }

    // OccurredAtUtc is left out: the database default SYSUTCDATETIME() stamps it (server clock, never a client clock).
    private IEnumerable<(string Sql, object[] Parameters)> AuditStatements(List<(string Action, string Entity, int EntityId)> rows)
    {
        foreach (var chunk in rows.Chunk(AuditRowsPerStatement))
        {
            var values = string.Join(",", chunk.Select((_, i) => $"(@s{i},@a{i},@e{i},@i{i})"));
            var parameters = new List<object>();
            for (var i = 0; i < chunk.Length; i++)
            {
                parameters.Add(new SqlParameter($"@s{i}", SqlDbType.Int) { Value = (object?)CurrentStaffId ?? DBNull.Value });
                parameters.Add(new SqlParameter($"@a{i}", SqlDbType.VarChar, 10) { Value = chunk[i].Action });
                parameters.Add(new SqlParameter($"@e{i}", SqlDbType.VarChar, 30) { Value = chunk[i].Entity });
                parameters.Add(new SqlParameter($"@i{i}", SqlDbType.Int) { Value = chunk[i].EntityId });
            }

            yield return ($"INSERT INTO [AuditLog] ([StaffId], [Action], [Entity], [EntityId]) VALUES {values}", parameters.ToArray());
        }
    }

    private static bool IsAudited(Type entity) =>
        entity == typeof(Donor) || entity == typeof(Patient) || entity == typeof(Request) ||
        entity == typeof(Receipt) || entity == typeof(Appointment) || entity == typeof(Entities.Staff);
}
