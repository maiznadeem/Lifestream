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
public partial class SqlServerDatabaseFixture : IAsyncLifetime
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
        await SweepLeftoverDatabasesOnceAsync(ct);
        await CreateAsync(ct);
        if (ApplyMigrations)
        {
            await using var db = NewContext();
            await db.Database.MigrateAsync(ct);
        }

        ServerVersion = await ScalarAsync<string>("SELECT @@VERSION", ct);
        await OnMigratedAsync(ct);
    }

    /// <summary>Drops a database by name through the same checked, parameterised statement the fixture uses for its own (the test of the name check calls this).</summary>
    internal Task DropDatabaseNamedAsync(string name, CancellationToken ct) => ExecuteOnMasterAsync(DropSql, name, ct);

    /// <summary>The names this fixture generates: <c>Lifestream_Test_</c> and 32 lower-case hex digits. Nothing else is ever created or dropped by it.</summary>
    internal static bool IsFixtureDatabaseName(string name) => FixtureName().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex(@"^Lifestream_Test_[0-9a-f]{32}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex FixtureName();

    // The name travels as a parameter and is quoted by the server (QUOTENAME), never spliced into the statement text on the client.
    private const string CreateSql = "DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name); EXEC sp_executesql @sql;";

    private const string DropSql =
        "DECLARE @n sysname = @name; IF DB_ID(@n) IS NOT NULL BEGIN DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@n) + N' SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ' + QUOTENAME(@n); EXEC sp_executesql @sql; END";

    private static readonly SemaphoreSlim SweepGate = new(1, 1);
    private static bool _swept;

    /// <summary>
    /// A test run that is killed never reaches <see cref="DisposeAsync"/>, so its databases stay on the server. Once per process, before the first
    /// database is created, drop every <c>Lifestream_Test_*</c> database that is more than two hours old and has no session (a database of a run
    /// that is still going is younger, or has sessions, and is left alone). Cleaning up is best effort: a server that will not list or drop one
    /// must not fail the tests, so a SQL error here is ignored.
    /// </summary>
    private async Task SweepLeftoverDatabasesOnceAsync(CancellationToken ct)
    {
        await SweepGate.WaitAsync(ct);
        try
        {
            if (_swept)
            {
                return;
            }

            _swept = true;
            try
            {
                var stale = new List<string>();
                await using (var connection = new SqlConnection(MasterConnection))
                {
                    await connection.OpenAsync(ct);
                    await using var list = new SqlCommand(
                        "SELECT d.name FROM sys.databases d WHERE d.name LIKE N'Lifestream[_]Test[_]%' AND d.create_date < DATEADD(HOUR, -2, GETDATE()) " +
                        "AND NOT EXISTS (SELECT 1 FROM sys.dm_exec_sessions s WHERE s.database_id = d.database_id)",
                        connection);
                    await using var reader = await list.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        stale.Add(reader.GetString(0));
                    }
                }

                foreach (var name in stale.Where(IsFixtureDatabaseName))
                {
                    await ExecuteOnMasterAsync(DropSql, name, ct);
                }
            }
            catch (SqlException)
            {
                // best effort, see above
            }
        }
        finally
        {
            SweepGate.Release();
        }
    }

    /// <summary>
    /// CREATE DATABASE copies the <c>model</c> database and needs an exclusive lock on it for a moment, so two creations at the same time (the many
    /// fixtures of one parallel run, or another test run on the same server) can make one fail with error 1807, "Could not obtain exclusive lock on
    /// database 'model'. Retry the operation later." That is the server asking for a retry; any other error is thrown at once.
    /// </summary>
    private async Task CreateDatabaseAsync(CancellationToken ct)
    {
        const int modelLocked = 1807;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteOnMasterAsync(CreateSql, DatabaseName, ct);
                return;
            }
            catch (SqlException ex) when (ex.Number == modelLocked && attempt < 30)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }

    /// <summary>Creates the (empty) database; a fixture whose database is a copy of a prepared one restores it here instead.</summary>
    protected virtual Task CreateAsync(CancellationToken ct) => CreateDatabaseAsync(ct);

    /// <summary>Runs a statement on <c>master</c> with the fixture's database name as <c>@name</c> (checked to be a fixture name first) and the given extra parameters.</summary>
    private protected async Task ExecuteOnMasterAsync(string sql, string databaseName, CancellationToken ct, params SqlParameter[] parameters)
    {
        if (!IsFixtureDatabaseName(databaseName))
        {
            throw new InvalidOperationException("Refusing to create or drop a database that is not a fixture database: " + databaseName);
        }

        await using var connection = new SqlConnection(MasterConnection);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = databaseName });
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>False gives an empty database, for tests that apply the schema some other way (the idempotent script).</summary>
    protected virtual bool ApplyMigrations => true;

    protected virtual Task OnMigratedAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        SqlConnection.ClearAllPools();
        var ct = CancellationToken.None;
        await ExecuteOnMasterAsync(DropSql, DatabaseName, ct);
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

    private Task ExecuteOnMasterAsync(string sql, string databaseName, CancellationToken ct) => ExecuteOnMasterAsync(sql, databaseName, ct, []);
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

