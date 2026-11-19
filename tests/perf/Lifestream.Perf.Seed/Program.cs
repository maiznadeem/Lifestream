using System.Globalization;
using Lifestream.Data;
using Lifestream.Data.Entities;
using Lifestream.Data.Security;
using Lifestream.Data.Seed;
using Lifestream.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

// The deterministic scale-data script (DB-06). Creates a uniquely named local SQL Server database, applies the EF migrations, loads
// ScaleDataGenerator.Generate(factor, today) (factor 100: 27,000 receipts, 30,000 appointments, 15,000 requests, 3,500 donors, 3,500
// patients, 1,100 staff; the same date and factor always give the same rows), rebuilds the indexes and reads the statistics in full
// (what a production database gets), and sets the password of the first generated administrator (printed as "admin") for the measuring session. Last line of output on success is
// one JSON object with the database name and the row counts. The database is only ever dropped by the `drop` command.
//
//   Lifestream.Perf.Seed create --factor 100 [--today 2026-11-19] [--name Lifestream_Perf_x]   (password in LIFESTREAM_PERF_ADMIN_PASSWORD)
//   Lifestream.Perf.Seed drop --name Lifestream_Perf_x
//
// The server comes from LIFESTREAM_TEST_SQL (default: local default instance, Windows authentication).
const string DefaultServer = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

try
{
    return await RunAsync(args);
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

static string? Option(string[] args, string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

static async Task<int> RunAsync(string[] args)
{
    var server = Environment.GetEnvironmentVariable("LIFESTREAM_TEST_SQL") is { Length: > 0 } fromEnv ? fromEnv : DefaultServer;
    if (System.Text.RegularExpressions.Regex.IsMatch(server, @"(?i)database\.windows\.net|(?:password|pwd)\s*=|user\s+id\s*=")
        || !System.Text.RegularExpressions.Regex.IsMatch(server, @"(?i)(server|data source)\s*=\s*(localhost|\(local\)|\.|127\.0\.0\.1)(\\|,|;|$)"))
    {
        Console.Error.WriteLine("ERROR: the perf fixture only runs against a local SQL Server with Windows authentication.");
        return 2;
    }

    var command = args.Length > 0 ? args[0] : "";
    var name = Option(args, "--name") ?? $"Lifestream_Perf_{Guid.NewGuid():N}";
    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^Lifestream_Perf_[A-Za-z0-9]+$"))
    {
        Console.Error.WriteLine("ERROR: the database name must look like Lifestream_Perf_<letters and digits>.");
        return 2;
    }

    var master = new SqlConnectionStringBuilder(server) { InitialCatalog = "master" }.ConnectionString;
    var target = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;

    switch (command)
    {
        case "list":
            // Every fixture database that exists (there must be none once a perf run is over).
            await using (var c = new SqlConnection(master))
            {
                await c.OpenAsync();
                await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE name LIKE N'Lifestream[_]Perf[_]%' ORDER BY name", c);
                await using var reader = await cmd.ExecuteReaderAsync();
                var any = false;
                while (await reader.ReadAsync())
                {
                    Console.WriteLine(reader.GetString(0));
                    any = true;
                }

                if (!any)
                {
                    Console.WriteLine("(none)");
                }
            }

            return 0;

        case "drop":
            SqlConnection.ClearAllPools();
            await ExecuteAsync(master, $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
            var left = await ScalarAsync(master, $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{name}'");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"dropped {name}; remaining with that name: {left}"));
            return left == 0 ? 0 : 1;

        case "create":
            var factor = int.Parse(Option(args, "--factor") ?? "100", CultureInfo.InvariantCulture);
            var today = Option(args, "--today") is { } d ? DateOnly.ParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture) : DateOnly.FromDateTime(DateTime.UtcNow);
            var admin = "";
            var password = Environment.GetEnvironmentVariable("LIFESTREAM_PERF_ADMIN_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("ERROR: set LIFESTREAM_PERF_ADMIN_PASSWORD (the session administrator's password).");
                return 2;
            }

            await ExecuteAsync(master, $"CREATE DATABASE [{name}]");
            try
            {
                var options = new DbContextOptionsBuilder<LifestreamDbContext>().UseSqlServer(target, sql => sql.CommandTimeout(300)).Options;
                await using (var db = new LifestreamDbContext(options))
                {
                    await db.Database.MigrateAsync();
                    db.AuditEnabled = false;
                    db.ChangeTracker.AutoDetectChangesEnabled = false;
                    var set = ScaleDataGenerator.Generate(factor, today);
                    // The measuring session signs in as the first generated administrator (its password is the only thing changed), so the
                    // staff count stays exactly the generator's (110 at factor 10, 1,100 at factor 100).
                    var sessionAdmin = set.Staff.First(s => s.Role == Role.Admin && !s.IsDemo);
                    sessionAdmin.Credential = new StaffCredential { PasswordHash = PasswordHashing.Hash(password) };
                    admin = sessionAdmin.Username;
                    set.AddTo(db);
                    // Today's DemoState row: without it the API's once-a-day demo reset runs on the first signed-in request and replaces this dataset with the 1x demo set.
                    db.DemoState.Add(new DemoState { LastResetDate = DateOnly.FromDateTime(DateTime.UtcNow) });
                    await db.SaveChangesAsync();
                }

                foreach (var table in new[] { "Donor", "Patient", "Staff", "Appointment", "Request", "Receipt", "BloodBag" })
                {
                    await ExecuteAsync(target, $"ALTER INDEX ALL ON [{table}] REBUILD; UPDATE STATISTICS [{table}] WITH FULLSCAN;");
                }

                var counts = new List<string>();
                foreach (var table in new[] { "Staff", "Donor", "Patient", "Appointment", "Request", "Receipt", "BloodBag" })
                {
                    var rows = await ScalarAsync(target, $"SELECT COUNT(*) FROM [{table}]");
                    counts.Add(string.Create(CultureInfo.InvariantCulture, $"\"{table}\":{rows}"));
                }

                var version = (await ScalarObjectAsync(target, "SELECT @@VERSION"))?.ToString()?.Split('\n')[0].Trim().Replace("\"", "'", StringComparison.Ordinal);
                Console.WriteLine($"{{\"database\":\"{name}\",\"factor\":{factor},\"admin\":\"{admin}\",\"today\":\"{today:yyyy-MM-dd}\",\"server\":\"{version}\",\"rows\":{{{string.Join(',', counts)}}}}}");
                return 0;
            }
            catch
            {
                SqlConnection.ClearAllPools();
                await ExecuteAsync(master, $"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
                throw;
            }

        default:
            Console.Error.WriteLine("usage: create --factor N [--today yyyy-MM-dd] [--name Lifestream_Perf_x] | drop --name Lifestream_Perf_x");
            return 2;
    }
}

static async Task ExecuteAsync(string connection, string sql)
{
    await using var c = new SqlConnection(connection);
    await c.OpenAsync();
    await using var command = new SqlCommand(sql, c) { CommandTimeout = 600 };
    await command.ExecuteNonQueryAsync();
}

static async Task<object?> ScalarObjectAsync(string connection, string sql)
{
    await using var c = new SqlConnection(connection);
    await c.OpenAsync();
    await using var command = new SqlCommand(sql, c) { CommandTimeout = 120 };
    return await command.ExecuteScalarAsync();
}

static async Task<int> ScalarAsync(string connection, string sql) =>
    Convert.ToInt32(await ScalarObjectAsync(connection, sql), CultureInfo.InvariantCulture);
