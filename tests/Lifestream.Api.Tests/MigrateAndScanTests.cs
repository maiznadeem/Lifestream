using System.Text.RegularExpressions;
using Lifestream.Api.Infrastructure;
using Lifestream.Api.Tests.Data;
using Lifestream.Api.Tests.Support;
using Lifestream.Contracts;
using Lifestream.Contracts.Lookups;
using Lifestream.Data.Security;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lifestream.Api.Tests;

/// <summary>The <c>--migrate</c> entry point run by the Container Apps migrate job.</summary>
public sealed class MigrateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each test gets its own empty database (named Lifestream_Test_..., dropped and checked by the fixture).</summary>
    private static async Task<EmptyDatabaseFixture> NewEmptyAsync()
    {
        var fixture = new EmptyDatabaseFixture();
        await fixture.InitializeAsync();
        return fixture;
    }

    private static string[] Args(EmptyDatabaseFixture db, string? ownerPassword, string? connection = null)
    {
        var args = new List<string> { $"--ConnectionStrings:Lifestream={connection ?? db.ConnectionString}", "--Database:WakeBudgetSeconds=1" };
        if (ownerPassword is not null)
        {
            args.Add($"--Owner:AdminPassword={ownerPassword}");
        }

        return [.. args];
    }

    [Fact]
    public async Task Migrate_OnAnEmptyDatabase_AppliesSchema_CreatesTheOwner_SeedsTheDemo_Exit0_NothingSecretLogged()
    {
        await using var db = await NewEmptyAsync();
        Assert.Contains("Microsoft SQL Server", db.ServerVersion);
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables", Ct));
        var password = TestSecrets.OwnerPrefix + Guid.NewGuid().ToString("N");
        using var logs = new CapturingLoggerProvider();

        var exit = await Migrator.RunAsync(Args(db, password), extraLogs: logs);

        Assert.Equal(0, exit);
        Assert.True(await db.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Donor','Patient','Appointment','Request','Receipt','BloodBag','Staff','StaffPhoto','RefreshToken','AuditLog','DemoState')", Ct) == 11);
        await using var context = db.NewContext();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Ct));

        var owner = await context.Staff.AsNoTracking().Include(s => s.Credential).SingleAsync(s => s.Username == Migrator.OwnerUsername, Ct);
        Assert.True(owner.IsProtected);
        Assert.False(owner.IsDemo);
        Assert.Equal(Role.Admin, owner.Role);
        Assert.True(PasswordHashing.IsValidHash(owner.Credential!.PasswordHash));
        Assert.True(PasswordHashing.Verify(owner.Credential!.PasswordHash, password));
        Assert.False(PasswordHashing.Verify(owner.Credential!.PasswordHash, password + "x"));
        Assert.NotEqual(password, owner.Credential!.PasswordHash);

        // The demo data exists, and the reset bookkeeping says it was seeded.
        Assert.Equal(SeedSizes.Donors, await context.Donors.CountAsync(Ct));
        Assert.Equal(SeedSizes.Receipts, await context.Receipts.CountAsync(Ct));
        Assert.NotNull((await context.DemoState.SingleAsync(Ct)).LastResetDate);
        Assert.Equal(1, await context.Staff.CountAsync(s => s.IsDemo, Ct));

        var text = string.Join('\n', logs.Lines);
        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Credential!.PasswordHash, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_RunTwice_ChangesNothing_AndRotatesTheOwnerPasswordWhenItChanged()
    {
        await using var db = await NewEmptyAsync();
        var first = "First-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, await Migrator.RunAsync(Args(db, first)));
        await using (var before = db.NewContext())
        {
            var ownerBefore = await before.Staff.AsNoTracking().Include(s => s.Credential).SingleAsync(s => s.Username == Migrator.OwnerUsername, Ct);
            var donorIds = await before.Donors.OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(Ct);

            Assert.Equal(0, await Migrator.RunAsync(Args(db, first)));
            await using var after = db.NewContext();
            var ownerAfter = await after.Staff.AsNoTracking().Include(s => s.Credential).SingleAsync(s => s.Username == Migrator.OwnerUsername, Ct);
            Assert.Equal(ownerBefore.Credential!.PasswordHash, ownerAfter.Credential!.PasswordHash);
            Assert.Equal(1, await after.Staff.CountAsync(s => s.Username == Migrator.OwnerUsername, Ct));
            Assert.Equal(donorIds, await after.Donors.OrderBy(d => d.Id).Select(d => d.Id).ToListAsync(Ct));
        }

        var second = "Second-" + Guid.NewGuid().ToString("N");
        Assert.Equal(0, await Migrator.RunAsync(Args(db, second)));
        await using var rotated = db.NewContext();
        var owner = await rotated.Staff.AsNoTracking().Include(s => s.Credential).SingleAsync(s => s.Username == Migrator.OwnerUsername, Ct);
        Assert.True(PasswordHashing.Verify(owner.Credential!.PasswordHash, second));
        Assert.False(PasswordHashing.Verify(owner.Credential!.PasswordHash, first));
    }

    [Fact]
    public async Task Migrate_WithoutOwnerPassword_SkipsTheOwner_WarnsAndStillExitsZero()
    {
        await using var scratch = await NewEmptyAsync();
        using var logs = new CapturingLoggerProvider();
        var exit = await Migrator.RunAsync(Args(scratch, null), extraLogs: logs);
        Assert.Equal(0, exit);
        Assert.Equal(0, await scratch.ScalarAsync<int>("SELECT COUNT(*) FROM Staff WHERE Username = 'owner'", Ct));
        Assert.Contains(logs.Lines, l => l.StartsWith("[" + LogLevel.Warning + "]", StringComparison.Ordinal) && l.Contains("Owner:AdminPassword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrate_WithAnUnreachableDatabase_ExitsNonZero()
    {
        await using var db = await NewEmptyAsync();
        var exit = await Migrator.RunAsync(Args(db, "x-owner-password-1", "Server=127.0.0.1,1;Database=Nothing;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=2"));
        Assert.NotEqual(0, exit);
    }

    [Fact]
    public async Task Migrate_WithNoConnectionString_ExitsNonZero() => Assert.Equal(1, await Migrator.RunAsync([]));
}

/// <summary>API-45, and the source-level rules (API-36, 20, 39, 30): shared lists, typed queries only, no timers, no credential reads.</summary>
public sealed class SharedListsAndSourceTests(ApiFixture fx) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lifestream.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("solution root not found"), "src", "Lifestream.Api");
    }

    private static IEnumerable<(string File, string Text)> ApiSource() =>
        Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public async Task LookupsEnums_AreTheSharedContractLists_AndEqualTheDatabaseConstraintValues()
    {
        var staff = await fx.AddStaffAsync(Role.Staff);
        var lookups = await (await staff.Client.GetApi(ApiRoutes.LookupEnums)).Read<EnumLookups>();
        Assert.Equivalent(EnumLookups.Create(), lookups);

        // Each list equals the CHECK constraint that guards the column in the real database.
        var checks = new (string Name, IReadOnlyList<EnumOption> Values)[]
        {
            ("CK_Donor_BloodType", lookups.BloodTypes), ("CK_Donor_Gender", lookups.Genders), ("CK_Donor_Frequency", lookups.DonorFrequencies),
            ("CK_Staff_Designation", lookups.Designations), ("CK_Staff_Shift", lookups.Shifts), ("CK_Staff_Role", lookups.Roles),
            ("CK_Appointment_Status", lookups.AppointmentStatuses), ("CK_Request_Status", lookups.RequestStatuses),
            ("CK_Receipt_Kind", lookups.ReceiptKinds), ("CK_BloodBag_Status", lookups.BagStatuses),
        };
        foreach (var (name, values) in checks)
        {
            var definition = await fx.Db.ScalarAsync<string>($"SELECT definition FROM sys.check_constraints WHERE name = '{name}'", Ct);
            var inDatabase = Regex.Matches(definition, "'([A-Za-z]+)'").Select(m => m.Groups[1].Value).Order().ToList();
            Assert.Equal(values.Select(v => v.Value).Order(), inDatabase);
        }

        Assert.Equal(["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"], lookups.BloodTypes.Select(b => b.Display));
        Assert.Equal(Enum.GetNames<Gender>(), lookups.Genders.Select(g => g.Value));
        Assert.Contains(lookups.Genders, g => g.Value == "Other");
    }

    [Fact]
    public void Source_UsesOnlyParameterisedOrTypedQueries_NoSelectStar_NoSqlStringBuilding()
    {
        var raw = new Regex(@"FromSqlRaw|SqlQueryRaw|ExecuteSqlRaw|FromSqlInterpolated|ExecuteSqlInterpolated|new SqlCommand|CommandText\s*=");
        var offenders = new List<string>();
        foreach (var (file, text) in ApiSource())
        {
            var name = Path.GetFileName(file);
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"SELECT\s+\*", RegexOptions.IgnoreCase) && !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && !line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    offenders.Add($"{name}:{i + 1} SELECT *");
                }

                if (raw.IsMatch(line) && (line.Contains("$\"", StringComparison.Ordinal) || line.Contains("\" +", StringComparison.Ordinal) || line.Contains("+ \"", StringComparison.Ordinal) || line.Contains("string.Format", StringComparison.Ordinal)))
                {
                    offenders.Add($"{name}:{i + 1} raw SQL built from text: {line.Trim()}");
                }
            }

            // Raw SQL lives in a few files, which are parameterised with SqlParameter (LoginAttempts.cs: the login counters, whose statements must not vary with the name).
            if (raw.IsMatch(text) && name is not ("BloodBagSql.cs" or "Lists.cs" or "ApiSetup.cs" or "LoginAttempts.cs"))
            {
                offenders.Add($"{name}: raw SQL outside the allowed files");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        var bagSql = File.ReadAllText(Path.Combine(SourceRoot(), "Features", "BloodBagSql.cs"));
        Assert.Contains("new SqlParameter(", bagSql, StringComparison.Ordinal);
        Assert.DoesNotContain("$\"", bagSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_HasNoTimerBackgroundJobOrKeepAlive_ThatCouldWakeTheDatabase()
    {
        var forbidden = new Regex(@"IHostedService|BackgroundService|AddHostedService|PeriodicTimer|System\.Threading\.Timer|new Timer\(|System\.Timers|Hangfire|Quartz|Task\.Delay\(\s*TimeSpan\.From(Minutes|Hours)");
        var offenders = ApiSource().Where(s => forbidden.IsMatch(s.Text)).Select(s => Path.GetFileName(s.File)).ToList();
        Assert.True(offenders.Count == 0, "timer-like code in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Source_ReadsThePasswordHashColumnOnlyWhereItMustVerifyOrWriteIt()
    {
        var allowed = new[] { "AuthService.cs", "StaffEndpoints.cs", "ProfileLookupDemoEndpoints.cs", "Migrator.cs" };
        var offenders = ApiSource().Where(s => s.Text.Contains("PasswordHash", StringComparison.Ordinal) && !allowed.Contains(Path.GetFileName(s.File))).Select(s => Path.GetFileName(s.File)).ToList();
        Assert.True(offenders.Count == 0, "PasswordHash referenced in: " + string.Join(", ", offenders));

        // No contract record exposes a password-like member except the request-side ones.
        foreach (var type in typeof(ApiRoutes).Assembly.GetTypes().Where(t => t.Namespace?.StartsWith("Lifestream.Contracts", StringComparison.Ordinal) == true && t.Name.EndsWith("Response", StringComparison.Ordinal) || t.Name.EndsWith("Item", StringComparison.Ordinal) || t.Name.EndsWith("Detail", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Hash", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Temporary", StringComparison.OrdinalIgnoreCase));
        }

    }
}