/// <summary>
/// The same database with the 100x dataset loaded, once per class. Loading 100,000 rows and settling the indexes takes about 20 s, so it is done once
/// into a template database that is backed up and dropped at once; each class then gets its own database restored from that backup (seconds):
/// the same rows, rebuilt indexes and full-scan statistics as a database loaded for the class alone. The backup is kept between runs and used again
/// while the sources that decide its content, the day and the server are the same (<see cref="ScaleTemplateCache"/>). Used by the plan and I/O tests
/// and available to performance tests.
/// </summary>
public sealed class ScaleDatabaseFixture : SqlServerDatabaseFixture
{
    public const int Factor = 100;

    /// <summary>The day the dataset was generated for (the template's).</summary>
    public DateOnly Today { get; private set; }

    public int Receipts { get; private set; }

    protected override bool ApplyMigrations => false; // the restored copy already has the schema

    /// <summary>
    /// Held while a copy is restored, and by a test that reads plans back from the server's plan cache for its whole run: a RESTORE empties the plan
    /// cache of the whole instance, so a restore for another class must not happen between that test's request and its reading of the plan.
    /// </summary>
    public static SemaphoreSlim PlanCacheGate { get; } = new(1, 1);

    protected override async Task CreateAsync(CancellationToken ct)
    {
        const int cannotOpenBackupDevice = 3201;
        for (var attempt = 1; ; attempt++)
        {
            var template = await ScaleTemplate.GetAsync(ct);
            Today = template.Today;
            Receipts = template.Receipts;
            await PlanCacheGate.WaitAsync(ct);
            try
            {
                await RestoreAsync(template, ct);
                return;
            }
            catch (SqlException ex) when (attempt < 3 && ex.Errors.Cast<SqlError>().Any(e => e.Number == cannotOpenBackupDevice))
            {
                // The kept backup is gone: a run with other sources replaced it between this run's look at it and this restore. Build it again.
            }
            finally
            {
                PlanCacheGate.Release();
            }

            await ScaleTemplate.ForgetAsync(template, ct);
        }
    }

    private Task RestoreAsync(ScaleTemplate.Built template, CancellationToken ct) =>
        ExecuteOnMasterAsync(
            """
            DECLARE @data nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)) + @name + N'.mdf';
            DECLARE @log nvarchar(4000) = CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000)) + @name + N'_log.ldf';
            RESTORE DATABASE @name FROM DISK = @file WITH MOVE @dataFile TO @data, MOVE @logFile TO @log, RECOVERY;
            """,
            DatabaseName,
            ct,
            new SqlParameter("@file", SqlDbType.NVarChar, 4000) { Value = template.BackupFile },
            new SqlParameter("@dataFile", SqlDbType.NVarChar, 128) { Value = template.DataFile },
            new SqlParameter("@logFile", SqlDbType.NVarChar, 128) { Value = template.LogFile });

    /// <summary>The 100x template of this test run: built on first use, backed up into the server's backup folder, then dropped.</summary>
    private static class ScaleTemplate
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static Built? _built;

        public sealed record Built(string BackupFile, string DataFile, string LogFile, DateOnly Today, int Receipts);

