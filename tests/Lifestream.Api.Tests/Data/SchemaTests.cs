using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Lifestream.Api.Tests.Data;

/// <summary>What the migrations build, read back from the server's own catalog (DB-01, 04, 05, 08, 09, 10, 11, 17).</summary>
public sealed partial class SchemaTests(SqlServerDatabaseFixture fixture) : IClassFixture<SqlServerDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] ExpectedTables =
        ["AuditLog", "Appointment", "BloodBag", "DemoState", "Donor", "Patient", "Receipt", "RefreshToken", "Request", "Staff", "StaffPhoto"];

    // ---- DB-01 ----

    [Fact]
    public async Task Fixture_RunsAgainstRealSqlServer_NotInMemory()
    {
        await using var db = fixture.NewContext();
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", db.Database.ProviderName);
        Assert.Contains("Microsoft SQL Server", fixture.ServerVersion, StringComparison.Ordinal);
        Assert.Equal(fixture.DatabaseName, await fixture.ScalarAsync<string>("SELECT DB_NAME()", Ct));
        Assert.StartsWith("Lifestream_Test_", fixture.DatabaseName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrations_ApplyToEmptyDatabase_NoPendingChanges()
    {
        await using var db = fixture.NewContext();
        var all = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync(Ct)).ToList();

        Assert.NotEmpty(all);
        Assert.Equal(all, applied);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));
        // Zero drift: the migrations produce exactly the current model.
        Assert.False(db.Database.HasPendingModelChanges(), "The model has changes that no migration contains; add a migration.");
        Assert.Contains(all, m => m.EndsWith("_InitialSchema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Schema_IsCreatedOnlyByMigrations_OneHistoryRowPerMigration_NoOtherTables()
    {
        var tables = (await fixture.RowsAsync("SELECT name FROM sys.tables", Ct)).Select(r => (string)r[0]!).Order().ToList();
        Assert.Equal(ExpectedTables.Append("__EFMigrationsHistory").Order(), tables);
        var history = await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM __EFMigrationsHistory", Ct);
        await using var db = fixture.NewContext();
        Assert.Equal(db.Database.GetMigrations().Count(), history);
    }

    // ---- DB-17 ----

    [Fact]
    public async Task Attendance_IsAbsentFromMigrations_BloodBagIsPresent()
    {
        var tables = (await fixture.RowsAsync("SELECT name FROM sys.tables", Ct)).Select(r => (string)r[0]!).ToList();
        Assert.DoesNotContain("Attendance", tables);
        Assert.Contains("BloodBag", tables);
        await using var db = fixture.NewContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        var created = assembly.Migrations.Values
            .SelectMany(m => assembly.CreateMigration(m, db.Database.ProviderName!).UpOperations)
            .OfType<CreateTableOperation>()
            .Select(o => o.Name)
            .ToList();
        Assert.DoesNotContain("Attendance", created);
        Assert.Contains("BloodBag", created);
        Assert.Contains("Attendance", await File.ReadAllTextAsync(RepoFile("docs/database.md"), Ct), StringComparison.Ordinal);
    }

    // ---- DB-04 ----

    [Fact]
    public async Task Receipt_HasForeignKeysToStaffRequestAndAppointment()
    {
        var rows = await fixture.RowsAsync(
            """
            SELECT COL_NAME(fkc.parent_object_id, fkc.parent_column_id), OBJECT_NAME(fkc.referenced_object_id)
            FROM sys.foreign_key_columns fkc WHERE OBJECT_NAME(fkc.parent_object_id) = 'Receipt'
            """,
            Ct);
        var pairs = rows.Select(r => $"{r[0]}->{r[1]}").Order().ToList();
        Assert.Equal(["AppointmentId->Appointment", "RequestId->Request", "StaffId->Staff"], pairs);
    }

    [Fact]
    public async Task AllForeignKeyColumns_HaveIndexes()
    {
        var total = await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_key_columns", Ct);
        Assert.Equal(9, total);

        // A supporting index has the foreign key column as its first key column.
        var unsupported = await fixture.RowsAsync(
            """
            SELECT OBJECT_NAME(fkc.parent_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
            FROM sys.foreign_key_columns fkc
            WHERE NOT EXISTS (
                SELECT 1 FROM sys.index_columns ic
                WHERE ic.object_id = fkc.parent_object_id AND ic.column_id = fkc.parent_column_id AND ic.key_ordinal = 1 AND ic.is_included_column = 0)
            """,
            Ct);
        Assert.Empty(unsupported.Select(r => $"{r[0]}.{r[1]}"));
    }

    // ---- DB-05 ----

    [Theory]
    [InlineData("Request", "Status")]
    [InlineData("Request", "PlacedAtUtc")]
    [InlineData("Request", "BloodType")]
    [InlineData("Request", "PatientId")]
    [InlineData("Request", "RequiredBy")]
    [InlineData("BloodBag", "BloodType")]
    [InlineData("BloodBag", "CollectedAtUtc")]
    [InlineData("BloodBag", "ExpiresOn")]
    [InlineData("Receipt", "Kind")]
    [InlineData("Receipt", "CreatedAtUtc")]
    [InlineData("Receipt", "BloodType")]
    [InlineData("Receipt", "StaffId")]
    [InlineData("Staff", "Username")]
    [InlineData("Staff", "DisplayName")]
    [InlineData("Appointment", "Status")]
    [InlineData("Appointment", "SlotStartUtc")]
    [InlineData("Appointment", "PlacedAtUtc")]
    [InlineData("Appointment", "DonorId")]
    [InlineData("Donor", "Name")]
    [InlineData("Donor", "BloodType")]
    [InlineData("Donor", "Gender")]
    [InlineData("Patient", "Name")]
    [InlineData("Patient", "BloodType")]
    [InlineData("Patient", "Gender")]
    [InlineData("AuditLog", "Entity")]
    [InlineData("AuditLog", "OccurredAtUtc")]
    [InlineData("RefreshToken", "TokenHash")]
    public async Task FilteredOrSortedColumn_IsTheLeadingColumnOfAnIndex(string table, string column)
    {
        var count = await fixture.ScalarAsync<int>(
            $"""
            SELECT COUNT(*) FROM sys.index_columns ic
            WHERE ic.object_id = OBJECT_ID('{table}') AND ic.key_ordinal = 1 AND ic.is_included_column = 0
              AND COL_NAME(ic.object_id, ic.column_id) = '{column}'
            """,
            Ct);
        Assert.True(count > 0, $"{table}.{column} has no index that starts with it.");
    }

    // ---- DB-08 ----

    [Fact]
    public async Task Naming_IsOneStyle_PascalCase_NoUnderscores()
    {
        var names = await fixture.RowsAsync(
            "SELECT t.name, c.name FROM sys.tables t JOIN sys.columns c ON c.object_id = t.object_id WHERE t.name <> '__EFMigrationsHistory'",
            Ct);
        var pascal = PascalCaseWord();
        Assert.All(names, r =>
        {
            Assert.Matches(pascal, (string)r[0]!);
            Assert.Matches(pascal, (string)r[1]!);
        });
    }

    [Fact]
    public async Task Ids_AreOneType_IdentityInt_AndForeignKeysAreInt()
    {
        // Every table's key is an int: "Id" (identity), except StaffPhoto, whose key is the owning StaffId.
        var keys = await fixture.RowsAsync(
            """
            SELECT t.name, c.name, ty.name, c.is_identity
            FROM sys.tables t
            JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_primary_key = 1
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.name <> '__EFMigrationsHistory'
            """,
            Ct);
        Assert.Equal(ExpectedTables.Length, keys.Count);
        Assert.All(keys, r =>
        {
            Assert.Equal("int", (string)r[2]!);
            if ((string)r[0]! != "StaffPhoto")
            {
                Assert.Equal("Id", (string)r[1]!);
            }

            // DemoState is the single row Id = 1, so it is not generated.
            Assert.Equal((string)r[0]! != "DemoState" && (string)r[0]! != "StaffPhoto", (bool)r[3]!);
        });

        var foreignKeyTypes = await fixture.RowsAsync(
            """
            SELECT DISTINCT ty.name FROM sys.foreign_key_columns fkc
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            """,
            Ct);
        Assert.Equal(["int"], foreignKeyTypes.Select(r => (string)r[0]!));
    }

    [Fact]
    public async Task NoDeprecatedOrLegacyColumnTypes()
    {
        var rows = await fixture.RowsAsync(
            """
            SELECT t.name + '.' + c.name + ' ' + ty.name FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE ty.name IN ('image', 'text', 'ntext', 'datetime', 'smalldatetime', 'money')
            """,
            Ct);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData("Donor", "DateOfBirth")]
    [InlineData("Donor", "LastDonated")]
    [InlineData("Patient", "DateOfBirth")]
    [InlineData("Staff", "DateOfBirth")]
    [InlineData("Request", "RequiredBy")]
    [InlineData("BloodBag", "ExpiresOn")]
    [InlineData("DemoState", "LastResetDate")]
    public async Task DateOnlyColumns_AreTypedDate(string table, string column) =>
        Assert.Equal("date", await ColumnType(table, column));

    // ---- DB-09 ----

    [Fact]
    public async Task Timestamps_AreDatetime2()
    {
        var utcColumns = await fixture.RowsAsync(
            """
            SELECT t.name + '.' + c.name, ty.name FROM sys.tables t
            JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.name LIKE '%Utc'
            """,
            Ct);
        Assert.True(utcColumns.Count >= 10, "Expected the schema's timestamp columns.");
        Assert.All(utcColumns, r => Assert.Equal("datetime2", (string)r[1]!));
        Assert.Empty(await fixture.RowsAsync(
            "SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id JOIN sys.tables t ON t.object_id = c.object_id WHERE ty.name LIKE '%datetime' OR ty.name = 'datetimeoffset'",
            Ct));
    }

    [Fact]
    public async Task TimestampDefaults_AreTheServersUtcClock_NeverLocalTime()
    {
        var defaults = (await fixture.RowsAsync(
            "SELECT LOWER(d.definition) FROM sys.default_constraints d JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id WHERE c.name LIKE '%Utc'",
            Ct)).Select(r => (string)r[0]!).ToList();
        Assert.True(defaults.Count >= 5);
        Assert.All(defaults, d => Assert.Contains("sysutcdatetime", d, StringComparison.Ordinal));
        Assert.Empty(await fixture.RowsAsync(
            "SELECT 1 FROM sys.default_constraints WHERE LOWER(definition) LIKE '%getdate%' OR LOWER(definition) LIKE '%sysdatetime()%'", Ct));
    }

    [Fact]
    public async Task InsertWithoutTimestamp_IsStampedByTheServerClock()
    {
        await using var db = fixture.NewContext();
        var donor = TestRows.Donor("Clock Donor");
        var appointment = TestRows.Appointment(donor);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(Ct);

        var gapSeconds = await fixture.ScalarAsync<double>(
            $"SELECT ABS(DATEDIFF_BIG(MILLISECOND, PlacedAtUtc, SYSUTCDATETIME())) / 1000.0 FROM Appointment WHERE Id = {appointment.Id}", Ct);
        Assert.InRange(gapSeconds, 0, 60);
    }

    // ---- DB-10 ----

    [Fact]
    public async Task Database_HasNoTriggersOrUserFunctions()
    {
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT COUNT(*) FROM sys.triggers", Ct));
        Assert.Equal(
            0,
            await fixture.ScalarAsync<int>(
                "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('FN', 'IF', 'TF', 'AF', 'FS', 'FT', 'P', 'PC', 'TR', 'TA')",
                Ct));
    }

    // ---- DB-11 ----

    [Fact]
    public async Task Staff_HasNoBinaryColumn_PhotosLiveInStaffPhoto()
    {
        var binary = await fixture.RowsAsync(
            "SELECT c.name FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('Staff') AND ty.name IN ('image', 'varbinary', 'binary')",
            Ct);
        // The 8-byte RowVersion concurrency token is typed rowversion (listed as "timestamp"), not binary image data.
        Assert.Empty(binary);
        Assert.Equal("varbinary", await ColumnType("StaffPhoto", "Content"));
        // varbinary(30720) is stored as varbinary(max) by SQL Server; the CHECK is what holds the 30 KB limit.
        Assert.Contains(
            "30720",
            await fixture.ScalarAsync<string>("SELECT definition FROM sys.check_constraints WHERE name = 'CK_StaffPhoto_Size'", Ct),
            StringComparison.Ordinal);
    }

    private async Task<string> ColumnType(string table, string column) =>
        await fixture.ScalarAsync<string>(
            $"SELECT ty.name FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('{table}') AND c.name = '{column}'",
            Ct);

    internal static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lifestream.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Lifestream.slnx not found."), relative);
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]*$")]
    private static partial Regex PascalCaseWord();
}
