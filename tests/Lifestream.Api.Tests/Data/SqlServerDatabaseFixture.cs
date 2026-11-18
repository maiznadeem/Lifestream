using System.Data;
using Lifestream.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Lifestream.Api.Tests.Data;

/// <summary>
/// A real SQL Server database for one test class: created as <c>Lifestream_Test_&lt;guid&gt;</c>, schema applied by the
/// EF migrations (never <c>EnsureCreated</c>), dropped on dispose, and the drop is asserted. The server comes from
/// <c>LIFESTREAM_TEST_SQL</c>; the default is the local default instance with Windows authentication.
/// </summary>
public class SqlServerDatabaseFixture : IAsyncLifetime
{
    public const string DefaultServerConnection = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    private readonly string _serverConnection = Environment.GetEnvironmentVariable("LIFESTREAM_TEST_SQL") is { Length: > 0 } fromEnv
        ? fromEnv
        : DefaultServerConnection;

    public string DatabaseName { get; } = $"Lifestream_Test_{Guid.NewGuid():N}";

    public string ConnectionString => new SqlConnectionStringBuilder(_serverConnection) { InitialCatalog = DatabaseName }.ConnectionString;

    /// <summary>The server's own description of itself, proof the tests ran against a real SQL Server and not an in-memory fallback.</summary>
    public string ServerVersion { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await ExecuteOnMasterAsync($"CREATE DATABASE [{DatabaseName}]", ct);
        if (ApplyMigrations)
        {
            await using var db = NewContext();
            await db.Database.MigrateAsync(ct);
        }

        ServerVersion = await ScalarAsync<string>("SELECT @@VERSION", ct);
        await OnMigratedAsync(ct);
    }

    /// <summary>False gives an empty database, for tests that apply the schema some other way (the idempotent script).</summary>
    protected virtual bool ApplyMigrations => true;

    protected virtual Task OnMigratedAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        SqlConnection.ClearAllPools();
        var ct = CancellationToken.None;
        await ExecuteOnMasterAsync(
            $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END",
            ct);
        await using var connection = new SqlConnection(MasterConnection);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("SELECT DB_ID(@name)", connection);
        command.Parameters.AddWithValue("@name", DatabaseName);
        var id = await command.ExecuteScalarAsync(ct);
        if (id is not (null or DBNull))
        {
            throw new InvalidOperationException($"Test database {DatabaseName} was not dropped.");
        }
    }

    public string MasterConnection => new SqlConnectionStringBuilder(_serverConnection) { InitialCatalog = "master" }.ConnectionString;

    public LifestreamDbContext NewContext(int? staffId = null, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<LifestreamDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(interceptors)
            .Options;
        return new LifestreamDbContext(options) { CurrentStaffId = staffId };
    }

    public async Task<T> ScalarAsync<T>(string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(ct);
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<object?[]>> RowsAsync(string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        return rows;
    }

    public async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Removes every row, children first, so a test that counts rows starts from nothing.</summary>
    public Task ClearAllAsync(CancellationToken ct) => ExecuteAsync(
        """
        DELETE FROM BloodBag; DELETE FROM Receipt; DELETE FROM Request; DELETE FROM Appointment; DELETE FROM Patient; DELETE FROM Donor;
        DELETE FROM RefreshToken; DELETE FROM StaffPhoto; DELETE FROM AuditLog; DELETE FROM Staff; DELETE FROM DemoState;
        """,
        ct);

    private async Task ExecuteOnMasterAsync(string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(MasterConnection);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection);
        command.CommandType = CommandType.Text;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>A database with no tables at all.</summary>
public sealed class EmptyDatabaseFixture : SqlServerDatabaseFixture
{
    protected override bool ApplyMigrations => false;
}

/// <summary>The same database with the 1x demo dataset loaded (what the public demo holds after each reset), once per class.</summary>
public sealed class DemoSizeDatabaseFixture : SqlServerDatabaseFixture
{
    protected override async Task OnMigratedAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        db.AuditEnabled = false;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        Lifestream.Data.Seed.ScaleDataGenerator.Generate(1, DateOnly.FromDateTime(DateTime.UtcNow)).AddTo(db);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The same database with the 100x dataset loaded, once per class. Used by the plan and I/O tests and available to performance tests.</summary>
public sealed class ScaleDatabaseFixture : SqlServerDatabaseFixture
{
    public const int Factor = 100;

    public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

    public int Receipts { get; private set; }

    protected override async Task OnMigratedAsync(CancellationToken ct)
    {
        await using var db = NewContext();
        db.AuditEnabled = false;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        var set = Lifestream.Data.Seed.ScaleDataGenerator.Generate(Factor, Today);
        Receipts = set.Receipts.Count;
        set.AddTo(db);
        await db.SaveChangesAsync(ct);
        await SettleAsync(ct);
    }

    /// <summary>
    /// What a database that has just been bulk loaded needs before its plans mean anything: indexes rebuilt (rows arrive in clustered-key
    /// order but in random order for every other index, so the nonclustered ones are half empty and look bigger than the table itself)
    /// and statistics read in full (the automatic ones are sampled, and a sample differs from run to run). The same maintenance a
    /// production database gets.
    /// </summary>
    private async Task SettleAsync(CancellationToken ct)
    {
        foreach (var table in new[] { "Donor", "Patient", "Staff", "Appointment", "Request", "Receipt", "BloodBag" })
        {
            await ExecuteAsync($"ALTER INDEX ALL ON [{table}] REBUILD; UPDATE STATISTICS [{table}] WITH FULLSCAN;", ct);
        }
    }
}