        public static async Task<Built> GetAsync(CancellationToken ct)
        {
            await Gate.WaitAsync(ct);
            try
            {
                return _built ??= await BuildAsync(ct);
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>Makes the next <see cref="GetAsync"/> look for the template again, when the one it gave out can no longer be restored from.</summary>
        public static async Task ForgetAsync(Built gone, CancellationToken ct)
        {
            await Gate.WaitAsync(ct);
            try
            {
                if (ReferenceEquals(_built, gone))
                {
                    _built = null;
                }
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// The template of this run: the backup an earlier run kept, when it was built from the same sources for the same day (see
        /// <see cref="ScaleTemplateCache"/>); otherwise built now, and kept for the next run.
        /// </summary>
        private static async Task<Built> BuildAsync(CancellationToken ct)
        {
            var source = new Loader();
            var (cache, reason) = await ScaleTemplateCache.OpenAsync(source.MasterConnection, Factor, source.Today, ct);
            if (cache is null)
            {
                return await LoadAndBackUpAsync(source, null, "not kept: " + reason, ct);
            }

            await using var oneAtATime = await cache.LockAsync(ct);
            if (await cache.TryReadAsync(ct) is { } kept)
            {
                ScaleTemplateCache.Report($"100x template: restored from the kept backup {cache.BackupFile}");
                return new Built(cache.BackupFile, kept.DataFile, kept.LogFile, kept.Today, kept.Receipts);
            }

            var built = await LoadAndBackUpAsync(source, cache, "kept as " + cache.BackupFile, ct);
            await cache.DeleteOthersAsync(ct);
            return built;
        }

        private static async Task<Built> LoadAndBackUpAsync(Loader source, ScaleTemplateCache? cache, string note, CancellationToken ct)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await source.InitializeAsync();
            try
            {
                var files = await source.RowsAsync("SELECT type, name FROM sys.database_files", ct);
                var dataFile = (string)files.Single(f => Convert.ToInt32(f[0], System.Globalization.CultureInfo.InvariantCulture) == 0)[1]!;
                var logFile = (string)files.Single(f => Convert.ToInt32(f[0], System.Globalization.CultureInfo.InvariantCulture) == 1)[1]!;
                string backupFile;
                if (cache is null)
                {
                    // A backup for this run only, in the server's backup folder, deleted when the run ends.
                    await DeleteStaleBackupsAsync(source, ct);
                    var backupDirectory = await source.ScalarAsync<string>("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))", ct);
                    backupFile = Path.Combine(backupDirectory, source.DatabaseName + ".bak");
                    await source.BackupToAsync(backupFile, "Lifestream test template of one run", ct);
                    var master = source.MasterConnection;
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteBackup(master, backupFile);
                }
                else
                {
                    backupFile = cache.BackupFile;
                    await source.BackupToAsync(backupFile, cache.Describe(source.Today, source.Receipts), ct);
                }

                ScaleTemplateCache.Report(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"100x template: built in {watch.Elapsed.TotalSeconds:0.0} s, {note}"));
                return new Built(backupFile, dataFile, logFile, source.Today, source.Receipts);
            }
            finally
            {
                await source.DisposeAsync(); // the template database itself is dropped (and the drop asserted) as soon as its backup exists
            }
        }

        /// <summary>Backups of earlier runs that were killed before they could delete theirs (more than two hours old). Best effort, like the database sweep.</summary>
        private static async Task DeleteStaleBackupsAsync(SqlServerDatabaseFixture any, CancellationToken ct)
        {
            try
            {
                var stale = await any.RowsAsync(
                    "SELECT full_filesystem_path FROM sys.dm_os_enumerate_filesystem(CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)), N'Lifestream_Test_*.bak') " +
                    "WHERE last_write_time < DATEADD(HOUR, -2, SYSDATETIMEOFFSET())",
                    ct);
                foreach (var row in stale)
                {
                    if (row[0] is string path && IsFixtureDatabaseName(Path.GetFileNameWithoutExtension(path)))
                    {
                        DeleteBackup(any.MasterConnection, path);
                    }
                }
            }
            catch (SqlException)
            {
                // best effort
            }
        }

        private static void DeleteBackup(string masterConnection, string backupFile)
        {
            try
            {
                using var connection = new SqlConnection(masterConnection);
                connection.Open();
                using var command = new SqlCommand("EXEC master.dbo.xp_delete_file 0, @file", connection);
                command.Parameters.Add(new SqlParameter("@file", SqlDbType.NVarChar, 4000) { Value = backupFile });
                command.ExecuteNonQuery();
            }
            catch (SqlException)
            {
                // best effort: a later run's sweep deletes it
            }
        }

        /// <summary>Loads the dataset the way each class used to load its own: migrations, the 100x rows, then the indexes rebuilt and the statistics read in full.</summary>
        private sealed class Loader : SqlServerDatabaseFixture
        {
            public DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.UtcNow);

            public int Receipts { get; private set; }

            public Task BackupToAsync(string file, string description, CancellationToken ct) =>
                ExecuteOnMasterAsync(
                    "BACKUP DATABASE @name TO DISK = @file WITH COPY_ONLY, INIT, FORMAT, DESCRIPTION = @description",
                    DatabaseName,
                    ct,
                    new SqlParameter("@file", SqlDbType.NVarChar, 4000) { Value = file },
                    new SqlParameter("@description", SqlDbType.NVarChar, 255) { Value = description });

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
    }
}
