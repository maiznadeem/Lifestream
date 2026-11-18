using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Data;

/// <summary>
/// The only way the application reaches the database. The schema is created by migrations only.
/// Writes to the six business tables (donor, patient, request, receipt, appointment, staff) write an
/// <see cref="AuditLog"/> row in the same save; see <see cref="AuditEnabled"/> and <see cref="CurrentStaffId"/>.
/// </summary>
public sealed class LifestreamDbContext(DbContextOptions<LifestreamDbContext> options) : DbContext(options)
{
    private bool _auditing;

    public DbSet<Donor> Donors => Set<Donor>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<BloodBag> BloodBags => Set<BloodBag>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<StaffPhoto> StaffPhotos => Set<StaffPhoto>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<DemoState> DemoState => Set<DemoState>();

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
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime2(3)");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LifestreamDbContext).Assembly);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var deferred = StageAudit();
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        if (deferred.Count > 0)
        {
            AuditLogs.AddRange(deferred.Select(d => NewAudit(d.Action, d.Entity)));
            SaveAuditOnly(() => base.SaveChanges(acceptAllChangesOnSuccess));
        }

        return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var deferred = StageAudit();
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        if (deferred.Count > 0)
        {
            AuditLogs.AddRange(deferred.Select(d => NewAudit(d.Action, d.Entity)));
            _auditing = true;
            try
            {
                await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            }
            finally
            {
                _auditing = false;
            }
        }

        return result;
    }

    private void SaveAuditOnly(Action save)
    {
        _auditing = true;
        try
        {
            save();
        }
        finally
        {
            _auditing = false;
        }
    }

    /// <summary>
    /// Adds audit rows for updates, deletes and inserts whose id is already known (same batch, same transaction).
    /// Inserts that get an identity value from the database are returned so they are audited right after the save.
    /// </summary>
    private List<(string Action, IEntity Entity)> StageAudit()
    {
        var deferred = new List<(string, IEntity)>();
        if (!AuditEnabled || _auditing)
        {
            return deferred;
        }

        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is not IEntity entity || !IsAudited(entity))
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
            if (action is null)
            {
                continue;
            }

            if (entry.Property(nameof(IEntity.Id)).IsTemporary)
            {
                deferred.Add((action, entity));
            }
            else
            {
                AuditLogs.Add(NewAudit(action, entity));
            }
        }

        return deferred;
    }

    private AuditLog NewAudit(string action, IEntity entity) => new()
    {
        StaffId = CurrentStaffId,
        Action = action,
        Entity = entity.GetType().Name,
        EntityId = entity.Id,
        // OccurredAtUtc is left unset: the database default SYSUTCDATETIME() stamps it (server clock).
    };

    private static bool IsAudited(IEntity entity) =>
        entity is Donor or Patient or Request or Receipt or Appointment or Entities.Staff;
}
