using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// The <c>--migrate</c> entry point (the Container Apps migrate job). Applies the EF Core migrations, makes sure the demo data
/// exists (a database that was never reset is seeded; a demo reset today by an older release, which has no appointments today, is rebuilt), creates or refreshes the protected owner admin from
/// <c>Owner:AdminPassword</c>, and returns the process exit code: 0 on success, 1 on any failure. The password is never logged.
/// </summary>
public static class Migrator
{
    public const string OwnerUsername = "owner";

    public static async Task<int> RunAsync(string[] args, TimeProvider? time = null, ILoggerProvider? extraLogs = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        StandardLogging.Configure(builder.Logging, builder.Configuration, "worker");
        if (extraLogs is not null)
        {
            builder.Logging.AddProvider(extraLogs);
        }

        builder.Services.Configure<DatabaseSettings>(builder.Configuration.GetSection(DatabaseSettings.Section));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lifestream.Migrate");
        try
        {
            var connection = builder.Configuration.GetConnectionString("Lifestream");
            if (string.IsNullOrWhiteSpace(connection))
            {
                logger.LogError("ConnectionStrings:Lifestream is not configured.");
                return 1;
            }

            var database = host.Services.GetRequiredService<IOptions<DatabaseSettings>>().Value;
            var demoEnabled = (builder.Configuration.GetSection(DemoSettings.Section).Get<DemoSettings>() ?? new DemoSettings()).Enabled;
            var options = new DbContextOptionsBuilder<LifestreamDbContext>()
                .UseSqlServer(connection, sql => sql.ExecutionStrategy(d => new WakeAwareExecutionStrategy(d, database)))
                .Options;
            await using var db = new LifestreamDbContext(options);

            logger.LogInformation("Applying migrations.");
            await db.Database.MigrateAsync();

            var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var neverReset = await db.DemoState.AnyAsync(s => s.LastResetDate != null) is false;
            if (neverReset)
            {
                logger.LogInformation("Seeding the demo data.");
                await DemoReset.RunIfDueAsync(db, now, CancellationToken.None);
            }
            else if (demoEnabled && await DemoReset.RunIfTodayIsMissingAsync(db, now, CancellationToken.None))
            {
                // Today's demo data came from a release whose generator left today without appointments: rebuilt now, not at the next UTC midnight.
                logger.LogInformation("The demo data of today had no appointments today; rebuilt.");
            }

            var ownerPassword = builder.Configuration["Owner:AdminPassword"];
            if (string.IsNullOrEmpty(ownerPassword))
            {
                logger.LogWarning("Owner:AdminPassword is not set; the owner admin account was not created or changed.");
            }
            else
            {
                await EnsureOwnerAsync(db, ownerPassword, logger);
            }

            logger.LogInformation("Migration finished.");
            return 0;
        }
        catch (Exception exception)
        {
            // Type and SQL number only: an exception message could carry a connection detail.
            logger.LogError(LogEvents.Exception, "Migration failed: {ExceptionType} (sql {SqlNumber})", exception.GetType().Name, DatabaseErrors.NumberOf(exception));
            return 1;
        }
    }

    private static async Task EnsureOwnerAsync(LifestreamDbContext db, string password, ILogger logger)
    {
        var owner = await db.Staff.Include(s => s.Credential).FirstOrDefaultAsync(s => s.Username == OwnerUsername);
        if (owner is null)
        {
            db.Staff.Add(new Staff
            {
                DisplayName = "Owner",
                Username = OwnerUsername,
                Credential = new StaffCredential { PasswordHash = PasswordHashing.Hash(password) },
                Gender = Gender.Other,
                DateOfBirth = new DateOnly(1990, 1, 1),
                Designation = Designation.Admin,
                Shift = Shift.FullTime,
                Role = Role.Admin,
                IsProtected = true,
            });
            await db.SaveChangesAsync();
            logger.LogInformation("Owner admin account created.");
            return;
        }

        // Only rewrite the hash when the configured password no longer matches, so a re-run changes nothing.
        var changed = false;
        if (owner.Credential is null)
        {
            owner.Credential = new StaffCredential { PasswordHash = PasswordHashing.Hash(password) };
            changed = true;
        }
        else if (!PasswordHashing.Verify(owner.Credential.PasswordHash, password))
        {
            owner.Credential.PasswordHash = PasswordHashing.Hash(password);
            owner.Credential.SecurityStamp = Guid.NewGuid();
            changed = true;
        }

        if (!owner.IsProtected || owner.Role != Role.Admin)
        {
            owner.IsProtected = true;
            owner.Role = Role.Admin;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync();
        }

        logger.LogInformation("Owner admin account checked ({Changed}).", changed ? "updated" : "unchanged");
    }
}
